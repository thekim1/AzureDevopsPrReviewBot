using System.Globalization;
using System.Text;
using PrReviewBot.Config;

namespace PrReviewBot.Services;

// Builds the line-numbered hunk diffs that are sent to the reviewer.
//
// Only changed regions plus a window of surrounding context are emitted, with
// the skipped stretches marked explicitly. Compared to sending whole files
// this cuts input tokens sharply on small changes to large files, which is
// what pays for the repository context in the prompt.
//
// Format:  <sign><lineNumber> | <content>
//   sign = '+' added (lineNumber is the NEW file line)
//   sign = '-' removed (lineNumber is the OLD file line)
//   sign = ' ' unchanged (lineNumber is the NEW file line)
internal static class DiffBuilder
{
    public const string TruncationMarker = "@@ DIFF TRUNCATED — the rest of this file's changes were not sent. @@";

    // Outcome of diffing a single file. A file that cannot be diffed safely is
    // reported as a failure rather than returned as placeholder text, which
    // the model would otherwise try to review.
    //
    // IsWholeFile: every line of the file is in Text, so the model may rely on
    // what is absent from it.
    public readonly record struct DiffResult(
        bool Success,
        string Text,
        bool IsTruncated,
        int NewFileLineCount,
        string FailureReason,
        bool IsWholeFile = false)
    {
        public static DiffResult Failed(string reason) => new(false, "", false, 0, reason);
    }

    public static DiffResult Build(string oldContent, string newContent, ReviewSettings settings, string path = "")
    {
        string[] oldLines = SplitLines(oldContent);
        string[] newLines = SplitLines(newContent);

        // The LCS table is O(old * new) in memory. Refuse rather than diff a
        // truncated prefix: a prefix diff reports the entire tail of the file
        // as deleted, which produces spectacular false positives.
        if ((long)oldLines.Length * newLines.Length > settings.MaxDiffCells)
        {
            return DiffResult.Failed(
                $"too large to diff ({oldLines.Length} → {newLines.Length} lines)");
        }

        List<(char Op, string Line, int OldNum, int NewNum)> diff = ComputeLineDiff(oldLines, newLines);

        if (!diff.Exists(d => d.Op != ' '))
        {
            return new DiffResult(true, "(no textual changes in this file)", false, newLines.Length, "");
        }

        // A small file is sent whole. Hunks save little on it, and a complete
        // file is the one case where the model may trust that something
        // missing from what it sees is really missing.
        bool wholeFile = newLines.Length <= settings.WholeFileMaxLines && diff.Count <= settings.MaxDiffLinesPerFile;

        List<(int Start, int End)> hunks;
        if (wholeFile)
        {
            hunks = [(0, diff.Count)];
        }
        else
        {
            // Widened hunks first. If they would blow the per-file line cap,
            // fall back to plain context windows rather than truncating: the
            // changes themselves matter more than the blocks around them.
            hunks = BuildHunks(diff, newLines, settings, path, expandScopes: true);
            if (hunks.Sum(h => h.End - h.Start) > settings.MaxDiffLinesPerFile)
            {
                hunks = BuildHunks(diff, newLines, settings, path, expandScopes: false);
            }
        }

        StringBuilder sb = new();
        int emitted = 0;
        bool truncated = false;
        int previousEnd = 0;

        foreach ((int start, int end) in hunks)
        {
            if (emitted >= settings.MaxDiffLinesPerFile)
            {
                truncated = true;
                break;
            }

            int gap = start - previousEnd;
            if (gap > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"@@ ... {gap} unchanged line(s) not shown ... @@");
            }

            for (int i = start; i < end; i++)
            {
                if (emitted >= settings.MaxDiffLinesPerFile)
                {
                    truncated = true;
                    break;
                }

                (char op, string line, int oldNum, int newNum) = diff[i];
                string num = op == '-'
                    ? oldNum.ToString(CultureInfo.InvariantCulture)
                    : newNum.ToString(CultureInfo.InvariantCulture);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{op}{num,5} | {line}");
                emitted++;
            }

            previousEnd = Math.Max(previousEnd, end);
        }

        if (truncated)
        {
            sb.AppendLine(TruncationMarker);
        }
        else if (previousEnd < diff.Count)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"@@ ... {diff.Count - previousEnd} unchanged line(s) not shown ... @@");
        }

        return new DiffResult(true, sb.ToString(), truncated, newLines.Length, "", wholeFile && !truncated);
    }

    // Returns [start, end) ranges over the diff list that together cover:
    //  * every changed line plus ContextLines on each side;
    //  * with expandScopes, the enclosing block of each change (see
    //    ScopeExpander), up to ScopeMaxLines;
    //  * the file header — imports, namespace, type declaration, fields and
    //    constructor — which says what every method below can rely on.
    // Ranges closer together than HunkMergeDistance are merged.
    private static List<(int Start, int End)> BuildHunks(
        List<(char Op, string Line, int OldNum, int NewNum)> diff,
        string[] newLines,
        ReviewSettings settings,
        string path,
        bool expandScopes)
    {
        int context = settings.ContextLines;
        List<(int Start, int End)> ranges = [];

        for (int i = 0; i < diff.Count; i++)
        {
            if (diff[i].Op != ' ')
            {
                ranges.Add((Math.Max(0, i - context), Math.Min(diff.Count, i + context + 1)));
            }
        }

        // Diff position of each new-file line, for mapping new-file ranges
        // (which is what the header and scopes are computed on) back.
        int[] newToDiff = new int[newLines.Length];
        for (int i = 0; i < diff.Count; i++)
        {
            if (diff[i].Op != '-')
            {
                newToDiff[diff[i].NewNum - 1] = i;
            }
        }

        void AddNewLineRange(int first, int last)
        {
            if (newLines.Length == 0)
            {
                return;
            }

            first = Math.Clamp(first, 0, newLines.Length - 1);
            last = Math.Clamp(last, first, newLines.Length - 1);
            ranges.Add((newToDiff[first], newToDiff[last] + 1));
        }

        if (expandScopes && settings.ScopeMaxLines > 0)
        {
            foreach ((int first, int last) in ChangedNewLineRegions(diff, newLines.Length))
            {
                (int wideFirst, int wideLast) = ScopeExpander.Expand(newLines, first, last, settings.ScopeMaxLines);
                AddNewLineRange(wideFirst, wideLast);
            }
        }

        if (settings.FileHeaderLines > 0 && newLines.Length != 0)
        {
            int headerStart = HeaderStart(newLines, path);
            AddNewLineRange(headerStart, headerStart + settings.FileHeaderLines - 1);
        }

        ranges.Sort();

        List<(int Start, int End)> hunks = [];
        foreach ((int start, int end) in ranges)
        {
            if (hunks.Count != 0 && start - hunks[^1].End <= settings.HunkMergeDistance)
            {
                hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, end));
            }
            else
            {
                hunks.Add((start, end));
            }
        }

        return hunks;
    }

    // Each run of consecutive changed lines, as a zero-based new-file range.
    // A run of pure deletions has no new-file lines, so it is represented by
    // the new-file lines either side of where it was.
    private static IEnumerable<(int First, int Last)> ChangedNewLineRegions(
        List<(char Op, string Line, int OldNum, int NewNum)> diff, int newLineCount)
    {
        int i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Op == ' ')
            {
                i++;
                continue;
            }

            int runStart = i;
            while (i < diff.Count && diff[i].Op != ' ')
            {
                i++;
            }

            int first = int.MaxValue, last = -1;
            for (int j = runStart; j < i; j++)
            {
                if (diff[j].Op == '+')
                {
                    first = Math.Min(first, diff[j].NewNum - 1);
                    last = Math.Max(last, diff[j].NewNum - 1);
                }
            }

            if (last < 0)
            {
                int before = runStart > 0 ? diff[runStart - 1].NewNum - 1 : 0;
                int after = i < diff.Count ? diff[i].NewNum - 1 : newLineCount - 1;
                first = Math.Max(0, before);
                last = Math.Max(first, after);
            }

            if (newLineCount != 0)
            {
                yield return (first, Math.Min(last, newLineCount - 1));
            }
        }
    }

    // Where the useful header of a file begins. In a Vue single-file component
    // the imports and props live in the script block, not at the top of the
    // template.
    private static int HeaderStart(string[] newLines, string path)
    {
        if (path.EndsWith(".vue", StringComparison.OrdinalIgnoreCase))
        {
            int script = Array.FindIndex(newLines, l => l.TrimStart().StartsWith("<script", StringComparison.OrdinalIgnoreCase));
            if (script >= 0)
            {
                return script;
            }
        }

        return 0;
    }

    private static string[] SplitLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        return content.Replace("\r\n", "\n").Split('\n');
    }

    // Content fetched as text from a binary blob comes back riddled with NUL
    // characters. Reviewing that wastes tokens and yields nonsense findings.
    public static bool LooksBinary(string content)
    {
        int limit = Math.Min(content.Length, 4000);
        for (int i = 0; i < limit; i++)
        {
            if (content[i] == '\0')
            {
                return true;
            }
        }

        return false;
    }

    private static List<(char Op, string Line, int OldNum, int NewNum)> ComputeLineDiff(string[] oldLines, string[] newLines)
    {
        int m = oldLines.Length, n = newLines.Length;
        int[,] dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                dp[i, j] = oldLines[i - 1] == newLines[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        List<(char, string, int, int)> result = new(m + n);
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && oldLines[x - 1] == newLines[y - 1])
            { result.Add((' ', oldLines[x - 1], x, y)); x--; y--; }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            { result.Add(('+', newLines[y - 1], 0, y)); y--; }
            else
            { result.Add(('-', oldLines[x - 1], x, 0)); x--; }
        }

        result.Reverse();
        return result;
    }
}

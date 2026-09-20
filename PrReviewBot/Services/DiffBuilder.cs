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
    public readonly record struct DiffResult(
        bool Success,
        string Text,
        bool IsTruncated,
        int NewFileLineCount,
        string FailureReason)
    {
        public static DiffResult Failed(string reason) => new(false, "", false, 0, reason);
    }

    public static DiffResult Build(string oldContent, string newContent, ReviewSettings settings)
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
        List<(int Start, int End)> hunks = BuildHunks(diff, settings.ContextLines, settings.HunkMergeDistance);

        if (hunks.Count == 0)
        {
            return new DiffResult(true, "(no textual changes in this file)", false, newLines.Length, "");
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

        return new DiffResult(true, sb.ToString(), truncated, newLines.Length, "");
    }

    // Returns [start, end) ranges over the diff list covering every changed
    // line plus `context` lines on each side, merging ranges that are closer
    // together than `mergeDistance`.
    private static List<(int Start, int End)> BuildHunks(
        List<(char Op, string Line, int OldNum, int NewNum)> diff, int context, int mergeDistance)
    {
        List<(int Start, int End)> hunks = [];

        for (int i = 0; i < diff.Count; i++)
        {
            if (diff[i].Op == ' ')
            {
                continue;
            }

            int start = Math.Max(0, i - context);
            int end = Math.Min(diff.Count, i + context + 1);

            if (hunks.Count != 0 && start - hunks[^1].End <= mergeDistance)
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

using System.Globalization;
using System.Text;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Checks every finding the model returned against the diff it was actually
// given, and throws away the ones that cannot be true. This runs locally after
// the response comes back, so it removes false positives at zero token cost.
//
// Four classes of bad finding are caught here:
//  1. The file does not exist in this PR (invented path).
//  2. The quoted evidence line does not appear in that file's diff
//     (the model described code that was never shown to it).
//  3. The line number does not match any line in the diff — re-anchored from
//     the evidence when possible, so comments land on the right line.
//  4. The finding repeats something already said, by the model itself or by a
//     human in an existing PR comment.
public sealed class ReviewValidator
{
    private readonly ReviewSettings _settings;

    public ReviewValidator(ReviewSettings settings)
    {
        _settings = settings;
    }

    public ReviewValidationResult Validate(PullRequestInfo pr, List<ReviewComment> comments)
    {
        Dictionary<string, FileDiffIndex> index = BuildIndex(pr);

        // Safety valve: a weaker model may ignore the "evidence" field
        // entirely. Filtering on evidence would then discard the whole review,
        // so the check is disabled when nothing in the batch quotes anything.
        bool requireEvidence = _settings.RequireEvidence
            && comments.Any(c => IsUsableEvidence(c.Evidence));

        if (_settings.RequireEvidence && !requireEvidence && comments.Count != 0)
        {
            Console.WriteLine(
                "Warning: the model returned no usable \"evidence\" quotes, so grounding checks were skipped. " +
                "Findings from this run are less reliable.");
        }

        List<ReviewComment> kept = [];
        Dictionary<string, int> dropped = [];
        int reanchored = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (ReviewComment comment in comments)
        {
            if (string.IsNullOrWhiteSpace(comment.Issue))
            {
                Count(dropped, "empty finding");
                continue;
            }

            FileDiffIndex? file = Resolve(index, comment.FilePath);
            if (file is null)
            {
                Count(dropped, "file not part of this PR");
                continue;
            }

            // Normalise the path to exactly what Azure DevOps expects, so the
            // post-back does not silently fail on a near-miss path.
            comment.FilePath = file.Path;

            comment.Confidence = comment.Confidence == 0 ? 3 : Math.Clamp(comment.Confidence, 1, 5);
            if (comment.Confidence < _settings.MinConfidenceToKeep)
            {
                Count(dropped, "confidence too low");
                continue;
            }

            int? evidenceLine = file.FindLineByEvidence(comment.Evidence, comment.LineNumber);

            if (requireEvidence && evidenceLine is null)
            {
                Count(dropped, IsUsableEvidence(comment.Evidence)
                    ? "evidence not found in the diff"
                    : "no verifiable evidence quoted");
                continue;
            }

            int? anchored = evidenceLine ?? file.SnapToNearestReviewableLine(comment.LineNumber);
            if (anchored != comment.LineNumber)
            {
                reanchored++;
            }

            comment.LineNumber = anchored;

            string key = DedupeKey(comment);
            if (!seen.Add(key))
            {
                Count(dropped, "duplicate finding");
                continue;
            }

            if (RepeatsExistingComment(pr, comment))
            {
                Count(dropped, "already raised by a human on this PR");
                continue;
            }

            kept.Add(comment);
        }

        return new ReviewValidationResult(kept, dropped, reanchored);
    }

    private static void Count(Dictionary<string, int> counts, string reason)
        => counts[reason] = counts.TryGetValue(reason, out int n) ? n + 1 : 1;

    private static string DedupeKey(ReviewComment comment)
    {
        string issue = Normalize(comment.Issue);
        if (issue.Length > 60)
        {
            issue = issue[..60];
        }

        return string.Create(CultureInfo.InvariantCulture, $"{comment.FilePath}|{comment.LineNumber}|{issue}");
    }

    // A human already saying essentially the same thing on the same line is
    // the most irritating kind of bot noise, and the model ignores the
    // "do not repeat" instruction often enough to be worth checking here.
    private static bool RepeatsExistingComment(PullRequestInfo pr, ReviewComment comment)
    {
        foreach (PrComment existing in pr.ExistingComments)
        {
            if (existing.FilePath is null
                || !PathsMatch(existing.FilePath, comment.FilePath))
            {
                continue;
            }

            if (existing.LineNumber.HasValue && comment.LineNumber.HasValue
                && Math.Abs(existing.LineNumber.Value - comment.LineNumber.Value) > 2)
            {
                continue;
            }

            if (WordOverlap(existing.Content, comment.Issue) >= 0.6)
            {
                return true;
            }
        }

        return false;
    }

    private static double WordOverlap(string a, string b)
    {
        HashSet<string> wordsA = Words(a);
        HashSet<string> wordsB = Words(b);
        if (wordsA.Count == 0 || wordsB.Count == 0)
        {
            return 0;
        }

        int shared = wordsA.Count(w => wordsB.Contains(w));
        return (double)shared / Math.Min(wordsA.Count, wordsB.Count);
    }

    private static HashSet<string> Words(string text)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string word in text.Split([' ', '\t', '\n', '\r', '.', ',', ':', ';', '(', ')', '"', '\''],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 3)
            {
                result.Add(word);
            }
        }

        return result;
    }

    // Evidence shorter than this is too generic to verify ("return;", "}"),
    // so a miss against the diff says nothing either way.
    private static bool IsUsableEvidence(string? evidence)
        => Normalize(evidence ?? "").Length >= 8;

    private static Dictionary<string, FileDiffIndex> BuildIndex(PullRequestInfo pr)
    {
        Dictionary<string, FileDiffIndex> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (ChangedFile file in pr.ChangedFiles)
        {
            index[file.Path] = FileDiffIndex.Build(file);
        }

        return index;
    }

    private static FileDiffIndex? Resolve(Dictionary<string, FileDiffIndex> index, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (index.TryGetValue(path, out FileDiffIndex? exact))
        {
            return exact;
        }

        foreach (KeyValuePair<string, FileDiffIndex> entry in index)
        {
            if (PathsMatch(entry.Key, path))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static bool PathsMatch(string a, string b)
    {
        string na = a.Replace('\\', '/').TrimStart('/');
        string nb = b.Replace('\\', '/').TrimStart('/');
        return na.Equals(nb, StringComparison.OrdinalIgnoreCase)
            || na.EndsWith('/' + nb, StringComparison.OrdinalIgnoreCase)
            || nb.EndsWith('/' + na, StringComparison.OrdinalIgnoreCase);
    }

    // Whitespace is the thing models most reliably get wrong when copying a
    // line, so it is collapsed away before comparing.
    private static string Normalize(string text)
    {
        StringBuilder sb = new(text.Length);
        bool lastWasSpace = true;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    // A parsed view of one file's hunk diff: the new-file line number of every
    // line the model was shown, plus its normalised text for evidence lookup.
    private sealed class FileDiffIndex
    {
        // AnchorLine is where a comment about the line lands: its own number
        // for an added or unchanged line, the next new-file line for a removed
        // one (which only has an old-file number).
        private readonly List<(int? NewLine, int? AnchorLine, string Text)> _lines = [];
        private readonly SortedSet<int> _reviewableLines = [];
        private int _firstAddedLine = -1;

        public string Path { get; private init; } = "";

        public static FileDiffIndex Build(ChangedFile file)
        {
            FileDiffIndex index = new() { Path = file.Path };

            foreach (string raw in file.Diff.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("@@", StringComparison.Ordinal))
                {
                    continue;
                }

                char op = line[0];
                if (op is not ('+' or '-' or ' '))
                {
                    continue;
                }

                int bar = line.IndexOf('|', StringComparison.Ordinal);
                if (bar < 0)
                {
                    continue;
                }

                if (!int.TryParse(line[1..bar].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                {
                    continue;
                }

                string content = Normalize(line[(bar + 1)..]);
                if (content.Length == 0)
                {
                    continue;
                }

                // Removed lines carry an OLD-file number, which must never be
                // used to anchor a comment in the new file.
                int? newLine = op == '-' ? null : number;
                index._lines.Add((newLine, newLine, content));

                if (newLine.HasValue)
                {
                    index._reviewableLines.Add(newLine.Value);
                }

                if (op == '+' && index._firstAddedLine < 0)
                {
                    index._firstAddedLine = number;
                }
            }

            index.AnchorRemovedLines();
            return index;
        }

        private void AnchorRemovedLines()
        {
            int? next = null;
            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                if (_lines[i].NewLine is { } line)
                {
                    next = line;
                }
                else
                {
                    _lines[i] = _lines[i] with { AnchorLine = next };
                }
            }

            int? previous = null;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].NewLine is { } line)
                {
                    previous = line;
                }
                else if (_lines[i].AnchorLine is null)
                {
                    _lines[i] = _lines[i] with { AnchorLine = previous };
                }
            }
        }

        // Finds the line the quoted evidence came from and returns its new-file
        // number. This both proves the finding is grounded and fixes the line
        // number when the model copied the wrong one.
        //
        // With whole files and widened blocks in the diff, a common line
        // (`return result;`, `}`) often appears several times. Taking the first
        // match anchored comments on an unrelated occurrence far from the
        // finding, so an exact match beats a partial one, and among equals the
        // one nearest the line the model named wins.
        public int? FindLineByEvidence(string? evidence, int? preferredLine = null)
        {
            if (evidence is null)
            {
                return null;
            }

            // Models sometimes quote a short block rather than the single line
            // they were asked for. Each quoted line is tried in turn and the
            // first one found in the diff wins.
            foreach (string candidate in evidence.Split('\n'))
            {
                string needle = Normalize(candidate);
                if (needle.Length < 8)
                {
                    continue;
                }

                int? best = null;
                int bestQuality = int.MaxValue;
                int bestDistance = int.MaxValue;

                foreach ((int? _, int? anchor, string text) in _lines)
                {
                    int quality = MatchQuality(needle, text);
                    if (quality < 0 || anchor is null)
                    {
                        continue;
                    }

                    int distance = preferredLine is { } preferred ? Math.Abs(anchor.Value - preferred) : 0;
                    if (quality < bestQuality || (quality == bestQuality && distance < bestDistance))
                    {
                        best = anchor;
                        bestQuality = quality;
                        bestDistance = distance;
                    }
                }

                if (best is not null)
                {
                    return best;
                }
            }

            return null;
        }

        // 0 exact, 1 the line contains the quote, 2 the quote contains the
        // line, -1 no match. The last only counts for a line long enough to
        // be distinctive: otherwise a lone `{` or `}` matches almost any quote.
        private static int MatchQuality(string needle, string text)
        {
            if (text == needle)
            {
                return 0;
            }

            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return 1;
            }

            if (text.Length >= Math.Max(8, needle.Length / 2) && needle.Contains(text, StringComparison.Ordinal))
            {
                return 2;
            }

            return -1;
        }

        // Moves a line number onto a line the model was actually shown. An
        // out-of-range number means the comment would otherwise attach to an
        // unrelated part of the file in the Azure DevOps UI.
        public int? SnapToNearestReviewableLine(int? requested)
        {
            if (_reviewableLines.Count == 0)
            {
                return null;
            }

            if (requested is null)
            {
                return _firstAddedLine > 0 ? _firstAddedLine : _reviewableLines.Min;
            }

            if (_reviewableLines.Contains(requested.Value))
            {
                return requested;
            }

            int best = _reviewableLines.Min;
            int bestDistance = Math.Abs(best - requested.Value);
            foreach (int candidate in _reviewableLines)
            {
                int distance = Math.Abs(candidate - requested.Value);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            return best;
        }
    }
}

// What survived validation, and why the rest did not.
public sealed record ReviewValidationResult(
    List<ReviewComment> Kept,
    Dictionary<string, int> DroppedByReason,
    int ReanchoredCount)
{
    public int DroppedCount => DroppedByReason.Values.Sum();
}

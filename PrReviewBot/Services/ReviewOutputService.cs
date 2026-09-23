using System.Globalization;
using System.Text;
using PrReviewBot.Models;
using Spectre.Console;

namespace PrReviewBot.Services;

public class ReviewOutputService
{
    private readonly string _outputDirectory;

    public ReviewOutputService(string? outputDirectory = null)
    {
        _outputDirectory = outputDirectory ?? Path.Combine(AppContext.BaseDirectory, "reviews");
        Directory.CreateDirectory(_outputDirectory);
    }

    // Reports what the local grounding checks threw away. Seeing this is how
    // you tell a quiet model from an over-aggressive filter.
    public static void DisplayValidationSummary(ReviewValidationResult validation)
    {
        if (validation.DroppedCount == 0 && validation.ReanchoredCount == 0)
        {
            return;
        }

        if (validation.DroppedCount != 0)
        {
            string reasons = string.Join(", ",
                validation.DroppedByReason.OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{kv.Value}× {kv.Key}"));
            AnsiConsole.MarkupLine(
                $"[grey]Filtered out {validation.DroppedCount} unverifiable finding(s): {Markup.Escape(reasons)}[/]");
        }

        if (validation.ReanchoredCount != 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Re-anchored {validation.ReanchoredCount} comment(s) to the line their evidence came from.[/]");
        }
    }

    // Writes a provider's unparsable reply next to the reviews so a failure can
    // be diagnosed without re-running the request.
    public string SaveRawResponse(PullRequestInfo pr, string rawResponse)
    {
        string path = Path.Combine(
            _outputDirectory,
            $"{DateTime.Now:yyyy-MM-dd_HHmmss}_PR{pr.Id}_FAILED_raw-response.txt");
        File.WriteAllText(path, rawResponse, Encoding.UTF8);
        return path;
    }

    public static void DisplayReview(
        PullRequestInfo pr, List<ReviewComment> comments, IReadOnlyList<string>? unreviewedFiles = null)
    {
        AnsiConsole.Write(new Rule($"[bold blue]PR #{pr.Id}: {Markup.Escape(pr.Title)}[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]Author: {Markup.Escape(pr.Author)} | {Markup.Escape(pr.SourceBranch)} → {Markup.Escape(pr.TargetBranch)}[/]");
        AnsiConsole.MarkupLine($"[grey]URL: {Markup.Escape(pr.Url)}[/]");
        AnsiConsole.WriteLine();

        int unreviewed = unreviewedFiles?.Count ?? 0;

        if (comments.Count == 0)
        {
            // "Looks good" is only true if everything was actually looked at.
            // Saying it after a partial review is the same false green as
            // reporting a failed parse as a clean review.
            if (unreviewed != 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ No issues found in the {pr.ChangedFiles.Count - unreviewed} file(s) that were reviewed — "
                    + $"but {unreviewed} file(s) could not be reviewed, so this is NOT a clean bill of health.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[green]✓ No issues found — looks good![/]");
            }

            return;
        }

        List<ReviewComment> primaryComments = [.. comments.Where(c => !c.IsAdditionalObservation)];
        List<ReviewComment> additionalComments = [.. comments.Where(c => c.IsAdditionalObservation)];

        if (primaryComments.Count != 0)
        {
            AnsiConsole.Write(new Rule("[bold]Review of PR Changes[/]").LeftJustified());
            RenderCommentGroup(primaryComments);
        }

        if (additionalComments.Count != 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold grey]💡 Additional Observations (outside PR changes)[/]").LeftJustified());
            RenderCommentGroup(additionalComments);
        }

        int criticalCount = comments.Count(c => c.Severity == CommentSeverity.Critical);
        int warningCount = comments.Count(c => c.Severity == CommentSeverity.Warning);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Summary: [red]{criticalCount} critical[/], [yellow]{warningCount} warnings[/], [blue]{comments.Count - criticalCount - warningCount} info[/][/]");

        if (unreviewed != 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Incomplete: {unreviewed} of {pr.ChangedFiles.Count} file(s) were not reviewed.[/]");
        }
    }

    public string SaveReviewToFile(
        PullRequestInfo pr, List<ReviewComment> comments, IReadOnlyList<string>? unreviewedFiles = null)
    {
        string fileName = BuildFileName(pr);
        string filePath = Path.Combine(_outputDirectory, fileName);

        StringBuilder sb = new();

        // Header
        sb.AppendLine(CultureInfo.InvariantCulture, $"# PR Review: {pr.Title}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Date:**       {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Repository:** {pr.RepositoryName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **PR #:**       {pr.Id}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Author:**     {pr.Author}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Branch:**     {pr.SourceBranch} → {pr.TargetBranch}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **URL:**        {pr.Url}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // The report outlives the terminal output, so an incomplete review
        // must say so here too — this file is what someone reads later when
        // deciding whether the PR was checked.
        if (unreviewedFiles is { Count: > 0 })
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"> ⚠️ **Incomplete review — {unreviewedFiles.Count} of {pr.ChangedFiles.Count} file(s) could not be reviewed:**");
            sb.AppendLine(">");
            foreach (string path in unreviewedFiles)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{path}`");
            }

            sb.AppendLine(">");
            sb.AppendLine("> Absence of comments on those files does not mean they are clean.");
            sb.AppendLine();
        }

        if (comments.Count == 0)
        {
            sb.AppendLine(unreviewedFiles is { Count: > 0 }
                ? "⚠️ No issues found in the files that *were* reviewed — see the warning above."
                : "✅ No issues found — looks good!");
        }
        else
        {
            // Summary counts
            int criticalCount = comments.Count(c => c.Severity == CommentSeverity.Critical);
            int warningCount = comments.Count(c => c.Severity == CommentSeverity.Warning);
            int infoCount = comments.Count(c => c.Severity == CommentSeverity.Info);

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"| Severity | Count |");
            sb.AppendLine($"|----------|-------|");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| 🔴 Critical | {criticalCount} |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| 🟡 Warning  | {warningCount} |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| 🔵 Info     | {infoCount} |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| **Total**   | **{comments.Count}** |");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // Comments grouped by file
            sb.AppendLine("## Review Comments");
            sb.AppendLine();

            List<ReviewComment> primaryComments = [.. comments.Where(c => !c.IsAdditionalObservation)];
            List<ReviewComment> additionalComments = [.. comments.Where(c => c.IsAdditionalObservation)];

            if (primaryComments.Count != 0)
            {
                AppendCommentGroup(sb, primaryComments, isAdditional: false);
            }

            if (additionalComments.Count != 0)
            {
                sb.AppendLine("## 💡 Additional Observations (outside PR changes)");
                sb.AppendLine();
                AppendCommentGroup(sb, additionalComments, isAdditional: true);
            }
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return filePath;
    }

    public static string FormatCommentForAzureDevOps(ReviewComment comment)
    {
        StringBuilder sb = new();
        string emoji = comment.Severity switch
        {
            CommentSeverity.Critical => "🔴 **Critical**",
            CommentSeverity.Warning => "🟡 **Warning**",
            _ => "🔵 **Info**"
        };

        sb.AppendLine(CultureInfo.InvariantCulture, $"{emoji}: {comment.Issue}");
        sb.AppendLine();
        sb.AppendLine(comment.Suggestion);

        if (!string.IsNullOrWhiteSpace(comment.CodeExample))
        {
            sb.AppendLine();
            sb.AppendLine("**Suggested change:**");
            sb.AppendLine(CultureInfo.InvariantCulture, $"```{GetLanguageHint(comment.FilePath)}");
            sb.AppendLine(comment.CodeExample);
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("*— AI Review Bot*");

        return sb.ToString();
    }

    private static void RenderCommentGroup(IEnumerable<ReviewComment> items)
    {
        foreach (IGrouping<string, ReviewComment> fileGroup in items.GroupBy(c => c.FilePath))
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]📄 {fileGroup.Key}[/]");

            foreach (ReviewComment? comment in fileGroup.OrderBy(c => c.LineNumber))
            {
                string color = comment.Severity switch
                {
                    CommentSeverity.Critical => "red",
                    CommentSeverity.Warning => "yellow",
                    _ => "blue"
                };
                string icon = comment.Severity switch
                {
                    CommentSeverity.Critical => "🔴",
                    CommentSeverity.Warning => "🟡",
                    _ => "🔵"
                };

                string lineInfo = comment.LineNumber.HasValue ? $" (line {comment.LineNumber})" : "";
                string confidence = comment.Confidence < 5 ? $" [grey]·confidence {comment.Confidence}/5[/]" : "";
                AnsiConsole.MarkupLine($"\n  {icon} [{color}]{comment.Severity}{lineInfo}[/]{confidence}: {Markup.Escape(comment.Issue)}");
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(comment.Suggestion)}[/]");

                if (!string.IsNullOrWhiteSpace(comment.CodeExample))
                {
                    AnsiConsole.Write(new Panel(
                        // Tabs are expanded here because the panel counts a tab
                        // as one column while the terminal draws it as up to
                        // eight, which pushes lines through the border.
                        new Markup($"[green]{Markup.Escape(comment.CodeExample.Replace("\t", "    ", StringComparison.Ordinal))}[/]"))
                        .Header("Suggested Code")
                        .BorderColor(Color.Green)
                        .Padding(1, 0));
                }
            }
        }
    }

    private static string BuildFileName(PullRequestInfo pr)
    {
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string repo = SanitizeForFileName(pr.RepositoryName);
        string prTitle = SanitizeForFileName(pr.Title);

        // Keep title segment reasonable — max 50 chars
        if (prTitle.Length > 50)
        {
            prTitle = prTitle[..50].TrimEnd('-');
        }

        return $"{date}_{repo}_PR{pr.Id}_{prTitle}.txt";
    }

    private static string SanitizeForFileName(string value)
    {
        // Replace invalid filename chars and spaces with hyphens, collapse runs
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        StringBuilder result = new();
        bool lastWasHyphen = false;

        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch) || invalid.Contains(ch) || ch == '.')
            {
                if (!lastWasHyphen)
                {
                    result.Append('-');
                    lastWasHyphen = true;
                }
            }
            else
            {
                result.Append(ch);
                lastWasHyphen = false;
            }
        }

        return result.ToString().Trim('-');
    }

    private static void AppendCommentGroup(StringBuilder sb, IEnumerable<ReviewComment> items, bool isAdditional)
    {
        string scopeTag = isAdditional ? "`[Optional]`" : "`[PR Change]`";

        foreach (IGrouping<string, ReviewComment> fileGroup in items.GroupBy(c => c.FilePath))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### 📄 `{fileGroup.Key}`");
            sb.AppendLine();

            foreach (ReviewComment? comment in fileGroup.OrderBy(c => c.LineNumber))
            {
                string icon = comment.Severity switch
                {
                    CommentSeverity.Critical => "🔴 Critical",
                    CommentSeverity.Warning => "🟡 Warning",
                    _ => "🔵 Info"
                };

                string lineInfo = comment.LineNumber.HasValue
                    ? $" — Line {comment.LineNumber}"
                    : "";

                sb.AppendLine(CultureInfo.InvariantCulture, $"#### {icon}{lineInfo} {scopeTag}");
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"**Issue:** {comment.Issue}");
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"*Confidence: {comment.Confidence}/5*");
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"{comment.Suggestion}");

                if (!string.IsNullOrWhiteSpace(comment.CodeExample))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Suggested change:**");
                    sb.AppendLine();

                    string lang = GetLanguageHint(fileGroup.Key);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"```{lang}");
                    sb.AppendLine(comment.CodeExample.TrimEnd());
                    sb.AppendLine("```");
                }

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }
    }

    private static string GetLanguageHint(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".vue" => "vue",
            ".ts" => "typescript",
            ".tsx" => "typescript",
            ".js" => "javascript",
            ".jsx" => "javascript",
            ".json" => "json",
            ".yaml" => "yaml",
            ".yml" => "yaml",
            ".xml" => "xml",
            ".razor" => "razor",
            ".html" => "html",
            ".css" => "css",
            ".scss" => "scss",
            _ => ""
        };
}

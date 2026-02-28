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

    public static void DisplayReview(PullRequestInfo pr, List<ReviewComment> comments)
    {
        AnsiConsole.Write(new Rule($"[bold blue]PR #{pr.Id}: {pr.Title}[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]Author: {pr.Author} | {pr.SourceBranch} → {pr.TargetBranch}[/]");
        AnsiConsole.MarkupLine($"[grey]URL: {pr.Url}[/]");
        AnsiConsole.WriteLine();

        if (comments.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ No issues found — looks good![/]");
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
    }

    public string SaveReviewToFile(PullRequestInfo pr, List<ReviewComment> comments)
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

        if (comments.Count == 0)
        {
            sb.AppendLine("✅ No issues found — looks good!");
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
                AppendCommentGroup(sb, primaryComments);
            }

            if (additionalComments.Count != 0)
            {
                sb.AppendLine("## 💡 Additional Observations (outside PR changes)");
                sb.AppendLine();
                AppendCommentGroup(sb, additionalComments);
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
            sb.AppendLine("```csharp");
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
                AnsiConsole.MarkupLine($"\n  {icon} [{color}]{comment.Severity}{lineInfo}[/]: {Markup.Escape(comment.Issue)}");
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(comment.Suggestion)}[/]");

                if (!string.IsNullOrWhiteSpace(comment.CodeExample))
                {
                    AnsiConsole.Write(new Panel(
                        new Markup($"[green]{Markup.Escape(comment.CodeExample)}[/]"))
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

    private static void AppendCommentGroup(StringBuilder sb, IEnumerable<ReviewComment> items)
    {
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

                sb.AppendLine(CultureInfo.InvariantCulture, $"#### {icon}{lineInfo}");
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"**Issue:** {comment.Issue}");
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
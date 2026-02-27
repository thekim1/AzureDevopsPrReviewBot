using PrReviewBot.Models;
using Spectre.Console;
using System.Text;

namespace PrReviewBot.Services;

public class ReviewOutputService
{
    private readonly string _outputDirectory;

    public ReviewOutputService(string? outputDirectory = null)
    {
        _outputDirectory = outputDirectory ?? Path.Combine(AppContext.BaseDirectory, "reviews");
        Directory.CreateDirectory(_outputDirectory);
    }

    public void DisplayReview(PullRequestInfo pr, List<ReviewComment> comments)
    {
        AnsiConsole.Write(new Rule($"[bold blue]PR #{pr.Id}: {pr.Title}[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]Author: {pr.Author} | {pr.SourceBranch} → {pr.TargetBranch}[/]");
        AnsiConsole.MarkupLine($"[grey]URL: {pr.Url}[/]");
        AnsiConsole.WriteLine();

        if (!comments.Any())
        {
            AnsiConsole.MarkupLine("[green]✓ No issues found — looks good![/]");
            return;
        }

        IEnumerable<IGrouping<string, ReviewComment>> grouped = comments.GroupBy(c => c.FilePath);

        foreach (IGrouping<string, ReviewComment> fileGroup in grouped)
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

        int criticalCount = comments.Count(c => c.Severity == CommentSeverity.Critical);
        int warningCount = comments.Count(c => c.Severity == CommentSeverity.Warning);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Summary: [red]{criticalCount} critical[/], [yellow]{warningCount} warnings[/], [blue]{comments.Count - criticalCount - warningCount} info[/][/]");
    }

    public string SaveReviewToFile(PullRequestInfo pr, List<ReviewComment> comments)
    {
        string fileName = BuildFileName(pr);
        string filePath = Path.Combine(_outputDirectory, fileName);

        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"# PR Review: {pr.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Date:**       {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Repository:** {pr.RepositoryName}");
        sb.AppendLine($"- **PR #:**       {pr.Id}");
        sb.AppendLine($"- **Author:**     {pr.Author}");
        sb.AppendLine($"- **Branch:**     {pr.SourceBranch} → {pr.TargetBranch}");
        sb.AppendLine($"- **URL:**        {pr.Url}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (!comments.Any())
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
            sb.AppendLine($"| 🔴 Critical | {criticalCount} |");
            sb.AppendLine($"| 🟡 Warning  | {warningCount} |");
            sb.AppendLine($"| 🔵 Info     | {infoCount} |");
            sb.AppendLine($"| **Total**   | **{comments.Count}** |");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // Comments grouped by file
            sb.AppendLine("## Review Comments");
            sb.AppendLine();

            IEnumerable<IGrouping<string, ReviewComment>> grouped = comments.GroupBy(c => c.FilePath);

            foreach (IGrouping<string, ReviewComment> fileGroup in grouped)
            {
                sb.AppendLine($"### 📄 `{fileGroup.Key}`");
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

                    sb.AppendLine($"#### {icon}{lineInfo}");
                    sb.AppendLine();
                    sb.AppendLine($"**Issue:** {comment.Issue}");
                    sb.AppendLine();
                    sb.AppendLine($"{comment.Suggestion}");

                    if (!string.IsNullOrWhiteSpace(comment.CodeExample))
                    {
                        sb.AppendLine();
                        sb.AppendLine("**Suggested change:**");
                        sb.AppendLine();

                        // Pick syntax highlighting hint from file extension
                        string lang = GetLanguageHint(fileGroup.Key);
                        sb.AppendLine($"```{lang}");
                        sb.AppendLine(comment.CodeExample.TrimEnd());
                        sb.AppendLine("```");
                    }

                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
            }
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return filePath;
    }

    public string FormatCommentForAzureDevOps(ReviewComment comment)
    {
        var sb = new StringBuilder();
        string emoji = comment.Severity switch
        {
            CommentSeverity.Critical => "🔴 **Critical**",
            CommentSeverity.Warning => "🟡 **Warning**",
            _ => "🔵 **Info**"
        };

        sb.AppendLine($"{emoji}: {comment.Issue}");
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

    private static string BuildFileName(PullRequestInfo pr)
    {
        string date = DateTime.Now.ToString("yyyy-MM-dd");
        string repo = SanitizeForFileName(pr.RepositoryName);
        string prTitle = SanitizeForFileName(pr.Title);

        // Keep title segment reasonable — max 50 chars
        if (prTitle.Length > 50)
            prTitle = prTitle[..50].TrimEnd('-');

        return $"{date}_{repo}_PR{pr.Id}_{prTitle}.txt";
    }

    private static string SanitizeForFileName(string value)
    {
        // Replace invalid filename chars and spaces with hyphens, collapse runs
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new StringBuilder();
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
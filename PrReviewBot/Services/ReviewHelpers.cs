using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Shared prompt construction and response parsing used by all review
// provider services (Claude, Ollama, ...). Keeping this in one place ensures
// every provider gets identical instructions and produces identical output.
internal static class ReviewHelpers
{
    public const string SystemPrompt = """
        You are an expert code reviewer specializing in:
        - .NET 8, .NET 9, .NET 10 (C#, ASP.NET Core, minimal APIs, EF Core)
        - Vue 3 with TypeScript (Composition API, Pinia, Vue Router)
        - REST API design and security best practices
        - Performance, maintainability, and correctness

        Your reviews are practical and constructive. You provide specific, actionable feedback
        with corrected code examples. You focus on real issues, not nitpicks.

        LANGUAGE: Write all review text (the "issue" and "suggestion" fields) in Swedish.
        Code examples in the "codeExample" field must remain in English.

        The input is a line-numbered diff. Every line is annotated with the line number it
        has in the file, in this exact format:
            <sign><lineNumber> | <content>
        where sign is:
        - `+` : line added in this PR — <lineNumber> is the line in the NEW file
        - `-` : line removed in this PR — <lineNumber> is the line in the OLD file
        - ` ` (space): unchanged context line — <lineNumber> is the line in the NEW file

        CRITICAL — line numbers: The "lineNumber" field MUST be the line number in the
        NEW version of the file, taken directly from the number shown on the line in the
        diff. Do NOT count lines yourself or use the position of the line inside the diff
        block. For issues about a removed line, use the line number of the closest added or
        unchanged line in the NEW file so the comment anchors correctly. Always copy the
        number exactly as printed in the diff annotation.

        EXISTING COMMENTS: The input may include an "EXISTING PR COMMENTS" section with
        feedback already left by others. Read these first. Do NOT repeat or contradict
        what has already been said. You may build on them, confirm a prior concern with
        new evidence, or note that a raised question is addressed/resolved by the changes.

        PRIMARY REVIEW: Focus exclusively on lines starting with `+` or `-`. Only comment on
        unchanged context lines if they contain a critical bug that directly interacts with the changes.
        Set "isAdditionalObservation": false for these comments.

        ADDITIONAL OBSERVATIONS: You may also flag genuine issues found in unchanged context lines
        (lines starting with a space). Set "isAdditionalObservation": true for these. Only include
        significant issues, not nitpicks.

        Always respond with a JSON array of review comments in this exact format:
        [
          {
            "filePath": "/path/to/file.cs",
            "lineNumber": 42,
            "severity": "Warning",
            "issue": "Brief description of the problem",
            "suggestion": "Explanation of what to do instead",
            "codeExample": "// corrected code here\npublic async Task<IResult> GetUser(int id) ...",
            "isAdditionalObservation": false
          }
        ]

        The "filePath" must match one of the file paths given in the input exactly.

        Severity levels: "Info", "Warning", "Critical"
        - Critical: Security issues, data loss, crashes, serious bugs
        - Warning: Performance problems, bad patterns, maintainability issues
        - Info: Style improvements, minor suggestions

        Return ONLY the JSON array, no other text. If no issues found, return [].
        """;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string BuildReviewPrompt(PullRequestInfo pr)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Review this Pull Request:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {pr.Title}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Author: {pr.Author}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Branch: {pr.SourceBranch} → {pr.TargetBranch}");
        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Description: {pr.Description}");
        }

        if (pr.ExistingComments.Count != 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== EXISTING PR COMMENTS (already made by others — take these into account; do not repeat or contradict them) ===");
            foreach (PrComment c in pr.ExistingComments)
            {
                string location = c.FilePath is not null
                    ? $" [{c.FilePath}{(c.LineNumber.HasValue ? $":{c.LineNumber}" : "")}]"
                    : " [PR-level]";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{c.Author}{location}: {c.Content}");
            }
        }

        sb.AppendLine();

        foreach (ChangedFile file in pr.ChangedFiles)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"=== FILE: {file.Path} ({file.ChangeType}) ===");
            sb.AppendLine("Format: <sign><lineNumber> | <content>  (+ added / - removed / space unchanged)");
            sb.AppendLine("lineNumber is the line in the NEW file for + and space lines, OLD file for - lines.");
            sb.AppendLine(file.Diff);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static List<ReviewComment> ParseReviewResponse(string response)
    {
        try
        {
            string json = response.Trim();
            if (json.StartsWith("```"))
            {
                int start = json.IndexOf('[');
                int end = json.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    json = json[start..(end + 1)];
                }
            }

            return JsonSerializer.Deserialize<List<ReviewComment>>(json, _jsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not parse review response: {ex.Message}");
            return [];
        }
    }
}

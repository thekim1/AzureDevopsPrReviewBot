using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

public class ClaudeReviewService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeSettings _settings;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public ClaudeReviewService(ClaudeSettings settings)
    {
        _settings = settings;
        // Official SDK: ApiKey is a property on the client initializer
        _client = new AnthropicClient { ApiKey = settings.ApiKey };
    }

    public async Task<List<ReviewComment>> ReviewPullRequestAsync(PullRequestInfo pr)
    {
        string prompt = BuildReviewPrompt(pr);

        MessageCreateParams parameters = new()
        {
            Model = _settings.Model,
            MaxTokens = 4096,
            System = SystemPrompt,
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = prompt
                }
            ]
        };

        Message response = await _client.Messages.Create(parameters);

        string content = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .FirstOrDefault()?.Text ?? "";

        return ParseReviewResponse(content);
    }

    private const string SystemPrompt = """
        You are an expert code reviewer specializing in:
        - .NET 8, .NET 9, .NET 10 (C#, ASP.NET Core, minimal APIs, EF Core)
        - Vue 3 with TypeScript (Composition API, Pinia, Vue Router)
        - REST API design and security best practices
        - Performance, maintainability, and correctness

        Your reviews are practical and constructive. You provide specific, actionable feedback
        with corrected code examples. You focus on real issues, not nitpicks.

        The input is in unified diff format. Each line is prefixed with:
        - `+` : line added in this PR
        - `-` : line removed in this PR
        - ` ` (space): unchanged context line

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

        Severity levels: "Info", "Warning", "Critical"
        - Critical: Security issues, data loss, crashes, serious bugs
        - Warning: Performance problems, bad patterns, maintainability issues
        - Info: Style improvements, minor suggestions

        Return ONLY the JSON array, no other text. If no issues found, return [].
        """;

    private static string BuildReviewPrompt(PullRequestInfo pr)
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

        sb.AppendLine();

        foreach (ChangedFile file in pr.ChangedFiles)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"=== FILE: {file.Path} ({file.ChangeType}) ===");
            sb.AppendLine("(+ = added, - = removed, space = unchanged context)");
            sb.AppendLine(file.Diff);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<ReviewComment> ParseReviewResponse(string response)
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
            Console.WriteLine($"Warning: Could not parse Claude response: {ex.Message}");
            return [];
        }
    }
}
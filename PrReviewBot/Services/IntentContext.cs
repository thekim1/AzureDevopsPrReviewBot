using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Turns what Azure DevOps knows about why a change was made — linked work
// items and commit messages — into short plain text for the prompt.
//
// Without it the model reviews code against what it guesses the code is for.
// With it, it can tell a deliberate behaviour change from an accidental one.
internal static partial class IntentContext
{
    // Work item descriptions are HTML. Tags, entities and runs of whitespace
    // cost tokens and carry no meaning for the reviewer.
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        string text = BlockTag().Replace(html, "\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = SpacesAndTabs().Replace(text, " ");
        text = BlankLines().Replace(text, "\n");
        return text.Trim();
    }

    // One work item, cut to its share of the budget.
    public static LinkedWorkItem Describe(
        int id, string? type, string? title, string? descriptionHtml, string? acceptanceHtml, int maxChars)
    {
        string description = Cut(HtmlToText(descriptionHtml), maxChars / 2);
        string acceptance = Cut(HtmlToText(acceptanceHtml), maxChars - description.Length);

        return new LinkedWorkItem
        {
            Id = id,
            Type = type ?? "",
            Title = title ?? "",
            Description = description,
            AcceptanceCriteria = acceptance
        };
    }

    // Commit messages worth reading: merges and empty messages are noise, and
    // only the subject line and a short body are kept.
    public static List<string> UsefulCommitMessages(IEnumerable<string?> messages, int maxCommits, int maxCharsEach)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string? message in messages)
        {
            string text = (message ?? "").Trim();
            if (text.Length == 0
                || text.StartsWith("Merge ", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Merged PR ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            text = Cut(BlankLines().Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), "\n"), maxCharsEach);
            if (seen.Add(text))
            {
                result.Add(text);
            }

            if (result.Count >= maxCommits)
            {
                break;
            }
        }

        return result;
    }

    private static string Cut(string text, int max)
        => max <= 0 ? "" : text.Length <= max ? text : text[..Math.Max(0, max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(@"\s*\n\s*(\n\s*)*")]
    private static partial Regex BlankLines();
}

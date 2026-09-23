using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// A PR-wide list of the declarations each changed file adds or removes, sent
// with every batch.
//
// Batches see only their own files, so a signature changed in one batch and
// called in another could never be checked against each other. The summary
// gives every batch the shape of the whole change — one line per changed
// public declaration — without the code, which stays in its own batch.
internal static partial class ChangeSummary
{
    private const int MaxDeclarationsPerFile = 8;

    public static string Build(IReadOnlyList<ChangedFile> files, int maxChars)
    {
        StringBuilder sb = new();

        foreach (ChangedFile file in files)
        {
            List<string> declarations = ChangedDeclarations(file.Diff);

            StringBuilder entry = new();
            entry.Append(CultureInfo.InvariantCulture, $"{file.Path} ({file.ChangeType})");
            if (declarations.Count == 0)
            {
                entry.AppendLine(" — no public declarations changed");
            }
            else
            {
                entry.AppendLine();
                foreach (string declaration in declarations.Take(MaxDeclarationsPerFile))
                {
                    entry.AppendLine(CultureInfo.InvariantCulture, $"  {declaration}");
                }

                if (declarations.Count > MaxDeclarationsPerFile)
                {
                    entry.AppendLine(CultureInfo.InvariantCulture, $"  … {declarations.Count - MaxDeclarationsPerFile} more");
                }
            }

            if (sb.Length + entry.Length > maxChars)
            {
                sb.AppendLine("… (summary cut to fit its budget)");
                break;
            }

            sb.Append(entry);
        }

        return sb.ToString();
    }

    // "+ decl" / "- decl" for every declaration-looking line the diff adds or
    // removes. A line both removed and added unchanged (moved, or re-indented)
    // is not a change and is left out.
    internal static List<string> ChangedDeclarations(string diff)
    {
        List<(char Op, string Text)> changed = [];

        foreach (string raw in diff.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[0] is not ('+' or '-'))
            {
                continue;
            }

            int bar = line.IndexOf(" | ", StringComparison.Ordinal);
            if (bar < 0)
            {
                continue;
            }

            string content = line[(bar + 3)..];
            if (LooksLikeDeclaration(content))
            {
                changed.Add((line[0], Clean(content)));
            }
        }

        HashSet<string> removed = [.. changed.Where(c => c.Op == '-').Select(c => c.Text)];
        HashSet<string> added = [.. changed.Where(c => c.Op == '+').Select(c => c.Text)];

        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((char op, string text) in changed)
        {
            bool unchanged = op == '-' ? added.Contains(text) : removed.Contains(text);
            if (!unchanged && seen.Add(op + text))
            {
                result.Add($"{op} {text}");
            }
        }

        return result;
    }

    // Declarations other files can see: C# members and types that are not
    // private, TypeScript exports, and a Vue component's props and emits.
    internal static bool LooksLikeDeclaration(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
        {
            return false;
        }

        return CSharpDeclaration().IsMatch(trimmed)
            || trimmed.StartsWith("export ", StringComparison.Ordinal)
            || trimmed.Contains("defineProps", StringComparison.Ordinal)
            || trimmed.Contains("defineEmits", StringComparison.Ordinal);
    }

    // The declaration without its body or trailing punctuation.
    private static string Clean(string line)
    {
        string text = line.Trim();

        int arrow = text.IndexOf("=>", StringComparison.Ordinal);
        if (arrow > 0 && text.Contains('(', StringComparison.Ordinal))
        {
            text = text[..arrow];
        }

        return text.TrimEnd('{', ' ', ';', ',').Trim();
    }

    // Starts with a non-private access modifier. Fields, properties, methods,
    // constructors, types and delegates all do.
    [GeneratedRegex(@"^(\[.*\]\s*)?(public|internal|protected)\b")]
    private static partial Regex CSharpDeclaration();
}

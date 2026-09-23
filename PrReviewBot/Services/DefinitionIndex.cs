using System.Text;
using System.Text.RegularExpressions;

namespace PrReviewBot.Services;

// Finds the files that define what the changed code uses, and reduces each to
// an outline: the declarations, without bodies.
//
// This is what the partial-view rule in the prompt costs most: a call into a
// type the model has not seen gets a "might be null / might not exist /
// might throw" finding, or the model stays silent about a real mismatch. An
// outline of the type answers most of those questions for a few hundred
// characters.
//
// Deliberately heuristic. A C# type is found by the convention that Foo lives
// in Foo.cs; a TypeScript module by its import path. Anything that does not
// resolve cleanly is simply left out.
internal static partial class DefinitionIndex
{
    private static readonly string[] ScriptExtensions = [".ts", ".tsx", ".js", ".jsx", ".vue", ".d.ts", "/index.ts", "/index.js"];

    // Names that are PascalCase but never worth an outline even if a repo
    // happens to have a file by that name.
    private static readonly HashSet<string> CommonTypeNames = new(StringComparer.Ordinal)
    {
        "Task", "ValueTask", "List", "Dictionary", "HashSet", "IEnumerable", "IReadOnlyList", "IList",
        "String", "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "Exception", "Action", "Func",
        "CancellationToken", "Results", "TypedResults", "IResult", "ILogger", "Console", "Math",
        "Promise", "Array", "Record", "Partial", "Map", "Set", "Error", "Date", "JSON", "Object"
    };

    // What one changed file refers to: C# type names, each with a score (a
    // changed line counts three times as much as a context line), and
    // TypeScript import specifiers.
    public static (Dictionary<string, int> TypeNames, List<string> Imports) References(string diff)
    {
        Dictionary<string, int> names = new(StringComparer.Ordinal);
        List<string> imports = [];

        foreach (string raw in diff.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[0] is not ('+' or ' '))
            {
                continue;
            }

            int bar = line.IndexOf(" | ", StringComparison.Ordinal);
            if (bar < 0)
            {
                continue;
            }

            string content = line[(bar + 3)..];
            int weight = line[0] == '+' ? 3 : 1;

            Match import = ImportFrom().Match(content);
            if (import.Success)
            {
                imports.Add(import.Groups["spec"].Value);
                continue;
            }

            string code = StripStringsAndComments(content);
            foreach (Match m in PascalIdentifier().Matches(code))
            {
                string name = m.Value;
                if (name.Length >= 3 && !CommonTypeNames.Contains(name))
                {
                    names[name] = names.GetValueOrDefault(name) + weight;
                }
            }
        }

        return (names, imports.Distinct(StringComparer.Ordinal).ToList());
    }

    // The definition files worth fetching for a PR, best first: each with how
    // strongly the PR refers to it and which changed files do. Files that are
    // part of the PR are left out — the model sees those already.
    public static List<DefinitionCandidate> SelectCandidates(
        IReadOnlyList<PrReviewBot.Models.ChangedFile> files,
        IReadOnlyCollection<string> repoFiles,
        IReadOnlySet<string> excludedPaths,
        int max)
    {
        HashSet<string> all = new(repoFiles, StringComparer.Ordinal);
        ILookup<string, string> byName = repoFiles.ToLookup(p => p[(p.LastIndexOf('/') + 1)..], StringComparer.Ordinal);
        Dictionary<string, DefinitionCandidate> found = new(StringComparer.Ordinal);

        void Add(string? path, int score, string from)
        {
            if (path is null || excludedPaths.Contains(path))
            {
                return;
            }

            if (!found.TryGetValue(path, out DefinitionCandidate? candidate))
            {
                candidate = new DefinitionCandidate(path);
                found[path] = candidate;
            }

            candidate.Score += score;
            if (!candidate.ReferencedFrom.Contains(from))
            {
                candidate.ReferencedFrom.Add(from);
            }
        }

        foreach (PrReviewBot.Models.ChangedFile file in files)
        {
            (Dictionary<string, int> names, List<string> imports) = References(file.Diff);

            if (file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach ((string name, int score) in names)
                {
                    Add(ResolveCSharpType(name, file.Path, byName), score, file.Path);
                }
            }
            else
            {
                // An import is a deliberate dependency; weigh it like a
                // type used on a changed line.
                foreach (string spec in imports)
                {
                    Add(ResolveImport(spec, file.Path, all), 3, file.Path);
                }
            }
        }

        return [.. found.Values.OrderByDescending(c => c.Score).ThenBy(c => c.Path, StringComparer.Ordinal).Take(max)];
    }

    // The file that defines C# type `name`, by the Foo-in-Foo.cs convention.
    // Several candidates (the same name in two projects) resolve to the one
    // sharing the longest folder prefix with the referencing file.
    public static string? ResolveCSharpType(string name, string referencingPath, ILookup<string, string> filesByName)
    {
        List<string> candidates = [.. filesByName[name + ".cs"]];
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(c => CommonPrefixLength(c, referencingPath))
            .ThenBy(c => c.Length)
            .First();
    }

    // The file an import specifier points at. Relative paths resolve against
    // the importing file; "@/" is the Vue/Vite alias for the project's src
    // folder, taken to be the nearest "/src/" above the importing file.
    // Package imports ("vue", "@vueuse/core") resolve to nothing.
    public static string? ResolveImport(string spec, string importingPath, IReadOnlySet<string> allFiles)
    {
        string? basePath;
        if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal))
        {
            basePath = Combine(Folder(importingPath), spec);
        }
        else if (spec.StartsWith("@/", StringComparison.Ordinal))
        {
            int src = importingPath.LastIndexOf("/src/", StringComparison.OrdinalIgnoreCase);
            if (src < 0)
            {
                return null;
            }

            basePath = importingPath[..(src + 4)] + "/" + spec[2..];
        }
        else
        {
            return null;
        }

        if (allFiles.Contains(basePath))
        {
            return basePath;
        }

        foreach (string extension in ScriptExtensions)
        {
            if (allFiles.Contains(basePath + extension))
            {
                return basePath + extension;
            }
        }

        return null;
    }

    // Declarations only. For C#: type declarations and non-private member
    // signatures, attributes dropped, bodies dropped. For TypeScript: export
    // lines, plus the members of exported interfaces and types, which is
    // where the shape of the data is. For Vue: the props and emits blocks.
    public static string Outline(string path, string content, int maxChars)
    {
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> kept = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? CSharpOutline(lines)
            : ScriptOutline(lines);

        StringBuilder sb = new();
        foreach (string line in kept)
        {
            if (sb.Length + line.Length + 1 > maxChars)
            {
                sb.AppendLine("  … (outline cut to fit its budget)");
                break;
            }

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> CSharpOutline(string[] lines)
    {
        List<string> kept = [];

        // Inside an interface or enum, members carry no access modifier, so
        // every member line is kept until the block closes.
        int memberBlockIndent = -1;
        // A signature split over several lines is kept until its parentheses close.
        int openParens = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (openParens > 0)
            {
                kept.Add(line.TrimEnd().TrimEnd('{').TrimEnd());
                openParens += Count(trimmed, '(') - Count(trimmed, ')');
                continue;
            }

            if (trimmed.StartsWith('[') || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
            {
                continue;
            }

            if (memberBlockIndent >= 0)
            {
                int indent = ScopeExpander.Indent(line);
                if (trimmed == "}" && indent <= memberBlockIndent)
                {
                    memberBlockIndent = -1;
                    continue;
                }

                if (trimmed != "{" && indent > memberBlockIndent)
                {
                    kept.Add(line.TrimEnd());
                    continue;
                }
            }

            bool typeDeclaration = TypeKeyword().IsMatch(trimmed) && !trimmed.StartsWith("private", StringComparison.Ordinal);
            bool visibleMember = VisibleMember().IsMatch(trimmed);
            if (typeDeclaration || visibleMember)
            {
                kept.Add(line.TrimEnd().TrimEnd('{').TrimEnd());
                openParens = Count(trimmed, '(') - Count(trimmed, ')');

                if (InterfaceOrEnum().IsMatch(trimmed) && !trimmed.EndsWith(';'))
                {
                    memberBlockIndent = ScopeExpander.Indent(line);
                }
            }
        }

        return kept;
    }

    private static List<string> ScriptOutline(string[] lines)
    {
        List<string> kept = [];
        int blockDepth = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (blockDepth > 0)
            {
                kept.Add(line.TrimEnd());
                blockDepth += Count(trimmed, '{') - Count(trimmed, '}');
                continue;
            }

            bool shapeBlock = (trimmed.StartsWith("export interface ", StringComparison.Ordinal)
                    || trimmed.StartsWith("interface ", StringComparison.Ordinal)
                    || TypeAlias().IsMatch(trimmed)
                    || trimmed.Contains("defineProps", StringComparison.Ordinal)
                    || trimmed.Contains("defineEmits", StringComparison.Ordinal))
                && trimmed.Contains('{', StringComparison.Ordinal);

            if (shapeBlock)
            {
                kept.Add(line.TrimEnd());
                blockDepth = Count(trimmed, '{') - Count(trimmed, '}');
                continue;
            }

            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                int arrow = trimmed.IndexOf("=>", StringComparison.Ordinal);
                string declaration = arrow > 0 ? trimmed[..arrow].TrimEnd() : trimmed.TrimEnd('{', ' ');
                kept.Add(declaration);
            }
        }

        return kept;
    }

    private static int Count(string text, char c) => text.Count(x => x == c);

    // Code without string literals and trailing comments, so words inside
    // messages ("User not found") are not taken for type names.
    private static string StripStringsAndComments(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        string code = comment >= 0 ? line[..comment] : line;
        return StringLiteral().Replace(code, "\"\"");
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
        {
            i++;
        }

        return i;
    }

    private static string Folder(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "" : path[..slash];
    }

    private static string Combine(string folder, string relative)
    {
        List<string> parts = [.. folder.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string part in relative.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count != 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }

        return "/" + string.Join('/', parts);
    }

    [GeneratedRegex(@"\bimport\b[^'""]*\bfrom\s*['""](?<spec>[^'""]+)['""]|^\s*import\s*['""](?<spec>[^'""]+)['""]")]
    private static partial Regex ImportFrom();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9]+\b")]
    private static partial Regex PascalIdentifier();

    [GeneratedRegex(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b(class|record|struct|interface|enum)\s+[A-Z]")]
    private static partial Regex TypeKeyword();

    [GeneratedRegex(@"^(public|internal|protected)\b")]
    private static partial Regex VisibleMember();

    [GeneratedRegex(@"\b(interface|enum)\s+[A-Z]")]
    private static partial Regex InterfaceOrEnum();

    [GeneratedRegex(@"^(export\s+)?type\s+\w+.*=\s*\{")]
    private static partial Regex TypeAlias();
}

internal sealed class DefinitionCandidate(string path)
{
    public string Path { get; } = path;
    public int Score { get; set; }
    public List<string> ReferencedFrom { get; } = [];
}

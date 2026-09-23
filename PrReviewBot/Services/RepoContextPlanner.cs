using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Works out which repository convention files to read, from a listing of the
// folders they could live in rather than by probing every candidate path.
//
// Probing costs one request per candidate, and most candidates do not exist,
// so most of those requests were 404s. A listing per folder is a handful of
// requests however many candidates there are.
internal static class RepoContextPlanner
{
    // The folders that have to be listed to know which candidates exist.
    public static List<string> DirectoriesToList(IEnumerable<RepoContextGroup> groups)
        => [.. groups.SelectMany(g => g.Paths).Select(ParentOf).Distinct(StringComparer.OrdinalIgnoreCase)];

    // The candidates of each group that exist in the repository, in the
    // group's order of preference, spelled as the repository spells them.
    //
    // Matching ignores case so that "readme.md" is found as "/README.md".
    public static List<List<string>> ResolveCandidates(
        IEnumerable<RepoContextGroup> groups, IReadOnlyCollection<string> existingPaths)
    {
        List<List<string>> result = [];

        foreach (RepoContextGroup group in groups)
        {
            List<string> found = [];
            foreach (string candidate in group.Paths)
            {
                string? actual = existingPaths.FirstOrDefault(p => string.Equals(p, candidate, StringComparison.Ordinal))
                    ?? existingPaths.FirstOrDefault(p => string.Equals(p, candidate, StringComparison.OrdinalIgnoreCase));

                if (actual is not null && !found.Contains(actual, StringComparer.Ordinal))
                {
                    found.Add(actual);
                }
            }

            result.Add(found);
        }

        return result;
    }

    // Builds the context bundle from the fetched files, one per group.
    //
    // The first file in a group with any content wins: the alternatives within
    // a group are different names for the same kind of document, not extra
    // information. Groups are filled in order until the total budget runs out.
    // A null content means the file could not be read.
    public static List<RepoContextFile> Assemble(
        IReadOnlyList<IReadOnlyList<(string Path, string? Content)>> groups,
        int maxTotalChars,
        int maxFileChars)
    {
        List<RepoContextFile> result = [];
        int budget = maxTotalChars;

        foreach (IReadOnlyList<(string Path, string? Content)> group in groups)
        {
            if (budget <= 0)
            {
                break;
            }

            foreach ((string path, string? content) in group)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                int limit = Math.Min(maxFileChars, budget);
                bool truncated = content.Length > limit;
                string kept = truncated ? content[..limit] : content;

                budget -= kept.Length;
                result.Add(new RepoContextFile
                {
                    Path = path,
                    Content = kept,
                    IsTruncated = truncated
                });
                break;
            }
        }

        return result;
    }

    // Every folder between the repository root and each changed file, root
    // excluded (the root is covered by the regular repository context),
    // nearest to the files first.
    public static List<string> AncestorDirectories(IEnumerable<string> changedPaths)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in changedPaths)
        {
            string dir = ParentOf(path.Replace('\\', '/'));
            while (dir != "/")
            {
                if (seen.Add(dir))
                {
                    result.Add(dir);
                }

                dir = ParentOf(dir);
            }
        }

        return [.. result.OrderByDescending(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.OrdinalIgnoreCase)];
    }

    // From the listing of one folder, the convention files to read: the first
    // agent-instructions file (they point at each other, as at the root), and
    // the folder's .editorconfig.
    public static List<string> ScopedFilesIn(IReadOnlyCollection<string> filesInFolder)
    {
        string[] agentNames = ["AGENTS.md", "CLAUDE.md", ".cursorrules"];
        List<string> result = [];

        string? agent = agentNames
            .Select(n => filesInFolder.FirstOrDefault(f => FileName(f).Equals(n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(f => f is not null);
        if (agent is not null)
        {
            result.Add(agent);
        }

        string? editorConfig = filesInFolder.FirstOrDefault(f => FileName(f).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase));
        if (editorConfig is not null)
        {
            result.Add(editorConfig);
        }

        return result;
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }
}

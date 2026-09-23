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

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }
}

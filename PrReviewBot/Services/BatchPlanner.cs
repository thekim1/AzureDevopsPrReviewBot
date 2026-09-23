using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Decides how a PR's files are cut into review requests, and in which order
// those requests are sent.
internal static class BatchPlanner
{
    // Names too common to relate files by on their own: two index.ts files in
    // different folders have nothing to do with each other.
    private static readonly HashSet<string> GenericStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "index", "types", "type", "utils", "util", "helpers", "helper", "constants", "const",
        "main", "app", "program", "startup", "readme", "package", "tsconfig", "vite", "webpack",
        "styles", "style", "config", "settings", "module", "routes", "router", "store", "models", "model"
    };

    // Packs files into batches bounded by both file count and diff size, so one
    // enormous file cannot smuggle a whole batch's worth of tokens through a
    // count-based limit. A file larger than the character budget on its own
    // still gets a batch to itself rather than being dropped.
    //
    // Related files are kept in the same batch whenever they fit: a class with
    // its interface and its tests, a component next to the files in its
    // folder. Batching in list order used to separate them, so one batch saw a
    // changed signature and another saw its caller, and neither could tell
    // whether the two still agreed.
    public static List<IReadOnlyList<ChangedFile>> Split(
        IReadOnlyList<ChangedFile> files, int maxFilesPerBatch, int maxDiffCharsPerBatch)
    {
        List<IReadOnlyList<ChangedFile>> batches = [];
        List<ChangedFile> current = [];
        int currentChars = 0;

        void Flush()
        {
            if (current.Count != 0)
            {
                batches.Add(current);
                current = [];
                currentChars = 0;
            }
        }

        bool Fits(int count, int chars) =>
            current.Count + count <= maxFilesPerBatch
            && (current.Count == 0 || currentChars + chars <= maxDiffCharsPerBatch);

        foreach (List<ChangedFile> cluster in Cluster(files))
        {
            int clusterChars = cluster.Sum(f => f.Diff.Length);

            // A whole cluster that fits in an empty batch is not split up just
            // because the current batch is partly full.
            if (!Fits(cluster.Count, clusterChars)
                && cluster.Count <= maxFilesPerBatch && clusterChars <= maxDiffCharsPerBatch)
            {
                Flush();
            }

            foreach (ChangedFile file in cluster)
            {
                if (!Fits(1, file.Diff.Length))
                {
                    Flush();
                }

                current.Add(file);
                currentChars += file.Diff.Length;
            }
        }

        Flush();
        return batches;
    }

    // Groups files that belong together, then orders the groups by folder so
    // neighbouring files end up in neighbouring batches. Within a folder the
    // original order is kept.
    internal static List<List<ChangedFile>> Cluster(IReadOnlyList<ChangedFile> files)
    {
        List<List<ChangedFile>> clusters = [];
        Dictionary<string, List<ChangedFile>> byKey = new(StringComparer.OrdinalIgnoreCase);

        foreach (ChangedFile file in files)
        {
            string key = RelationKey(file.Path);
            if (!byKey.TryGetValue(key, out List<ChangedFile>? cluster))
            {
                cluster = [];
                byKey[key] = cluster;
                clusters.Add(cluster);
            }

            cluster.Add(file);
        }

        return [.. clusters.OrderBy(c => Folder(c[0].Path), StringComparer.OrdinalIgnoreCase)];
    }

    // Files with the same key are related. The key is the file name up to its
    // first dot, without a test suffix or an interface prefix, so that
    // Foo.cs, IFoo.cs, FooTests.cs, Foo.test.ts and Foo.Designer.cs all agree.
    internal static string RelationKey(string path)
    {
        string normalized = path.Replace('\\', '/');
        string name = normalized[(normalized.LastIndexOf('/') + 1)..];
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = dot > 0 ? name[..dot] : name;

        foreach (string suffix in (string[])["Tests", "Test", "Spec"])
        {
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
            {
                stem = stem[..^suffix.Length];
                break;
            }
        }

        if (stem.Length > 2 && stem[0] == 'I' && char.IsUpper(stem[1]) && char.IsLower(stem[2]))
        {
            stem = stem[1..];
        }

        return GenericStems.Contains(stem)
            ? Folder(normalized) + "/" + stem.ToLowerInvariant()
            : stem.ToLowerInvariant();
    }

    private static string Folder(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    // Orders the batches to finish the whole PR as early as possible.
    //
    // The cache warm-up batch runs alone, so everything else waits for it: the
    // smallest batch keeps that wait short. The rest start largest first, so
    // the slowest request is not the one left to start last while every other
    // slot sits idle.
    public static BatchSchedule Schedule(IReadOnlyList<IReadOnlyList<ChangedFile>> batches, bool warmPrefixCache)
    {
        List<int> bySizeDescending =
        [
            .. Enumerable.Range(0, batches.Count)
                .OrderByDescending(i => Size(batches[i]))
                .ThenBy(i => i)
        ];

        if (!warmPrefixCache || batches.Count < 2)
        {
            return new BatchSchedule(null, bySizeDescending);
        }

        int smallest = Enumerable.Range(0, batches.Count)
            .OrderBy(i => Size(batches[i]))
            .ThenBy(i => i)
            .First();

        bySizeDescending.Remove(smallest);
        return new BatchSchedule(smallest, bySizeDescending);
    }

    private static int Size(IReadOnlyList<ChangedFile> batch) => batch.Sum(f => f.Diff.Length);
}

// WarmUpIndex: the batch to run alone before the others, if any.
// Order: the remaining batch indices, in the order they should be started.
internal sealed record BatchSchedule(int? WarmUpIndex, List<int> Order);

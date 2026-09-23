using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Decides how a PR's files are cut into review requests, and in which order
// those requests are sent.
internal static class BatchPlanner
{
    // Packs files into batches bounded by both file count and diff size, so one
    // enormous file cannot smuggle a whole batch's worth of tokens through a
    // count-based limit. A file larger than the character budget on its own
    // still gets a batch to itself rather than being dropped.
    public static List<IReadOnlyList<ChangedFile>> Split(
        IReadOnlyList<ChangedFile> files, int maxFilesPerBatch, int maxDiffCharsPerBatch)
    {
        List<IReadOnlyList<ChangedFile>> batches = [];
        List<ChangedFile> current = [];
        int currentChars = 0;

        foreach (ChangedFile file in files)
        {
            int size = file.Diff.Length;
            bool wouldOverflow = current.Count >= maxFilesPerBatch
                || (current.Count != 0 && currentChars + size > maxDiffCharsPerBatch);

            if (wouldOverflow)
            {
                batches.Add(current);
                current = [];
                currentChars = 0;
            }

            current.Add(file);
            currentChars += size;
        }

        if (current.Count != 0)
        {
            batches.Add(current);
        }

        return batches;
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

using System.Globalization;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Splits a pull request across several review requests and merges the results.
//
// A single request covering a whole PR fails badly on large changes: the model
// has to hold every file in its head before it can answer, and on a reasoning
// model the deliberation alone can exhaust the output budget, returning an
// empty message after minutes of work. Observed on a 20-file PR: 97k characters
// of diff in, 16,384 completion tokens spent entirely on reasoning, zero
// characters of answer.
//
// Smaller batches bound the work per request, so the model reaches an answer.
// They also make failure partial — one bad batch costs a few files, not the
// whole review.
public sealed class PullRequestReviewer
{
    private readonly IReviewService _reviewService;
    private readonly ReviewSettings _settings;

    public PullRequestReviewer(IReviewService reviewService, ReviewSettings settings)
    {
        _reviewService = reviewService;
        _settings = settings;
    }

    public async Task<PullRequestReviewResult> ReviewAsync(
        PullRequestInfo pr,
        Action<string>? progress = null,
        IProgress<ReviewProgress>? liveProgress = null,
        Action<IReadOnlyList<BatchInfo>>? onBatchesPlanned = null,
        Action<int, bool>? onBatchCompleted = null)
    {
        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(
            pr.ChangedFiles, _settings.MaxFilesPerRequest, _settings.MaxDiffCharsPerRequest);

        // Tell the caller the shape of the work before any of it starts, so a
        // live display can lay out one row per batch.
        onBatchesPlanned?.Invoke(
            [.. batches.Select((b, i) => new BatchInfo(i, [.. b.Select(f => f.Path)]))]);

        // The batches are independent requests, so running them one after
        // another just multiplies wall-clock time by the batch count. Results
        // are collected by index so the merged output stays in file order
        // regardless of which request finishes first.
        List<ReviewComment>?[] results = new List<ReviewComment>?[batches.Count];
        BatchFailure?[] batchFailures = new BatchFailure?[batches.Count];

        int completed = 0;
        using SemaphoreSlim gate = new(Math.Max(1, _settings.MaxParallelRequests));

        async Task RunAsync(IReadOnlyList<ChangedFile> batch, int index, bool alone)
        {
            if (!alone)
            {
                await gate.WaitAsync();
            }

            try
            {
                // Stamp each update with its batch index here rather than
                // storing the index on the service: one service instance is
                // shared by every batch, so mutable state on it would be a
                // race between the requests running in parallel.
                IProgress<ReviewProgress>? batchProgress = liveProgress is null
                    ? null
                    : new BatchStampedProgress(liveProgress, index);

                // Marks the batch as sent. Until the first token arrives the
                // display would otherwise show it as still queued, which looks
                // the same as a request stuck waiting on the provider.
                batchProgress?.Report(new ReviewProgress(index, 0, 0, null, IsAnswer: false));

                results[index] = await _reviewService.ReviewPullRequestAsync(
                    pr, batch, batchProgress);
            }
            catch (ReviewFailedException ex)
            {
                // Keep the batches that did work. Losing a few files is a far
                // better outcome than losing the review.
                batchFailures[index] = new BatchFailure([.. batch.Select(f => f.Path)], ex);
            }
            finally
            {
                onBatchCompleted?.Invoke(index, batchFailures[index] is null);

                if (!alone)
                {
                    gate.Release();
                }

                progress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"Reviewing PR #{pr.Id} — {Interlocked.Increment(ref completed)} of {batches.Count} part(s) done..."));
            }
        }

        BatchSchedule schedule = BatchPlanner.Schedule(batches, _settings.WarmPrefixCache);

        // The first request populates the provider's prompt cache with the
        // prefix every other batch repeats; the rest then read it cheaply.
        if (schedule.WarmUpIndex is int warmUp)
        {
            await RunAsync(batches[warmUp], warmUp, alone: true);
        }

        // Started in schedule order. The gate releases waiters first come,
        // first served, so this is also the order in which they get a slot.
        await Task.WhenAll(schedule.Order.Select(i => RunAsync(batches[i], i, alone: false)));

        List<ReviewComment> comments = [.. results.Where(r => r is not null).SelectMany(r => r!)];
        List<BatchFailure> failures = [.. batchFailures.Where(f => f is not null).Select(f => f!)];

        return new PullRequestReviewResult(comments, failures, batches.Count);
    }
}

// Rewrites each update's batch index on its way to the display. Reports
// synchronously so the ordering the model produced is the ordering shown.
internal sealed class BatchStampedProgress(IProgress<ReviewProgress> inner, int batchIndex)
    : IProgress<ReviewProgress>
{
    public void Report(ReviewProgress value) => inner.Report(value with { BatchIndex = batchIndex });
}

// The planned shape of one batch, published before the work starts.
public sealed record BatchInfo(int Index, List<string> FilePaths);

// A batch that could not be reviewed, and the files it covered.
public sealed record BatchFailure(List<string> FilePaths, ReviewFailedException Exception);

public sealed record PullRequestReviewResult(
    List<ReviewComment> Comments,
    List<BatchFailure> Failures,
    int BatchCount)
{
    public bool AnySucceeded => Failures.Count < BatchCount;

    public List<string> UnreviewedFiles => [.. Failures.SelectMany(f => f.FilePaths)];
}

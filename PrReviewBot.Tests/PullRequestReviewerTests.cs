using PrReviewBot.Config;
using PrReviewBot.Models;
using PrReviewBot.Services;
using static PrReviewBot.Tests.TestData;

namespace PrReviewBot.Tests;

public class PullRequestReviewerTests
{
    // Records the order requests start and finish in, how many run at once,
    // and answers each with one comment per file after a per-file delay.
    private sealed class RecordingReviewService : IReviewService
    {
        private readonly object _lock = new();
        private int _inFlight;

        public List<string> Started { get; } = [];
        public List<string> Finished { get; } = [];
        public List<string> Events { get; } = [];
        public int MaxInFlight { get; private set; }
        public Func<IReadOnlyList<ChangedFile>, TimeSpan> Delay { get; init; } = _ => TimeSpan.FromMilliseconds(20);
        public Func<IReadOnlyList<ChangedFile>, bool> Fails { get; init; } = _ => false;

        public async Task<List<ReviewComment>> ReviewPullRequestAsync(
            PullRequestInfo pr, IReadOnlyList<ChangedFile> files,
            IProgress<ReviewProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            string key = string.Join(",", files.Select(f => f.Path));
            lock (_lock)
            {
                Started.Add(key);
                Events.Add("start " + key);
                _inFlight++;
                MaxInFlight = Math.Max(MaxInFlight, _inFlight);
            }

            try
            {
                await Task.Delay(Delay(files), cancellationToken);

                if (Fails(files))
                {
                    throw new ReviewFailedException("boom");
                }

                return [.. files.Select(f => new ReviewComment { FilePath = f.Path })];
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                    Finished.Add(key);
                    Events.Add("end " + key);
                }
            }
        }
    }

    private static ReviewSettings Settings(int parallel = 3, bool warm = true, int filesPerRequest = 1) => new()
    {
        MaxParallelRequests = parallel,
        WarmPrefixCache = warm,
        MaxFilesPerRequest = filesPerRequest,
        MaxDiffCharsPerRequest = 100_000
    };

    [Fact]
    public async Task WarmUpBatchIsTheSmallestAndFinishesBeforeAnyOtherStarts()
    {
        RecordingReviewService service = new();
        PullRequestInfo pr = Pr(File("/a", 50), File("/b", 5), File("/c", 80));

        await new PullRequestReviewer(service, Settings()).ReviewAsync(pr);

        Assert.Equal(["start /b", "end /b"], service.Events.Take(2));
        Assert.Equal(6, service.Events.Count);
    }

    [Fact]
    public async Task RemainingBatchesStartLargestFirst()
    {
        RecordingReviewService service = new();
        PullRequestInfo pr = Pr(File("/a", 50), File("/b", 5), File("/c", 80), File("/d", 20));

        await new PullRequestReviewer(service, Settings(parallel: 1)).ReviewAsync(pr);

        Assert.Equal(["/b", "/c", "/a", "/d"], service.Started);
    }

    [Fact]
    public async Task NeverExceedsParallelLimit()
    {
        RecordingReviewService service = new();
        PullRequestInfo pr = Pr([.. Enumerable.Range(0, 10).Select(i => File($"/{i}"))]);

        await new PullRequestReviewer(service, Settings(parallel: 3, warm: false)).ReviewAsync(pr);

        Assert.Equal(10, service.Started.Count);
        Assert.Equal(3, service.MaxInFlight);
    }

    [Fact]
    public async Task CommentsComeBackInFileOrderWhateverOrderBatchesFinish()
    {
        // The first file is the slowest, so its batch finishes last.
        RecordingReviewService service = new()
        {
            Delay = files => files[0].Path == "/a" ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromMilliseconds(5)
        };
        PullRequestInfo pr = Pr(File("/a", 10), File("/b", 20), File("/c", 30));

        PullRequestReviewResult result = await new PullRequestReviewer(service, Settings(warm: false)).ReviewAsync(pr);

        Assert.Equal("/a", service.Finished[^1]);
        Assert.Equal(["/a", "/b", "/c"], result.Comments.Select(c => c.FilePath));
    }

    [Fact]
    public async Task FailedBatchCostsOnlyItsOwnFiles()
    {
        RecordingReviewService service = new() { Fails = files => files.Any(f => f.Path == "/b") };
        PullRequestInfo pr = Pr(File("/a"), File("/b"), File("/c"));

        PullRequestReviewResult result = await new PullRequestReviewer(service, Settings()).ReviewAsync(pr);

        Assert.True(result.AnySucceeded);
        Assert.Equal(3, result.BatchCount);
        Assert.Equal(["/b"], result.UnreviewedFiles);
        Assert.Equal(["/a", "/c"], result.Comments.Select(c => c.FilePath));
    }

    [Fact]
    public async Task ReportsPlannedBatchesInFileOrderAndEveryCompletion()
    {
        RecordingReviewService service = new();
        PullRequestInfo pr = Pr(File("/a", 50), File("/b", 5), File("/c", 80));
        IReadOnlyList<BatchInfo>? planned = null;
        List<int> completed = [];

        await new PullRequestReviewer(service, Settings()).ReviewAsync(
            pr,
            onBatchesPlanned: b => planned = b,
            onBatchCompleted: (i, _) => { lock (completed) { completed.Add(i); } });

        Assert.NotNull(planned);
        Assert.Equal([["/a"], ["/b"], ["/c"]], planned.Select(b => b.FilePaths.ToArray()));
        Assert.Equal([0, 1, 2], completed.Order());
    }
}

using System.Text.Json;
using PrReviewBot.Config;
using PrReviewBot.Models;
using PrReviewBot.Services;
using static PrReviewBot.Tests.TestData;

namespace PrReviewBot.Tests;

// The review used to hang after the last batch: the streaming loops waited for
// the server to close the connection instead of stopping at the end-of-answer
// marker, and nothing timed out a stream that went quiet.
public class StreamingProviderTests
{
    private const string Answer = """[{"filePath":"/a.cs","lineNumber":1,"severity":"Warning","issue":"x"}]""";

    private static ReviewSettings Streaming(int idleSeconds) => new()
    {
        ShowThinking = true,
        StreamIdleTimeoutSeconds = idleSeconds
    };

    private static string SseBody(bool withDone, bool withFinish = false, bool withUsage = true)
    {
        string Chunk(object delta) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta } } }) + "\n\n";

        return Chunk(new { reasoning_content = "Let me look at a.cs." })
            + Chunk(new { content = Answer[..20] })
            + Chunk(new { content = Answer[20..] })
            + (withFinish ? "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } }) + "\n\n" : "")
            + (withUsage ? "data: " + JsonSerializer.Serialize(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 10, completion_tokens = 5 } }) + "\n\n" : "")
            + (withDone ? "data: [DONE]\n\n" : "");
    }

    private static string OllamaBody(bool withDone)
        => JsonSerializer.Serialize(new { thinking = "Hmm.", response = "", done = false }) + "\n"
           + JsonSerializer.Serialize(new { response = Answer, done = false }) + "\n"
           + (withDone ? JsonSerializer.Serialize(new { response = "", done = true, done_reason = "stop" }) + "\n" : "");

    [Fact]
    public async Task BifrostStopsAtDoneEvenWhenTheConnectionStaysOpen()
    {
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 60),
            new FakeStreamingHandler(SseBody(withDone: true), keepOpen: true));
        PullRequestInfo pr = Pr(File("/a.cs"));

        List<ReviewComment> comments = await service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("/a.cs", Assert.Single(comments).FilePath);
    }

    [Fact]
    public async Task BifrostGivesUpOnAStalledStream()
    {
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 1),
            new FakeStreamingHandler(SseBody(withDone: false), keepOpen: true));
        PullRequestInfo pr = Pr(File("/a.cs"));

        ReviewFailedException ex = await Assert.ThrowsAsync<ReviewFailedException>(
            () => service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("StreamIdleTimeoutSeconds", ex.Message);
    }

    [Fact]
    public async Task BifrostGivesUpWhenOnlyHeartbeatsArrive()
    {
        // What the hung run looked like: the model stopped mid-thought, and
        // the gateway kept the connection busy with keep-alive comments.
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 1),
            new FakeStreamingHandler(
                "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = "Actually yes, ASP.NET Core CORS" } } } }) + "\n\n",
                keepOpen: true, heartbeat: TimeSpan.FromMilliseconds(100)));
        PullRequestInfo pr = Pr(File("/a.cs"));

        ReviewFailedException ex = await Assert.ThrowsAsync<ReviewFailedException>(
            () => service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("connection was alive", ex.Message);
    }

    [Fact]
    public async Task BifrostStopsAfterFinishAndUsageWithoutWaitingForDone()
    {
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 60),
            new FakeStreamingHandler(SseBody(withDone: false, withFinish: true), keepOpen: true,
                heartbeat: TimeSpan.FromMilliseconds(100)));
        PullRequestInfo pr = Pr(File("/a.cs"));

        List<ReviewComment> comments = await service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(comments);
    }

    [Fact]
    public async Task BifrostFinishesAfterGraceWhenNeitherUsageNorDoneArrives()
    {
        // maximhq/bifrost#7108: the upstream omits [DONE] and the gateway
        // holds the finished stream open on heartbeats.
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 60),
            new FakeStreamingHandler(SseBody(withDone: false, withFinish: true, withUsage: false), keepOpen: true,
                heartbeat: TimeSpan.FromMilliseconds(100)))
        {
            FinishGrace = TimeSpan.FromMilliseconds(300)
        };
        PullRequestInfo pr = Pr(File("/a.cs"));

        List<ReviewComment> comments = await service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(comments);
    }

    [Fact]
    public async Task BifrostStillReadsAStreamThatSimplyEnds()
    {
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 60),
            new FakeStreamingHandler(SseBody(withDone: false), keepOpen: false));
        PullRequestInfo pr = Pr(File("/a.cs"));

        Assert.Single(await service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BifrostReportsThinkingAndAnswerProgress()
    {
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 60),
            new FakeStreamingHandler(SseBody(withDone: true), keepOpen: false));
        PullRequestInfo pr = Pr(File("/a.cs"));
        List<ReviewProgress> updates = [];

        await service.ReviewPullRequestAsync(pr, pr.ChangedFiles, new SyncProgress(updates.Add), TestContext.Current.CancellationToken);

        Assert.Contains(updates, u => !u.IsAnswer && u.ReasoningChars > 0);
        Assert.Equal(Answer.Length, updates[^1].AnswerChars);
    }

    [Fact]
    public async Task OllamaStopsAtDoneEvenWhenTheConnectionStaysOpen()
    {
        using OllamaReviewService service = new(
            new OllamaSettings { BaseUrl = "http://ollama.test/api" }, Streaming(idleSeconds: 60),
            new FakeStreamingHandler(OllamaBody(withDone: true), keepOpen: true));
        PullRequestInfo pr = Pr(File("/a.cs"));

        List<ReviewComment> comments = await service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Single(comments);
    }

    [Fact]
    public async Task OllamaGivesUpOnAStalledStream()
    {
        using OllamaReviewService service = new(
            new OllamaSettings { BaseUrl = "http://ollama.test/api" }, Streaming(idleSeconds: 1),
            new FakeStreamingHandler(OllamaBody(withDone: false), keepOpen: true));
        PullRequestInfo pr = Pr(File("/a.cs"));

        await Assert.ThrowsAsync<ReviewFailedException>(
            () => service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StalledBatchFailsAloneAndTheReviewStillFinishes()
    {
        // One service instance for all batches, as in the real run; only the
        // first request's stream stalls.
        StallFirstHandler handler = new(SseBody(withDone: true));
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, Streaming(idleSeconds: 1), handler);
        PullRequestInfo pr = Pr(File("/a.cs"), File("/b.cs"), File("/c.cs"));
        ReviewSettings settings = new() { MaxFilesPerRequest = 1, MaxParallelRequests = 3, WarmPrefixCache = false };

        PullRequestReviewResult result = await new PullRequestReviewer(service, settings).ReviewAsync(pr)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Single(result.Failures);
        Assert.Equal(3, result.BatchCount);
    }

    private sealed class StallFirstHandler(string body) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool stall = Interlocked.Increment(ref _requests) == 1;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(new ScriptedStream(
                    System.Text.Encoding.UTF8.GetBytes(stall ? "" : body), keepOpen: stall))
            });
        }
    }

    private sealed class SyncProgress(Action<ReviewProgress> handler) : IProgress<ReviewProgress>
    {
        public void Report(ReviewProgress value) => handler(value);
    }
}

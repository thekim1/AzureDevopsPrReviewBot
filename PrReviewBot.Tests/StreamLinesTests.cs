using System.Text;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class StreamLinesTests
{
    private static StreamReader Reader(string text, bool keepOpen, TimeSpan? heartbeat = null)
        => new(new ScriptedStream(Encoding.UTF8.GetBytes(text), keepOpen, heartbeat));

    private static async Task<List<string>> ReadAll(StreamReader reader, StreamWatchdog watchdog, bool kickEachLine = false)
    {
        List<string> lines = [];
        await foreach (string line in StreamLines.ReadAsync(reader, watchdog, TestContext.Current.CancellationToken))
        {
            lines.Add(line);
            if (kickEachLine)
            {
                watchdog.Kick();
            }
        }

        return lines;
    }

    [Fact]
    public async Task ReadsEveryLineOfAStreamThatEnds()
    {
        using StreamWatchdog watchdog = new(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "c"], await ReadAll(Reader("a\nb\nc", keepOpen: false), watchdog));
    }

    [Fact]
    public async Task ThrowsReviewFailedWhenTheStreamGoesQuiet()
    {
        using StreamWatchdog watchdog = new(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        ReviewFailedException ex = await Assert.ThrowsAsync<ReviewFailedException>(
            () => ReadAll(Reader("a\n", keepOpen: true), watchdog));

        Assert.Contains("StreamIdleTimeoutSeconds", ex.Message);
    }

    [Fact]
    public async Task HeartbeatsDoNotKeepAStalledStreamAlive()
    {
        // The hang seen through Bifrost: the model stopped, the gateway kept
        // sending keep-alive comments, and a per-line timeout never fired.
        using StreamWatchdog watchdog = new(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        ReviewFailedException ex = await Assert.ThrowsAsync<ReviewFailedException>(
            () => ReadAll(Reader("data: {}\n", keepOpen: true, heartbeat: TimeSpan.FromMilliseconds(50)), watchdog)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("connection was alive", ex.Message);
        Assert.Contains(": keep-alive", ex.RawResponse);
    }

    [Fact]
    public async Task OutputKeepsTheStreamAlivePastTheTimeout()
    {
        // Lines arrive every 50 ms for well over the 300 ms timeout; each one
        // counts as output, so the read must never be abandoned.
        using StreamWatchdog watchdog = new(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(1200));
        int lines = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string _ in StreamLines.ReadAsync(
                Reader("", keepOpen: true, heartbeat: TimeSpan.FromMilliseconds(50)), watchdog, stop.Token))
            {
                lines++;
                watchdog.Kick();
                stop.Token.ThrowIfCancellationRequested();
            }
        });

        Assert.True(lines > 10, $"{lines} lines");
    }

    [Fact]
    public async Task RunningOutOfGraceAfterFinishingEndsTheStreamQuietly()
    {
        using StreamWatchdog watchdog = new(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        watchdog.Finishing(TimeSpan.FromMilliseconds(200));

        List<string> lines = await ReadAll(Reader("last\n", keepOpen: true), watchdog)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["last"], lines);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAStall()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));
        using StreamWatchdog watchdog = new(TimeSpan.FromMinutes(5), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string _ in StreamLines.ReadAsync(Reader("", keepOpen: true), watchdog, cts.Token))
            {
            }
        });
    }
}

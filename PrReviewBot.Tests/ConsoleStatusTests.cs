using System.Diagnostics;
using PrReviewBot.Services;
using Spectre.Console;

namespace PrReviewBot.Tests;

// Counts process threads, so it must not run alongside other tests.
[Collection(nameof(ConsoleCollection))]
public class ConsoleStatusTests
{
    private static IAnsiConsole InteractiveConsole() => AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.Yes,
        Interactive = InteractionSupport.Yes,
        Out = new AnsiConsoleOutput(new StringWriter())
    });

    private static int ThreadCount()
    {
        using Process process = Process.GetCurrentProcess();
        return process.Threads.Count;
    }

    [Fact]
    public async Task InstantSpinnersLeaveNoRefreshThreadsBehind()
    {
        // Measured before the fix: 300 instant spinners straight through
        // Spectre left 178 refresh threads running forever. Any one of them
        // deadlocks the next live display.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        int before = ThreadCount();

        for (int i = 0; i < 20; i++)
        {
            await ConsoleStatus.RunAsync(InteractiveConsole(), "instant", _ => Task.CompletedTask);
        }

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(ThreadCount() <= before + 3, $"{before} threads before, {ThreadCount()} after");
    }

    [Fact]
    public async Task InstantWorkStillKeepsTheSpinnerOpenLongEnoughForItsThreadToStart()
    {
        // Spectre leaves a spinner's refresh thread running forever if the
        // spinner is stopped before that thread starts; next to a live display
        // the orphan deadlocks the console. See ConsoleStatus.
        IAnsiConsole console = InteractiveConsole();
        Stopwatch watch = Stopwatch.StartNew();

        await ConsoleStatus.RunAsync(console, "instant", _ => Task.CompletedTask);

        Assert.True(watch.Elapsed >= ConsoleStatus.MinimumDuration - TimeSpan.FromMilliseconds(20), $"{watch.Elapsed}");
    }

    [Fact]
    public async Task FailingWorkStillWaitsAndRethrows()
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) });
        Stopwatch watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConsoleStatus.RunAsync(console, "failing", _ => throw new InvalidOperationException()));

        Assert.True(watch.Elapsed >= ConsoleStatus.MinimumDuration - TimeSpan.FromMilliseconds(20));
    }
}

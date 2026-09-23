using PrReviewBot.Config;
using PrReviewBot.Models;
using PrReviewBot.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PrReviewBot.Tests;

// The live table scrambled the terminal once a row wrapped or the table grew
// taller than the window, because each repaint moves the cursor up by the
// height it drew last time. These pin down "one line per row, inside the window".
public class LiveReviewDisplayTests
{
    private static string[] Render(IRenderable renderable, int width)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Width = width;
        console.Write(renderable);

        return writer.ToString().TrimEnd('\n', '\r').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static LiveReviewDisplay DisplayWith(int batches, string thought)
    {
        LiveReviewDisplay display = new(new ReviewSettings { ThinkingPreviewChars = 400 });
        display.Plan([.. Enumerable.Range(0, batches).Select(i =>
            new BatchInfo(i, [$"/src/Some/Rather/Deep/Folder/AVeryLongFileNameForTesting{i}.cs"]))]);

        for (int i = 0; i < batches; i++)
        {
            display.Report(new ReviewProgress(i, 40_000, 0, thought, IsAnswer: false));
        }

        return display;
    }

    private const string MessyThought =
        "Looking at\tthe method\r\nGetUser(int id) — [bold]not markup[/] \u001b[31mred\u001b[0m 让我们检查一下这个方法是否正确处理了空值 "
        + "and the rest of a long, long line of reasoning that goes on and on well past any terminal width you might have.";

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(60)]
    public void EveryRowIsOneLineAndFitsTheWidth(int width)
    {
        LiveReviewDisplay display = DisplayWith(batches: 5, MessyThought);

        string[] lines = Render(display.Build(width, height: 50), width);

        Assert.Equal(5 + 4, lines.Length); // rows + top, header, separator, bottom
        Assert.All(lines, l => Assert.True(l.GetCellWidth() <= width, $"{l.GetCellWidth()} > {width}: {l}"));
    }

    [Fact]
    public void TableNeverOutgrowsTheWindow()
    {
        LiveReviewDisplay display = DisplayWith(batches: 30, "thinking");
        for (int i = 0; i < 20; i++)
        {
            display.Complete(i, succeeded: true);
        }

        string[] lines = Render(display.Build(100, height: 20), 100);

        Assert.True(lines.Length < 20, $"{lines.Length} lines");
        Assert.Contains(lines, l => l.Contains("20 done", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("30.", StringComparison.Ordinal) || l.Contains("more not shown", StringComparison.Ordinal));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string RowText(LiveReviewDisplay display) => string.Join("\n", Render(display.Build(140, 40), 140));

    [Fact]
    public void SentRequestWithNoOutputSaysItIsWaiting()
    {
        ManualClock clock = new();
        LiveReviewDisplay display = new(new ReviewSettings(), clock);
        display.Plan([new BatchInfo(0, ["/a.cs"])]);
        display.Report(new ReviewProgress(0, 0, 0, null, IsAnswer: false));

        clock.Now += TimeSpan.FromSeconds(42);

        string text = RowText(display);
        Assert.Contains("waiting for the model to start… 42s", text);
        Assert.Contains("42s waiting", text);
    }

    [Fact]
    public void StalledOutputShowsHowLongItHasBeenQuiet()
    {
        ManualClock clock = new();
        LiveReviewDisplay display = new(new ReviewSettings(), clock);
        display.Plan([new BatchInfo(0, ["/a.cs"])]);
        display.Report(new ReviewProgress(0, 0, 0, null, IsAnswer: false));
        display.Report(new ReviewProgress(0, 1000, 0, "Actually yes, ASP.NET Core CORS", IsAnswer: false));

        clock.Now += TimeSpan.FromSeconds(5);
        Assert.Contains("ASP.NET Core CORS", RowText(display));

        clock.Now += TimeSpan.FromSeconds(120);
        Assert.Contains("no output for 2m 05s", RowText(display));
    }

    [Fact]
    public void FinishedPartShowsHowLongItTook()
    {
        ManualClock clock = new();
        LiveReviewDisplay display = new(new ReviewSettings(), clock);
        display.Plan([new BatchInfo(0, ["/a.cs"]), new BatchInfo(1, ["/b.cs"])]);
        display.Report(new ReviewProgress(0, 0, 0, null, IsAnswer: false));
        display.Report(new ReviewProgress(1, 0, 0, null, IsAnswer: false));
        clock.Now += TimeSpan.FromSeconds(75);
        display.Complete(0, succeeded: true);
        display.Complete(1, succeeded: false);

        string text = RowText(display);

        Assert.Contains("took 1m 15s", text);
        Assert.Contains("failed after 1m 15s", text);
    }

    [Fact]
    public void ClockTickRepaintsARunningPartEvenWithoutNewOutput()
    {
        ManualClock clock = new();
        LiveReviewDisplay display = new(new ReviewSettings(), clock);
        display.Plan([new BatchInfo(0, ["/a.cs"])]);
        display.Report(new ReviewProgress(0, 0, 0, null, IsAnswer: false));

        Assert.True(display.TryRender(out _));
        Assert.False(display.TryRender(out _));

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(display.TryRender(out _));
    }

    [Fact]
    public void ThoughtIsFlattenedToPrintableText()
    {
        string flat = LiveReviewDisplay.SingleLine("a\tb\r\n\u001b[31mc  d");

        Assert.Equal("a b [31mc d", flat);
    }

    [Fact]
    public void FitEndKeepsTheLatestTextAndCountsWideCharactersAsTwoCells()
    {
        string fitted = LiveReviewDisplay.FitEnd("开始思考这个问题", 7);

        Assert.StartsWith("…", fitted);
        Assert.EndsWith("问题", fitted);
        Assert.True(fitted.GetCellWidth() <= 7);
    }

    [Fact]
    public void FitEndLeavesShortTextAlone()
        => Assert.Equal("short", LiveReviewDisplay.FitEnd("short", 20));

    [Fact]
    public void RunningPartShowsPromptSizeAndATickingClock()
    {
        ManualClock clock = new();
        LiveReviewDisplay display = new(new ReviewSettings(), clock);
        display.Plan([new BatchInfo(0, ["/a.cs"])]);
        display.Report(new ReviewProgress(0, 0, 0, null, IsAnswer: false, PromptChars: 48_000));
        clock.Now += TimeSpan.FromSeconds(3);
        display.Report(new ReviewProgress(0, 400, 0, "thinking about it", IsAnswer: false));
        clock.Now += TimeSpan.FromSeconds(2);

        string text = RowText(display);

        Assert.Contains("~12,000 tok", text);
        Assert.Contains("5s thinking about it", text);
    }
}

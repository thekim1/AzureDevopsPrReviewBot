using PrReviewBot.Config;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class DiffBuilderTests
{
    private static readonly ReviewSettings Settings = new() { ContextLines = 2, HunkMergeDistance = 1 };

    private static string Lines(int count, Func<int, string>? line = null)
        => string.Join("\n", Enumerable.Range(1, count).Select(i => line?.Invoke(i) ?? $"line {i}"));

    [Fact]
    public void NumbersAddedLinesInTheNewFileAndRemovedLinesInTheOld()
    {
        string oldText = Lines(10);
        string newText = Lines(10, i => i == 5 ? "changed" : $"line {i}");

        DiffBuilder.DiffResult diff = DiffBuilder.Build(oldText, newText, Settings);

        Assert.True(diff.Success);
        Assert.Contains("-    5 | line 5", diff.Text);
        Assert.Contains("+    5 | changed", diff.Text);
        Assert.Contains("     3 | line 3", diff.Text);
        Assert.DoesNotContain("line 1\n", diff.Text);
        Assert.Contains("@@ ... 2 unchanged line(s) not shown ... @@", diff.Text);
        Assert.Equal(10, diff.NewFileLineCount);
    }

    [Fact]
    public void NewFileIsAllAdditions()
    {
        DiffBuilder.DiffResult diff = DiffBuilder.Build("", "a\nb", Settings);

        Assert.Contains("+    1 | a", diff.Text);
        Assert.Contains("+    2 | b", diff.Text);
    }

    [Fact]
    public void IdenticalContentReportsNoChanges()
        => Assert.Equal("(no textual changes in this file)", DiffBuilder.Build("a\nb", "a\r\nb", Settings).Text);

    [Fact]
    public void RefusesFilesTooLargeToDiff()
    {
        DiffBuilder.DiffResult diff = DiffBuilder.Build(Lines(100), Lines(100), new ReviewSettings { MaxDiffCells = 50 });

        Assert.False(diff.Success);
        Assert.StartsWith("too large to diff", diff.FailureReason);
    }

    [Fact]
    public void MarksTruncationExplicitly()
    {
        DiffBuilder.DiffResult diff = DiffBuilder.Build("", Lines(50), new ReviewSettings { MaxDiffLinesPerFile = 10 });

        Assert.True(diff.IsTruncated);
        Assert.Contains(DiffBuilder.TruncationMarker, diff.Text);
    }

    [Fact]
    public void DetectsBinaryContent()
    {
        Assert.True(DiffBuilder.LooksBinary("PNG\0\0data"));
        Assert.False(DiffBuilder.LooksBinary("plain text"));
    }
}

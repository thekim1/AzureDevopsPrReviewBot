using PrReviewBot.Models;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class IntentContextTests
{
    [Fact]
    public void HtmlBecomesPlainText()
        => Assert.Equal(
            "Users can upload PDFs.\nMax 10 MB & signed",
            IntentContext.HtmlToText("<div>Users can <b>upload</b> PDFs.</div><p></p><ul><li>Max 10&nbsp;MB &amp; signed</li></ul>"));

    [Fact]
    public void EmptyHtmlIsEmpty()
        => Assert.Equal("", IntentContext.HtmlToText(null));

    [Fact]
    public void WorkItemIsCutToItsBudget()
    {
        LinkedWorkItem item = IntentContext.Describe(
            19235, "User Story", "Upload PDFs", new string('d', 5000), new string('a', 5000), maxChars: 400);

        Assert.Equal(19235, item.Id);
        Assert.True(item.Description.Length + item.AcceptanceCriteria.Length <= 400);
        Assert.EndsWith("…", item.Description);
    }

    [Fact]
    public void CommitMessagesDropMergesDuplicatesAndBlanks()
    {
        List<string> kept = IntentContext.UsefulCommitMessages(
            ["Add PDF upload", "Merge branch 'main' into story/19235", "Merged PR 4201: stuff", "", null, "Add PDF upload", "Fix tests\r\n\r\nDetails"],
            maxCommits: 10, maxCharsEach: 300);

        Assert.Equal(["Add PDF upload", "Fix tests\nDetails"], kept);
    }

    [Fact]
    public void CommitMessagesAreCapped()
        => Assert.Equal(3, IntentContext.UsefulCommitMessages(Enumerable.Range(0, 10).Select(i => $"c{i}"), 3, 300).Count);
}

using PrReviewBot.Config;
using PrReviewBot.Models;
using PrReviewBot.Services;
using static PrReviewBot.Tests.TestData;

namespace PrReviewBot.Tests;

public class ReviewValidatorTests
{
    private static PullRequestInfo PrWithDiff(string diff)
    {
        PullRequestInfo pr = Pr(File("/src/A.cs"));
        pr.ChangedFiles[0].Diff = diff;
        return pr;
    }

    private static ReviewComment Finding(string evidence, int line) => new()
    {
        FilePath = "/src/A.cs",
        LineNumber = line,
        Evidence = evidence,
        Issue = "Something",
        Confidence = 4
    };

    private static List<ReviewComment> Validate(PullRequestInfo pr, params ReviewComment[] comments)
        => new ReviewValidator(new ReviewSettings()).Validate(pr, [.. comments]).Kept;

    [Fact]
    public void RepeatedLineAnchorsToTheOccurrenceNearestTheModelsLineNumber()
    {
        // With whole files in the diff the same line often appears several
        // times; the first occurrence used to win however far away it was.
        PullRequestInfo pr = PrWithDiff("""
                 10 |         return result;
                 11 |     }
            +   120 |         return result;
                121 |     }
            """);

        ReviewComment kept = Assert.Single(Validate(pr, Finding("return result;", 118)));

        Assert.Equal(120, kept.LineNumber);
    }

    [Fact]
    public void ExactMatchBeatsALongerLineContainingTheQuote()
    {
        PullRequestInfo pr = PrWithDiff("""
                  5 |     var total = Sum(items); // cached below
            +    40 |     var total = Sum(items);
            """);

        Assert.Equal(40, Assert.Single(Validate(pr, Finding("var total = Sum(items);", 5))).LineNumber);
    }

    [Fact]
    public void ShortLinesDoNotMatchByContainment()
    {
        // "{" is contained in the quote, but must not count as its source.
        PullRequestInfo pr = PrWithDiff("""
                  3 | {
            +    30 |     if (user is null) { return; }
            """);

        Assert.Equal(30, Assert.Single(Validate(pr, Finding("if (user is null) {", 3))).LineNumber);
    }

    [Fact]
    public void FindingOnARemovedLineAnchorsWhereTheRemovalWas()
    {
        PullRequestInfo pr = PrWithDiff("""
                 50 |     Before();
            -    61 |     connection.Dispose();
                 51 |     After();
            +    90 |     Added();
            """);

        Assert.Equal(51, Assert.Single(Validate(pr, Finding("connection.Dispose();", 61))).LineNumber);
    }

    [Fact]
    public void EvidenceNotInTheDiffIsDropped()
    {
        PullRequestInfo pr = PrWithDiff("""
            +     1 |     RealMethodCall();
            """);

        List<ReviewComment> kept = Validate(pr, Finding("RealMethodCall();", 1), Finding("SomethingInvented();", 1));

        Assert.Equal(["RealMethodCall();"], kept.Select(c => c.Evidence));
    }
}

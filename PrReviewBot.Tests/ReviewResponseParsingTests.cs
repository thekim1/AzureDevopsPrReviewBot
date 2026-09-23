using PrReviewBot.Models;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class ReviewResponseParsingTests
{
    private const string OneComment = """[{"filePath":"/a.cs","lineNumber":3,"severity":"Warning","issue":"x"}]""";

    [Fact]
    public void ParsesABareArray()
    {
        ReviewComment comment = Assert.Single(ReviewHelpers.ParseReviewResponse(OneComment));

        Assert.Equal("/a.cs", comment.FilePath);
        Assert.Equal(3, comment.LineNumber);
        Assert.Equal(CommentSeverity.Warning, comment.Severity);
    }

    [Fact]
    public void EmptyArrayMeansNoFindings()
        => Assert.Empty(ReviewHelpers.ParseReviewResponse("[]"));

    [Fact]
    public void ParsesAnObjectWrapper()
        => Assert.Single(ReviewHelpers.ParseReviewResponse($$"""{"comments": {{OneComment}} }"""));

    [Fact]
    public void ParsesACodeFencedAnswer()
        => Assert.Single(ReviewHelpers.ParseReviewResponse($"```json\n{OneComment}\n```"));

    [Fact]
    public void FindsTheAnswerInsideProseWithoutBeingFooledByOtherBrackets()
        => Assert.Single(ReviewHelpers.ParseReviewResponse($"Looking at string[] GetItems() ... here: {OneComment} done."));

    [Fact]
    public void EmptyReplyIsAFailureNotACleanReview()
        => Assert.Throws<ReviewFailedException>(() => ReviewHelpers.ParseReviewResponse("  "));

    [Fact]
    public void CutOffArrayIsAFailure()
    {
        ReviewFailedException ex = Assert.Throws<ReviewFailedException>(
            () => ReviewHelpers.ParseReviewResponse("""[{"filePath":"/a.cs","issue":"x"}, {"filePath":"""));

        Assert.Contains("cut off", ex.Message);
    }

    [Fact]
    public void TrailingCommaAndCommentsAreAccepted()
        => Assert.Equal(2, ReviewHelpers.ParseReviewResponse("""
            [
              // first
              {"filePath":"/a.cs","issue":"x",},
              {"filePath":"/b.cs","issue":"y"},
            ]
            """).Count);

    [Fact]
    public void OneMalformedFindingDoesNotCostTheOthers()
    {
        // An unescaped quote inside the prose of the second finding.
        string answer = """
            {"comments": [
              {"filePath":"/a.cs","lineNumber":1,"issue":"fine"},
              {"filePath":"/b.cs","lineNumber":2,"issue":"Använd "await" här"},
              {"filePath":"/c.cs","lineNumber":3,"issue":"also fine"}
            ]}
            """;

        List<ReviewComment> comments = ReviewHelpers.ParseReviewResponse(answer);

        Assert.Equal(["/a.cs", "/c.cs"], comments.Select(c => c.FilePath));
    }

    [Fact]
    public void SalvageReportsHowManyFindingsWereLost()
    {
        Assert.True(ReviewHelpers.TrySalvageFindings(
            """[{"filePath":"/a.cs","issue":"ok"}, {"filePath": oops}]""", out List<ReviewComment> kept, out int lost));

        Assert.Single(kept);
        Assert.Equal(1, lost);
    }

    [Fact]
    public void SalvageNeverTreatsACutOffAnswerAsComplete()
        => Assert.False(ReviewHelpers.TrySalvageFindings(
            """[{"filePath":"/a.cs","issue":"ok"}, {"filePath":"/b.cs","iss""", out _, out _));
}

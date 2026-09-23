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
}

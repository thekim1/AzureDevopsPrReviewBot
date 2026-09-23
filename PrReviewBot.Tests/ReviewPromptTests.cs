using PrReviewBot.Models;
using PrReviewBot.Services;
using static PrReviewBot.Tests.TestData;

namespace PrReviewBot.Tests;

public class ReviewPromptTests
{
    private static PullRequestInfo RichPr()
    {
        PullRequestInfo pr = Pr(File("/src/A.cs"), File("/src/B.cs"));
        pr.ChangedFiles[0].Diff = "+    1 | class A {}";
        pr.ChangedFiles[1].Diff = "+    1 | class B {}";
        pr.RepoContext = [new RepoContextFile { Path = "/AGENTS.md", Content = "Use tabs." }];
        pr.SkippedFiles = [new SkippedFile { Path = "/logo.png", Reason = "binary content" }];
        pr.ExistingComments =
        [
            new PrComment { Author = "Reviewer", Content = "About A", FilePath = "/src/A.cs", LineNumber = 3 },
            new PrComment { Author = "Reviewer", Content = "About B", FilePath = "src/B.cs" },
            new PrComment { Author = "Reviewer", Content = "Overall looks fine" }
        ];
        return pr;
    }

    [Fact]
    public void SharedPrefixIsIdenticalForEveryBatchOfAPr()
    {
        PullRequestInfo pr = RichPr();

        ReviewPrompt first = ReviewHelpers.BuildReviewPromptParts(pr, [pr.ChangedFiles[0]]);
        ReviewPrompt second = ReviewHelpers.BuildReviewPromptParts(pr, [pr.ChangedFiles[1]]);

        Assert.Equal(first.SharedPrefix, second.SharedPrefix);
        Assert.NotEqual(first.BatchSuffix, second.BatchSuffix);
    }

    [Fact]
    public void SharedPrefixHoldsPrWideMaterialInCacheFriendlyOrder()
    {
        PullRequestInfo pr = RichPr();

        string prefix = ReviewHelpers.BuildReviewPromptParts(pr, [pr.ChangedFiles[0]]).SharedPrefix;

        int context = prefix.IndexOf("Use tabs.", StringComparison.Ordinal);
        int metadata = prefix.IndexOf("Title: Add widgets", StringComparison.Ordinal);
        int skipped = prefix.IndexOf("/logo.png — binary content", StringComparison.Ordinal);

        Assert.True(context >= 0 && metadata > context && skipped > metadata);
        Assert.DoesNotContain("=== FILE:", prefix);
        Assert.DoesNotContain("EXISTING PR COMMENTS", prefix);
    }

    [Fact]
    public void BatchSuffixHoldsOnlyThisBatchsFilesAndComments()
    {
        PullRequestInfo pr = RichPr();

        string suffix = ReviewHelpers.BuildReviewPromptParts(pr, [pr.ChangedFiles[0]]).BatchSuffix;

        Assert.Contains("=== FILE: /src/A.cs (Edit) ===", suffix);
        Assert.Contains("class A {}", suffix);
        Assert.DoesNotContain("/src/B.cs", suffix);
        Assert.Contains("[/src/A.cs:3]: About A", suffix);
        Assert.Contains("[PR-level]: Overall looks fine", suffix);
        Assert.DoesNotContain("About B", suffix);
    }

    [Fact]
    public void CommentPathsMatchWithoutLeadingSlash()
    {
        PullRequestInfo pr = RichPr();

        string suffix = ReviewHelpers.BuildReviewPromptParts(pr, [pr.ChangedFiles[1]]).BatchSuffix;

        Assert.Contains("About B", suffix);
    }

    [Fact]
    public void UnscopedCommentsAreSharedByEveryBatch()
    {
        PullRequestInfo pr = RichPr();

        ReviewPrompt first = ReviewHelpers.BuildReviewPromptParts(pr, [pr.ChangedFiles[0]], scopeCommentsToBatch: false);
        ReviewPrompt second = ReviewHelpers.BuildReviewPromptParts(pr, [pr.ChangedFiles[1]], scopeCommentsToBatch: false);

        Assert.Equal(first.SharedPrefix, second.SharedPrefix);
        Assert.Contains("About A", first.SharedPrefix);
        Assert.Contains("About B", first.SharedPrefix);
        Assert.DoesNotContain("EXISTING PR COMMENTS", first.BatchSuffix);
    }

    [Fact]
    public void FullPromptIsPrefixFollowedBySuffix()
    {
        PullRequestInfo pr = RichPr();
        IReadOnlyList<ChangedFile> batch = [pr.ChangedFiles[0]];

        ReviewPrompt parts = ReviewHelpers.BuildReviewPromptParts(pr, batch);

        Assert.Equal(parts.SharedPrefix + parts.BatchSuffix, ReviewHelpers.BuildReviewPrompt(pr, batch));
    }

    [Fact]
    public void TruncatedFileIsFlagged()
    {
        PullRequestInfo pr = Pr(File("/a.cs"));
        pr.ChangedFiles[0].IsTruncated = true;

        string prompt = ReviewHelpers.BuildReviewPrompt(pr, pr.ChangedFiles);

        Assert.Contains("WARNING: this diff was truncated", prompt);
    }

    [Fact]
    public void WholeFileIsAnnouncedAsComplete()
    {
        PullRequestInfo pr = Pr(File("/a.cs"), File("/b.cs"));
        pr.ChangedFiles[0].IsWholeFile = true;

        string prompt = ReviewHelpers.BuildReviewPrompt(pr, pr.ChangedFiles);

        Assert.Contains("This is the complete file (50 lines)", prompt);
        Assert.Contains("The full file is 50 lines; only the file's opening lines", prompt);
        Assert.Contains("\"This is the complete file\"", ReviewHelpers.SystemPrompt);
    }

    [Fact]
    public void SkippedFilesAreCappedPerReason()
    {
        PullRequestInfo pr = Pr(File("/a.cs"));
        pr.SkippedFiles =
        [
            .. Enumerable.Range(0, 300).Select(i => new SkippedFile { Path = $"/img/{i}.png", Reason = "binary or media file" }),
            new SkippedFile { Path = "/package-lock.json", Reason = "lockfile" }
        ];

        string prefix = ReviewHelpers.BuildReviewPromptParts(pr, pr.ChangedFiles).SharedPrefix;

        Assert.Equal(ReviewHelpers.MaxSkippedFilesListedPerReason, prefix.Split('\n').Count(l => l.StartsWith("/img/", StringComparison.Ordinal)));
        Assert.Contains("... and 290 more — binary or media file", prefix);
        Assert.Contains("/package-lock.json — lockfile", prefix);
    }
}

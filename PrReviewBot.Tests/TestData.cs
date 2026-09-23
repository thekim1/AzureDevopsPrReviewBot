using PrReviewBot.Models;

namespace PrReviewBot.Tests;

internal static class TestData
{
    public static ChangedFile File(string path, int diffChars = 10)
        => new() { Path = path, ChangeType = "Edit", Diff = new string('x', diffChars), NewFileLineCount = 50 };

    public static PullRequestInfo Pr(params ChangedFile[] files) => new()
    {
        Id = 42,
        Title = "Add widgets",
        Description = "Adds the widget endpoint.",
        Author = "Someone",
        SourceBranch = "feature/widgets",
        TargetBranch = "main",
        RepositoryName = "Widgets",
        RepositoryId = "repo-1",
        ChangedFiles = [.. files]
    };
}

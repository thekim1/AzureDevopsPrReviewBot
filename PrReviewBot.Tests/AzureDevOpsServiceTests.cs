using Microsoft.TeamFoundation.SourceControl.WebApi;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class AzureDevOpsServiceTests
{
    private static GitPullRequestChange Change(
        string path, VersionControlChangeType type, string? originalPath = null, string? sourceServerItem = null)
        => new()
        {
            Item = new GitItem { Path = path },
            ChangeType = type,
            OriginalPath = originalPath,
            SourceServerItem = sourceServerItem
        };

    [Fact]
    public void EditIsDiffedAgainstItsOwnPath()
        => Assert.Equal("/src/A.cs", AzureDevOpsService.BasePathFor(Change("/src/A.cs", VersionControlChangeType.Edit)));

    [Fact]
    public void RenameIsDiffedAgainstItsOriginalPath()
        => Assert.Equal("/src/Old.cs", AzureDevOpsService.BasePathFor(
            Change("/src/New.cs", VersionControlChangeType.Rename | VersionControlChangeType.Edit, originalPath: "/src/Old.cs")));

    [Fact]
    public void RenameFallsBackToSourceServerItem()
        => Assert.Equal("/src/Old.cs", AzureDevOpsService.BasePathFor(
            Change("/src/New.cs", VersionControlChangeType.Rename, sourceServerItem: "/src/Old.cs")));

    [Fact]
    public void RenameWithoutAnOldPathUsesTheNewPath()
        => Assert.Equal("/src/New.cs", AzureDevOpsService.BasePathFor(
            Change("/src/New.cs", VersionControlChangeType.Rename)));

    [Fact]
    public void OriginalPathIsIgnoredWhenNotARename()
        => Assert.Equal("/src/A.cs", AzureDevOpsService.BasePathFor(
            Change("/src/A.cs", VersionControlChangeType.Edit, originalPath: "/elsewhere.cs")));
}

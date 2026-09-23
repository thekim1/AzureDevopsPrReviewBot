using PrReviewBot.Config;
using PrReviewBot.Models;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class RepoContextPlannerTests
{
    private static readonly List<RepoContextGroup> Groups =
    [
        new("agent", ["/AGENTS.md", "/CLAUDE.md", "/.github/copilot-instructions.md"]),
        new("overview", ["/README.md"]),
        new("arch", ["/docs/architecture.md"])
    ];

    [Fact]
    public void ListsEachCandidateFolderOnce()
        => Assert.Equal(["/", "/.github", "/docs"], RepoContextPlanner.DirectoriesToList(Groups));

    [Fact]
    public void ResolvesOnlyExistingCandidatesInPreferenceOrder()
    {
        string[] existing = ["/CLAUDE.md", "/.github/copilot-instructions.md", "/src/Program.cs"];

        List<List<string>> resolved = RepoContextPlanner.ResolveCandidates(Groups, existing);

        Assert.Equal(["/CLAUDE.md", "/.github/copilot-instructions.md"], resolved[0]);
        Assert.Empty(resolved[1]);
        Assert.Empty(resolved[2]);
    }

    [Fact]
    public void MatchesCaseInsensitivelyButUsesTheRepositorySpelling()
    {
        List<List<string>> resolved = RepoContextPlanner.ResolveCandidates(Groups, ["/readme.md"]);

        Assert.Equal(["/readme.md"], resolved[1]);
    }

    [Fact]
    public void PrefersAnExactCaseMatch()
    {
        List<List<string>> resolved = RepoContextPlanner.ResolveCandidates(Groups, ["/readme.md", "/README.md"]);

        Assert.Equal(["/README.md"], resolved[1]);
    }

    [Fact]
    public void AssembleTakesFirstNonEmptyFilePerGroup()
    {
        List<RepoContextFile> result = RepoContextPlanner.Assemble(
            [
                [("/AGENTS.md", "   "), ("/CLAUDE.md", "real rules"), ("/.github/copilot-instructions.md", "other")],
                [("/README.md", null)],
                [("/docs/architecture.md", "layers")]
            ],
            maxTotalChars: 1000, maxFileChars: 1000);

        Assert.Equal(["/CLAUDE.md", "/docs/architecture.md"], result.Select(f => f.Path));
        Assert.All(result, f => Assert.False(f.IsTruncated));
    }

    [Fact]
    public void AssembleTruncatesToPerFileAndTotalBudgets()
    {
        List<RepoContextFile> result = RepoContextPlanner.Assemble(
            [
                [("/a", new string('a', 50))],
                [("/b", new string('b', 50))],
                [("/c", new string('c', 50))]
            ],
            maxTotalChars: 70, maxFileChars: 40);

        Assert.Equal(["/a", "/b"], result.Select(f => f.Path));
        Assert.Equal(40, result[0].Content.Length);
        Assert.Equal(30, result[1].Content.Length);
        Assert.True(result[0].IsTruncated);
        Assert.True(result[1].IsTruncated);
    }
}

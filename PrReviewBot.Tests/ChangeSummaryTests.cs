using PrReviewBot.Models;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class ChangeSummaryTests
{
    [Fact]
    public void ListsAddedAndRemovedPublicDeclarations()
    {
        string diff = """
            -   10 |     public Task<User> GetUser(int id)
            +   10 |     public Task<User?> GetUser(int id, CancellationToken ct)
                11 |     {
            +   12 |         var x = 1;
            +   20 |     private void Helper() { }
            +   30 |     public int Count => _items.Count;
            """;

        Assert.Equal(
            ["- public Task<User> GetUser(int id)", "+ public Task<User?> GetUser(int id, CancellationToken ct)", "+ public int Count => _items.Count"],
            ChangeSummary.ChangedDeclarations(diff));
    }

    [Fact]
    public void MovedOrReindentedDeclarationIsNotAChange()
    {
        string diff = """
            -    5 | public class Worker {
            +    9 |     public class Worker {
            """;

        Assert.Empty(ChangeSummary.ChangedDeclarations(diff));
    }

    [Theory]
    [InlineData("export const compressImageFile = async (file: File) => {", true)]
    [InlineData("export interface ICachedFile {", true)]
    [InlineData("const props = defineProps<{ file: File }>();", true)]
    [InlineData("[HttpGet(\"/users\")] public IResult List()", true)]
    [InlineData("internal sealed record BatchInfo(int Index);", true)]
    [InlineData("private readonly int _x;", false)]
    [InlineData("const local = 1;", false)]
    [InlineData("// public comment", false)]
    public void RecognisesDeclarations(string line, bool expected)
        => Assert.Equal(expected, ChangeSummary.LooksLikeDeclaration(line));

    [Fact]
    public void EveryFileIsListedEvenWithoutDeclarationChanges()
    {
        ChangedFile a = new() { Path = "/a.cs", ChangeType = "Edit", Diff = "+    1 |     public void M() { }" };
        ChangedFile b = new() { Path = "/b.ts", ChangeType = "Add", Diff = "+    1 | const x = 1;" };

        string summary = ChangeSummary.Build([a, b], 4000);

        Assert.Contains("/a.cs (Edit)\n  + public void M()", summary.Replace("\r", "", StringComparison.Ordinal));
        Assert.Contains("/b.ts (Add) — no public declarations changed", summary);
    }

    [Fact]
    public void SummaryStaysWithinItsBudget()
    {
        ChangedFile[] files = [.. Enumerable.Range(0, 100).Select(i => new ChangedFile { Path = $"/src/File{i}.cs", ChangeType = "Edit", Diff = "" })];

        string summary = ChangeSummary.Build(files, 500);

        Assert.True(summary.Length < 600);
        Assert.Contains("summary cut", summary);
    }
}

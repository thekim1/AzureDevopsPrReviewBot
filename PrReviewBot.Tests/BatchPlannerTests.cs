using PrReviewBot.Models;
using PrReviewBot.Services;
using static PrReviewBot.Tests.TestData;

namespace PrReviewBot.Tests;

public class BatchPlannerTests
{
    [Fact]
    public void Split_RespectsFileCountLimit()
    {
        ChangedFile[] files = [File("/a"), File("/b"), File("/c"), File("/d"), File("/e")];

        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(files, maxFilesPerBatch: 2, maxDiffCharsPerBatch: 1000);

        Assert.Equal([2, 2, 1], batches.Select(b => b.Count));
        Assert.Equal(["/a", "/b", "/c", "/d", "/e"], batches.SelectMany(b => b).Select(f => f.Path));
    }

    [Fact]
    public void Split_RespectsCharacterLimit()
    {
        ChangedFile[] files = [File("/a", 60), File("/b", 60), File("/c", 30)];

        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(files, maxFilesPerBatch: 10, maxDiffCharsPerBatch: 100);

        Assert.Equal([["/a"], ["/b", "/c"]], batches.Select(b => b.Select(f => f.Path).ToArray()));
    }

    [Fact]
    public void Split_OversizedFileGetsItsOwnBatchInsteadOfBeingDropped()
    {
        ChangedFile[] files = [File("/small", 10), File("/huge", 5000), File("/after", 10)];

        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(files, maxFilesPerBatch: 10, maxDiffCharsPerBatch: 100);

        Assert.Equal([["/small"], ["/huge"], ["/after"]], batches.Select(b => b.Select(f => f.Path).ToArray()));
    }

    [Fact]
    public void Split_NoFilesGivesNoBatches()
        => Assert.Empty(BatchPlanner.Split([], 4, 1000));

    [Fact]
    public void Schedule_WarmsWithSmallestBatchAndRunsTheRestLargestFirst()
    {
        List<IReadOnlyList<ChangedFile>> batches =
        [
            [File("/medium", 50)],
            [File("/large", 90)],
            [File("/small", 5)],
            [File("/tiny-but-later", 20)]
        ];

        BatchSchedule schedule = BatchPlanner.Schedule(batches, warmPrefixCache: true);

        Assert.Equal(2, schedule.WarmUpIndex);
        Assert.Equal([1, 0, 3], schedule.Order);
    }

    [Fact]
    public void Schedule_WithoutWarmUpRunsEverythingLargestFirst()
    {
        List<IReadOnlyList<ChangedFile>> batches = [[File("/a", 10)], [File("/b", 30)], [File("/c", 20)]];

        BatchSchedule schedule = BatchPlanner.Schedule(batches, warmPrefixCache: false);

        Assert.Null(schedule.WarmUpIndex);
        Assert.Equal([1, 2, 0], schedule.Order);
    }

    [Fact]
    public void Schedule_SingleBatchIsNeverWarmedSeparately()
    {
        BatchSchedule schedule = BatchPlanner.Schedule([[File("/a")]], warmPrefixCache: true);

        Assert.Null(schedule.WarmUpIndex);
        Assert.Equal([0], schedule.Order);
    }

    [Fact]
    public void Schedule_TiesKeepFileOrder()
    {
        List<IReadOnlyList<ChangedFile>> batches = [[File("/a", 10)], [File("/b", 10)], [File("/c", 10)]];

        BatchSchedule schedule = BatchPlanner.Schedule(batches, warmPrefixCache: true);

        Assert.Equal(0, schedule.WarmUpIndex);
        Assert.Equal([1, 2], schedule.Order);
    }

    [Fact]
    public void Schedule_CoversEveryBatchExactlyOnce()
    {
        List<IReadOnlyList<ChangedFile>> batches = [.. Enumerable.Range(0, 7).Select(i => (IReadOnlyList<ChangedFile>)[File($"/{i}", (i * 37) % 11)])];

        BatchSchedule schedule = BatchPlanner.Schedule(batches, warmPrefixCache: true);

        List<int> all = [.. schedule.Order, schedule.WarmUpIndex!.Value];
        Assert.Equal(Enumerable.Range(0, 7), all.Order());
    }

    [Theory]
    [InlineData("/src/Api/UserService.cs", "userservice")]
    [InlineData("/src/Api/IUserService.cs", "userservice")]
    [InlineData("/tests/Api.Tests/UserServiceTests.cs", "userservice")]
    [InlineData("/src/web/imageUtils.spec.ts", "imageutils")]
    [InlineData("/src/Api/appsettings.Development.json", "appsettings")]
    [InlineData("/src/Api/Invoice.cs", "invoice")]
    [InlineData("/src/web/a/index.ts", "/src/web/a/index")]
    public void RelationKeyTiesRelatedFilesTogether(string path, string expected)
        => Assert.Equal(expected, BatchPlanner.RelationKey(path));

    [Fact]
    public void ClassInterfaceAndTestsLandInTheSameBatch()
    {
        ChangedFile[] files =
        [
            File("/src/Api/UserService.cs"),
            File("/src/Api/OrderService.cs"),
            File("/src/Web/Page.vue"),
            File("/src/Api/Contracts/IUserService.cs"),
            File("/tests/Api.Tests/UserServiceTests.cs")
        ];

        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(files, maxFilesPerBatch: 3, maxDiffCharsPerBatch: 10_000);

        IReadOnlyList<ChangedFile> userBatch = Assert.Single(batches, b => b.Any(f => f.Path == "/src/Api/UserService.cs"));
        Assert.Contains(userBatch, f => f.Path == "/src/Api/Contracts/IUserService.cs");
        Assert.Contains(userBatch, f => f.Path == "/tests/Api.Tests/UserServiceTests.cs");
        Assert.Equal(5, batches.Sum(b => b.Count));
    }

    [Fact]
    public void ClusterThatDoesNotFitStartsAFreshBatchInsteadOfBeingSplit()
    {
        ChangedFile[] files =
        [
            File("/src/A.cs"),
            File("/src/B.cs"),
            File("/src/C.cs"),
            File("/src/ICustomer.cs"),
            File("/src/Customer.cs")
        ];

        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(files, maxFilesPerBatch: 4, maxDiffCharsPerBatch: 10_000);

        Assert.Contains(batches, b => b.Select(f => f.Path).Order().SequenceEqual(["/src/Customer.cs", "/src/ICustomer.cs"]));
    }

    [Fact]
    public void ClusterLargerThanABatchIsSplitRatherThanDropped()
    {
        ChangedFile[] files = [File("/src/Foo.cs"), File("/src/IFoo.cs"), File("/tests/FooTests.cs"), File("/src/Foo.razor")];

        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(files, maxFilesPerBatch: 2, maxDiffCharsPerBatch: 10_000);

        Assert.Equal([2, 2], batches.Select(b => b.Count));
    }

    [Fact]
    public void FilesInTheSameFolderStayAdjacent()
    {
        ChangedFile[] files = [File("/web/A.vue"), File("/api/X.cs"), File("/web/B.vue"), File("/api/Y.cs")];

        List<IReadOnlyList<ChangedFile>> batches = BatchPlanner.Split(files, maxFilesPerBatch: 2, maxDiffCharsPerBatch: 10_000);

        Assert.Equal([["/api/X.cs", "/api/Y.cs"], ["/web/A.vue", "/web/B.vue"]], batches.Select(b => b.Select(f => f.Path).ToArray()));
    }

    [Fact]
    public void GenericNamesInDifferentFoldersAreNotGrouped()
    {
        ChangedFile[] files = [File("/a/index.ts"), File("/b/other.ts"), File("/c/index.ts")];

        Assert.Equal(3, BatchPlanner.Cluster(files).Count);
    }
}

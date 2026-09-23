using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class FileClassifierTests
{
    [Theory]
    [InlineData("/src/web/package-lock.json")]
    [InlineData("/yarn.lock")]
    [InlineData("/src/Api/packages.lock.json")]
    [InlineData("/src/Forms/Form1.Designer.cs")]
    [InlineData("/src/Data/Migrations/AppDbContextModelSnapshot.cs")]
    [InlineData("/src/Data/Migrations/20260101_Init.Designer.cs")]
    [InlineData("/wwwroot/js/site.min.js")]
    [InlineData("/wwwroot/lib/bootstrap/bootstrap.js")]
    [InlineData("/web/node_modules/x/index.js")]
    [InlineData("/assets/logo.png")]
    [InlineData("/docs/manual.pdf")]
    [InlineData("/src/Api/obj/project.assets.json")]
    public void KnownNoiseIsExcluded(string path)
        => Assert.NotNull(FileClassifier.ExclusionReason(path, []));

    [Theory]
    [InlineData("/src/Api/Program.cs")]
    [InlineData("/db/001_create.sql")]
    [InlineData("/build/deploy.ps1")]
    [InlineData("/Directory.Build.props")]
    [InlineData("/src/Web/Views/Home/Index.cshtml")]
    [InlineData("/infra/main.bicep")]
    [InlineData("/Dockerfile")]
    [InlineData("/src/web/package.json")]
    [InlineData("/src/Data/Migrations/20260101_Init.cs")]
    [InlineData("/packages/app/src/index.ts")]
    [InlineData("/src/web/src/components/Modal.vue")]
    public void RealCodeIsReviewed(string path)
        => Assert.Null(FileClassifier.ExclusionReason(path, []));

    [Theory]
    [InlineData("**/Generated/**", "/src/Api/Generated/Client.cs", true)]
    [InlineData("**/Generated/**", "/src/Api/Client.cs", false)]
    [InlineData("/src/api-client/**", "/src/api-client/deep/x.ts", true)]
    [InlineData("/src/api-client/**", "/other/src/api-client/x.ts", false)]
    [InlineData("*.sql", "/db/deep/a.sql", true)]
    [InlineData("*.sql", "/db/a.sqlx", false)]
    [InlineData("src/*.cs", "/src/A.cs", true)]
    [InlineData("src/*.cs", "/src/sub/A.cs", false)]
    [InlineData("  ", "/anything", false)]
    public void GlobsMatchLikeGitignore(string glob, string path, bool expected)
        => Assert.Equal(expected, FileClassifier.GlobMatches(glob, path));

    [Fact]
    public void ConfiguredPatternsExclude()
        => Assert.Equal("excluded by Review:ExcludedPaths",
            FileClassifier.ExclusionReason("/src/Api/Generated/Client.cs", ["**/Generated/**"]));

    [Theory]
    [InlineData("/src/Api/UserService.cs", (int)FilePriority.Source)]
    [InlineData("/src/web/src/utils/imageUtils.ts", (int)FilePriority.Source)]
    [InlineData("/src/Api/appsettings.json", (int)FilePriority.Configuration)]
    [InlineData("/src/Api/Api.csproj", (int)FilePriority.Configuration)]
    [InlineData("/Dockerfile", (int)FilePriority.Configuration)]
    [InlineData("/tests/Api.Tests/UserServiceTests.cs", (int)FilePriority.Test)]
    [InlineData("/src/web/src/utils/imageUtils.spec.ts", (int)FilePriority.Test)]
    [InlineData("/src/web/src/App.test.tsx", (int)FilePriority.Test)]
    [InlineData("/src/web/src/styles/site.scss", (int)FilePriority.Style)]
    [InlineData("/README.md", (int)FilePriority.Documentation)]
    [InlineData("/src/Api/Manifest.cs", (int)FilePriority.Source)]
    public void FilesArePrioritised(string path, int expected)
        => Assert.Equal((FilePriority)expected, FileClassifier.Priority(path));

    [Fact]
    public void RankingPutsSourceFirstAndKeepsOrderWithinAPriority()
    {
        string[] paths = ["/README.md", "/tests/ATests.cs", "/src/B.cs", "/appsettings.json", "/src/A.cs"];

        Assert.Equal(
            ["/src/B.cs", "/src/A.cs", "/appsettings.json", "/tests/ATests.cs", "/README.md"],
            FileClassifier.RankByPriority(paths, p => p));
    }
}

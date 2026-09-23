using PrReviewBot.Models;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class DefinitionIndexTests
{
    [Fact]
    public void CollectsTypeNamesWeightingChangedLinesAndIgnoringStrings()
    {
        string diff = """
                 3 | using Demo.Contracts;
            +   10 |     var user = await _users.Find(new UserQuery(id));
                11 |     UserQuery again = null;
            +   12 |     throw new Exception("User Not Found");
            """;

        (Dictionary<string, int> names, _) = DefinitionIndex.References(diff);

        Assert.Equal(4, names["UserQuery"]);
        Assert.False(names.ContainsKey("User"));
        Assert.False(names.ContainsKey("Exception"));
    }

    [Fact]
    public void CollectsImportSpecifiers()
    {
        string diff = """
                 1 | import { ref } from 'vue';
            +    2 | import { compressImageFile } from '@/utils/imageUtils';
                 3 | import BaseModal from "../base/BaseModal.vue";
            +    4 | import './styles.css';
            """;

        (_, List<string> imports) = DefinitionIndex.References(diff);

        Assert.Equal(["vue", "@/utils/imageUtils", "../base/BaseModal.vue", "./styles.css"], imports);
    }

    private static readonly string[] Repo =
    [
        "/src/Api/Services/UserService.cs",
        "/src/Api/Contracts/IUserService.cs",
        "/src/Worker/Contracts/IUserService.cs",
        "/web/src/utils/imageUtils.ts",
        "/web/src/components/base/BaseModal.vue",
        "/web/src/components/forms/index.ts"
    ];

    [Fact]
    public void CSharpTypeResolvesToTheNearestFileByThatName()
    {
        ILookup<string, string> byName = Repo.ToLookup(p => p[(p.LastIndexOf('/') + 1)..]);

        Assert.Equal("/src/Api/Contracts/IUserService.cs",
            DefinitionIndex.ResolveCSharpType("IUserService", "/src/Api/Endpoints/Users.cs", byName));
        Assert.Equal("/src/Worker/Contracts/IUserService.cs",
            DefinitionIndex.ResolveCSharpType("IUserService", "/src/Worker/Jobs/Sync.cs", byName));
        Assert.Null(DefinitionIndex.ResolveCSharpType("Missing", "/src/Api/X.cs", byName));
    }

    [Theory]
    [InlineData("@/utils/imageUtils", "/web/src/components/Upload.vue", "/web/src/utils/imageUtils.ts")]
    [InlineData("../base/BaseModal.vue", "/web/src/components/forms/Form.vue", "/web/src/components/base/BaseModal.vue")]
    [InlineData("./forms", "/web/src/components/Page.vue", "/web/src/components/forms/index.ts")]
    [InlineData("vue", "/web/src/components/Page.vue", null)]
    [InlineData("@vueuse/core", "/web/src/components/Page.vue", null)]
    [InlineData("./missing", "/web/src/components/Page.vue", null)]
    public void ImportsResolveToRepositoryFiles(string spec, string from, string? expected)
        => Assert.Equal(expected, DefinitionIndex.ResolveImport(spec, from, new HashSet<string>(Repo)));

    [Fact]
    public void CSharpOutlineKeepsSignaturesAndDropsBodies()
    {
        string source = """
            namespace Demo;

            /// <summary>Users.</summary>
            [Service]
            public sealed class UserService(IUserRepository repository) : IUserService
            {
                private readonly int _secret = 42;

                public string Name { get; init; } = "";

                public async Task<User?> FindAsync(
                    int id,
                    CancellationToken ct)
                {
                    var hidden = await repository.Get(id);
                    return hidden;
                }

                private void Helper() { }
            }
            """;

        string outline = DefinitionIndex.Outline("/UserService.cs", source, 2000);

        Assert.Contains("public sealed class UserService(IUserRepository repository) : IUserService", outline);
        Assert.Contains("public string Name { get; init; } = \"\";", outline);
        Assert.Contains("public async Task<User?> FindAsync(", outline);
        Assert.Contains("CancellationToken ct)", outline);
        Assert.DoesNotContain("hidden", outline);
        Assert.DoesNotContain("_secret", outline);
        Assert.DoesNotContain("Helper", outline);
        Assert.DoesNotContain("[Service]", outline);
    }

    [Fact]
    public void InterfaceAndEnumMembersAreKeptWithoutModifiers()
    {
        string source = """
            public interface IUserService
            {
                Task<User?> FindAsync(int id);
                string Name { get; }
            }

            public enum Role
            {
                Reader,
                Admin
            }
            """;

        string outline = DefinitionIndex.Outline("/IUserService.cs", source, 2000);

        Assert.Contains("Task<User?> FindAsync(int id);", outline);
        Assert.Contains("string Name { get; }", outline);
        Assert.Contains("Admin", outline);
    }

    [Fact]
    public void TypeScriptOutlineKeepsExportsAndShapes()
    {
        string source = """
            import { fromBlob } from 'image-resize-compress';

            export interface ICachedFile {
              blobUrl: string;
              contentType?: string;
            }

            const internalHelper = () => 1;

            export const compressImageFile = async (file: File, max = 1500): Promise<File> => {
              const secret = 1;
              return file;
            };
            """;

        string outline = DefinitionIndex.Outline("/imageUtils.ts", source, 2000);

        Assert.Contains("export interface ICachedFile {", outline);
        Assert.Contains("contentType?: string;", outline);
        Assert.Contains("export const compressImageFile = async (file: File, max = 1500): Promise<File>", outline);
        Assert.DoesNotContain("secret", outline);
        Assert.DoesNotContain("internalHelper", outline);
    }

    [Fact]
    public void VueOutlineKeepsProps()
    {
        string source = """
            <template><div /></template>
            <script setup lang="ts">
            const props = defineProps<{
              file: File;
              title?: string;
            }>();
            const hidden = 1;
            </script>
            """;

        string outline = DefinitionIndex.Outline("/BaseModal.vue", source, 2000);

        Assert.Contains("title?: string;", outline);
        Assert.DoesNotContain("hidden", outline);
    }

    [Fact]
    public void OutlineRespectsItsBudget()
    {
        string source = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"    public int P{i} {{ get; set; }}"));

        Assert.True(DefinitionIndex.Outline("/Big.cs", source, 300).Length < 360);
    }

    [Fact]
    public void CandidatesExcludePrFilesAndRankByUse()
    {
        ChangedFile endpoint = new()
        {
            Path = "/src/Api/Endpoints/Users.cs",
            Diff = """
                +   10 |     IUserService service = GetService();
                +   11 |     UserService concrete = new UserService();
                """
        };
        ChangedFile page = new()
        {
            Path = "/web/src/components/Upload.vue",
            Diff = "+    2 | import { compressImageFile } from '@/utils/imageUtils';"
        };

        List<DefinitionCandidate> candidates = DefinitionIndex.SelectCandidates(
            [endpoint, page], Repo, new HashSet<string> { "/src/Api/Services/UserService.cs" }, max: 10);

        Assert.Equal(["/src/Api/Contracts/IUserService.cs", "/web/src/utils/imageUtils.ts"], candidates.Select(c => c.Path));
        Assert.Equal(["/web/src/components/Upload.vue"], candidates[1].ReferencedFrom);
    }
}

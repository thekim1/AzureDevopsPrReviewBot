using System.Globalization;
using System.Text;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

public class AzureDevOpsService
{
    private const string RefsHeadsPrefix = "refs/heads/";

    private readonly AzureDevOpsSettings _settings;
    private readonly VssConnection _connection;

    public AzureDevOpsService(AzureDevOpsSettings settings)
    {
        _settings = settings;
        VssBasicCredential credentials = new(string.Empty, settings.PersonalAccessToken);
        _connection = new VssConnection(new Uri(settings.OrganizationUrl), credentials);
    }

    public async Task<List<PullRequestInfo>> GetAssignedPullRequestsAsync()
        => [.. (await GetAllActivePullRequestsAsync()).Where(pr => pr.IsAssignedToMe)];

    // Fetches all active, non-draft PRs across every enabled repository in the
    // project and marks which ones are assigned to the authenticated user
    // (IsAssignedToMe). Callers can partition the result into "assigned to me"
    // and "other" groups.
    public async Task<List<PullRequestInfo>> GetAllActivePullRequestsAsync()
    {
        GitHttpClient gitClient = await _connection.GetClientAsync<GitHttpClient>();
        List<GitRepository> repos = await gitClient.GetRepositoriesAsync(_settings.Project);

        // Filter out disabled repositories — they are returned by GetRepositoriesAsync
        // but throw TF401019 when used in subsequent API calls like GetPullRequestsAsync
        repos = [.. repos.Where(r => r.IsDisabled != true)];

        List<PullRequestInfo> result = [];

        Guid reviewerId = await GetCurrentUserIdAsync();

        foreach (GitRepository? repo in repos)
        {
            GitPullRequestSearchCriteria searchCriteria = new()
            {
                Status = PullRequestStatus.Active
            };

            // Fix CS1744: remove duplicate positional+named project args
            List<GitPullRequest> prs = await gitClient.GetPullRequestsAsync(
                _settings.Project, repo.Id, searchCriteria);

            foreach (GitPullRequest? pr in prs)
            {
                if (pr.IsDraft == true)
                {
                    continue;
                }

                bool isAssignedToMe = pr.Reviewers is not null
                    && pr.Reviewers.Any(r => Guid.TryParse(r.Id, out Guid id) && id == reviewerId);
                result.Add(new PullRequestInfo
                {
                    Id = pr.PullRequestId,
                    Title = pr.Title,
                    Description = pr.Description ?? "",
                    Author = pr.CreatedBy.DisplayName,
                    SourceBranch = pr.SourceRefName.Replace(RefsHeadsPrefix, ""),
                    TargetBranch = pr.TargetRefName.Replace(RefsHeadsPrefix, ""),
                    RepositoryName = repo.Name,
                    RepositoryId = repo.Id.ToString(),
                    // Diffs are not fetched here — the listing only needs metadata.
                    // Call LoadPrChangesAsync for selected PRs before reviewing.
                    ChangedFiles = [],
                    Url = $"{_settings.OrganizationUrl}/{_settings.Project}/_git/{repo.Name}/pullrequest/{pr.PullRequestId}",
                    IsAssignedToMe = isAssignedToMe
                });
            }
        }

        return result;
    }

    // Lazily fetch the diff for a single selected PR and populate its
    // ChangedFiles. Call this only for PRs the user chose to review.
    public async Task LoadPrChangesAsync(PullRequestInfo pr)
    {
        GitHttpClient gitClient = await _connection.GetClientAsync<GitHttpClient>();
        pr.ChangedFiles = await GetPrChangesAsync(
            gitClient, pr.RepositoryId, pr.Id,
            RefsHeadsPrefix + pr.SourceBranch, RefsHeadsPrefix + pr.TargetBranch);
    }

    private async Task<List<ChangedFile>> GetPrChangesAsync(
        GitHttpClient gitClient, string repoId, int prId,
        string sourceRefName, string targetRefName)
    {
        List<ChangedFile> result = [];
        try
        {
            List<GitPullRequestIteration> iterations = await gitClient.GetPullRequestIterationsAsync(
                _settings.Project, repoId, prId);

            if (iterations.Count == 0)
            {
                return result;
            }

            GitPullRequestIteration latestIteration = iterations.OrderByDescending(i => i.Id).First();

            GitPullRequestIterationChanges changes = await gitClient.GetPullRequestIterationChangesAsync(
                _settings.Project, repoId, prId, latestIteration.Id!.Value);

            foreach (GitPullRequestChange? change in changes.ChangeEntries.Take(20))
            {
                string? filePath = change.Item?.Path;
                if (string.IsNullOrEmpty(filePath) || !IsCodeFile(filePath))
                {
                    continue;
                }

                string diff = await GetFileDiffAsync(
                    gitClient, repoId, filePath, change.ChangeType, sourceRefName, targetRefName);
                result.Add(new ChangedFile
                {
                    Path = filePath,
                    ChangeType = change.ChangeType.ToString(),
                    Diff = diff
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not get changes for PR #{prId}: {ex.Message}");
        }

        return result;
    }

    private async Task<string> GetFileDiffAsync(
        GitHttpClient gitClient, string repoId, string filePath,
        VersionControlChangeType changeType, string sourceRefName, string targetRefName)
    {
        try
        {
            string oldContent = "";
            string newContent = "";

            bool isAdd = changeType.HasFlag(VersionControlChangeType.Add);
            bool isDelete = changeType.HasFlag(VersionControlChangeType.Delete);

            if (!isDelete)
            {
                GitVersionDescriptor sourceVersion = new()
                {
                    Version = sourceRefName.Replace(RefsHeadsPrefix, ""),
                    VersionType = GitVersionType.Branch
                };
                newContent = await ReadStreamAsync(gitClient, repoId, filePath, sourceVersion);
            }

            if (!isAdd)
            {
                GitVersionDescriptor targetVersion = new()
                {
                    Version = targetRefName.Replace(RefsHeadsPrefix, ""),
                    VersionType = GitVersionType.Branch
                };
                oldContent = await ReadStreamAsync(gitClient, repoId, filePath, targetVersion);
            }

            return GenerateUnifiedDiff(oldContent, newContent);
        }
        catch
        {
            return "[Could not retrieve file diff]";
        }
    }

    private static async Task<string> ReadStreamAsync(
        GitHttpClient gitClient, string repoId, string filePath, GitVersionDescriptor version)
    {
        using Stream stream = await gitClient.GetItemContentAsync(repoId, filePath, versionDescriptor: version);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync();
    }

    private static string GenerateUnifiedDiff(string oldContent, string newContent)
    {
        const int maxLines = 300;
        string[] oldLines = oldContent.Length == 0 ? [] : oldContent.Split('\n');
        string[] newLines = newContent.Length == 0 ? [] : newContent.Split('\n');

        if (oldLines.Length > maxLines)
        {
            oldLines = oldLines[..maxLines];
        }

        if (newLines.Length > maxLines)
        {
            newLines = newLines[..maxLines];
        }

        List<(char op, string line)> diff = ComputeLineDiff(oldLines, newLines);

        StringBuilder sb = new();
        foreach ((char op, string? line) in diff)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{op}{line}");
        }

        return sb.ToString();
    }

    private static List<(char op, string line)> ComputeLineDiff(string[] oldLines, string[] newLines)
    {
        int m = oldLines.Length, n = newLines.Length;
        int[,] dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                dp[i, j] = oldLines[i - 1] == newLines[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        List<(char, string)> result = new(m + n);
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && oldLines[x - 1] == newLines[y - 1])
            { result.Add((' ', oldLines[x - 1])); x--; y--; }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            { result.Add(('+', newLines[y - 1])); y--; }
            else
            { result.Add(('-', oldLines[x - 1])); x--; }
        }

        result.Reverse();
        return result;
    }

    private async Task<Guid> GetCurrentUserIdAsync()
    {
        // Fix CS1061: GetSelfAsync doesn't exist on IdentityHttpClient in v19
        // Use the connection's authenticated identity instead
        await _connection.ConnectAsync();
        return _connection.AuthorizedIdentity.Id;
    }

    public async Task PostCommentToPrAsync(
    string repoId, int prId, string filePath, int? line, string comment)
    {
        GitHttpClient gitClient = await _connection.GetClientAsync<GitHttpClient>();

        GitPullRequestCommentThread thread;

        if (line.HasValue && !string.IsNullOrEmpty(filePath))
        {
            thread = new GitPullRequestCommentThread
            {
                Comments = [new Comment { Content = comment, CommentType = CommentType.Text }],
                Status = CommentThreadStatus.Active,

                // ThreadContext (CommentThreadContext) is where FilePath and position live
                ThreadContext = new CommentThreadContext
                {
                    FilePath = filePath,
                    RightFileStart = new CommentPosition { Line = line.Value, Offset = 1 },
                    RightFileEnd = new CommentPosition { Line = line.Value, Offset = 1 }
                },

                // PullRequestThreadContext (GitPullRequestCommentThreadContext) holds iteration info
                // Required for inline comments to render correctly in Azure DevOps UI
                PullRequestThreadContext = new GitPullRequestCommentThreadContext
                {
                    ChangeTrackingId = 1,
                    IterationContext = new CommentIterationContext
                    {
                        FirstComparingIteration = 1,
                        SecondComparingIteration = 1
                    }
                }
            };
        }
        else
        {
            // General PR-level comment, no file context needed
            thread = new GitPullRequestCommentThread
            {
                Comments = [new Comment { Content = comment, CommentType = CommentType.Text }],
                Status = CommentThreadStatus.Active
            };
        }

        // Fix CS1744: CreateThreadAsync signature is (thread, repositoryId, pullRequestId, project)
        // project is a named param that must NOT also be given positionally
        await gitClient.CreateThreadAsync(thread, repoId, prId, _settings.Project);
    }

    private static bool IsCodeFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string[] codeExtensions = [ ".cs", ".vue", ".ts", ".js", ".tsx", ".jsx",
            ".json", ".yaml", ".yml", ".xml", ".csproj", ".razor", ".html", ".css", ".scss", ".esproj" ];
        return codeExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}

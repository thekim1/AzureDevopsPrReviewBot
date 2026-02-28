using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

public class AzureDevOpsService
{
    private readonly AzureDevOpsSettings _settings;
    private readonly VssConnection _connection;

    public AzureDevOpsService(AzureDevOpsSettings settings)
    {
        _settings = settings;
        VssBasicCredential credentials = new VssBasicCredential(string.Empty, settings.PersonalAccessToken);
        _connection = new VssConnection(new Uri(settings.OrganizationUrl), credentials);
    }

    public async Task<List<PullRequestInfo>> GetAssignedPullRequestsAsync()
    {
        GitHttpClient gitClient = await _connection.GetClientAsync<GitHttpClient>();
        List<GitRepository> repos = await gitClient.GetRepositoriesAsync(_settings.Project);

        // Filter out disabled repositories — they are returned by GetRepositoriesAsync
        // but throw TF401019 when used in subsequent API calls like GetPullRequestsAsync
        repos = repos.Where(r => r.IsDisabled != true).ToList();

        List<PullRequestInfo> result = new List<PullRequestInfo>();

        Guid reviewerId = await GetCurrentUserIdAsync();

        foreach (GitRepository? repo in repos)
        {
            GitPullRequestSearchCriteria searchCriteria = new GitPullRequestSearchCriteria
            {
                Status = PullRequestStatus.Active,
                ReviewerId = reviewerId
            };

            // Fix CS1744: remove duplicate positional+named project args
            List<GitPullRequest> prs = await gitClient.GetPullRequestsAsync(
                _settings.Project, repo.Id, searchCriteria);

            foreach (GitPullRequest? pr in prs)
            {
                List<ChangedFile> changes = await GetPrChangesAsync(gitClient, repo.Id.ToString(), pr);
                result.Add(new PullRequestInfo
                {
                    Id = pr.PullRequestId,
                    Title = pr.Title,
                    Description = pr.Description ?? "",
                    Author = pr.CreatedBy.DisplayName,
                    SourceBranch = pr.SourceRefName.Replace("refs/heads/", ""),
                    TargetBranch = pr.TargetRefName.Replace("refs/heads/", ""),
                    RepositoryName = repo.Name,
                    RepositoryId = repo.Id.ToString(),
                    ChangedFiles = changes,
                    Url = $"{_settings.OrganizationUrl}/{_settings.Project}/_git/{repo.Name}/pullrequest/{pr.PullRequestId}"
                });
            }
        }

        return result;
    }

    private async Task<List<ChangedFile>> GetPrChangesAsync(
        GitHttpClient gitClient, string repoId, GitPullRequest pr)
    {
        List<ChangedFile> result = new List<ChangedFile>();
        try
        {
            List<GitPullRequestIteration> iterations = await gitClient.GetPullRequestIterationsAsync(
                _settings.Project, repoId, pr.PullRequestId);

            if (!iterations.Any()) return result;

            GitPullRequestIteration latestIteration = iterations.OrderByDescending(i => i.Id).First();

            GitPullRequestIterationChanges changes = await gitClient.GetPullRequestIterationChangesAsync(
                _settings.Project, repoId, pr.PullRequestId, latestIteration.Id!.Value);

            foreach (GitPullRequestChange? change in changes.ChangeEntries.Take(20))
            {
                var filePath = change.Item.Path;
                if (!IsCodeFile(filePath)) continue;

                var diff = await GetFileContentAsync(gitClient, repoId, pr, filePath);
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
            Console.WriteLine($"Warning: Could not get changes for PR #{pr.PullRequestId}: {ex.Message}");
        }

        return result;
    }

    private async Task<string> GetFileContentAsync(
        GitHttpClient gitClient, string repoId, GitPullRequest pr, string filePath)
    {
        try
        {
            GitVersionDescriptor sourceVersion = new GitVersionDescriptor
            {
                Version = pr.SourceRefName.Replace("refs/heads/", ""),
                VersionType = GitVersionType.Branch
            };

            using Stream stream = await gitClient.GetItemContentAsync(
                repoId, filePath, versionDescriptor: sourceVersion);
            using StreamReader reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            IEnumerable<string> lines = content.Split('\n').Take(300);
            return string.Join('\n', lines);
        }
        catch
        {
            return "[Could not retrieve file content]";
        }
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

    private static bool IsCodeFile(string path)
    {
        var codeExtensions = new[] { ".cs", ".vue", ".ts", ".js", ".tsx", ".jsx",
            ".json", ".yaml", ".yml", ".xml", ".csproj", ".razor", ".html", ".css", ".scss" };
        return codeExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
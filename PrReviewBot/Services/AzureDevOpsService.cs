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
    private readonly ReviewSettings _reviewSettings;
    private readonly VssConnection _connection;

    // Repository conventions are identical for every PR in a repo, so they are
    // fetched once per (repo, target branch) per run instead of per PR.
    private readonly Dictionary<string, List<RepoContextFile>> _repoContextCache =
        new(StringComparer.OrdinalIgnoreCase);

    public AzureDevOpsService(AzureDevOpsSettings settings, ReviewSettings? reviewSettings = null)
    {
        _settings = settings;
        _reviewSettings = reviewSettings ?? new ReviewSettings();
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
                bool hasReviewers = pr.Reviewers is not null && pr.Reviewers.Length != 0;
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
                    IsAssignedToMe = isAssignedToMe,
                    HasReviewers = hasReviewers
                });
            }
        }

        return result;
    }

    // Lazily fetch everything the reviewer needs for a single selected PR:
    // the diff, the existing comment threads, and the repository's own
    // convention documents. Call this only for PRs the user chose to review.
    public async Task LoadPrChangesAsync(PullRequestInfo pr)
    {
        GitHttpClient gitClient = await _connection.GetClientAsync<GitHttpClient>();
        (List<ChangedFile> files, List<SkippedFile> skipped, int iterationId) = await GetPrChangesAsync(
            gitClient, pr.RepositoryId, pr.Id, RefsHeadsPrefix + pr.TargetBranch);
        pr.ChangedFiles = files;
        pr.SkippedFiles = skipped;
        pr.LatestIterationId = iterationId;
        pr.ExistingComments = await GetPrCommentsAsync(gitClient, pr.RepositoryId, pr.Id);

        if (_reviewSettings.IncludeRepoContext)
        {
            pr.RepoContext = await GetRepoContextAsync(gitClient, pr.RepositoryId, pr.TargetBranch);
        }
    }

    // Reads the repository's own instructions — agent files (AGENTS.md,
    // CLAUDE.md, copilot-instructions.md), architecture notes, README and the
    // build/style configuration — from the PR's target branch. Without these
    // the model reviews against generic best practice and flags deliberate
    // project conventions as defects.
    private async Task<List<RepoContextFile>> GetRepoContextAsync(
        GitHttpClient gitClient, string repoId, string targetBranch)
    {
        string cacheKey = $"{repoId}@{targetBranch}";
        if (_repoContextCache.TryGetValue(cacheKey, out List<RepoContextFile>? cached))
        {
            return cached;
        }

        List<RepoContextFile> result = [];
        int budget = _reviewSettings.MaxRepoContextChars;

        GitVersionDescriptor version = new()
        {
            Version = targetBranch,
            VersionType = GitVersionType.Branch
        };

        foreach (RepoContextGroup group in _reviewSettings.RepoContextFileGroups)
        {
            if (budget <= 0)
            {
                break;
            }

            // First hit wins: the alternatives within a group are different
            // names for the same kind of document, not extra information.
            foreach (string path in group.Paths)
            {
                string content;
                try
                {
                    content = await ReadStreamAsync(gitClient, repoId, path, version);
                }
                catch
                {
                    // File simply does not exist in this repo — expected for
                    // most of the candidate list, so not worth reporting.
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                int limit = Math.Min(_reviewSettings.MaxRepoContextFileChars, budget);
                bool truncated = content.Length > limit;
                if (truncated)
                {
                    content = content[..limit];
                }

                budget -= content.Length;
                result.Add(new RepoContextFile
                {
                    Path = path,
                    Content = content,
                    IsTruncated = truncated
                });
                break;
            }
        }

        _repoContextCache[cacheKey] = result;
        return result;
    }

    // Fetches existing comment threads on the PR so the reviewer is aware of
    // feedback someone else has already left (e.g. decisions, questions).
    // System-generated threads ("X voted", "updated the source branch") and
    // deleted comments are excluded — they are pure noise in the prompt and
    // make the model think a concern was raised when none was.
    private async Task<List<PrComment>> GetPrCommentsAsync(GitHttpClient gitClient, string repoId, int prId)
    {
        List<PrComment> result = [];
        try
        {
            List<GitPullRequestCommentThread> threads = await gitClient.GetThreadsAsync(
                _settings.Project, repoId, prId);

            foreach (GitPullRequestCommentThread? thread in threads)
            {
                if (thread.Comments is null || thread.IsDeleted == true)
                {
                    continue;
                }

                foreach (Comment? c in thread.Comments)
                {
                    if (c is null || c.IsDeleted == true)
                    {
                        continue;
                    }

                    // Only human/bot prose. System comments are Azure DevOps
                    // activity records, not review feedback.
                    if (c.CommentType is CommentType.System)
                    {
                        continue;
                    }

                    string content = c.Content ?? "";
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    result.Add(new PrComment
                    {
                        Author = c.Author?.DisplayName ?? "Unknown",
                        Content = content,
                        FilePath = thread.ThreadContext?.FilePath,
                        LineNumber = thread.ThreadContext?.RightFileStart?.Line
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not get comments for PR #{prId}: {ex.Message}");
        }

        return result;
    }

    private async Task<(List<ChangedFile> Files, List<SkippedFile> Skipped, int IterationId)> GetPrChangesAsync(
        GitHttpClient gitClient, string repoId, int prId, string targetRefName)
    {
        List<ChangedFile> result = [];
        List<SkippedFile> skipped = [];
        int iterationId = 1;
        try
        {
            List<GitPullRequestIteration> iterations = await gitClient.GetPullRequestIterationsAsync(
                _settings.Project, repoId, prId);

            if (iterations.Count == 0)
            {
                return (result, skipped, iterationId);
            }

            GitPullRequestIteration latestIteration = iterations.OrderByDescending(i => i.Id).First();
            iterationId = latestIteration.Id!.Value;

            // Diff the exact commits this iteration refers to, never the
            // branch tips. Two reasons:
            //  * CommonRefCommit is the merge base — the same base Azure
            //    DevOps diffs against. Using the target branch tip instead
            //    drags in every unrelated commit merged into the target since
            //    the PR branched, which the reviewer then reports as defects.
            //  * SourceRefCommit pins the new-file line numbers to the
            //    iteration we are reviewing. The source branch tip can move
            //    between listing the changes and reading the content, which
            //    silently shifts every line number.
            GitVersionDescriptor sourceVersion = CommitVersion(latestIteration.SourceRefCommit?.CommitId)
                ?? BranchVersion(targetRefName);
            GitVersionDescriptor baseVersion = CommitVersion(latestIteration.CommonRefCommit?.CommitId)
                ?? BranchVersion(targetRefName);

            GitPullRequestIterationChanges changes = await gitClient.GetPullRequestIterationChangesAsync(
                _settings.Project, repoId, prId, iterationId);

            List<GitPullRequestChange> entries = [.. changes.ChangeEntries];

            foreach (GitPullRequestChange? change in entries)
            {
                string? filePath = change.Item?.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    continue;
                }

                if (change.Item?.IsFolder == true)
                {
                    continue;
                }

                if (!IsCodeFile(filePath))
                {
                    skipped.Add(new SkippedFile { Path = filePath, Reason = "not a reviewable source file" });
                    continue;
                }

                if (result.Count >= _reviewSettings.MaxFilesPerPr)
                {
                    skipped.Add(new SkippedFile
                    {
                        Path = filePath,
                        Reason = $"over the {_reviewSettings.MaxFilesPerPr}-file review limit"
                    });
                    continue;
                }

                DiffBuilder.DiffResult diff = await GetFileDiffAsync(
                    gitClient, repoId, filePath, change.ChangeType,
                    sourceVersion, baseVersion, _reviewSettings);

                if (!diff.Success)
                {
                    skipped.Add(new SkippedFile { Path = filePath, Reason = diff.FailureReason });
                    continue;
                }

                result.Add(new ChangedFile
                {
                    Path = filePath,
                    ChangeType = change.ChangeType.ToString(),
                    Diff = diff.Text,
                    IsTruncated = diff.IsTruncated,
                    NewFileLineCount = diff.NewFileLineCount,
                    ChangeTrackingId = change.ChangeTrackingId
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not get changes for PR #{prId}: {ex.Message}");
        }

        return (result, skipped, iterationId);
    }

    private static GitVersionDescriptor? CommitVersion(string? commitId)
        => string.IsNullOrEmpty(commitId)
            ? null
            : new GitVersionDescriptor { Version = commitId, VersionType = GitVersionType.Commit };

    private static GitVersionDescriptor BranchVersion(string refName)
        => new() { Version = refName.Replace(RefsHeadsPrefix, ""), VersionType = GitVersionType.Branch };

    private static async Task<DiffBuilder.DiffResult> GetFileDiffAsync(
        GitHttpClient gitClient, string repoId, string filePath,
        VersionControlChangeType changeType,
        GitVersionDescriptor sourceVersion, GitVersionDescriptor baseVersion,
        ReviewSettings reviewSettings)
    {
        string oldContent = "";
        string newContent = "";

        bool isAdd = changeType.HasFlag(VersionControlChangeType.Add);
        bool isDelete = changeType.HasFlag(VersionControlChangeType.Delete);

        try
        {
            if (!isDelete)
            {
                newContent = await ReadStreamAsync(gitClient, repoId, filePath, sourceVersion);
            }
        }
        catch (Exception ex)
        {
            return DiffBuilder.DiffResult.Failed($"could not read the new version ({ex.GetType().Name})");
        }

        try
        {
            if (!isAdd)
            {
                oldContent = await ReadStreamAsync(gitClient, repoId, filePath, baseVersion);
            }
        }
        catch
        {
            // A rename or copy leaves no file at this path in the merge base.
            // Treating it as an addition is correct and shows the whole new
            // file, which is what a reviewer needs anyway.
            oldContent = "";
        }

        if (DiffBuilder.LooksBinary(newContent) || DiffBuilder.LooksBinary(oldContent))
        {
            return DiffBuilder.DiffResult.Failed("binary content");
        }

        return DiffBuilder.Build(oldContent, newContent, reviewSettings);
    }

    private static async Task<string> ReadStreamAsync(
        GitHttpClient gitClient, string repoId, string filePath, GitVersionDescriptor version)
    {
        using Stream stream = await gitClient.GetItemContentAsync(repoId, filePath, versionDescriptor: version);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync();
    }

    private async Task<Guid> GetCurrentUserIdAsync()
    {
        // Fix CS1061: GetSelfAsync doesn't exist on IdentityHttpClient in v19
        // Use the connection's authenticated identity instead
        await _connection.ConnectAsync();
        return _connection.AuthorizedIdentity.Id;
    }

    // iterationId/changeTrackingId must describe the SAME iteration the diff
    // was taken from — see the comment on the thread context below.
    public async Task PostCommentToPrAsync(
        string repoId, int prId, string filePath, int? line, string comment,
        int iterationId = 1, int changeTrackingId = 0)
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

                // PullRequestThreadContext tells Azure DevOps which iteration
                // the line number is expressed in. RightFileStart refers to the
                // "after" side, i.e. SecondComparingIteration.
                //
                // This used to hardcode SecondComparingIteration = 1, while the
                // diff being reviewed came from the LATEST iteration. Azure
                // DevOps therefore took the line number as an iteration-1
                // coordinate and tracked it forward to the current view,
                // shifting every comment by the net lines added or removed in
                // the pushes since — which is why comments landed further and
                // further off the more the author pushed. Passing the real
                // iteration keeps the coordinate space we actually reviewed.
                PullRequestThreadContext = new GitPullRequestCommentThreadContext
                {
                    // GitPullRequestChange exposes this as int while the thread
                    // context takes a short; clamp rather than wrap silently.
                    ChangeTrackingId = changeTrackingId is > 0 and <= short.MaxValue
                        ? (short)changeTrackingId
                        : (short)0,
                    IterationContext = new CommentIterationContext
                    {
                        FirstComparingIteration = 1,
                        SecondComparingIteration = iterationId is > 0 and <= short.MaxValue
                            ? (short)iterationId
                            : (short)1
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
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string[] codeExtensions = [ ".cs", ".vue", ".ts", ".js", ".tsx", ".jsx",
            ".json", ".yaml", ".yml", ".xml", ".csproj", ".razor", ".html", ".css", ".scss", ".esproj" ];
        return codeExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}

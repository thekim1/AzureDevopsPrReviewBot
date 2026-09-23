using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using WorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;
using WorkItemErrorPolicy = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItemErrorPolicy;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

public class AzureDevOpsService
{
    private const string RefsHeadsPrefix = "refs/heads/";

    // Page size for the project-wide PR listing. The API returns a limited
    // number of results per call when no page size is given.
    private const int PullRequestPageSize = 500;

    // Page size for a PR iteration's change list. The API returns 100 changes
    // when none is given, so on a larger PR every file after the hundredth was
    // silently missing — not reviewed, and not listed as skipped either.
    private const int ChangePageSize = 1000;

    private readonly AzureDevOpsSettings _settings;
    private readonly ReviewSettings _reviewSettings;
    private readonly VssConnection _connection;

    // Repository conventions are identical for every PR in a repo, so they are
    // fetched once per (repo, target branch) per run instead of per PR. The
    // task is cached, not the result, so two PRs loading at the same time share
    // one fetch instead of racing to make two.
    private readonly ConcurrentDictionary<string, Lazy<Task<List<RepoContextFile>>>> _repoContextCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Caps concurrent file reads across everything this service does,
    // including a PR being prefetched while another is loading. Reading files
    // one at a time was most of the wait before a review could start.
    private readonly SemaphoreSlim _readGate;

    // Folder listings, per repo, branch and folder. PRs in the same repo share
    // most of their folders, and each listing is a round trip.
    private readonly ConcurrentDictionary<string, Lazy<Task<string[]>>> _listingCache =
        new(StringComparer.OrdinalIgnoreCase);

    // The repository's full file list, per repo and branch, for resolving
    // referenced definitions. One call per repo for the whole run.
    private readonly ConcurrentDictionary<string, Lazy<Task<string[]>>> _treeCache =
        new(StringComparer.OrdinalIgnoreCase);

    // A PAT without Work Items (Read) fails every lookup the same way; saying
    // so once is enough.
    private int _workItemWarningShown;

    // More folders than this between the root and the changed files are not
    // searched for conventions; the nearest ones are.
    private const int MaxScopedDirectories = 40;

    public AzureDevOpsService(AzureDevOpsSettings settings, ReviewSettings? reviewSettings = null)
    {
        _settings = settings;
        _reviewSettings = reviewSettings ?? new ReviewSettings();
        _readGate = new SemaphoreSlim(Math.Max(1, _reviewSettings.MaxParallelDevOpsRequests));
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

        GitPullRequestSearchCriteria searchCriteria = new()
        {
            Status = PullRequestStatus.Active
        };

        // One project-wide listing instead of one listing per repository, and
        // the three independent calls in parallel rather than one after another.
        Task<List<GitRepository>> reposTask = gitClient.GetRepositoriesAsync(_settings.Project);
        Task<Guid> reviewerTask = GetCurrentUserIdAsync();
        Task<List<GitPullRequest>> prsTask = ConcurrencyHelpers.ReadAllPagesAsync(
            PullRequestPageSize,
            (skip, top) => gitClient.GetPullRequestsByProjectAsync(
                _settings.Project, searchCriteria, skip: skip, top: top));

        await Task.WhenAll(reposTask, reviewerTask, prsTask);

        // Disabled repositories are returned by GetRepositoriesAsync but throw
        // TF401019 when used in subsequent API calls, so their PRs are left out.
        // Listing order follows the repository list, as it did when PRs were
        // fetched repository by repository.
        Dictionary<Guid, int> repoOrder = [];
        foreach (GitRepository repo in reposTask.Result)
        {
            if (repo.IsDisabled != true)
            {
                repoOrder[repo.Id] = repoOrder.Count;
            }
        }

        Guid reviewerId = reviewerTask.Result;
        List<PullRequestInfo> result = [];

        IEnumerable<GitPullRequest> prs = prsTask.Result
            .Where(pr => pr.Repository is not null && repoOrder.ContainsKey(pr.Repository.Id))
            .OrderBy(pr => repoOrder[pr.Repository.Id]);

        foreach (GitPullRequest pr in prs)
        {
            if (pr.IsDraft == true)
            {
                continue;
            }

            GitRepository repo = pr.Repository;
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

        return result;
    }

    // Lazily fetch everything the reviewer needs for a single selected PR:
    // the diff, the existing comment threads, and the repository's own
    // convention documents. Call this only for PRs the user chose to review.
    public async Task LoadPrChangesAsync(PullRequestInfo pr)
    {
        GitHttpClient gitClient = await _connection.GetClientAsync<GitHttpClient>();

        // The three are independent, so they are fetched side by side.
        Task<(List<ChangedFile> Files, List<SkippedFile> Skipped, int IterationId)> changesTask =
            GetPrChangesAsync(gitClient, pr.RepositoryId, pr.Id, RefsHeadsPrefix + pr.TargetBranch);
        Task<List<PrComment>> commentsTask = GetPrCommentsAsync(gitClient, pr.RepositoryId, pr.Id);
        Task<List<RepoContextFile>>? contextTask = _reviewSettings.IncludeRepoContext
            ? GetRepoContextAsync(gitClient, pr.RepositoryId, pr.TargetBranch)
            : null;

        Task<List<LinkedWorkItem>>? workItemsTask = _reviewSettings.IncludeWorkItems
            ? GetWorkItemsAsync(gitClient, pr.RepositoryId, pr.Id)
            : null;
        Task<List<string>>? commitsTask = _reviewSettings.IncludeCommitMessages
            ? GetCommitMessagesAsync(gitClient, pr.RepositoryId, pr.Id)
            : null;

        (List<ChangedFile> files, List<SkippedFile> skipped, int iterationId) = await changesTask;
        pr.ChangedFiles = files;
        pr.SkippedFiles = skipped;
        pr.LatestIterationId = iterationId;

        // These depend on which files changed, so they start once those are known.
        Task<List<RepoContextFile>>? scopedTask = _reviewSettings.IncludeScopedContext && files.Count != 0
            ? GetScopedContextAsync(gitClient, pr.RepositoryId, pr.TargetBranch, files.Select(f => f.Path))
            : null;
        Task<List<ReferencedDefinition>>? definitionsTask = _reviewSettings.IncludeReferencedDefinitions && files.Count != 0
            ? GetReferencedDefinitionsAsync(gitClient, pr.RepositoryId, pr.TargetBranch, files, skipped)
            : null;

        pr.ExistingComments = await commentsTask;

        if (contextTask is not null)
        {
            pr.RepoContext = await contextTask;
        }

        if (workItemsTask is not null)
        {
            pr.WorkItems = await workItemsTask;
        }

        if (commitsTask is not null)
        {
            pr.CommitMessages = await commitsTask;
        }

        if (scopedTask is not null)
        {
            pr.ScopedContext = await scopedTask;
        }

        if (definitionsTask is not null)
        {
            pr.ReferencedDefinitions = await definitionsTask;
        }
    }

    // Reads the repository's own instructions — agent files (AGENTS.md,
    // CLAUDE.md, copilot-instructions.md), architecture notes, README and the
    // build/style configuration — from the PR's target branch. Without these
    // the model reviews against generic best practice and flags deliberate
    // project conventions as defects.
    private Task<List<RepoContextFile>> GetRepoContextAsync(
        GitHttpClient gitClient, string repoId, string targetBranch)
        => _repoContextCache.GetOrAdd(
            $"{repoId}@{targetBranch}",
            _ => new Lazy<Task<List<RepoContextFile>>>(
                () => FetchRepoContextAsync(gitClient, repoId, targetBranch))).Value;

    private async Task<List<RepoContextFile>> FetchRepoContextAsync(
        GitHttpClient gitClient, string repoId, string targetBranch)
    {
        GitVersionDescriptor version = new()
        {
            Version = targetBranch,
            VersionType = GitVersionType.Branch
        };

        List<RepoContextGroup> groups = _reviewSettings.RepoContextFileGroups;

        // List the handful of folders the candidates live in, then read only
        // the candidates that exist.
        string[][] listings = await Task.WhenAll(RepoContextPlanner.DirectoriesToList(groups)
            .Select(dir => ListFilesAsync(gitClient, repoId, dir, version)));
        HashSet<string> existing = new(listings.SelectMany(l => l), StringComparer.Ordinal);

        List<List<string>> candidates = RepoContextPlanner.ResolveCandidates(groups, existing);

        (string Path, string? Content)[][] fetched = await Task.WhenAll(candidates.Select(async group =>
            await Task.WhenAll(group.Select(async path => (path, await TryReadAsync(gitClient, repoId, path, version))))));

        return RepoContextPlanner.Assemble(
            fetched, _reviewSettings.MaxRepoContextChars, _reviewSettings.MaxRepoContextFileChars);
    }

    // Convention files from the folders between the root and the changed
    // files, nearest first, within their own budget.
    private async Task<List<RepoContextFile>> GetScopedContextAsync(
        GitHttpClient gitClient, string repoId, string targetBranch, IEnumerable<string> changedPaths)
    {
        GitVersionDescriptor version = new() { Version = targetBranch, VersionType = GitVersionType.Branch };

        List<string> directories = [.. RepoContextPlanner.AncestorDirectories(changedPaths).Take(MaxScopedDirectories)];

        string[][] listings = await Task.WhenAll(directories.Select(dir =>
            _listingCache.GetOrAdd(
                $"{repoId}@{targetBranch}:{dir}",
                _ => new Lazy<Task<string[]>>(() => ListFilesAsync(gitClient, repoId, dir, version))).Value));

        List<(string Directory, string Path)> wanted =
        [
            .. directories.Zip(listings).SelectMany(pair =>
                RepoContextPlanner.ScopedFilesIn(pair.Second).Select(path => (pair.First, path)))
        ];

        string?[] contents = await Task.WhenAll(wanted.Select(w => TryReadAsync(gitClient, repoId, w.Path, version)));

        List<RepoContextFile> files = RepoContextPlanner.Assemble(
            [.. wanted.Select((w, i) => (IReadOnlyList<(string, string?)>)[(w.Path, contents[i])])],
            _reviewSettings.MaxScopedContextChars,
            _reviewSettings.MaxRepoContextFileChars);

        foreach (RepoContextFile file in files)
        {
            file.AppliesTo = wanted.First(w => w.Path == file.Path).Directory + "/";
        }

        return files;
    }

    // Outlines of the files outside the PR that its changed files use, read
    // from the target branch.
    private async Task<List<ReferencedDefinition>> GetReferencedDefinitionsAsync(
        GitHttpClient gitClient, string repoId, string targetBranch,
        List<ChangedFile> files, List<SkippedFile> skipped)
    {
        GitVersionDescriptor version = new() { Version = targetBranch, VersionType = GitVersionType.Branch };

        string[] tree = await _treeCache.GetOrAdd(
            $"{repoId}@{targetBranch}",
            _ => new Lazy<Task<string[]>>(() => ListTreeAsync(gitClient, repoId, version))).Value;
        if (tree.Length == 0)
        {
            return [];
        }

        HashSet<string> inPr = new(files.Select(f => f.Path).Concat(skipped.Select(s => s.Path)), StringComparer.Ordinal);
        List<DefinitionCandidate> candidates = DefinitionIndex.SelectCandidates(
            files, tree, inPr, _reviewSettings.MaxReferencedDefinitions);

        string?[] contents = await Task.WhenAll(candidates.Select(c => TryReadAsync(gitClient, repoId, c.Path, version)));

        List<ReferencedDefinition> result = [];
        for (int i = 0; i < candidates.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(contents[i]) || DiffBuilder.LooksBinary(contents[i]!))
            {
                continue;
            }

            string outline = DefinitionIndex.Outline(candidates[i].Path, contents[i]!, _reviewSettings.MaxDefinitionOutlineChars);
            if (outline.Length != 0)
            {
                result.Add(new ReferencedDefinition
                {
                    Path = candidates[i].Path,
                    Outline = outline,
                    ReferencedFrom = candidates[i].ReferencedFrom
                });
            }
        }

        return result;
    }

    // Every file path in the repository on one branch. Empty when it cannot
    // be listed, in which case reviews go ahead without referenced definitions.
    private async Task<string[]> ListTreeAsync(GitHttpClient gitClient, string repoId, GitVersionDescriptor version)
    {
        await _readGate.WaitAsync();
        try
        {
            List<GitItem> items = await gitClient.GetItemsAsync(
                repoId, scopePath: "/", recursionLevel: VersionControlRecursionType.Full, versionDescriptor: version);
            return [.. items.Where(i => !i.IsFolder && !string.IsNullOrEmpty(i.Path)).Select(i => i.Path)];
        }
        catch (Exception ex)
        {
            DeferredConsole.WriteLine($"Warning: could not list the repository's files ({ex.Message}); reviewing without referenced definitions.");
            return [];
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task<List<LinkedWorkItem>> GetWorkItemsAsync(GitHttpClient gitClient, string repoId, int prId)
    {
        try
        {
            List<ResourceRef> refs = await gitClient.GetPullRequestWorkItemRefsAsync(_settings.Project, repoId, prId);
            List<int> ids =
            [
                .. refs.Select(r => int.TryParse(r.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0)
                    .Where(id => id > 0)
                    .Distinct()
                    .Take(_reviewSettings.MaxWorkItems)
            ];

            if (ids.Count == 0)
            {
                return [];
            }

            WorkItemTrackingHttpClient workItemClient = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();
            List<WorkItem> items = await workItemClient.GetWorkItemsAsync(
                ids,
                fields:
                [
                    "System.WorkItemType", "System.Title", "System.Description",
                    "Microsoft.VSTS.Common.AcceptanceCriteria", "Microsoft.VSTS.TCM.ReproSteps"
                ],
                errorPolicy: WorkItemErrorPolicy.Omit);

            return
            [
                .. items.Where(i => i?.Id is not null).Select(i => IntentContext.Describe(
                    i.Id!.Value,
                    Field(i, "System.WorkItemType"),
                    Field(i, "System.Title"),
                    // A bug keeps its story in the repro steps, not the description.
                    Field(i, "System.Description") ?? Field(i, "Microsoft.VSTS.TCM.ReproSteps"),
                    Field(i, "Microsoft.VSTS.Common.AcceptanceCriteria"),
                    _reviewSettings.MaxWorkItemChars))
            ];
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _workItemWarningShown, 1) == 0)
            {
                DeferredConsole.WriteLine(
                    $"Warning: could not read linked work items ({ex.Message}). Reviews continue without them; "
                    + "give the PAT Work Items (Read), or set Review:IncludeWorkItems to false.");
            }

            return [];
        }
    }

    private static string? Field(WorkItem item, string name)
        => item.Fields is not null && item.Fields.TryGetValue(name, out object? value) ? value?.ToString() : null;

    private async Task<List<string>> GetCommitMessagesAsync(GitHttpClient gitClient, string repoId, int prId)
    {
        try
        {
            List<GitCommitRef> commits = await gitClient.GetPullRequestCommitsAsync(_settings.Project, repoId, prId);
            return IntentContext.UsefulCommitMessages(
                commits.Select(c => c.Comment), _reviewSettings.MaxCommitMessages, maxCharsEach: 300);
        }
        catch (Exception ex)
        {
            DeferredConsole.WriteLine($"Warning: Could not get commits for PR #{prId}: {ex.Message}");
            return [];
        }
    }

    // Paths of the files directly inside one folder. A folder that does not
    // exist is expected for most of the candidate list, so it is not reported.
    private async Task<string[]> ListFilesAsync(
        GitHttpClient gitClient, string repoId, string directory, GitVersionDescriptor version)
    {
        await _readGate.WaitAsync();
        try
        {
            List<GitItem> items = await gitClient.GetItemsAsync(
                repoId, scopePath: directory, recursionLevel: VersionControlRecursionType.OneLevel,
                versionDescriptor: version);
            return [.. items.Where(i => !i.IsFolder && !string.IsNullOrEmpty(i.Path)).Select(i => i.Path)];
        }
        catch
        {
            return [];
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task<string?> TryReadAsync(
        GitHttpClient gitClient, string repoId, string path, GitVersionDescriptor version)
    {
        try
        {
            return await ReadStreamAsync(gitClient, repoId, path, version);
        }
        catch
        {
            return null;
        }
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
                        LineNumber = thread.ThreadContext?.RightFileStart?.Line,
                        Status = thread.Status.ToString()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            DeferredConsole.WriteLine($"Warning: Could not get comments for PR #{prId}: {ex.Message}");
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

            List<GitPullRequestChange> changes = await ConcurrencyHelpers.ReadAllPagesAsync(
                ChangePageSize,
                async (skip, top) => (await gitClient.GetPullRequestIterationChangesAsync(
                    _settings.Project, repoId, prId, iterationId, top: top, skip: skip)).ChangeEntries?.ToList() ?? []);

            List<GitPullRequestChange> candidates = [];

            foreach (GitPullRequestChange? change in changes)
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

                string? exclusion = FileClassifier.ExclusionReason(filePath, _reviewSettings.ExcludedPaths);
                if (exclusion is not null)
                {
                    skipped.Add(new SkippedFile { Path = filePath, Reason = exclusion });
                    continue;
                }

                candidates.Add(change);
            }

            // When there are more files than the limit, the ones cut are the
            // least important: source first, then configuration, tests,
            // styles and documentation.
            candidates = FileClassifier.RankByPriority(candidates, c => c.Item.Path);

            // Diffs are fetched concurrently, but the files accepted are the
            // same ones, in the same order, as reading them one by one up to
            // the limit would give: a file that cannot be diffed does not use
            // up a place.
            LimitedSelection<GitPullRequestChange, DiffBuilder.DiffResult> selection =
                await ConcurrencyHelpers.SelectUpToLimitAsync(
                    candidates,
                    _reviewSettings.MaxFilesPerPr,
                    change => GetFileDiffAsync(gitClient, repoId, change, sourceVersion, baseVersion),
                    diff => diff.Success);

            foreach ((GitPullRequestChange change, DiffBuilder.DiffResult diff) in selection.Fetched)
            {
                string filePath = change.Item.Path;

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
                    IsWholeFile = diff.IsWholeFile,
                    ChangeTrackingId = change.ChangeTrackingId
                });
            }

            foreach (GitPullRequestChange change in selection.NotFetched)
            {
                skipped.Add(new SkippedFile
                {
                    Path = change.Item.Path,
                    Reason = $"over the {_reviewSettings.MaxFilesPerPr}-file review limit"
                });
            }
        }
        catch (Exception ex)
        {
            DeferredConsole.WriteLine($"Warning: Could not get changes for PR #{prId}: {ex.Message}");
        }

        return (result, skipped, iterationId);
    }

    private static GitVersionDescriptor? CommitVersion(string? commitId)
        => string.IsNullOrEmpty(commitId)
            ? null
            : new GitVersionDescriptor { Version = commitId, VersionType = GitVersionType.Commit };

    private static GitVersionDescriptor BranchVersion(string refName)
        => new() { Version = refName.Replace(RefsHeadsPrefix, ""), VersionType = GitVersionType.Branch };

    // Where the file's previous version lives in the merge base. For a rename
    // that is its old path: reading the new path there finds nothing, which
    // turned every renamed file into a whole-file addition and hid the few
    // lines that actually changed.
    internal static string BasePathFor(GitPullRequestChange change)
    {
        string path = change.Item?.Path ?? "";

        if (!change.ChangeType.HasFlag(VersionControlChangeType.Rename))
        {
            return path;
        }

        string? original = !string.IsNullOrEmpty(change.OriginalPath)
            ? change.OriginalPath
            : change.SourceServerItem;

        return string.IsNullOrEmpty(original) ? path : original;
    }

    private async Task<DiffBuilder.DiffResult> GetFileDiffAsync(
        GitHttpClient gitClient, string repoId, GitPullRequestChange change,
        GitVersionDescriptor sourceVersion, GitVersionDescriptor baseVersion)
    {
        string filePath = change.Item.Path;
        bool isAdd = change.ChangeType.HasFlag(VersionControlChangeType.Add);
        bool isDelete = change.ChangeType.HasFlag(VersionControlChangeType.Delete);

        // Both sides at once; they do not depend on each other.
        Task<string> newTask = isDelete
            ? Task.FromResult("")
            : ReadStreamAsync(gitClient, repoId, filePath, sourceVersion);
        Task<string> oldTask = isAdd
            ? Task.FromResult("")
            : ReadStreamAsync(gitClient, repoId, BasePathFor(change), baseVersion);

        string newContent;
        try
        {
            newContent = await newTask;
        }
        catch (Exception ex)
        {
            // Observe the other read so its failure is not left unobserved.
            await oldTask.ContinueWith(_ => { }, TaskScheduler.Default);
            return DiffBuilder.DiffResult.Failed($"could not read the new version ({ex.GetType().Name})");
        }

        string oldContent;
        try
        {
            oldContent = await oldTask;
        }
        catch
        {
            // A copy, or a rename Azure DevOps reported without its old path,
            // leaves no file at this path in the merge base. Treating it as an
            // addition is correct and shows the whole new file, which is what
            // a reviewer needs anyway.
            oldContent = "";
        }

        if (DiffBuilder.LooksBinary(newContent) || DiffBuilder.LooksBinary(oldContent))
        {
            return DiffBuilder.DiffResult.Failed("binary content");
        }

        return DiffBuilder.Build(oldContent, newContent, _reviewSettings, filePath);
    }

    private async Task<string> ReadStreamAsync(
        GitHttpClient gitClient, string repoId, string filePath, GitVersionDescriptor version)
    {
        await _readGate.WaitAsync();
        try
        {
            using Stream stream = await gitClient.GetItemContentAsync(repoId, filePath, versionDescriptor: version);
            using StreamReader reader = new(stream);
            return await reader.ReadToEndAsync();
        }
        finally
        {
            _readGate.Release();
        }
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
}

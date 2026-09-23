namespace PrReviewBot.Models;

public class PullRequestInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public List<ChangedFile> ChangedFiles { get; set; } = [];
    public string Url { get; set; } = "";
    // True when the current user is a reviewer on the PR.
    public bool IsAssignedToMe { get; set; }
    // True when at least one reviewer is assigned to the PR.
    public bool HasReviewers { get; set; }
    // Existing comments already made on the PR (fetched lazily). Passed to
    // the LLM as context so it can avoid duplicating or contradicting feedback.
    public List<PrComment> ExistingComments { get; set; } = [];
    // Repository conventions (agent instructions, README, .editorconfig, ...)
    // read from the target branch. Tells the reviewer what "correct" looks
    // like in *this* codebase instead of guessing from generic best practice.
    public List<RepoContextFile> RepoContext { get; set; } = [];
    // Convention files (AGENTS.md, .editorconfig, ...) from the folders the
    // changed files live in, below the repository root. In a monorepo these
    // are where each sub-project's own rules are.
    public List<RepoContextFile> ScopedContext { get; set; } = [];
    // Work items linked to the PR: what the change is meant to achieve.
    public List<LinkedWorkItem> WorkItems { get; set; } = [];
    // Commit messages on the PR's branch, merges left out.
    public List<string> CommitMessages { get; set; } = [];
    // Outlines of files outside the PR that the changed code uses.
    public List<ReferencedDefinition> ReferencedDefinitions { get; set; } = [];
    // Files that were changed by the PR but deliberately not sent for review
    // (binary, too large, diff failed). Listed in the prompt so the model
    // knows its view of the change is incomplete and does not reason about
    // what it cannot see.
    public List<SkippedFile> SkippedFiles { get; set; } = [];

    // The PR iteration the diff was actually taken from. Comments must be
    // posted against this same iteration, or Azure DevOps re-maps their line
    // numbers from whichever iteration it was told and the comment lands on
    // the wrong line.
    public int LatestIterationId { get; set; } = 1;
}

public class ChangedFile
{
    public string Path { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public string Diff { get; set; } = "";
    public string FileType => Path.Split('.').LastOrDefault() ?? "";
    // True when the emitted diff was cut short. Surfaced to the model so it
    // does not report "the method is never closed" on a file we cut off.
    public bool IsTruncated { get; set; }
    // Line count of the file on the source branch, for the same reason.
    public int NewFileLineCount { get; set; }
    // True when the diff contains every line of the file, not just hunks.
    public bool IsWholeFile { get; set; }

    // Azure DevOps' own identifier for this file's change within the
    // iteration. It is what lets the server track the file across iterations;
    // posting a comment with the wrong one misplaces it.
    public int ChangeTrackingId { get; set; }
}

// An outline (declarations, no bodies) of a file outside the PR that the
// changed code refers to, and which changed files refer to it.
public class ReferencedDefinition
{
    public string Path { get; set; } = "";
    public string Outline { get; set; } = "";
    public List<string> ReferencedFrom { get; set; } = [];
}

public class LinkedWorkItem
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
}

// A convention/architecture document read from the repository's target branch.
public class RepoContextFile
{
    public string Path { get; set; } = "";
    // For a file below the root: the folder whose files it governs.
    public string? AppliesTo { get; set; }
    public string Content { get; set; } = "";
    public bool IsTruncated { get; set; }
}

// A changed file that was intentionally excluded from the review prompt.
public class SkippedFile
{
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
}

// An existing comment already made on the PR by someone else. Sent to the
// LLM as context so it can avoid repeating feedback or build on prior input.
public class PrComment
{
    public string Author { get; set; } = "";
    public string Content { get; set; } = "";
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    // The thread's status in Azure DevOps: Active, Fixed, WontFix, Closed,
    // ByDesign, Pending. A concern someone already decided not to act on
    // should not come back from the reviewer.
    public string? Status { get; set; }
}

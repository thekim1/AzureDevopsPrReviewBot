# Copilot Instructions

## Project overview

PR Review Bot is a .NET 10 console app that fetches pull requests assigned to you from Azure DevOps, sends their diffs to an LLM (Claude, Ollama, or anything behind a Bifrost gateway) for review, validates the findings against the diff that was actually sent, displays the results in the terminal (Spectre.Console), optionally posts the comments back to Azure DevOps, and saves a markdown report to disk.

Single-project solution: `PrReviewBot.slnx` → `PrReviewBot/PrReviewBot.csproj`.

## Build, run, and lint

```bash
# Build (also runs analyzers — see below)
dotnet build PrReviewBot.slnx

# Run (from the PrReviewBot/ project directory so appsettings.json is found)
cd PrReviewBot && dotnet run
```

There is **no test project** and no separate lint command. Linting is enforced at compile time via `.editorconfig` analyzers (a large set of `CA*` and `IDE*` rules at `warning` severity). `dotnet build` is the lint step — treat new analyzer warnings as failures.

The app requires live secrets to run end-to-end (Azure DevOps PAT + Anthropic API key). Prefer building over running when verifying changes.

## Configuration and secrets

Configuration is loaded in `Program.cs` in this order (later overrides earlier): `appsettings.json` → .NET User Secrets → environment variables.

- **Never commit secrets to `appsettings.json`.** It contains placeholder values only.
- Nested keys use `:` in JSON and `__` (double underscore) in environment variables (e.g. `AzureDevOps__PersonalAccessToken`).
- Strongly-typed binding lives in `Config/AppSettings.cs` (`AppSettings` → `AzureDevOpsSettings`, `ClaudeSettings`, `OllamaSettings`, `BifrostSettings`, `ReviewSettings`). Add new settings here when extending config.
- `ReviewSettings` holds every review-accuracy dial (diff context width, file/line caps, repo-context budget, confidence and evidence thresholds). Prefer adding a setting there over hard-coding a constant in a service.

## Architecture

The flow is linear and orchestrated entirely in `Program.cs` (top-level statements):

1. `AzureDevOpsService.GetAllActivePullRequestsAsync()` — connects via `VssConnection`, enumerates repos in the project, filters disabled repos (they throw `TF401019`), returns all active non-draft PRs and marks each with `IsAssignedToMe` (current user is among its reviewers). `GetAssignedPullRequestsAsync()` is a thin filter over this. Program.cs partitions the list into "Assigned to you" and "Other PRs" groups for the selection prompt.
2. User selects review provider (Claude or Ollama) via a Spectre.Console `SelectionPrompt`; the chosen `IReviewService` is used for the rest of the run.
3. User selects PR(s) via a Spectre.Console `MultiSelectionPrompt`.
4. `IReviewService.ReviewPullRequestAsync()` — builds a prompt from the PR diff and asks the configured provider for a JSON array of comments, then parses it.
4b. `PullRequestReviewer.ReviewAsync()` — splits the PR's files into batches and calls the provider once per batch, merging the findings. **Never send a whole PR in one request**: a real 20-file PR spent its entire 16,384-token output budget on reasoning and returned an empty message. A failing batch costs its own files only.
5. `ReviewValidator.Validate()` — checks each finding against the diff that was actually sent and drops the ones that cannot be true. This runs before anything is displayed, saved, or posted.
6. `ReviewOutputService` — renders to terminal and writes a markdown report under `reviews/` (next to the build output, via `AppContext.BaseDirectory`).
7. On confirmation, comments are posted back via `AzureDevOpsService.PostCommentToPrAsync` (inline threads with file/line context, or general PR threads when no line).

### Services (`PrReviewBot/Services/`)

- **`AzureDevOpsService`** — owns all Azure DevOps REST interaction. Note the non-obvious bits:
  - The reviewer identity is obtained from `_connection.AuthorizedIdentity.Id` after `ConnectAsync()` — there is no `GetSelfAsync` in client v19.
  - Diffs are computed **in-process** by `DiffBuilder`, not via the API, using a custom LCS.
  - **Diff both sides from the iteration's commits, never the branch tips.** New content comes from `latestIteration.SourceRefCommit`, old content from `latestIteration.CommonRefCommit` (the merge base — the same base Azure DevOps diffs against). Using the target branch tip drags in every unrelated commit merged into the target since the PR branched, and the reviewer reports those as defects.
  - `PullRequestInfo.LatestIterationId` and `ChangedFile.ChangeTrackingId` exist purely so comments can be posted against the same iteration the diff came from. See `PostCommentToPrAsync`.
  - It also reads the reviewed repo's own convention files (`AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`, README, `.editorconfig`, ...) from the PR's **target branch** into `PullRequestInfo.RepoContext`, cached per (repo, branch) per run.
  - A changed file that cannot be reviewed safely (binary, too large to diff, unreadable, over the file limit) goes into `PullRequestInfo.SkippedFiles` and is named in the prompt. It is never sent as placeholder text — the model would review the placeholder.
  - System-generated comment threads are filtered out of `ExistingComments`; only human prose reaches the prompt.
  - `CreateThreadAsync` / `GetPullRequestsAsync` have positional-arg quirks in v19 — `project` must be passed exactly as shown (see comments marked `Fix CS1744`).
  - **Comment anchoring**: `CommentThreadContext.RightFileStart` is a position in the *right-hand* side of a diff, and `CommentIterationContext.SecondComparingIteration` is what says which iteration that right-hand side is. These must agree with the iteration the diff was read from, or Azure DevOps tracks the position forward from the iteration it was told and the comment lands on the wrong line. Both `ChangeTrackingId` and the iteration fields are `short` here, while `GitPullRequestChange.ChangeTrackingId` is `int` — clamp, do not wrap.
- **`DiffBuilder`** (static) — produces the line-numbered **hunk** diff (`<sign><lineNumber> | <content>`). Only changed regions plus `ContextLines` of surrounding code are emitted; omitted stretches are marked `@@ ... N unchanged line(s) not shown ... @@` and a cut-off diff ends with an explicit truncation marker. **Never silently truncate a file here** — the previous 300-line cap made everything past line 300 look deleted, which was the single largest source of false positives. A file too large to diff is failed outright instead.
- **`ReviewHelpers`** (static) — single source of truth for the review system prompt, prompt builder, and JSON response parser shared by all providers. The Swedish-text / English-code-example rule lives in `SystemPrompt` here, as does the "you are seeing a partial view" rule that stops the model reporting absent-looking code as missing.
- **Extracting JSON from a model reply** (`ReviewHelpers.TryFindCommentArray`) — must find a *balanced* array and prove it deserializes into review comments before accepting it. **Never fall back to "first `[` to last `]`".** Model prose is full of brackets: a reasoning trace discussing `string[] GetOverridingArray()` begins `[] GetOverridingArray(string key)`, which that slice hands straight to the JSON parser. The resulting `'G' is invalid after a single JSON value` then buries the real failure, which is that the model never answered. A recovery path must never make the diagnosis worse than no recovery at all.
- **`ReviewValidator`** — post-response grounding checks, at zero token cost: drops findings on files not in the PR, findings whose `Evidence` quote is not in the diff, duplicates, repeats of existing human comments, and low-confidence guesses; re-anchors a comment's line number onto the line its evidence actually came from. Has a safety valve: if *no* finding in a batch quotes anything, evidence filtering is skipped rather than silencing the whole review (weaker models ignore the field).
- **`IReviewService`** — common interface (`ReviewPullRequestAsync`) implemented by all providers.
- **`ClaudeReviewService`** — wraps the official `Anthropic` SDK. Response parsing is tolerant of malformed JSON (returns `[]` on failure) via the shared helper.
- **`OllamaReviewService`** — uses a plain `HttpClient` against the Ollama `/api/generate` endpoint (`stream: false`, `format: "json"`). Works against local Ollama (`http://localhost:11434/api`) or the ollama.com cloud (`https://ollama.com/api`); when `ApiKey` is set it is sent as a `Bearer` token. Default model: `glm-5.2:cloud`. Implements `IDisposable` (owns the `HttpClient`).
- **`LiveReviewDisplay`** — the live per-batch thinking/answer table. Updates arrive from several parallel requests, so it is lock-guarded and repainted on a timer rather than per token; redrawing on every streamed delta costs more than the review.
- **Streaming** is implemented per provider because the transports differ: newline-delimited JSON for Ollama (`thinking` / `response` fields), server-sent events for Bifrost (`delta.reasoning_content` / `delta.content`, plus `stream_options.include_usage` so the gateway still records cost), and typed events from the Anthropic SDK's `CreateStreaming`. Batch identity is stamped onto the progress stream by `PullRequestReviewer`, never stored on the service — one service instance is shared by every batch, so mutable state there would race.
- **`ReviewOutputService`** — pure presentation/persistence; no external calls. `FormatCommentForAzureDevOps` and `GetLanguageHint` are shared by the post-back and file-output paths.

### Models (`PrReviewBot/Models/`)

- `PullRequestInfo` + `ChangedFile` (note `ChangedFile.FileType` is derived from the extension).
- `ReviewComment` + `CommentSeverity` enum (`Info`, `Warning`, `Critical`), plus `Evidence` (the verbatim diff line the finding is about) and `Confidence` (1-5).
- The `IsAdditionalObservation` flag distinguishes comments on PR-changed lines vs. context lines. Only non-additional comments at or above `Review:MinConfidenceToPost` are posted back to Azure DevOps; everything else stays in the saved report.
- `RepoContextFile` and `SkippedFile` carry the repository conventions and the deliberately-unreviewed files into the prompt.

## Key conventions

- **Review text language**: the system prompt instructs the model to write `issue` and `suggestion` in **Swedish**, while `codeExample` stays in English. Preserve this when editing the prompt.
- **Never let an incomplete review look like a clean one.** This has been got wrong twice: once by returning `[]` from a failed parse, once by printing "No issues found — looks good" after a partial review. `DisplayReview` and `SaveReviewToFile` both take the unreviewed-file list and qualify their output. Any new output path must do the same.
- **False positives are the priority.** When changing the prompt or the diff format, the guiding rule is that the model must never be given reason to reason about code it cannot see. Anything that makes the input look complete when it is not (silent truncation, placeholder diff text, dropped skip-lists) will produce confident, wrong findings.
- **Temperature**: not set for Claude — it is deprecated on the Anthropic API and models after Opus 4.6 reject any value but `1.0`. Ollama pins it to `0`; Bifrost sends it only when `Bifrost:Temperature` is configured, since the gateway may route to an Anthropic model.
- **Severity ordering**: `Critical` > `Warning` > `Info`. Emoji mapping: 🔴 / 🟡 / 🔵 is used consistently across terminal, file, and Azure DevOps output.
- **String formatting**: use `CultureInfo.InvariantCulture` with `StringBuilder.AppendLine`/`AppendLine` overloads (seen throughout the services). `CA1305` is a warning.
- **C# style** (enforced by `.editorconfig`):
  - File-scoped namespaces (`csharp_style_namespace_declarations = file_scoped`, `IDE0161` warning).
  - **Do not use `var`** — `csharp_style_var_*` are `false:warning`. Use explicit types.
  - Always use braces, even for single-line bodies (`IDE0011` warning).
  - Private/internal fields are `_camelCase`; `const` fields are `PascalCase`.
  - `dotnet_sort_system_directives_first = true`.
  - Newline before all braces (Allman style); one initializer element per line.
- **Comments in code**: existing code uses inline comments to document SDK/API quirks (e.g. `Fix CS1744`, `Fix CS1061`). Keep these when editing the surrounding code — they explain non-obvious library behavior.
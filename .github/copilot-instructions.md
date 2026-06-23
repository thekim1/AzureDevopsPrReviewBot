# Copilot Instructions

## Project overview

PR Review Bot is a .NET 10 console app that fetches pull requests assigned to you from Azure DevOps, sends their diffs to Anthropic Claude for review, displays the results in the terminal (Spectre.Console), optionally posts the comments back to Azure DevOps, and saves a markdown report to disk.

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
- Strongly-typed binding lives in `Config/AppSettings.cs` (`AppSettings` → `AzureDevOpsSettings`, `ClaudeSettings`). Add new settings here when extending config.

## Architecture

The flow is linear and orchestrated entirely in `Program.cs` (top-level statements):

1. `AzureDevOpsService.GetAllActivePullRequestsAsync()` — connects via `VssConnection`, enumerates repos in the project, filters disabled repos (they throw `TF401019`), returns all active non-draft PRs and marks each with `IsAssignedToMe` (current user is among its reviewers). `GetAssignedPullRequestsAsync()` is a thin filter over this. Program.cs partitions the list into "Assigned to you" and "Other PRs" groups for the selection prompt.
2. User selects review provider (Claude or Ollama) via a Spectre.Console `SelectionPrompt`; the chosen `IReviewService` is used for the rest of the run.
3. User selects PR(s) via a Spectre.Console `MultiSelectionPrompt`.
4. `IReviewService.ReviewPullRequestAsync()` — builds a prompt from the PR diff and asks the configured provider for a JSON array of comments, then parses it.
5. `ReviewOutputService` — renders to terminal and writes a markdown report under `reviews/` (next to the build output, via `AppContext.BaseDirectory`).
6. On confirmation, comments are posted back via `AzureDevOpsService.PostCommentToPrAsync` (inline threads with file/line context, or general PR threads when no line).

### Services (`PrReviewBot/Services/`)

- **`AzureDevOpsService`** — owns all Azure DevOps REST interaction. Note the non-obvious bits:
  - The reviewer identity is obtained from `_connection.AuthorizedIdentity.Id` after `ConnectAsync()` — there is no `GetSelfAsync` in client v19.
  - Diffs are computed **in-process** with a custom LCS algorithm (`ComputeLineDiff`), not via the API. Files are capped at 300 lines and 20 changed files per PR; only "code" extensions are considered (see `IsCodeFile`).
  - `CreateThreadAsync` / `GetPullRequestsAsync` have positional-arg quirks in v19 — `project` must be passed exactly as shown (see comments marked `Fix CS1744`).
- **`ReviewHelpers`** (static) — single source of truth for the review system prompt, prompt builder, and JSON response parser shared by all providers. The Swedish-text / English-code-example rule lives in `SystemPrompt` here.
- **`IReviewService`** — common interface (`ReviewPullRequestAsync`) implemented by all providers.
- **`ClaudeReviewService`** — wraps the official `Anthropic` SDK. Response parsing is tolerant of malformed JSON (returns `[]` on failure) via the shared helper.
- **`OllamaReviewService`** — uses a plain `HttpClient` against the Ollama `/api/generate` endpoint (`stream: false`, `format: "json"`). Works against local Ollama (`http://localhost:11434/api`) or the ollama.com cloud (`https://ollama.com/api`); when `ApiKey` is set it is sent as a `Bearer` token. Default model: `glm-5.2:cloud`. Implements `IDisposable` (owns the `HttpClient`).
- **`ReviewOutputService`** — pure presentation/persistence; no external calls. `FormatCommentForAzureDevOps` and `GetLanguageHint` are shared by the post-back and file-output paths.

### Models (`PrReviewBot/Models/`)

- `PullRequestInfo` + `ChangedFile` (note `ChangedFile.FileType` is derived from the extension).
- `ReviewComment` + `CommentSeverity` enum (`Info`, `Warning`, `Critical`).
- The `IsAdditionalObservation` flag distinguishes comments on PR-changed lines vs. context lines; only non-additional comments are posted back to Azure DevOps.

## Key conventions

- **Review text language**: Claude's system prompt instructs the model to write `issue` and `suggestion` in **Swedish**, while `codeExample` stays in English. Preserve this when editing the prompt.
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
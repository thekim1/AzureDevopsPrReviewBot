# PR Review Bot

An AI-powered pull request review tool for **Azure DevOps**. It reviews code changes with the LLM
of your choice — **Anthropic Claude**, **Ollama** (local or cloud), or anything behind a
**Bifrost** gateway — and gives you actionable feedback in your terminal, optionally posted back
to the pull request.

Its design goal is **reviews you can trust**: findings the model cannot ground in the diff are
discarded before you see them, and a review that did not complete is never presented as a clean
one. See [Review accuracy](#review-accuracy).

![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet) ![Claude](https://img.shields.io/badge/Claude-Anthropic-orange) ![Ollama](https://img.shields.io/badge/Ollama-local%20or%20cloud-lightgrey) ![Bifrost](https://img.shields.io/badge/Bifrost-gateway-brightgreen) ![Azure DevOps](https://img.shields.io/badge/Azure-DevOps-blue)

---

## Features

- 🔍 **Fetches PRs assigned to you** across all repositories in an Azure DevOps project
- 🤖 **Bring your own model** — Claude, Ollama, or any provider behind a Bifrost gateway, chosen at startup
- 👀 **Watch it think** — output streams live, with per-batch thinking and answer counters
- 🎛️ **Interactive selection** — choose one or multiple PRs to review in a single run
- 📊 **Severity-rated comments** — Critical 🔴, Warning 🟡, Info 🔵
- 🧭 **Repo-aware** — reads the reviewed repository's own agent instructions (`AGENTS.md`, `CLAUDE.md`, `copilot-instructions.md`), architecture notes and style config, so project conventions are not reported as defects
- 🛡️ **Grounding checks** — every finding must quote the diff line it came from; findings that quote code the model was never shown are discarded locally, before you ever see them
- 💬 **Post comments back** to Azure DevOps with a single confirmation
- 🧩 **Handles large PRs** — reviewed in parallel batches, so one oversized request cannot sink the run
- 💾 **Saves reviews to disk** as markdown files for later reference
- 🖥️ **Rich terminal UI** powered by Spectre.Console

---

## Prerequisites

| Requirement | Notes |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Only to build from source — the [prebuilt downloads](#option-a-download-a-release-no-net-needed) bundle their own runtime |
| Azure DevOps account | With access to the target project |
| Azure DevOps PAT | Personal Access Token with `Code (Read)` and `Code (Write)` scopes |
| A model provider | **One** of: an [Anthropic API key](https://console.anthropic.com/), an [Ollama](https://ollama.com) instance (local needs no key), or a [Bifrost](https://github.com/maximhq/bifrost) gateway |

---

## Getting Started

### Option A: Download a release (no .NET needed)

Grab the zip for your platform from the
[latest release](https://github.com/thekim1/AzureDevopsPrReviewBot/releases/latest):

| File | Platform |
|---|---|
| `PrReviewBot-<version>-win-x64.zip` | Windows (Intel/AMD) |
| `PrReviewBot-<version>-linux-x64.zip` | Linux (Intel/AMD), including WSL |

Unpack it and you get the executable, the `appsettings.json` you need to edit, this README,
and the `reviews/` folder your saved reviews land in. The builds are self-contained, so there
is no runtime to install.

```bash
# Linux
unzip PrReviewBot-1.0.0-linux-x64.zip
cd PrReviewBot
nano appsettings.json    # see Configure settings, below
chmod +x PrReviewBot     # only if the executable bit did not survive the unzip
./PrReviewBot
```

On Windows, unpack the zip, edit `appsettings.json`, then run `PrReviewBot.exe`.

`appsettings.json` is read from the folder holding the executable, so the tool works from any
working directory. A second `appsettings.json` in the directory you launch from is layered on
top if present, which lets one install serve several organisations.

Then fill in [Configure settings](#configure-settings) below.

### Option B: Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/thekim1/AzureDevopsPrReviewBot.git
cd AzureDevopsPrReviewBot
```

Fill in [Configure settings](#configure-settings) below, then:

```bash
cd PrReviewBot
dotnet run
```

### Configure settings

Open `appsettings.json` — next to the executable in a downloaded release, or
`PrReviewBot/appsettings.json` in a source checkout — and fill in your values:

```json
{
  "AzureDevOps": {
    "OrganizationUrl": "https://dev.azure.com/YOUR_ORG",
    "Project": "YOUR_PROJECT",
    "PersonalAccessToken": "YOUR_PAT_HERE",
    "ReviewerEmail": "you@example.com"
  },
  "Claude": {
    "ApiKey": "YOUR_ANTHROPIC_API_KEY",
    "Model": "claude-sonnet-4-6"
  },
  "Ollama": {
    "ApiKey": "YOUR_OLLAMA_API_KEY",
    "BaseUrl": "https://ollama.com/api",
    "Model": "glm-5.2:cloud"
  },
  "Bifrost": {
    "ApiKey": "YOUR_BIFROST_API_KEY",
    "BaseUrl": "http://localhost:8080/v1",
    "Model": "anthropic/claude-sonnet-4-5"
  }
}
```

Everything under `Review` is optional — the defaults are tuned to work out of the box. Add it only
when you want to change how much context the model sees or how hard its output is filtered; see
[Review Tuning](#review-tuning-review).

> ⚠️ **Do not commit secrets.** Use [.NET User Secrets](#using-net-user-secrets-recommended) or environment variables instead.

At startup the app asks which provider to use (**Claude**, **Ollama**, or **Bifrost**). All provider API keys can be configured; only the selected provider is used per run.

**Bifrost** ([maximhq/bifrost](https://github.com/maximhq/bifrost)) is an LLM gateway that routes to any provider. Models are addressed as `provider/model`, e.g. `anthropic/claude-sonnet-4-5` or `openai/gpt-4o`.

---

## Configuration

The app loads configuration from the following sources in order (later sources override earlier ones):

1. `appsettings.json` next to the executable — the copy shipped in a release zip
2. `appsettings.json` in the directory you launch from, if one exists there
3. [.NET User Secrets](#using-net-user-secrets-recommended)
4. Environment variables

### Configuration Reference

| Key | Description |
|---|---|
| `AzureDevOps:OrganizationUrl` | Your Azure DevOps org URL, e.g. `https://dev.azure.com/myorg` |
| `AzureDevOps:Project` | The Azure DevOps project name |
| `AzureDevOps:PersonalAccessToken` | PAT with `Code (Read)` + `Code (Write)` scopes |
| `AzureDevOps:ReviewerEmail` | Your email — used to identify PRs assigned to you |
| `Claude:ApiKey` | Your Anthropic API key |
| `Claude:Model` | Claude model to use (default: `claude-sonnet-4-6`) |
| `Ollama:ApiKey` | Your Ollama API key (for ollama.com cloud; leave empty for local Ollama) |
| `Ollama:BaseUrl` | Ollama API base URL (default: `https://ollama.com/api`; use `http://localhost:11434/api` for local) |
| `Ollama:Model` | Ollama model to use (default: `glm-5.2:cloud`) |
| `Bifrost:ApiKey` | Your Bifrost API key (e.g. a Bifrost virtual key; leave empty for an unauthenticated local gateway) |
| `Bifrost:BaseUrl` | Bifrost OpenAI-compatible base URL (default: `http://localhost:8080/v1`) |
| `Bifrost:Model` | Model in `provider/model` form (default: `anthropic/claude-sonnet-4-5`) |
| `Bifrost:ResponseFormat` | Sent as `response_format: {"type": ...}`. Defaults to `json_object` — the OpenAI-style analogue of the native Ollama path's `format: "json"`. **Hybrid reasoning models (GLM, Qwen, DeepSeek) need this**: without a JSON constraint they emit a thinking block that the gateway reports as `reasoning_content`, leaving `content` empty and the review looking like it found nothing. Set to empty to disable if your gateway rejects the parameter |
| `Bifrost:ExtraParameters` | Extra top-level fields merged into the request body verbatim. Only useful for parameters the OpenAI schema already defines — **Bifrost drops fields it does not recognise**, so Ollama-native switches like `think` never reach the model (verified against a live gateway). Empty this first if a request starts failing |
| `Bifrost:Temperature` | Optional sampling temperature. Unset by default — Anthropic models reject any value but `1.0`. Set to `0` when routing to OpenAI, Ollama, or another provider that honours it |

### Review Tuning (`Review:*`)

These control how much the reviewer sees and how hard its output is filtered. The defaults are
tuned so the prompt is *more* useful without being bigger — see [Review accuracy](#review-accuracy).

| Key | Default | Description |
|---|---|---|
| `Review:ContextLines` | `12` | Unchanged lines kept either side of a change. The main accuracy dial: too few and the model invents "missing" null checks that live just outside the window |
| `Review:WholeFileMaxLines` | `300` | Files up to this many lines are sent whole rather than as hunks — the one view where the model may rely on what is absent from a file |
| `Review:ScopeMaxLines` | `120` | Each change in a larger file is widened to its enclosing block (method, if-block, template element) when that block is at most this many lines. `0` = context lines only |
| `Review:FileHeaderLines` | `30` | Opening lines of every file not sent whole — imports, type declaration, injected fields, constructor. For `.vue`, counted from the `<script>` block. `0` = off |
| `Review:MaxFilesPerPr` | `20` | Files reviewed per PR. Over the limit, source is kept first, then configuration, tests, styles and docs; the rest are listed as explicitly not shown |
| `Review:ExcludedPaths` | `[]` | Extra globs never to review, on top of the built-in lockfiles, generated, minified, binary and vendored files. `**` spans folders, `*` stays in one; no leading `/` matches at any depth. E.g. `["**/Generated/**", "/src/api-client/**"]` |
| `Review:MaxFilesPerRequest` | `4` | Files per request. A PR is reviewed in several requests — see [Large pull requests](#large-pull-requests) |
| `Review:MaxDiffCharsPerRequest` | `30000` | Second bound on a request, so one huge file cannot fill a batch past the file-count limit |
| `Review:ShowThinking` | `true` | Stream the model's output and show a live table of what each batch is thinking and answering. Supported by all three providers. Usage is still reported to the gateway, so cost tracking is unaffected. Set `false` for the plain spinner |
| `Review:ThinkingPreviewChars` | `220` | How much of the current thought to keep on screen per batch |
| `Review:MaxParallelRequests` | `3` | Batches in flight at once. Keep it at or below the gateway's upstream connection limit — for Bifrost that is the provider's `max_conns_per_host` — or the surplus requests just queue |
| `Review:MaxParallelDevOpsRequests` | `8` | File reads and listings in flight against Azure DevOps at once, across everything being loaded. Lower it if Azure DevOps starts throttling |
| `Review:StreamIdleTimeoutSeconds` | `180` | A streamed request whose model produces no output for this long is abandoned and its batch reported as failed. Keep-alive heartbeats do not count as output |
| `Review:MaxRequestMinutes` | `15` | Hard limit on one request, start to finish — catches a model that trickles output slowly enough to never trip the idle timeout |
| `Review:WarmPrefixCache` | `true` | Run the first batch alone so the shared prefix lands in the provider's prompt cache before the rest go out. Costs ~one round trip, makes every later batch far cheaper. Set `false` to favour speed over cost |
| `Review:ScopeExistingCommentsToBatch` | `true` | Send only the existing PR comments that concern the batch's own files (plus PR-level ones) |
| `Review:MaxDiffLinesPerFile` | `400` | Cap on emitted diff lines per file, applied after hunking |
| `Review:MaxOutputTokens` | `16384` | Budget for the model's *answer*. Reasoning models bill their thinking against this too, so 4096 can be exhausted before a single character of JSON is emitted — raise it if reviews come back empty or cut off mid-array |
| `Review:IncludeRepoContext` | `true` | Read the repo's agent/architecture/style files from the target branch |
| `Review:MaxRepoContextChars` | `12000` | Total budget for that bundle |
| `Review:RequireEvidence` | `true` | Discard findings whose quoted line does not appear in the diff |
| `Review:MinConfidenceToKeep` | `2` | Findings below this self-reported confidence (1–5) are discarded outright |
| `Review:MinConfidenceToPost` | `4` | Findings below this stay in the saved report but are never posted to Azure DevOps |

### Using .NET User Secrets (Recommended)

Keeps secrets out of source control:

```bash
cd PrReviewBot
dotnet user-secrets init
dotnet user-secrets set "AzureDevOps:PersonalAccessToken" "YOUR_PAT"
dotnet user-secrets set "Claude:ApiKey" "YOUR_ANTHROPIC_API_KEY"
dotnet user-secrets set "Ollama:ApiKey" "YOUR_OLLAMA_API_KEY"
dotnet user-secrets set "Bifrost:ApiKey" "YOUR_BIFROST_API_KEY"
```

### Using Environment Variables

```bash
# Windows (PowerShell)
$env:AzureDevOps__PersonalAccessToken = "YOUR_PAT"
$env:Claude__ApiKey = "YOUR_ANTHROPIC_API_KEY"
$env:Ollama__ApiKey = "YOUR_OLLAMA_API_KEY"
$env:Bifrost__ApiKey = "YOUR_BIFROST_API_KEY"

# Linux / macOS
export AzureDevOps__PersonalAccessToken="YOUR_PAT"
export Claude__ApiKey="YOUR_ANTHROPIC_API_KEY"
export Ollama__ApiKey="YOUR_OLLAMA_API_KEY"
export Bifrost__ApiKey="YOUR_BIFROST_API_KEY"
```

> Note: Use double underscores (`__`) as the separator for nested keys in environment variables.

---

## How It Works

```
┌─────────────────────────────────────────────────────────────┐
│  1. Connect to Azure DevOps and fetch active PRs assigned   │
│     to you across all repositories in the project           │
│                                                             │
│  2. Select which PR(s) to review from an interactive list   │
│                                                             │
│  3. Changed regions of each file (plus surrounding context) │
│     are sent to the model, together with the repository's   │
│     own conventions and any existing PR comments            │
│                                                             │
│  3b. Large PRs are split into batches and reviewed in       │
│     parallel, streaming their progress to the terminal      │
│                                                             │
│  3c. Findings are checked against the real diff locally:    │
│     invented files, unquotable evidence, duplicates and     │
│     low-confidence guesses are dropped, and the rest are    │
│     re-anchored to the line their evidence came from        │
│                                                             │
│  4. Review results are displayed in the terminal with       │
│     severity ratings and suggested code fixes               │
│                                                             │
│  5. Optionally post comments directly to the PR in          │
│     Azure DevOps                                            │
│                                                             │
│  6. Review is saved to a local file in /reviews/            │
└─────────────────────────────────────────────────────────────┘
```

### Severity Levels

| Severity | Icon | When Used |
|---|---|---|
| **Critical** | 🔴 | Security vulnerabilities, data loss, crashes, serious bugs |
| **Warning** | 🟡 | Performance problems, bad patterns, maintainability issues |
| **Info** | 🔵 | Style improvements, minor suggestions |

Each finding also carries a **confidence** of 1–5: how sure the model is that this is a real
defect rather than an inference about code it could not see. Low-confidence findings are kept in
the saved report but are not posted to Azure DevOps (see `Review:MinConfidenceToPost`).

---

## Review accuracy

Most false positives in an LLM code review come from the model reasoning about code it was never
shown. Four things keep that in check:

1. **The right amount of each file.** Small files (`WholeFileMaxLines`) are sent whole and marked
   as complete. In larger files each change is widened to the block it sits in — the whole method
   rather than a fixed window, which is where the "missing" null check or dispose usually turns out
   to be — plus the file's opening lines (imports, fields, constructor). Omitted stretches are
   marked `@@ ... N unchanged line(s) not shown ... @@`. If widening would exceed
   `MaxDiffLinesPerFile`, the file falls back to plain `ContextLines` hunks rather than cutting
   changes off. Files that are too large to diff safely are reported as *not reviewed* rather than
   diffed on a truncated prefix — a truncated prefix makes the entire tail of a file look deleted.
2. **Repository context.** The reviewed repo's own `AGENTS.md` / `CLAUDE.md` /
   `copilot-instructions.md`, architecture notes, README and `.editorconfig` are read from the PR's
   **target branch** and marked authoritative, so a deliberate project convention is not reported
   as a defect.
3. **A partial-view rule in the prompt.** The model is told explicitly never to report something as
   missing, unregistered, unused or never called unless the shown lines prove it.
4. **Local grounding checks** (`ReviewValidator`). Every finding must quote the source line it
   refers to. After the response comes back, each quote is looked up in the diff that was actually
   sent. Findings on files that are not in the PR, or quoting code that was never shown, are
   discarded; a quote that *is* found also fixes the comment's line number, so comments land on
   the right line in Azure DevOps. When the quoted line occurs more than once, an exact match wins,
   then the occurrence nearest the line the model named. This costs no tokens.

The terminal prints what was filtered on each run, so you can tell a quiet model from an
over-aggressive filter.

### A partial review is never reported as a clean one

If some batches fail, the terminal and the saved report both say so, name the files that went
unreviewed, and state plainly that their silence is not approval. "No issues found — looks good"
appears only when every file was actually looked at.

### A failed review is never reported as a clean one

If a provider returns something that cannot be read as a review — an empty message, or JSON that
stops mid-array — the run reports the failure, writes the raw reply to
`reviews/..._FAILED_raw-response.txt`, and moves to the next PR. It never shows
"No issues found". An empty finding list means the model read the diff and found nothing; those
are different outcomes and must not look alike.

The most common cause is a reasoning model exhausting `Review:MaxOutputTokens` on thinking before
it emits any answer. The error message reports `finish_reason` and the token split so you can tell
that apart from a genuine refusal.

### Watching the model work

With `Review:ShowThinking` (on by default) the review streams, and each batch gets a row showing
how much it has thought, how much it has answered, and the tail of its current thought:

```
╭─────────────────┬────────────┬─────────┬────────────────────────────────────╮
│ Part            │   Thinking │  Answer │ Latest thought                     │
├─────────────────┼────────────┼─────────┼────────────────────────────────────┤
│ ● 1. 3 files    │ ~4,100 tok │       — │ ...line 42 dereferences it before  │
│ ✓ 2. Helper.cs  │ ~1,250 tok │ ~310 tok│                                    │
│ ✗ 3. 2 files    │ ~16,000 tok│       — │                                    │
╰─────────────────┴────────────┴─────────┴────────────────────────────────────╯
```

This is diagnostic, not decoration. A reasoning model can think for minutes before producing any
answer, and a spinner cannot tell that apart from a hang. A row whose **Thinking** column climbs
while **Answer** stays at `—` is a request heading for an exhausted output budget; the column turns
red past 20k tokens so you can see it coming rather than reading about it afterwards.

All three providers stream: Bifrost and Ollama over their HTTP APIs, Claude through the Anthropic
SDK's `CreateStreaming`. Models that expose their reasoning separately (Claude's thinking blocks,
Ollama's `thinking` field, `reasoning_content` through a gateway) show it in the **Thinking**
column; models that do not simply fill the **Answer** column instead.

For Bifrost, streaming sends `stream_options.include_usage`, so the gateway still records
prompt/completion tokens and cost for every call — nothing is lost from the billing picture. This
matters if you are routing everything through one gateway precisely to keep AI spend accountable.

### Large pull requests

A PR is **not** reviewed in one request. It is split into batches of at most
`Review:MaxFilesPerRequest` files (and `Review:MaxDiffCharsPerRequest` characters), reviewed
separately, and the findings merged.

A single request covering a whole PR fails badly on large changes. Measured on a real 20-file PR:
97,403 characters of diff went in, the model spent **all 16,384** completion tokens on internal
reasoning, and returned an empty message after 122 seconds — `finish_reason: "length"`, zero
characters of answer, and it was still second-guessing findings it had already made when the
budget ran out. That same PR now splits into six requests of 2.5k–29k characters.

Batching also makes failure partial: a batch that fails costs its own files, and the run says
exactly which files went unreviewed instead of silently returning fewer findings.

**Related files share a batch.** A class travels with its interface and its tests (`Foo.cs`,
`IFoo.cs`, `FooTests.cs`), and batches are filled folder by folder, so the request that sees a
changed signature usually also sees its callers.

Batches are **independent requests, so they run concurrently** (`Review:MaxParallelRequests`).
Running them one after another would just multiply wall-clock time by the batch count. The
smallest batch goes first (it warms the cache, and the others wait on it), then the rest largest
first, so the slowest request is never the one left to start last.

The cost of batching is that every request repeats the same prefix — system prompt, repository
context, PR metadata. Three things keep that in check:

- **Only the first matching file per context group is read.** Repos often carry `AGENTS.md`,
  `CLAUDE.md` *and* `copilot-instructions.md`, where two of them just say "see the third";
  sending all three pays three times for one document, in every batch.
- **Existing PR comments are scoped to the batch's own files.** A comment about a file the model
  cannot see is noise it has to read and pay for.
- **The first batch runs alone to warm the provider's prompt cache** (`Review:WarmPrefixCache`).
  Cached prompt tokens measured 5× cheaper on Ollama cloud, and every batch after the first reads
  the shared prefix from cache. Turn it off to trade cost for wall-clock time.

If reviews are still slower than you want, `Review:MaxFilesPerRequest` is the lever: bigger batches
mean fewer repeated prefixes and fewer round trips, at the risk of the model running out of output
budget again on a big PR. Raise it gradually and watch for partial-review warnings.

> **Ollama-native parameters cannot reach the model through Bifrost.** Bifrost translates to each
> provider's schema and drops fields it does not recognise, and its passthrough endpoints cover only
> OpenAI, Anthropic, Azure and Gemini/Vertex — not Ollama. Confirmed against a live gateway: a
> request sent with `{ "think": false }` was logged by Bifrost as
> `{"max_completion_tokens": …, "response_format": {"type":"json_object"}}` — the `think` field was
> gone. `Bifrost:ExtraParameters` is therefore only useful for parameters the OpenAI schema already
> defines.
>
> So for a hybrid reasoning model whose thinking you need to switch off, **use the native Ollama
> provider rather than the gateway**. Bifrost remains the right route for everything else.
>
> A failure dump records the parameters this app sent (prompt omitted) above the response, so you
> can tell "the setting never left this app" from "the gateway dropped it".
>
> Note that **shrinking batches does not help here**: measured on glm-5.3-flash, a batch of three
> small files (6,020 prompt tokens) still spent all 16,384 completion tokens reasoning without
> reaching an answer. How much this model deliberates is driven by how hard the code is, not by how
> much of it you send — which is why `Review:MaxOutputTokens` is the lever that matters.

> **If the same model works via Ollama directly but not through Bifrost**, the difference is the
> JSON constraint: the native path sends `format: "json"`, so the gateway path must send
> `Bifrost:ResponseFormat` (default `json_object`) to match. Because `json_object` forbids a
> top-level array, the model answers `{"comments": [...]}` — the parser accepts that, a bare
> array, and either wrapped in a code fence.

### Why comments land on the right line

Two things beyond the model's own line numbers matter here:

- **The diff is taken from the PR iteration's commits**, not the branch tips: new content from the
  iteration's source commit, old content from its `CommonRefCommit` (the merge base). Diffing
  against the target branch *tip* instead would drag in every unrelated commit merged into the
  target since the PR branched, and report other people's work as this PR's deletions.
- **Comments are posted against that same iteration.** `RightFileStart` is a position on the right
  side of a diff, and `SecondComparingIteration` declares which iteration that right side is. If
  they disagree, Azure DevOps maps the position forward from the iteration it was told to the one
  being displayed, and the comment drifts by however many lines were added or removed in between.

Because hunk diffs are much smaller than whole files, the repository context is roughly paid for
by the tokens they save; on this repository's own history the diff payload shrank ~38%.

---

## Project Structure

```
PrReviewBot/
├── Config/
│   └── AppSettings.cs          # Strongly-typed configuration classes
├── Models/
│   ├── PullRequestInfo.cs      # PR, changed files, repo context, skipped files
│   ├── ReviewComment.cs        # Review comment + severity, evidence, confidence
│   └── ReviewProgress.cs       # Live streaming update
├── Services/
│   ├── AzureDevOpsService.cs   # Azure DevOps API integration
│   ├── ClaudeReviewService.cs  # Anthropic Claude (official SDK)
│   ├── OllamaReviewService.cs  # Ollama API (local or ollama.com)
│   ├── BifrostReviewService.cs # Bifrost LLM gateway (OpenAI-compatible)
│   ├── IReviewService.cs       # Common provider interface
│   ├── PullRequestReviewer.cs  # Splits a PR into batches, runs them in parallel
│   ├── DiffBuilder.cs          # Line-numbered hunk diff generation
│   ├── ReviewHelpers.cs        # Shared review prompt + response parsing
│   ├── ReviewValidator.cs      # Grounding checks against the real diff
│   ├── ReviewFailedException.cs# A review that did not complete, never a silent []
│   ├── LiveReviewDisplay.cs    # Live per-batch thinking/answer table
│   └── ReviewOutputService.cs  # Terminal display + file output
├── Program.cs                  # Entry point + interactive CLI flow
└── appsettings.json            # Configuration file
```

---

## Azure DevOps PAT Setup

1. Go to **Azure DevOps → User Settings → Personal Access Tokens**
2. Click **New Token**
3. Set an expiration date and select the following scopes:
   - **Code** → `Read`
   - **Code** → `Write` *(only needed if you want to post comments)*
4. Copy the generated token into your configuration

---

## Model Provider Setup

Pick **one** provider per run; the app asks at startup.

| Provider | Setup | Notes |
|---|---|---|
| **Claude** | [console.anthropic.com](https://console.anthropic.com/) → API key → `Claude:ApiKey` | Strongest reviews out of the box |
| **Ollama** | Local: run `ollama serve` and set `Ollama:BaseUrl` to `http://localhost:11434/api` (no key). Cloud: key from [ollama.com](https://ollama.com) | Sends `format: "json"`, which also keeps hybrid reasoning models answering rather than thinking indefinitely |
| **Bifrost** | Run the [gateway](https://github.com/maximhq/bifrost), set `Bifrost:BaseUrl` and a virtual key | Routes to any provider and centralises cost and usage tracking — useful when an organisation needs one place to account for AI spend |

### Anthropic API Key Setup

1. Sign in at [console.anthropic.com](https://console.anthropic.com/)
2. Navigate to **API Keys** and create a new key
3. Copy the key into your configuration

---

## Dependencies

| Package | Purpose |
|---|---|
| `Anthropic` | Official Anthropic SDK for Claude API |
| `Microsoft.TeamFoundationServer.Client` | Azure DevOps REST API client |
| `Microsoft.VisualStudio.Services.Client` | Azure DevOps authentication & connection |
| `Spectre.Console` | Rich terminal UI (colors, spinners, panels) |
| `Microsoft.Extensions.Configuration.*` | JSON + env var + user secrets config |
| `System.Data.SqlClient` | Not used directly — pinned to `4.8.6` only to override a vulnerable transitive of the Azure DevOps 19.x clients ([GHSA-98g6-xh36-x2p7](https://github.com/advisories/GHSA-98g6-xh36-x2p7)). Drop it when moving to the 20.x clients, which use `Microsoft.Data.SqlClient` |

> Ollama is accessed via plain `HttpClient` (no SDK dependency) against the
> `/api/generate` endpoint, so it works with both local Ollama and ollama.com.
> Bifrost is likewise accessed via plain `HttpClient` against its
> OpenAI-compatible `/v1/chat/completions` endpoint, so any provider behind
> the gateway works.
>
> Streaming is handled per transport: newline-delimited JSON for Ollama,
> server-sent events for Bifrost, and typed stream events for the Anthropic SDK.

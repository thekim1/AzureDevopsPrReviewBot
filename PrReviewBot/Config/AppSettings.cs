using System.Text.Json;

namespace PrReviewBot.Config;

public class AppSettings
{
    public AzureDevOpsSettings AzureDevOps { get; set; } = new();
    public ClaudeSettings Claude { get; set; } = new();
    public OllamaSettings Ollama { get; set; } = new();
    public BifrostSettings Bifrost { get; set; } = new();
    public ReviewSettings Review { get; set; } = new();
}

public class AzureDevOpsSettings
{
    public string OrganizationUrl { get; set; } = "";
    public string Project { get; set; } = "";
    public string PersonalAccessToken { get; set; } = "";
    public string ReviewerEmail { get; set; } = "";
}

public class ClaudeSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
}

public class OllamaSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://ollama.com/api";
    public string Model { get; set; } = "glm-5.3-flash:cloud";

    // How hard a reasoning model thinks, sent as Ollama's `think` field: one
    // of the model's own levels, or "true"/"false". Empty leaves the model's
    // default — which for glm-5.3-flash is its HIGHEST level, "max".
    //
    // The levels are per model; ask Ollama for them with
    //   curl https://ollama.com/api/show -d '{"model":"glm-5.3-flash"}'
    // (glm-5.3-flash: "low", "high", "max"). Use a listed name exactly: a
    // name the model does not list, such as "minimal", falls back to the
    // default instead of the nearest level. Leave empty for a model without
    // thinking, which may reject the field.
    public string Think { get; set; } = "";
}

// Bifrost LLM gateway (https://github.com/maximhq/bifrost). Uses its
// OpenAI-compatible endpoint, so any provider behind the gateway works.
// Models are addressed as "provider/model", e.g. "openai/gpt-4o" or
// "anthropic/claude-sonnet-4-5".
public class BifrostSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "http://localhost:8080/v1";
    public string Model { get; set; } = "anthropic/claude-sonnet-4-5";
    // Sent only when set. 
    public double? Temperature { get; set; }

    // The OpenAI-style analogue of Ollama's native `format: "json"`. Sent as
    // {"response_format": {"type": "<this>"}}.
    //
    // This matters for hybrid reasoning models (GLM, Qwen, DeepSeek): without a
    // JSON constraint they emit a thinking block first, which an
    // OpenAI-compatible layer reports as `reasoning_content` while leaving
    // `content` empty — the review then looks like it returned nothing. The
    // native Ollama path has always sent `format: "json"`, which is why the
    // same model works there and not through the gateway.
    //
    // Note that "json_object" forbids a top-level array, so the model answers
    // with {"comments": [...]}; the parser accepts either shape. Set to empty
    // to disable if your gateway or provider rejects the parameter.
    public string ResponseFormat { get; set; } = "json_object";

    // Sent as `reasoning_effort` when set — the OpenAI-schema way to ask a
    // reasoning model for less (or more) thinking, which Bifrost understands
    // and translates per provider. For an Ollama model the value must be one
    // of the model's own levels (see OllamaSettings.Think; glm-5.3-flash:
    // "low", "high", "max"). Empty leaves the model's default.
    //
    // To confirm it reaches the model, watch the Thinking column in the live
    // display: at "low" it should be a fraction of what it was.
    public string ReasoningEffort { get; set; } = "";

    // Extra top-level fields merged into the request body verbatim.
    //
    // Only useful for parameters the target gateway already understands.
    // Verified against a live Bifrost instance: it normalises requests into
    // each provider's own schema and silently drops fields it does not
    // recognise, and its native passthrough endpoints cover only OpenAI,
    // Anthropic, Azure and Gemini — not Ollama. A request sent with
    // { "think": false } was logged by the gateway as
    // {"max_completion_tokens": ..., "response_format": {...}} with the think
    // field gone.
    //
    // So Ollama-native switches cannot be delivered this way. Use the native
    // Ollama provider when you need them.
    //
    // Values are passed through as JSON, so booleans, numbers, strings and
    // nested objects all work. If a request starts failing, empty this first.
    public Dictionary<string, JsonElement> ExtraParameters { get; set; } = [];
}

// A set of interchangeable sources for one kind of repository context. Only
// the first path that exists is used.
public sealed record RepoContextGroup(string Name, string[] Paths)
{
    public RepoContextGroup() : this("", [])
    {
    }
}

public class ReviewSettings
{
    // Unchanged lines kept on either side of a changed region. This is the
    // main accuracy/cost dial: too few and the model invents missing null
    // checks or disposals that live just outside the window.
    public int ContextLines { get; set; } = 12;

    // Hunks closer together than this are merged into one, so the model never
    // sees two near-identical blocks separated by a pointless gap marker.
    public int HunkMergeDistance { get; set; } = 8;

    // Files up to this many lines are sent whole instead of as hunks. Hunks
    // save little on a small file, and a complete file is the one view where
    // the model can trust that something it does not see is really not there.
    public int WholeFileMaxLines { get; set; } = 300;

    // Each change in a larger file is widened to the block it sits in — the
    // whole method, if-block or template element — as long as that block is
    // at most this many lines. 0 turns it off and leaves only ContextLines.
    public int ScopeMaxLines { get; set; } = 120;

    // The first lines of every file not sent whole: imports, namespace, type
    // declaration, injected fields and constructor. For a .vue file, counted
    // from the <script> block. 0 turns it off.
    public int FileHeaderLines { get; set; } = 30;

    public int MaxFilesPerPr { get; set; } = 20;

    // Extra paths never to review, on top of the built-in lockfiles,
    // generated, minified, binary and vendored files. Globs: `**` spans
    // folders, `*` stays within one; a pattern without a leading slash matches
    // at any depth. Example: ["**/Generated/**", "/src/api-client/**", "*.sql"].
    public List<string> ExcludedPaths { get; set; } = [];

    // A PR is reviewed in several requests of at most this many files. One
    // request per PR does not survive a large change: the model must take in
    // every file before it can answer, and a reasoning model can spend its
    // entire output budget deliberating and return nothing. Smaller batches
    // bound the work per request and make a failure cost a few files instead
    // of the whole review.
    public int MaxFilesPerRequest { get; set; } = 4;

    // Second bound on a batch, so one very large file cannot smuggle a whole
    // batch's worth of tokens past the file-count limit.
    public int MaxDiffCharsPerRequest { get; set; } = 30000;

    // Cap on emitted diff lines per file, applied *after* hunking. When it
    // trips the file is explicitly marked as truncated in the prompt rather
    // than silently cut — silent truncation is a large false-positive source.
    public int MaxDiffLinesPerFile { get; set; } = 400;

    // The LCS diff is O(old * new) in memory. Files whose product exceeds this
    // are reported as "not reviewed" instead of being diffed on truncated
    // input, which would fabricate huge bogus deletions.
    public int MaxDiffCells { get; set; } = 4_000_000;

    // Fetch repository conventions (agent instructions, README, .editorconfig,
    // Directory.Packages.props, ...) from the PR's target branch and put them
    // in the prompt. Costs a few hundred tokens once per PR and removes most
    // "this violates our conventions" style false positives.
    public bool IncludeRepoContext { get; set; } = true;

    // Total budget for the whole bundle, deliberately kept below the tokens
    // that hunk diffs free up so the prompt does not get more expensive.
    public int MaxRepoContextChars { get; set; } = 12000;

    // Per-file cap inside the bundle, so one huge README or .editorconfig
    // cannot crowd out the agent instructions that matter most.
    public int MaxRepoContextFileChars { get; set; } = 8000;

    // Also read AGENTS.md / CLAUDE.md and .editorconfig from the folders the
    // changed files live in, not just the repository root. In a monorepo that
    // is where each sub-project keeps its own conventions.
    public bool IncludeScopedContext { get; set; } = true;

    // Budget for those folder-level files together, nearest folder first.
    public int MaxScopedContextChars { get; set; } = 8000;

    // Put the PR's linked work items (title, description, acceptance
    // criteria) in the prompt, so the change is reviewed against what it is
    // for. Needs the PAT to have Work Items (Read); without it the review
    // carries on without them.
    public bool IncludeWorkItems { get; set; } = true;

    // Characters per work item, description and acceptance criteria together.
    public int MaxWorkItemChars { get; set; } = 1500;

    public int MaxWorkItems { get; set; } = 5;

    // Put the PR's commit messages (merges left out) in the prompt.
    public bool IncludeCommitMessages { get; set; } = true;

    // When a PR spans several batches, send every batch a one-line-per-change
    // list of the public declarations each changed file adds or removes, so a
    // batch can check the code it sees against signatures changed elsewhere.
    public bool IncludeChangeSummary { get; set; } = true;

    public int MaxChangeSummaryChars { get; set; } = 4000;

    // Send outlines — declarations, no bodies — of the types and modules the
    // changed code uses, from files outside the PR. Answers most "might be
    // null / might not exist" questions the model would otherwise guess at.
    // Found by the Foo-in-Foo.cs convention for C# and by import path for
    // TypeScript and Vue.
    public bool IncludeReferencedDefinitions { get; set; } = true;

    // Definition files fetched per PR, the most referenced first.
    public int MaxReferencedDefinitions { get; set; } = 12;

    // Characters of outlines per batch, and per outline.
    public int MaxReferencedDefinitionChars { get; set; } = 6000;

    public int MaxDefinitionOutlineChars { get; set; } = 1500;

    public int MaxCommitMessages { get; set; } = 20;

    // Candidate paths grouped by what they tell the reviewer. Only the FIRST
    // file found in each group is used.
    //
    // This matters because agent-instruction files are usually pointers to one
    // another rather than independent content — a repo with AGENTS.md,
    // CLAUDE.md and copilot-instructions.md typically has the substance in one
    // and "see AGENTS.md" in the others. Sending all three pays three times for
    // one document, in every batch.
    //
    // Missing files are skipped silently, so listing generous alternatives
    // costs nothing.
    public List<RepoContextGroup> RepoContextFileGroups { get; set; } =
    [
        new("agent-instructions",
            ["/AGENTS.md", "/CLAUDE.md", "/.github/copilot-instructions.md",
             "/.cursorrules", "/.cursor/rules.md"]),
        new("architecture",
            ["/ARCHITECTURE.md", "/docs/architecture.md", "/docs/ARCHITECTURE.md", "/CONTRIBUTING.md"]),
        new("overview", ["/README.md"]),
        new("style-and-build",
            ["/.editorconfig", "/Directory.Packages.props", "/Directory.Build.props", "/global.json"])
    ];

    // How many batches may be in flight at once. The batches are independent
    // requests, so running them sequentially just multiplies the wall-clock
    // time by the batch count for no benefit.
    //
    // Keep this at or below the gateway's connection limit to the upstream
    // provider, or the surplus requests just queue and add latency without
    // adding throughput. For Bifrost that limit is the provider's
    // `max_conns_per_host`, which defaults to 3.
    public int MaxParallelRequests { get; set; } = 3;

    // How many file reads and listings may be in flight against Azure DevOps
    // at once, across everything the tool is loading. Fetching a PR reads two
    // versions of every changed file; one at a time, that was most of the wait
    // before a review could start. Lower it if Azure DevOps starts throttling.
    public int MaxParallelDevOpsRequests { get; set; } = 8;

    // Stream the model's output and show it live in the terminal.
    //
    // Worth having on for two reasons beyond looking good: a reasoning model
    // can think for minutes before producing any answer, and a spinner cannot
    // tell you whether that is work or a hang; and watching the thinking run
    // on while the answer stays empty is the clearest possible explanation of
    // why a request hit its output budget.
    //
    // Streaming still reports usage to the gateway (stream_options.include_usage
    // is sent), so cost and token tracking are unaffected.
    public bool ShowThinking { get; set; } = true;

    // A streamed request that sends nothing for this long is abandoned and its
    // batch reported as failed, instead of waiting forever. A reasoning model
    // streams its thinking continuously once it starts, so the only long quiet
    // stretch in a healthy request is the wait for the first token — which
    // includes queueing behind other requests at the gateway.
    public int StreamIdleTimeoutSeconds { get; set; } = 180;

    // Hard limit on one request from start to finish, whatever it is doing.
    // The idle timeout only catches a request that goes silent; this catches
    // one that trickles — a token every few seconds keeps the idle timeout
    // happy and would take hours to exhaust the output budget. Set well above
    // the slowest healthy request (about 3.5 minutes measured on glm-5.3-flash
    // with 19k tokens of thinking).
    public int MaxRequestMinutes { get; set; } = 15;

    // How many characters of the model's thinking to keep on screen per batch.
    public int ThinkingPreviewChars { get; set; } = 220;

    // Run the first batch alone before releasing the rest.
    //
    // Every batch opens with the same system prompt and repository context, so
    // after one request that prefix sits in the provider's prompt cache, where
    // it is far cheaper to read (5x, measured on Ollama cloud). Firing all
    // batches at once means they all miss the cache. Warming it costs roughly
    // one extra round trip and makes every later batch cheap.
    //
    // Set to false to favour wall-clock time over cost.
    public bool WarmPrefixCache { get; set; } = true;

    // Send only the existing PR comments that concern the files in the current
    // batch (plus PR-level ones). A comment about a file the model cannot see
    // is noise it has to read and pay for in every batch.
    public bool ScopeExistingCommentsToBatch { get; set; } = true;

    // Findings below this self-reported confidence (1-5) are kept in the
    // report but never posted back to Azure DevOps.
    public int MinConfidenceToPost { get; set; } = 4;

    // Drop findings below this confidence entirely, before they are shown.
    public int MinConfidenceToKeep { get; set; } = 2;

    // Output token budget for the model's answer. A reasoning model's thinking
    // is billed against this same budget, so it can burn the lot before
    // emitting a single character of JSON and return an empty message.
    //
    // This is a CAP, not a target: a request that answers promptly is charged
    // for what it used, so raising it costs nothing on the runs that work and
    // converts some of the runs that fail. Measured on glm-5.3-flash, a batch
    // of three small files still spent all of 16,384 tokens reasoning without
    // reaching an answer.
    public int MaxOutputTokens { get; set; } = 32768;

    // Drop a finding whose quoted evidence does not appear in the diff it
    // claims to come from. This is the strongest hallucination filter and
    // costs nothing at inference time.
    public bool RequireEvidence { get; set; } = true;
}

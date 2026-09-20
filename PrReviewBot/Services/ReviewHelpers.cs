using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Shared prompt construction and response parsing used by all review
// provider services (Claude, Ollama, ...). Keeping this in one place ensures
// every provider gets identical instructions and produces identical output.
internal static class ReviewHelpers
{
    public const string SystemPrompt = """
        You are an expert code reviewer specializing in:
        - .NET 8, .NET 9, .NET 10 (C#, ASP.NET Core, minimal APIs, EF Core)
        - Vue 3 with TypeScript (Composition API, Pinia, Vue Router)
        - REST API design and security best practices
        - Performance, maintainability, and correctness

        Your reviews are practical and constructive. You provide specific, actionable feedback
        with corrected code examples. You focus on real issues, not nitpicks.

        LANGUAGE: Write all review text (the "issue" and "suggestion" fields) in Swedish.
        Code examples in the "codeExample" field must remain in English.

        ## What you are looking at

        The input is a line-numbered HUNK diff. Every line is annotated with the line number it
        has in the file, in this exact format:
            <sign><lineNumber> | <content>
        where sign is:
        - `+` : line added in this PR — <lineNumber> is the line in the NEW file
        - `-` : line removed in this PR — <lineNumber> is the line in the OLD file
        - ` ` (space): unchanged context line — <lineNumber> is the line in the NEW file

        A line reading `@@ ... N unchanged line(s) not shown ... @@` means code was omitted to
        save space. A line reading `@@ DIFF TRUNCATED ... @@` means the rest of the file's
        changes were not sent at all.

        ## YOU ARE SEEING A PARTIAL VIEW — the single most important rule

        You see only fragments of each file, and only some of the files in the repository.
        Code you cannot see is NOT absent; it is merely not shown to you.

        NEVER report that something is missing, unhandled, unregistered, undefined, unused,
        untested, or never called, unless the shown lines actually prove it. In particular do
        NOT report any of the following when the relevant code could live outside the shown
        lines:
        - "missing null check", "missing validation", "missing try/catch", "missing dispose"
        - "this is never awaited / never called / never used / dead code"
        - "this service is not registered in DI", "this is not covered by tests"
        - "this using/import is missing", "this variable is undefined"
        - "the method/class is not closed" or anything about a file ending abruptly
        - anything about a type, method or config value whose definition is not in the input

        If you cannot prove a defect from the text in front of you, do not report it.
        Reporting nothing is a correct and acceptable answer.

        ## Project conventions beat generic best practice

        The input may include a "REPOSITORY CONTEXT" section with the repository's own agent
        instructions, architecture notes, README, and build/style configuration. Treat these as
        authoritative. A deliberate project convention is NOT a defect, even when it differs
        from what you would normally recommend. Never suggest a library, pattern, or style the
        project has explicitly chosen against, and never flag a style rule that the project's
        .editorconfig or analyzers already enforce — that is the compiler's job, not yours.

        ## Existing comments

        The input may include an "EXISTING PR COMMENTS" section with feedback already left by
        others. Read these first. Do NOT repeat or contradict what has already been said. You
        may build on them, confirm a prior concern with new evidence, or note that a raised
        question is addressed by the changes.

        ## Scope

        PRIMARY REVIEW: Focus on lines starting with `+` or `-`. Set "isAdditionalObservation":
        false for these comments.

        ADDITIONAL OBSERVATIONS: You may also flag a genuine, provable defect on an unchanged
        context line that directly interacts with the change. Set "isAdditionalObservation":
        true. Only significant issues, never nitpicks.

        ## Output format

        Respond with a JSON array of review comments in this exact format:
        [
          {
            "filePath": "/path/to/file.cs",
            "lineNumber": 42,
            "severity": "Warning",
            "confidence": 5,
            "evidence": "public async Task<IResult> GetUser(int id)",
            "issue": "Brief description of the problem",
            "suggestion": "Explanation of what to do instead",
            "codeExample": "// corrected code here\npublic async Task<IResult> GetUser(int id) ...",
            "isAdditionalObservation": false
          }
        ]

        - "filePath" MUST match one of the file paths given in the input exactly.
        - "evidence" MUST be a verbatim copy of the single source line the finding is about,
          copied character for character from the diff WITHOUT the `<sign><lineNumber> | `
          prefix. It is checked against the real diff; a finding whose evidence does not appear
          in the file is discarded as a hallucination. Do not paraphrase or reformat it.
        - "lineNumber" MUST be the number printed on that same evidence line, in the NEW file.
          Copy the number exactly as printed. Do NOT count lines yourself and do NOT use the
          position of the line inside the diff block. For an issue about a removed (`-`) line,
          use the number of the closest added or unchanged line so the comment anchors.
        - "confidence" is 1-5: how sure you are this is a real defect rather than an inference
          about code you cannot see. Use 5 only when the shown lines prove it, 4 when it is
          near-certain, 3 when it depends on an assumption you have stated in "suggestion".
          Do NOT emit findings below confidence 3 — leave them out entirely.

        Severity levels: "Info", "Warning", "Critical"
        - Critical: Security issues, data loss, crashes, serious bugs
        - Warning: Performance problems, bad patterns, maintainability issues
        - Info: Style improvements, minor suggestions

        Work briskly. Decide once per finding whether the shown lines prove it; if you are
        still arguing with yourself about a finding, drop it and move on. Do not restate the
        diff, re-check work you have already done, or explain your process — only the JSON is
        read, and deliberation that does not fit in the output budget loses the whole review.

        Return ONLY JSON, no other text, no explanation and no thinking outside it. If you find
        no issues, return an empty array — that is a valid and often correct answer.

        Either shape is accepted, so use whichever your output mode allows:
            [ ...the comments... ]
        or
            { "comments": [ ...the comments... ] }
        """;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string BuildReviewPrompt(
        PullRequestInfo pr, IReadOnlyList<ChangedFile> files, bool scopeCommentsToBatch = true)
    {
        StringBuilder sb = new();

        // Repository conventions come first: they frame everything that
        // follows, and they are identical for every PR in the repo, which
        // keeps them at a stable prefix position for prompt caching.
        if (pr.RepoContext.Count != 0)
        {
            sb.AppendLine("=== REPOSITORY CONTEXT (authoritative conventions for this codebase — prefer these over generic best practice) ===");
            foreach (RepoContextFile file in pr.RepoContext)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"--- {file.Path} ---");
                sb.AppendLine(file.Content);
                if (file.IsTruncated)
                {
                    sb.AppendLine("[... truncated ...]");
                }

                sb.AppendLine();
            }

            sb.AppendLine("=== END REPOSITORY CONTEXT ===");
            sb.AppendLine();
        }

        sb.AppendLine("Review this Pull Request:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Repository: {pr.RepositoryName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {pr.Title}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Author: {pr.Author}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Branch: {pr.SourceBranch} → {pr.TargetBranch}");
        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Description: {pr.Description}");
        }

        // Only comments about files in this batch (plus PR-level ones). A
        // comment on a file the model cannot see is noise it must read and pay
        // for, in every batch.
        List<PrComment> relevantComments = scopeCommentsToBatch
            ? [.. pr.ExistingComments.Where(c => c.FilePath is null || MentionsAnyOf(c.FilePath, files))]
            : pr.ExistingComments;

        if (relevantComments.Count != 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== EXISTING PR COMMENTS (already made by others — take these into account; do not repeat or contradict them) ===");
            foreach (PrComment c in relevantComments)
            {
                string location = c.FilePath is not null
                    ? $" [{c.FilePath}{(c.LineNumber.HasValue ? $":{c.LineNumber}" : "")}]"
                    : " [PR-level]";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{c.Author}{location}: {c.Content}");
            }
        }

        if (pr.SkippedFiles.Count != 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== FILES CHANGED BUT NOT SHOWN TO YOU (do not reason about these, and do not comment on them) ===");
            foreach (SkippedFile skipped in pr.SkippedFiles)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{skipped.Path} — {skipped.Reason}");
            }
        }

        sb.AppendLine();

        // Only the files in this batch. Files from the PR's other batches are
        // deliberately not mentioned: naming them invites the model to reason
        // about code it has not been given, which is the behaviour the
        // partial-view rule above exists to prevent.
        foreach (ChangedFile file in files)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"=== FILE: {file.Path} ({file.ChangeType}) ===");
            if (file.NewFileLineCount > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"The full file is {file.NewFileLineCount} lines; only changed regions and nearby context are shown below.");
            }

            if (file.IsTruncated)
            {
                sb.AppendLine("WARNING: this diff was truncated. Do not draw any conclusion about code that is not shown.");
            }

            sb.AppendLine("Format: <sign><lineNumber> | <content>  (+ added / - removed / space unchanged)");
            sb.AppendLine(file.Diff);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static bool MentionsAnyOf(string commentPath, IReadOnlyList<ChangedFile> files)
    {
        string normalized = commentPath.Replace('\\', '/').TrimStart('/');
        foreach (ChangedFile file in files)
        {
            if (file.Path.Replace('\\', '/').TrimStart('/')
                .Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Turns a provider's reply into review comments.
    //
    // Throws ReviewFailedException rather than returning an empty list when the
    // reply cannot be read. "[]" is a real answer meaning the model found
    // nothing; an empty or malformed reply is a failure, and conflating the two
    // reports unreviewed PRs as clean.
    public static List<ReviewComment> ParseReviewResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ReviewFailedException(
                "the provider returned an empty message. The model produced no text at all — "
                + "usually the output token budget was consumed before it could answer "
                + "(raise Review:MaxOutputTokens), or the model emitted only reasoning tokens.",
                response);
        }

        string text = StripCodeFence(response.Trim());

        // A bare array — the expected shape. An empty one is a valid answer.
        if (text.StartsWith('[') && TryDeserializeArray(text, out List<ReviewComment> direct))
        {
            return direct;
        }

        // An object wrapper: {"comments": [...]}. Required when the provider is
        // in json_object mode, which forbids a top-level array. Any property
        // holding an array is accepted, since models pick their own key.
        if (text.StartsWith('{') && TryUnwrapObject(text, out List<ReviewComment> unwrapped))
        {
            return unwrapped;
        }

        // Prose with the answer somewhere inside it.
        if (TryFindCommentArray(text, out List<ReviewComment>? found) && found is not null)
        {
            return found;
        }

        string hint = text.StartsWith('[') && !text.EndsWith(']')
            ? " The array is unterminated, so the answer was cut off — raise Review:MaxOutputTokens."
            : "";

        throw new ReviewFailedException(
            $"the provider's reply could not be read as review comments.{hint}", response);
    }

    // Finds a balanced JSON array of review comments embedded in arbitrary
    // text, and returns it only if it really deserializes.
    //
    // This must never fall back to "first '[' to last ']'". Model prose is full
    // of brackets — a reasoning trace discussing `string[] GetOverridingArray()`
    // starts with "[] GetOverridingArray(string key)", which that naive slice
    // happily hands to the JSON parser. The resulting error then hides the
    // actual failure, which is usually that the model returned no answer.
    public static bool TryFindCommentArray(
        string? text, [NotNullWhen(true)] out List<ReviewComment>? comments)
    {
        comments = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '[')
            {
                continue;
            }

            int close = FindMatchingBracket(text, i);
            if (close < 0)
            {
                continue;
            }

            string candidate = text[i..(close + 1)];

            // Require at least one object that looks like a review comment.
            // Without this, an empty "[]" or an array of code identifiers
            // lifted out of prose would be accepted as a clean review.
            if (TryDeserializeArray(candidate, out List<ReviewComment>? parsed)
                && parsed.Count != 0
                && parsed.Exists(c => !string.IsNullOrWhiteSpace(c.FilePath)))
            {
                comments = parsed;
                return true;
            }
        }

        return false;
    }

    // Walks forward from an opening bracket to its match, honouring JSON string
    // literals and escapes so brackets inside strings do not confuse it.
    private static int FindMatchingBracket(string text, int openIndex)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = openIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    depth--;
                    if (depth == 0)
                    {
                        return c == ']' ? i : -1;
                    }

                    break;
                default:
                    break;
            }
        }

        return -1;
    }

    private static bool TryUnwrapObject(string text, out List<ReviewComment> comments)
    {
        comments = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array
                    && TryDeserializeArray(property.Value.GetRawText(), out List<ReviewComment>? parsed))
                {
                    comments = parsed;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Not a JSON object after all; the caller falls through to scanning.
        }

        return false;
    }

    private static bool TryDeserializeArray(string json, out List<ReviewComment> comments)
    {
        try
        {
            comments = JsonSerializer.Deserialize<List<ReviewComment>>(json, _jsonOptions) ?? [];
            return true;
        }
        catch (JsonException)
        {
            comments = [];
            return false;
        }
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        string body = text[(firstNewline + 1)..];
        int closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }
}

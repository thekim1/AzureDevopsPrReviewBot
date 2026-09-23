using System.Globalization;
using System.Text;
using PrReviewBot.Config;
using PrReviewBot.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PrReviewBot.Services;

// Renders one row per batch while the model works, showing how much thinking
// and how much answer each has produced, plus the tail of the current thought.
//
// The point is not decoration. A reasoning model can think for minutes before
// emitting any answer, and a spinner cannot distinguish that from a hang. Here
// the reasoning counter climbing while the answer stays at zero is exactly the
// picture of a request that is about to exhaust its output budget.
public sealed class LiveReviewDisplay
{
    // A running part that has produced nothing for this long says so, so a
    // stalled request can be told apart from a slow one at a glance.
    private static readonly TimeSpan QuietThreshold = TimeSpan.FromSeconds(10);

    private readonly ReviewSettings _settings;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<BatchRow> _rows = [];
    private bool _dirty;
    private long _lastRenderedSecond = -1;

    public LiveReviewDisplay(ReviewSettings settings, TimeProvider? time = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    public void Plan(IReadOnlyList<BatchInfo> batches)
    {
        lock (_lock)
        {
            _rows.Clear();
            foreach (BatchInfo batch in batches)
            {
                _rows.Add(new BatchRow
                {
                    Label = batch.FilePaths.Count == 1
                        ? ShortName(batch.FilePaths[0])
                        : $"{batch.FilePaths.Count} files",
                });
            }

            _dirty = true;
        }
    }

    public void Report(ReviewProgress update)
    {
        lock (_lock)
        {
            if (update.BatchIndex < 0 || update.BatchIndex >= _rows.Count)
            {
                return;
            }

            BatchRow row = _rows[update.BatchIndex];
            DateTimeOffset now = _time.GetUtcNow();
            row.StartedAt ??= now;
            if (update.ReasoningChars != row.ReasoningChars || update.AnswerChars != row.AnswerChars)
            {
                row.LastOutputAt = now;
            }

            row.ReasoningChars = update.ReasoningChars;
            row.AnswerChars = update.AnswerChars;
            row.Started = true;
            if (update.PromptChars > 0)
            {
                row.PromptChars = update.PromptChars;
            }

            if (!string.IsNullOrEmpty(update.LatestText))
            {
                row.Tail = Tail(row.Tail + update.LatestText, _settings.ThinkingPreviewChars);
            }

            _dirty = true;
        }
    }

    public void Complete(int batchIndex, bool succeeded)
    {
        lock (_lock)
        {
            if (batchIndex >= 0 && batchIndex < _rows.Count)
            {
                _rows[batchIndex].Finished = true;
                _rows[batchIndex].Failed = !succeeded;
                _rows[batchIndex].FinishedAt = _time.GetUtcNow();
                _dirty = true;
            }
        }
    }

    // True when something changed since the last render, so the caller can skip
    // redrawing an unchanged table.
    public bool TryRender(out IRenderable renderable)
    {
        lock (_lock)
        {
            renderable = Build(AnsiConsole.Profile.Width, AnsiConsole.Profile.Height);

            // The timers on running parts change every second even when no
            // output arrives, which is exactly when they matter.
            long second = _time.GetUtcNow().ToUnixTimeSeconds();
            bool clockTicked = second != _lastRenderedSecond && _rows.Exists(r => r.Started && !r.Finished);
            _lastRenderedSecond = second;

            bool changed = _dirty || clockTicked;
            _dirty = false;
            return changed;
        }
    }

    // Every row is kept to a single line and the whole table inside the
    // terminal. A live display is repainted by moving the cursor back up by
    // the height it drew last time; once a row wraps, or the table outgrows
    // the window, that no longer lines up and each repaint leaves a scrambled
    // copy of the table behind. The thinking tail was the culprit — up to
    // ThinkingPreviewChars of free text, wrapping over several lines per row,
    // with tabs and other control characters in it.
    internal IRenderable Build(int width, int height)
    {
        const int LabelMaxWidth = 32;
        const int NumberWidth = 11;          // "~12,345 tok"
        const int TableChrome = 6 + (5 * 2); // six borders, one space either side of each column
        const int FixedLines = 5;            // top border, header, separator, bottom border, one spare

        // In a narrow window the label gives way too, down to a stub, so the
        // fixed columns alone never force a row to wrap.
        int available = width - TableChrome - (3 * NumberWidth);
        int labelMax = Math.Clamp(available / 2, 6, LabelMaxWidth);

        List<(string Label, BatchRow Row)> shown = [];
        for (int i = 0; i < _rows.Count; i++)
        {
            shown.Add((FitStart($"{i + 1}. {_rows[i].Label}", labelMax - 2), _rows[i]));
        }

        // Too many rows for the window: fold the finished ones into a count,
        // then cut the list, so the parts still in progress stay visible.
        int maxRows = Math.Max(1, height - FixedLines - 1);
        string? summary = null;
        if (shown.Count > maxRows)
        {
            int done = shown.Count(r => r.Row.Finished && !r.Row.Failed);
            shown = [.. shown.Where(r => !(r.Row.Finished && !r.Row.Failed))];

            int hidden = Math.Max(0, shown.Count - maxRows);
            shown = [.. shown.Take(maxRows)];
            summary = string.Create(CultureInfo.InvariantCulture,
                $"{done} done{(hidden > 0 ? $", {hidden} more not shown" : "")}");
        }

        int labelWidth = Math.Max(
            "Part".Length,
            shown.Select(r => r.Label.GetCellWidth() + 2).DefaultIfEmpty(0).Max());
        int tailWidth = Math.Max(0, available - labelWidth);

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]Part[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]Prompt[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[grey]Thinking[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[grey]Answer[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[grey]Latest thought[/]").NoWrap());

        foreach ((string label, BatchRow row) in shown)
        {
            string status = row switch
            {
                { Failed: true } => "[red]✗[/]",
                { Finished: true } => "[green]✓[/]",
                { Started: true } => "[yellow]●[/]",
                _ => "[grey]·[/]"
            };

            // Thinking with no answer yet is the state worth drawing attention
            // to — it is what precedes an exhausted output budget.
            string thinking = row.ReasoningChars == 0
                ? "[grey]—[/]"
                : $"[{(row.AnswerChars == 0 && row.ReasoningChars > 20000 ? "red" : "yellow")}]"
                  + $"{Approx(row.ReasoningChars)}[/]";

            string answer = row.AnswerChars == 0
                ? "[grey]—[/]"
                : $"[green]{Approx(row.AnswerChars)}[/]";

            // Prompt size matters for speed: a flash model slows down per
            // token as its input grows, which looks like a stall.
            string prompt = row.PromptChars == 0 ? "[grey]—[/]" : $"[grey]{Approx(row.PromptChars)}[/]";

            table.AddRow(
                $"{status} {Markup.Escape(label)}",
                prompt,
                thinking,
                answer,
                LatestColumn(row, tailWidth));
        }

        if (summary is not null)
        {
            table.AddRow($"[green]✓[/] {Markup.Escape(summary)}", "", "", "", "");
        }

        return table;
    }

    // What the part is doing now: the tail of its output while it streams, how
    // long it has been silent when it stops, or how long it took once done.
    private string LatestColumn(BatchRow row, int width)
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (row.Finished)
        {
            string took = row.StartedAt is { } started
                ? $"{(row.Failed ? "failed after" : "took")} {Duration((row.FinishedAt ?? now) - started)}"
                : "";
            return $"[grey]{Markup.Escape(FitEnd(took, width))}[/]";
        }

        if (row.StartedAt is not { } start)
        {
            return "";
        }

        // The running clock comes first and ticks every second, so a frozen
        // display — nothing repainting at all — is told apart from a slow
        // model at a glance.
        string clock = Duration(now - start) + " ";

        DateTimeOffset since = row.LastOutputAt ?? start;
        TimeSpan quiet = now - since;
        if (quiet >= QuietThreshold)
        {
            string text = row.LastOutputAt is null
                ? $"waiting for the model to start… {Duration(quiet)}"
                : $"no output for {Duration(quiet)}";
            return $"[grey]{Markup.Escape(clock)}[/][yellow]{Markup.Escape(FitEnd(text, Math.Max(0, width - clock.Length)))}[/]";
        }

        return $"[grey]{Markup.Escape(clock + FitEnd(SingleLine(row.Tail), Math.Max(0, width - clock.Length)))}[/]";
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds:00}s")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)span.TotalSeconds)}s");

    // Model output flattened to one line of printable text.
    internal static string SingleLine(string text)
    {
        StringBuilder sb = new(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            bool space = char.IsWhiteSpace(c) || char.IsControl(c);
            if (space)
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(c);
            }

            lastWasSpace = space;
        }

        return sb.ToString();
    }

    // The end of `text` that fits in `width` terminal cells. Measured in cells,
    // not characters, because CJK text — which some reasoning models think
    // in — takes two cells per character.
    internal static string FitEnd(string text, int width)
    {
        if (text.GetCellWidth() <= width)
        {
            return text;
        }

        if (width <= 1)
        {
            return "";
        }

        Rune[] runes = [.. text.EnumerateRunes()];
        int used = 1; // the leading ellipsis
        int start = runes.Length;
        while (start > 0)
        {
            int w = runes[start - 1].ToString().GetCellWidth();
            if (used + w > width)
            {
                break;
            }

            used += w;
            start--;
        }

        return "…" + string.Concat(runes[start..].Select(r => r.ToString()));
    }

    // The start of `text` that fits in `width` cells.
    private static string FitStart(string text, int width)
    {
        if (text.GetCellWidth() <= width)
        {
            return text;
        }

        StringBuilder sb = new();
        int used = 1; // the trailing ellipsis
        foreach (Rune rune in text.EnumerateRunes())
        {
            int w = rune.ToString().GetCellWidth();
            if (used + w > width)
            {
                break;
            }

            used += w;
            sb.Append(rune.ToString());
        }

        return sb.Append('…').ToString();
    }

    private static string Approx(int chars) =>
        string.Create(CultureInfo.InvariantCulture, $"~{chars / 4:N0} tok");

    private static string Tail(string text, int max) =>
        text.Length <= max ? text : text[^max..];

    private static string ShortName(string path) => path.Split('/')[^1];

    private sealed class BatchRow
    {
        public string Label { get; set; } = "";
        public string Tail { get; set; } = "";
        public int ReasoningChars { get; set; }
        public int AnswerChars { get; set; }
        public int PromptChars { get; set; }
        public bool Started { get; set; }
        public bool Finished { get; set; }
        public bool Failed { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? LastOutputAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
    }
}

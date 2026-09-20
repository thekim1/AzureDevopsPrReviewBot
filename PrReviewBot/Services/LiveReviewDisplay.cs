using System.Globalization;
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
    private readonly ReviewSettings _settings;
    private readonly Lock _lock = new();
    private readonly List<BatchRow> _rows = [];
    private bool _dirty;

    public LiveReviewDisplay(ReviewSettings settings)
    {
        _settings = settings;
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
            row.ReasoningChars = update.ReasoningChars;
            row.AnswerChars = update.AnswerChars;
            row.Started = true;

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
            renderable = Build();
            bool changed = _dirty;
            _dirty = false;
            return changed;
        }
    }

    private IRenderable Build()
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]Part[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]Thinking[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[grey]Answer[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[grey]Latest thought[/]"));

        for (int i = 0; i < _rows.Count; i++)
        {
            BatchRow row = _rows[i];

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

            table.AddRow(
                $"{status} {Markup.Escape($"{i + 1}. {row.Label}")}",
                thinking,
                answer,
                $"[grey]{Markup.Escape(row.Finished ? "" : row.Tail.ReplaceLineEndings(" "))}[/]");
        }

        return table;
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
        public bool Started { get; set; }
        public bool Finished { get; set; }
        public bool Failed { get; set; }
    }
}

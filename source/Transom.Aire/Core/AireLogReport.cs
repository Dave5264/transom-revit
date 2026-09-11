using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Transom.Core;

/// <summary>
///     One render (or one clip) as the log reader presents it: the outcome, the engine's own sentence, and the
///     single line that says what to do about it. PROPERTIES, not fields — these are data-bound by
///     LogViewerWindow's item template, and WPF bindings cannot see fields.
/// </summary>
public sealed class AireLogRow
{
    /// <summary>The source file's name alone — the full path is in <see cref="SourcePath"/>.</summary>
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string OutputPath { get; set; } = "";

    /// <summary>"Success" | "Failed" | whatever the vendor called it (the video log stores its own states:
    /// completed, failed, nsfw, canceled).</summary>
    public string Status { get; set; } = "";
    public bool Succeeded { get; set; }

    /// <summary>The plain-language failure sentence the engine already wrote into the CSV, or "" on success.</summary>
    public string Message { get; set; } = "";

    /// <summary>Class of the failure — read from the error_kind column, or sniffed from the message on a log
    /// written before that column existed. "" on success.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The one thing the user can do about it, from <see cref="AireLogReport.WhatToDo"/>. "" on success.</summary>
    public string Advice { get; set; } = "";

    /// <summary>"3840x2160 · quality max · 2 min 14 sec · billed $0.4012 (13,342 output tokens)" — whichever
    /// of those the log actually recorded.</summary>
    public string Detail { get; set; } = "";

    /// <summary>What the output line shows: the produced file's name on success, nothing otherwise.</summary>
    public string OutputName => OutputPath.Length > 0 ? Path.GetFileName(OutputPath) : "";

    /// <summary>"01-riverside-dusk.png → 01-riverside-dusk_enhanced.png", or just the source when there is no output.</summary>
    public string Title => OutputName.Length > 0 ? $"{Name}  →  {OutputName}" : Name;

    /// <summary>Prefixed here rather than in the template so the label and the line vanish together.</summary>
    public string AdviceLine => Advice.Length > 0 ? "What to do: " + Advice : "";

    public double? ActualCostUsd { get; set; }
    public double EstimatedCostUsd { get; set; }
}

/// <summary>
///     A batch CSV, read back as something a person can read. The CSV itself is unchanged and stays the durable
///     record — sortable, sixty rows wide, the right thing to open in Excel — and the reader keeps an "Open CSV"
///     button for exactly that. This is the layer in front of it, because "the log is at
///     &lt;path&gt;\logs\enhancement_log_20260911_143207.csv" is the wrong answer to "what happened?".
///     <para>
///     Columns are mapped by HEADER NAME, never by index. That is what lets one code path read the original
///     ten-column file, v1.9.17's fourteen, v1.9.18's fifteen, and the Video tab's entirely different schema —
///     and what keeps this working the next time a column is appended.
///     </para>
///     <para>
///     Every sentence here except <see cref="WhatToDo"/> was already written by the engine and stored in the
///     log. Nothing is re-worded on the way out, so the reader cannot disagree with the CSV.
///     </para>
/// </summary>
public sealed class AireLogReport
{
    public string CsvPath = "";
    public string FileName = "";

    /// <summary>"Enhance" or "Video", inferred from the header row.</summary>
    public string Kind = "Enhance";

    public DateTime? RunAt;
    public List<AireLogRow> Rows = new();

    public int SuccessCount => Rows.Count(r => r.Succeeded);
    public int FailureCount => Rows.Count(r => !r.Succeeded);
    public double TotalSeconds;
    public double EstimatedCostUsd;

    /// <summary>Billed total, and how many rows contributed. Null when no row recorded one — an older log, or
    /// a batch where no response carried usage.</summary>
    public double? ActualCostUsd;
    public int ActualCostRows;

    /// <summary>Set when the last row is a failure that would have repeated on every remaining render, i.e. the
    /// batch stopped itself. The count of what was skipped is NOT knowable from the file (an unattempted image
    /// has no row), so this says that it stopped and why, and the batch summary dialog gives the number.</summary>
    public string StoppedReason = "";

    /// <summary>Anything the parser could not make sense of, so a malformed log says so rather than rendering
    /// as an empty, confident-looking report.</summary>
    public string Warning = "";

    public static AireLogReport Load(string csvPath)
    {
        var report = new AireLogReport { CsvPath = csvPath, FileName = Path.GetFileName(csvPath) };
        report.RunAt = TimestampFromName(report.FileName);

        var lines = ParseCsv(File.ReadAllText(csvPath));
        if (lines.Count == 0)
        {
            report.Warning = "The log file is empty.";
            return report;
        }

        var header = lines[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        string Cell(List<string> row, string name)
        {
            int i = Col(name);
            return i >= 0 && i < row.Count ? row[i] : "";
        }

        report.Kind = Col("source_file") >= 0 && Col("request_id") >= 0 ? "Video" : "Enhance";
        if (Col("original_file") < 0 && Col("source_file") < 0)
        {
            report.Warning = "This file does not look like an AIRE log (no original_file or source_file column).";
            return report;
        }

        foreach (var cells in lines.Skip(1))
        {
            if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace)) continue;

            var source = Cell(cells, "original_file");
            if (source.Length == 0) source = Cell(cells, "source_file");
            var status = Cell(cells, "status");
            var message = Cell(cells, "error_message");

            var row = new AireLogRow
            {
                SourcePath = source,
                Name = source.Length > 0 ? Path.GetFileName(source) : "(unnamed)",
                OutputPath = Cell(cells, "output_file"),
                Status = status,
                Succeeded = IsSuccess(status),
                Message = message,
            };
            // The column exists from v1.9.18; before that, sniff the sentence. Both paths are the engine's own
            // wording, so the sniff is matching text this codebase wrote, not free-form English.
            row.Kind = row.Succeeded ? "" : NonEmpty(Cell(cells, "error_kind"), SniffKind(status, message));
            row.Advice = row.Succeeded ? "" : WhatToDo(row.Kind);
            row.EstimatedCostUsd = Num(Cell(cells, "estimated_cost_usd"));
            row.ActualCostUsd = OptionalNum(Cell(cells, "actual_cost_usd"));
            row.Detail = report.Kind == "Video" ? VideoDetail(cells, Cell) : EnhanceDetail(cells, Cell, row);

            report.TotalSeconds += Num(Cell(cells, "time_seconds"));
            report.EstimatedCostUsd += row.EstimatedCostUsd;
            if (row.Succeeded && row.ActualCostUsd is { } billed)
            {
                report.ActualCostUsd = (report.ActualCostUsd ?? 0) + billed;
                report.ActualCostRows++;
            }
            report.Rows.Add(row);
        }

        // "Stopped here" is a property of the LAST row: the run loop breaks straight after recording it.
        var last = report.Rows.LastOrDefault();
        if (last is { Succeeded: false } && AireEngine.StopsBatch(last.Kind))
            report.StoppedReason = last.Message;

        return report;
    }

    /// <summary>The newest AIRE log under <paramref name="outputFolder"/>\logs, or null. What makes View Log
    /// live on a fresh open rather than only after a run in this window.</summary>
    public static string? NewestLogIn(string outputFolder, string prefix = "enhancement_log_")
    {
        try
        {
            var logs = Path.Combine((outputFolder ?? "").Trim(), "logs");
            if (!Directory.Exists(logs)) return null;
            return Directory.EnumerateFiles(logs, prefix + "*.csv")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                // AIRE log names embed the run time, so the name breaks a tie deterministically — two logs
                // written inside one filesystem timestamp tick would otherwise come back in any order.
                .ThenByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Every AIRE log under that folder, newest first — the reader's "Log" dropdown.</summary>
    public static List<string> LogsIn(string outputFolder)
    {
        try
        {
            var logs = Path.Combine((outputFolder ?? "").Trim(), "logs");
            if (!Directory.Exists(logs)) return new List<string>();
            return Directory.EnumerateFiles(logs, "*.csv")
                .Where(f => Path.GetFileName(f).StartsWith("enhancement_log_", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(f).StartsWith("video_log_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    ///     The one new sentence in the whole reader: what the user can actually do about this class of failure.
    ///     A lookup on the kind, not a parser — the failure sentence itself is already in <see cref="AireLogRow.Message"/>
    ///     and was written by <see cref="AireEngine.DescribeApiError"/>.
    /// </summary>
    public static string WhatToDo(string kind) => kind switch
    {
        "auth" => "Check the key in the API Key box, or create a replacement on platform.openai.com → API keys. "
                  + "Nothing was charged.",
        "verification" => "Open Settings → Organization → General on platform.openai.com and click Verify Organization. "
                          + "Access takes about fifteen minutes to take effect. Nothing was charged.",
        "quota" => "Add credit under Billing on platform.openai.com, then run the batch again. Nothing was charged.",
        "rate_limit" => "Wait a minute and process the remaining images again. Your account tier caps images per "
                        + "minute. Nothing was charged for this one.",
        "moderation" => "Adjust the prompt, or take this render out of the queue. The other renders were unaffected "
                        + "and nothing was charged for this one.",
        "fidelity" => "Untick Fidelity and run the batch again. This model always reads the source at full fidelity "
                      + "and refuses the setting. Nothing was charged.",
        "size" => "Pick a different Resolution. Both edges must be multiples of 16, the shape between 1:3 and 3:1, "
                  + "and the total between 0.66 and 8.3 megapixels. Nothing was charged.",
        "server" => "OpenAI's service had a problem. AIRE already retried — try the batch again in a few minutes. "
                    + "Nothing was charged.",
        "network" => "AIRE could not reach OpenAI. Check the connection (and any proxy or firewall), then run the "
                     + "batch again. Nothing was charged.",
        "cancelled" => "You cancelled the batch. An image already generating may still have been charged.",
        "nsfw" => "Higgsfield's content filter rejected the request. Reword the motion prompt and try again. "
                  + "Nothing was charged.",
        _ => "",
    };

    /// <summary>Everything in one pasteable block — the Copy button, and what goes into a support email.</summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AIRE {Kind.ToLowerInvariant()} log — {FileName}");
        if (RunAt is { } when) sb.AppendLine(when.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
        sb.AppendLine(HeadlineText());
        sb.AppendLine(CostText());
        if (Warning.Length > 0) sb.AppendLine("Warning: " + Warning);
        sb.AppendLine();
        foreach (var r in Rows)
        {
            sb.AppendLine((r.Succeeded ? "OK    " : "FAIL  ") + r.Name);
            if (r.Detail.Length > 0) sb.AppendLine("      " + r.Detail);
            if (r.Message.Length > 0) sb.AppendLine("      " + r.Message);
            if (r.Advice.Length > 0) sb.AppendLine("      What to do: " + r.Advice);
        }
        if (StoppedReason.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Batch stopped here — the same refusal would have come back for every remaining render, "
                          + "so they were not attempted.");
        }
        sb.AppendLine();
        sb.AppendLine(CsvPath);
        return sb.ToString();
    }

    /// <summary>"6 renders · 2 succeeded, 3 failed" — counts of what the FILE holds. An image the run never
    /// reached has no row, so "not attempted" is the summary dialog's to report, not the reader's.</summary>
    public string HeadlineText()
    {
        var noun = Kind == "Video" ? (Rows.Count == 1 ? "clip" : "clips") : (Rows.Count == 1 ? "render" : "renders");
        var parts = new List<string>();
        if (SuccessCount > 0) parts.Add($"{SuccessCount} succeeded");
        if (FailureCount > 0) parts.Add($"{FailureCount} failed");
        return $"{Rows.Count} {noun}" + (parts.Count > 0 ? " · " + string.Join(", ", parts) : "");
    }

    public string CostText()
    {
        var elapsed = TotalSeconds > 0 ? AireEngine.SecondsToText(TotalSeconds) : "";
        var estimated = EstimatedCostUsd > 0 ? $"estimated ${EstimatedCostUsd:0.0000}" : "";
        var billed = ActualCostUsd is { } a
            ? $"OpenAI billed ${a:0.0000}"
              + (ActualCostRows < SuccessCount ? $" ({ActualCostRows} of {SuccessCount} reported usage)" : "")
            : "";
        return string.Join("  ·  ", new[] { elapsed, estimated, billed }.Where(s => s.Length > 0));
    }

    // ---- per-row detail lines -----------------------------------------------------------------------

    private static string EnhanceDetail(List<string> cells, Func<List<string>, string, string> cell, AireLogRow row)
    {
        var bits = new List<string>();
        var size = NonEmpty(cell(cells, "actual_size"), cell(cells, "output_size"));
        if (size.Length > 0) bits.Add(size);
        var quality = cell(cells, "quality");
        if (quality.Length > 0) bits.Add("quality " + quality);
        var seconds = Num(cell(cells, "time_seconds"));
        if (seconds > 0) bits.Add(AireEngine.SecondsToText(seconds));
        if (row.Succeeded && row.ActualCostUsd is { } billed)
        {
            var tokens = cell(cells, "output_tokens");
            bits.Add($"billed ${billed:0.0000}"
                     + (tokens.Length > 0 ? $" ({Num(tokens):N0} output tokens)" : ""));
        }
        else if (row.Succeeded && row.EstimatedCostUsd > 0)
        {
            bits.Add($"estimated ${row.EstimatedCostUsd:0.0000} (OpenAI reported no usage)");
        }
        return string.Join(" · ", bits);
    }

    private static string VideoDetail(List<string> cells, Func<List<string>, string, string> cell)
    {
        var bits = new List<string>();
        var model = cell(cells, "model");
        if (model.Length > 0) bits.Add(model);
        var duration = cell(cells, "duration");
        if (duration.Length > 0) bits.Add(duration + "s");
        var resolution = cell(cells, "resolution");
        if (resolution.Length > 0) bits.Add(resolution + "p");
        var seconds = Num(cell(cells, "time_seconds"));
        if (seconds > 0) bits.Add(AireEngine.SecondsToText(seconds));
        var charged = cell(cells, "charged_cost_usd");
        if (charged.Length > 0 && charged != "0") bits.Add($"charged ${charged}");
        var credits = cell(cells, "estimated_credits");
        if (credits.Length > 0) bits.Add(credits + " credits");
        return string.Join(" · ", bits);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>The image batch writes "Success"; the video job stores its vendor state, where only "completed"
    /// is a win ("failed", "nsfw" and "canceled" are not).</summary>
    private static bool IsSuccess(string status) =>
        status.Equals("Success", StringComparison.OrdinalIgnoreCase)
        || status.Equals("completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The class of a failure in a log written before the error_kind column existed. Matches against the
    ///     engine's OWN sentences (and the video job's), which this codebase wrote and has not changed — so it
    ///     is a lookup on known text, not an attempt to understand English. Unknown text simply yields "",
    ///     which shows the message with no "What to do" line rather than a wrong one.
    /// </summary>
    public static string SniffKind(string status, string message)
    {
        var m = message ?? "";
        bool has(string s) => m.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
        if (m.Trim().Equals("cancelled", StringComparison.OrdinalIgnoreCase)) return "cancelled";
        if (status.Equals("nsfw", StringComparison.OrdinalIgnoreCase)) return "nsfw";
        if (status.Equals("canceled", StringComparison.OrdinalIgnoreCase)) return "cancelled";
        if (has("not verified") || has("Verify Organization")) return "verification";
        if (has("rejected the API key")) return "auth";
        if (has("out of credit")) return "quota";
        if (has("rate-limiting")) return "rate_limit";
        if (has("safety filter")) return "moderation";
        if (has("Input fidelity") || has("input_fidelity")) return "fidelity";
        if (has("resolution is not valid")) return "size";
        if (has("Could not reach the OpenAI API")) return "network";
        if (has("HTTP 5")) return "server";
        return "";
    }

    private static string NonEmpty(string first, string second) => first.Length > 0 ? first : second;

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double? OptionalNum(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The run time out of "enhancement_log_20260911_143207.csv" / "video_log_…", or null.</summary>
    private static DateTime? TimestampFromName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        int i = stem.LastIndexOf('_');
        if (i <= 0) return null;
        var stamp = stem[..i].LastIndexOf('_') is var j && j >= 0 ? stem[(j + 1)..] : stem;
        return DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var when) ? when : null;
    }

    /// <summary>
    ///     RFC 4180 enough for what <see cref="AireJob"/> writes: quoted fields, doubled quotes inside them, and
    ///     embedded newlines (an OpenAI message can contain one). BCL only — the rest of Transom.Aire takes no
    ///     CSV package and this is not the place to add one.
    /// </summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') { field.Append(c); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; continue; }
                quoted = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (any || row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    any = false;
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }
        if (field.Length > 0 || any || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        }
        return rows;
    }
}

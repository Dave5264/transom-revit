using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Transom.Core;

/// <summary>
///     Writes run-log.json — the contract between the add-in and Claude. The add-in never calls Claude;
///     it just records what it did/proposes so Claude (over MCP) can reconcile against the live model.
/// </summary>
public static class RunLog
{
    public static void WriteExport(string folder, List<ScheduleTable> tables, string workbook)
    {
        var first = tables.FirstOrDefault();
        var log = new
        {
            tool = "Transom",
            mode = "export",
            timestampUtc = DateTime.UtcNow.ToString("o"),
            model = new { guid = first?.SourceModelGuid ?? "", title = first?.SourceModelTitle ?? "" },
            workbook,
            schedules = tables.Select(t => new
            {
                name = t.ScheduleName,
                rows = t.RowCount,
                elementRows = t.ElementRowCount,
                roundTrippable = t.RoundTrippable,
            }).ToArray(),
        };
        Write(folder, log);
    }

    /// <param name="stage">"preview" (nothing written to the model yet) or "apply" (post-verification).
    /// This is the ONLY signal a reconciling consumer has: a preview log previously used the key
    /// "applied", so an abandoned preview looked like a set of writes that had silently failed.</param>
    public static void WriteImport(string folder, string workbook, ChangeSet cs, string stage = "preview")
    {
        bool applied = stage == "apply";
        var changes = cs.Changes.Select(c => new
        {
            uniqueId = c.UniqueId,
            field = c.Field,
            old = c.OldValue,
            @new = c.NewValue,
            binding = c.Binding,
            instancesAffected = c.InstancesAffected,
            outcome = c.Outcome.ToString(),
        }).ToArray();

        var log = new
        {
            tool = "Transom",
            mode = "import",
            stage,
            timestampUtc = DateTime.UtcNow.ToString("o"),
            workbook,
            // Named for what it is. "proposed" at preview time; "applied" only after the apply pass,
            // where each entry's `outcome` says whether that individual write actually landed.
            proposed = applied ? null : changes,
            applied = applied ? changes : null,
            // #64: the run-log's skipped array shows ONLY user-relevant skips (the user's data edits that didn't
            // land), matching the preview + post-apply count — structural/back-end skips (UserRelevant==false) are
            // omitted here too, so the JSON log agrees with everything the user sees.
            skipped = cs.Skipped.Where(s => s.UserRelevant).Select(s => new { reason = s.Reason, detail = s.Detail }).ToArray(),
        };
        Write(folder, log);
    }

    private static void Write(string folder, object log)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "run-log.json"),
                JsonSerializer.Serialize(log, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    // Drop the null half of proposed/applied so the file carries exactly one of them.
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                }));
        }
        catch { /* best-effort */ }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Writes a "run-results" workbook AFTER an import has applied: a re-read of every imported schedule with
///     each edited cell overlaid by its <see cref="ApplyOutcome"/> (bold = Applied, italic + red = Failed/
///     Unverified). Produced only when at least one change Failed or stayed Unverified — a clean run writes
///     nothing — so the file's existence is itself a signal that something needs review.
/// </summary>
public static class RunResultsWriter
{
    /// <summary>
    ///     Builds the run-results workbook next to the source import file (or in %TEMP%\Transom if no source path).
    ///     Returns the output path, or null when there's nothing to report (no Failed/Unverified change) or on any
    ///     failure (best-effort — never throws into the apply flow).
    /// </summary>
    public static string? Write(Document doc, ChangeSet cs, UIApplication app, string sourceImportPath)
    {
        try
        {
            // Report only on failure: if nothing failed or stayed unverified, there's nothing to flag.
            if (!cs.Changes.Any(c => c.Outcome is ApplyOutcome.Failed or ApplyOutcome.Unverified))
                return null;

            // Re-read each imported schedule (by name) from the now-applied model. Skip any that won't read.
            var byName = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate)
                .GroupBy(v => v.Name)
                .ToDictionary(g => g.Key, g => g.First());

            var tables = new List<ScheduleTable>();
            foreach (var schedName in cs.ImportedScheduleNames.Distinct())
            {
                if (!byName.TryGetValue(schedName, out var vs)) continue;
                try { tables.Add(new ScheduleReader(doc) { UiApp = app }.Read(vs)); }
                catch { /* a schedule that won't read — skip it */ }
            }
            if (tables.Count == 0) return null;

            // Overlay key: (row UniqueId, column ParameterId) -> outcome. A parallel `attempted` map carries the
            // value the user TYPED for cells whose source column is still present (rejected Option 2, §10c) so the
            // report shows the attempted text, not the re-read original.
            var outcomes = new Dictionary<(string uid, int paramId), ApplyOutcome>();
            var attempted = new Dictionary<(string uid, int paramId), string>();
            foreach (var ch in cs.Changes)
            {
                // W2 (H3 report-overlay): a bulk-instance cell leaves ch.UniqueId empty (its targets live in
                // BulkInstanceIds, which are instance uids — NOT rows in a grouped schedule), so it was skipped here
                // and a partially-failed bulk cell rendered as if nothing landed. Fall back to ReportRowUid (the
                // visible type/group row the cell renders on) so its true Outcome lands on the correct cell.
                string uid = !string.IsNullOrEmpty(ch.UniqueId) ? ch.UniqueId : ch.ReportRowUid;
                // #28: a TYPE-bound change carries only TypeId (UniqueId/ReportRowUid empty), so its Failed/Unverified
                // outcome never reached the overlay and type-bound failures rendered nowhere on the run-results sheet.
                // Resolve the type's UniqueId from TypeId — that's exactly the anchor a grouped/type schedule's row
                // carries (ReadTypeAnchors stamps the type UniqueId), so the marker lands on the correct type row.
                if (string.IsNullOrEmpty(uid) && ch.Binding == "type" && ch.TypeId != 0)
                {
                    try { uid = doc.GetElement(new ElementId(ch.TypeId))?.UniqueId ?? ""; }
                    catch { /* type vanished between apply and report — nothing to overlay */ }
                }
                if (string.IsNullOrEmpty(uid)) continue;
                if (ch.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam)
                {
                    // §10c: an APPLIED (replaced) Option 2 moved the value into a DIFFERENT parameter and REMOVED
                    // the source field — keying by the OLD ParameterId would mis-place the marker on a column that
                    // no longer exists, so skip it (its stamp is reported in the apply note). A REJECTED (Failed)
                    // Option 2 made NO change: the source field is STILL PRESENT, so overlay it on the source
                    // ParameterId and render the ATTEMPTED value (ch.NewValue) italic so the user can see what
                    // they typed and re-import just that column.
                    if (ch.Outcome == ApplyOutcome.Applied) continue;
                    outcomes[(uid, ch.ParameterId)] = ch.Outcome;
                    attempted[(uid, ch.ParameterId)] = ch.NewValue;
                    continue;
                }
                outcomes[(uid, ch.ParameterId)] = ch.Outcome;
                // Any Failed/Unverified cell shows the ATTEMPTED value (what the user typed), italic on red:
                // the model still holds the old value, so the re-read text would hide what needs fixing. The
                // user can correct just those cells (or re-import as-is — the three-way compare re-offers
                // exactly the cells whose workbook value still differs from the model). For a partially-persisted
                // bulk cell, append the C4 "N of M instances persisted" note so the report shows the partial truth
                // (W2) instead of a flat failure.
                if (ch.Outcome is ApplyOutcome.Failed or ApplyOutcome.Unverified)
                    attempted[(uid, ch.ParameterId)] =
                        string.IsNullOrEmpty(ch.OutcomeNote) ? ch.NewValue : $"{ch.NewValue} ({ch.OutcomeNote})";
            }

            var outPath = OutputPath(sourceImportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            new ExcelWriter().WriteMany(tables, outPath, outcomes, attempted);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The source path with "-run-results" inserted before the extension (foo.xlsx -> foo-run-results.xlsx).
    /// Falls back to %TEMP%\Transom\run-results.xlsx when no source path is known.</summary>
    private static string OutputPath(string sourceImportPath)
    {
        if (string.IsNullOrEmpty(sourceImportPath))
            return Path.Combine(Path.GetTempPath(), "Transom", "run-results.xlsx");

        var dir = Path.GetDirectoryName(sourceImportPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourceImportPath);
        var ext = Path.GetExtension(sourceImportPath);
        if (string.IsNullOrEmpty(ext)) ext = ".xlsx";
        return Path.Combine(dir, name + "-run-results" + ext);
    }
}

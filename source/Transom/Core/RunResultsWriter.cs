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

            // Overlay key: (row UniqueId, column ParameterId) -> outcome.
            var outcomes = new Dictionary<(string uid, int paramId), ApplyOutcome>();
            foreach (var ch in cs.Changes)
            {
                if (string.IsNullOrEmpty(ch.UniqueId)) continue;
                outcomes[(ch.UniqueId, ch.ParameterId)] = ch.Outcome;
            }

            var outPath = OutputPath(sourceImportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            new ExcelWriter().WriteMany(tables, outPath, outcomes);
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

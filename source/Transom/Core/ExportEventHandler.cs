using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Runs the export inside Revit's API context. Reads each selected schedule and writes one workbook
///     (a sheet each). When Claude-assist staging is on, writes to the exchange folder + a run-log and
///     reports the staged path for a later Finalize.
/// </summary>
public sealed class ExportEventHandler : IExternalEventHandler
{
    public List<long> ScheduleIds = new();
    public string OutputPath = "";
    public string DocTitle = "";
    public bool Stage;
    public string ExchangeFolder = "";
    /// <summary>When true, grouped built-in-parameter cells export as yellow (Claude can apply them via the
    /// definition-swap); when false they export as a distinct grey (no path to apply).</summary>
    public bool ClaudeAssistEnabled;
    public Action<string> ReportStatus = _ => { };
    public Action<string> OnStaged = _ => { };

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = DocUtil.Resolve(app, DocTitle);
            if (doc == null) { ReportStatus("Export failed: project not found."); return; }
            var reader = new ScheduleReader(doc) { UiApp = app };
            var tables = new List<ScheduleTable>();
            var failures = new List<(string Name, Exception Ex)>();
            int okCount = 0;
            foreach (var id in ScheduleIds.Distinct())
            {
                if (doc.GetElement(new ElementId(id)) is not ViewSchedule vs) continue;
                try
                {
                    var t = reader.Read(vs);
                    t.ClaudeAssistEnabled = ClaudeAssistEnabled;
                    tables.Add(t);
                    if (t.Companion != null)
                    {
                        t.Companion.ClaudeAssistEnabled = ClaudeAssistEnabled;
                        tables.Add(t.Companion); // editable component params of combined fields
                    }
                    okCount++;
                }
                catch (Exception ex)
                {
                    // One unreadable schedule must NOT abort the whole export — record it (with the full stack)
                    // and keep going so the good schedules still export. This is the diagnostic the failed
                    // "referenced object is not valid" batch lacked.
                    failures.Add((vs.Name, ex));
                }
            }

            var logPath = failures.Count > 0 ? WriteErrorLog(failures, doc.Title) : null;

            if (tables.Count == 0)
            {
                ReportStatus(failures.Count > 0
                    ? $"Export failed: every selected schedule errored. Full details (copyable):\n{logPath}"
                    : "Export failed: no schedules found.");
                return;
            }

            int elems = tables.Sum(t => t.ElementRowCount);
            string failNote = failures.Count == 0 ? "" :
                $"\n\n⚠ {failures.Count} schedule(s) could not be exported: " +
                string.Join(", ", failures.Select(f => f.Name)) +
                $"\nFull error details (copyable): {logPath}";

            if (Stage && !string.IsNullOrWhiteSpace(ExchangeFolder))
            {
                Directory.CreateDirectory(ExchangeFolder);
                var staged = Path.Combine(ExchangeFolder, Path.GetFileName(OutputPath));
                new ExcelWriter().WriteMany(tables, staged);
                RunLog.WriteExport(ExchangeFolder, tables, staged);
                OnStaged(staged);
                ReportStatus($"Staged {okCount} schedule(s) to the exchange folder. Verify with Claude, then Finalize." + failNote);
            }
            else
            {
                new ExcelWriter().WriteMany(tables, OutputPath);
                ReportStatus($"Exported {okCount} schedule(s) ({elems} element rows) to {OutputPath}" + failNote);
            }
        }
        catch (Exception ex)
        {
            ReportStatus("Export failed: " + ex.Message);
        }
    }

    /// <summary>
    ///     Writes a copyable diagnostic log of per-schedule export failures (name + type + full stack) to
    ///     %TEMP%\Transom\export-errors.log, so a single unreadable schedule is debuggable instead of an
    ///     opaque one-line "Export failed" toast with no way to copy the detail.
    /// </summary>
    private static string? WriteErrorLog(List<(string Name, Exception Ex)> failures, string docTitle)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Transom");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "export-errors.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Transom export error log");
            sb.AppendLine("Model: " + docTitle);
            sb.AppendLine("When:  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(failures.Count + " schedule(s) failed to export:");
            sb.AppendLine();
            foreach (var (name, ex) in failures)
            {
                sb.AppendLine(new string('=', 64));
                sb.AppendLine("SCHEDULE: " + name);
                sb.AppendLine("ERROR:    " + ex.GetType().FullName + ": " + ex.Message);
                sb.AppendLine("STACK:");
                sb.AppendLine(ex.StackTrace ?? "(none)");
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch { return null; }
    }

    public string GetName() => "Transom Export";
}

using System;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Runs import preview (read-only diff) and apply (transaction) in Revit's API context,
///     raised from the modeless dialog.
/// </summary>
public sealed class ImportEventHandler : IExternalEventHandler
{
    public enum Mode { Preview, Apply }

    public Mode RequestedMode = Mode.Preview;
    public string WorkbookPath = "";
    public ChangeSet? PendingChangeSet;
    public bool WriteRunLog;
    public string ExchangeFolder = "";
    public string DocTitle = "";
    public bool ProduceReport;
    public System.Collections.Generic.List<CellCorrection>? Corrections;
    /// <summary>§16 pre-analysis tab picker: when non-null, Preview analyzes ONLY these sheet tabs (the scoped
    /// ExcelReader.Read skips the rest's ReadRows + diff). Null = analyze every tab (today's behaviour). The VM sets
    /// it from the picker before raising Preview, and carries it across a corrections re-preview (§16.3).</summary>
    public System.Collections.Generic.ISet<string>? SelectedSheetTabs;

    public Action<ChangeSet> OnPreview = _ => { };
    public Action<string> OnApplied = _ => { };
    public Action<string> OnAppliedLog = _ => { };   // full apply diagnostic (incl. Revit warnings) for Copy-log
    public Action<string> OnError = _ => { };

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = DocUtil.Resolve(app, DocTitle);
            if (doc == null) { OnError("project not found"); return; }
            if (RequestedMode == Mode.Preview)
            {
                var wb = new ExcelReader().Read(WorkbookPath, SelectedSheetTabs);   // §16: scoped to picked tabs when set

                // Apply any user-supplied fixes for previously-unparseable cells. Values already in the
                // schedule's unit format are written back into the workbook; values that parse but differ
                // come back as reformat suggestions to confirm.
                System.Collections.Generic.List<ReformatSuggestion> reformats = new();
                if (Corrections != null && Corrections.Count > 0)
                    reformats = ExcelCorrector.Apply(WorkbookPath, wb, Corrections, doc.GetUnits()).Reformats;

                var cs = new Importer().BuildChangeSet(doc, wb);
                // Merge — BuildChangeSet itself parks parse-OK-but-wrong-format cells in cs.Reformats;
                // corrector suggestions go first so ShowPreview's per-cell dedupe keeps the user-typed value.
                cs.Reformats.InsertRange(0, reformats);
                if (ProduceReport && cs.Diagnostics.Count > 0)
                {
                    var dir = System.IO.Path.GetDirectoryName(WorkbookPath) ?? ".";
                    var name = System.IO.Path.GetFileNameWithoutExtension(WorkbookPath);
                    var reportPath = System.IO.Path.Combine(dir, name + "_import-report.xlsx");
                    try { DiagnosticsWriter.Write(wb, cs.Diagnostics, reportPath); cs.ReportPath = reportPath; }
                    catch { /* report is best-effort */ }
                }
                if (WriteRunLog) RunLog.WriteImport(ExchangeFolder, WorkbookPath, cs);
                OnPreview(cs);
            }
            else
            {
                if (PendingChangeSet == null) { OnError("No previewed changes to apply."); return; }

                // Some edits raise modal DialogBoxes (not Failures) that would block the apply — e.g. renaming a
                // Level prompts "rename associated views?". Auto-dismiss them (7 = No/Cancel, same as the export
                // pass) so the apply completes; record which ones for the log.
                var dialogs = new System.Collections.Generic.List<string>();
                EventHandler<Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs> dh = (_, e) =>
                {
                    try { dialogs.Add(e.DialogId); } catch { /* ignore */ }
                    try { e.OverrideResult(7); } catch { /* ignore */ }
                };
                app.DialogBoxShowing += dh;
                string status;
                try { status = new Importer().Apply(doc, PendingChangeSet); }
                finally { app.DialogBoxShowing -= dh; }

                // Post-commit verification (catches silent post-apply failures), then a run-results workbook if
                // anything Failed/Unverified — written next to the source import file.
                try
                {
                    new Importer().VerifyApplied(doc, PendingChangeSet);
                    // FIX 3 + FIX 4: rebuild the apply log + status from the FINAL by-uid verified outcomes, so the
                    // counts don't carry the mid-loop type-bound over-count and the non-applied set is split into
                    // honest buckets (write-didn't-take vs no-such-parameter). Only for the clean single-commit path:
                    // a rollback ("ROLLED BACK") or per-change recovery ("recovered after rollback") keeps its own
                    // authoritative log (those carry essential rollback/retry context the finalizer would drop, and
                    // their per-change transactions don't have the mid-loop type-bound over-count).
                    if (!status.Contains("ROLLED BACK") && !status.Contains("recovered after rollback"))
                        status = new Importer().FinalizeApplyReport(PendingChangeSet);

                    var runResultsPath = RunResultsWriter.Write(doc, PendingChangeSet, app, WorkbookPath);
                    if (!string.IsNullOrEmpty(runResultsPath)) status += $"  ·  run-results: {runResultsPath}";
                }
                catch { /* verification / report is best-effort — never block the apply */ }

                if (dialogs.Count > 0) status += $"  ·  {dialogs.Count} Revit prompt(s) auto-dismissed (see log)";

                // W1 (Task #17): if Revit's regen auto-fix deleted geometry during apply, surface it on the status
                // line too (the detail/ids are in the apply log) — a "successful" apply that removed geometry must
                // never look clean.
                if (PendingChangeSet.RevitDeletions.Count > 0)
                    status += $"  ·  ⚠ {PendingChangeSet.RevitDeletions.Count} element(s) auto-deleted by Revit during apply (see log)";

                OnApplied(status);
                OnAppliedLog(PendingChangeSet.DiagnosticLog
                    + (dialogs.Count > 0
                        ? "\n\n== Revit prompts auto-dismissed during apply ==\n  - " + string.Join("\n  - ", dialogs)
                        : ""));
            }
        }
        catch (Exception ex)
        {
            OnError(ex.Message);
        }
    }

    public string GetName() => "Transom Import";
}

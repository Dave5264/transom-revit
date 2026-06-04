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
                var wb = new ExcelReader().Read(WorkbookPath);

                // Apply any user-supplied fixes for previously-unparseable cells. Values already in the
                // schedule's unit format are written back into the workbook; values that parse but differ
                // come back as reformat suggestions to confirm.
                System.Collections.Generic.List<ReformatSuggestion> reformats = new();
                if (Corrections != null && Corrections.Count > 0)
                    reformats = ExcelCorrector.Apply(WorkbookPath, wb, Corrections, doc.GetUnits()).Reformats;

                var cs = new Importer().BuildChangeSet(doc, wb);
                cs.Reformats = reformats;
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

                if (dialogs.Count > 0) status += $"  ·  {dialogs.Count} Revit prompt(s) auto-dismissed (see log)";

                // Resolution option 3 (automated group dance) runs AFTER the import transaction has committed,
                // because the dance opens its OWN per-group-type transactions (ungroup/regroup must not be nested
                // in the import write). GroupDanceApplier manages its own dialog suppression + rollback.
                var danceChanges = PendingChangeSet.Changes
                    .Where(c => c.Resolution == GroupResolution.GroupDance && !c.Frozen).ToList();
                string danceLog = "";
                if (danceChanges.Count > 0)
                {
                    var dr = new GroupDanceApplier().Apply(doc, danceChanges, app);
                    status += $"  ·  group dance: {dr.Danced} danced, {dr.Skipped} skipped, {dr.Failed} failed";
                    danceLog = "\n\n" + dr.Log;
                }

                OnApplied(status);
                OnAppliedLog(PendingChangeSet.DiagnosticLog + danceLog + (dialogs.Count > 0
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

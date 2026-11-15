// worksharing.cs - OPTIONAL: turns the model into a workshared central, as the first project's model is (worksets
// Workset1 for the building, Site for site elements, Shared Levels and Grids). Do it only when the user wants a team model;
// a demo can stay non-workshared. After this, work in a LOCAL copy (Revit creates one when the central is opened with
// "Create New Local"); Synchronize with Central only when the user asks, and with readOnly:true.
// ARGS { CFG, CENTRAL } - CENTRAL = full path of the central file to write. Asks nothing; the hook asks before the save.
public class Pre : IFailuresPreprocessor
{
    public List<string> Msgs = new List<string>();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        bool error = false;
        foreach (var f in a.GetFailureMessages().ToList())
        {
            Msgs.Add(f.GetSeverity() + ": " + f.GetDescriptionText());
            if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else error = true;
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
var central = @"{{CENTRAL}}";
var log = new List<string>();
if (!D.IsWorkshared) { D.EnableWorksharing("Shared Levels and Grids", "Workset1"); log.Add("worksharing enabled"); }
var pre = new Pre();
using (var t = new Transaction(D, "Site workset")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    var site = new FilteredWorksetCollector(D).OfKind(WorksetKind.UserWorkset).FirstOrDefault(w => w.Name == "Site") ?? Workset.Create(D, "Site");
    // site elements onto Site: toposolids, site floors and walls, planting, site families, railings hosted on site floors
    int moved = 0;
    foreach (var e in new FilteredElementCollector(D).WhereElementIsNotElementType().ToElements()) {
        var c = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
        bool isSite = e is Toposolid || c.StartsWith("SITE") || c.StartsWith("LANDSCAPE") || e.Category?.Id.Value == (long)BuiltInCategory.OST_Planting || e.Category?.Id.Value == (long)BuiltInCategory.OST_Site;
        var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
        if (isSite && p != null && !p.IsReadOnly && p.AsInteger() != site.Id.IntegerValue) { p.Set(site.Id.IntegerValue); moved++; }
    }
    log.Add($"Site workset: {moved} elements moved, " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(central));
var so = new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 3 };
so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
D.SaveAs(central, so);
log.Add("saved as central: " + central + " (worksets: " + string.Join(", ", new FilteredWorksetCollector(D).OfKind(WorksetKind.UserWorkset).Select(w => w.Name)) + ")");
return string.Join("\n", log);

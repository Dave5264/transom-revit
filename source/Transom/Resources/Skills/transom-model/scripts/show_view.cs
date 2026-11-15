// show_view.cs - DEMO: put a view on screen so the audience sees the stage's result. Makes the view active in the
// target model, optionally sets its display style, closes the other open view tabs, and zooms to fit.
// Run through run_file.cs, readOnly:true. ARGS { CFG, VIEW, STYLE } with STYLE "" (leave) or a DisplayStyle name:
// Realistic, Shading, ShadingWithEdges, HLR, Wireframe, FlatColors, Rendering.
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
var name = "{{VIEW}}"; var style = "{{STYLE}}";
var v = new FilteredElementCollector(D).OfClass(typeof(View)).Cast<View>().FirstOrDefault(x => !x.IsTemplate && x.Name == name)
        ?? throw new Exception("no view named " + name);
if (style != "" && Enum.TryParse<DisplayStyle>(style, out var ds) && v.DisplayStyle != ds) {
    var pre = new Pre();
    using (var t = new Transaction(D, "Demo display style")) {
        var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
        t.Start(); v.DisplayStyle = ds; t.Commit();
    }
}
var uidoc = uiapp.ActiveUIDocument;
if (uidoc == null || uidoc.Document.Title != D.Title) uidoc = uiapp.OpenAndActivateDocument(D.PathName);
uidoc.ActiveView = v;
foreach (var uv in uidoc.GetOpenUIViews()) {
    if (uv.ViewId == v.Id) uv.ZoomToFit();
    else { try { uv.Close(); } catch { } }
}
return "showing " + v.Name + (style != "" ? " (" + v.DisplayStyle + ")" : "");

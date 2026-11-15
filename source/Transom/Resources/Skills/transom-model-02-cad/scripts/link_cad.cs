// link_cad.cs - links every cleaned drawing in the config into its own view (never imports), on ONE shared transform:
//   plan_links:      moved by cad_to_model_in (drawing inches -> model inches), so the levels stack;
//   elevation_links: moved only ALONG the view plane, to the plan transform plus the internal Z of zero_level
//                    (drawing Y = 0 is that level); Draw Layer = Foreground (IMPORT_BACKGROUND 0, inverted from its name).
// ThisViewOnly, colours preserved, Unit = cad_units (from the drawing's INSUNITS; never auto-detect),
// AutoCorrectAlmostVHLines OFF (it moves geometry). Links arrive pinned: unpin, move, re-pin. Older CAD in a target view
// is hidden, never deleted. A file already linked into its view is skipped, so this is safe to rerun.
// Run through run_file.cs with ARGS { CFG }. Own transaction per file, with a failure preprocessor.
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
var dir = CFG.GetProperty("cad_dir").GetString();
var unit = CFG.TryGetProperty("cad_units", out var u) ? (ImportUnit)Enum.Parse(typeof(ImportUnit), u.GetString()) : ImportUnit.Inch;
var mv = CFG.GetProperty("cad_to_model_in").EnumerateArray().Select(e => e.GetDouble() / 12).ToArray();
var log = new List<string>();
View V(string name) => new FilteredElementCollector(D).OfClass(typeof(View)).Cast<View>().First(v => !v.IsTemplate && v.Name == name);
Level Lv(string name) => new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().First(l => l.Name == name);
var jobs = new List<(string file, string view, bool elev, string zero)>();
foreach (var e in CFG.GetProperty("plan_links").EnumerateArray()) jobs.Add((e.GetProperty("file").GetString(), e.GetProperty("view").GetString(), false, null));
foreach (var e in CFG.GetProperty("elevation_links").EnumerateArray()) jobs.Add((e.GetProperty("file").GetString(), e.GetProperty("view").GetString(), true, e.GetProperty("zero_level").GetString()));
foreach (var j in jobs) {
    var path = System.IO.Path.Combine(dir, j.file);
    var view = V(j.view);
    var inView = new FilteredElementCollector(D, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => i.OwnerViewId == view.Id).ToList();
    if (inView.Any(i => i.IsLinked && D.GetElement(i.GetTypeId())?.Name == System.IO.Path.GetFileNameWithoutExtension(j.file))) { log.Add("already linked: " + j.file); continue; }
    if (!System.IO.File.Exists(path)) { log.Add("MISSING " + path); continue; }
    var pre = new Pre();
    var opt = new DWGImportOptions { ThisViewOnly = true, ColorMode = ImportColorMode.Preserved, Unit = unit,
        Placement = ImportPlacement.Origin, AutoCorrectAlmostVHLines = false, VisibleLayersOnly = true };
    using (var t = new Transaction(D, "Link " + j.file)) {
        var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
        t.Start();
        if (!D.Link(path, opt, view, out ElementId id)) { t.RollBack(); log.Add("Link returned false: " + j.file); continue; }
        var ii = (ImportInstance)D.GetElement(id);
        ii.Pinned = false;
        XYZ move;
        if (!j.elev) move = new XYZ(mv[0], mv[1], 0);
        else {
            var target = new XYZ(mv[0], mv[1], Lv(j.zero).ProjectElevation);
            var dd = target - ii.GetTotalTransform().Origin;
            move = view.RightDirection.Multiply(dd.DotProduct(view.RightDirection)) + view.UpDirection.Multiply(dd.DotProduct(view.UpDirection));
            ii.get_Parameter(BuiltInParameter.IMPORT_BACKGROUND)?.Set(0);   // Foreground
        }
        ElementTransformUtils.MoveElement(D, id, move);
        ii.Pinned = true;
        if (inView.Count > 0) view.HideElements(inView.Select(i => i.Id).ToList());
        var st = t.Commit();
        var o = ii.GetTotalTransform().Origin;
        log.Add($"{j.file} -> {j.view}: {st}, id {id.Value}, origin ({o.X:0.####}, {o.Y:0.####}, {o.Z:0.####})" + (inView.Count > 0 ? $", hid {inView.Count} older" : "") + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
    }
}
return string.Join("\n", log);

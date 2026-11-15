// demo_views.cs - DEMO: 3D views sized for watching the build. "DEMO - BUILDING SE" and "DEMO - BUILDING NW" are
// isometric, boxed to the building box + 25 ft (the SA ISO views box the whole site, so the building is small in them).
// Also hides the section box edges and level lines in them and in the SA ISO views.
// Run through run_file.cs, readOnly:true, ARGS { CFG }. Safe to rerun.
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
var bb = CFG.GetProperty("building_box_in").EnumerateArray().Select(e => e.GetDouble() / 12).ToArray();
double m = 25, x0 = bb[0] - m, y0 = bb[1] - m, x1 = bb[2] + m, y1 = bb[3] + m, cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
var lv = new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
double z0 = lv.First().ProjectElevation - 8, z1 = lv.Last().ProjectElevation + 20, zc = (z0 + z1) / 2;
var vft = new FilteredElementCollector(D).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
var pre = new Pre(); var log = new List<string>();
using (var t = new Transaction(D, "Demo 3D views")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    foreach (var (name, sx, sy) in new[] { ("DEMO - BUILDING SE", 1, -1), ("DEMO - BUILDING NW", -1, 1) }) {
        var v = new FilteredElementCollector(D).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x => !x.IsTemplate && x.Name == name) ?? View3D.CreateIsometric(D, vft.Id);
        v.Name = name;
        double r = Math.Max(x1 - x0, y1 - y0);
        var eye = new XYZ(cx + sx * r, cy + sy * r, zc + r * 0.7);
        var fwd = (new XYZ(cx, cy, zc) - eye).Normalize();
        v.SetOrientation(new ViewOrientation3D(eye, fwd.CrossProduct(XYZ.BasisZ).CrossProduct(fwd).Normalize(), fwd));
        v.SetSectionBox(new BoundingBoxXYZ { Min = new XYZ(x0, y0, z0), Max = new XYZ(x1, y1, z1) });
        v.IsSectionBoxActive = true; v.DisplayStyle = DisplayStyle.ShadingWithEdges; v.DetailLevel = ViewDetailLevel.Fine;
        foreach (var c in new[] { BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_Levels }) v.SetCategoryHidden(new ElementId(c), true);   // no box edges or level lines on screen
        log.Add(name + " " + v.Id.Value);
    }
    foreach (var v in new FilteredElementCollector(D).OfClass(typeof(View3D)).Cast<View3D>().Where(x => !x.IsTemplate && x.Name.StartsWith("SA ISO")))
        foreach (var c in new[] { BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_Levels }) v.SetCategoryHidden(new ElementId(c), true);   // the site views too
    log.Add("commit: " + t.Commit());
}
return string.Join("\n", log);

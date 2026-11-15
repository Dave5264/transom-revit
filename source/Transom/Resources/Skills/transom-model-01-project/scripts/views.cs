// views.cs - the working views of the first project's model:
//   * NORTH/SOUTH/EAST/WEST ELEVATION: one elevation marker per facade, placed OUTSIDE the building box (a marker inside
//     the building cuts it like a section), each crop sized to the building width + 10 ft and from the lowest level
//     - 6 ft to the top level + 16 ft, far clip off. Template elevations are renamed "<name> (template)" and left alone.
//   * SA ISO - NE/NW/SE/SW: isometric 3D views from each corner, Shading with Edges, Medium detail, section box =
//     site box (a tight box hides any mesh element it cuts, such as Enscape planting stand-ins).
// building_box_in and site_box_in come from the config ([xmin, ymin, xmax, ymax] model inches).
// Run through run_file.cs with ARGS { CFG }. Own transaction with a failure preprocessor. Safe to rerun.
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
double[] Box(string k) => CFG.GetProperty(k).EnumerateArray().Select(e => e.GetDouble() / 12).ToArray();
var bb = Box("building_box_in"); var sb = Box("site_box_in");
double x0 = bb[0], y0 = bb[1], x1 = bb[2], y1 = bb[3], cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
var lv = new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
double zLow = lv.First().ProjectElevation - 6, zHigh = lv.Last().ProjectElevation + 16;
var log = new List<string>();
var pre = new Pre();
using (var t = new Transaction(D, "Elevations and SA ISO views")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    var all = new FilteredElementCollector(D).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
    var planView = new FilteredElementCollector(D).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == lv.First(l => l.ProjectElevation >= -1e-6).Id);
    var evft = new FilteredElementCollector(D).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.Elevation);
    // facade name -> marker position (30 ft outside the box) and the view direction (points TOWARD the viewer)
    var facades = new[] {
        ("NORTH ELEVATION", new XYZ(cx, y1 + 30, 0), XYZ.BasisY, x1 - x0),
        ("SOUTH ELEVATION", new XYZ(cx, y0 - 30, 0), -XYZ.BasisY, x1 - x0),
        ("EAST ELEVATION", new XYZ(x1 + 30, cy, 0), XYZ.BasisX, y1 - y0),
        ("WEST ELEVATION", new XYZ(x0 - 30, cy, 0), -XYZ.BasisX, y1 - y0) };
    foreach (var v in all.Where(v => v.ViewType == ViewType.Elevation && !facades.Any(f => f.Item1 == v.Name) && !v.Name.EndsWith("(template)"))) v.Name = v.Name + " (template)";
    foreach (var (name, at, dir, width) in facades) {
        var ev = new FilteredElementCollector(D).OfClass(typeof(ViewSection)).Cast<ViewSection>().FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && v.Name == name);
        if (ev == null) {
            var mk = ElevationMarker.CreateElevationMarker(D, evft.Id, at, 96);
            for (int i = 0; i < 4 && ev == null; i++) {
                var cand = mk.CreateElevation(D, planView.Id, i);
                if (cand.ViewDirection.IsAlmostEqualTo(dir)) ev = cand; else D.Delete(cand.Id);
            }
            ev.Name = name;
        }
        // crop: local X along the view, Y up; centred on the building
        var cb = ev.CropBox; var tr = cb.Transform; var inv = tr.Inverse;
        var c = inv.OfPoint(new XYZ(cx, cy, 0));
        cb.Min = new XYZ(c.X - width / 2 - 10, zLow, cb.Min.Z); cb.Max = new XYZ(c.X + width / 2 + 10, zHigh, cb.Max.Z);
        ev.CropBox = cb; ev.CropBoxActive = true; ev.CropBoxVisible = true;
        ev.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.Set(0);
        ev.DetailLevel = ViewDetailLevel.Fine;
        log.Add($"{name}: id {ev.Id.Value}, dir {ev.ViewDirection}");
    }
    var v3ft = new FilteredElementCollector(D).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
    double r = Math.Max(x1 - x0, y1 - y0) * 1.2, zc = (zLow + zHigh) / 2;
    foreach (var (name, sx, sy) in new[] { ("SA ISO - NE", 1, 1), ("SA ISO - NW", -1, 1), ("SA ISO - SE", 1, -1), ("SA ISO - SW", -1, -1) }) {
        var v3 = new FilteredElementCollector(D).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && v.Name == name) ?? View3D.CreateIsometric(D, v3ft.Id);
        v3.Name = name;
        var eye = new XYZ(cx + sx * r, cy + sy * r, zc + r * 0.8);
        var fwd = (new XYZ(cx, cy, zc) - eye).Normalize();
        var up = fwd.CrossProduct(XYZ.BasisZ).CrossProduct(fwd).Normalize();
        v3.SetOrientation(new ViewOrientation3D(eye, up, fwd));
        v3.SetSectionBox(new BoundingBoxXYZ { Min = new XYZ(sb[0], sb[1], zLow - 20), Max = new XYZ(sb[2], sb[3], zHigh + 60) });
        v3.IsSectionBoxActive = true;
        v3.DisplayStyle = DisplayStyle.ShadingWithEdges; v3.DetailLevel = ViewDetailLevel.Medium;
        log.Add(name + ": id " + v3.Id.Value);
    }
    log.Add("commit: " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
return string.Join("\n", log);

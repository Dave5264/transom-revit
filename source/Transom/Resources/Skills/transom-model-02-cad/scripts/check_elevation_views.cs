// check_elevation_views.cs - READ ONLY. Will each elevation view capture the whole building? The footprint comes from the
// plan links' bounding boxes (a level whose box is far larger than the others is site context and is left out).
// Pass: marker in front of the whole building, crop spans the full width and from below the lowest level to above the
// roof, far clip off or deeper than the far side, the view's phase shows walls. Widening a crop or deepening the far
// offset is safe (do it and say so). ASK before moving a marker, changing a phase or detaching a template.
// Run through run_file.cs with ARGS { CFG }.
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
var plans = new FilteredElementCollector(D).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
    .Where(i => i.IsLinked && D.GetElement(i.OwnerViewId) is ViewPlan)
    .Select(i => i.get_BoundingBox((View)D.GetElement(i.OwnerViewId))).Where(b => b != null).ToList();
if (plans.Count == 0) return "no plan links yet";
var minArea = plans.Min(b => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y));
var fp = plans.Where(b => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) < 1.5 * minArea).ToList();
double x0 = fp.Min(b => b.Min.X), x1 = fp.Max(b => b.Max.X), y0 = fp.Min(b => b.Min.Y), y1 = fp.Max(b => b.Max.Y);
var lv = new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
double zLow = lv.First().ProjectElevation - 5, zHigh = lv.Last().ProjectElevation + 15;
var corners = new List<XYZ>();
foreach (var x in new[] { x0, x1 }) foreach (var y in new[] { y0, y1 }) foreach (var z in new[] { zLow, zHigh }) corners.Add(new XYZ(x, y, z));
var out_ = new List<string> { $"footprint from {fp.Count} plan links: x {x0:0.0}..{x1:0.0} ft, y {y0:0.0}..{y1:0.0} ft" };
foreach (var v in new FilteredElementCollector(D).OfClass(typeof(ViewSection)).Cast<ViewSection>().Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && !v.Name.EndsWith("(template)")))
{
    var cb = v.CropBox; var inv = cb.Transform.Inverse;
    var loc = corners.Select(p => inv.OfPoint(p)).ToList();      // local X along the view, Y up, Z toward the viewer (0 at the marker)
    bool inFront = loc.All(p => p.Z < 0);
    bool wide = loc.All(p => p.X >= cb.Min.X && p.X <= cb.Max.X), tall = loc.All(p => p.Y >= cb.Min.Y && p.Y <= cb.Max.Y);
    int farClip = v.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING).AsInteger();   // 0 = No clip
    double farNeeded = -loc.Min(p => p.Z), farOffset = v.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR).AsDouble();
    var phase = D.GetElement(v.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId())?.Name;
    bool pass = inFront && wide && tall && (farClip == 0 || farOffset >= farNeeded) && !v.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Walls));
    out_.Add($"{(pass ? "PASS" : "FAIL")} {v.Name}: markerInFront={inFront} cropWide={wide} cropTall={tall} " +
             $"farClip={(farClip == 0 ? "none" : $"{farOffset:F1} ft (needs {farNeeded:F1})")} phase={phase} template={v.ViewTemplateId != ElementId.InvalidElementId}");
}
return string.Join("\n", out_);

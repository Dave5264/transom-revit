// EXTRACT (execute_revit_code, readOnly: true). Writes, for each view in VIEWS:
//   elevation/section -> elev_<NAME>.csv  (layer,u0,z0,u1,z1 inches; u = p . view.RightDirection, z = model Z)
//                        elev_<NAME>.json ({view, R, facing}; facing = view.ViewDirection = the exterior faces it sees)
//   floor plan        -> plan_<level name>.csv (layer,x0,y0,x1,y1 inches)
// and walls.json (every straight wall: type, level, bbox, location line, orientation, exterior flag).
// Only CAD the view actually shows is read (hidden imports are skipped by the view collector).
const string DIR = @"<project>\Revit Modeling\openings";
var VIEWS = new (long id, string name)[] { (0, "SOUTH") };   // elevation views by facade name; plans: name ignored
var log = new System.Text.StringBuilder();
foreach (var (VIEW_ID, NAME0) in VIEWS) {
  var v = (View)doc.GetElement(new ElementId(VIEW_ID));
  bool elev = v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Section;
  string NAME = elev ? NAME0 : v.GenLevel.Name;
  var R = v.RightDirection;
  var sb = new System.Text.StringBuilder(); int n = 0;
  string Lay(GeometryObject g) => (doc.GetElement(g.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "?";
  void Emit(string l, XYZ a, XYZ b) {
    if (elev) sb.AppendLine($"{l},{a.DotProduct(R) * 12:0.####},{a.Z * 12:0.####},{b.DotProduct(R) * 12:0.####},{b.Z * 12:0.####}");
    else sb.AppendLine($"{l},{a.X * 12:0.####},{a.Y * 12:0.####},{b.X * 12:0.####},{b.Y * 12:0.####}");
    n++;
  }
  void Walk(GeometryElement ge) {
    foreach (var g in ge) {
      if (g is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
      else if (g is Line ln) Emit(Lay(ln), ln.GetEndPoint(0), ln.GetEndPoint(1));
      else if (g is PolyLine pl) { var c = pl.GetCoordinates(); for (int i = 0; i + 1 < c.Count; i++) Emit(Lay(pl), c[i], c[i + 1]); }
      else if (g is Curve cv) { var t = cv.Tessellate(); for (int i = 0; i + 1 < t.Count; i++) Emit(Lay(cv), t[i], t[i + 1]); }
    }
  }
  foreach (var ii in new FilteredElementCollector(doc, v.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => !i.ViewSpecific || i.OwnerViewId == v.Id))
    Walk(ii.get_Geometry(new Options { View = v }));
  System.IO.Directory.CreateDirectory(DIR);
  var file = System.IO.Path.Combine(DIR, (elev ? "elev_" : "plan_") + NAME + ".csv");
  System.IO.File.WriteAllText(file, sb.ToString());
  if (elev) System.IO.File.WriteAllText(System.IO.Path.Combine(DIR, "elev_" + NAME + ".json"),
    $"{{\"view\":{v.Id.Value},\"R\":[{R.X:0.######},{R.Y:0.######}],\"facing\":[{v.ViewDirection.X:0.######},{v.ViewDirection.Y:0.######}]}}");
  log.AppendLine($"{System.IO.Path.GetFileName(file)}: {n} segments");
}
var rows = new List<string>();
foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()) {
  if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line)) continue;
  var a = lc.Curve.GetEndPoint(0); var b = lc.Curve.GetEndPoint(1); var bb = w.get_BoundingBox(null); var lv = (Level)doc.GetElement(w.LevelId);
  bool ext = w.WallType.Function == WallFunction.Exterior;
  rows.Add("{" + $"\"id\":{w.Id.Value},\"type\":{System.Text.Json.JsonSerializer.Serialize(w.WallType.Name)},\"level\":{System.Text.Json.JsonSerializer.Serialize(lv.Name)},\"levelZ\":{lv.ProjectElevation * 12:0.###},\"z0\":{bb.Min.Z * 12:0.###},\"z1\":{bb.Max.Z * 12:0.###},\"a\":[{a.X * 12:0.###},{a.Y * 12:0.###}],\"b\":[{b.X * 12:0.###},{b.Y * 12:0.###}],\"min\":[{bb.Min.X * 12:0.###},{bb.Min.Y * 12:0.###}],\"max\":[{bb.Max.X * 12:0.###},{bb.Max.Y * 12:0.###}],\"width\":{w.Width * 12:0.###},\"o\":[{w.Orientation.X:0.####},{w.Orientation.Y:0.####}],\"ext\":{(ext ? "true" : "false")}" + "}");
}
System.IO.File.WriteAllText(System.IO.Path.Combine(DIR, "walls.json"), "[\n" + string.Join(",\n", rows) + "\n]");
var lvls = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => "{\"name\":" + System.Text.Json.JsonSerializer.Serialize(l.Name) + ",\"z\":" + (l.ProjectElevation * 12).ToString("0.###") + "}");
System.IO.File.WriteAllText(System.IO.Path.Combine(DIR, "levels.json"), "[" + string.Join(",", lvls) + "]");
log.AppendLine("walls.json: " + rows.Count + " walls");
return log.ToString();

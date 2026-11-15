// DUMP AEC WALLS (execute_autocad_code, context "application"). Writes walls_<LEVEL>.json for exterior_faces.py.
// The drawing must be OPEN IN THE EDITOR. A side database returns GeometricExtents with zero width for AEC walls (only the baseline), so
// flood-filling those loses every wall thickness, including real steps from thick wall styles. Opens read-only and closes without saving.
var FILES = new Dictionary<string, string> { { "LOWER", @"<cad>\LowerLevel.dwg" }, { "MAIN", @"<cad>\MainLevel.dwg" }, { "UPPER", @"<cad>\UpperLevel.dwg" } };
const string OUT = @"<project>\Revit Modeling\data\";
var inv = System.Globalization.CultureInfo.InvariantCulture;
string N(double v) => v.ToString("0.###", inv);
string P(Point3d p) => "[" + N(p.X) + "," + N(p.Y) + "," + N(p.Z) + "]";
string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
var report = new List<string>();
foreach (var kv in FILES) {
  Document d = null; bool opened = false;
  foreach (Document x in Application.DocumentManager) if (string.Equals(x.Name, kv.Value, StringComparison.OrdinalIgnoreCase)) d = x;
  if (d == null) { d = Application.DocumentManager.Open(kv.Value, true); opened = true; }
  var rows = new List<string>(); int zero = 0;
  using (var t = d.Database.TransactionManager.StartOpenCloseTransaction()) {
    var ms = (BlockTableRecord)t.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(d.Database), OpenMode.ForRead);
    foreach (ObjectId id in ms) {
      if (id.ObjectClass.Name != "AecDbWall") continue;
      var e = (Entity)t.GetObject(id, OpenMode.ForRead); var ty = e.GetType();
      var sp = (Point3d)ty.GetProperty("StartPoint").GetValue(e); var ep = (Point3d)ty.GetProperty("EndPoint").GetValue(e);
      var st = t.GetObject((ObjectId)ty.GetProperty("StyleId").GetValue(e), OpenMode.ForRead);
      var name = (string)st.GetType().GetProperty("Name").GetValue(st);
      double width = Convert.ToDouble(ty.GetProperty("Width").GetValue(e));
      var ex = e.GeometricExtents; bool horiz = Math.Abs(sp.Y - ep.Y) < 0.05;
      double band = horiz ? ex.MaxPoint.Y - ex.MinPoint.Y : ex.MaxPoint.X - ex.MinPoint.X;
      if (band < 0.1 && (horiz || Math.Abs(sp.X - ep.X) < 0.05)) zero++;
      rows.Add("{\"h\":" + Q(e.Handle.ToString()) + ",\"style\":" + Q(name) + ",\"layer\":" + Q(e.Layer) + ",\"start\":" + P(sp) + ",\"end\":" + P(ep) + ",\"width\":" + N(width) + ",\"ext\":[" + P(ex.MinPoint) + "," + P(ex.MaxPoint) + "]}");
    }
    t.Abort();
  }
  System.IO.File.WriteAllText(OUT + "walls_" + kv.Key + ".json", "[\n" + string.Join(",\n", rows) + "\n]");
  report.Add(kv.Key + " walls=" + rows.Count + (zero > rows.Count / 4 ? "  STOP: " + zero + " walls have zero-width extents" : ""));
  if (opened) d.CloseAndDiscard();
}
return string.Join("\n", report);

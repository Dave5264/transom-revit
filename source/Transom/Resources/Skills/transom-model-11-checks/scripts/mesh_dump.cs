// MESH DUMP (execute_revit_code, readOnly: true). Writes the shell's triangles for envelope.py. Inches, model frame.
// F = face (category, element, index, planar, normal, origin, area sf, material after " |"), T = triangle, B = window/door bounding box.
const string OUT = @"<scratch>\mesh_dump.txt";
var inv = System.Globalization.CultureInfo.InvariantCulture;
string V(XYZ p) => (p.X * 12).ToString("0.###", inv) + " " + (p.Y * 12).ToString("0.###", inv) + " " + (p.Z * 12).ToString("0.###", inv);
var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
var sb = new System.Text.StringBuilder(); int nf = 0, nt = 0, ne = 0;
void Walk(GeometryElement ge, string cat, long id) {
  foreach (GeometryObject g in ge) {
    if (g is GeometryInstance gi) { Walk(gi.GetInstanceGeometry(), cat, id); continue; }
    var s = g as Solid; if (s == null || s.Faces.IsEmpty || s.Volume <= 1e-9) continue;
    foreach (Face f in s.Faces) {
      var pf = f as PlanarFace; var mat = doc.GetElement(f.MaterialElementId)?.Name ?? "";
      sb.Append("F ").Append(cat).Append(' ').Append(id).Append(' ').Append(nf).Append(' ').Append(pf != null ? "1" : "0").Append(' ');
      if (pf != null) sb.Append(pf.FaceNormal.X.ToString("0.#####", inv) + " " + pf.FaceNormal.Y.ToString("0.#####", inv) + " " + pf.FaceNormal.Z.ToString("0.#####", inv) + " " + V(pf.Origin)); else sb.Append("0 0 0 0 0 0");
      sb.Append(' ').Append(f.Area.ToString("0.###", inv)).Append(" |").AppendLine(mat);
      var m = f.Triangulate(0.5);
      for (int i = 0; i < m.NumTriangles; i++) { var t = m.get_Triangle(i); sb.Append("T ").Append(V(t.get_Vertex(0))).Append(' ').Append(V(t.get_Vertex(1))).Append(' ').Append(V(t.get_Vertex(2))).AppendLine(); nt++; }
      nf++;
    }
  }
}
foreach (var bic in new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Fascia, BuiltInCategory.OST_Gutter })
  foreach (var el in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()) {
    var ge = el.get_Geometry(opt); if (ge == null) continue; Walk(ge, bic.ToString().Replace("OST_", ""), el.Id.Value); ne++; }
foreach (var bic in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
  foreach (var el in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()) {
    var bb = el.get_BoundingBox(null); if (bb == null) continue;
    sb.Append("B ").Append(bic.ToString().Replace("OST_", "")).Append(' ').Append(el.Id.Value).Append(' ').Append(V(bb.Min)).Append(' ').Append(V(bb.Max)).AppendLine(); ne++; }
System.IO.File.WriteAllText(OUT, sb.ToString());
return "elements=" + ne + " faces=" + nf + " tris=" + nt;

// ROOF EDGE: TWO SQUARE TIERS (execute_revit_code, readOnly: true; own transactions). For a drafted edge of a fascia board standing proud of a
// square-cut roof: every exposed edge comes back by the board's thickness (an eave by T*cos(slope) in plan, a rake by T), the base offset rises by
// T*sin(slope) so the roof plane does not move, the rafter cut becomes Two Cut - Square through the full depth (a pure square cut: the API's
// EaveCutterType has no Perpendicular), and a native Roof Fascia goes on the exposed top edges. Revit squares the eave board to the slope and keeps
// the rake board vertical. Edges that run into a wall or another roof get neither (roof_edges.py sorts them; read its list first).
// The footprint is moved in place, which keeps wall attachments and roof joins. Revit's automatic sketch dimensions make a partial move fail with
// "Base sketch for roof is invalid", so they are deleted first. A change of topology (a jog end open for part of an edge) needs a rebuilt roof.
// All slope-defining edges of a roof must be exposed (true on the first project), or their offsets need their own correction.
public class Pre : IFailuresPreprocessor
{
    public List<string> M = new List<string>();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        bool err = false;
        foreach (var f in a.GetFailureMessages().ToList()) { M.Add(f.GetSeverity() + ": " + f.GetDescriptionText() + " {" + string.Join(",", f.GetFailingElementIds().Select(i => i.Value)) + "}"); if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else err = true; }
        return err ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
const double T = 1.5, DEPTH = 7.875;                        // board thickness and depth (depth square to the slope), from the drafted edge
const string PROFILE_FAMILY = "Fascia_Flat", PROFILE_FROM = "1x8", FASCIA_FROM = "Fascia - Flat 1x8", MATERIAL = "Clad - Black";
var EXPOSED = new Dictionary<long, int[]> { };              // roof id -> indices of its exposed footprint edges, in GetProfiles() order
var log = new List<string>();
void Tx(string name, Action body) { var pre = new Pre(); using (var t = new Transaction(doc, name)) { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo); t.Start(); body(); log.Add(name + ": " + t.Commit() + (pre.M.Count > 0 ? " [" + string.Join(" | ", pre.M.Distinct()) + "]" : "")); } }
string pname = "SA " + T + " x " + DEPTH, fname = "SA Fascia " + T + " x " + DEPTH;
Tx("Fascia type " + fname, () => {
  var np = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s => s.FamilyName == PROFILE_FAMILY && s.Name == pname)
        ?? new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s => s.FamilyName == PROFILE_FAMILY && s.Name == PROFILE_FROM).Duplicate(pname) as FamilySymbol;
  np.LookupParameter("Thickness").Set(T / 12); np.LookupParameter("Height").Set(DEPTH / 12);
  var ft = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Architecture.FasciaType)).Cast<ElementType>().FirstOrDefault(x => x.Name == fname)
        ?? new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Architecture.FasciaType)).Cast<ElementType>().First(x => x.Name == FASCIA_FROM).Duplicate(fname);
  ft.LookupParameter("Profile").Set(np.Id);
  ft.LookupParameter("Material").Set(new FilteredElementCollector(doc).OfClass(typeof(Material)).First(m => m.Name == MATERIAL).Id);
});
var fasciaType = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Architecture.FasciaType)).Cast<Autodesk.Revit.DB.Architecture.FasciaType>().First(x => x.Name == fname);
foreach (var kv in EXPOSED) {
  var r = doc.GetElement(new ElementId(kv.Key)) as FootPrintRoof;
  var mcs = r.GetProfiles().Cast<ModelCurveArray>().First().Cast<ModelCurve>().ToList();
  int n = mcs.Count; var A = mcs.Select(m => m.GeometryCurve.GetEndPoint(0)).ToList(); var B = mcs.Select(m => m.GeometryCurve.GetEndPoint(1)).ToList();
  double area = 0; for (int i = 0; i < n; i++) area += A[i].X * B[i].Y - B[i].X * A[i].Y;          // > 0: counter-clockwise, interior on the left
  double slope = 0; var lines = new List<Tuple<XYZ, XYZ>>();
  for (int i = 0; i < n; i++) {
    var d = (B[i] - A[i]).Normalize(); var left = new XYZ(-d.Y, d.X, 0); var inward = area > 0 ? left : -left;
    bool exp = kv.Value.Contains(i), sl = r.get_DefinesSlope(mcs[i]); if (sl) slope = r.get_SlopeAngle(mcs[i]);
    double off = !exp ? 0 : (sl ? T * Math.Cos(Math.Atan(r.get_SlopeAngle(mcs[i]))) : T) / 12;
    lines.Add(Tuple.Create(A[i] + inward * off, d));
  }
  XYZ Meet(Tuple<XYZ, XYZ> p, Tuple<XYZ, XYZ> q) { var w = q.Item1 - p.Item1; double den = p.Item2.X * q.Item2.Y - p.Item2.Y * q.Item2.X; double t = (w.X * q.Item2.Y - w.Y * q.Item2.X) / den; return p.Item1 + p.Item2 * t; }
  var V = new List<XYZ>(); for (int i = 0; i < n; i++) V.Add(Meet(lines[(i - 1 + n) % n], lines[i]));
  double dz = T * Math.Sin(Math.Atan(slope)) / 12;
  var dims = r.GetDependentElements(new ElementClassFilter(typeof(Dimension))).ToList();
  Tx("Roof " + kv.Key + ": edges back by the board, square cut", () => {
    if (dims.Count > 0) doc.Delete(dims);
    for (int i = 0; i < n; i++) { var a = V[i]; var b = V[(i + 1) % n]; mcs[i].SetGeometryCurve(Line.CreateBound(new XYZ(a.X, a.Y, A[i].Z), new XYZ(b.X, b.Y, A[i].Z)), true); }
    var bp = r.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM); bp.Set(bp.AsDouble() + dz);
    r.get_Parameter(BuiltInParameter.ROOF_EAVE_CUT_PARAM).Set((int)EaveCutterType.TwoCutSquare);
    r.get_Parameter(BuiltInParameter.FASCIA_DEPTH_PARAM).Set(r.RoofType.GetCompoundStructure().GetWidth());
  });
  // the exposed edges' new plan lines; fascia goes on the solid's top edges lying on them
  var segs = kv.Value.Select(i => Tuple.Create(V[i], V[(i + 1) % n])).ToList();
  bool OnSeg(XYZ p, Tuple<XYZ, XYZ> s) { var d = s.Item2 - s.Item1; var dd = new XYZ(d.X, d.Y, 0); double L = dd.GetLength(); var u = dd / L; var w = new XYZ(p.X - s.Item1.X, p.Y - s.Item1.Y, 0); double along = w.DotProduct(u); return Math.Abs(w.X * u.Y - w.Y * u.X) < 0.02 / 12 && along > -0.05 / 12 && along < L + 0.05 / 12; }
  var refs = new ReferenceArray();
  foreach (var g in r.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine })) {
    var s = g as Solid; if (s == null) continue;
    foreach (Edge e in s.Edges) {
      var c = e.AsCurve() as Line; if (c == null || e.Reference == null || c.Length < 1.0 / 12) continue;
      if (!segs.Any(q => OnSeg(c.GetEndPoint(0), q) && OnSeg(c.GetEndPoint(1), q))) continue;
      bool top = false; for (int k = 0; k < 2; k++) { var f = e.GetFace(k) as PlanarFace; if (f != null && f.FaceNormal.Z > 0.3) top = true; }
      if (top) refs.Append(e.Reference);
    }
  }
  Tx("Fascia on roof " + kv.Key + " (" + refs.Size + " edges)", () => {
    var f = doc.Create.NewFascia(fasciaType, refs);
    f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("SHELL FASCIA roof " + kv.Key);
  });
  log.Add("   walls still attached to " + kv.Key + ": " + new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Count(w => w.GetAttachmentIds(AttachmentLocation.Top).Contains(r.Id) || w.GetAttachmentIds(AttachmentLocation.Base).Contains(r.Id)));
}
return string.Join("\n", log);

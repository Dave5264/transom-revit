// build_families.cs - builds the scripted families of the first project from Revit's own family templates, flex-tests
// them, and saves them to families_dir. ARGS { CFG, FAMILIES } with FAMILIES a comma list of:
//   trim_board   SA Trim Board.rfa         Generic Model face based: one board, instance Width/Height/Depth, type Trim
//                                          Material; origin = board centre; Width along the face, Height up, Depth out.
//                                          Corner boards, trim rings, head trim, bands sweeps can't do.
//   ptac_grille  SA PTAC Grille <W>x<H>    Generic Model face based: 1" frame 1 1/2" out, a dark backing, 45 degree
//                                          louver blades; Frame/Backing Material. Sizes in PTAC_SIZES below.
//   curb_stop    SA Curb Stop.rfa          Generic Model: 72" long, 6" base, 4" top, 4" high, centred; Material.
//   curb_profile SA Curb 6x18 on paving    Profile-Rail: the curb section drawn PRE-OFFSET for a railing hosted on the
//                                          asphalt (a rail Height/Offset fails "Can't create Railings"): origin on the
//                                          path 1/2" in from the asphalt edge; face 1/4" over the asphalt, back at edge + 6",
//                                          top 6" above the asphalt, bottom 12 1/2" below it, a 1/2" round on the face edge
//                                          and 1/4" on the back.
// Every family transaction has a failure preprocessor. Family documents are closed without saving after SaveAs.
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
var TPL = CFG.GetProperty("family_templates").GetString();
var dir = CFG.GetProperty("families_dir").GetString(); System.IO.Directory.CreateDirectory(dir);
var WANT = "{{FAMILIES}}".Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
var PTAC_SIZES = new[] { (42.0, 15.6, 7), (42.0, 18.0, 8) };   // width", height", blades
var log = new List<string>();
Transaction T(Document fd, string name, Pre pre) { var t = new Transaction(fd, name); var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo); t.Start(); return t; }
CurveArray Rect(double x0, double y0, double x1, double y1, double z) { var p = new[] { new XYZ(x0, y0, z), new XYZ(x1, y0, z), new XYZ(x1, y1, z), new XYZ(x0, y1, z) }; var c = new CurveArray(); for (int i = 0; i < 4; i++) c.Append(Line.CreateBound(p[i], p[(i + 1) % 4])); return c; }
void SaveClose(Document fd, string file) { try { fd.SaveAs(System.IO.Path.Combine(dir, file), new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 }); log.Add("saved " + file); } finally { fd.Close(false); } }

if (WANT.Contains("trim_board")) {
    var fd = app.NewFamilyDocument(System.IO.Path.Combine(TPL, "Generic Model face based.rft"));
    var fm = fd.FamilyManager; var fc = fd.FamilyCreate; var pre = new Pre();
    var plan = new FilteredElementCollector(fd).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => v.ViewType == ViewType.FloorPlan && v.Name == "Ref. Level");
    ReferencePlane P(string n) => new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().First(r => r.Name == n);
    Extrusion ext = null; FamilyParameter pW = null, pH = null, pD = null;
    using (var t = T(fd, "Trim board", pre)) {
        if (fm.CurrentType == null) fm.NewType("Default");
        pW = fm.AddParameter("Width", GroupTypeId.Geometry, SpecTypeId.Length, true);
        pH = fm.AddParameter("Height", GroupTypeId.Geometry, SpecTypeId.Length, true);
        pD = fm.AddParameter("Depth", GroupTypeId.Geometry, SpecTypeId.Length, true);
        var pM = fm.AddParameter("Trim Material", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
        double w = 4.5 / 12, h = 48.0 / 12, dd = 0.5 / 12;
        fm.Set(pW, w); fm.Set(pH, h); fm.Set(pD, dd);
        var L = fc.NewReferencePlane(new XYZ(-w / 2, 6, 0), new XYZ(-w / 2, -6, 0), XYZ.BasisZ, plan); L.Name = "Left Edge";
        var Rr = fc.NewReferencePlane(new XYZ(w / 2, 6, 0), new XYZ(w / 2, -6, 0), XYZ.BasisZ, plan); Rr.Name = "Right Edge";
        var B = fc.NewReferencePlane(new XYZ(6, -h / 2, 0), new XYZ(-6, -h / 2, 0), XYZ.BasisZ, plan); B.Name = "Bottom Edge";
        var Tp = fc.NewReferencePlane(new XYZ(6, h / 2, 0), new XYZ(-6, h / 2, 0), XYZ.BasisZ, plan); Tp.Name = "Top Edge";
        fd.Regenerate();
        var cx = P("Center (Left/Right)"); var cy = P("Center (Front/Back)");
        // EQ dimensions keep the board centred on the origin; labelled dimensions drive Width and Height
        var ra = new ReferenceArray(); ra.Append(L.GetReference()); ra.Append(cx.GetReference()); ra.Append(Rr.GetReference());
        fc.NewDimension(plan, Line.CreateBound(new XYZ(-6, -h / 2 - 1, 0), new XYZ(6, -h / 2 - 1, 0)), ra).AreSegmentsEqual = true;
        var rb = new ReferenceArray(); rb.Append(L.GetReference()); rb.Append(Rr.GetReference());
        fc.NewDimension(plan, Line.CreateBound(new XYZ(-6, -h / 2 - 2, 0), new XYZ(6, -h / 2 - 2, 0)), rb).FamilyLabel = pW;
        var rc = new ReferenceArray(); rc.Append(B.GetReference()); rc.Append(cy.GetReference()); rc.Append(Tp.GetReference());
        fc.NewDimension(plan, Line.CreateBound(new XYZ(-w / 2 - 1, -6, 0), new XYZ(-w / 2 - 1, 6, 0)), rc).AreSegmentsEqual = true;
        var rd = new ReferenceArray(); rd.Append(B.GetReference()); rd.Append(Tp.GetReference());
        fc.NewDimension(plan, Line.CreateBound(new XYZ(-w / 2 - 2, -6, 0), new XYZ(-w / 2 - 2, 6, 0)), rd).FamilyLabel = pH;
        var caa = new CurveArrArray(); caa.Append(Rect(-w / 2, -h / 2, w / 2, h / 2, 0));
        ext = fc.NewExtrusion(true, caa, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero)), dd);
        ext.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM).Set(0.0);
        fd.Regenerate();
        fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), pD);
        fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), pM);
        int locks = 0;   // lock each side face to its reference plane, so Width/Height move the faces
        foreach (var g in ext.get_Geometry(new Options { ComputeReferences = true })) if (g is Solid s) foreach (Face f in s.Faces) {
            if (!(f is PlanarFace pf)) continue; var n = pf.FaceNormal;
            var rp = n.X < -0.9 ? L : n.X > 0.9 ? Rr : n.Y < -0.9 ? B : n.Y > 0.9 ? Tp : null;
            if (rp != null) { fc.NewAlignment(plan, rp.GetReference(), pf.Reference); locks++; }
        }
        log.Add("trim board: " + t.Commit() + ", " + locks + " face locks");
    }
    foreach (var s in new[] { (10.0, 30.0, 1.0), (60.0, 5.5, 0.75), (4.5, 48.0, 0.5) }) {   // flex test, ending on the default
        using (var t = T(fd, "flex", pre)) { fm.Set(pW, s.Item1 / 12); fm.Set(pH, s.Item2 / 12); fm.Set(pD, s.Item3 / 12); t.Commit(); }
        var bb = ext.get_BoundingBox(null);
        log.Add($"  flex {s.Item1} x {s.Item2} x {s.Item3} -> {(bb.Max.X - bb.Min.X) * 12:0.##} x {(bb.Max.Y - bb.Min.Y) * 12:0.##} x {(bb.Max.Z - bb.Min.Z) * 12:0.##}");
    }
    SaveClose(fd, "SA Trim Board.rfa");
}
if (WANT.Contains("ptac_grille")) foreach (var size in PTAC_SIZES) {
    double W = size.Item1 / 12, H = size.Item2 / 12, fr = 1.0 / 12, dep = 1.5 / 12; int nb = size.Item3;
    var fd = app.NewFamilyDocument(System.IO.Path.Combine(TPL, "Generic Model face based.rft"));
    var before = new FilteredElementCollector(fd).OfClass(typeof(Extrusion)).ToElementIds().ToList();   // the template's own host extrusion
    var fm = fd.FamilyManager; var fc = fd.FamilyCreate; var pre = new Pre();
    using (var t = T(fd, "PTAC grille", pre)) {
        if (fm.CurrentType == null) fm.NewType("Default");
        var pF = fm.AddParameter("Frame Material", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
        var pB = fm.AddParameter("Backing Material", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
        var face = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        var fa = new CurveArrArray(); fa.Append(Rect(-W / 2, -H / 2, W / 2, H / 2, 0)); fa.Append(Rect(-W / 2 + fr, -H / 2 + fr, W / 2 - fr, H / 2 - fr, 0));
        var frame = fc.NewExtrusion(true, fa, face, dep);
        fm.AssociateElementParameterToFamilyParameter(frame.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), pF);
        var ba = new CurveArrArray(); ba.Append(Rect(-W / 2 + fr, -H / 2 + fr, W / 2 - fr, H / 2 - fr, 0));
        var back = fc.NewExtrusion(true, ba, face, 0.25 / 12);
        fm.AssociateElementParameterToFamilyParameter(back.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), pB);
        double inner = H - 2 * fr, spc = inner / nb, x0 = -W / 2 + fr;
        var bp = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(x0, 0, 0)));
        for (int k = 0; k < nb; k++) {   // blades slope down and out
            double y = -H / 2 + fr + spc * (k + 0.5), a = 0.4 / 12, zb = 0.35 / 12, zf = 1.3 / 12, th = 0.085 / 12;
            var A = new XYZ(x0, y + a, zb); var Bq = new XYZ(x0, y - a, zf); var Cq = new XYZ(x0, y - a + th, zf + th); var Dq = new XYZ(x0, y + a + th, zb + th);
            var c = new CurveArray(); c.Append(Line.CreateBound(A, Bq)); c.Append(Line.CreateBound(Bq, Cq)); c.Append(Line.CreateBound(Cq, Dq)); c.Append(Line.CreateBound(Dq, A));
            var caa = new CurveArrArray(); caa.Append(c);
            var blade = fc.NewExtrusion(true, caa, bp, W - 2 * fr);
            fm.AssociateElementParameterToFamilyParameter(blade.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), pF);
        }
        log.Add($"PTAC {size.Item1}x{size.Item2}: " + t.Commit());
    }
    var bbs = new FilteredElementCollector(fd).OfClass(typeof(Extrusion)).Where(e => !before.Contains(e.Id)).Select(e => e.get_BoundingBox(null)).ToList();
    log.Add($"  {bbs.Count} solids, {(bbs.Max(b => b.Max.X) - bbs.Min(b => b.Min.X)) * 12:0.##} x {(bbs.Max(b => b.Max.Y) - bbs.Min(b => b.Min.Y)) * 12:0.##} x {(bbs.Max(b => b.Max.Z) - bbs.Min(b => b.Min.Z)) * 12:0.##}");
    SaveClose(fd, $"SA PTAC Grille {size.Item1:0}x{size.Item2:0.#}.rfa");
}
if (WANT.Contains("curb_stop")) {
    var fd = app.NewFamilyDocument(System.IO.Path.Combine(TPL, "Generic Model.rft"));
    var fm = fd.FamilyManager; var fc = fd.FamilyCreate; var pre = new Pre();
    using (var t = T(fd, "Curb stop", pre)) {
        if (fm.CurrentType == null) fm.NewType("Default");
        var pM = fm.AddParameter("Material", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
        double L = 72.0 / 12, wb = 6.0 / 12, wt = 4.0 / 12, h = 4.0 / 12;   // section in the YZ plane, extruded along X, centred
        var sp = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(-L / 2, 0, 0)));
        var p = new[] { new XYZ(-L / 2, -wb / 2, 0), new XYZ(-L / 2, wb / 2, 0), new XYZ(-L / 2, wt / 2, h), new XYZ(-L / 2, -wt / 2, h) };
        var ca = new CurveArray(); for (int i = 0; i < 4; i++) ca.Append(Line.CreateBound(p[i], p[(i + 1) % 4]));
        var caa = new CurveArrArray(); caa.Append(ca);
        var ext = fc.NewExtrusion(true, caa, sp, L);
        fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), pM);
        log.Add("curb stop: " + t.Commit());
    }
    SaveClose(fd, "SA Curb Stop.rfa");
}
if (WANT.Contains("curb_profile")) {
    var fd = app.NewFamilyDocument(System.IO.Path.Combine(TPL, "Profile-Rail.rft"));
    var view = new FilteredElementCollector(fd).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);
    var pre = new Pre(); double I = 1.0 / 12; XYZ Q(double x, double y) => new XYZ(x * I, y * I, 0);
    using (var t = T(fd, "SA Curb 6x18 on paving", pre)) {
        // X + toward the grass, Y up; the profile is closed detail lines in the template's only plan view
        var curves = new List<Curve> {
            Line.CreateBound(Q(0.25, -12.5), Q(6.5, -12.5)),
            Line.CreateBound(Q(6.5, -12.5), Q(6.5, 5.75)),
            Arc.Create(Q(6.5, 5.75), Q(6.25, 6.0), Q(6.25 + 0.25 * Math.Cos(Math.PI / 4), 5.75 + 0.25 * Math.Sin(Math.PI / 4))),
            Line.CreateBound(Q(6.25, 6.0), Q(0.75, 6.0)),
            Arc.Create(Q(0.75, 6.0), Q(0.25, 5.5), Q(0.75 - 0.5 * Math.Cos(Math.PI / 4), 5.5 + 0.5 * Math.Sin(Math.PI / 4))),
            Line.CreateBound(Q(0.25, 5.5), Q(0.25, -12.5)) };
        foreach (var c in curves) fd.FamilyCreate.NewDetailCurve(view, c);
        log.Add("curb profile: " + t.Commit() + ", usage " + fd.OwnerFamily.get_Parameter(BuiltInParameter.FAM_PROFILE_USAGE)?.AsValueString());
    }
    SaveClose(fd, "SA Curb 6x18 on paving.rfa");
}
return string.Join("\n", log);

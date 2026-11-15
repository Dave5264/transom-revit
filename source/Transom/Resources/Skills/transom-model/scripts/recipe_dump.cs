// recipe_dump.cs - READ ONLY. Records the finished model as a recipe that recipe_build.cs replays stage by stage in a new
// project: walls (curve, type, constraints, location line, end joins, roof attachments), floors (loops incl. arcs, offset,
// slab-shape points), toposolids (loops, points, the floors that excavate them), railings (path, host, type), placed
// family instances (host, point, rotation, flips, face-hosted direction, writable instance parameters), wall sweeps, and
// direct shapes (their solids/meshes, rebuilt as new direct shapes). Lengths in model inches.
// Run through run_file.cs. ARGS: TARGET = title prefix of the model to read, OUT = json path.
var TARGET = "{{TARGET}}";
var OUT = @"{{OUT}}";
var d = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(TARGET));
double In(double ft) => Math.Round(ft * 12, 5);
double[] P(XYZ p) => new[] { In(p.X), In(p.Y), In(p.Z) };
string N(ElementId id) => id == null || id == ElementId.InvalidElementId ? null : d.GetElement(id)?.Name;
string C(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
object Crv(Curve c) { var o = new Dictionary<string, object> { ["a"] = P(c.GetEndPoint(0)), ["b"] = P(c.GetEndPoint(1)) }; if (c is Arc) o["mid"] = P(c.Evaluate(0.5, true)); return o; }
List<List<object>> Loops(Sketch sk) { var r = new List<List<object>>(); if (sk == null) return r; foreach (CurveArray ca in sk.Profile) { var l = new List<object>(); foreach (Curve c in ca) l.Add(Crv(c)); r.Add(l); } return r; }
List<double[]> Shape(SlabShapeEditor se) { var r = new List<double[]>(); if (se == null || !se.IsEnabled) return r; foreach (SlabShapeVertex v in se.SlabShapeVertices) r.Add(P(v.Position)); return r; }
object Par(Element e) {
    var o = new Dictionary<string, object>();
    foreach (Parameter p in e.Parameters) {
        if (p.IsReadOnly || p.Definition == null || !p.HasValue) continue;
        if (p.StorageType == StorageType.Double) o[p.Definition.Name] = p.AsDouble();
        else if (p.StorageType == StorageType.Integer) o[p.Definition.Name] = p.AsInteger();
        else if (p.StorageType == StorageType.String) o[p.Definition.Name] = p.AsString();
    }
    return o;
}
var walls = new List<object>();
foreach (var w in new FilteredElementCollector(d).OfClass(typeof(Wall)).Cast<Wall>()) {
    if (!(w.Location is LocationCurve lc)) continue;
    var topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
    walls.Add(new Dictionary<string, object> { ["id"] = w.Id.Value, ["type"] = w.WallType.Name, ["curve"] = Crv(lc.Curve),
        ["base_level"] = N(w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()), ["base_offset"] = In(w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble()),
        ["top_level"] = N(topId), ["top_offset"] = In(w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble()),
        ["height"] = In(w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()),
        ["location_line"] = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).AsInteger(), ["flipped"] = w.Flipped,
        ["join0"] = WallUtils.IsWallJoinAllowedAtEnd(w, 0), ["join1"] = WallUtils.IsWallJoinAllowedAtEnd(w, 1),
        ["jt0"] = lc.get_JoinType(0).ToString(), ["jt1"] = lc.get_JoinType(1).ToString(),
        ["attach_top"] = w.GetAttachmentIds(AttachmentLocation.Top).Select(i => i.Value).ToList(),
        ["attach_base"] = w.GetAttachmentIds(AttachmentLocation.Base).Select(i => i.Value).ToList(),
        ["structural"] = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() ?? 0,
        ["workset"] = d.IsWorkshared ? d.GetWorksetTable().GetWorkset(w.WorksetId).Name : null, ["comments"] = C(w) });
}
var floors = new List<object>();
foreach (var f in new FilteredElementCollector(d).OfClass(typeof(Floor)).Cast<Floor>())
    floors.Add(new Dictionary<string, object> { ["id"] = f.Id.Value, ["type"] = N(f.GetTypeId()), ["level"] = N(f.LevelId),
        ["offset"] = In(f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).AsDouble()), ["loops"] = Loops(d.GetElement(f.SketchId) as Sketch),
        ["shape"] = Shape(f.GetSlabShapeEditor()), ["structural"] = f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() ?? 0,
        ["workset"] = d.IsWorkshared ? d.GetWorksetTable().GetWorkset(f.WorksetId).Name : null, ["comments"] = C(f) });
var topos = new List<object>();
foreach (var t in new FilteredElementCollector(d).OfClass(typeof(Toposolid)).Cast<Toposolid>()) {
    if (t.HostTopoId != ElementId.InvalidElementId) continue;   // sub-divisions follow their host
    var cut = new List<long>();
    try { cut = SolidSolidCutUtils.GetCuttingSolids(t).Select(i => i.Value).ToList(); } catch { }
    topos.Add(new Dictionary<string, object> { ["id"] = t.Id.Value, ["type"] = N(t.GetTypeId()), ["level"] = N(t.LevelId),
        ["loops"] = Loops(d.GetElement(t.SketchId) as Sketch), ["points"] = Shape(t.GetSlabShapeEditor()), ["cut_by"] = cut,
        ["workset"] = d.IsWorkshared ? d.GetWorksetTable().GetWorkset(t.WorksetId).Name : null, ["comments"] = C(t) });
}
var rails = new List<object>();
foreach (var r in new FilteredElementCollector(d).OfClass(typeof(Autodesk.Revit.DB.Architecture.Railing)).Cast<Autodesk.Revit.DB.Architecture.Railing>())
    rails.Add(new Dictionary<string, object> { ["id"] = r.Id.Value, ["type"] = N(r.GetTypeId()), ["level"] = N(r.LevelId), ["host"] = r.HostId?.Value ?? -1,
        ["path"] = r.GetPath().Select(c => Crv(c)).ToList(), ["flipped"] = r.Flipped,
        ["base_offset"] = In(r.get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT_OFFSET)?.AsDouble() ?? 0), ["comments"] = C(r) });
var inst = new List<object>();
foreach (var i in new FilteredElementCollector(d).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()) {
    if (i.SuperComponent != null || i.ViewSpecific) continue;     // nested parts come with their parent; detail items are not model
    if (i.Category?.Id.Value == (long)BuiltInCategory.OST_StairsRailingBaluster) continue;   // balusters and posts belong to their railing
    if (i.Category == null || i.Category.CategoryType != CategoryType.Model) continue;
    var row = new Dictionary<string, object> { ["id"] = i.Id.Value, ["category"] = i.Category.Name, ["family"] = i.Symbol.FamilyName, ["type"] = i.Symbol.Name,
        ["level"] = N(i.LevelId), ["host"] = i.Host?.Id.Value ?? -1, ["host_kind"] = i.Host?.GetType().Name,
        ["facing_flipped"] = i.FacingFlipped, ["hand_flipped"] = i.HandFlipped, ["mirrored"] = i.Mirrored,
        ["facing"] = P(i.FacingOrientation), ["hand"] = P(i.HandOrientation), ["params"] = Par(i), ["comments"] = C(i) };
    if (i.Location is LocationPoint lp) { row["point"] = P(lp.Point); row["rotation"] = lp.Rotation; }
    if (i.HostFace != null) { try { var face = i.Host.GetGeometryObjectFromReference(i.HostFace) as PlanarFace; if (face != null) row["face_normal"] = P(face.FaceNormal); } catch { } }
    var tr = i.GetTransform(); row["origin"] = P(tr.Origin); row["basis_x"] = P(tr.BasisX); row["basis_z"] = P(tr.BasisZ);
    inst.Add(row);
}
var symbols = new List<object>();   // family types the instances use, with writable type parameters (types made in the
foreach (var sym in new FilteredElementCollector(d).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()   // project are NOT in the
         .Where(i => i.SuperComponent == null && !i.ViewSpecific).Select(i => i.Symbol).GroupBy(x => x.Id.Value).Select(gr => gr.First())) {   // exported .rfa)
    var pr = new Dictionary<string, object>();
    foreach (Parameter p in sym.Parameters) {
        if (p.IsReadOnly || p.Definition == null || !p.HasValue) continue;
        if (p.StorageType == StorageType.Double) pr[p.Definition.Name] = p.AsDouble();
        else if (p.StorageType == StorageType.Integer) pr[p.Definition.Name] = p.AsInteger();
        else if (p.StorageType == StorageType.String) pr[p.Definition.Name] = p.AsString();
        else if (p.StorageType == StorageType.ElementId && d.GetElement(p.AsElementId()) is Material mm) pr[p.Definition.Name] = "material:" + mm.Name;
    }
    symbols.Add(new Dictionary<string, object> { ["family"] = sym.FamilyName, ["type"] = sym.Name, ["category"] = sym.Category?.Name, ["params"] = pr });
}
var fascias = new List<object>();   // roof fascias: owner roof, the roof edges they run on (found by geometry: an edge whose
var roofList = new FilteredElementCollector(d).OfClass(typeof(FootPrintRoof)).Cast<FootPrintRoof>().ToList();   // midpoint touches
foreach (var f in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Fascia).WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.Architecture.Fascia>()) {   // the fascia solid)
    var owner = roofList.FirstOrDefault(r => r.GetDependentElements(new ElementCategoryFilter(BuiltInCategory.OST_Fascia)).Contains(f.Id));
    var faces = new List<Face>(); foreach (var go in f.get_Geometry(new Options())) if (go is Solid fs) foreach (Face ff in fs.Faces) faces.Add(ff);
    var segs = new List<object>();
    if (owner != null) foreach (var go in owner.get_Geometry(new Options { ComputeReferences = true })) if (go is Solid so) foreach (Edge e in so.Edges) {
        var c = e.AsCurve(); if (c == null || c.Length < 1.0 / 12) continue;
        var mid = c.Evaluate(0.5, true);
        // a fascia runs on TOP edges: one neighbour face is the roof's top (normal up), the other is not
        var n0 = (e.GetFace(0) as PlanarFace)?.FaceNormal.Z ?? 0; var n1 = (e.GetFace(1) as PlanarFace)?.FaceNormal.Z ?? 0;
        if (!((n0 > 0.2) ^ (n1 > 0.2))) continue;
        if (faces.Any(ff => { var pr = ff.Project(mid); return pr != null && pr.Distance < 0.01; })) segs.Add(Crv(c));
    }
    fascias.Add(new Dictionary<string, object> { ["id"] = f.Id.Value, ["type"] = N(f.GetTypeId()), ["roof"] = owner?.Id.Value ?? -1, ["segments"] = segs,
        ["h_offset"] = In(f.HorizontalOffset), ["v_offset"] = In(f.VerticalOffset), ["angle"] = f.Angle, ["h_flipped"] = f.HorizontalFlipped, ["v_flipped"] = f.VerticalFlipped });
}
var sweeps = new List<object>();
foreach (var s in new FilteredElementCollector(d).OfClass(typeof(WallSweep)).Cast<WallSweep>()) {
    var info = s.GetWallSweepInfo();
    sweeps.Add(new Dictionary<string, object> { ["id"] = s.Id.Value, ["type"] = N(s.GetTypeId()), ["hosts"] = s.GetHostIds().Select(x => x.Value).ToList(),
        ["sweep_type"] = info.WallSweepType.ToString(), ["vertical"] = info.IsVertical, ["side"] = info.WallSide.ToString(),
        ["distance"] = In(info.Distance), ["from"] = info.DistanceMeasuredFrom.ToString(), ["wall_offset"] = In(info.WallOffset),
        ["offset_from_level"] = In(s.get_Parameter(BuiltInParameter.WALL_SWEEP_OFFSET_PARAM)?.AsDouble() ?? 0), ["comments"] = C(s) });
}
var shapes = new List<object>();
var opt = new Options { DetailLevel = ViewDetailLevel.Fine };
foreach (var ds in new FilteredElementCollector(d).OfClass(typeof(DirectShape)).Cast<DirectShape>()) {
    var tris = new List<object>();   // [material name, x1,y1,z1, x2,y2,z2, x3,y3,z3]
    void Tri(string mat, MeshTriangle tr) => tris.Add(new object[] { mat }.Concat(P(tr.get_Vertex(0)).Concat(P(tr.get_Vertex(1))).Concat(P(tr.get_Vertex(2))).Cast<object>()).ToArray());
    foreach (var go in ds.get_Geometry(opt)) {
        if (go is Solid so) foreach (Face f in so.Faces) { var m = f.Triangulate(); var mat = N(f.MaterialElementId); for (int k = 0; k < m.NumTriangles; k++) Tri(mat, m.get_Triangle(k)); }
        else if (go is Mesh me) { var mat = N(me.MaterialElementId); for (int k = 0; k < me.NumTriangles; k++) Tri(mat, me.get_Triangle(k)); }
    }
    shapes.Add(new Dictionary<string, object> { ["id"] = ds.Id.Value, ["category"] = ds.Category?.Name, ["category_id"] = ds.Category?.Id.Value ?? -1, ["name"] = ds.Name,
        ["material"] = N(ds.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.AsElementId()) ?? N(ds.GetMaterialIds(false).FirstOrDefault()), ["triangles"] = tris, ["comments"] = C(ds) });
}
var o = new Dictionary<string, object> { ["model"] = d.Title, ["taken"] = DateTime.Now.ToString("s"), ["units"] = "model inches; rotation radians; params in Revit internal units",
    ["walls"] = walls, ["floors"] = floors, ["toposolids"] = topos, ["railings"] = rails, ["symbols"] = symbols, ["instances"] = inst, ["fascias"] = fascias, ["sweeps"] = sweeps, ["direct_shapes"] = shapes };
System.IO.File.WriteAllText(OUT, System.Text.Json.JsonSerializer.Serialize(o));
return $"symbols {symbols.Count}, walls {walls.Count}, floors {floors.Count}, toposolids {topos.Count}, railings {rails.Count}, instances {inst.Count}, fascias {fascias.Count} ({fascias.Sum(x => ((List<object>)((Dictionary<string, object>)x)["segments"]).Count)} segments), sweeps {sweeps.Count}, direct shapes {shapes.Count} -> {OUT}";

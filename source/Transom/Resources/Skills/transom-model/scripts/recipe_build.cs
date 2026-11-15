// recipe_build.cs - replays a recipe (written by recipe_dump.cs from a finished model) into the target model, ONE STAGE
// per call, in this order:
//   copytypes  - railing, fascia and sweep types copied from the SOURCE model (their profiles and materials come along)
//   families   - every .rfa in family_library loaded (types activated)
//   symbols    - family types made in the project (not in the exported .rfa) duplicated and given their type parameters
//   walls      - walls exactly as recorded (curve, type, constraints, location line, end joins, mitres)
//   floors     - floors with their loops (arcs stay arcs) and slab-shape points
//   attach     - wall tops/bases attached to the replayed roofs (needs roof_ids.json from roofs_from_table.cs)
//   topo       - toposolids NOT on an OLD/hidden workset, with their points, smoothing on
//   excavate   - the recorded floors excavate the toposolid (20-30 s each: logs to replay_log.txt; poll if the bridge times out)
//   rails      - railings on their path, re-hosted on the replayed floor
//   instances  - windows/doors in their walls, face-based families on the same wall face, the rest at their point and
//                rotation; writable instance parameters restored
//   sweeps     - wall sweeps on the replayed walls
//   fascias    - roof fascias on the replayed roofs' matching edges (needs roof_ids.json)
//   shapes     - direct shapes rebuilt from their triangles with per-face materials
// Old->new ids go to <work_dir>\id_map.json, so every stage can find what an earlier one made, and a stage rerun skips
// what already exists. ARGS { CFG, RECIPE, STAGE, SOURCE } (SOURCE = title prefix of the model to copy types from).
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
// Without load options a family whose nested shared family is already in the project (window trim) is NOT loaded and
// LoadFamily just returns false: both windows went missing on the first replay. Keep the project's nested copies.
public class LoadOpts : IFamilyLoadOptions {
    public bool OnFamilyFound(bool inUse, out bool overwrite) { overwrite = true; return true; }
    public bool OnSharedFamilyFound(Family f, bool inUse, out FamilySource src, out bool overwrite) { src = FamilySource.Project; overwrite = false; return true; }
}
public class UseDest : IDuplicateTypeNamesHandler { public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs a) => DuplicateTypeAction.UseDestinationTypes; }
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
var R = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{RECIPE}}")).RootElement;
var STAGE = "{{STAGE}}";
// ONLY (optional) = comma list of comment prefixes (SHELL, SITE, MODEL, RAIL, LANDSCAPE) or category names (Windows, Doors,
// Generic Models, Planting, Site): walls, floors, railings and instances outside it are left for a later call, so a demo
// can show the shell, the openings, the details and the site one at a time. Empty = everything.
var onlyRaw = "{{ONLY}}"; var ONLY = onlyRaw.StartsWith("{{") ? new string[0] : onlyRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
bool Pick(System.Text.Json.JsonElement r) => ONLY.Length == 0 || ONLY.Any(p =>
    (r.TryGetProperty("comments", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String && (c.GetString() ?? "").StartsWith(p)) ||
    (r.TryGetProperty("category", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String && k.GetString() == p));
var work = CFG.GetProperty("work_dir").GetString();
var mapPath = System.IO.Path.Combine(work, "id_map.json");
var map = System.IO.File.Exists(mapPath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(System.IO.File.ReadAllText(mapPath)) : new Dictionary<string, long>();
var roofPath = System.IO.Path.Combine(work, "roof_ids.json");
if (System.IO.File.Exists(roofPath)) foreach (var kv in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(System.IO.File.ReadAllText(roofPath))) map[kv.Key] = kv.Value;
var logPath = System.IO.Path.Combine(work, "replay_log.txt");
var log = new List<string>();
void L(string s) { log.Add(s); System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss ") + STAGE + ": " + s + "\n"); }
void Save() => System.IO.File.WriteAllText(mapPath, System.Text.Json.JsonSerializer.Serialize(map));
Element Mapped(long oldId) => map.TryGetValue(oldId.ToString(), out var n) ? D.GetElement(new ElementId(n)) : null;
string Str(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
double Dbl(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).GetDouble();
XYZ P(System.Text.Json.JsonElement a) => new XYZ(a[0].GetDouble() / 12, a[1].GetDouble() / 12, a[2].GetDouble() / 12);
Curve Crv(System.Text.Json.JsonElement c) => c.TryGetProperty("mid", out var m) ? Arc.Create(P(c.GetProperty("a")), P(c.GetProperty("b")), P(m)) : Line.CreateBound(P(c.GetProperty("a")), P(c.GetProperty("b")));
Level Lv(string n) => n == null ? null : new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == n);
T Type<T>(string n) where T : Element => new FilteredElementCollector(D).OfClass(typeof(T)).Cast<T>().FirstOrDefault(x => x.Name == n);
void SetC(Element e, string c) { if (!string.IsNullOrEmpty(c)) e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(c); }
bool Run(string name, Action body) {
    var pre = new Pre();
    using (var t = new Transaction(D, name)) {
        var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
        t.Start();
        try { body(); } catch (Exception ex) { t.RollBack(); L(name + ": FAILED " + ex.Message); return false; }
        var st = t.Commit();
        if (st != TransactionStatus.Committed || pre.Msgs.Any(m => m.StartsWith("Error"))) L(name + ": " + st + " [" + string.Join(" | ", pre.Msgs.Distinct().Take(6)) + "]");
        return st == TransactionStatus.Committed;
    }
}
List<CurveLoop> Loops(System.Text.Json.JsonElement loops) => loops.EnumerateArray().Select(l => CurveLoop.Create(l.EnumerateArray().Select(Crv).ToList())).ToList();
int made = 0, skipped = 0, failed = 0;

if (STAGE == "copytypes") {
    var S = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith("{{SOURCE}}"));
    var names = new HashSet<string>();
    foreach (var r in R.GetProperty("railings").EnumerateArray()) names.Add("R|" + Str(r, "type"));
    foreach (var s in R.GetProperty("sweeps").EnumerateArray()) names.Add("S|" + Str(s, "type"));
    var ids = new List<ElementId>();
    foreach (var t in new FilteredElementCollector(S).OfClass(typeof(Autodesk.Revit.DB.Architecture.RailingType)).Cast<ElementType>()) if (names.Contains("R|" + t.Name) && Type<Autodesk.Revit.DB.Architecture.RailingType>(t.Name) == null) ids.Add(t.Id);
    foreach (var t in new FilteredElementCollector(S).OfClass(typeof(Autodesk.Revit.DB.Architecture.FasciaType)).Cast<ElementType>()) if (!new FilteredElementCollector(D).OfClass(typeof(Autodesk.Revit.DB.Architecture.FasciaType)).Any(x => x.Name == t.Name)) ids.Add(t.Id);
    foreach (var sw in new FilteredElementCollector(S).OfClass(typeof(WallSweep)).Cast<WallSweep>()) { var t = S.GetElement(sw.GetTypeId()); if (t != null && !ids.Contains(t.Id) && !new FilteredElementCollector(D).OfClass(typeof(ElementType)).Any(x => x.Name == t.Name && x.Category?.Id == t.Category?.Id)) ids.Add(t.Id); }
    if (ids.Count > 0) Run("Copy types from " + S.Title, () => {
        var opts = new CopyPasteOptions(); opts.SetDuplicateTypeNamesHandler(new UseDest());
        var got = ElementTransformUtils.CopyElements(S, ids, D, Transform.Identity, opts);
        L($"copied {got.Count} types: " + string.Join(", ", ids.Select(i => S.GetElement(i).Name)));
    });
    else L("types already present");
}
else if (STAGE == "families") {
    var dir = CFG.GetProperty("family_library").GetString();
    foreach (var f in System.IO.Directory.GetFiles(dir, "*.rfa")) {
        var name = System.IO.Path.GetFileNameWithoutExtension(f);
        if (new FilteredElementCollector(D).OfClass(typeof(Family)).Any(x => x.Name == name)) { skipped++; continue; }
        bool loaded = false;
        if (Run("Load " + name, () => { if (D.LoadFamily(f, new LoadOpts(), out Family fam) && fam != null) { loaded = true; foreach (var sid in fam.GetFamilySymbolIds()) { var s = (FamilySymbol)D.GetElement(sid); if (!s.IsActive) s.Activate(); } } }) && loaded) made++; else { failed++; L("not loaded: " + name); }
    }
}
else if (STAGE == "symbols") {
    Run("Replay family types", () => {
        foreach (var r in R.GetProperty("symbols").EnumerateArray()) {
            var fam = Str(r, "family"); var name = Str(r, "type");
            var all = new FilteredElementCollector(D).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s => s.FamilyName == fam).ToList();
            if (all.Count == 0) { L("family not loaded: " + fam); failed++; continue; }
            var sym = all.FirstOrDefault(s => s.Name == name);
            if (sym == null) { sym = (FamilySymbol)all[0].Duplicate(name); made++; } else skipped++;
            foreach (var p in r.GetProperty("params").EnumerateObject()) {
                var prm = sym.LookupParameter(p.Name); if (prm == null || prm.IsReadOnly) continue;
                try {
                    if (prm.StorageType == StorageType.Double && p.Value.ValueKind == System.Text.Json.JsonValueKind.Number) prm.Set(p.Value.GetDouble());
                    else if (prm.StorageType == StorageType.Integer && p.Value.ValueKind == System.Text.Json.JsonValueKind.Number) prm.Set(p.Value.GetInt32());
                    else if (prm.StorageType == StorageType.String && p.Value.ValueKind == System.Text.Json.JsonValueKind.String) prm.Set(p.Value.GetString());
                    else if (prm.StorageType == StorageType.ElementId && p.Value.ValueKind == System.Text.Json.JsonValueKind.String && p.Value.GetString().StartsWith("material:")) {
                        var m = Type<Material>(p.Value.GetString().Substring(9)); if (m != null) prm.Set(m.Id);
                    }
                } catch { }
            }
            if (!sym.IsActive) sym.Activate();
        }
    });
}
else if (STAGE == "walls") {
    var news = new List<(Wall w, System.Text.Json.JsonElement r)>();
    Run("Replay walls", () => {
        foreach (var r in R.GetProperty("walls").EnumerateArray()) {
            if (!Pick(r)) continue;
            var oid = r.GetProperty("id").GetInt64();
            if (Mapped(oid) != null) { skipped++; continue; }
            var wt = Type<WallType>(Str(r, "type")); var bl = Lv(Str(r, "base_level"));
            if (wt == null || bl == null) { L($"wall {oid}: no type '{Str(r, "type")}' or level"); failed++; continue; }
            var crv = Crv(r.GetProperty("curve"));
            var crvAtLevel = crv.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, bl.ProjectElevation - crv.GetEndPoint(0).Z)));
            var w = Wall.Create(D, crvAtLevel, wt.Id, bl.Id, Math.Max(Dbl(r, "height") / 12, 1), 0, false, r.GetProperty("structural").GetInt32() == 1);
            w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(r.GetProperty("location_line").GetInt32());
            ((LocationCurve)w.Location).Curve = crvAtLevel;                       // the recorded line IS the location line
            var tl = Lv(Str(r, "top_level"));
            if (tl != null) { w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(tl.Id); w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(Dbl(r, "top_offset") / 12); }
            else w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(Dbl(r, "height") / 12);
            w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).Set(Dbl(r, "base_offset") / 12);   // top BEFORE base
            if (!r.GetProperty("join0").GetBoolean()) WallUtils.DisallowWallJoinAtEnd(w, 0);
            if (!r.GetProperty("join1").GetBoolean()) WallUtils.DisallowWallJoinAtEnd(w, 1);
            SetC(w, Str(r, "comments"));
            map[oid.ToString()] = w.Id.Value; news.Add((w, r)); made++;
        }
    });
    Save();
    Run("Replay wall flips and joins", () => {
        foreach (var (w, r) in news) {
            if (!w.IsValidObject) continue;
            if (r.GetProperty("flipped").GetBoolean() != w.Flipped) { var c = ((LocationCurve)w.Location).Curve; w.Flip(); ((LocationCurve)w.Location).Curve = c; }
            var lc = (LocationCurve)w.Location;
            for (int e = 0; e < 2; e++) { var jt = (JoinType)Enum.Parse(typeof(JoinType), Str(r, "jt" + e)); try { if (lc.get_JoinType(e) != jt) lc.set_JoinType(e, jt); } catch { } }
        }
    });
}
else if (STAGE == "attach") {
    Run("Replay wall attachments", () => {
        foreach (var r in R.GetProperty("walls").EnumerateArray()) {
            var w = Mapped(r.GetProperty("id").GetInt64()) as Wall; if (w == null) continue;
            foreach (var (k, loc) in new[] { ("attach_top", AttachmentLocation.Top), ("attach_base", AttachmentLocation.Base) })
                foreach (var a in r.GetProperty(k).EnumerateArray()) {
                    var target = Mapped(a.GetInt64());
                    if (target == null) { L($"wall {r.GetProperty("id").GetInt64()}: target {a.GetInt64()} not replayed yet"); failed++; continue; }
                    if (w.GetAttachmentIds(loc).Contains(target.Id)) { skipped++; continue; }
                    w.AddAttachment(target.Id, loc); made++;
                }
        }
    });
}
else if (STAGE == "floors") {
    foreach (var r in R.GetProperty("floors").EnumerateArray()) {
        if (!Pick(r)) continue;
        var oid = r.GetProperty("id").GetInt64();
        if (Mapped(oid) != null) { skipped++; continue; }
        var ft = Type<FloorType>(Str(r, "type")); var lv = Lv(Str(r, "level"));
        if (ft == null || lv == null) { L($"floor {oid}: no type '{Str(r, "type")}' or level"); failed++; continue; }
        Floor fl = null;
        if (!Run("Floor " + Str(r, "comments"), () => {
            var loops = Loops(r.GetProperty("loops")).Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, lv.ProjectElevation - l.First().GetEndPoint(0).Z)))).ToList();
            fl = Floor.Create(D, loops, ft.Id, lv.Id, r.GetProperty("structural").GetInt32() == 1, null, 0);
            fl.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(Dbl(r, "offset") / 12);
            SetC(fl, Str(r, "comments"));
        })) { failed++; continue; }
        map[oid.ToString()] = fl.Id.Value; made++; Save();
        var shape = r.GetProperty("shape").EnumerateArray().Select(P).ToList();
        if (shape.Count == 0) continue;
        Run("Shape enable", () => fl.GetSlabShapeEditor().Enable());          // commit Enable() before reading the vertices
        double top = lv.ProjectElevation + Dbl(r, "offset") / 12;
        Run("Shape points " + Str(r, "comments"), () => {
            var se = fl.GetSlabShapeEditor(); var add = new List<XYZ>();
            var have = se.SlabShapeVertices.Cast<SlabShapeVertex>().ToList();
            foreach (var p in shape) {
                var v = have.FirstOrDefault(x => Math.Abs(x.Position.X - p.X) < 0.01 && Math.Abs(x.Position.Y - p.Y) < 0.01);
                if (v != null) se.ModifySubElement(v, p.Z - top);                // an existing vertex: offset from the original top
                else add.Add(p);                                                 // a new point: ABSOLUTE z
            }
            if (add.Count > 0) se.AddPoints(add);
        });
    }
}
else if (STAGE == "topo") {
    foreach (var r in R.GetProperty("toposolids").EnumerateArray()) {
        var oid = r.GetProperty("id").GetInt64();
        if ((Str(r, "workset") ?? "").Contains("OLD")) { L($"skip superseded toposolid {oid} ({Str(r, "workset")})"); skipped++; continue; }
        if (Mapped(oid) != null) { skipped++; continue; }
        var tt = Type<ToposolidType>(Str(r, "type")); var lv = Lv(Str(r, "level"));
        var loops = Loops(r.GetProperty("loops"));
        var pts = r.GetProperty("points").EnumerateArray().Select(P).ToList();
        Toposolid ts = null;
        if (Run("Toposolid " + Str(r, "comments"), () => { ts = Toposolid.Create(D, loops, pts, tt.Id, lv.Id); SetC(ts, Str(r, "comments")); Toposolid.SetSmoothedSurface(D, true); })) { map[oid.ToString()] = ts.Id.Value; made++; Save(); }
        else failed++;
    }
}
else if (STAGE == "excavate") {
    foreach (var r in R.GetProperty("toposolids").EnumerateArray()) {
        var ts = Mapped(r.GetProperty("id").GetInt64()) as Toposolid; if (ts == null) continue;
        foreach (var c in r.GetProperty("cut_by").EnumerateArray()) {
            var f = Mapped(c.GetInt64()); if (f == null) { L("cutter " + c.GetInt64() + " not replayed"); failed++; continue; }
            if (SolidSolidCutUtils.GetCuttingSolids(ts).Contains(f.Id)) { skipped++; continue; }
            if (Run("Excavate by " + (f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? f.Id.Value.ToString()), () => ts.ExcavateBy(f.Id))) { made++; L("excavated by " + f.Id.Value); } else failed++;
        }
    }
}
else if (STAGE == "rails") {
    Run("Replay railings", () => {
        foreach (var r in R.GetProperty("railings").EnumerateArray()) {
            if (!Pick(r)) continue;
            var oid = r.GetProperty("id").GetInt64();
            if (Mapped(oid) != null) { skipped++; continue; }
            var rt = Type<Autodesk.Revit.DB.Architecture.RailingType>(Str(r, "type")); var lv = Lv(Str(r, "level"));
            if (rt == null) { L("no railing type " + Str(r, "type")); failed++; continue; }
            var path = CurveLoop.Create(r.GetProperty("path").EnumerateArray().Select(Crv).ToList());
            var rl = Autodesk.Revit.DB.Architecture.Railing.Create(D, path, rt.Id, lv.Id);
            var hostOld = r.GetProperty("host").GetInt64();
            if (hostOld != -1) { var h = Mapped(hostOld); if (h != null) rl.HostId = h.Id; else L($"railing {oid}: host {hostOld} not replayed"); }
            rl.get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT_OFFSET)?.Set(Dbl(r, "base_offset") / 12);
            if (r.GetProperty("flipped").GetBoolean() != rl.Flipped) rl.Flip();
            SetC(rl, Str(r, "comments"));
            map[oid.ToString()] = rl.Id.Value; made++;
        }
    });
}
else if (STAGE == "instances") {
    var skipCats = new HashSet<string> { "Detail Items", "Balusters" };
    var rows = R.GetProperty("instances").EnumerateArray().Where(r => !skipCats.Contains(Str(r, "category")) && Pick(r)).ToList();
    foreach (var chunk in rows.Select((r, k) => (r, k)).GroupBy(x => x.k / 100)) {
        Run("Replay instances " + chunk.Key, () => {
            foreach (var (r, _) in chunk) {
                var oid = r.GetProperty("id").GetInt64();
                if (Mapped(oid) != null) { skipped++; continue; }
                var sym = new FilteredElementCollector(D).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s => s.FamilyName == Str(r, "family") && s.Name == Str(r, "type"));
                if (sym == null) { L($"no family type {Str(r, "family")} : {Str(r, "type")}"); failed++; continue; }
                if (!sym.IsActive) sym.Activate();
                var lv = Lv(Str(r, "level"));
                FamilyInstance fi = null;
                var hostOld = r.GetProperty("host").GetInt64();
                var org = P(r.GetProperty("origin"));
                if (hostOld != -1 && Str(r, "host_kind") == "Wall") {
                    var host = Mapped(hostOld) as Wall; if (host == null) { L($"{oid}: host wall {hostOld} not replayed"); failed++; continue; }
                    if (r.TryGetProperty("face_normal", out var fn)) {                 // face-based: the same face of the new wall
                        var n = P(fn).Normalize(); Reference best = null; double bd = 1e9;
                        foreach (var go in host.get_Geometry(new Options { ComputeReferences = true })) if (go is Solid so) foreach (Face f in so.Faces)
                            if (f is PlanarFace pf && pf.FaceNormal.IsAlmostEqualTo(n, 1e-3)) { var pr = pf.Project(org); if (pr != null && pr.Distance < bd) { bd = pr.Distance; best = pf.Reference; } }
                        if (best == null) { L($"{oid}: no face on {host.Id.Value} facing {n}"); failed++; continue; }
                        fi = D.Create.NewFamilyInstance(best, org, P(r.GetProperty("basis_x")).Normalize(), sym);
                    } else {
                        fi = D.Create.NewFamilyInstance(r.TryGetProperty("point", out var pt) ? P(pt) : org, sym, host, lv, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        if (r.GetProperty("facing_flipped").GetBoolean() != fi.FacingFlipped) fi.flipFacing();
                        if (r.GetProperty("hand_flipped").GetBoolean() != fi.HandFlipped) fi.flipHand();
                    }
                } else {
                    var at = r.TryGetProperty("point", out var pt) ? P(pt) : org;
                    fi = lv != null ? D.Create.NewFamilyInstance(at, sym, lv, Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                                    : D.Create.NewFamilyInstance(at, sym, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    if (r.TryGetProperty("rotation", out var rot) && Math.Abs(rot.GetDouble()) > 1e-9)
                        ElementTransformUtils.RotateElement(D, fi.Id, Line.CreateBound(at, at + XYZ.BasisZ), rot.GetDouble());
                }
                foreach (var p in r.GetProperty("params").EnumerateObject()) {
                    var prm = fi.LookupParameter(p.Name); if (prm == null || prm.IsReadOnly) continue;
                    try {
                        if (prm.StorageType == StorageType.Double && p.Value.ValueKind == System.Text.Json.JsonValueKind.Number) prm.Set(p.Value.GetDouble());
                        else if (prm.StorageType == StorageType.Integer && p.Value.ValueKind == System.Text.Json.JsonValueKind.Number) prm.Set(p.Value.GetInt32());
                        else if (prm.StorageType == StorageType.String && p.Value.ValueKind == System.Text.Json.JsonValueKind.String) prm.Set(p.Value.GetString());
                    } catch { }
                }
                map[oid.ToString()] = fi.Id.Value; made++;
            }
        });
        Save();
    }
}
else if (STAGE == "sweeps") {
    Run("Replay wall sweeps", () => {
        foreach (var r in R.GetProperty("sweeps").EnumerateArray()) {
            var oid = r.GetProperty("id").GetInt64();
            if (Mapped(oid) != null) { skipped++; continue; }
            var host = r.GetProperty("hosts").EnumerateArray().Select(h => Mapped(h.GetInt64()) as Wall).FirstOrDefault(w => w != null);
            var st = new FilteredElementCollector(D).OfClass(typeof(ElementType)).Cast<ElementType>().FirstOrDefault(t => t.Name == Str(r, "type") && t.Category?.Id.Value == (long)BuiltInCategory.OST_Cornices);
            if (host == null || st == null) { L($"sweep {oid}: host or type '{Str(r, "type")}' missing"); failed++; continue; }
            var info = new WallSweepInfo((WallSweepType)Enum.Parse(typeof(WallSweepType), Str(r, "sweep_type")), r.GetProperty("vertical").GetBoolean()) {
                WallSide = (WallSide)Enum.Parse(typeof(WallSide), Str(r, "side")), Distance = Dbl(r, "distance") / 12,
                DistanceMeasuredFrom = (DistanceMeasuredFrom)Enum.Parse(typeof(DistanceMeasuredFrom), Str(r, "from")), WallOffset = Dbl(r, "wall_offset") / 12 };
            var sw = WallSweep.Create(host, st.Id, info);
            // a sweep recorded on several joined walls: created on the first, it wraps the joined corners on its own
            map[oid.ToString()] = sw.Id.Value; made++;
        }
    });
    Run("Sweep heights", () => {   // the height that sticks is Offset From Level, set AFTER creation
        foreach (var r in R.GetProperty("sweeps").EnumerateArray()) { var sw = Mapped(r.GetProperty("id").GetInt64()); sw?.get_Parameter(BuiltInParameter.WALL_SWEEP_OFFSET_PARAM)?.Set(Dbl(r, "offset_from_level") / 12); }
    });
}
else if (STAGE == "fascias") {
    foreach (var r in R.GetProperty("fascias").EnumerateArray()) {
      if (!Run("Fascia " + r.GetProperty("id").GetInt64(), () => {
            var oid = r.GetProperty("id").GetInt64();
            if (Mapped(oid) != null) { skipped++; return; }
            var roof = Mapped(r.GetProperty("roof").GetInt64());
            var ft = new FilteredElementCollector(D).OfClass(typeof(Autodesk.Revit.DB.Architecture.FasciaType)).Cast<ElementType>().FirstOrDefault(t => t.Name == Str(r, "type"));
            if (roof == null || ft == null) { L($"fascia {oid}: roof or type '{Str(r, "type")}' missing"); failed++; return; }
            var want = r.GetProperty("segments").EnumerateArray().Select(c => (P(c.GetProperty("a")), P(c.GetProperty("b")))).ToList();
            var refs = new ReferenceArray(); int n = 0;
            foreach (var go in roof.get_Geometry(new Options { ComputeReferences = true })) if (go is Solid so) foreach (Edge e in so.Edges) {
                var c = e.AsCurve(); if (c == null || e.Reference == null) continue;
                var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
                if (want.Any(w => (w.Item1.DistanceTo(a) < 0.005 && w.Item2.DistanceTo(b) < 0.005) || (w.Item1.DistanceTo(b) < 0.005 && w.Item2.DistanceTo(a) < 0.005))) { refs.Append(e.Reference); n++; }
            }
            if (n == 0) { L($"fascia {oid}: no matching edge on roof {roof.Id.Value}"); failed++; return; }
            var f = D.Create.NewFascia((Autodesk.Revit.DB.Architecture.FasciaType)ft, refs);
            f.HorizontalOffset = Dbl(r, "h_offset") / 12; f.VerticalOffset = Dbl(r, "v_offset") / 12; f.Angle = Dbl(r, "angle");
            if (r.GetProperty("h_flipped").GetBoolean() != f.HorizontalFlipped) f.HorizontalFlip();
            if (r.GetProperty("v_flipped").GetBoolean() != f.VerticalFlipped) f.VerticalFlip();
            if (n != want.Count) L($"fascia {oid}: {n} of {want.Count} segments matched");
            map[oid.ToString()] = f.Id.Value; made++;
      })) failed++;
    }
}
else if (STAGE == "shapes") {
    Run("Replay direct shapes", () => {
        foreach (var r in R.GetProperty("direct_shapes").EnumerateArray()) {
            var oid = r.GetProperty("id").GetInt64();
            if (Mapped(oid) != null) { skipped++; continue; }
            var tsb = new TessellatedShapeBuilder(); tsb.OpenConnectedFaceSet(false);
            foreach (var tr in r.GetProperty("triangles").EnumerateArray()) {
                var a = tr.EnumerateArray().ToList(); var mn = a[0].ValueKind == System.Text.Json.JsonValueKind.String ? a[0].GetString() : null;
                var mid = mn == null ? ElementId.InvalidElementId : (Type<Material>(mn)?.Id ?? ElementId.InvalidElementId);
                var v = Enumerable.Range(0, 3).Select(k => new XYZ(a[1 + 3 * k].GetDouble() / 12, a[2 + 3 * k].GetDouble() / 12, a[3 + 3 * k].GetDouble() / 12)).ToList();
                tsb.AddFace(new TessellatedFace(v, mid));
            }
            tsb.CloseConnectedFaceSet(); tsb.Target = TessellatedShapeBuilderTarget.AnyGeometry; tsb.Fallback = TessellatedShapeBuilderFallback.Mesh; tsb.Build();
            var ds = DirectShape.CreateElement(D, new ElementId(r.GetProperty("category_id").GetInt64()));
            ds.SetShape(tsb.GetBuildResult().GetGeometricalObjects()); ds.Name = Str(r, "name") ?? "Replayed shape";
            SetC(ds, Str(r, "comments"));
            map[oid.ToString()] = ds.Id.Value; made++;
        }
    });
}
else throw new Exception("unknown STAGE " + STAGE);
Save();
return $"{STAGE}: {made} made, {skipped} skipped, {failed} failed" + (log.Count > 0 ? "\n" + string.Join("\n", log.Take(25)) : "");

// roofs_from_table.cs - builds footprint roofs from a roof table: rows written by dump_roofs.cs from a finished model, or by
// hand from the Phase 8 roof plan (one row per roof: type, level, base offset, Rafter/Truss, eave cut, fascia depth,
// comments, and each footprint edge as end points in model inches with its slope flag, rise/run and offset; arcs carry a
// mid point). Facts it relies on (first project):
//   * With Rafter or Truss = Truss the UNDERSIDE sits at the base height along each slope-defining edge, so base offset =
//     drafted top at that eave - the vertical roof thickness.
//   * `NewFootPrintRoof(curves, level, type, out mca)` needs `var mca = new ModelCurveArray();` first; a null throws.
//   * An eave that jogs with the wall as ONE footprint roof failed "Can't make footprint roof": build one roof per eave
//     segment on the same planes and join them (the joined_with column). Seams show in 3D: look at them.
//   * Every edge that meets a wall runs to that wall's centreline, or attach leaves a slit and a post.
// Writes old->new ids to <work_dir>\roof_ids.json for attach_walls.cs and the fascia script. Safe to rerun: a roof whose
// comments already exist in the model is skipped. Run through run_file.cs with ARGS { CFG, TABLE }.
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
var table = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{TABLE}}")).RootElement.GetProperty("roofs");
var idsPath = System.IO.Path.Combine(CFG.GetProperty("work_dir").GetString(), "roof_ids.json");
var ids = System.IO.File.Exists(idsPath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(System.IO.File.ReadAllText(idsPath)) : new Dictionary<string, long>();
var log = new List<string>();
XYZ P(System.Text.Json.JsonElement a, double z) => new XYZ(a[0].GetDouble() / 12, a[1].GetDouble() / 12, z);
string C(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
var existing = new FilteredElementCollector(D).OfClass(typeof(FootPrintRoof)).Cast<FootPrintRoof>().ToList();
foreach (var row in table.EnumerateArray()) {
    var comments = row.GetProperty("comments").GetString();
    var have = existing.FirstOrDefault(r => comments != "" && C(r) == comments);
    if (have != null) { ids[row.GetProperty("id").GetInt64().ToString()] = have.Id.Value; log.Add("exists: " + comments); continue; }
    var rt = new FilteredElementCollector(D).OfClass(typeof(RoofType)).Cast<RoofType>().First(x => x.Name == row.GetProperty("type").GetString());
    var lv = new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().First(x => x.Name == row.GetProperty("level").GetString());
    double z = lv.ProjectElevation;
    var ca = new CurveArray(); var edges = new List<System.Text.Json.JsonElement>();
    foreach (var loop in row.GetProperty("loops").EnumerateArray()) foreach (var e in loop.EnumerateArray()) {
        Curve c = e.TryGetProperty("mid", out var m) ? (Curve)Arc.Create(P(e.GetProperty("a"), z), P(e.GetProperty("b"), z), P(m, z)) : Line.CreateBound(P(e.GetProperty("a"), z), P(e.GetProperty("b"), z));
        ca.Append(c); edges.Add(e);
    }
    var pre = new Pre();
    using (var t = new Transaction(D, "Roof " + comments)) {
        var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
        t.Start();
        try {
            var mca = new ModelCurveArray();
            var r = D.Create.NewFootPrintRoof(ca, lv, rt, out mca);
            void SetInt(BuiltInParameter bp, string k) { if (row.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number) r.get_Parameter(bp)?.Set(v.GetInt32()); }
            SetInt(BuiltInParameter.ROOF_RAFTER_OR_TRUSS_PARAM, "rafter_or_truss");
            SetInt(BuiltInParameter.ROOF_EAVE_CUT_PARAM, "eave_cut");
            r.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM).Set(row.GetProperty("base_offset_in").GetDouble() / 12);
            if (row.GetProperty("fascia_depth_in").GetDouble() > 0) r.get_Parameter(BuiltInParameter.FASCIA_DEPTH_PARAM)?.Set(row.GetProperty("fascia_depth_in").GetDouble() / 12);
            r.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(comments);
            int matched = 0;
            foreach (ModelCurve mc in mca) {
                var gc = mc.GeometryCurve; var mid = gc.Evaluate(0.5, true);
                var e = edges.OrderBy(x => {
                    var a = P(x.GetProperty("a"), z); var b = P(x.GetProperty("b"), z);
                    var em = x.TryGetProperty("mid", out var mm) ? P(mm, z) : (a + b) / 2;
                    return em.DistanceTo(mid);
                }).First();
                bool s = e.GetProperty("slope").GetBoolean();
                r.set_DefinesSlope(mc, s);
                if (s) r.set_SlopeAngle(mc, e.GetProperty("rise_run").GetDouble());
                r.set_Offset(mc, e.GetProperty("offset_in").GetDouble() / 12);
                matched++;
            }
            var st = t.Commit();
            if (st == TransactionStatus.Committed) ids[row.GetProperty("id").GetInt64().ToString()] = r.Id.Value;
            var bb = r.get_BoundingBox(null);
            log.Add($"{comments}: {st}, {matched} edges, z {bb.Min.Z * 12:0.0}..{bb.Max.Z * 12:0.0}" + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
        } catch (Exception ex) { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); log.Add(comments + ": FAILED " + ex.Message); }
    }
}
// joins between roofs, as recorded
var preJ = new Pre(); int joined = 0;
using (var t = new Transaction(D, "Join roofs")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(preJ); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    foreach (var row in table.EnumerateArray()) {
        if (!ids.TryGetValue(row.GetProperty("id").GetInt64().ToString(), out var a) || !row.TryGetProperty("joined_with", out var jw)) continue;
        foreach (var o in jw.EnumerateArray()) {
            if (!ids.TryGetValue(o.GetInt64().ToString(), out var b)) continue;   // joined to a wall or floor: attach_walls handles walls
            var ea = D.GetElement(new ElementId(a)); var eb = D.GetElement(new ElementId(b));
            if (ea == null || eb == null || JoinGeometryUtils.AreElementsJoined(D, ea, eb)) continue;
            try { JoinGeometryUtils.JoinGeometry(D, ea, eb); joined++; } catch (Exception ex) { log.Add($"join {a}/{b} failed: {ex.Message}"); }
        }
    }
    log.Add($"joins: {t.Commit()} {joined}" + (preJ.Msgs.Count > 0 ? " [" + string.Join(" | ", preJ.Msgs.Distinct()) + "]" : ""));
}
System.IO.File.WriteAllText(idsPath, System.Text.Json.JsonSerializer.Serialize(ids));
return string.Join("\n", log);

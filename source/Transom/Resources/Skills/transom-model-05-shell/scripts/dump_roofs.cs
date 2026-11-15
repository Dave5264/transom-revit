// dump_roofs.cs - READ ONLY. Writes every footprint roof as a roof-table row that roofs_from_table.cs rebuilds:
// type, level, base offset, rafter/truss, eave cut, fascia depth, comments, and each footprint edge (end points in model
// inches, arcs with a mid point) with its slope flag, slope (rise/run), offset and overhang. Run through run_file.cs.
// ARGS: TARGET = title prefix of the model to read, OUT = json path.
var TARGET = "{{TARGET}}";
var OUT = @"{{OUT}}";
var d = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(TARGET));
double[] P(XYZ p) => new[] { Math.Round(p.X * 12, 4), Math.Round(p.Y * 12, 4) };
var rows = new List<Dictionary<string, object>>();
foreach (var r in new FilteredElementCollector(d).OfClass(typeof(FootPrintRoof)).Cast<FootPrintRoof>()) {
    var loops = new List<List<Dictionary<string, object>>>();
    foreach (ModelCurveArray loop in r.GetProfiles()) {
        var edges = new List<Dictionary<string, object>>();
        foreach (ModelCurve mc in loop) {
            var c = mc.GeometryCurve;
            var e = new Dictionary<string, object> { ["a"] = P(c.GetEndPoint(0)), ["b"] = P(c.GetEndPoint(1)),
                ["slope"] = r.get_DefinesSlope(mc), ["rise_run"] = r.get_DefinesSlope(mc) ? Math.Round(r.get_SlopeAngle(mc), 6) : 0.0,
                ["offset_in"] = Math.Round(r.get_Offset(mc) * 12, 4) };   // get_Overhang only exists on pick-walls roofs
            if (c is Arc) e["mid"] = P(c.Evaluate(0.5, true));
            edges.Add(e);
        }
        loops.Add(edges);
    }
    var lv = d.GetElement(r.LevelId) as Level;
    rows.Add(new Dictionary<string, object> {
        ["id"] = r.Id.Value, ["type"] = d.GetElement(r.GetTypeId()).Name, ["level"] = lv?.Name,
        ["base_offset_in"] = Math.Round(r.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM).AsDouble() * 12, 4),
        ["rafter_or_truss"] = r.get_Parameter(BuiltInParameter.ROOF_RAFTER_OR_TRUSS_PARAM)?.AsInteger(),
        ["eave_cut"] = r.get_Parameter(BuiltInParameter.ROOF_EAVE_CUT_PARAM)?.AsInteger(),
        ["fascia_depth_in"] = Math.Round((r.get_Parameter(BuiltInParameter.FASCIA_DEPTH_PARAM)?.AsDouble() ?? 0) * 12, 4),
        ["comments"] = r.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
        ["joined_with"] = JoinGeometryUtils.GetJoinedElements(d, r).Select(i => i.Value).ToList(),
        ["loops"] = loops });
}
var o = new Dictionary<string, object> { ["model"] = d.Title, ["units"] = "model inches; rise_run = slope as rise/run", ["roofs"] = rows };
System.IO.File.WriteAllText(OUT, System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
return $"{rows.Count} roofs, {rows.Sum(x => ((List<List<Dictionary<string, object>>>)x["loops"]).Sum(l => l.Count))} edges -> {OUT}";

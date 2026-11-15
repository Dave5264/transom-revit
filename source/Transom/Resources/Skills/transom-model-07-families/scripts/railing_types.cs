// railing_types.cs - railing types for rails that are not handrails, built from scratch or recorded:
//   MODE=dump  (READ ONLY) writes every railing type the model uses to SPEC: top rail, handrail positions, non-continuous
//              rails (name, height, offset, profile family:type, material), baluster pattern and posts, connections.
//   MODE=build creates or updates each type in SPEC in the target model (SUFFIX appended to names, "" for real use).
// What the first project needed:
//   * CURB: no top rail, no handrails, no balusters or posts, ONE non-continuous rail at height 0 / offset 0 with the
//     curb profile (SA Curb 6x18 on paving, drawn pre-offset) and the curb material; Rail Connections = Weld, Angled Joins
//     = No Connector. Hosted on the asphalt FLOOR it follows the shape-edited surface within 0.02". A rail Height/Offset
//     or a hosted toposolid fails or zig-zags.
//   * JULIET RAIL: horizontal bars as non-continuous rails at their drafted heights, no baluster pattern, start/end posts.
// Profiles must be loaded first (build_families.cs curb_profile, or the family library).
// ARGS { CFG, MODE, SPEC, SUFFIX, SOURCE } - SOURCE = title prefix of the model to dump (MODE=dump only).
using Autodesk.Revit.DB.Architecture;
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
var MODE = "{{MODE}}"; var SPEC = @"{{SPEC}}"; var SUFFIX = "{{SUFFIX}}";
string FT(Document d, ElementId id) { var s = d.GetElement(id) as FamilySymbol; return s == null ? null : s.FamilyName + ":" + s.Name; }
string MN(Document d, ElementId id) => id == null || id == ElementId.InvalidElementId ? null : d.GetElement(id)?.Name;
if (MODE == "dump") {
    var d = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith("{{SOURCE}}"));
    var used = new HashSet<long>(new FilteredElementCollector(d).OfClass(typeof(Railing)).Select(r => r.GetTypeId().Value));
    var rows = new List<object>();
    foreach (var rt in new FilteredElementCollector(d).OfClass(typeof(RailingType)).Cast<RailingType>().Where(t => used.Contains(t.Id.Value))) {
        var rs = rt.RailStructure; var rails = new List<object>();
        for (int k = 0; k < rs.GetNonContinuousRailCount(); k++) { var r = rs.GetNonContinuousRail(k); rails.Add(new Dictionary<string, object> { ["name"] = r.Name, ["height_in"] = r.Height * 12, ["offset_in"] = r.Offset * 12, ["profile"] = FT(d, r.ProfileId), ["material"] = MN(d, r.MaterialId) }); }
        var bp = rt.BalusterPlacement; var pat = bp.BalusterPattern; var bal = new List<object>();
        for (int k = 0; k < pat.GetBalusterCount(); k++) { var b = pat.GetBaluster(k); bal.Add(new Dictionary<string, object> { ["family"] = FT(d, b.BalusterFamilyId), ["base"] = b.BaseReferenceName, ["base_offset_in"] = b.BaseOffset * 12, ["top"] = b.TopReferenceName, ["top_offset_in"] = b.TopOffset * 12, ["space_in"] = b.DistanceFromPreviousOrSpace * 12, ["offset_in"] = b.Offset * 12 }); }
        object Post(BalusterInfo b) => new Dictionary<string, object> { ["family"] = FT(d, b.BalusterFamilyId), ["base"] = b.BaseReferenceName, ["base_offset_in"] = b.BaseOffset * 12, ["top"] = b.TopReferenceName, ["top_offset_in"] = b.TopOffset * 12, ["offset_in"] = b.Offset * 12 };
        var pp = bp.PostPattern;
        rows.Add(new Dictionary<string, object> { ["name"] = rt.Name, ["use_top_rail"] = rt.LookupParameter("Use Top Rail")?.AsInteger() ?? 0, ["top_rail_type"] = MN(d, rt.TopRailType), ["top_rail_height_in"] = rt.TopRailHeight * 12,
            ["primary_handrail"] = rt.PrimaryHandRailPosition.ToString(), ["secondary_handrail"] = rt.SecondaryHandRailPosition.ToString(),
            ["rail_connections"] = rt.LookupParameter("Rail Connections")?.AsInteger(), ["angled_joins"] = rt.LookupParameter("Angled Joins")?.AsInteger(), ["tangent_joins"] = rt.LookupParameter("Tangent Joins")?.AsInteger(),
            ["rails"] = rails, ["balusters"] = bal, ["start_post"] = Post(pp.StartPost), ["end_post"] = Post(pp.EndPost), ["corner_post"] = Post(pp.CornerPost) });
    }
    System.IO.File.WriteAllText(SPEC, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["model"] = d.Title, ["railing_types"] = rows }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return rows.Count + " railing types -> " + SPEC;
}
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
var spec = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(SPEC)).RootElement.GetProperty("railing_types");
ElementId Sym(string ft) { if (string.IsNullOrEmpty(ft)) return ElementId.InvalidElementId; var p = ft.Split(':'); return new FilteredElementCollector(D).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s => s.FamilyName == p[0] && s.Name == p[1])?.Id ?? ElementId.InvalidElementId; }
ElementId Mat(string n) => string.IsNullOrEmpty(n) ? ElementId.InvalidElementId : new FilteredElementCollector(D).OfClass(typeof(Material)).FirstOrDefault(m => m.Name == n)?.Id ?? ElementId.InvalidElementId;
string S(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
var log = new List<string>(); var pre = new Pre();
using (var t = new Transaction(D, "Railing types")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    foreach (var r in spec.EnumerateArray()) {
        var name = S(r, "name") + SUFFIX;
        var rt = new FilteredElementCollector(D).OfClass(typeof(RailingType)).Cast<RailingType>().FirstOrDefault(x => x.Name == name)
              ?? (RailingType)new FilteredElementCollector(D).OfClass(typeof(RailingType)).Cast<RailingType>().First().Duplicate(name);
        rt.LookupParameter("Use Top Rail")?.Set(r.GetProperty("use_top_rail").GetInt32());
        // a handrail position can only be set while that handrail exists ("The rail has no secondary hand rail"): try each
        try { rt.PrimaryHandRailPosition = (HandRailPosition)Enum.Parse(typeof(HandRailPosition), S(r, "primary_handrail")); } catch (Exception ex) { if (S(r, "primary_handrail") != "None") log.Add(name + ": primary handrail " + ex.Message); }
        try { rt.SecondaryHandRailPosition = (HandRailPosition)Enum.Parse(typeof(HandRailPosition), S(r, "secondary_handrail")); } catch (Exception ex) { if (S(r, "secondary_handrail") != "None") log.Add(name + ": secondary handrail " + ex.Message); }
        foreach (var (k, p) in new[] { ("rail_connections", "Rail Connections"), ("angled_joins", "Angled Joins"), ("tangent_joins", "Tangent Joins") })
            if (r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number) rt.LookupParameter(p)?.Set(v.GetInt32());
        var rs = rt.RailStructure;
        while (rs.GetNonContinuousRailCount() > 0) rs.RemoveNonContinuousRail(0);
        foreach (var rr in r.GetProperty("rails").EnumerateArray()) {
            rs.AddNonContinuousRail(S(rr, "name"), rr.GetProperty("height_in").GetDouble() / 12, rr.GetProperty("offset_in").GetDouble() / 12);
            var ni = rs.GetNonContinuousRail(rs.GetNonContinuousRailCount() - 1);
            var prof = Sym(S(rr, "profile")); if (prof == ElementId.InvalidElementId) log.Add($"{name}: profile {S(rr, "profile")} not loaded");
            ni.ProfileId = prof; ni.MaterialId = Mat(S(rr, "material"));
        }
        var bp = rt.BalusterPlacement; var pat = bp.BalusterPattern;
        var bal = r.GetProperty("balusters").EnumerateArray().ToList();
        for (int k = 0; k < pat.GetBalusterCount(); k++) {
            var b = pat.GetBaluster(k);
            if (k < bal.Count) { b.BalusterFamilyId = Sym(S(bal[k], "family")); b.DistanceFromPreviousOrSpace = bal[k].GetProperty("space_in").GetDouble() / 12; }
            else b.BalusterFamilyId = ElementId.InvalidElementId;
        }
        void Post(BalusterInfo b, System.Text.Json.JsonElement j) {
            b.BalusterFamilyId = Sym(S(j, "family"));
            if (S(j, "base") != null) b.BaseReferenceName = S(j, "base"); if (S(j, "top") != null) b.TopReferenceName = S(j, "top");
            b.BaseOffset = j.GetProperty("base_offset_in").GetDouble() / 12; b.TopOffset = j.GetProperty("top_offset_in").GetDouble() / 12; b.Offset = j.GetProperty("offset_in").GetDouble() / 12;
        }
        Post(bp.PostPattern.StartPost, r.GetProperty("start_post")); Post(bp.PostPattern.EndPost, r.GetProperty("end_post")); Post(bp.PostPattern.CornerPost, r.GetProperty("corner_post"));
        log.Add($"{name}: {rs.GetNonContinuousRailCount()} rails");
    }
    log.Add("commit: " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
return string.Join("\n", log);

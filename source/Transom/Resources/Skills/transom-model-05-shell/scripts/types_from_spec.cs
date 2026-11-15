// types_from_spec.cs - builds the wall, floor, roof and toposolid types of a types spec (written by dump_types.cs, or by
// hand) in the target model: each type is duplicated from a template type of the same family (Basic wall, floor,
// foundation slab, roof, toposolid) unless one of that name exists, then its layers are set exactly: width, function,
// material, the core boundaries, end and insert wrapping, and the variable layer.
// A layer material that the model lacks gets a plain placeholder of that name (reported), so the type is never built
// on <By Category>. Run materials_from_spec.cs FIRST so the render materials exist with their textures.
// Fascia and sweep types are not built here: their profiles come from the detail scripts (roof_edge_fascia.cs, band).
// Run through run_file.cs with ARGS { CFG, SPEC }. One transaction, with a failure preprocessor. Safe to rerun.
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
var spec = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{SPEC}}")).RootElement;
var log = new List<string>();
var placeholders = new List<string>();
ElementId Mat(string name) {
    if (string.IsNullOrEmpty(name)) return ElementId.InvalidElementId;
    var m = new FilteredElementCollector(D).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
    if (m != null) return m.Id;
    placeholders.Add(name);
    return Material.Create(D, name);
}
CompoundStructure Build(System.Text.Json.JsonElement st, CompoundStructure template) {
    var rows = st.GetProperty("layers").EnumerateArray().ToList();
    var layers = rows.Select(r => new CompoundStructureLayer(r.GetProperty("width_in").GetDouble() / 12,
        (MaterialFunctionAssignment)Enum.Parse(typeof(MaterialFunctionAssignment), r.GetProperty("function").GetString()),
        Mat(r.GetProperty("material").GetString()))).ToList();
    var cs = template;
    cs.SetLayers(layers);
    int firstCore = rows.FindIndex(r => r.GetProperty("core").GetBoolean()), lastCore = rows.FindLastIndex(r => r.GetProperty("core").GetBoolean());
    if (firstCore >= 0) { cs.SetNumberOfShellLayers(ShellLayerType.Exterior, firstCore); cs.SetNumberOfShellLayers(ShellLayerType.Interior, rows.Count - 1 - lastCore); }
    if (st.TryGetProperty("end_cap", out var ec) && Enum.TryParse<EndCapCondition>(ec.GetString(), out var ecv)) cs.EndCap = ecv;
    if (st.TryGetProperty("opening_wrap", out var ow) && Enum.TryParse<OpeningWrappingCondition>(ow.GetString(), out var owv)) cs.OpeningWrapping = owv;
    if (st.TryGetProperty("variable_layer", out var vl) && vl.GetInt32() >= 0 && vl.GetInt32() < rows.Count) cs.VariableLayerIndex = vl.GetInt32();
    return cs;
}
T Get<T>(string name, Func<T, bool> isSource) where T : HostObjAttributes {
    var all = new FilteredElementCollector(D).OfClass(typeof(T)).Cast<T>().ToList();
    var hit = all.FirstOrDefault(x => x.Name == name);
    if (hit != null) return hit;
    var src = all.FirstOrDefault(isSource) ?? throw new Exception("no template type to duplicate for " + name);
    return (T)src.Duplicate(name);
}
var pre = new Pre();
using (var t = new Transaction(D, "Types from spec")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    foreach (var w in spec.GetProperty("wall_types").EnumerateArray()) {
        var name = w.GetProperty("name").GetString();
        if (w.GetProperty("kind").GetString() != "Basic") { log.Add("skip non-basic wall type " + name); continue; }
        var wt = Get<WallType>(name, x => x.Kind == WallKind.Basic && x.GetCompoundStructure() != null);
        wt.SetCompoundStructure(Build(w.GetProperty("structure"), wt.GetCompoundStructure()));
        if (Enum.TryParse<WallFunction>(w.GetProperty("function").GetString(), out var wf)) wt.Function = wf;
        log.Add($"wall  {name}: {wt.Width * 12:0.###}\"");
    }
    foreach (var f in spec.GetProperty("floor_types").EnumerateArray()) {
        var name = f.GetProperty("name").GetString(); bool fnd = f.GetProperty("foundation").GetBoolean();
        var ft = Get<FloorType>(name, x => x.IsFoundationSlab == fnd && x.GetCompoundStructure() != null);
        ft.SetCompoundStructure(Build(f.GetProperty("structure"), ft.GetCompoundStructure()));
        log.Add($"floor {name}: {ft.GetCompoundStructure().GetWidth() * 12:0.###}\"" + (fnd ? " (foundation)" : ""));
    }
    foreach (var r in spec.GetProperty("roof_types").EnumerateArray()) {
        var name = r.GetProperty("name").GetString();
        var rt = Get<RoofType>(name, x => x.GetCompoundStructure() != null);
        rt.SetCompoundStructure(Build(r.GetProperty("structure"), rt.GetCompoundStructure()));
        log.Add($"roof  {name}: {rt.GetCompoundStructure().GetWidth() * 12:0.###}\"");
    }
    if (spec.TryGetProperty("toposolid_types", out var tts)) foreach (var r in tts.EnumerateArray()) {
        var name = r.GetProperty("name").GetString();
        var tt = Get<ToposolidType>(name, x => x.GetCompoundStructure() != null);
        tt.SetCompoundStructure(Build(r.GetProperty("structure"), tt.GetCompoundStructure()));
        log.Add($"topo  {name}: {tt.GetCompoundStructure().GetWidth() * 12:0.###}\"");
    }
    log.Add("commit: " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
if (placeholders.Count > 0) log.Add("placeholder materials (no render appearance; give them one if they show): " + string.Join(", ", placeholders.Distinct()));
return string.Join("\n", log);

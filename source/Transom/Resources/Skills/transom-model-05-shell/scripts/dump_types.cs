// dump_types.cs - READ ONLY. Writes the definitions of every wall, floor and roof type the model USES (layers, functions,
// materials, wrapping), plus the fascia and sweep types, to a json spec that types_from_spec.cs rebuilds in a new project.
// Run through run_file.cs. ARGS: TARGET = title prefix of the model to read, OUT = json path.
var TARGET = "{{TARGET}}";
var OUT = @"{{OUT}}";
var d = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(TARGET));
string MN(ElementId id) => id == null || id == ElementId.InvalidElementId ? "" : d.GetElement(id)?.Name ?? "";
object Layers(CompoundStructure cs) {
    if (cs == null) return null;
    var ls = cs.GetLayers();
    return new Dictionary<string, object> {
        ["layers"] = ls.Select((l, i) => new Dictionary<string, object> {
            ["width_in"] = Math.Round(l.Width * 12, 4), ["function"] = l.Function.ToString(), ["material"] = MN(l.MaterialId),
            ["core"] = i >= cs.GetFirstCoreLayerIndex() && i <= cs.GetLastCoreLayerIndex() }).ToList(),
        ["shell_ext"] = cs.GetNumberOfShellLayers(ShellLayerType.Exterior), ["shell_int"] = cs.GetNumberOfShellLayers(ShellLayerType.Interior),
        ["end_cap"] = cs.EndCap.ToString(), ["opening_wrap"] = cs.OpeningWrapping.ToString(),
        ["variable_layer"] = cs.VariableLayerIndex };
}
var used = new HashSet<long>();
foreach (var e in new FilteredElementCollector(d).WhereElementIsNotElementType().ToElements()) { var t = e.GetTypeId(); if (t != null && t != ElementId.InvalidElementId) used.Add(t.Value); }
var walls = new FilteredElementCollector(d).OfClass(typeof(WallType)).Cast<WallType>().Where(t => used.Contains(t.Id.Value))
    .Select(t => new Dictionary<string, object> { ["name"] = t.Name, ["kind"] = t.Kind.ToString(), ["function"] = t.Function.ToString(),
        ["width_in"] = Math.Round(t.Width * 12, 4), ["structure"] = Layers(t.GetCompoundStructure()) }).ToList();
var floors = new FilteredElementCollector(d).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => used.Contains(t.Id.Value))
    .Select(t => new Dictionary<string, object> { ["name"] = t.Name, ["foundation"] = t.IsFoundationSlab, ["structure"] = Layers(t.GetCompoundStructure()) }).ToList();
var roofs = new FilteredElementCollector(d).OfClass(typeof(RoofType)).Cast<RoofType>().Where(t => used.Contains(t.Id.Value))
    .Select(t => new Dictionary<string, object> { ["name"] = t.Name, ["structure"] = Layers(t.GetCompoundStructure()) }).ToList();
var topo = new FilteredElementCollector(d).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().Where(t => used.Contains(t.Id.Value))
    .Select(t => new Dictionary<string, object> { ["name"] = t.Name, ["structure"] = Layers(t.GetCompoundStructure()) }).ToList();
var fascia = new FilteredElementCollector(d).OfClass(typeof(Autodesk.Revit.DB.Architecture.FasciaType)).Cast<ElementType>().Where(t => used.Contains(t.Id.Value))
    .Select(t => new Dictionary<string, object> { ["name"] = t.Name,
        ["profile"] = MN(t.LookupParameter("Profile")?.AsElementId()), ["material"] = MN(t.LookupParameter("Material")?.AsElementId()) }).ToList();
var sweepTypes = new Dictionary<long, ElementType>();
foreach (var sw in new FilteredElementCollector(d).OfClass(typeof(WallSweep)).Cast<WallSweep>()) { var st = d.GetElement(sw.GetTypeId()) as ElementType; if (st != null) sweepTypes[st.Id.Value] = st; }
var sweeps = sweepTypes.Values.Select(t => new Dictionary<string, object> { ["name"] = t.Name,
    ["profile"] = MN(t.LookupParameter("Profile")?.AsElementId()), ["material"] = MN(t.LookupParameter("Material")?.AsElementId()) }).ToList();
var o = new Dictionary<string, object> { ["model"] = d.Title, ["wall_types"] = walls, ["floor_types"] = floors, ["roof_types"] = roofs,
    ["toposolid_types"] = topo, ["fascia_types"] = fascia, ["sweep_types"] = sweeps };
System.IO.File.WriteAllText(OUT, System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
return $"wall {walls.Count}, floor {floors.Count}, roof {roofs.Count}, toposolid {topo.Count}, fascia {fascia.Count}, sweep {sweeps.Count} types -> {OUT}";

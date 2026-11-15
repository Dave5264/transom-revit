// inventory.cs - READ ONLY. What the model holds, by kind, so a demo run can be compared with the reference result.
// Run through run_file.cs. ARGS: TARGET = title prefix of the model to read, OUT = json file to write ("" = no file).
var TARGET = "{{TARGET}}";
var OUT = @"{{OUT}}";
var d = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(TARGET));
string C(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
string Tag(Element e) { var c = C(e); var w = c.Split(' '); return w.Length >= 2 ? w[0] + " " + w[1] : c; }
var res = new SortedDictionary<string, int>();
void Add(string k) { res[k] = res.TryGetValue(k, out var n) ? n + 1 : 1; }
string N(ElementId id) => d.GetElement(id)?.Name ?? "?";

foreach (var w in new FilteredElementCollector(d).OfClass(typeof(Wall)).Cast<Wall>()) Add("wall | " + (w.WallType?.Name ?? "?"));
foreach (var f in new FilteredElementCollector(d).OfClass(typeof(Floor)).Cast<Floor>()) Add("floor | " + N(f.GetTypeId()));
foreach (var r in new FilteredElementCollector(d).OfClass(typeof(FootPrintRoof)).Cast<FootPrintRoof>()) Add("roof | " + N(r.GetTypeId()));
foreach (var r in new FilteredElementCollector(d).OfClass(typeof(Autodesk.Revit.DB.Architecture.Railing)).Cast<Element>()) Add("railing | " + N(r.GetTypeId()));
foreach (var f in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Fascia).WhereElementIsNotElementType()) Add("fascia | " + N(f.GetTypeId()));
foreach (var s in new FilteredElementCollector(d).OfClass(typeof(WallSweep)).Cast<Element>()) Add("wall sweep | " + N(s.GetTypeId()));
foreach (var t in new FilteredElementCollector(d).OfClass(typeof(Toposolid)).Cast<Element>()) Add("toposolid | " + N(t.GetTypeId()));
foreach (var s in new FilteredElementCollector(d).OfClass(typeof(DirectShape)).Cast<Element>()) Add("directshape | " + Tag(s));
foreach (var i in new FilteredElementCollector(d).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
    Add((i.Category?.Name ?? "?") + " | " + (i.Symbol?.FamilyName ?? "?") + (i.Category?.Id.Value == (long)BuiltInCategory.OST_Planting ? "" : " : " + (i.Symbol?.Name ?? "?")));
foreach (var l in new FilteredElementCollector(d).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()) Add((l.IsLinked ? "cad link | " : "cad import | ") + N(l.GetTypeId()));
foreach (var m in new FilteredElementCollector(d).OfClass(typeof(Material)).Cast<Material>().Where(m => m.Name.StartsWith("SA "))) Add("material SA | " + m.Name);
var levels = new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation)
    .Select(l => $"{l.Name}: internal {l.ProjectElevation:0.###} ft, Elevation {l.Elevation:0.###} ft").ToList();
var summary = res.GroupBy(kv => kv.Key.Split('|')[0].Trim()).Select(g => $"{g.Key}: {g.Sum(x => x.Value)}").ToList();
if (OUT != "") {
    var o = new Dictionary<string, object> { ["model"] = d.Title, ["taken"] = DateTime.Now.ToString("s"), ["levels"] = levels, ["counts"] = res };
    System.IO.File.WriteAllText(OUT, System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
return string.Join("\n", levels) + "\n" + string.Join("\n", summary) + (OUT != "" ? "\nwrote " + OUT : "");

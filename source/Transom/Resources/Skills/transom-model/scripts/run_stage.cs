// run_stage.cs - DEMO: one named stage of the demonstration rebuild = one short call. Runs that stage's scripts in order
// through the bridge's script host, then puts the stage's view on screen. Send it with run_file.cs (readOnly:true) and
// ARGS { CFG, STAGE }. Stages, in demo order (timings from the 2026-11-13 run):
//   project   new model, datum, levels, views; shows SOUTH ELEVATION (levels)                 ~12 s
//   links     the cleaned CAD linked on one transform, elevation views checked; MAIN plan     ~6 s
//   materials demo 3D views, 32 materials, 21 wall/floor/roof types                            ~10 s
//   families  the 60-family library loaded (no view change)                                   ~85 s (bridge times out: poll)
//   shell     family/fascia/sweep/railing types, 15 roofs, 58 shell walls, slabs, 40 attachments,
//             11 fascias; DEMO - BUILDING SE                                                  ~24 s
//   openings  86 windows, 3 doors, 14 Juliet rails; DEMO - BUILDING SE                         ~19 s
//   details   the scripted families rebuilt, 292 trim boards and grilles, 8 band sweeps; Realistic ~18 s
//   site      48 site walls, 20 site floors, the grass toposolid, curbs, striping, stops; SA ISO - SE ~29 s
//   excavate  the 19 floors cut the toposolid (no view change)                                ~170 s (poll)
//   landscape 215 planting pieces and 2 benches; SA ISO - SE Realistic                        ~6 s
//   finale    inventory to <work_dir>\demo_inventory.json; DEMO - BUILDING NW Realistic         ~3 s
// Every stage appends "<time> <stage> DONE in <s>s" to <work_dir>\demo_log.txt, so a long one can be polled.
var CFGP = @"{{CFG}}";
var STAGE = "{{STAGE}}";
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(CFGP)).RootElement;
string C(string k) => CFG.GetProperty(k).GetString();
var SK = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills") + "\\";
var REF = C("reference_dir") + "\\"; var REC = REF + "recipe.json"; var RB = SK + @"transom-model\scripts\recipe_build.cs";
var WORK = C("work_dir"); var TITLE = C("model_title");
Dictionary<string, string> A(params string[] kv) { var d = new Dictionary<string, string> { ["CFG"] = CFGP }; for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1]; return d; }
(string, Dictionary<string, string>) R(string stage, string only) => (RB, A("RECIPE", REC, "STAGE", stage, "ONLY", only, "SOURCE", ""));
(string, Dictionary<string, string>) Show(string view, string style) => (SK + @"transom-model\scripts\show_view.cs", A("VIEW", view, "STYLE", style));
var stages = new Dictionary<string, List<(string, Dictionary<string, string>)>> {
    ["project"] = new() { (SK + @"transom-model-01-project\scripts\new_project.cs", A()), (SK + @"transom-model-01-project\scripts\levels.cs", A()),
                          (SK + @"transom-model-01-project\scripts\views.cs", A()), Show("SOUTH ELEVATION", "") },
    ["links"] = new() { (SK + @"transom-model-02-cad\scripts\link_cad.cs", A()), (SK + @"transom-model-02-cad\scripts\check_elevation_views.cs", A()), Show("MAIN LEVEL - PLAN", "") },
    ["materials"] = new() { (SK + @"transom-model\scripts\demo_views.cs", A()), (SK + @"transom-model-04-materials\scripts\materials_from_spec.cs", A("SPEC", REF + "materials.json")),
                            (SK + @"transom-model-05-shell\scripts\types_from_spec.cs", A("SPEC", REF + "types.json")) },
    ["families"] = new() { R("families", "") },
    ["shell"] = new() { R("symbols", ""), (SK + @"transom-model-05-shell\scripts\hosted_sweep_types.cs", A("SPEC", REF + "types.json")),
                        (SK + @"transom-model-07-families\scripts\railing_types.cs", A("MODE", "build", "SPEC", REF + "railing_types.json", "SUFFIX", "", "SOURCE", "")),
                        (SK + @"transom-model-05-shell\scripts\roofs_from_table.cs", A("TABLE", REF + "roof_table.json")),
                        R("walls", "SHELL"), R("floors", "SHELL"), R("attach", ""), R("fascias", ""), Show("DEMO - BUILDING SE", "ShadingWithEdges") },
    ["openings"] = new() { R("instances", "Windows,Doors"), R("rails", "RAIL"), Show("DEMO - BUILDING SE", "ShadingWithEdges") },
    ["details"] = new() { (SK + @"transom-model-07-families\scripts\build_families.cs", A("FAMILIES", "trim_board,ptac_grille,curb_stop,curb_profile")),
                          R("instances", "SHELL"), R("sweeps", ""), Show("DEMO - BUILDING SE", "Realistic") },
    ["site"] = new() { R("walls", "SITE,MODEL"), R("floors", "SITE,MODEL"), R("topo", ""), R("rails", "SITE"), R("shapes", ""), R("instances", "SITE"), Show("SA ISO - SE", "Realistic") },
    ["excavate"] = new() { R("excavate", "") },
    ["landscape"] = new() { R("instances", "Planting,Site"), Show("SA ISO - SE", "Realistic") },
    ["finale"] = new() { (SK + @"transom-model\scripts\demo_views.cs", A()),
                         (SK + @"transom-model-11-checks\scripts\inventory.cs", A("TARGET", TITLE, "OUT", System.IO.Path.Combine(WORK, "demo_inventory.json"))),
                         Show("DEMO - BUILDING NW", "Realistic") },
};
if (!stages.ContainsKey(STAGE)) throw new Exception("unknown STAGE " + STAGE + "; one of " + string.Join(", ", stages.Keys));
var host = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("Transom.Scripting.ScriptHost", false); } catch { return null; } }).First(t => t != null);
var refs = new[] { typeof(Document).Assembly, typeof(UIApplication).Assembly, typeof(Enumerable).Assembly, typeof(object).Assembly, typeof(Transom.Core.RevitCodeExecutor).Assembly };
var imports = new[] { "System", "System.Linq", "System.Collections.Generic", "Autodesk.Revit.DB", "Autodesk.Revit.UI", "Autodesk.Revit.ApplicationServices" };
var outl = new List<string>(); var sw = System.Diagnostics.Stopwatch.StartNew(); bool ok = true;
foreach (var (file, args) in stages[STAGE]) {
    var code = System.IO.File.ReadAllText(file); foreach (var kv in args) code = code.Replace("{{" + kv.Key + "}}", kv.Value);
    var g = new Transom.Core.RevitCodeExecutor.Globals { doc = doc, uiapp = uiapp, app = app };
    var o = (Dictionary<string, object>)host.GetMethod("Run").Invoke(null, new object[] { code, g, refs, imports, System.Threading.CancellationToken.None });
    var v = (string)o["status"] == "ok" ? o.GetValueOrDefault("value")?.ToString() ?? "" : o["status"] + " " + o.GetValueOrDefault("error");
    var lines = v.Split('\n');
    outl.Add(System.IO.Path.GetFileName(file) + (args.ContainsKey("STAGE") ? ":" + args["STAGE"] : "") + $" [{sw.Elapsed.TotalSeconds:0}s]: " + (lines.Length > 4 ? lines[0] + " / ... / " + lines[lines.Length - 1] : string.Join(" / ", lines)));
    if ((string)o["status"] != "ok") { ok = false; break; }
}
System.IO.File.AppendAllText(System.IO.Path.Combine(WORK, "demo_log.txt"), $"{DateTime.Now:HH:mm:ss} {STAGE} {(ok ? "DONE" : "FAILED")} in {sw.Elapsed.TotalSeconds:0}s\n" + string.Join("\n", outl.Select(x => "    " + x)) + "\n");
return string.Join("\n", outl);

// run_file.cs - run a script FILE through the Transom bridge's own script host, so one demo step is one short call.
//
// Send this snippet with mcp__transom__execute_revit_code and readOnly:true (the script starts its own transactions).
// Edit the two settings lines only:
//   FILE - the script to run (a .cs file from one of the transom-model skills, or the project's copy)
//   ARGS - text substitutions applied to the file before it compiles: every {{KEY}} becomes its value
// The script sees the same globals as a snippet (doc, app, uiapp, Print) and its return value comes back here.
var FILE = @"C:\path\to\script.cs";
var ARGS = new Dictionary<string, string> { ["CFG"] = @"C:\path\to\Revit Modeling\model_config.json" };

var code = System.IO.File.ReadAllText(FILE);
foreach (var kv in ARGS) code = code.Replace("{{" + kv.Key + "}}", kv.Value);
var host = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => { try { return a.GetType("Transom.Scripting.ScriptHost", false); } catch { return null; } })
    .FirstOrDefault(t => t != null) ?? throw new Exception("Transom.Scripting.ScriptHost is not loaded");
var run = host.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
var g = new Transom.Core.RevitCodeExecutor.Globals { doc = doc, uiapp = uiapp, app = app };
var refs = new[] { typeof(Document).Assembly, typeof(UIApplication).Assembly, typeof(Enumerable).Assembly, typeof(object).Assembly, g.GetType().Assembly };
var imports = new[] { "System", "System.Linq", "System.Collections.Generic", "Autodesk.Revit.DB", "Autodesk.Revit.UI", "Autodesk.Revit.ApplicationServices" };
var outcome = (Dictionary<string, object>)run.Invoke(null, new object[] { code, g, refs, imports, System.Threading.CancellationToken.None });
var printed = g.GetType().GetProperty("LogText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(g) as string;
if (!string.IsNullOrWhiteSpace(printed)) Print(printed.TrimEnd());
var status = outcome["status"] as string;
if (status != "ok") throw new Exception(System.IO.Path.GetFileName(FILE) + " " + status + ": " + (outcome.TryGetValue("error", out var e) ? e : "") + (outcome.TryGetValue("stack", out var st) ? "\n" + st : ""));
return outcome.TryGetValue("value", out var v) ? v : null;

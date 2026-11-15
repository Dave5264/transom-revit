// acad_run_file.cs - run a script FILE through Transom for AutoCAD's own script host, so an AutoCAD step is one short
// call (the AutoCAD twin of run_file.cs). Send with mcp__transom-acad__execute_autocad_code in DOCUMENT context:
// readOnly:true for a report (the transaction is aborted), readOnly:false to write (one transaction, one undo step).
// The file sees the same globals as a snippet (doc, db, ed, tr, ms, Print). ARGS replaces {{KEY}} in the file.
// Proven 2026-11-12 against Transom for AutoCAD 1.9.19.
var FILE = @"C:\path\to\script.cs";
var ARGS = new Dictionary<string, string> { ["DRY_RUN"] = "true" };

var code = System.IO.File.ReadAllText(FILE);
foreach (var kv in ARGS) code = code.Replace("{{" + kv.Key + "}}", kv.Value);
var exT = typeof(Transom.Acad.AcadCodeExecutor);
var host = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("Transom.Acad.Scripting.ScriptHost", false); } catch { return null; } }).First(t => t != null);
var refs = (System.Reflection.Assembly[])exT.GetMethod("References", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
var imports = (string[])exT.GetField("Imports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
var compiled = (Dictionary<string, object>)host.GetMethod("Compile").Invoke(null, new object[] { code, typeof(Transom.Acad.AcadCodeExecutor.Globals), refs, imports });
if ((string)compiled["status"] != "ok") throw new System.Exception(System.IO.Path.GetFileName(FILE) + " " + compiled["status"] + ": " + compiled.GetValueOrDefault("error"));
var g = new Transom.Acad.AcadCodeExecutor.Globals { doc = doc, db = db, ed = ed, tr = tr };
typeof(Transom.Acad.AcadCodeExecutor.Globals).GetField("OpenModelSpace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
    .SetValue(g, (Func<BlockTableRecord>)(() => (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead)));
var outcome = (Dictionary<string, object>)((Delegate)compiled["runner"]).DynamicInvoke(g, System.Threading.CancellationToken.None);
var printed = typeof(Transom.Acad.AcadCodeExecutor.Globals).GetProperty("LogText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(g) as string;
if (!string.IsNullOrWhiteSpace(printed)) Print(printed.TrimEnd());
if ((string)outcome["status"] != "ok") throw new System.Exception(System.IO.Path.GetFileName(FILE) + " " + outcome["status"] + ": " + outcome.GetValueOrDefault("error"));
return outcome.GetValueOrDefault("value");

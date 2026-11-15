# Conventions shared by every transom-model script

## Units and frames

- Data files (json) hold **model inches**. Revit's API is in internal **feet**. Every script converts at its edges.
- Internal Z 0 = the main floor (stage 1). Levels are named in capitals: `LOWER LEVEL`, `MAIN LEVEL`, ...
- The CAD frame to model frame transform is `cad_to_model_in` in the config: model = CAD + [dx, dy] inches. Elevation
  drawings: South X = plan x, North X = -plan x, East X = plan y, West X = -plan y, Y = 0 at the zero level.

## The config

`model_config.json` (template `model_config.template.json`), passed to every script as `ARGS["CFG"]`. Keys:
`model_title` (the target is resolved by this title prefix, never by the active doc), `model_path`, `template`,
`family_templates`, `modeling_dir`, `work_dir` (logs and id maps of this run), `reference_dir`, `families_dir`,
`family_library`, `textures_dir`, `cad_dir`, `cad_units`, `datum_ft`, `levels[]` (name, elev_ft, plan),
`building_box_in`, `site_box_in`, `cad_to_model_in`, `plan_links[]`, `elevation_links[]`, `wall_types_by_material`,
`datum_level_by_material`, `tag`.

## The failure preprocessor (every write transaction has one)

```csharp
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
// using (var t = new Transaction(D, "name")) {
//   var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
//   t.Start(); ... ; var st = t.Commit();   // report pre.Msgs with the result
// }
```

Warnings are deleted and reported; an error rolls the transaction back and comes back as text instead of a modal dialog
that would stop Revit. A `SketchEditScope`'s inner transaction needs one too. Send every write with `readOnly: true`:
the bridge's own transaction (readOnly:false) has no failure handler.

## Resolving the target model

```csharp
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
```

The bridge's `doc` is the ACTIVE model. With an asset library or a client model open next to the target, `doc` may be
the wrong one. Resolve by title and pass `D` to every transaction, collector, view and `ReferenceIntersector`.

## Running files: the loader

One script (`scripts/run_file.cs`, readOnly:true):

```csharp
var FILE=@"<skill>\scripts\<script>.cs"; var ARGS=new Dictionary<string,string>{["CFG"]=@"<...>\model_config.json"};
var code=System.IO.File.ReadAllText(FILE); foreach(var kv in ARGS) code=code.Replace("{{"+kv.Key+"}}",kv.Value);
var host=AppDomain.CurrentDomain.GetAssemblies().Select(a=>{try{return a.GetType("Transom.Scripting.ScriptHost",false);}catch{return null;}}).First(t=>t!=null);
var g=new Transom.Core.RevitCodeExecutor.Globals{doc=doc,uiapp=uiapp,app=app};
var o=(Dictionary<string,object>)host.GetMethod("Run").Invoke(null,new object[]{code,g,new[]{typeof(Document).Assembly,typeof(UIApplication).Assembly,typeof(Enumerable).Assembly,typeof(object).Assembly,g.GetType().Assembly},new[]{"System","System.Linq","System.Collections.Generic","Autodesk.Revit.DB","Autodesk.Revit.UI","Autodesk.Revit.ApplicationServices"},System.Threading.CancellationToken.None});
return (string)o["status"]=="ok"? o.GetValueOrDefault("value") : o["status"]+" "+o.GetValueOrDefault("error");
```

Several stages in one call: keep `host` and loop over `(FILE, ARGS)` pairs, stopping at the first status that isn't
"ok". The nested run has no cancellation token, so a long script finishes even after the bridge reports "timed out
waiting for Revit". Its own log file (`replay_log.txt`, `export_log.txt`) says how far it got.

`using` directives in a script file must come before its first class declaration.

### AutoCAD: acad_run_file.cs

Same idea through Transom for AutoCAD's script host (`Transom.Acad.Scripting.ScriptHost.Compile` with the executor's
own references and imports, then the compiled runner invoked with a `Transom.Acad.AcadCodeExecutor.Globals` that shares
the snippet's `doc`, `db`, `ed` and `tr`). Send it in document context: readOnly:true runs inside an aborted
transaction (a report), readOnly:false inside the one transaction that commits (one undo step). A script that needs
the application context (`clean_copy.cs`, opening or switching drawings) is sent directly, not through the loader.

## Ids between stages

- `<work_dir>\roof_ids.json`: roof table id -> new roof id (written by `roofs_from_table.cs`).
- `<work_dir>\id_map.json`: recipe id -> new element id, for every element `recipe_build.cs` made. A stage rerun skips
  what is already mapped and still exists.

## Comments tags (instance Comments)

| Tag | On |
|---|---|
| `SHELL <material> plane <n>` / `SHELL <material> J<n> <what>` | shell walls from runs / junction walls |
| `SHELL ROOF <name>`, `ROOF <name> (from elevations)`, `SHELL RIDGE vent/cap <name>` | roofs |
| `SHELL slab <LEVEL>`, `SHELL soffit <name>` | building floors |
| `MASS CHECK` | form-proving masses |
| `SITE <what>` | site floors, toposolid, site walls, shapes |
| `LANDSCAPE <zone>` | planting and site furniture |
| `CAD verify` | test walls on drawn lines |

## Logs

- `<work_dir>\bridge_log.jsonl`: every `execute_revit_code` call of the session (written by the revit-post hook).
- `<work_dir>\replay_log.txt`: recipe_build stages.
- `MODEL PLAN.md`: the human record. Tick and log as each step finishes.

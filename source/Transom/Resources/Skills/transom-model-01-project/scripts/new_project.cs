// new_project.cs - creates the model from the template in the config, saves it to model_path, and opens it as the
// ACTIVE document (the bridge's `doc` is always the active document, so everything after this works on the new model).
// Never overwrites an existing file. Run through run_file.cs with ARGS { CFG }.
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
string S(string k) => CFG.GetProperty(k).GetString();
var path = S("model_path");
var log = new List<string>();
if (System.IO.File.Exists(path)) log.Add("exists, not overwritten: " + path);
else {
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
    var nd = app.NewProjectDocument(S("template"));
    nd.SaveAs(path, new SaveAsOptions { MaximumBackups = 3 });
    nd.Close(false);
    log.Add("created " + path + " from " + System.IO.Path.GetFileName(S("template")));
}
var open = app.Documents.Cast<Document>().FirstOrDefault(x => !x.IsLinked && x.PathName == path);
var ui = uiapp.OpenAndActivateDocument(path);
log.Add("active: " + ui.Document.Title + (open != null ? " (was already open)" : ""));
return string.Join("\n", log);

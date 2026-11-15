// hello.cs - smoke test for run_file.cs: lists the open documents and reads a setting substituted by ARGS.
var cfg = @"{{CFG}}";
Print("config path: " + cfg);
var lines = new List<string>();
foreach (Document d in app.Documents) lines.Add($"{d.Title} | modified={d.IsModified} | family={d.IsFamilyDocument} | linked={d.IsLinked}");
lines.Add("active=" + doc.Title);
return string.Join("\n", lines);

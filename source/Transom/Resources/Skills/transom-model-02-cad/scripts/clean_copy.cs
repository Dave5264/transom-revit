// clean_copy.cs - context "application" (send the code directly, not through acad_run_file.cs). Copies an OPEN source
// drawing from memory, so unsaved edits come along, to <name>-Clean.dwg next to it. Never overwrite an existing -Clean
// file without asking. A closed drawing is simply copied on disk (File.Copy / Copy-Item) and the copy opened.
var SRC = "LowerLevel.dwg";                                   // file name of the open source drawing
var OUT = @"<folder>\LowerLevel-Clean.dwg";                  // must end in -Clean.dwg (the acad-pre hook checks)
if (System.IO.File.Exists(OUT)) return "exists, not overwritten: " + OUT;
foreach (Document d in Application.DocumentManager)
{
    if (System.IO.Path.GetFileName(d.Name) != SRC) continue;
    using (d.LockDocument())
    using (var copy = d.Database.Wblock())
        copy.SaveAs(OUT, DwgVersion.Current);
    return "copied " + SRC + " -> " + OUT;
}
return SRC + " is not open";

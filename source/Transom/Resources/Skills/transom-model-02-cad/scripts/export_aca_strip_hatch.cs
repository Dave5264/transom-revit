// export_aca_strip_hatch.cs - execute_autocad_code, context "application": after
//   run_command ["-AECEXPORTTOAUTOCAD", "", "<folder>\ACAD-<name>-Clean.dwg"]
// opens the export in a side database and strips the hatches, 2D solids and text the export re-creates (wall poche,
// sill patterns). Clean BEFORE export. Run through acad_run_file.cs with ARGS { PATH } = the exported file (document
// context, readOnly:false; the side database is independent of the open drawing). Verified 2026-11-12.
var path = @"{{PATH}}";
int erased = 0;
using (var sdb = new Database(false, true))
{
    sdb.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
    sdb.CloseInput(true);
    using (var t = sdb.TransactionManager.StartTransaction())
    {
        var bt = (BlockTable)t.GetObject(sdb.BlockTableId, OpenMode.ForRead);
        var msId = bt[BlockTableRecord.ModelSpace];   // compare ids: btr.Name is "*Model_Space", the constant is "*MODEL_SPACE"
        foreach (ObjectId bid in bt)
        {
            var btr = (BlockTableRecord)t.GetObject(bid, OpenMode.ForRead);
            if (btr.IsFromExternalReference || (btr.IsLayout && bid != msId)) continue;
            foreach (ObjectId id in btr)
                if (t.GetObject(id, OpenMode.ForRead) is Entity e && (e is Hatch || e is Solid || e is DBText || e is MText))
                { e.UpgradeOpen(); e.Erase(); erased++; }
        }
        t.Commit();
    }
    sdb.SaveAs(path, DwgVersion.Current);
}
return $"{System.IO.Path.GetFileName(path)}: erased {erased} hatches, solids and text";

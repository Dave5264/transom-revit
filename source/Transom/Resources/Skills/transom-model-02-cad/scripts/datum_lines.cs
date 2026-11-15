// datum_lines.cs - ELEVATIONS AND SECTIONS ONLY, after cleanup.cs and before the purge. Removes the level datum lines:
// every horizontal line at least 90% as long as the model-space extents are wide (level heads ride on them, they are not
// built). Grade and footing lines are shorter and stay. Read the grid X positions before the grid layer goes (they
// prove the elevation's frame). Run through acad_run_file.cs, document context, readOnly:false.
int n = 0;
double x0 = 1e9, x1 = -1e9;
foreach (ObjectId id in ms) { try { var x = ((Entity)tr.GetObject(id, OpenMode.ForRead)).GeometricExtents; x0 = Math.Min(x0, x.MinPoint.X); x1 = Math.Max(x1, x.MaxPoint.X); } catch {} }
foreach (ObjectId id in ms)
    if (tr.GetObject(id, OpenMode.ForRead) is Line ln && Math.Abs(ln.StartPoint.Y - ln.EndPoint.Y) < 0.01 && ln.Length >= 0.9 * (x1 - x0))
    { ln.UpgradeOpen(); ln.Erase(); n++; }
return $"erased {n} datum lines (extents width {(x1 - x0):0.#})";

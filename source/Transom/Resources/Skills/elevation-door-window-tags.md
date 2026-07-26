---
name: elevation-door-window-tags
description: Tags the doors and windows on A300-series building elevations, using a colour-coded image pass to recognise only the openings actually visible in each view — occluded ones are never tagged — and skipping any already tagged. Use it when a set of elevations needs its openings tagged.
---

# Tag doors and windows on elevation views

Place door and window tags on the elevation views of the A300-series sheets, tagging **only the openings
actually visible** in each view.

Visibility is the hard part, and it is decided **optically**. A view-scoped `FilteredElementCollector`
returns every opening inside the elevation's clip depth, including interior doors sitting behind the
facade. Revit has no occlusion query, so this skill gives each opening a unique flat colour, exports the
view to PNG, and reads back which colours actually survive to pixels. An opening hidden behind a wall
contributes zero pixels and is never recognised. No geometric proxy reproduces this — see *Why optical*.

## Tools

- `mcp__transom__status` — confirm the bridge before anything else.
- `mcp__transom__execute_revit_code` — the workhorse. **Roslyn C#**, not Python.
- The PNG decode runs **outside Revit**, in PowerShell (Step 6c) — the script host has no `System.Drawing`
  reference and an 8-second cap, so it cannot do the pixel scan itself.

### execute_revit_code contract — read this before writing a snippet

- **C#**, with `doc`, `uiapp`, `app`, and `Print(...)` in scope. `System`, `System.Linq`,
  `System.Collections.Generic`, `Autodesk.Revit.DB` and `.UI` are pre-imported.
- **It opens the transaction for you.** With the default (`readOnly` false) the snippet already runs
  inside a transaction that commits on success and rolls back on any exception. **Do not construct a
  `Transaction`** — a nested one throws.
- Pass `readOnly: true` for anything that does not modify the model, including the image export and the
  canvas-theme calls.
- **8-second hard cap.** One view per call, never batch. If an export times out, drop `PixelSize`.
- Element ids are `long` — use `id.Value` and `new ElementId(longValue)`.
- Tag categories contain more than tags. `OST_WindowTags` holds `FamilyInstance` elements as well as
  `IndependentTag`, so **always `.OfType<IndependentTag>()`, never `.Cast<IndependentTag>()`** — Cast
  throws on the first non-tag it meets.

## Step 1 — Confirm the bridge

Call `mcp__transom__status` and confirm `ok: true`. Name the connected document. If it fails, stop and
point the user at Transom's Settings tab (Claude Assist on, Revit open, Claude Code restarted after
first-time setup).

## Step 2 — Discover which tags the project uses

Do **not** assume a tag family. Follow what the project already does.

```csharp
var used = new Dictionary<string,int>();
foreach (var bic in new[]{ BuiltInCategory.OST_DoorTags, BuiltInCategory.OST_WindowTags })
foreach (var t in new FilteredElementCollector(doc).OfCategory(bic)
                      .WhereElementIsNotElementType().OfType<IndependentTag>())
{
    var v = doc.GetElement(t.OwnerViewId) as View;
    if (v == null || v.ViewType != ViewType.Elevation) continue;
    var ty = doc.GetElement(t.GetTypeId()) as ElementType;
    var k = $"{bic} | id={ty?.Id.Value} | {ty?.FamilyName} : {ty?.Name}";
    used.TryGetValue(k, out var c); used[k] = c + 1;
}
foreach (var kv in used.OrderByDescending(k => k.Value)) Print($"{kv.Value,4}x  {kv.Key}");
if (used.Count == 0) Print("NONE — no door/window tags placed in any elevation view");

Print("--- all available tag types ---");
foreach (var bic in new[]{ BuiltInCategory.OST_DoorTags, BuiltInCategory.OST_WindowTags })
foreach (var t in new FilteredElementCollector(doc).OfCategory(bic)
                      .WhereElementIsElementType().OfType<ElementType>())
    Print($"{bic} | id={t.Id.Value} | {t.FamilyName} : {t.Name}");
```

- If elevation views already use a tag type, **adopt the most-used one per category** and say which you
  picked and from how many placements.
- If **no** tags exist in any elevation view, or a category has none, **stop and ask the user which tag
  type to use for that category**, listing the available types. Do not guess or fall back to a default.

## Step 3 — Find the qualifying elevation views

```csharp
foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                      .OrderBy(s => s.SheetNumber))
{
    if (!s.SheetNumber.StartsWith("A3")) continue;
    foreach (var vpId in s.GetAllViewports())
    {
        var vp = doc.GetElement(vpId) as Viewport; if (vp == null) continue;
        var v = doc.GetElement(vp.ViewId) as View;
        if (v == null || v.IsTemplate) continue;
        if (v.ViewType != ViewType.Elevation) continue;
        if (v.Scale > 96) continue;                       // 96 = 1/8"; bigger number = smaller scale
        Print($"{s.SheetNumber} | {v.Name} | id={v.Id.Value} | 1:{v.Scale}");
    }
}
```

The scale filter excludes whole-building elevations drawn at 1:192 (1/16"), which are typically not
opening-tagged. Say so when showing the list and let the user opt them in. **Get confirmation before
modifying anything.**

## Step 4 — Force a light canvas, and remember to put it back

**This is load-bearing.** If Revit's canvas theme is Dark, the image export compresses every colour
(measured: `0 → 64`, `255 → 191`) and **not one palette colour survives** — the decode silently returns
nothing. Setting the canvas theme to Light makes the export exact. It does not touch the UI theme, so the
user's interface does not change appearance.

```csharp
Print($"SAVED canvasTheme={Autodesk.Revit.UI.UIThemeManager.CurrentCanvasTheme} " +
      $"followSystem={Autodesk.Revit.UI.UIThemeManager.FollowSystemColorTheme}");
Autodesk.Revit.UI.UIThemeManager.FollowSystemColorTheme = false;
Autodesk.Revit.UI.UIThemeManager.CurrentCanvasTheme = Autodesk.Revit.UI.UITheme.Light;
```

Record the two saved values. **Restore them in Step 7 no matter how the run ends** — including on failure.

## Step 5 — Verify the export is readable before trusting any decode

After the first view's export (Step 6b), confirm the background is pure white `255,255,255` and that at
least one exact palette colour is present. If the background is not white, **stop** — the canvas theme did
not take effect and every later result would be garbage. Report it rather than tagging on bad data.

## Step 6 — Process one view at a time

Run 6a → 6e for a single view, confirm, then move on. Never batch.

### 6a — Colour every opening

Each opening gets one colour from a palette with channels drawn from {0, 51, 102, 153, 204, 255}, greys
excluded (background is white, linework black). Encode a **palette index, not the element id** — Revit
ids routinely exceed 16,777,215 and do not fit in 24 bits of RGB.

```csharp
const long VIEW_ID = 0;

var vid  = new ElementId(VIEW_ID);
var view = (View)doc.GetElement(vid);
var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
    .Cast<FillPatternElement>().First(f => f.GetFillPattern().IsSolidFill).Id;

var elems = new[]{ BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows }
    .SelectMany(bic => new FilteredElementCollector(doc, vid).OfCategory(bic)
        .WhereElementIsNotElementType().ToElements())
    .OrderBy(e => e.Id.Value).ToList();

var lv = new[]{ 0, 51, 102, 153, 204, 255 };
var palette = (from r in lv from g in lv from b in lv where !(r==g && g==b) select new[]{r,g,b}).ToList();
if (elems.Count > palette.Count) { Print($"STOP: {elems.Count} openings > {palette.Count} codes"); return; }

for (int i = 0; i < elems.Count; i++)
{
    var c = palette[i];
    var ogs = new OverrideGraphicSettings();
    ogs.SetSurfaceForegroundPatternId(solid);
    ogs.SetSurfaceForegroundPatternColor(new Color((byte)c[0], (byte)c[1], (byte)c[2]));
    ogs.SetSurfaceForegroundPatternVisible(true);
    ogs.SetSurfaceTransparency(0);           // glazing is transparent; without this the fill blends
    view.SetElementOverrides(elems[i].Id, ogs);
    Print($"{c[0]},{c[1]},{c[2]}={elems[i].Id.Value}:{(elems[i].Category.Id.Value==(long)BuiltInCategory.OST_Doors?"D":"W")}");
}
Print($"coloured {elems.Count}");
```

**Keep the printed colour → element map.** Step 6d needs it and it exists only in your context.

Do **not** use `mcp__transom__color_splash` — it colours by category across the active view *and every
other view of the same type*, which would corrupt every other elevation in the set.

### 6b — Export to PNG

`readOnly: true`. **PNG only** — JPEG's lossy compression shifts the exact channel values.

```csharp
const long VIEW_ID = 0;
var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "transom-elev-tags");
System.IO.Directory.CreateDirectory(dir);
foreach (var old in System.IO.Directory.GetFiles(dir, "*.png")) System.IO.File.Delete(old);
var opts = new ImageExportOptions {
    ExportRange = ExportRange.SetOfViews,
    FilePath = System.IO.Path.Combine(dir, "elev"),
    HLRandWFViewsFileType = ImageFileType.PNG,
    ShadowViewsFileType   = ImageFileType.PNG,
    ImageResolution = ImageResolution.DPI_150,
    ZoomType = ZoomFitType.FitToPage,
    PixelSize = 1400,
};
opts.SetViewsAndSheets(new List<ElementId>{ new ElementId(VIEW_ID) });
doc.ExportImage(opts);
foreach (var f in System.IO.Directory.GetFiles(dir, "*.png")) Print(f);
```

Revit appends the view name, so read the printed path. `PixelSize = 2200` exceeded the 8-second cap in
testing; 1400 completes comfortably and still resolves a typical opening.

### 6c — Decode (PowerShell, outside Revit)

Write to a `.ps1` and run it. Uses `LockBits`, not `GetPixel` — per-pixel calls would take minutes.

```powershell
param([string]$Path, [int]$Stride = 2, [int]$MinPixels = 8)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;using System.Collections.Generic;using System.Drawing;
using System.Drawing.Imaging;using System.Runtime.InteropServices;
public static class TransomPng {
  public static string[] Histogram(string path,int stride){
    using(var bmp=new Bitmap(path)){
      var d=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
      try{
        int len=Math.Abs(d.Stride)*bmp.Height; var buf=new byte[len];
        Marshal.Copy(d.Scan0,buf,0,len);
        var counts=new Dictionary<int,int>();
        for(int y=0;y<bmp.Height;y+=stride){ int row=y*d.Stride;
          for(int x=0;x<bmp.Width;x+=stride){ int i=row+x*4;
            int k=(buf[i+2]<<16)|(buf[i+1]<<8)|buf[i]; int c; counts.TryGetValue(k,out c); counts[k]=c+1; } }
        var o=new List<string>();
        foreach(var kv in counts) o.Add(((kv.Key>>16)&0xFF)+","+((kv.Key>>8)&0xFF)+","+(kv.Key&0xFF)+"="+kv.Value);
        return o.ToArray();
      } finally { bmp.UnlockBits(d); } } }
}
'@
$lv = 0,51,102,153,204,255
$h = @{}; [TransomPng]::Histogram($Path,$Stride) | ForEach-Object { $p=$_ -split '='; $h[$p[0]]=[int]$p[1] }
if (-not $h.ContainsKey('255,255,255')) { Write-Output 'FAIL: background is not white - canvas theme did not apply'; return }
foreach ($k in $h.Keys) {
    $c = $k -split ','; $r=[int]$c[0]; $g=[int]$c[1]; $b=[int]$c[2]
    if (($lv -contains $r) -and ($lv -contains $g) -and ($lv -contains $b) `
        -and -not ($r -eq $g -and $g -eq $b) -and $h[$k] -ge $MinPixels) {
        [pscustomobject]@{ Color = $k; Pixels = $h[$k] }
    }
} | Sort-Object Pixels -Descending | Format-Table -AutoSize
```

Match the reported colours against 6a's map. Colours that do not appear are **occluded** — that is the
whole point; do not tag them. Delete the PNG once decoded.

### 6d — Tag what is visible and sufficiently in frame

Optical presence proves an opening is not hidden. It does **not** prove enough of it is in frame: a window
clipped by the crop edge still shows pixels. Add a coverage test — measured against hand-tagged work, the
threshold that reproduces drafting practice is **50% of projected area inside the crop**.

```csharp
const long VIEW_ID = 0, WINDOW_TAG = 0, DOOR_TAG = 0;
const double MIN_COVERAGE = 0.5;
var visibleWindows = new long[]{ /* from 6c */ };
var visibleDoors   = new long[]{ /* from 6c */ };

var vid = new ElementId(VIEW_ID);
var view = (View)doc.GetElement(vid);
var cb = view.CropBox; var inv = cb.Transform.Inverse; bool cropOn = view.CropBoxActive;

double Coverage(Element e){
    if (!cropOn) return 1.0;
    var bb = e.get_BoundingBox(null); if (bb == null) return 0.0;
    double minU=1e9,maxU=-1e9,minV=1e9,maxV=-1e9;
    for (int i=0;i<8;i++){
        var p = new XYZ((i&1)==0?bb.Min.X:bb.Max.X,(i&2)==0?bb.Min.Y:bb.Max.Y,(i&4)==0?bb.Min.Z:bb.Max.Z);
        var q = inv.OfPoint(p);
        minU=Math.Min(minU,q.X);maxU=Math.Max(maxU,q.X);minV=Math.Min(minV,q.Y);maxV=Math.Max(maxV,q.Y); }
    double w=maxU-minU, h=maxV-minV; if (w<=1e-9||h<=1e-9) return 0.0;
    double iw=Math.Max(0,Math.Min(maxU,cb.Max.X)-Math.Max(minU,cb.Min.X));
    double ih=Math.Max(0,Math.Min(maxV,cb.Max.Y)-Math.Max(minV,cb.Min.Y));
    return (iw*ih)/(w*h);
}

var already = new HashSet<long>();
foreach (var bic in new[]{ BuiltInCategory.OST_DoorTags, BuiltInCategory.OST_WindowTags })
foreach (var t in new FilteredElementCollector(doc, vid).OfCategory(bic)
                      .WhereElementIsNotElementType().OfType<IndependentTag>())
    foreach (var i in t.GetTaggedLocalElementIds()) already.Add(i.Value);

int placed=0, skipClip=0, skipDup=0;
foreach (var (ids, tagType) in new[]{ (visibleWindows, WINDOW_TAG), (visibleDoors, DOOR_TAG) })
foreach (var raw in ids)
{
    if (already.Contains(raw)) { skipDup++; continue; }
    var e = doc.GetElement(new ElementId(raw));
    if (e == null) continue;
    double cov = Coverage(e);
    if (cov < MIN_COVERAGE) { Print($"skip {raw} coverage={cov:P0}"); skipClip++; continue; }
    var bb = e.get_BoundingBox(null);            // WORLD bbox, not the view overload
    var lp = e.Location as LocationPoint;
    if (bb == null || lp == null) continue;
    var pt = new XYZ(lp.Point.X, lp.Point.Y, (bb.Min.Z + bb.Max.Z)/2.0);
    IndependentTag.Create(doc, new ElementId(tagType), vid, new Reference(e),
                          false, TagOrientation.Horizontal, pt);
    placed++;
}
Print($"PLACED={placed} skipClipped={skipClip} skipAlreadyTagged={skipDup}");
```

The tag anchors at the **world-space** bounding-box centre. In an elevation the on-screen vertical is
world Z, and an opening's `LocationPoint.Z` sits at its base, so a tag anchored there hangs low.
Centre-of-opening is what matches hand drafting.

This is also why the skill does not use `mcp__transom__tag_elements`: it anchors at `LocationPoint`, and
its `offset` shifts world X/Y, which cannot correct vertical placement in an elevation.

### 6e — Clear the overrides

Immediately, in its own call, so a later failure never leaves a view coloured.

```csharp
const long VIEW_ID = 0;
var vid = new ElementId(VIEW_ID);
var view = (View)doc.GetElement(vid);
var empty = new OverrideGraphicSettings();
int n=0;
foreach (var bic in new[]{ BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
foreach (var e in new FilteredElementCollector(doc, vid).OfCategory(bic).WhereElementIsNotElementType().ToElements())
{ view.SetElementOverrides(e.Id, empty); n++; }
Print($"cleared {n}");
```

If anything failed mid-view, **still run 6e** before stopping.

## Step 7 — Restore the canvas theme

Always, including on the failure path:

```csharp
Autodesk.Revit.UI.UIThemeManager.CurrentCanvasTheme = Autodesk.Revit.UI.UITheme.Dark;  // saved value
Autodesk.Revit.UI.UIThemeManager.FollowSystemColorTheme = true;                        // saved value
Print($"restored canvasTheme={Autodesk.Revit.UI.UIThemeManager.CurrentCanvasTheme}");
```

## Step 8 — Report

| Sheet | View | Doors tagged | Windows tagged | Total |
|---|---|---|---|---|

Then state: views where nothing was visible; the tag types used and whether inferred or user-chosen;
openings skipped as clipped, with their coverage percentages; confirmation that overrides were cleared and
the canvas theme restored; and any view whose export or decode failed, with the error.

If the model is workshared, remind the user to sync with central.

## Why optical

Verified 2026-07-26 on `FRONT ENTRY ELEVATION` (A303), 22 windows and 26 doors in view:

- The optical pass recognised **3 doors** — exactly the three hosted in exterior walls. The other 7 doors
  that face the viewer and sit inside the crop are interior doors behind the facade; they produced **zero
  pixels** and were correctly never recognised.
- Window tags reproduced all 20 hand-placed tags exactly, zero missing and zero extra, once the coverage
  test excluded the two windows clipped to 29% — which a human had also left untagged.
- A purely geometric rule (facing + crop + exterior-wall host) was tried and **rejected**: it cannot see
  occlusion, and across 14 hand-tagged elevations a coverage threshold tuned to fix one false negative
  introduced false positives on three other views.

## Rules and limits

- One view per call — the 8-second cap makes batching fail.
- Never open a `Transaction` in a snippet; the tool owns it.
- Never `.Cast<IndependentTag>()` over a tag category; use `.OfType<IndependentTag>()`.
- PNG only, never JPEG. Canvas theme Light for every export, restored afterwards.
- Never `color_splash` for this workflow — it is not view-scoped.
- Always clear overrides and restore the theme, including on the failure path.
- Confirm the view list with the user before the first write.
- **Scoped to exterior building elevations** (A300 series). Do not run on interior elevations.
- If tags come out visually blank, the tag family is reading a parameter the openings do not carry (e.g. a
  Type Mark tag against doors with no Type Mark). That is model data, not placement — report it.

---
name: transom-model-02-cad
description: Stage 2 of transom-model. Prepares plan, elevation and section DWGs from any source and links them into Revit - clean copies cut down to linework that will physically exist (annotation goes, the site stays), AutoCAD Architecture objects exported to plain AutoCAD, every file linked into its own view with colours preserved on ONE evidence-based transform, elevations moved only along their view plane, and the elevation views checked. Use when CAD needs cleaning or linking, when links don't line up, or before modeling from CAD.
hooks:
  PreToolUse:
    - matcher: "mcp__transom__execute_revit_code"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" revit-pre'
    - matcher: "mcp__transom-acad__.*"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" acad-pre'
    - matcher: "Bash|PowerShell"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" shell-pre'
  PostToolUse:
    - matcher: "mcp__transom__execute_revit_code"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" revit-post'
    - matcher: "Bash|PowerShell"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" shell-post'
---

# Stage 2: clean the CAD and link it

Part of `transom-model`. AutoCAD scripts run through `transom/scripts/acad_run_file.cs` (the AutoCAD twin of
`run_file.cs`: `mcp__transom-acad__execute_autocad_code`, document context, `FILE` + `ARGS`), commands through
`run_command`, Revit scripts through `run_file.cs`. The acad-pre hook refuses any save to a path that isn't a
`-Clean.dwg` and reminds you that a QSAVE saves the active drawing.

Launch AutoCAD Architecture with
`acad.exe /ld "C:\Program Files\Autodesk\AutoCAD 2027\AecBase.dbx" /p "AutoCAD Architecture (Global)" /product "ACA" /language "en-US"`
(the Start Menu shortcut's command), then `powershell -File transom/scripts/acad_always_load.ps1`: it clicks
"Always Load" if the unsigned plug-in prompt shows and waits for the main window. The bridge port (48830) opens a few
seconds after the window.

## What the first project used

- Source: `New CAD Files\the plans sheet (A2.1)` (plans), `the elevations sheet (A3.0)` (four elevations in a row), `the sections sheet (A4.0)` (four
  building sections). The user broke the plans out into `LowerLevel/MainLevel/UpperLevel.dwg` (ACA objects) in one
  frame, inches: grid 1 = west wall face = x 0.
- The elevations were broken out to `South/West/North/EastElevation.dwg` in that frame by a side-database Wblock:
  South X = plan x, North X = -plan x, East X = plan y, West X = -plan y, Y = 0 at MAIN.
- Results: `ACAD-<Level>Level-Clean.dwg` (cleaned, then exported to plain AutoCAD), `<Face>Elevation-Clean.dwg`
  (plain AutoCAD, datums, tags and grids removed).
- The transform came from four steel HSS columns: `MainLevel.dwg + (2437.0", 2221.42")` = the original sheet's MAIN
  copy. So every plan link moves **(+3.0833, -14.8817) ft**: `cad_to_model_in` = [37.0, -178.58]. A second object has
  to agree before it's trusted.

## Fast path (config driven)

1. Clean and export each drawing: Steps 2-6 below. The proven sequence (re-run on 2026-11-12 on a fresh copy of
   `MainLevel.dwg`):
   - copy to `<name>-Clean.dwg` (`clean_copy.cs` if the source is open, a plain file copy if not) and `open_drawing` it;
   - `get_entities` with limit 1 for the inventory (`countsByType`);
   - `cleanup.cs` through `acad_run_file.cs` with `ARGS { DRY_RUN = "true" }` and readOnly:true; read the report against
     the rule; then `DRY_RUN = "false"`, readOnly:false;
   - elevations and sections only: `datum_lines.cs` (readOnly:false);
   - `run_command` `-PURGE _A * _N`, `-PURGE _R * _N`, `_.AUDIT _Y`, `_.REGEN`, `_.ZOOM _E`, `_.QSAVE`, then
     `get_view_image` and look;
   - `run_command ["-AECEXPORTTOAUTOCAD", "", "<folder>\\ACAD-<name>-Clean.dwg"]`;
   - `export_aca_strip_hatch.cs` through `acad_run_file.cs` with `ARGS { PATH }`;
   - verify the export read-only: no `AEC*`, no proxies, no text, no dimensions, no hatches.

   The re-run matched the first project's files exactly. The dry run reported 423 to remove and 832 to keep, plus 9
   inside blocks (tags, dimensions, leaders, notes, points, stair-arrow linework, hatches, setback and property lines).
   The cleaned drawing held the same 832 entities by type as `New CAD Files\MainLevel-Clean.dwg`. The export, after 239
   hatches, solids and text were stripped, held the same 4,017 entities by type, model space and blocks, as
   `ACAD-MainLevel-Clean.dwg`.
2. Put the files, views and transform in the config (`plan_links`, `elevation_links`, `cad_to_model_in`, `cad_units`).
3. `scripts/link_cad.cs`: every link into its own view, plans moved by the transform, elevations moved only along
   their view plane to the zero level, Foreground, older CAD in the view hidden (never deleted). Safe to rerun.
4. `scripts/check_elevation_views.cs`: every elevation must PASS.
5. Export the linked views as images (`doc.ExportImage`, PNG about 1600 px) and look at them. `get_revit_view` doesn't
   render CAD.

Verified 2026-11-11 in a new project: all seven links landed exactly on the first project's origins (plans at
(3.0833, -14.8817, 0) ft, elevations along their planes), and all four elevation views passed.

## Cleaning and linking (proven on the first project)

Turn a set of CAD drawings (plans, elevations, sections, from any office or any version) into underlays to
model from. One rule decides everything:

> **Delete linework that will not physically exist when the project is built. Keep everything that will.**

Annotation goes: text, dimensions, leaders, tags, keynotes, section/elevation/detail marks, grid lines and
bubbles, stair walk-line arrows, revision clouds, title blocks, hatch and fill patterns, property, setback
and easement lines, and demolition. Built things stay, **including the site**: topography, parking layout,
curbs, walks, fences, site walls, trash enclosures, neighbouring buildings, egress wells, decks and patios,
fixtures, casework, roof edges below and floor edges above. If a call is unclear, apply the rule. Don't
fall back on a layer name or on distance from the building.

### Plan and progress file — write it first

A run can outlast the session. Before Step 1, write `PLAN.md` in the project's folder. It holds the goal,
every step below as a checkbox, a **Fixed facts** section (drawing paths, the `-Clean` files, view ids,
link ids, the transform), and a **Progress log**. Tick each step and log what it produced (ids, counts,
anything deviating) **as soon as that step finishes**, never batched at the end. Save a memory note that
points at the file. A session that resumes reads the file first and continues from the first unchecked
step. It never re-derives a fixed fact.

### Tools

- `transom-acad` (AutoCAD 2027 with the Transom plug-in): `status`, `execute_autocad_code`, `run_command`,
  `get_view_image`, `get_entities`, `open_drawing`, `close_drawing`, `cancel_command`.
- `transom` (Revit): `status`, `execute_revit_code`.
- `transom-ui-assist`: `revit_list_dialogs`, `revit_click_dialog`, when a Revit call times out.

`execute_autocad_code` facts: C# with `doc`, `db`, `ed`, `tr`, `ms`, `Print`. A document-context run is
one undo step and commits on success. `readOnly: true` always aborts. `context: "application"` has no
lock and no transaction, so use it for side databases and for switching documents. **20 s cap per call.**
`execute_revit_code`: with `readOnly: false` the bridge owns a transaction. With `readOnly: true` you
may start your own `Transaction`, which Step 7 depends on.

### Step 1 — Connect and identify the drawings

1. Call `mcp__transom-acad__status`. It lists the open drawings and units. If it fails with
   `missing or invalid token`, see *Troubleshooting*.
2. Call `mcp__transom__status` and name the Revit document.
3. Pair each drawing with its Revit view by name (`LowerLevel.dwg` → `LOWER LEVEL - PLAN`). List the views
   with `ViewPlan` / `ViewSection` collectors. If a pairing is ambiguous, ask.

### Step 2 — Never touch the source: make `<name>-Clean.dwg` copies

Work only on copies saved next to the source. When the drawing is **open**, copy it from memory so unsaved
edits come along:

Script: `scripts/clean_copy.cs`.

When it is not open, `File.Copy` it. Refuse to overwrite an existing `-Clean` file without asking. Then
`open_drawing` the copy.

### Step 3 — Look before deleting

Take an inventory of model space by type and layer (`get_entities` returns `countsByType`), list the
block references with their contents, and `get_view_image` after `ZOOM E`. Two things to know:

- **Layer names lie.** In real files, walls sit on `A-Detl-Iden` and site lines sit on `A-Flor-Deck`. The
  script never erases a building object (AEC wall/door/window/etc.) because of its layer.
- **Copy-pasted blocks hide content.** `A$C…` blocks can hold a whole stair, a unit layout, a site plan,
  or every section mark on the sheet. The script scores block contents recursively.

### Step 4 — Run the cleanup script

Run it with `DRY_RUN = true` and `readOnly: true` first, read the report, then run it with
`DRY_RUN = false` in document context. What it does:

- **By type, anywhere in model space or inside kept blocks:** text and attributes, dimensions, leaders,
  tables, tolerances, wipeouts, images, underlays, OLE, points, rays and xlines, hatches, 2D solids and
  MPOLYGON fills.
- **AutoCAD Architecture:** anchored MV-block tags (door, window and room tags), AEC dimensions, schedules,
  spaces, zones, section/elevation lines and column grids. Washer/dryer and other fixture MV blocks are
  unanchored and stay.
- **By layer, primitives only:** layers whose name holds an annotation token (`ANNO`, `IDEN`, `NOTE`,
  `TEXT`, `DIMS`, `SYMB`, `TAG`, `KEYN`, `REVS`, `TTLB`, `NPLT`, `PATT`, `DEFPOINTS` …), legal and zoning
  lines (`C-PROP-*`, `PROPERTY LINE`, `SETBACK`, `EASEMENT`), grids (`S-GRID`, `A-GRID`) and demolition
  (`DEMO`). Matching is by token, so `A-WALL-TEXTURE` does not match `TEXT`. A ceiling grid
  (`A-CLNG-GRID`) is built and stays.
- **Blocks:** removes a block reference whose content is all annotation (section/elevation mark groups,
  title blocks). It also removes one named like an annotation symbol when annotation outweighs geometry
  and the block holds no building object. Kept blocks are cleaned inside, including nested blocks.

Script: `scripts/cleanup.cs`.

Read the dry-run report against the rule before the real run. A `DEMO` layer that holds something that
looks new (a fixture rather than a wall) may be mislayered, so ask.

### Step 5 — Purge, regenerate, and look for strays

```text
run_command ["-PURGE","_A","*","_N"]
run_command ["-PURGE","_R","*","_N"]
run_command ["_.AUDIT","_Y"]
run_command ["_.REGEN"]          // blocks show cached graphics: erased in-block text stays visible until REGEN
run_command ["_.ZOOM","_E"]
run_command ["_.QSAVE"]
```

Then `get_view_image` and **look**. The script cannot catch a stray line that sits on a physical layer.
The real example: a 36 ft fence-layer line that ran straight through three units. Look for lines crossing
rooms or walls, lone fragments, and leftover arrows or break lines. Find them by window with a read-only
extents query, delete by handle, and say why. Ignore AutoCAD Architecture's wall defect-warning triangles:
they are display markers, and the export drops them.

**Elevations and sections: level datums.** The horizontal datum lines that carry level heads run the full
width of the sheet and are not built. Remove every horizontal line at least 90% as long as the model-space
extents are wide. Grade lines and footing lines are shorter and stay. Run this after the script and before
purging:

Script: `scripts/datum_lines.cs`.

Grid lines in elevations sit on `A-GRID` and go with the layer rule. Before deleting them, read their X
positions: they prove the elevation's horizontal frame matches the plans (Step 7).

**Do not add a distance rule.** A "far from the building" pass deleted the topography, parking, trash
enclosure and neighbouring building on a site-bearing plan. All of those get built or already exist.

### Step 6 — Export AutoCAD Architecture objects to plain AutoCAD

Revit sees AEC objects only through proxy graphics, and drawings are often saved with `PROXYGRAPHICS=0`.
Export every drawing that holds `AecDb*` objects:

```text
run_command ["-AECEXPORTTOAUTOCAD", "", "<folder>\\ACAD-<name>-Clean.dwg"]
```

Clean **before** exporting. The export turns walls into anonymous blocks that inherit layers like
`A-Detl-Iden`, so the layer rules would read them as annotation. Tags are only identifiable as anchored
AEC objects before export.

The export writes AEC display components out as real hatches (wall poché, sill patterns). Strip them from
the exported file with a side database:

Script: `scripts/export_aca_strip_hatch.cs`.

Verify each exported file the same way (read-only side database). Require zero `Aec*` objects, zero
`ACAD_PROXY_ENTITY`, zero text and zero dimensions, then open one and look at it.

### Step 7 — Link (never import) into the matching view, on one shared transform

Link each file into **its own view only**, with colours preserved. Turn `AutoCorrectAlmostVHLines` off:
"correct slightly off-axis lines" moves geometry and breaks alignment. Run with `readOnly: true` and your
own transaction plus a failure preprocessor. A Revit error then comes back as text and rolls back, instead
of opening an "Error - cannot be ignored" modal that blocks the bridge.

Links placed origin-to-origin arrive **pinned**, so unpin, move, then re-pin:

Script: `scripts/link_one.cs (or link_cad.cs for the whole config)`.

Set `Unit` from the drawing's `INSUNITS` (`status` reports it). Never fall back to auto-detect.

**The transform.** Files broken out of one set usually share a coordinate frame. Give every file in that
frame the **same** move, so the plans stack and modeled elements line up level to level. Derive it from
evidence, never by eye:

- Match a unique physical object (a steel column, a stair corner) between the drawing and something
  already placed in the model: grids, modeled walls, or an earlier CAD placement. Confirm the result with a
  second object. Example: four HSS columns gave one exact offset between `MainLevel.dwg` and the original
  sheet file.
- Report the residual. Small offsets between levels inside the drawings themselves are drafting, not
  placement. Name them and don't "fix" them.
- **Elevations and sections:** link into the matching elevation or section view. Drawing X runs along the
  view's `RightDirection` and drawing Y is height. Prove the frame before linking. Grid X positions in each
  elevation should equal the plan grids (South X = plan x, North X = −plan x, East X = plan y, West X =
  −plan y for a set broken out together). The datum at Y = 0 should be a known level. Then move the link
  only along the view plane, never across it:

  ```csharp
  var target = new XYZ(planDx, planDy, levelZ);   // where DWG (0,0) belongs: the plan transform plus the level's internal Z
  var d = target - ii.GetTotalTransform().Origin;
  var move = view.RightDirection.Multiply(d.DotProduct(view.RightDirection)) + view.UpDirection.Multiply(d.DotProduct(view.UpDirection));
  ```

  `GetTotalTransform()` on an elevation link reports its basis in plan terms (BasisY = +Y, not up). Don't
  read orientation from it. Verify placement from the link's geometry bounds: X/12 + offset along the
  view, Y/12 = internal Z. Example: a footing line at Y = −136" landed at Z −11.33.
- **Draw Layer.** In elevations and sections, set every link to **Foreground**
  (`IMPORT_BACKGROUND` = 0, inverted from the name). Otherwise modeled walls hide the CAD linework behind
  them.
- **Old CAD in the target view.** Hide earlier imports or links in that view with `view.HideElements`. Don't
  delete them unasked.
- Reading link geometry back: skip unbound curves (full circles) with `c.IsBound`, or `GetEndPoint` throws.

For the whole workflow, including checking the elevation views, modeling a verification level, and
compensating for drafting errors, use the `transom-model` skill.

### Step 8 — Verify and report

`get_revit_view` does not render CAD. Use `doc.ExportImage` on the linked views (PNG, `PixelSize` about
1600, `readOnly: true`) and read the images. Check each link's `IsLinked`, `ViewSpecific`, owner view and
origin. Report, for each drawing, what was removed (by reason, from the script report plus any handles
deleted by hand), what was deliberately kept (site, egress wells, overhead edges), the output file names,
and the transform. If the model is workshared, **offer** Synchronize with Central. Don't sync unasked.

### Troubleshooting

- **`transom-acad` says `missing or invalid token`** in the Claude desktop app. The app is an MSIX
  package, so a stale copy of `%LOCALAPPDATA%\Transom\acad\bridge.token` can sit in
  `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Local\Transom\acad\` and shadow the real token AutoCAD
  writes. Rename that overlay copy to `bridge.token.stale`. The shim reads the token on every call, so the
  next call works.
- **Revit call returns `timed out waiting for Revit (request not applied)`.** A modal is up. Call
  `revit_list_dialogs`. "Error - cannot be ignored" with Cancel/Show/Expand is usually a failed link step,
  so Cancel rolls it back. Then rerun with the Step 7 pattern.
- **A command is left waiting for input** (`stillActive` in the `run_command` reply): call
  `cancel_command`, then send the full token list in one call.

## Proving the frame and the transform (from the setup runbook)


1. `mcp__transom-acad__status` and `mcp__transom__status`. If transom-acad says `missing or invalid token`, see Troubleshooting
   in `transom-model-02-cad`.
2. List the drawings and the Revit views. Pair plans with floor plans by level, and elevations and sections with their views.
   Ask when a pairing is ambiguous. **List the sections explicitly. Plot them and read them.** If the set has none, say so now.
3. **Prove the frame before trusting it.** Read the grid lines in every drawing (still present at this point) and the level
   datums in the elevations and sections:
   - Plans broken out of one set should share grid coordinates.
   - Elevations: South X = plan x, North X = -plan x, East X = plan y, West X = -plan y, and the Y = 0 datum is a named level.
   - Check units against a known dimension (a grid bay) before anything else.
   If a drawing is not in the shared frame, find its own transform the same way (Phase 3).


Find where drawing (0,0) belongs in the model from objects that physically exist. Steel columns, a stair
core and grids are good choices. Use two or more and require them to agree. In the first run, four HSS
columns gave one exact offset: `MainLevel.dwg + (2437.0", 2221.42")` lands on the original sheet's MAIN
copy, which the model had placed at (−200, −200) ft. So every plan link moves (+3.0833, −14.8817) ft
from origin. If the model already holds walls or grids, match against those instead. Write the transform
down. Every link in the frame uses it.

## Comparing drawings: what to do with a difference


| Pattern | Meaning | Action |
|---|---|---|
| ≥ 70% of matched features agree within 1/8" on an offset ≥ 1/8" | The whole drawing is displaced | Move that link by -offset, record it, measure again |
| Mixed signs, low agreement | Drafting scatter | Leave the link. Reconcile in Phase 8 |
| One drawing disagrees with two that agree | That drawing is the outlier | Report it. Never move the agreeing majority |
| Datums disagree with Revit levels | Elevation drafting or a wrong level | Levels win if they come from the title blocks or survey. Report it |
| A dimension doesn't match a known bay | Units or scale | Stop. Fix the units first |

## Rules

- Never modify, save over or close-with-save the source drawings. Work on `-Clean` copies.
- Link, never import. One view per file, colours preserved, the same transform for every file in a frame.
- No distance-based deletion. Site features that exist or will be built stay.
- Clean in AutoCAD Architecture first, then export, then strip the export's hatches.
- Look at the result (REGEN first) before calling a drawing done.

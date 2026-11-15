---
name: transom-model-11-checks
description: Stage 11 of transom-model, run after every stage. Checks a Revit model against its CAD drawings and for envelope faults - rule check of the walls, elevation comparison against the drafted elevations, envelope coverage, slots, see-through, and looking at 3D views and section cuts - plus the details-and-site look list, an inventory of the model compared with the reference result, and the final report. Use after each modeling phase, before telling the user a stage is done, or to compare a demo run with the finished model.
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

# Stage 11: checks (after every stage)

Run all of them after every modeling phase, and before calling anything finished. Reads only, except temporary views inside a
TransactionGroup that is rolled back.

**The numbers narrow down where to look. They do not replace looking.** Each check below catches things the others can't. On the
first project a model that had passed its checks had three holes in the envelope, seams at every floor line and a sill on every
window. A person found them by looking.

| Check | Catches | Cannot catch |
|---|---|---|
| 1. Rule check | Duplicate and overlapping walls, stack scatter, free ends, unwrapped types, Revit's own warnings | Whether a wall should exist |
| 2. Elevation against CAD | Roofs and walls off the drawing, lines the drawing doesn't have, drafted lines never modeled | Which drawing is right when two disagree. Anything hidden in elevation |
| 3. Envelope coverage | Missing walls and roofs anywhere the reconciled building says a surface belongs | Gaps narrower than its 10" tolerance: it read COVERED with six 3-7" slots open. Anything wrong in `prisms.json` |
| 3b. Slots | Gaps between the faces of walls on one plane, read from the model's own faces | Gaps between walls on different planes |
| 3c. See-through | Any hole, however narrow, that lets a floor or an inside finish be seen from outside | A hole that only shows another wall's siding. A fin or a wrong shape |
| 4. Look in 3D | Everything visual: form, seams, materials, family options, what reads wrong | Small offsets, so check 1 and 2 still run |

What they found on the first project (the first project, 2026-11-06):

| Finding | Found by |
|---|---|
| Hole in the center gable over the link roof, about 17 ft x up to 4'-3" | Check 3, confirmed in check 4 |
| Hole above the stair lean-to, about 8'-8" x 27-61" | Check 3, confirmed in check 4 |
| No roof over the entry vestibule, open to the sky | Check 3, confirmed in check 4 |
| Both misplaced infill walls lying on top of existing walls | Check 1, and Revit's own overlap warnings, ignored at the time |
| 21 stacked wall pairs off by under 2", with the same type up to 1" off | Check 1 |
| Floor-line seams across every facade, none of them drafted | Check 2, check 4 |
| Lean-to high edge 31" off the south elevation | Check 2 |
| Sill and apron on every window, flush door where a lite is drafted, two different glass materials | Check 4 and the family audit, after the user pointed at the sill |
| Raw joist at every roof edge, slab edge showing under a bay | Check 4 |

After the rebuild (2026-11-06 and 07), with checks 1 to 3 passing:

| Finding | Found by |
|---|---|
| Six slots 3-7" wide where a wall's end condition changed with height | The user saw two on screen. Then check 3b. Check 3 had read COVERED |
| A fin at a corner where a return sat 3/4" off the wall above it | Check 4 |
| Floor edge showing between a low roof and the wall above, and the roof's edges 2" and 5/8" short of its walls | Check 4, then check 3c |
| Siding courses shifted 30% at every joint between walls on different base levels | A check 4 close-up, measured with `course_offset.py` |
| Slab edge drawn through a wall seen at an angle | The user's 1/8" hidden-line view. The shaded exports hid it |
| Roof edges cut plumb where the drafting shows a 1 1/2" fascia board over a square-cut roof | The user, in the elevation views. Check 2's roof percentages had passed |
| Ridge caps, the flat-roof trim bands, window trim and PTAC grilles drafted but not modeled | The user, in the elevation views |

## Before you start

`mcp__transom__status` is ok. Keep calls small: the bridge gives up at 30 s. If a call times out, check `revit_list_dialogs` before
anything else: a warning dialog is the usual cause. Elevation CAD comes from `extract.cs` in `transom-model-06-openings`.

## Check 1: Rules

Run `gate_rules.cs`, with `TOL` from `planes.py`. Clear every FAIL or explain it in the plan file.
- **A duplicate wall usually means a hole somewhere else.** Don't just delete the duplicate. Find the span it was meant to close.
- **Read the warnings list, every time.** Some warnings are expected and the openings skill names them. Anything else gets explained.
- Steps over tolerance are not faults. Each must trace to a drawing or a ruling in the plan file.
- **6b, coursing:** every wall of a coursed type must sit on one base level, because the pattern starts at the base level. Fix a fail by
  changing the base level and offset together, never by nudging the offset.
- **Free ends:** an end with joins turned off was run to a plane on purpose, and passes. Any other free end fails unless it is named in
  `EXPECT_FREE`, with the reason in the plan file.

## Check 2: Elevations against the CAD

1. `export_elevations.cs`: one hidden-line DWG per elevation, from temporary duplicates. A plain duplicate carries no view-specific CAD.
2. `read_export.cs` through `execute_autocad_code` with `context: "application"`.
3. `python elev_compare.py <export dir> <cad dir> <out dir> [FACE ...]`, then **open each `compare_<FACE>.png`**.

No fitting is applied. The export is already in the same (u, z) frame as the CAD, so a real shift can't be hidden by aligning it away.

How to read it:
- **Red roof lines:** go to the sections and the other elevations. It may be the model, or the drawings may disagree. Conflicts go in the plan file.
- **Long horizontal wall lines with no drafted line** at a floor level are seams from per-level walls.
- **Layered drafting reads as distance.** Roof top, sheathing and underside are separate lines, so a line a constant 11" off may just be the
  drafted underside against the modeled top. Judge from the picture, not one number.
- The foundation and hidden lines add noise to the wall bands. The wall percentages are not pass/fail yet. Read the worst-lines list.
- **Roof edge profiles need their own check.** Run `edge_corners.py` on a window around each eave end, where the eave shows in
  cross-section. Every drafted corner should have a modeled corner within 1/4". The line-length bands can't see this: a plumb cut
  left the bottom corner 5" out and 1 5/8" low while the roof lines still read 95% within 3".
- **Drafted but not modeled** is a list of missing details as much as of noise. Plot each doubtful place with `cadzoom.py` and name it in
  the plan file: grade or hidden line, or a detail to model (`transom-model` 8e).

## Check 3: Envelope coverage

Run `mesh_dump.cs` (walls, roofs, floors, fascias and gutters, with window and door boxes), then `python envelope.py mesh_dump.txt prisms.json`. It samples every surface of the reconciled building (`transom-model`
Phase 8d) and reports where no modeled surface lies within 10". Every reported patch is a hole, or a wrong prism. Go and look at each one.

A patch is only the part further than 10" from anything, so a real hole is bigger than the patch. A gap narrower than 10" never shows
here. Checks 3b and 3c are for those.

## Check 3b: Slots

Run `wall_faces.cs`, then `python slots.py wall_faces.json --width 12`. It unions each plane's exterior faces and reports the narrow
gaps between them. Decide each one. A slot where a perpendicular wall butts into the plane is filled by that wall's end and is hidden.
A slot above a lower wall's top, or above a low roof, is real. Fix it as `transom-model` Phase 10b says: run the end to the plane
with joins off.

## Check 3c: See-through

Run `mesh_dump.cs`, then `python seethrough.py mesh_dump.txt --skip <open site walls> --ok <soffits> --inner "Gypsum,Resilient"`
(the inner materials are the wall types' interior finish and core). Rays from 8 directions at two tilts, on 1" pixels. Each cluster
is a place where a floor or an inside finish is the first thing seen from outside.
- **Run a control once per project.** Put a known defect back inside a TransactionGroup, dump the mesh, roll back, and confirm the
  check fails. On the first project the control caught the low-roof gap and one slot, and missed a slot that looked onto another
  wall's siding. That is why 3b also runs.
- Never `--skip` an element that closes the shell, such as a foundation wall or a soffit. The rays then reach whatever it hides and
  report that. Name visible exterior floors with `--ok` instead.

## Check 4: Look

Run `slices.cs`, choosing shots for this phase. Then **read every image.** An exported image nobody looked at checked nothing. Report what you see,
and show the user.

Always take the four corners, a cut through each mass and each junction, a plan cut per storey and one through the roofs, close-ups of every roof junction, and
one typical window and door close enough to read their parts. Add a close-up of every place where wall pieces meet on one plane or
round a corner above a lower wall or roof.

Look for:
- **Lines and steps** at floor levels and up the corners. Misalignments show as lines. On a continuous wall there should be none.
- **See-through:** the background color through the building, or the far side's inside face seen over a roof. A flat gray area where siding belongs is often this.
- **In a cut:** is the outline closed all the way round? Does each wall meet the roof above it? Does anything end in the air?
- **Roofs against the elevation:** eaves, rakes, ridges, and how each roof meets the next. Seams where one plane was built in pieces.
- **Wrong materials from outside:** raw joist or stud, gypsum, `Default Wall` gray, slab edges.
- **Family options against the drafted unit:** sill, apron, trim, door panel and lite, muntins, glass that differs between types. Does this make sense on this building?
- **Things sitting oddly:** rails floating off the wall, windows cut by a roof, a slab through glass, walls standing alone.
- **Slots and fins at wall ends,** where a wall's lower and upper parts meet different walls.
- **Coursing across joints and round corners.** Measure a doubtful joint with `course_offset.py`. Under about 5% of a course is straight.
- **The user's own view style.** When the user points at something, take a screenshot of their view. Revit drew a slab edge through a
  wall in the user's 1/8" hidden-line 3D view that no shaded export showed.
- **The CAD overlay in the real elevation views.** Export the elevation views themselves (with their CAD links) at 300 dpi and crop the
  roof ends, ridges and trim. The model's lines are gray and the CAD's coloured, and where they agree the CAD hides the model. A
  plumb-cut eave showed there as a gray face hanging below the drafted edge.

Render in Enscape before calling a facade done. These views don't show what flickers or how materials read.

## After details and site


`slots.py` on a fresh `wall_faces.json`, then the elevation compare (export, read back through AutoCAD, `elev_compare.py`), then
Realistic close-ups of each lot, entry, corner and the building base. Look for: stripes under the asphalt, grass on curb faces, grass teeth behind curbs,
spikes where a walk meets a curb,
grooves at the wall base, buried windows, light-looking asphalt, and site elements missing from new views.

## Inventory against the reference

`scripts/inventory.cs` (`ARGS { TARGET, OUT }`) counts what a model holds by kind (walls by type, floors, roofs,
railings, fascias, sweeps, toposolids, direct shapes, families by type, links, SA materials) and lists the levels.
`python scripts/compare_inventory.py <reference_inventory.json> <run_inventory.json>` prints every kind that differs.
The reference is the recorded inventory of the model you compare with. A full replay differs only in detail
items, annotation, a title block and old CAD imports (from the office project the original began as) and in the
superseded toposolids.

## Report


One table per check. The transform, the links, the tolerance, the wall and plane counts, every ruling, what is still open, worst first.
Show the 3D views. If the model is workshared, offer Synchronize with Central.

Also: what the hooks logged (`<work_dir>\bridge_log.jsonl` holds every bridge call of the run), and the contact sheet of
the SA ISO views. Offer Synchronize with Central only when the model is workshared, and only sync when asked.

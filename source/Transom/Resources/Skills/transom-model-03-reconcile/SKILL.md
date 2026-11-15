---
name: transom-model-03-reconcile
description: Stage 3 of transom-model. Turns linked plans, elevations and sections into one building on paper before anything is modelled - exterior wall faces per level, reconciled wall planes and continuous runs (drafting scatter separated from real steps), slab loops, a roof table, a junction list, envelope prisms and an inventory of drafted details - and gets the user's rulings. Use after the CAD is linked and before modeling walls, or when a CAD-based model has seams, steps or gaps between levels.
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

# Stage 3: reconcile the drawings into one building

Part of `transom-model`. **Model nothing until the user has seen this stage's output.** Scripts in `scripts/`; C# runs
through `run_file.cs`, Python in the folder that holds `data\`.

## Rules that drive this stage

- Plans govern wall locations within tolerance. Elevations govern heights and roofs. Where a section and an elevation
  disagree, the elevation wins (the user's ruling: the sections put the centre ridge 21" above the elevations).
- A step between levels must show in a second drawing. Report conflicts over 3" and ask. Record every ruling in
  `MODEL PLAN.md` AND in the data, so a rerun can't undo it.
- Sections are required input: they show what closes a junction. All three envelope holes on the first project were
  at junctions nothing had asked about.
- Compensate for drafting errors, don't hide them: measure, correct what is systematic, report what is scattered.

## Extract the wall facts


**Open each plan in the editor.** Run `dump_walls.cs`, then `python exterior_faces.py <LEVEL> [x0 y0 x1 y1]` for each level, in the
folder that holds `data\`. The bounds drop separate structures such as a trash enclosure. Classification is geometric: walls in
real files carry "Interior" styles on the exterior and sit on annotation layers, so never go by style or layer.

- **Never read AEC walls through a side database for this.** It returns zero-width extents, only the baseline. On the first
  project that silently erased the real 5" step at the thick shear walls, and the result looked like cleaner data.
- Require **0 unmatched face ends** on every level before going on.
- Extract everything else AutoCAD is needed for at the same time, so modeling never waits on it.

## Reconcile


**Model nothing until the user has seen this phase's output.**

**8a. Wall planes.** `python planes.py data . LOWER=Stone MAIN=Siding UPPER=Siding --gov MAIN` (levels bottom up, each with its wall
material). It finds the natural break between drafting scatter and real steps, merges faces into planes, keeps each plane on a
drawn line, closes each level's loop, and writes `planes.json` and `runs.json`. On the first project:

| Before | After |
|---|---|
| 66 faces on separate per-level lines | 30 planes |
| 3 of 39 stacked pairs coplanar | 33 of 39, the other 6 are the real steps |
| 66 walls | 54 continuous runs |
| Offsets 0.11" to 1.84" and 4.5" to 6.2", nothing between | Tolerance 2", largest wall move 1.85" |

Then check:
- **A narrow break means ask.** A wide one (1.84" to 4.5" here) means scatter and real steps separate cleanly.
- **Each real step needs a second drawing.** Find it in an elevation or section. The user may rule either way: on the first
  project the drawn elevation moved the upper tower walls onto the lower lines.
- **Runs under 12" are often stale data or drafting slips.** Check each one against the drawing. If a ruling changed a wall since
  the faces were extracted, correct `faces_<LEVEL>.json` so the ruling survives a rerun.
- Show the user the steps and every move over 1".

**8b. Roof plan.** A table in the plan file: for each mass and each low roof, the shape, slope, ridge, eaves, rakes and which
drawing says so. Trust order: an elevation showing the roof in profile, then a face-on elevation for extents, then a section only
for what no elevation shows (on the first project, the tower shed eave hidden behind the link).
Measured slopes are a check on the reading. Conflicts over 3" go to the user: on the first project the elevations disagreed by
up to 33" and were settled one view at a time with the sections unread.

**8c. Junction list.** One line for every place two masses or roofs meet: what closes it (a wall up to a roof, a roof dying into a
wall, a valley) and which drawing shows it. Also compare each level's loop with the level above: **any area with no level above it
needs a roof or deck.** Walk the whole perimeter of each mass above each lower roof. Where no drawing answers, ask. All three holes
on the first project were here: two infill walls on the wrong span, and a low roof nothing had asked for.

**8d. The envelope as prisms.** Write `prisms.json`: each mass, bay and low roof as a closed profile extruded along an axis, with its
top at the roof underside. A prism is `{"name","axis":"x"|"y","a":[start,end],"profile":[[v,z],...]}` in model inches, where `v` is
the other plan coordinate. Together they are the whole building. Masses and the coverage check both read this file.

**8e. Drafted details inventory.** List everything the elevations draw beyond walls, roofs and openings, with its size and where it
appears: the roof edge profile at each eave and rake (fascia board, tiers, cut), trim bands at flat roofs, ridge caps and vents, window
trim, louvers and grilles (PTAC units), soffits, rails, boxes and vents on roofs. Each one becomes a modeled item or an omission the user
has agreed to. Find them by plotting the drafted lines with `cadzoom.py` (in `transom-model-11-checks`). The layers in real files are pen
weights, so they say nothing about what a line is. On the first project all of these were drafted and none were modeled until the user
pointed at them: the two-tier roof edge, a two-band trim at the flat link roof (a 9.2" fascia under a 4.2" cap standing 1.7" proud),
ridge vent caps on two ridges (a 2.3" board 3.7" above the ridge, 22.7" out each side on supports at 19", stopping 12" short of each
rake), a 4.5" trim band around every window group, and PTAC grilles under the windows of the door groups.

## Recorded data of the first project

The first project's data set (kept with that project): `faces_<LEVEL>.json`, `planes.json`, `loops.json`, `runs.json` (planes.py
output, 52 runs), `runs_rest.json` (43 runs after the west mass was built and the rulings applied, with six junction
walls J1-J7), `slabs.json`, `prisms.json`, `roof_edges.json`, `wall_faces.json`, `openings_uv.json`, `ptac_blocks.json`.
`rebuild_data.py` and `rest_runs.py` regenerate the second set from the first. The roof table the shell was built from
is `reference\roof_table.json`.

## Helpers

`plot_elev.py`, `plot_sections.py`, `secseg.py`, `drawn_check.py` plot the drafted linework of an elevation or a section
for reading. `ptac_find.py` finds the PTAC blocks.
`transom-model-11-checks/scripts/cadzoom.py` zooms the drafting at a point, for reading details.

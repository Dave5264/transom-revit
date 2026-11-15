---
name: transom-model-06-openings
description: Stage 6 of transom-model. Models a building's exterior windows and doors from the drafted 2D elevations linked in its elevation views, one elevation at a time - family audit first, units detected from the linework, exceptions recorded, each unit mapped to a wall and cross-checked against the plan, placed, then verified in the elevation, the plan and 3D - plus Juliet rails in front of doors. Use when placing or rebuilding openings from CAD elevations, or when openings disagree with the drafting.
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

# Stage 6: exterior openings from the drafted elevations

The deliverable is every exterior opening of one facade at the size, height and position the drafted elevation
shows, each one cutting its host wall, checked against the elevation, the plan and a 3D view. Then the next facade.

## Rules (from the user who shaped this workflow)

1. **Work from the drafted 2D CAD elevations.** Position along the wall, width, height and sill all come from the
   elevation linework. **Don't trust anything 3D in AutoCAD Architecture** (window object heights, sills, sizes).
   The first attempt used ACA widths and matched lines near the jambs for heights. It produced types like 43.04 x 74.5,
   42 x 63.75 and 64.46 x 84, with openings sitting off the elevations, and the user rejected it.
2. **One elevation at a time.** Finish a facade, verify it in the elevation, the plan and 3D, and show the user.
   Only then start the next one.
3. **Every opening must cut its host wall** (`Wall.FindInserts`). An opening that doesn't cut is a failure.
4. **The plan is the cross-check**, and the source for faces that no elevation can show (a wall hidden behind a
   nearer mass). Report plan/elevation disagreements over 3". Don't pick one silently.
5. **Drafted sizes**, rounded to 1/2" (door widths to the inch). The family's Width is the drafted unit. The
   families add 1/2" for the rough opening, so a drafted 41 1/2" unit gets a clean 42" rough opening.
6. **Plain glass:** turn muntins off. The drafted glass has no grid.
7. **Audit the families first, and leave no option on by default.** Every visual option on a family or type in use gets a decision, traced to a
   drafted unit and to what makes sense on this building. The user: "you should be checking the families and types you use visually match the
   intent on the elevations and make sense for the condition." See Step 0.
8. Every correction goes in `fix_<FACE>.json` with a `_why`. Progress goes in the project's `MODEL PLAN.md` as
   each facade finishes. Don't sync to central unasked.

## Before you start

- Each elevation view has its facade's CAD **linked view-specific** (Draw Layer Foreground), on the model frame. The
  plans are linked in their floor plans. See `transom-model-02-cad` and `transom-model`.
- The exterior walls exist and their type's Function is Exterior (`transom-model` Phase 10).
- Working folder `<project>\Revit Modeling\openings\`. Write the scripts at the end of this skill into it. Every
  data file goes there too. Python 3 is enough (PIL only for cropping check images).
- `mcp__transom__status` is ok. Keep every call small: the bridge gives up at 30 s, and a timed-out script keeps
  running. Wait for `status` to answer before the next call.

## Step 0: Audit the families against the drafting

Before the first placement, and again for any new family. This isn't a formality: on the first project every window shipped with a sill and apron the
building doesn't have, because the family's default was left on.

1. Plot one drafted unit of each kind (window, pair, French door, slider, entry door) from the elevation linework.
2. Run `family_audit.cs` to list every visual option. It works before anything is placed if you name the families.
3. **Render each option that changes the look,** in a temporary view with the option set in turn, inside a TransactionGroup that is rolled back. Compare
   each with the drafted unit, and pick. Ask whether it makes sense for the condition, not only whether it matches: a window in lap siding has no projecting
   stone-style sill, and a full-lite door at the floor has no apron.
4. Write the decisions and reasons into `family_options.cs` and the plan file. Run it after every `place.cs`, because placing resets instance parameters.

What the first project's audit decided:

| Option | Default | Decision | Why |
|---|---|---|---|
| Windows: `Exterior Trim Visibility` | Yes, `with Sill` | No | Drafted with no projecting sill or apron. The user's ruling |
| Glass Panel Material | `+Exterior Glass` on some types, `_Glass Windows` (0 transparency) on others | One glass across the building | The second read as opaque panels |
| Door: `Panel Type` | `F`, flush | `FG` | The door is drafted with a tall 23.5" x 67" lite. `G` and `N` were far too small |
| Door: `Function` | Interior | Exterior | It's an exterior door |

The user answered the open question (2026-11-07). The plain rectangle about 4.5" outside each drafted window group is a flat trim band
around the whole group. There is none between mulled units, so a per-window `Picture Frame` option would draw trim that isn't there. The
hatched blocks under the windows of the door groups are PTAC grilles. Neither is a window option: model both as separate elements, and
look for both on every facade during the audit.

## Step 1: Extract the linework and walls

Run `extract.cs` (readOnly) with `VIEWS` = every elevation view (named SOUTH/NORTH/EAST/WEST) plus every floor plan
that has a CAD link. Rerun it with only the plans skipped (`VIEWS` = one elevation) whenever walls change, since it
also rewrites `walls.json`.

## Step 2: Know how the drafting reads

The drafted windows are nested rectangles. Measured on the first project (south, a 3'-6" x 5'-6" casement):

| Rectangle | Size | Layer there |
|---|---|---|
| Trim / casing | 50.5 x 74.5 (4.5" outside the frame) | LW-2 |
| **Unit frame (what we model)** | **41.5 x 65.5** | LW-2 |
| Frame inner lines | 39.8 x 63.8 | LW-0 |
| Glass / sash | 36.0 x 60.0 | LW-1 |

Layers change from block to block (the centre-mass units had their frames on LW-1/LW-4), so detection is geometric,
never by layer. Exclude only layers that are not windows: railings, grade, floor and detail lines.

## Step 3: Detect the units

```
python units.py . SOUTH --skip LW-3,LW-Grade-Existing,LW-Grade-Finish,A-Flor,A-Flor-Strs,A-Detl
```

A *glass* is an innermost closed rectangle. A *unit* is the largest closed rectangle around exactly one glass,
within 6.6" on every side (the trim sits further out and is ignored). Touching panes with one head and sill merge into
a slider and grow to its outer frame. Read the printed list against the elevation before going on.

## Step 4: Read the exceptions and write `fix_<FACE>.json`

For anything odd, list the real lines with `python lines.py . SOUTH u0 u1 z0 z1 [minlen]` and read the unit off
them. Patterns seen so far:

| What the list shows | What it is | Fix |
|---|---|---|
| Height 68.36 where its twins are 66.36 (or 67.5 vs 65.5), with lines every 2" below | Window over a louver panel. The top slat was read as the sill. The real sill is the frame line where the slats stop | `rules: {"h": 68.36, "dz0": 2.0}` |
| A 21-24" x 67" "window" inside a door, next to a 39-42" unit | An entry door's lite. The door and sidelite share one frame | `drop` both detections, `add` the door leaf (kind `door`) and the sidelite (kind `side`) from lines.py |
| Two 35-37" units with one head and sill, 2" apart or overlapping, crossing a floor line | Stair window: two units in one frame | `set` it to the outer frame, kind `stair` (Step 6) |
| A lower window shorter than its twins | A grade or well-rim line crosses it | `set` z0 to the outer frame's bottom from lines.py |
| 36.32 x 82 or 86 frames with a lite and rail lines | Full-lite doors at Juliet rails | `rules: {"w": 36.32, "kind": "french"}` |
| 12"-tall bands, a box on the roof, a step | Footings, steps, parapets | `drop` |

Schema (every key optional; `at` matches a detection's `[u0, z0]` within 0.6"):

```json
{"rules": [{"h": 68.36, "dz0": 2.0, "_why": "..."}, {"w": 36.32, "kind": "french"}],
 "set":  [{"at": [73.6, -57.65], "z0": -71.5, "kind": "slider", "_why": "..."}],
 "drop": [[1108.5, 8.0]],
 "add":  [{"u0": 1102.5, "u1": 1138.5, "z0": 0.0, "z1": 81.0, "kind": "door"}],
 "plan_only": [{"wall": 7754167, "uc": 154.705, "w": 35.5, "h": 65.5, "sill": 17.0, "kind": "win", "_why": "hidden face"}],
 "families": {"win": ["Window-Fixed", "SA {w} x {h}"]},
 "wall_exclude": "(?i)site|egress|retaining|foundation - concrete",
 "plan_layers": ["A-Glaz", "A-Door"]}
```

Kinds and default families: `win` and `french` and `side` use Window-Fixed (a full-lite door reads as glass from
outside), `slider` and `stair` use Window-Fixed-Double, `door` uses Masonry_Single. Units with two panes default
to `slider`.

## Step 5: Map each unit to a wall and cross-check the plan

```
python place_list.py . SOUTH
```

Each unit goes to the **frontmost** exterior wall facing the view whose face holds it and whose height band holds
its sill. Site walls (well U's, retaining walls) are excluded, or a well's front wall would take the lower windows.
Read the flags:
- `SPANS-WALL-HEIGHT`: the unit crosses a floor line (a stair window). Do Step 6 first.
- `END-CLEAR n`: under 4" from a wall end. It would straddle a corner and won't cut. Find the right wall or the
  drafting error.
- `PLAN-OFF n`: the plan's glazing centre differs by more than 3". Report it to the user with the numbers. Elevation
  governs unless they say otherwise.
- *Plan openings no unit claims*: a face hidden behind a nearer mass (place it with `plan_only`, using its visible
  neighbour's drafted size and that neighbour's plan-to-elevation offset), or the upper half of a stair window that
  both plans draw (ignore it).

## Step 6: Stair windows that cross a floor line

**With continuous walls, as `transom-model` builds them, there is nothing to do:** the window sits in the wall and `SPANS-WALL-HEIGHT` doesn't fire. This
step is only for per-level walls in an older model.

A window can only cut one wall. Run `stairwall.cs` with the stair bay's walls on both levels, **including the short
return walls at its corners**. Order matters: upper bases go up first, then lower tops. Either order inside one
transaction fails with "Can't keep elements joined / Can't make Wall". `SPLIT` must clear the window head
(UPPER+36" cleared a head at UPPER+30"). If the two levels' faces are not coplanar (a 5" step between plans), report
it before splitting. Then rerun Step 1 for `walls.json` and Step 5.

## Step 7: Place the facade

Run `place.cs` with `FILE` = `openings_<FACE>.json`. One TransactionGroup:
1. Delete every window/door on exterior walls facing this way. Old openings are often stale or not cutting.
2. Make and size the types (Width, Height, muntins off).
3. Place each opening on its wall's location line at `uc`, at the level, then set the sill and face it out.
4. **Force the cuts.** A batch placement leaves Revit's cuts stale: `FindInserts` is false and the wall has no hole,
   even though a single test placement cuts. Nudging every sill +0.01 ft, regenerating and setting it back fixes it.
5. Verify every opening (in FindInserts, at its `uc` and sill, facing out). Anything bad rolls the group back.

An opening is placed on the level it sits on, so a window on a continuous wall keeps its sill above its own floor. Then run `family_options.cs`.

## Step 8: Verify in the elevation, the plan and 3D

Run `check.cs` once per view: the facade's elevation, its floor plans cropped to a strip along that facade, and the
3D iso from that side (`SA ISO - SE` etc.). Crop the PNGs with PIL and compare each opening with the red linework.
Model edges should sit on the drafted frame lines, and the unit should look like the drafted one up close: trim, sill, panel, glass. Then run `transom-model-11-checks`. Show the user the elevation and the 3D, and say what's still open
(louvers not modeled, plan disagreements, slab edges seen through glass).

## Step 9: Record, then the next facade

Log in `MODEL PLAN.md`: ids deleted and placed, the type list, every `fix` entry, the flags and the decisions. Then
start the next elevation at Step 3.

## Pitfalls

- A wall type left unwrapped shows `Default Wall` on every jamb. Fix the type (`transom-model` Phase 10a). The stained-wood stripes visible through
  the glass are the window family's interior frame, not wall layers.

- Class declarations (the failure preprocessor) go **at the top** of a snippet. A class after the statements fails
  to compile.
- Wall location lines can read Finish Face: Exterior while the geometry is centred on them. Faces and depth come from
  the wall's bounding box (place_list does this). The location curve is only used to place.
- `doc.ExportImage` at 5000 px timed out the bridge. Stay at 3000-4000 px, one view per call.
- Window-Fixed hides its grid with `Muntin Visibility`. Window-Fixed-Double needs `Top Muntin Visibility` and
  `Bottom Muntin Visibility`.
- A stair window shows the floor slab through the glass. Stop the floor at the stair flights (top riser to the
  stair-window wall, across the stairwell) by **notching the slab's outer boundary** with `SketchEditScope`. An inner
  loop can't reach the edge, so it leaves a slab strip inside the wall that the window cut exposes.
- If the stair tower's face differs between the two plans (5" on the first project), ask which plan governs before
  splitting the wall. The elevation draws the window as one unit, so the face must be one plane there.
- Expected warnings: "Insert conflicts with joined Wall" (a slider where well walls join its host) and "inserts
  overlap" (an entry door's own frame overlapping its sidelite at the shared mullion). Both cut and look right.
  Check them in the elevation, don't chase them.
- TransactionGroup + RollBack restores temporary view overrides and crops. Never leave check colours in the
  user's views.

## First run (the first project, 2026-11-05)

South facade: 27 old openings replaced with 26 from the drafted elevation, all cutting. Types came out as
41.5 x 65.5 (casement), 41.5 x 66.5 (over a PTAC louver), 64 x 84 (link stair), 36 x 82/86 (French doors),
71.5 x 47.5 (lower sliders), 72 x 84 (stair pair), and a 36 x 81 entry door with a 42 x 79 sidelite. The plan agreed
within 1.5" except a lower slider (-7.4"), the stair window (72" drafted vs 60" in the plan) and the entry door. The
generic scripts below reproduced that placement exactly (0 differences). North (25), east (13) and west (24) then ran
through the skill as written, all cutting. One north stair window is held on a plan disagreement. The west file added
two `plan_only` windows on a return face the west mass hides. A copy of this skill is kept in

## Juliet rails and guards


**Openings:** use `transom-model-06-openings`, one elevation at a time, starting with its family audit. A continuous wall hosts windows
on every level, each on the level it sits on.

**Juliet rails / guards** from the elevations: duplicate a horizontal-rail railing type and set `RailStructure`
(`AddNonContinuousRail(name, height, offset)`, then `ProfileId`), clear the baluster pattern, and set start/end posts
(`BaseReferenceName = "Host"`, offsets) from the drafted lines relative to the door sill. `Railing.Create` with a one-line path in
front of the door, base offset = sill. The plan's handrail layer gives the depth off the wall.


On the first project: 14 Juliet rails, type `SA Juliet Rail - Horizontal Bars` (ten 1" bars at 4" centres from 1 3/8",
a top bar at 41 3/8", round posts, Rail Connections = Trim). Build the type with
`transom-model-07-families/scripts/railing_types.cs` (spec `reference\railing_types.json`). After the trim rings went
on, the rails were re-seated with their back on the trim face, 1/2" proud (stage 8).

## Scripts and records

Scripts named above are in `scripts/` (`family_audit.cs` and `family_options.cs` are in `transom-model-07-families`).
The first project's per-facade files (`elev_<FACE>.csv/json`, `units_<FACE>.json`, `fix_<FACE>.json`,
`openings_<FACE>.json`) stay with that project.

## Replay

`recipe_build.cs` stages `families`, `symbols` (window types made in the project), `instances` (windows and doors
re-hosted on the replayed walls, flips and sill heights restored) and `rails`. Verified 2026-11-11: 86 windows and 3
doors.

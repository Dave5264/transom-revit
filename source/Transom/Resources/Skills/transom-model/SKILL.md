---
name: transom-model
description: Runbook for modeling a render-ready Revit model of a building from its CAD drawings (AutoCAD Architecture plans, elevations, sections) through Transom - eleven stages from project setup and CAD cleanup through the shell, openings, families, details, a site of floors and an Enscape landscape, with checks after every stage - plus recording a finished model and replaying it into a new file, stage by stage, for a demonstration or a comparison. Proven end to end on a remodelled apartment building in Nov 2026. Use when asked to model a building from CAD, to plan or resume such a model, to rebuild or replay a model in a new file, or to stage a live build.
when_to_use: '"model this building from the CAD", "start a render model from these drawings", "resume the model", "rebuild the model in a new file", "replay the model", "walk me through the modeling process".'
argument-hint: "[method | replay | stage name]"
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

# Model a render-ready building from CAD

The process behind this runbook built a render-only exterior model of an existing building (later remodelled into
apartments): shell, details, a full site and an Enscape landscape, from AutoCAD Architecture drawings. Every step is a
script or a recorded decision. The bridge rules, the script loaders and the hooks are in the `transom` skill: read it
first.

## Two ways to run it

| Mode | What it does | When |
|---|---|---|
| **Method** | Derives everything from the drawings, stage by stage, with the checks and the user's rulings | A new project, or showing how a stage really works |
| **Replay** | Rebuilds a recorded finished model in a new file, stage by stage (`scripts/recipe_build.cs`) | Demonstrating a result quickly, comparing a method run, or moving a model onto a clean template |

Tested: a replay of the first project into Autodesk's plain template rebuilt all of it, and the inventory matched the
original kind for kind: 106 walls, 23 floors, 15 roofs (bounding boxes identical to 0.000"), 18 railings, 645
placed families, 11 fascias, 8 sweeps, 6 direct shapes, a toposolid and its 19 excavations. It took about 15 minutes,
most of it the family loads and the excavations.

## The stages

Run them in this order. Each sub-skill has its own steps, scripts, checks and traps.

| # | Skill | Produces | Method scripts | Replay |
|---|---|---|---|---|
| 1 | `transom-model-01-project` | model file, datum, levels, plans, elevations, SA ISO views, optional central | new_project, levels, views, worksharing | same scripts |
| 2 | `transom-model-02-cad` | `-Clean.dwg` copies, ACA exports, links on one transform | clean_copy, cleanup, export, link_cad | link_cad |
| 3 | `transom-model-03-reconcile` | wall planes and runs, slab loops, roof table, junction list, details inventory | dump_walls, exterior_faces.py, planes.py | recorded `shell\data` |
| 4 | `transom-model-04-materials` | textures folder, placeholder materials with Generic appearances | textures.py, materials_from_spec | reference `materials.json` |
| 5 | `transom-model-05-shell` | wall/floor/roof types, walls, slabs, roofs, two-tier roof edges, attachments | types_from_spec, walls_from_runs, roofs_from_table | recipe: walls, floors, attach, fascias |
| 6 | `transom-model-06-openings` | windows and doors from the drafted elevations, Juliet rails | extract, units.py, place_list.py, place | recipe: instances, rails |
| 7 | `transom-model-07-families` | scripted families, railing types, family audits | build_families, railing_types | family library + copytypes |
| 8 | `transom-model-08-details` | base band, corner boards, trim rings, caps, ridge caps, PTAC grilles | corners.py, trim_rings.py, place_boards | recipe: instances, sweeps, roofs |
| 9 | `transom-model-09-site` | grass toposolid, asphalt and concrete floors, railing curbs, paint, stops, fences, wells | grade.py, site_floors.py, site_fix2.py | recipe: topo, floors, excavate, rails, shapes |
| 10 | `transom-model-10-landscape` | Enscape planting and furniture designed for the site | planting_zones.py, planting_plan.py | recipe: instances |
| 11 | `transom-model-11-checks` | rule, elevation, envelope, slot, see-through checks; looks; inventory against the reference | gate_rules, elev_compare.py, envelope.py, inventory | inventory + compare |

Checks (stage 11) run after **every** stage, not only at the end.

## Before starting

1. **Plan file first.** Write `<project>\Revit Modeling\MODEL PLAN.md` from `transom/reference/model_plan_template.md`
   before touching a model: fixed facts, rulings, every stage as a checkbox, a progress log. Tick and log the moment a
   step finishes. A resuming session reads it first. Save a memory note that points at it.
2. **Config.** Copy `transom/reference/model_config.template.json` to `<project>\Revit Modeling\model_config.json` and
   fill it in. Every script takes its path as `CFG`. A sandbox or a demo gets its own `model_title`, `model_path` and
   `work_dir`.
3. **Tools.** The `transom` skill's requirements and launch rules. Enscape isn't needed to build: the planting shows as
   stand-ins in Revit and as real plants where Enscape runs.

## Recording a finished model, and replaying it

Record, all read-only on the finished model: `scripts/recipe_dump.cs` (walls, floors, toposolids, railings, family
types and instances, fascias, sweeps, direct shapes), `transom-model-05-shell` `dump_types.cs` and `dump_roofs.cs`,
`transom-model-04-materials` `dump_materials.cs`, `transom-model-07-families` `railing_types.cs MODE=dump` and
`export_families.cs`, and `transom-model-11-checks` `inventory.cs`. Keep the outputs in the project's
`Revit Modeling\reference\`.

Replay into a new file:
1. `transom-model-01-project`: `new_project.cs`, `levels.cs`, `views.cs`.
2. `transom-model-02-cad`: `link_cad.cs`, then `check_elevation_views.cs`.
3. `transom-model-04-materials`: `materials_from_spec.cs` (the recorded `materials.json`).
4. `transom-model-05-shell`: `types_from_spec.cs`, `hosted_sweep_types.cs` (after the families), `roofs_from_table.cs`.
5. `scripts/recipe_build.cs`, one STAGE per call: `families` (with load options), `symbols`, `walls`, `floors`,
   `attach`, `topo`, `excavate`, `rails`, `instances`, `sweeps`, `fascias`, `shapes`. `ONLY` limits a stage to
   comment prefixes (`SHELL`, `SITE`, `MODEL`, `RAIL`) or categories (`Windows,Doors`, `Planting,Site`), so the shell,
   the openings, the details and the site can be shown one at a time. `copytypes` copies railing, fascia and sweep
   types from an open source model instead of building them.
6. `transom-model-11-checks`: `inventory.cs`, then `compare_inventory.py` against the recorded inventory.

Old-to-new ids go to `<work_dir>\id_map.json` and roof ids to `roof_ids.json`, so a stage rerun skips what exists.

## A staged live build

`scripts/run_stage.cs` (through `transom/scripts/run_file.cs`, `ARGS { CFG, STAGE }`) runs one named stage and
opens its view: `project`, `links`, `materials`, `families`, `shell`, `openings`, `details`, `site`, `excavate`,
`landscape`, `finale`. Before each stage, `transom-ui`'s `demo_window.ps1` brings Revit to the front; after it,
`demo_shot.ps1` takes the screenshot. `families` (about 85 s) and `excavate` (about 3 minutes) outlast the bridge's
wait: poll `<work_dir>\demo_log.txt` for "DONE". `scripts/show_view.cs` and `scripts/demo_views.cs` make and show
building-sized 3D views, with no section box edges or level lines on screen. The CAD cleanup can run live in AutoCAD
first (`transom-model-02-cad`).

## Rulings from the first project (defaults: confirm them with the user on a new one)

- Delete only linework that won't physically exist. Keep the site. Link, never import.
- Plans govern wall positions. Elevations govern heights, roofs and openings. Where a section and an elevation
  disagree, the elevation wins. Report conflicts over 3" and ask.
- Same-material walls run continuously through the levels. One base level per coursed material.
- Families are design decisions: audit every option against the drafting, and leave no default on.
- A placeholder material for every surface kind, with every site portion its own. Measure every texture map's
  brightness.
- Details only where they're drafted. Trim 1/2" proud. An undrafted height is the shape's shorter plan dimension.
- Site: grass is a toposolid, hardscape is simple floors (concrete flat or one slope), curbs are railings on the
  asphalt.
- Landscape: designed new for the site. A model used as an asset library is never modified.
- Measure and look after every stage. A number narrows down where to look; it doesn't replace looking.

## Reference

`transom/reference/conventions.md` (loaders, the Pre class, config keys, id maps, comment tags), `sources.md` (the
practices this follows), `model_config.template.json`, `model_plan_template.md`.

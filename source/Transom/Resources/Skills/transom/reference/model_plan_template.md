# <Project>: Revit modeling plan and progress

Started <date>. **Read this whole file before doing anything.** It is the resume point when a session runs out. Tick the
checkboxes and add to the Progress log as each step finishes, never in a batch at the end.

## How to resume

1. Read this file, then the memory note that points at it.
2. Invoke the `transom-model` skill (it registers the hooks), then the sub-skill of the first unchecked stage.
3. Check `mcp__transom__status` and, if CAD is needed, `mcp__transom-acad__status`.
4. Continue from the first unchecked step. Never re-derive a fixed fact.

## Fixed facts (don't re-derive)

- Model: <path>. Revit <version>. Workshared: <yes/no>; last synced <date>.
- Config: `model_config.json` (title prefix `<title>`).
- Levels (reads / internal Z): <LOWER 90' / -10, MAIN 100' / 0, ...>. Phase for new work: <phase>.
- CAD frame -> model: x + <dx>", y + <dy>". Elevations: Y = 0 at <level>. Evidence: <objects that agreed>.
- Links: <file -> view, link id> ...
- Types: <wall, floor, roof types with widths>.

## Rulings (every drawing conflict, the decision, who made it)

| # | Where | Conflict | Ruling | By |
|---|---|---|---|---|

## Stages

- [ ] 1 Project, datum, levels, views (`transom-model-01-project`)
- [ ] 2 CAD cleaned, exported, linked; elevation views pass (`-02-cad`)
- [ ] 3 Planes, runs, slab loops, roof table, junction list, prisms, details inventory; user has seen them (`-03-reconcile`)
- [ ] 4 Textures and placeholder materials (`-04-materials`)
- [ ] 5 Types, masses, walls, slabs, roofs, roof edges, attachments (`-05-shell`)
- [ ] 6 Openings per facade, Juliet rails (`-06-openings`)
- [ ] 7 Scripted families, railing types, family audit (`-07-families`)
- [ ] 8 Band, corner boards, trim rings, caps, ridge caps, grilles (`-08-details`)
- [ ] 9 Site: grade, toposolid, floors, excavations, curbs, paint, stops, fences, wells (`-09-site`)
- [ ] 10 Landscape (`-10-landscape`)
- [ ] 11 Checks after every stage; final inventory and report (`-11-checks`)

## Next work / open items

## Progress log

- <date time> <what was done, ids, counts, deviations, checks and their results>

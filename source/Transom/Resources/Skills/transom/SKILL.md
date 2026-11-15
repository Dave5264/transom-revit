---
name: transom
description: Start here for any work in Revit or AutoCAD through Transom - the mcp__transom__*, mcp__transom-ui-assist__* and mcp__transom-acad__* tools. Routes to the right transom-* skill (modeling a building from CAD and its eleven stages, driving or testing Revit's UI, schedules, elevation tags) and holds the rules every bridge call follows - target the right model, write in your own transaction with a failure handler, long calls keep running after the 30-second timeout, never sync or save unasked, never force-kill. Use whenever a task touches Revit or AutoCAD through Transom.
when_to_use: 'Any Revit or AutoCAD task through Transom: "model this in Revit", "clean this DWG", "link the CAD", "build the walls", "click through Revit", "test the add-in", "read this schedule", "tag the elevations".'
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

# Transom: start here

Transom connects Claude to Revit (the `transom` bridge and `transom-ui-assist` for the UI) and to AutoCAD
(`transom-acad`). This skill picks the skill for the task and sets the rules every call follows. Invoking any transom
skill also registers the safety hooks below.

## Which skill

| The task | Skill |
|---|---|
| Model a building, or part of one, from CAD drawings; rebuild or replay a finished model in a new file | `transom-model` (the runbook), then its stages |
| Create the model, the elevation datum and levels, the working views, a central | `transom-model-01-project` |
| Clean DWGs down to what gets built, export AutoCAD Architecture objects, link CAD on one transform | `transom-model-02-cad` |
| Turn plans, elevations and sections into one set of wall planes, slab loops, a roof table, a junction list | `transom-model-03-reconcile` |
| Texture maps and placeholder materials that render (Enscape) | `transom-model-04-materials` |
| Wall, floor and roof types; walls, slabs, roofs, roof edges, attachments | `transom-model-05-shell` |
| Windows and doors from the drafted elevations; Juliet rails | `transom-model-06-openings` |
| Families by script, profiles, railing types, family option audits, carrying families to another project | `transom-model-07-families` |
| Bands, corner boards, trim rings, caps, ridge caps, wall grilles | `transom-model-08-details` |
| Site: grass toposolid, paving and walks as floors, railing curbs, paint, stops, fences | `transom-model-09-site` |
| Planting and site furniture with Enscape assets copied from another model | `transom-model-10-landscape` |
| Check a model against its drawings and for envelope faults; compare with a reference | `transom-model-11-checks` |
| Drive Revit's UI (dialogs, Properties palette, Edit Group, the Transom Hub); test an add-in through the real UI; present a build with the app in front | `transom-ui` |
| Inventory or read schedules | `transom-schedules` |
| Tag doors and windows in elevation views | `transom-elevation-tags` |

## Rules for every bridge call (each one tested)

1. **The target model.** The bridge's `doc` is whatever model is ACTIVE in Revit. When more than one model is open (an
   asset library, a client's cloud model), resolve the target by title from `app.Documents`, pass it to every
   transaction and collector, treat the others as read-only, and check their `IsModified` is still false afterwards.
2. **Writes.** Send them with `readOnly: true` and start your own `Transaction` with a failure preprocessor (the `Pre`
   class in `reference/conventions.md`). Tested 2026-11-15: a write in the bridge's own transaction
   (`readOnly: false`) that raises a single warning opens Revit's modal failure dialog, and the bridge hangs until
   someone answers it. The same write with a preprocessor commits and returns the warning as text.
3. **Long calls.** The bridge stops waiting after about 30 s and answers "timed out waiting for Revit (request not
   applied)". The request keeps running to the end (tested: a 40 s script finished). Check `revit_list_dialogs`
   first, because a modal dialog gives the same message. Then poll the script's own log file, or wait until Revit
   answers again, and read back what exists. Never resend a write blind.
4. **Heavy reads.** Split scans of many views or sheets into one call per view or sheet. A whole-model sheet scan once
   hung Revit. Long writes are fine when they log to a file.
5. **Sync and save** only when the user asks. A project note can grant standing permission for one model. Never save
   or sync a model opened as a library or a client's file. `SynchronizeWithCentral` must run with `readOnly: true`.
6. **Never force-kill** Revit or AutoCAD. Close with `CloseMainWindow` and answer the dialogs (`revit_list_dialogs`,
   `revit_click_dialog`).
7. **Launching.** `Start-Process Revit.exe "<path.rvt or .rte>"`. The bridge needs an open document, and
   `mcp__revit__launch_revit` hits a licensing error. Look for the unsigned add-in prompt on every launch; it appears
   after an update, and the bridge stays down until "Always Load" is clicked (`revit_find` + `revit_click_by_id`;
   AutoCAD: `scripts/acad_always_load.ps1`). Opening the shared template can raise "File Opened By Another User":
   continue, and never save the template.
8. **Never open an older `.rvt` in a newer Revit.** It upgrades the file for good. Test on a copy.
9. **Trust the artifact, not the status line.** Read back the model or the written file.
10. **Copying between models**: `ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, opts)`
    with an `IDuplicateTypeNamesHandler` returning UseDestinationTypes, in a transaction on the target. Levels map by
    name, so set the target's levels first and none are duplicated (tested). Never write to the source.

## Running scripts

Every C# step in these skills is a file. Send `scripts/run_file.cs` through `mcp__transom__execute_revit_code`
(readOnly:true) with `FILE` = the script and `ARGS` = its `{{KEY}}` values, and the file runs with the same globals
as a snippet. `scripts/acad_run_file.cs` does the same through `mcp__transom-acad__execute_autocad_code`.
`scripts/run.ps1` prints a ready snippet. `scripts/hello.cs` is a smoke test. The loaders, the `Pre` class, the config
keys, the id maps and the comment tags are in `reference/conventions.md`.

## Hooks

Registered by every transom skill's frontmatter and active for the rest of the session. A restarted session starts
without them, so invoke a transom skill again first. They're in `hooks/transom_hooks.ps1` (Windows PowerShell),
and `powershell -File hooks/transom_hooks.ps1 selftest` runs 17 cases.

| Event | Tool | Does |
|---|---|---|
| Before | `execute_revit_code` | Asks before a sync or a project save. Denies a write sent with readOnly:false and a Transaction without a failure preprocessor, including inside files run through the loaders. Notes writes through the active `doc`. |
| After | `execute_revit_code` | Logs every call to `<work_dir>\bridge_log.jsonl` (or `%LOCALAPPDATA%\Transom\logs`). Explains a timeout. |
| Before | `transom-acad` tools | Denies an AutoCAD save to anything but a `-Clean.dwg`. Asks before EXPLODE. Reminds you that QSAVE saves the active drawing. |
| Before | Bash, PowerShell | Denies force-killing Revit or AutoCAD, and accoreconsole EXPLODE. |
| After | Bash, PowerShell | After a Revit or AutoCAD launch: how to clear the unsigned add-in prompt. |

## Requirements

Revit 2025-2027 with Transom; AutoCAD Architecture 2027 with Transom for AutoCAD (CAD cleanup only); Python 3 with
shapely, scipy, numpy, matplotlib and Pillow (the `.py` method scripts only); Windows PowerShell (hooks, helpers).

## Where these skills live

Transom installs them as Claude Code skills in `%USERPROFILE%\.claude\skills\transom*`, with a copy in the Transom
library (`%LocalAppData%\Transom\skills`, the Hub's Claude Skills tab). An update replaces a file only if you haven't
edited it, so your changes survive. Delete an edited file to get the shipped version back.

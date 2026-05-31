# Transom — Project Brief

## One-line summary
A Revit add-in (C#) that exports schedules to spreadsheets with full visual fidelity and
imports edited values back into the model, with an optional Claude-assisted QA layer that
verifies results against the live model.

## The problem
Getting schedule data out of Revit and back in is painful, and the worst part is **type
parameters**. Editing them by hand is error-prone: you have to know whether a field lives on
the instance or the type, hunt down shared-parameter GUIDs, and avoid read-only fields.
Existing export options either lose formatting (CSV dumps) or don't round-trip (no reliable
way to push edits back). There's also no easy sanity check that what you exported — or
imported — is actually correct.

## What we're building
A single add-in, launched from a button on Revit's **Add-Ins** tab, that opens a two-tab
dialog:

- **Export** — pick one or many schedules, write them to `.xlsx` / `.csv` / `.xls` with the
  schedule's real formatting (merged cells, fonts, colors, grouping, hidden columns).
- **Import** — pick a previously exported workbook, auto-match its tabs back to schedules,
  preview the changes, and write edited values back into the model **including type
  parameters**, safely and inside one transaction.

The add-in does all the precise, deterministic work in C#. A separate, optional **Claude-assist**
layer (when the Cowork client + Revit MCP bridge are running) reviews results against the
live model — reconciling exports, pre-flighting imports, confirming changes landed, and
visually flagging problem elements in Revit.

## Why type parameters stop being a problem
The export reads each schedule column's exact `ParameterId` straight from the schedule
definition, which also tells us whether the field is a type or instance parameter. That map
is stored in the workbook. On import the tool resolves type-param columns to the element's
`ElementType` and writes there — no GUID hunting, no guessing. The add-in also detects when
multiple rows of the same type disagree on a type-param value and skips those conflicts
rather than writing contradictory data.

## Users
- Primary: Dave — Revit user who wants reliable schedule round-tripping and is comfortable
  building/installing a custom add-in.
- The tool assumes a desktop Revit install (2025 and 2027) and, for the optional QA layer,
  the Claude Cowork client with the Revit MCP configured.

## Goals
1. Export schedules to spreadsheets that look like the Revit schedule (full fidelity on
   `.xlsx`).
2. Reliably write edited values — especially type parameters — back into the model.
3. Make round-tripping safe: preview, conflict detection, unit parsing, transaction safety.
4. Layer in optional Claude QA that adds a model-aware second pair of eyes, without ever
   being required for the tool to function.

## Success criteria
- A schedule exported and re-imported with no edits produces zero changes (clean round-trip).
- Type-parameter edits in the spreadsheet land on the correct `ElementType` after import.
- Contradictory type-param edits are caught and skipped, not written.
- Non-`.xlsx` formats and the offline (no-Claude) path both work without errors.
- With Claude connected, the QA layer correctly flags injected discrepancies.

## In scope
- Two-tab GUI (Export / Import) on the Add-Ins tab.
- `.xlsx` / `.csv` / `.xls` export; full-fidelity formatting on `.xlsx`.
- Round-trip metadata + import with preview, conflict detection, unit parsing.
- Run-log emission + Claude-assist checkbox with MCP bridge detection.
- Staging → review → finalize flow for Claude-checked exports.
- Builds for Revit 2025 (.NET 8) and Revit 2027 (.NET 10) — multi-target.

## Out of scope (for now)
- Write-back to arbitrary (non-tool-exported) spreadsheets.
- Automated/scheduled triggering of Claude checks (interactive only).
- Image-cell content in exports.
- Revit versions other than 2025 / 2027.

## Related documents
- `SPEC.md` — the locked, decision-by-decision requirements.
- `IMPLEMENTATION_PLAN.md` — the technical/coding plan.
- `mockup.html` — visual mockup of both tabs.

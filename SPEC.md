# Schedule Excel — Revit Add-in Spec

A Revit add-in that exports schedules to spreadsheets with full visual fidelity and
imports edits back into the model, with an optional Claude-assisted QA layer that
verifies results against the live model.

Status: **requirements locked, not yet built.**
Last updated: 2026-05-31

---

## 1. Add-in shell

- **Language:** C#, single project.
- **Targets:** Revit 2025 (**.NET 8**, `net8.0-windows`) and Revit 2027 (**.NET 10**,
  `net10.0-windows`). Multi-target build; per-version configs reference each version's
  `RevitAPI.dll` / `RevitAPIUI.dll`. Note: Revit 2027 moved the all-users add-in folder to
  Program Files and added add-in isolation/manifest settings — handled per-version.
- **Excel engine:** NPOI (handles both `.xlsx` and legacy `.xls`). CSV written directly.
- **Entry point:** `IExternalApplication` adds a **Schedule Excel** button to the
  **Add-Ins** tab, under a "Schedule Tools" panel (icon + tooltip).
- Clicking the button opens a **tabbed dialog**: **Export** and **Import**.

---

## 2. Export tab (Revit → spreadsheet)

### Controls / layout
- **Active schedule** pinned in its own highlighted box at the top, always visible,
  pre-checked. Separate from the list below.
- **All schedules** list below it in a **fixed-height scroll region** (so the window size
  is constant regardless of schedule count), with a **filter/search** box, a **Select all**
  action, and an "N of M selected" counter.
- Checking list items adds them alongside the active schedule (each checked schedule →
  one tab). Active is simply always available and pre-checked.
- **Export folder** field + Browse… (defaults to the model's folder).
- **File type** dropdown: `.xlsx` / `.csv` / `.xls`.
- **File name** field (defaults to model / active-schedule name).
- **Claude-assist** checkbox (see §4a). Greyed out unless the MCP bridge is detected.
- **Export** and **Cancel** buttons.

### Data read
- Reads **rendered cell text** via `ViewSchedule.GetCellText`, so type parameters,
  calculated values, combined fields, and units come out exactly as displayed —
  no GUID hunting.
- Field → parameter mapping pulled from `ScheduleDefinition.GetField(...).ParameterId`,
  including whether each field is a type or instance parameter.

### Output structure
- Output **matches the schedule**: headers, grouping, subtotals/totals, and blank
  separator rows preserved.
- Multiple checked schedules → one worksheet tab each (`.xlsx` / `.xls`).
- **CSV** → one file per schedule, named `filename_<schedule>` (CSV can't hold tabs).

### Formatting fidelity (.xlsx — full visual match)
Read per-cell from `GetCellStyle` / `GetMergedCell` / section data and reproduced:
- Merged cells (grouped/merged headers and title rows).
- Bold / italic / underline.
- Text and background colors, **including conditional-formatting colors** (per-cell).
- Horizontal / vertical alignment.
- Cell borders (Excel weights are coarser → approximate thickness).
- Font name.
- Hidden Revit columns → carried as **hidden Excel columns** (data preserved,
  round-trippable, out of view).

Approximate (unit systems differ between Revit paper units and Excel):
- Column widths, row heights, absolute font point size (relative sizing preserved).

Not carried: image-based cell content (API returns no text). **CSV = data only.**
**.xls** supports styling within the old format's limits (≤65k rows, 256-color palette).

### Round-trip metadata
- Hidden `cowork_meta` sheet (`.xlsx` / `.xls` only) stores:
  - **per workbook:** source-model id (for cross-model detection on import).
  - **per tab/schedule:** the schedule's **UniqueId** + full name (drives import auto-select).
  - **per data row:** element **UniqueId** anchor (only on real element rows; group/total/
    blank rows none).
  - **per column/field:** field → parameter map, writable flags (read-only / calculated /
    combined marked non-writable), and the field's unit / spec (ForgeTypeId) for parsing.
- CSV is display-only (no metadata, not round-trippable).

### Guards
- Non-itemized schedule (rows collapse multiple elements) → **warn and ask** before
  exporting, since those rows won't round-trip.

---

## 3. Import tab (spreadsheet → Revit)

### Source & auto-select
- Pick a workbook (Browse…). Must be one this tool exported — matching relies on the
  hidden `cowork_meta` sheet. Arbitrary spreadsheets are rejected.
- On file pick, the add-in reads `cowork_meta` and, for each worksheet tab, the **schedule
  UniqueId** it was exported from (stored alongside the schedule name and source-model id —
  UniqueId is used, not the lossy ≤31-char tab name).
- It looks each schedule up in the current model and **auto-checks the matched schedules**
  in the import list. Each tab's columns map to *that schedule's* field→parameter
  definitions, so the tab↔schedule mapping drives which parameter each column writes to.
- **Unmatched UniqueId** → fall back to **name match**; if found, auto-select but flag
  "matched by name — verify." If still not found → flag, leave unchecked for manual mapping.
- **Different source model** (workbook's model id ≠ open model) → **warn but allow**; tabs
  map by name and import proceeds after the warning.
- **Claude-assist** checkbox (see §4a). Greyed out unless the MCP bridge is detected.
  When checked, the proposed changes are written to `run-log.json` for Claude pre-flight
  review *before* you commit (the two-step: review in chat → return → Apply).

### Matching & scope
- Match each data row to an element by **UniqueId**; compare spreadsheet value vs model.
- Write only **writable** fields; skip read-only / calculated / combined (flagged in metadata).
- Unmatched rows (deleted element or hand-added row) → **skip and report**.

### Units
- Edited text parsed back to internal units via `UnitFormatUtils.TryParse` against each
  field's stored unit/spec. Cells that can't be cleanly parsed are **skipped and reported**
  (never guessed).

### Type-parameter safety
- Type-param edits shown separately in the preview with **instance counts**.
- **Per-type conflict detection:** group edited rows by element type; if rows of the same
  type give *different* values for a type-param column, flag as a **conflict and skip**
  (reported, never written).
- Consistent edits (all rows agree, value differs from model) → write **once** to the type,
  on confirmation.

### Apply
- **Preview + confirm:** a diff list (element, field, old → new) the user approves before
  anything is written.
- All writes wrapped in **one transaction**, followed by a summary report.

---

## 4a. Claude-assist detection & checkbox

- Both tabs have a **Claude-assist** checkbox.
- On dialog open (and a small **Refresh**), the add-in **pings the MCP bridge's localhost
  port** (the same listener `get_revit_status` hits). Port discovered during build from the
  configured MCP setup.
  - Bridge answers → checkbox **enabled**.
  - No answer → checkbox **greyed out**, tooltip "Start Claude to enable."
- Probe is a TCP/HTTP health ping only (most reliable; not a process-name check).
- **Behavior when checked:** the run writes `run-log.json` (§4) and, on completion, shows a
  handoff note ("…complete — switch to Claude and ask it to verify").
- **Behavior when unchecked / offline:** plain run, no QA prep. Nothing depends on Claude
  being present — the add-in is fully standalone.
- Detection is informational; actual verification is interactive (user asks Claude in chat).

---

## 4b. Staging & finalize (Claude file access)

Claude (Cowork) can only read files inside the user's **connected Cowork folder**, not an
arbitrary export destination. So Claude-assist runs route through a staging folder.

- **Claude exchange folder:** a configurable add-in setting (default: a `.claude-exchange`
  subfolder of the connected Cowork folder, e.g. `…\Revit Coding\.claude-exchange\`). The
  add-in can't auto-discover the mounted folder, so the user sets this path once.
- **Export flow when Claude-assist is checked (stage → review → finalize):**
  1. Export writes the workbook **+ `run-log.json`** into the exchange folder (Claude-readable).
     Nothing is written to the user's chosen destination yet.
  2. Dialog shows "Staged for review — verify with Claude, then **Finalize**."
  3. User asks Claude in chat. Claude **opens the staged `.xlsx`** and reconciles its
     **values + formatting/layout** against the live model over MCP, and reports.
  4. User clicks **Finalize** → add-in copies the approved workbook to the chosen
     destination and clears staging. **Cancel** → nothing reaches the destination.
- **When Claude-assist is unchecked / offline:** file is written straight to the chosen
  destination, no staging, no finalize step.
- **Import** uses the same exchange folder for the pre-flight run-log (§3).

---

## 4. Run-log (enables Claude-assist)

- On every export/import run the add-in writes a structured **`run-log.json`** alongside
  the workbook describing exactly what it did:
  - every element UniqueId, field → parameter mapping, values read/written,
  - everything skipped, flagged as conflict, or unparseable.
- This log is the **contract** between the add-in and Claude. The add-in does **not** call
  Claude directly (no embedded API key, no tight coupling).

---

## 5. Claude-assist QA layer (interactive)

Claude (Cowork client) reaches into the live model through the **Revit MCP** independently
of the add-in, reads `run-log.json` + the workbook, and reconciles against the model.
**Trigger: interactive** — user runs the add-in, then asks Claude here to verify.

Checks to build toward:
1. **Export reconciliation** — open the staged `.xlsx` (§4b) and compare its values **and
   formatting/layout** vs the live model; flag mismatches, blanks, odd values; sanity-check
   subtotals; plain-language summary. Runs before Finalize.
2. **Import pre-flight** — review edits before write-back; flag risky / contradictory /
   out-of-range changes in plain English (second pair of eyes on the deterministic check).
3. **Post-import verification** — confirm each intended change actually landed; report
   silent failures.
4. **Visual flagging in Revit** — color / select / zoom to problem elements in the live
   model via MCP, so issues are visible in Revit, not just a list.

**Caveat:** the QA layer is live only when Revit + the MCP bridge are running and connected.
The add-in works standalone regardless.

---

## 6. Out of scope (for now)

- Automated/scheduled triggering of Claude checks (interactive only for now).
- Writing back to arbitrary (non-tool-exported) spreadsheets.
- Image-cell content in exports.

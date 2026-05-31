# Transom — Implementation / Coding Plan

Technical plan for **Transom**, a Revit add-in that exports schedules to spreadsheets with full visual
fidelity and imports edits back into the model, with an optional Claude-assisted QA layer.

> **This revision supersedes the earlier draft.** It reconciles `AUDIT.md` (first audit), `AUDIT2.md`
> (second audit), and web research. See `SPEC.md` for locked requirements and `BRIEF.md` for context.

**Targets:** Revit 2025 (**.NET 8**, `net8.0-windows`) and Revit 2027 (**.NET 10**, `net10.0-windows`),
C#, multi-target. **Excel via NPOI** (Apache-2.0; handles `.xlsx` + legacy `.xls`). **Scaffold base:**
[`Nice3point/RevitTemplates`](https://github.com/Nice3point/RevitTemplates) (MIT). **Reference (studied,
MIT, not a dependency):** [`bimone/addins-excelexporterimporter`](https://github.com/bimone/addins-excelexporterimporter).

---

## 0. Core architecture decision (the one that drives everything)

**ADDITIVE HYBRID.** Two independent passes produce one workbook:

1. **Display pass (owns the visible sheet + row order).** Render every visible cell from
   `ViewSchedule.GetCellText` / `GetTableData`, with styling from `GetTableCellStyle` / `GetMergedCell`.
   This reproduces *exactly what Revit shows* — calculated fields, combined-parameter fields,
   percentage/count fields, subtotals, grand totals, group headers, blank separators, per-column unit /
   precision / prefix-suffix overrides — and the row order **by construction** (Revit already sorted,
   grouped and filtered). **We do NOT re-implement sort/group/filter in C#.**

2. **Anchor pass (owns round-trip only).** Attach the source element's **UniqueId** to each rendered
   *element* body row (group/subtotal/blank rows get none), written into a **hidden anchor column**, plus
   per-row writable/binding flags and field spec. Round-trip values are resolved on import by reading the
   model and comparing to the displayed cell — never the reverse.

> Why not "enumerate elements → rebuild the table"? Because reading values from `element.Parameters` alone
> cannot reproduce calculated/combined/subtotal/overridden display values (AUDIT2 **C1**), and a C#
> re-implementation of Revit's sort/group/filter drifts from the real display, which mis-anchors rows
> (AUDIT2 **C2**). The display pass eliminates both.

**Anchor mechanism = to be decided by spike (milestone 2), two candidates:**
- **(A) Working-copy hidden-ID field.** On a temporary copy of the schedule (inside a `Transaction`/
  `TransactionGroup` that is **rolled back** so the user's schedule is never mutated), add a field carrying a
  unique per-element key, then read that column via `GetCellText` — giving row→UniqueId in the *exact same
  row order* as the display pass. First audit's recommendation; no sort re-implementation.
- **(B) Enumeration + match.** `FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType()` +
  correlate to body rows. Simpler but must solve correlation; fragile for several schedule kinds (see §10).

Spike **both** against a real sorted/grouped/itemized schedule and pick the reliable one. Round-trip
integrity (BRIEF success criterion #1) depends on this — prove it on day 2, not at the end.

---

## 1. Solution structure

Generated from the Nice3point template, then adapted:

```
Transom/
  Transom.csproj            multi-TFM net8.0-windows;net10.0-windows, UseWPF, per-TFM Revit refs
  Directory.Build.props     shared props (from template)
  App.cs                    IExternalApplication — ribbon panel + button
  Commands/
    OpenDialogCommand.cs     IExternalCommand — opens the tabbed dialog
  Ui/
    MainDialog.(xaml|cs)     tabbed window: Export + Import (matches mockup.html)
    ExportTabView, ImportTabView
    PreviewDialog            import diff + confirm
  Core/
    ScheduleReader.cs        display pass: GetCellText/GetTableData + styles
    AnchorResolver.cs        anchor pass: candidate (A) working-copy / (B) enumeration
    ScheduleKind.cs          detect key/embedded/multi-cat/material-takeoff/linked/itemized
    ExcelWriter.cs           NPOI workbook build (xlsx/xls) + CSV writer
    ExcelReader.cs           reads workbook + locates anchor column by sentinel + cowork_meta
    MetaModel.cs             cowork_meta schema (POCOs) + (de)serialization
    Importer.cs              match, parse, per-row binding, conflict-check, transaction write
    BindingClassifier.cs     per-(element,field) type-vs-instance + writability
    UnitsHelper.cs           UnitFormatUtils parse/format wrappers (IsMeasurableSpec-gated)
    RunLog.cs                run-log.json model + writer
    BridgeProbe.cs           async MCP bridge port ping (Claude detection)
    Staging.cs               exchange-folder staging + finalize/copy
    Settings.cs              persisted settings (exchange folder, bridge port)
  Resources/
    icon16.png … icon256.png (from branding/)
  Transom.addin             per-version manifest (2025 + 2027 variants)
```

---

## 2. Build, dependencies & deployment

### Multi-targeting
- `<TargetFrameworks>net8.0-windows;net10.0-windows</TargetFrameworks>`, `<UseWPF>true</UseWPF>`.
- Per-TFM `RevitAPI`/`RevitAPIUI` references (Nice3point NuGet API packages or HintPaths into each install).
  - **2027 uses `Nice3point.Revit.Api.* 2027.0.0-preview.*`** — preview surface; **re-verify at 2027 RTM** (AUDIT2 M1).
- Per-TFM `DefineConstants` (`REVIT2025` / `REVIT2027`) for the small amount of `#if` plumbing the runtime/
  manifest layer forces. The schedule APIs we use (ViewSchedule/TableData, ScheduleField, UnitFormatUtils,
  ribbon, Parameter) are stable 2025→2027 — branching is deploy/runtime only.

### NPOI dependency isolation (the integration risk)
- Pin a specific NPOI version; run `dotnet list package --include-transitive` and **co-deploy the exact
  transitive set** (esp. `System.Drawing.Common`) next to the add-in DLL (AUDIT2 M2).
- **Smoke-test NPOI loading inside Revit at milestone 1** (the documented `System.Drawing.Common` ALC
  conflict) — for both 2025 and 2027.
- **2027 manifest:** add explicit `PublicAssemblies` / `UseAllContextsForDependencyResolution` entries for
  NPOI + `System.Drawing.Common`. Note: the 2026→2027 ManifestSettings dedup behaviour is reportedly in flux —
  treat the smoke-test as mandatory, not a formality (AUDIT2 M1).

### Deployment
- **2025:** `%AppData%\Autodesk\Revit\Addins\2025\` (per-user — avoids UAC).
- **2027:** **per-user** `%AppData%\Autodesk\Revit\Addins\2027\`. (All-users moved to `Program Files` in 2027;
  per-user sidesteps the privilege/UAC issue and is cleaner.)

---

## 3. Ribbon & command entry

- `App : IExternalApplication`
  - `OnStartup`: `CreateRibbonPanel(Tab.AddIns, "Schedule Tools")`, add a `PushButton` bound to
    `OpenDialogCommand` with the Transom 16/32px icons + tooltip. (Confirmed supported 2025/2027.)
  - `OnShutdown`: nothing required.
- `OpenDialogCommand : IExternalCommand`
  - Capture `UIDocument`/`Document`; open `MainDialog` **modal**, owned to Revit via
    `new WindowInteropHelper(window){ Owner = uiapp.MainWindowHandle }`. Keep the apply step modal to avoid
    `ExternalEvent` complexity. All model reads/writes on this command's API context.

---

## 4. Export — display pass (visible sheet, full fidelity)

### Enumerate schedules
- `FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))`, excluding `IsTemplate` and
  `IsTitleblockRevisionSchedule`. Active schedule = `uidoc.ActiveView as ViewSchedule` (pinned if non-null).

### Read table content (rendered text — the fidelity source)
- `TableData td = vs.GetTableData();` for each present `SectionType` {Header, Body, Footer, Summary}:
  - `TableSectionData sec = td.GetSectionData(type);` → `sec.NumberOfRows`, `sec.NumberOfColumns`.
  - `vs.GetCellText(type, row, col)` per cell — exactly what Revit displays.
  - `sec.GetCellType(row,col)` to skip image/blank cells cleanly (`GetCellText` only returns text for
    Text/ParameterText/CustomField types).

### Read styles (full fidelity, xlsx)
- Per cell: `sec.GetTableCellStyle(row,col)` → `FontName`, `TextSize`, `IsFontBold/Italic/Underline`,
  `TextColor`, `BackgroundColor` (Revit `Color`→ARGB), `FontHorizontalAlignment`/`FontVerticalAlignment`,
  `BorderTopLineStyle` etc. (border = line-style ElementId → resolve weight → nearest NPOI BorderStyle; lossy,
  marked "approximate" in SPEC).
- Merged cells: `sec.GetMergedCell(row,col)` → `Top/Bottom/Left/Right`; **de-dupe** (an unmerged cell returns
  its own 1×1 bounds) to find genuine regions → NPOI `CellRangeAddress`.
- Sizing: prefer **`GetColumnWidthInPixels` / `GetRowHeightInPixels`** (convert to Excel units predictably)
  over the paper-unit (feet) variants. Absolute font point size + widths remain "approximate" (SPEC).
- Hidden Revit columns → `ScheduleField.IsHidden` → write the column then `sheet.SetColumnHidden`.
- **Style caching/interning is mandatory** (`.xls` 4,000-style cap; `.xlsx` ~64,000) — see §6 (AUDIT R3 / L1).

---

## 5. Export — anchor pass (round-trip)

For **round-trippable** schedules only (see §10), attach an element key per body row.

- **Anchor column:** a hidden column whose header carries a **magic sentinel** value (e.g.
  `__transom_uid__`) so the importer locates it by content, **not by index** (survives column moves;
  AUDIT2 H2). Each element body row gets its `element.UniqueId`; group/subtotal/blank rows are empty.
- **Mechanism (A) or (B)** per §0 — decided by the milestone-2 spike. If working-copy (A), all mutation
  happens inside a transaction/group that is **rolled back** so the real schedule is untouched (AUDIT R5).
- **Per-row binding + writability + spec** captured here for each (row element, field) — see §9.

### Field → parameter map
- `def.GetField(i)` → `ParameterId`, `FieldType` (`Instance` / `ElementType` / `Count` / `Formula` /
  `MaterialQuantity`), `IsCalculatedField`, `IsCombinedParameterField`, `IsHidden`, `HasSchedulableField`.
- Spec for unit parsing: **`ScheduleField.GetSpecTypeId()`** (or `sec.GetCellSpec(row,col)`), not fishing it
  off a sample parameter.

---

## 6. Export — writing the workbook (NPOI)

- `.xlsx` → `XSSFWorkbook`; `.xls` → `HSSFWorkbook`; CSV → `StreamWriter`.
- One `ISheet` per checked schedule; sheet name = sanitized schedule name (≤31 chars, strip `[]:*?/\`),
  de-duplicated. (Schedule **UniqueId** stored in meta drives import matching — never the lossy tab name.)
- Write section-by-section, applying cached `ICellStyle`. **Intern styles** in a dictionary keyed by style
  signature. For `.xls`, if the interned style count would exceed 4,000 → **hard-fail with a clear
  "too many styles for .xls — use .xlsx" message** (AUDIT R6 / item 6), don't let NPOI throw.
- Merges via `sheet.AddMergedRegion`; column widths / hidden columns set.
- **Hidden `cowork_meta` sheet** (`VeryHidden`): JSON blob in a single cell (§8).
- **CSV** = display data only (no styles, no meta, not round-trippable); multiple schedules → one file each
  (`filename_<schedule>.csv`).

---

## 7. cowork_meta schema (JSON, hidden sheet)

```json
{
  "tool": "Transom", "version": 1,
  "anchorSentinel": "__transom_uid__",
  "sourceModel": { "guid": "...", "path": "...", "title": "..." },
  "exportedUtc": "2026-05-31T...",
  "sheets": [
    {
      "sheetName": "Door Schedule",
      "scheduleUniqueId": "...", "scheduleName": "Door Schedule",
      "kind": "standard|key|materialTakeoff|embedded|multiCategory",
      "itemized": true, "roundTrippable": true,
      "phase": "New Construction", "activeDesignOption": null,
      "anchorColumnHeader": "__transom_uid__",
      "columns": [
        { "col": 3, "fieldName": "Fire Rating", "parameterId": 123456,
          "fieldType": "ElementType", "writable": true,
          "storageType": "String", "specTypeId": null }
      ],
      "rows": [
        { "excelRow": 5, "uniqueId": "...", "kind": "element",
          "bindings": { "Fire Rating": "type", "Mark": "instance" } },
        { "excelRow": 6, "uniqueId": null, "kind": "groupHeader" }
      ]
    }
  ]
}
```
- `excelRow` is **advisory only** — import re-derives row→anchor from the live sheet via the sentinel column
  (AUDIT2 H2). `bindings` are **per-row** (AUDIT2 M5).

---

## 8. Import

### Load + locate + auto-match
- Open workbook (`WorkbookFactory.Create`). Read `cowork_meta`. **Reject** non-Transom workbooks.
- For each data sheet: **find the anchor column by its sentinel header**; if missing/short → **reject the
  sheet with a clear message** (never mis-write). Re-derive row→UniqueId from the current sheet.
- Match each sheet's `scheduleUniqueId` → `doc.GetElement(uid)`:
  - found → auto-check ("matched"); not found → name-match ("by name — verify"); else flag, leave unchecked.
- Cross-model (workbook `sourceModel.guid` ≠ current doc) → warn, allow (map by name).
- Warn on phase / active-design-option mismatch (AUDIT2 M3).

### Build change set
- For each element body row → `doc.GetElement(uniqueId)` (skip+report unmatched: deleted / hand-added).
- For each **writable** column (per-row binding from meta): compare model value (formatted via
  `UnitFormatUtils.Format`, honouring the field's spec) to the cell text. If changed:
  - **gate with `UnitUtils.IsMeasurableSpec`**: only call `UnitFormatUtils.TryParse(units, specTypeId, text,
    out value)` for unit-bearing doubles; direct-parse strings/ints; resolve ElementId-valued by name.
    Unparseable → skip+report (never guess) (AUDIT item 7).
  - target: instance param on the element, or (type field) `element.GetTypeId()` → type param.

### Type-parameter safety (literal value, not formula mirror)
- Group proposed type-param writes by `(typeId, parameterId)`.
- **Different values within a group → conflict: skip all, report.** Consistent + differs from model → one
  write to the type, on confirm. (We write the **literal value to every instance row**'s underlying type;
  no Excel formula mirroring — AUDIT2 H1.) Preview shows type-param rows grouped with instance counts.

### Apply
- `PreviewDialog`: (element, field, old → new); type-param rows grouped w/ instance counts; skipped /
  conflict / unparseable lists.
- One `Transaction`. Every `Parameter.Set(...)` return value **checked** (it returns bool, can silently
  fail); skip writes to non-editable worksets; **re-read to confirm** the value changed; collect failures →
  summary + run-log (AUDIT item 6 / M3). Roll back on fatal error.

---

## 9. Type-vs-instance binding & writability (per-row)

- Primary signal: `ScheduleField.FieldType` — `ElementType` ⇒ type-bound, `Instance` ⇒ instance-bound;
  `Count`/`Formula`/`MaterialQuantity` ⇒ non-writable.
- For project/shared params, **confirm per (row element, field)** by probing
  `element.get_Parameter(id)` vs `type.get_Parameter(id)` (Building Coder technique) — because a shared param
  can be **type in one family, instance in another** within the same column (AUDIT2 M5). Store binding at
  **row** granularity in meta.
- No sample element (empty/filtered-to-zero schedule) or ambiguous → mark **non-writable + report**, never
  guess (AUDIT R4 / SPEC stance).
- Always check `Parameter.IsReadOnly` before writing. Shared-param writes may need the param **bound** in the
  target model (cross-model import) — report, don't crash (AUDIT R6).

---

## 10. Schedule-kind support matrix

Detect kind up front via **`ScheduleDefinition`**: `Definition.IsKeySchedule`, `Definition.IsMaterialTakeoff`,
embedded = `Definition.EmbeddedDefinition != null`, multi-category = `Definition.CategoryId ==
OST_MultiCategorySchedule` (not a name string), `Definition.IsItemized`, plus a scan for linked-model
elements. **Round-trip only where the anchor is sound:**

| Kind | Display export | Round-trip |
|---|---|---|
| Standard itemized (single category) | ✅ | ✅ |
| Key schedule | ✅ | ✅ (writable params live on the key element) |
| Multi-category | ✅ | ✅ (binding/grouping category-aware, per-row) |
| Non-itemized / multi-value collapsed | ✅ (from `GetCellText`) | ❌ disabled (no per-row anchor) |
| Material takeoff | ✅ | ❌ (rows are materials, not elements) |
| Embedded schedule | ✅ | ❌ (collector misses embedded blocks) |
| Linked-model elements | ✅ | ❌ (elements live in the link doc) |
| Related-element fields (e.g. door's room) | ✅ | ⚠️ that field non-writable (lives on another element) |

Display export must **never depend on the anchor pass succeeding** — non-round-trippable schedules still
produce a faithful read-only workbook (AUDIT2 M4). Warn-and-ask before exporting a non-round-trippable kind.

---

## 11. run-log.json (enables Claude-assist)

```json
{
  "tool": "Transom", "runId": "...", "mode": "export|import",
  "timestampUtc": "...", "model": { "guid": "...", "title": "..." }, "workbook": "….xlsx",
  "export": { "schedules": ["Door Schedule"], "rowCounts": { "Door Schedule": 142 },
              "displayOnly": ["Material Takeoff"] },
  "import": {
    "applied": [ { "uniqueId": "...", "field": "Fire Rating", "old": "1 Hour", "new": "2 Hour",
                   "binding": "type", "instancesAffected": 12, "verified": true } ],
    "skipped": [ { "reason": "unparseable|readonly|conflict|unmatched|missingAnchor", "detail": "..." } ]
  }
}
```
- The **contract** between the add-in and Claude. The add-in never calls Claude (no embedded key). Written to
  the exchange folder when Claude-assist is checked.

---

## 12. Claude-assist plumbing

- **`BridgeProbe`:** **async** TCP/HTTP health ping, explicit short timeout (200–500 ms), off the UI thread,
  result marshalled to the checkbox via the dispatcher (never block Revit's UI — AUDIT item 10).
- **Port = persisted, user-editable setting (default 48884)** + Refresh button — *not* build-discovered
  (AUDIT2 H4). The **write-capable community revit-mcp** is the bridge (the read-only 2027 built-in server
  can't do §5 visual flagging / write-back QA). Detection is informational only; all correctness is
  independent of it.
- **`Staging`:** when Claude-assist is checked on export, write workbook + run-log to the configured
  **exchange folder** (default `<connected Cowork folder>\.claude-exchange\`). Dialog shows
  "Staged for review"; **Finalize** copies to the user's chosen destination and clears staging; **Cancel**
  reaches the destination with nothing. When unchecked/offline: straight to destination, no staging.

---

## 13. Risks (updated)

| # | Risk | Severity | Mitigation |
|---|------|----------|------------|
| 1 | Row→element anchoring (no direct API). | **Critical** | Display pass owns order; anchor by sentinel column; **spike both (A) working-copy-ID and (B) enumeration** in milestone 2, pick winner. |
| 2 | Display fidelity for calc/combined/subtotal/overridden fields. | **High** | Render from `GetCellText` (the additive-hybrid invariant); never from element Parameters. |
| 3 | NPOI `System.Drawing.Common` ALC conflict (2025/2027). | **High** | Pin + co-deploy transitive set; 2027 `PublicAssemblies`; smoke-test in Revit at milestone 1. |
| 4 | 2027 = preview API + manifest-dedup in flux. | **Medium-High** | Keep 2027 a separate milestone; pin preview; re-verify at RTM; per-user Addins folder. |
| 5 | Type/instance binding (mixed shared params). | **Medium-High** | Classify **per-row** via `FieldType` + element/type probe; non-writable when ambiguous. |
| 6 | Silent `Parameter.Set` failures. | **Medium** | Check every return; re-read to confirm; surface in summary + run-log. |
| 7 | `UnitFormatUtils.TryParse` throws on non-measurable specs. | **Medium** | Gate with `IsMeasurableSpec`; direct-parse strings/ints. |
| 8 | Anchor desync on Excel edits. | **Medium** | Sentinel-located column; `excelRow` advisory; reject on missing anchor. |
| 9 | MCP bridge port unstable/unknown. | **Low-Med** | User-set port (default 48884), async probe, correctness independent. |

---

## 14. Build order (milestones)

1. **Toolchain + scaffold.** Generate Transom from Nice3point; multi-TFM net8/net10; ribbon button + empty
   WPF dialog building for **2025 (.NET 8)** and **2027 (.NET 10)**. **Smoke-test NPOI loads inside Revit
   2025** (trivial workbook write) — flush the `System.Drawing.Common` risk now.
2. **Anchor spike (Risk #1) — before anything else of value.** Implement both (A) working-copy-ID and
   (B) enumeration; verify each maps every body row to the correct UniqueId on a real sorted+grouped+itemized
   schedule. Pick the reliable one. If neither holds, cut round-trip scope — learn it now.
3. Export display pass → `.xlsx` (text only), eyeball vs Revit.
4. Export styling: styles, merges, hidden cols, **InPixels** widths; style interning.
5. Anchor pass + `cowork_meta` (sentinel column, per-row binding) + schedule-kind gating (§10).
6. CSV + `.xls` (enforce 4,000-style cap with a clear error).
7. Import: load, locate-by-sentinel, auto-match, change set, preview.
8. Import: `UnitFormatUtils` parse (IsMeasurableSpec-gated), per-row type-param conflict check, transaction
   write with **per-Set verification**, summary.
9. run-log + Claude-assist checkbox + **async** bridge probe + staging/finalize.
10. End-to-end clean round-trip (export → no-edit import → zero changes) on **2025**, across the test-model
    schedule kinds (key, multi-category, non-itemized display-only, phased…).
11. **Separate milestone:** validate the **2027** build once stable; resolve .NET 10 / manifest-isolation /
    preview-API issues at RTM.

## 15. Test fixtures (build via MCP when Revit is launched)
A model containing one of each: standard itemized, key schedule, multi-category, non-itemized, material
takeoff, embedded, linked-element, phased, design-option — plus the hybrid invariant assertion (visible
`GetCellText` == hidden round-trip value where expected).

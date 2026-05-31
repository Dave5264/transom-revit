# Schedule Excel — Implementation / Coding Plan

This is the technical plan the add-in will be built from. It is the document to audit for
viability. See `SPEC.md` for the locked requirements and `BRIEF.md` for context.

Target: Revit 2025 (**.NET 8**) and Revit 2027 (**.NET 10**), C#, multi-target. Excel via
NPOI. Optional Claude-assist via a local MCP bridge.

> CORRECTION (post-audit): an earlier draft said "both .NET 8." Revit 2027 runs on **.NET 10**
> (`net10.0-windows`) and relocates the add-in folder to Program Files with new isolation
> settings. See `AUDIT.md`.

---

## 1. Solution structure

```
ScheduleExcel/
  ScheduleExcel.csproj          multi-config (R25 / R27), net8.0-windows, WinForms or WPF
  App.cs                        IExternalApplication — ribbon panel + button
  Commands/
    OpenDialogCommand.cs        IExternalCommand — opens the tabbed dialog
  Ui/
    MainDialog.(xaml|cs)        tabbed window: Export + Import
    ExportTabView, ImportTabView
    PreviewDialog               import diff + confirm
  Core/
    ScheduleReader.cs           reads schedule table data + styles
    ExcelWriter.cs              NPOI workbook build (xlsx/xls) + CSV writer
    ExcelReader.cs              reads workbook + cowork_meta for import
    MetaModel.cs                cowork_meta schema (POCOs) + (de)serialization
    Importer.cs                 match, parse, conflict-check, transaction write
    UnitsHelper.cs              UnitFormatUtils parse/format wrappers
    RunLog.cs                   run-log.json model + writer
    BridgeProbe.cs              MCP bridge port ping (Claude detection)
    Staging.cs                  exchange-folder staging + finalize/copy
  Resources/
    icon16.png, icon32.png
  ScheduleExcel.addin           manifest (per-version install)
```

### Build configurations
- **Multi-target** `TargetFrameworks`: `net8.0-windows` (Revit 2025) and `net10.0-windows`
  (Revit 2027). Per-TFM `DefineConstants` (`REVIT2025` / `REVIT2027`) for any `#if` branching
  forced by 2027 API changes (see `AUDIT.md` for the 2027 change list).
- `<UseWPF>` true (WPF dialog).
- References to `RevitAPI.dll` / `RevitAPIUI.dll` via per-TFM `HintPath` into
  `C:\Program Files\Autodesk\Revit 2025\` and `…\Revit 2027\`, `Private=false` (do not copy).
- **NPOI** via PackageReference, copied to output. Pin/co-deploy its dependencies
  (esp. `System.Drawing.Common`) and smoke-test loading inside Revit at milestone 1 —
  documented AssemblyLoadContext conflict risk.

### Deployment
- Build output (add-in DLL + NPOI + deps) copied to a known folder; per-version `.addin`
  manifests:
  - 2025: `%ProgramData%\Autodesk\Revit\Addins\2025\`.
  - 2027: the relocated **Program Files** add-ins location + 2027's add-in isolation/manifest
    settings. Confirm exact path/settings against the 2027 SDK.

---

## 2. Ribbon & command entry

- `App : IExternalApplication`
  - `OnStartup`: create a ribbon panel "Schedule Tools" on the built-in **Add-Ins** tab via
    `UIControlledApplication.CreateRibbonPanel(Tab.AddIns, ...)` (or
    `GetAddinPanels`/`CreateRibbonPanel` with the Add-Ins tab), add a `PushButton` bound to
    `OpenDialogCommand` with 16/32px icons + tooltip.
  - `OnShutdown`: nothing required.
- `OpenDialogCommand : IExternalCommand`
  - `Execute`: capture `UIDocument`/`Document`, open `MainDialog` (modal, owned to the Revit
    window via `WindowInteropHelper` + main window handle). All model reads happen on the
    API context of this command.

---

## 3. Export — reading the schedule

### Enumerate schedules
- `new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))`, filtering out
  `vs.IsTemplate` and `vs.IsTitleblockRevisionSchedule`. Key schedules included.
- Active schedule = `uidoc.ActiveView as ViewSchedule` (pin it if non-null).

### Read table content (rendered text)
- `TableData td = vs.GetTableData();`
- For each `SectionType` in {Header, Body, Footer, Summary} present:
  - `TableSectionData sec = td.GetSectionData(sectionType);`
  - dims: `sec.NumberOfRows`, `sec.NumberOfColumns`.
  - text per cell: `vs.GetCellText(sectionType, row, col)`.
- This yields exactly what the schedule displays — type params, calculated values, combined
  fields, units — with no parameter lookups. Header/Body/etc. concatenated in order
  reproduces grouping, subtotals, totals, blank separators.

### Read styles (full fidelity, xlsx)
- Per cell: `TableCellStyle style = sec.GetTableCellStyle(row, col);`
  - font: `style.FontName`, `style.TextSize`, `style.IsFontBold`, `IsFontItalic`,
    `IsFontUnderline`.
  - colors: `style.TextColor`, `style.BackgroundColor` (Revit `Color` → ARGB).
  - alignment: `style.FontHorizontalAlignment`, `FontVerticalAlignment`.
  - borders: `style.BorderTopLineStyle` etc. (line style → nearest NPOI BorderStyle).
- Merged cells: `TableMergedCell mc = sec.GetMergedCell(row, col);` → `mc.Top/Bottom/Left/
  Right`. Build NPOI `CellRangeAddress` from unique merge regions.
- Column widths: `sec.GetColumnWidth(col)` (paper units) → convert to Excel char width
  (approximate). Row heights similar via `sec.GetRowHeight(row)`.
- Hidden fields: `ScheduleDefinition.GetField(i).IsHidden` → write column then hide it in NPOI
  (`sheet.SetColumnHidden`).

### Field → parameter map (drives round-trip)
- `ScheduleDefinition def = vs.Definition;`
- For each field index: `ScheduleField f = def.GetField(i);`
  - `f.ParameterId` (ElementId — BuiltInParameter if negative, else project/shared param).
  - type vs instance: resolve via the parameter — for a project/shared param, check the
    `Definition`/binding; for built-ins, known. Practical approach: probe a sample element's
    instance vs type `Parameter` for that id to classify, and cache per field.
  - writability: `f.FieldType` (Instance vs Calculated vs combined), `f.IsCalculatedField`,
    `f.IsCombinedParameterField`; plus `Parameter.IsReadOnly` on a sample.
  - storage type + spec: from the `Parameter` (`StorageType`, `GetUnitTypeId()`/`Definition
    .GetDataType()` for the ForgeTypeId used in unit parsing).

### Per-row element anchor
- For an **itemized** schedule, body data rows correspond 1:1 to elements. Obtain the element
  per row. Candidate approach: `FilteredElementCollector(doc, vs.Id)` returns the elements
  shown by the schedule; correlate to rows by the schedule's sort/group order. **RISK — see
  §8.** Store each element's `UniqueId` as the row anchor.
- Non-itemized schedule (`def.IsItemized == false` or collapsed): warn-and-ask; collapsed
  rows get no anchor.

---

## 4. Export — writing the workbook (NPOI)

- `.xlsx` → `XSSFWorkbook`; `.xls` → `HSSFWorkbook`; CSV → manual `StreamWriter`.
- One `ISheet` per checked schedule; sheet name = sanitized schedule name (≤31 chars, strip
  `[]:*?/\`), de-duplicated.
- Write cells section-by-section, applying `ICellStyle` (font, colors, alignment, borders).
  Cache styles to stay under the `.xls` 4000-style / `.xlsx` practical limits.
- Apply merges via `sheet.AddMergedRegion`. Set column widths / hidden columns.
- **Hidden `cowork_meta` sheet** (`sheet.IsHidden = true` or VeryHidden): a JSON blob (single
  cell) OR structured rows holding the metadata in §6. JSON-in-a-cell is simplest and
  robust.
- CSV with multiple schedules → one file per schedule, `filename_<schedule>.csv`, body text
  only (no styles, no meta).

---

## 5. Import

### Load + auto-match
- Open workbook (`WorkbookFactory.Create`). Read `cowork_meta`.
- For each data sheet: read its stored schedule `UniqueId`; `doc.GetElement(uniqueId)`.
  - found → auto-check, status "matched".
  - not found → match by stored schedule name against current `ViewSchedule`s → "by name"
    (flag) or "not found".
- Compare workbook source-model id to current `doc` (e.g. `doc.CreationGUID` or stored path)
  → cross-model warning, allow.

### Build change set
- For each data row with an anchor `UniqueId` → `doc.GetElement(uniqueId)`.
  - unmatched anchor → skip + report.
- For each writable column: compare current model value (formatted via UnitFormatUtils) to the
  cell's current text. If changed:
  - parse cell text → internal value with `UnitFormatUtils.TryParse(units, specTypeId, text,
    out double value)` for doubles; direct for strings/ints; ElementId-valued params resolved
    by name where applicable.
  - unparseable → skip + report.
  - target: instance param on the element, or (type field) the element's `ElementType`
    parameter.

### Type-parameter conflict check
- Group proposed type-param writes by `(ElementTypeId, parameterId)`.
- If grouped rows propose **different** values → conflict: skip all, report.
- If consistent and differs from current type value → one write to the type.

### Preview + apply
- `PreviewDialog` lists (element, field, old → new), type-param rows grouped with instance
  counts, plus skipped/conflict/unparseable lists.
- On confirm: single `Transaction`; set values via `Parameter.Set(...)`; collect failures;
  show summary. Roll back on fatal error.

---

## 6. cowork_meta schema (JSON)

```json
{
  "tool": "ScheduleExcel", "version": 1,
  "sourceModel": { "guid": "...", "path": "...", "title": "..." },
  "exportedUtc": "2026-05-31T...",
  "sheets": [
    {
      "sheetName": "Door Schedule",
      "scheduleUniqueId": "...", "scheduleName": "Door Schedule",
      "itemized": true,
      "columns": [
        { "col": 0, "fieldName": "Mark", "parameterId": -1010106,
          "binding": "instance", "writable": true,
          "storageType": "String", "specTypeId": null },
        { "col": 3, "fieldName": "Fire Rating", "parameterId": 123456,
          "binding": "type", "writable": true,
          "storageType": "String", "specTypeId": null }
      ],
      "rows": [
        { "excelRow": 4, "uniqueId": "...", "kind": "element" },
        { "excelRow": 5, "uniqueId": null, "kind": "groupHeader" }
      ]
    }
  ]
}
```

## 7. run-log.json schema (per run)

```json
{
  "tool": "ScheduleExcel", "runId": "...", "mode": "export|import",
  "timestampUtc": "...", "model": { "guid": "...", "title": "..." },
  "workbook": "….xlsx",
  "export": { "schedules": ["Door Schedule"], "rowCounts": { "Door Schedule": 142 } },
  "import": {
    "applied": [ { "uniqueId": "...", "field": "Fire Rating", "old": "1 Hour",
                   "new": "2 Hour", "binding": "type", "instancesAffected": 12 } ],
    "skipped": [ { "reason": "unparseable|readonly|conflict|unmatched", "detail": "..." } ]
  }
}
```

- Written to the exchange folder when Claude-assist is checked (and alongside the workbook).

---

## 8. Claude-assist plumbing

- `BridgeProbe`: TCP/HTTP health ping to the MCP bridge's localhost port (discovered from the
  configured MCP setup; configurable). Short timeout; result enables/greys the checkbox.
- `Staging`: when Claude-assist checked on export, write workbook + run-log to the configured
  **exchange folder** (default `<connected Cowork folder>\.claude-exchange\`). Dialog shows
  "Stage for review"; **Finalize** copies to the user's chosen destination and clears
  staging. Exchange folder path is a persisted add-in setting.
- Claude (separate process) reads the staged file + run-log, queries the live model over MCP,
  and reports. Interactive trigger only; the add-in never calls Claude.

---

## 9. Key risks / open technical questions (for audit)

1. **Row → element correlation in itemized schedules.** The table-data API gives cell *text*
   but no direct row→ElementId. Need a reliable way to map each body row to its element
   (collector order vs schedule sort/group order). If unreliable, round-trip anchors break.
   Possible fallbacks: include a hidden "Element Id"/GUID field in a working copy of the
   schedule; or read elements via the schedule and re-sort to mirror the definition's
   sort/group fields. **Highest-risk item.**
2. **TableCellStyle availability/shape.** Confirm `GetTableCellStyle`, `GetMergedCell`,
   `GetColumnWidth`, `GetRowHeight`, and the exact `TableCellStyle` members exist and are
   populated for schedules in 2025/2027 (API has shifted across versions).
3. **Type vs instance classification + writing type params.** Confirm classifying a field's
   binding and writing to `ElementType` parameters works for shared/project params, and that
   `Parameter.Set` on the type behaves as expected (and the all-instances side effect).
4. **UnitFormatUtils.TryParse round-trip.** Confirm the ForgeTypeId/spec retrieval per field
   feeds `TryParse` correctly across unit types; identify formats it can't parse.
5. **NPOI on .NET 8 inside Revit 2025/2027.** Confirm NPOI loads cleanly in Revit's
   AssemblyLoadContext without dependency conflicts; check `.xls` style/row limits don't bite.
6. **Ribbon button on the Add-Ins tab.** Confirm panels can be created on the built-in
   Add-Ins tab in 2025/2027 (vs a custom tab) via the supported API.
7. **MCP bridge port.** Confirm the bridge exposes a pingable localhost port and what it is,
   so `BridgeProbe` can detect it.
8. **WPF vs WinForms modal ownership** under Revit's message loop on .NET 8.

---

## 10. Build order (milestones)

1. Project skeleton + ribbon button + empty dialog, building for R25 and R27.
2. Export: enumerate + render text → `.xlsx` (no styles), open and eyeball.
3. Export: full styling, merges, hidden cols, widths.
4. cowork_meta + field→parameter map + row anchors (resolve Risk #1 first).
5. CSV + `.xls` outputs.
6. Import: load, auto-match tabs, build change set, preview.
7. Import: parsing, type-param conflict check, transaction write, summary.
8. run-log + Claude-assist checkbox + bridge probe + staging/finalize.
9. End-to-end round-trip test (export → no-edit import → zero changes).

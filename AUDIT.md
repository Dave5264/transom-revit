# Independent Viability Audit — Schedule Excel Add-in

**Auditor role:** Senior Revit API / .NET engineer (independent, skeptical)
**Date:** 2026-05-31
**Documents audited:** `IMPLEMENTATION_PLAN.md` (primary), `SPEC.md`, `BRIEF.md`
**Method:** API claims verified against revitapidocs.com (2025), Autodesk forums, The Building Coder, the official Autodesk Developer Blog (Revit 2027 SDK), and NPOI/POI docs. Citations inline.

---

## (a) Overall viability verdict

**Buildable as written: NO — not without correcting one factual error and re-architecting one feature.**
**Buildable after the fixes below: YES, with medium-high confidence (~75%).**

The core concept is sound and most of the API surface the plan leans on is real and confirmed in the 2025 API. The export path (read text + styles + merges + widths → NPOI) is solid. The import path's *parameter writing* is feasible. But two things are serious:

1. **A hard factual error that invalidates a stated foundation:** the plan, SPEC, and BRIEF all assert "Revit 2025 and 2027, **both .NET 8**." That is wrong. **Revit 2027 runs on .NET 10**, ships new add-in isolation/manifest semantics, and moves the all-users add-in folder from `ProgramData` to `Program Files`. The plan's "multi-config, single `net8.0-windows` TFM, two `.addin` manifests in `…\ProgramData\…\Addins\2025|2027`" approach does not hold for 2027. This needs a real fix, not a footnote.

2. **The stated highest risk (§9.1, row→element correlation) is real and the plan's preferred fallback is the weak one.** There is no API that returns the ElementId for a given body row. The plan's primary idea (collector + re-sort to mirror the definition) is unreliable; the robust approach (inject a hidden ID field into a working copy of the schedule) is listed only as a secondary "possible fallback." The build order even defers this to milestone 4. Round-trip integrity — the project's #1 success criterion — rests entirely on getting this right, so it must be the *first* thing proven, and the hidden-ID approach should be promoted to the primary design.

Everything else ranges from CONFIRMED to FEASIBLE-WITH-CAVEATS. No other item is a showstopper.

---

## (b) Per-item findings

### 1. Row → element correlation for itemized schedules (§9.1) — **RISKY, plan's primary approach LIKELY INFEASIBLE; robust fallback FEASIBLE**

There is **no API that maps a schedule body row to its ElementId.** This is long-standing and explicitly confirmed by Autodesk forum staff and The Building Coder: you cannot get element references directly from a schedule. The only element-level handle is `new FilteredElementCollector(doc, vs.Id)`, which returns the elements that appear in the schedule **but in collector (essentially ElementId) order — not in the schedule's sorted/grouped/filtered display order.** The plan's §3 "candidate approach" — collect via the view, then "re-sort to mirror the definition's sort/group fields" — is the **fragile path**: faithfully reproducing Revit's multi-level sort + grouping + headers/subtotals/blank-separator row interleaving + "itemize every instance" toggle + multi-value collapsing, in C#, well enough to land each `UniqueId` on the exact right body row, is error-prone and will silently mis-anchor rows. Silent mis-anchoring is the worst failure mode here (writes land on the wrong element).

The **reliable, community-proven method** is the one the plan lists only as a secondary fallback: on a **temporary working copy of the schedule**, add a field that carries a unique element key (e.g. a text field stamped with the ElementId/UniqueId, or use the built-in capability to read each row's parameter), read `GetCellText` for that column per row to get the exact row→element key, then discard the working copy. This sidesteps re-sorting entirely because you read the key *from the same table you read the data from*.

Additionally — and the plan misses this — `TableSectionData` exposes **`GetCellParamId(row, col)`** and **`GetCellCategoryId(row, col)`** ([TableSectionData, 2025](https://www.revitapidocs.com/2025/a0e6f821-5f53-1eb0-eba1-16554b3c0dc8.htm)). These give the parameter/category behind a cell but still **do not yield the ElementId of the row**, so they help with column→parameter mapping (item 3) but do not solve row correlation. Do not over-rely on them for anchoring.

> **Verdict:** Make-or-break confirmed. Promote the hidden-ID-field-on-a-working-copy approach to the **primary** design and prove it in milestone 1–2, not milestone 4. The "re-sort the collector" idea should be dropped or kept only as a sanity cross-check.
> Sources: [Autodesk forum — Get Element of ViewSchedule.GetTableData Row](https://forums.autodesk.com/t5/revit-api-forum/get-element-of-viewschedule-gettabledata-row/td-p/5907290); [The Building Coder — Schedule API and access to schedule data](https://jeremytammik.github.io/tbc/a/0761_access_schedule_data.htm)

---

### 2. `GetTableData` / `TableSectionData` members + `TableCellStyle` — **CONFIRMED feasible**

All members the plan names exist on `TableSectionData` in the **2025** API and are documented unchanged through 2026: `GetCellText`, `GetTableCellStyle`, `GetMergedCell`, `GetColumnWidth` (feet) **and** `GetColumnWidthInPixels`, `GetRowHeight` (feet) **and** `GetRowHeightInPixels`. `TableCellStyle` exposes `FontName`, `TextSize`, `IsFontBold`, `IsFontItalic`, `IsFontUnderline`, `TextColor`, `BackgroundColor`, `FontHorizontalAlignment`, `FontVerticalAlignment`, and `BorderTopLineStyle`/etc. (border = an `ElementId` of a line style, not a width — see caveat).

**Caveats:**
- **`GetMergedCell` returns a `TableMergedCell`** with `Top/Bottom/Left/Right`. Note that for an *unmerged* cell it returns that cell's own 1×1 bounds, so you must de-dupe to find genuine merge regions (the plan implies this; make it explicit).
- **Borders are line-style ElementIds, not pixel widths.** The plan's "nearest NPOI BorderStyle" mapping is doable but inherently lossy and requires resolving the `GraphicsStyle`/line weight from that id to pick an Excel weight. SPEC already calls this "approximate" — fine.
- **Widths are in feet (paper units).** Prefer `GetColumnWidthInPixels` / `GetRowHeightInPixels` over a paper-unit→char-width guess; pixels convert to Excel widths far more predictably than feet. The plan only mentions the feet variants.
- Use `GetCellType(row,col)` to skip image/blank cells cleanly (`GetCellText` only returns text for Text/ParameterText/CustomField cell types).

> Sources: [TableSectionData Class, 2025](https://www.revitapidocs.com/2025/a0e6f821-5f53-1eb0-eba1-16554b3c0dc8.htm); [TableCellStyle Properties](https://www.revitapidocs.com/2019/4a6d51fb-5250-49ea-b7b2-ee84f7f62718.htm); [GetCellText Method, 2025](https://www.revitapidocs.com/2025/c3459397-26e5-0784-e247-6b5d27503bb7.htm)

---

### 3. `ScheduleField` members + type-vs-instance classification — **CONFIRMED (members) / FEASIBLE-WITH-CAVEATS (classification)**

Confirmed on `ScheduleField` (2025): `ParameterId`, `FieldType`, `IsCalculatedField`, `IsCombinedParameterField`, `IsHidden`, `HasSchedulableField`. **Correction to the plan:** the plan (§3, §6) refers to `Parameter.GetUnitTypeId()`/`Definition.GetDataType()` for the spec; the cleaner field-level call is **`ScheduleField.GetSpecTypeId()`** (returns the spec `ForgeTypeId`), with `TableSectionData.GetCellSpec(row,col)` as a per-cell alternative. Use those for the `UnitFormatUtils` round-trip rather than fishing the spec off a sample parameter.

**Type-vs-instance classification is the soft spot.** The Revit API has **no direct "is this field type-bound or instance-bound" flag.** `ParameterId` tells you *which* parameter, not its binding. For built-in parameters the binding is fixed/known; for project & shared parameters you must inspect the binding — and the plan's stated "probe a sample element's instance vs type `Parameter` for that id" is in fact the accepted Building Coder technique (check `element.get_Parameter(...)` vs `element.GetTypeId() → type.get_Parameter(...)`). It works but has edge cases: empty schedules (no sample element), fields that are parameters of *related* elements (e.g. the room a door belongs to — the doc explicitly warns these exist), and shared params that can be type in one family and instance in another. **Mitigation:** classify per (field, sampled element) and, when ambiguous or no sample exists, mark the column **non-writable** and report rather than guess. This aligns with SPEC's "never guess" stance.

> Sources: [ScheduleField Class, 2025](https://www.revitapidocs.com/2025/3d6b0eb5-ed36-278d-a5df-38b6d600e876.htm); [The Building Coder — Determining whether a parameter is type or instance bound](https://jeremytammik.github.io/tbc/a/1064_param_type_or_inst.htm)

---

### 4. Writing type parameters (incl. shared/project) + all-instances side effect — **CONFIRMED feasible, with documented gotchas**

Setting a parameter on an `ElementType` via `Parameter.Set(...)` inside a `Transaction` is standard and works for project and shared params. The all-instances side effect is real and expected: writing a type parameter changes the value for **every instance of that type** — which is exactly why the plan's per-type conflict grouping (§5) is the correct safety design. Gotchas to bake in:

- **`Parameter.Set` returns a `bool` and can silently fail** (e.g. wrong `StorageType`, read-only, or a value Revit rejects) — it does **not** throw on a no-op. The plan must check the return value of every `Set` and report failures (the §5 "collect failures" line covers this — make it non-optional, and verify the value actually changed for post-write confirmation, which the SPEC's Claude post-import check also wants).
- **Always check `Parameter.IsReadOnly`** before writing (some type params are computed/read-only).
- Resolve the type via `element.GetTypeId()` → `doc.GetElement(typeId)` → `get_Parameter`. The schedule field's `ParameterId` is the same id on the type.
- Group-write **once per (typeId, paramId)** — the plan does this. Good.

> Sources: [The Building Coder — type vs instance bound](https://jeremytammik.github.io/tbc/a/1064_param_type_or_inst.htm); [LearnRevitAPI — Parameter basics (Set returns bool, type via ElementType)](https://www.learnrevitapi.com/newsletter/revit-api-parameter-basics-for-beginners)

---

### 5. `UnitFormatUtils.TryParse` signature (2025/2027) — **CONFIRMED feasible**

The modern signature is confirmed (documented identically 2022→2026):
`bool TryParse(Units units, ForgeTypeId specTypeId, string stringToParse, ValueParsingOptions options, out double value, out string message)` — plus a shorter overload without options/message. Get `units` from `doc.GetUnits()`, get `specTypeId` from `ScheduleField.GetSpecTypeId()` / `GetCellSpec`. This is the **post-2021 ForgeTypeId** world; the old `UnitType`/`DisplayUnitType` enums are gone in 2025/2027, so the plan is correctly on the ForgeTypeId path — no pre-2021 branching needed since both targets are post-2021.

**Caveats / things it can't do:**
- `TryParse` **throws `ArgumentException`** if `specTypeId` is not a measurable spec (`UnitUtils.IsMeasurableSpec`) — so it's only for numeric/unit-bearing fields. **Guard with `IsMeasurableSpec` first**; for string/integer/ElementId-valued params do **not** call it (parse directly / resolve by name). The plan's "direct for strings/ints" is right but must be gated by an explicit storage-type/spec check, not by catching exceptions.
- Returns `false` (skip + report, per SPEC) for unparseable text — good.
- Currency, some unitless and "general"/text-format fields, and combined/calculated fields won't round-trip — already marked non-writable in the plan.

> Sources: [UnitFormatUtils.TryParse (Units, ForgeTypeId, String, ValueParsingOptions, Double, String)](https://www.revitapidocs.com/2023/9b5d2bb7-e30e-3a9b-e6f9-5b5a52db286d.htm) (page lists 2024/2025/2026 as current); [ScheduleField.GetSpecTypeId, 2025](https://www.revitapidocs.com/2025/dbd738d0-9b8b-4792-34a9-5b64a1063083.htm)

---

### 6. NPOI on .NET 8 inside Revit's AssemblyLoadContext — **FEASIBLE WITH CAVEATS (real dependency-conflict risk)**

NPOI works on .NET 8 and handles both `.xlsx` (`XSSFWorkbook`) and `.xls` (`HSSFWorkbook`), so functionally it's a sound choice. **But** Revit 2025/2026 add-ins have a **well-documented assembly-version-conflict class of bug**, especially around **`System.Drawing.Common`** and other framework assemblies that Revit preloads at specific versions; NPOI pulls in `System.Drawing.Common` (and `SixLabors`-style imaging in some versions) and can trigger "Could not load file or assembly System.Drawing.Common, Version=X" at runtime. This is the single biggest *integration* risk after the two architecture issues.

**Mitigations:**
- Pin NPOI and its transitive deps explicitly; deploy them next to the add-in DLL; test the actual load inside Revit early (milestone 1), not at the end.
- On **Revit 2027** this gets *better and different*: 2027's new **add-in isolation** (`PublicAssemblies`, `Dependencies`, `UseAllContextsForDependencyResolution` manifest settings, per-add-in load context) is specifically designed to prevent these conflicts — but it means your **2027 manifest is not the same file as your 2025 manifest**, reinforcing item 8.
- `.xls` is a legitimate weak format: confirmed **4,000 cell-style limit** (vs ~64,000 for `.xlsx`) and ~65,536 rows. The plan's style-caching is therefore **mandatory** for `.xls`, not optional. For large/heavily-styled schedules, `.xls` may simply be infeasible — surface a clear "too many styles for .xls, use .xlsx" error rather than letting NPOI throw.
- Sound choice overall; no compelling reason to switch libraries. (ClosedXML, already dropped, is `.xlsx`-only and also drags `System.Drawing`-ish deps; NPOI is the better pick for needing `.xls`.)

> Sources: [Autodesk forum — Revit 2025 could not load System.Drawing.Common](https://forums.autodesk.com/t5/revit-api-forum/revit-2025-encountered-a-could-not-load-file-or-assembly-system/td-p/13275557); [Apache POI — 4000 cell-style limit for HSSF/.xls](https://poi.apache.org/components/spreadsheet/quick-guide.html); [Revit 2027 SDK — add-in isolation & dependency management](https://blog.autodesk.io/revit-2027-sdk-net-10-api-changes-and-additions/)

---

### 7. Ribbon PushButton/panel on the built-in Add-Ins tab — **CONFIRMED feasible**

`UIControlledApplication.CreateRibbonPanel(Tab.AddIns, "Schedule Tools")` is the documented, supported way to put a panel on the built-in Add-Ins tab; `AddItem(new PushButtonData(...))` with 16/32px images and tooltip is standard. No change across 2025/2027 here.

> Sources: [Autodesk forum — add a custom panel to a built-in tab (Tab.AddIns)](https://forums.autodesk.com/t5/revit-api-forum/add-a-new-custom-ribbon-panel-to-a-revit-built-in-tab/td-p/5538772); [Ribbon Panels and Controls — Revit API Dev Guide](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Add_In_Integration/)

---

### 8. Multi-targeting one project to 2025 **and** 2027 — **LIKELY INFEASIBLE as written; FEASIBLE with re-architecture**

This is the second serious problem. The plan/SPEC/BRIEF premise "both .NET 8, one `net8.0-windows` TFM, build configs `R25`/`R27`" is **factually wrong for 2027**:

- **Revit 2027 runs on .NET 10**, not .NET 8. A single `net8.0-windows` TFM cannot target both. You need **multi-TFM** (`<TargetFrameworks>net8.0-windows;net10.0-windows</TargetFrameworks>`) with per-TFM `RevitAPI`/`RevitAPIUI` HintPaths and `#if REVIT2027` where needed.
- **Add-in folder moved:** 2027 all-users add-ins live under `C:\Program Files\Autodesk\Revit\Addins\2027\`, **not** `ProgramData`. The plan's deployment section points both at `%ProgramData%\…\Addins\<ver>\` — correct for 2025, **wrong for 2027** (use per-user `%AppData%\Autodesk\Revit\Addins\2027\` to avoid the Program Files privilege/UAC issue, which is the cleaner path anyway).
- **Manifest semantics differ:** 2027 introduces `<manifestsettings>` (contexts, `PublicAssemblies`, dependency resolution). The "two near-identical manifests" assumption underestimates this; the 2027 manifest likely needs the isolation settings to make NPOI's deps resolve cleanly (ties to item 6).
- **API breaking changes 2025→2027:** the areas *this plan touches* (ViewSchedule/TableData, ScheduleField, UnitFormatUtils, ribbon, Parameter) appear **stable** — none of the deprecations called out for 2027 (AXM/FormIt, Mechanical.Zone, legacy rebar, some EnergyDataSettings) hit this add-in. The forced branching is from **the runtime/manifest/deploy layer (.NET 8 vs 10, folders, manifest), not the schedule API.** That's good news: business logic can be shared; only project plumbing needs `#if`/multi-TFM.

> **Verdict:** Achievable, but the build-system section must be rewritten. This also means the very first sentence of SPEC/BRIEF ("both .NET 8") is incorrect and should be fixed so downstream decisions aren't built on it.
> Sources: [Revit 2027 SDK: .NET 10 Migration & API Changes (Autodesk Developer Blog)](https://blog.autodesk.io/revit-2027-sdk-net-10-api-changes-and-additions/); [Migrating Revit to .NET 10 — What's New in 2027 (Autodesk Help)](https://help.autodesk.com/view/RVT/2027/ENU/?guid=GUID-8D7A4715-EAF8-4BD1-BE78-061F900D0BCE)

---

### 9. WPF/WinForms modal dialog owned by the Revit main window under .NET 8 — **CONFIRMED feasible**

Standard and well-trodden: `new WindowInteropHelper(window){ Owner = uiapp.MainWindowHandle }; window.ShowDialog();`. `UIApplication.MainWindowHandle` exists since 2019 and is the correct handle (do **not** use `Process.MainWindowHandle`). Doing all model reads on the command's API context (as the plan states) and keeping the dialog **modal** avoids the external-events complexity that modeless would force. One note: if you later want a non-blocking "stage → switch to Claude → return → Finalize" flow, that may push you toward modeless + `ExternalEvent`, which is materially harder — keep the apply step modal.

> Sources: [Revit Window Handle and parenting an add-in form (The Building Coder)](http://jeremytammik.github.io/tbc/a/1702_window_handle.html); [Autodesk forum — retaining modal state for a WPF window](https://forums.autodesk.com/t5/revit-api-forum/retaining-modal-state-for-wpf-window-after-using-a-revit-dialog/td-p/12906061)

---

### 10. MCP bridge localhost port ping for Claude detection — **FEASIBLE WITH CAVEATS**

A short, async TCP/HTTP health-check to a configured localhost port is reasonable and safe **if** it never runs synchronously on the UI thread. The risk is entirely implementation: a blocking `TcpClient.Connect` with a default timeout will freeze Revit's UI if nothing is listening. Use an **async connect with an explicit short timeout (e.g. 200–500 ms)** and run it off the UI thread, updating the checkbox via the dispatcher. Caveats:
- **Port discovery "during build from the configured MCP setup"** is brittle — the bridge port can vary per machine/config. Make it a **persisted, user-editable setting** with a sensible default and a Refresh button (SPEC already implies this; lock it in).
- A successful TCP connect proves *something* is listening, not that it's the Revit MCP bridge — fine for an enable/grey-out hint (SPEC correctly treats detection as informational only). Don't gate any correctness on it.
- This is a detection convenience, not a dependency — low risk. Acceptable.

(No authoritative public doc pins the bridge's port; treating it as configurable is the right call. **Flagging as unverified that any fixed/known port exists.**)

---

## Other viability risks the plan missed or underweights

- **R1 — "both .NET 8" is a wrong premise in all three docs.** Already covered (items 8, 6). It's listed here too because it's a *requirements-level* error, not just a plan detail: SPEC §1 and BRIEF state it as locked fact. Fix the source documents.
- **R2 — `cowork_meta` durability when the user edits in Excel.** The plan anchors rows by storing `excelRow → uniqueId` in a hidden sheet. If the user **inserts/deletes/sorts rows** in Excel (very likely — it's the whole point), the stored `excelRow` indices desync from the data. Anchoring by absolute row number is fragile. **Mitigation:** also write the `UniqueId` into a hidden *column* on each data sheet (not just a side table keyed by row index), so the anchor travels with the row even if rows move. The JSON-by-row-index design is the weakest part of the metadata model.
- **R3 — Reading styles is expensive at scale.** `GetTableCellStyle`/`GetCellText` per cell on a large multi-schedule export can be slow; combined with the `.xls` 4,000-style cap and `.xlsx` ~64,000-style cap, you must aggressively cache/intern styles. Plan mentions caching; treat it as mandatory and add a cell-count guard.
- **R4 — Empty / filtered-to-zero schedules** break the "sample an element to classify binding" step (item 3) and the working-copy ID read (item 1). Handle the no-sample case explicitly (mark columns non-writable, warn).
- **R5 — Modifying a working copy of the schedule** (the recommended row-anchor fix) requires a `Transaction` and then a clean rollback/delete of the temp field or temp view, all while the user is mid-export. Make sure this happens inside a transaction that is **rolled back** (or a `TransactionGroup` you discard) so the user's real schedule is never mutated. This is the main implementation hazard of the recommended fix.
- **R6 — Shared-parameter writes may require the parameter to be bound** in the target model. If a column maps to a shared param that exists in the source model but isn't bound in a *different* import-target model (SPEC allows cross-model import with a warning), the write will fail. Report, don't crash.
- **R7 — 2027 testability now.** 2027 is brand-new (SDK April 2026). You likely can't fully validate the 2027 build until you have it installed. Build/keep the 2027 path behind multi-TFM but treat 2027 verification as a **separate, later milestone** and don't let it block the 2025 deliverable.

---

## (c) Top risks, ranked, with mitigations

| # | Risk | Severity | Mitigation / plan change |
|---|------|----------|--------------------------|
| 1 | **Row→element correlation** has no direct API; the plan's primary (re-sort collector) approach silently mis-anchors. | **Critical** | Make the **hidden-ID-field-on-a-working-copy** approach the *primary* design. Read the key column via `GetCellText`. Do it inside a rolled-back transaction so the real schedule is untouched. Prove it in milestone 1–2. |
| 2 | **"Both .NET 8" is false — Revit 2027 = .NET 10**, new manifest/isolation, new add-in folder. | **Critical** | Rewrite build section: multi-TFM `net8.0-windows;net10.0-windows`, per-TFM Revit refs, `#if REVIT2027`, 2027 manifest with isolation settings, per-user Addins folder. Fix SPEC §1 / BRIEF. |
| 3 | **NPOI `System.Drawing.Common` / assembly-version conflicts** inside Revit's load context. | **High** | Pin & co-deploy deps; test load in Revit at milestone 1; use 2027 `PublicAssemblies`/isolation; enforce style-caching; hard-fail `.xls` over 4,000 styles with a clear message. |
| 4 | **Type-vs-instance binding** has no direct flag; sampling fails on empty/related-element fields. | **Medium-High** | Classify per (field, sampled element); when ambiguous or no sample → mark non-writable + report. Never guess (matches SPEC). |
| 5 | **`cowork_meta` row anchors keyed by Excel row index** desync if the user moves rows. | **Medium-High** | Store `UniqueId` as a hidden *column* in each data sheet (anchor travels with the row), not only in a row-indexed JSON table. |
| 6 | **Silent `Parameter.Set` failures** (returns bool, no throw). | **Medium** | Check every return value; re-read to confirm; surface in summary and run-log. |
| 7 | **`UnitFormatUtils.TryParse` throws on non-measurable specs.** | **Medium** | Gate with `UnitUtils.IsMeasurableSpec`; only call for unit-bearing doubles; direct-parse strings/ints. Use `ScheduleField.GetSpecTypeId()` for the spec. |
| 8 | **MCP probe blocking the UI thread.** | **Low-Medium** | Async connect, 200–500 ms timeout, off-UI-thread, dispatcher update; port = persisted setting + Refresh. |

---

## (d) Recommended changes to build order

The current order defers the two make-or-break items (row correlation → milestone 4; full 2027 build implied throughout but its .NET 10 reality unaddressed). Reorder so the riskiest, foundation-invalidating items are proven first:

1. **Correct the premise + prove the toolchain.** Fix SPEC/BRIEF ".NET 8 for 2027." Stand up the **multi-TFM** project (net8/net10), ribbon button + empty dialog, building for **2025 (.NET 8)** and **2027 (.NET 10)**. Confirm NPOI **loads inside Revit 2025** with a trivial workbook write (smoke-test the `System.Drawing.Common` risk now).
2. **Spike the row→element anchor (Risk #1) before anything else.** Working-copy hidden-ID field → `GetCellText` read → map each body row to `UniqueId`; verify against a sorted+grouped+itemized real schedule. If this can't be made reliable, the round-trip scope must be cut — find out on day 2, not at milestone 9.
3. Export: enumerate + render text → `.xlsx` (no styles), eyeball.
4. Export: styles, merges, hidden cols, widths (use the **InPixels** width/height APIs).
5. `cowork_meta` (with **UniqueId as a hidden column** anchor) + field→parameter map + binding classification.
6. CSV + `.xls` (enforce style cap).
7. Import: load, auto-match, change set, preview.
8. Import: `UnitFormatUtils` parse (guarded by `IsMeasurableSpec`), type-param conflict check, transaction write **with per-Set verification**, summary.
9. run-log + Claude-assist checkbox + **async** bridge probe + staging/finalize.
10. End-to-end clean round-trip (export → no-edit import → zero changes) on **2025**.
11. **Separate milestone:** validate the **2027** build once 2027 is installable; resolve any .NET 10 / manifest-isolation issues.

---

## Bottom line

The plan is **fundamentally sound in concept and mostly correct on the Revit schedule API** — items 2, 4, 5, 7, 9 are confirmed; items 3, 6, 10 are fine with stated caveats. It is **not buildable exactly as written** because (a) the 2027 = .NET 8 premise is false and forces a real multi-TFM/manifest/deploy re-architecture, and (b) the highest-risk feature (row anchoring) leans on the unreliable approach and is scheduled too late. Fix those two and adopt the metadata-anchor and validation-guard mitigations, and this is a buildable add-in with ~75% confidence — the residual uncertainty being mostly the untested 2027 runtime and the NPOI-in-Revit dependency behavior.

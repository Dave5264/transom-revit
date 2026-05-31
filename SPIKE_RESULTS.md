# Live Spike Results — Revit 2025

**Date:** 2026-05-31 · **Environment:** Revit 2025, live model driven via the pyRevit/the reference add-in MCP
(IronPython `execute_revit_code`). **Fixture:** `ScheduleTest` — 6 walls (M1–M6) of two types, in a wall
schedule grouped by Type + sorted by Length descending + itemized, so **display order ≠ collector order**.

These spikes empirically retire the make-or-break risks before the C# is written. Every Revit-API behavior
below is runtime-identical whether called from IronPython or Transom's C#.

---

## 1. Row→element anchoring — Approach A PROVEN, Approach B REJECTED  *(Critical)*

| | Result |
|---|---|
| Display order (GetCellText element rows) | `M3, M5, M1, M2, M6, M4` |
| Collector order (`FilteredElementCollector(doc, vs.Id)`) | `M1, M2, M3, M4, M5, M6` |

**Approach B (enumeration) mis-anchors on every row** — rejected.

**Approach A (rolled-back UID injection) works and is non-destructive:** inside a `Transaction`, stamp each
element's `UniqueId` into a transient field → `AddField` → **`doc.Regenerate()` without committing** (the
table immediately reflects the new column + values) → read visible cells + the UID column via `GetCellText`
in display order → **`RollBack()`**. Post-rollback verification: field count `4→5→4` and all stamped params
restored — **the model is never persistently mutated.** No C# re-implementation of sort/group/filter needed.

## 2. Display fidelity via `GetCellText` — CONFIRMED  *(High)*

The Body section renders exactly as Revit shows: column-header row, blank separator, group-header rows
(one per type), and element rows — with formatted values (`30' - 0"`). Group/blank rows carry no UID, so
element rows are cleanly classified. This is the source of truth for the visible sheet (additive hybrid).

## 3. Styling / merge / sizing read — CONFIRMED populated  *(was an audit caveat)*

- `GetTableCellStyle` ✓ — `FontName` (Trebuchet MS for data, **Arial** for group headers), `TextSize` 9.0,
  bold/italic/underline, `FontHorizontalAlignment`/`FontVerticalAlignment` (headers Center/Bottom, data
  Left/Top, group Center/Middle), `TextColor`/`BackgroundColor` as RGB, `BorderTopLineStyle` as an ElementId.
- `GetMergedCell` ✓ — group-header row returns one region `Top2 Bottom2 Left0 Right3` (spans all columns);
  de-dupe by bounds → genuine merge regions.
- `GetColumnWidthInPixels` / `GetRowHeightInPixels` ✓ — 96px / 16px (use pixels, not the feet variants).
- `GetCellType` ✓ — `Text` (headers) vs `Parameter` / `ParameterText` (data) → classify/skip cleanly.

## 4. Type-vs-instance classification — CONFIRMED signal

`ScheduleField.FieldType`: `Type Mark` → **`ElementType`**, `Mark`/`Length` → `Instance`. Direct per-field
signal (still confirm shared-param edge cases per-row at write time).

## 5. Unit round-trip + measurability gating — CONFIRMED  *(High)*

- `Length`: spec `autodesk.spec.aec:length-2.0.1`, `IsMeasurableSpec=True`.
  `UnitFormatUtils.TryParse("25' - 0\"")` → `(True, 25.0)`; `TryParse("30' - 0\"")` → `(True, 30.0)`.
- `TryParse("not a number")` → `(False, 0.0)` → skip + report (never guessed).
- `Type Mark` / `Mark`: empty spec, `IsMeasurableSpec=False` → parse as string, **do not call `TryParse`**.

---

## Net

The 2025 round-trip is de-risked end-to-end: anchoring, display fidelity, styling read, type/instance
classification, and unit parsing are all empirically confirmed on a real schedule. **Only remaining unproven
item: NPOI loading inside Transom's add-in load context** (deferred — it ships with, and is validated by, the
first real export slice, since Transom's DLL is locked while Revit runs and can only redeploy on restart).
The 2027 runtime remains a separate later milestone.

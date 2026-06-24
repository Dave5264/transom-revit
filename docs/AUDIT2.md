# Second-Pass Independent Audit — Transom (post-architecture-decision)

**Auditor role:** Senior Revit API / .NET desktop engineer (independent, skeptical, fresh context)
**Date:** 2026-05-31
**Scope:** Find holes the first audit (`AUDIT.md`) missed, especially new ones introduced by the
post-audit decisions (hybrid export architecture, Nice3point scaffold, NPOI, multi-TFM, Transom name,
MCP-bridge probe). Do not re-litigate the first audit.

> **Outcome:** the original "enumeration drives the rows" hybrid was found internally contradictory.
> The corrected architecture (below, **C1/C2 fix**) was adopted: `GetCellText`/`GetTableData` renders the
> visible sheet and owns row order; element-side work only fills the hidden anchor + writable flags;
> the C# re-implementation of sort/group/filter is dropped; the anchor mechanism is spiked in milestone 1–2.

---

## Critical

### C1. Display fidelity is structurally unachievable from element Parameters alone
Reading writable values "from the element's Parameters directly" cannot reproduce what the schedule
*displays* for: calculated/formula fields, combined-parameter fields, percentage fields, count fields,
grand-total/subtotal rows, and any field with schedule-level formatting overrides (precision, unit symbol
suppression, prefix/suffix). `Parameter.AsValueString()` uses *document* unit settings, but a schedule
column can override units/precision/rounding in the Formatting tab — so even plain unit-bearing instance
params can differ from the cell. Breaks BRIEF #1 ("looks like the Revit schedule") and SPEC §2.
**Fix (adopted):** use BOTH sources per cell. `GetCellText` is the *display* value written to the visible
cell (full fidelity). The element-Parameters read is ONLY for the hidden round-trip column, only for
fields classified writable. The Parameters value never drives the visible cell. The hybrid is **additive**,
not a replacement — keep the full `GetTableData`/`GetCellText` render pipeline.

### C2. C#-reimplemented sort/group/filter drifts from Revit's display → mis-anchored writes
Faithfully bit-matching Revit's multi-level sort (mixed asc/desc; some keys sorted on display string,
some on raw value), grouping headers/footers, blank-line separators, grand totals, the full set of
`ScheduleFilterType` operators (HasValue/HasNoValue/BeginsWith/Contains/GreaterOrEqual…) with Revit's own
empty/unset and case coercion, plus undocumented stable-sort tiebreaks — is a large correctness surface.
Any drift shifts every subsequent anchor by one row.
**Fix (adopted):** do NOT align the visible table to a C# enumeration. Render visible cells purely from
`GetCellText` (row-faithful by construction) and attach UniqueIds to body rows via the anchor mechanism.
**Drop the C# re-implementation of sort/group/filter entirely** — unnecessary once the visible table is
Revit-faithful. *(Central decision the original hybrid left contradictory: who owns row order — resolved
in favour of `GetCellText`.)*

---

## High

### H1. "Last row editable + Excel formula mirror" (BIM One trick) breaks the round-trip reader
Failure modes in our round-trip model: NPOI reads a formula cell's **cached** value (stale/blank unless
`FormulaEvaluator` is run); user re-sort/filter in Excel breaks relative refs and points at the wrong
type's master cell (and the per-type conflict check then sees false "agreement"); assumes contiguous
grouping by type, which fails when type is not the primary sort group.
**Fix (adopted):** for type-param columns, **write the same literal value to every instance row** and rely
on per-type conflict detection. No formulas.

### H2. UniqueId hidden-column anchor not durable against Excel column operations
Survives row moves but not column insert/delete/sort, and a user may delete the hidden column; `cowork_meta`
keyed by `excelRow` desyncs on any row insert.
**Fix (adopted):** locate the anchor column **by a header sentinel** (magic cell value), not by index;
re-derive row→anchor from the current sheet; treat `cowork_meta.rows[].excelRow` as advisory. If the anchor
column is missing/short → **reject import** with a clear message rather than mis-write.

### H3. Multi-category, key, embedded, related-element, material-takeoff, linked schedules
`FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType()` is insufficient for: **key
schedules** (rows are key elements; writable params live on the key); **embedded schedules** (collector
misses embedded row blocks → anchor count ≠ display row count); **related-element fields** (the field's
param lives on a *different* element than the row's element — writing via the row element is wrong);
**multi-category** (mixed categories; binding/grouping must be category-aware); **material takeoff**
(rows are materials within elements, not elements); **linked-model elements** (`doc.GetElement(uniqueId)`
returns nothing — they live in the link doc, not writable from host).
**Fix (adopted):** detect schedule kind up front (`IsKeySchedule`, `IsMaterialTakeoff`, embedded present,
multi-category, linked elements). For kinds enumeration can't faithfully anchor, **disable round-trip
(export display-only)** and say so in the warn-and-ask.

### H4. MCP bridge port is an unverified assumption; "the bridge" is ambiguous
Community/pyRevit revit-mcp (write-capable, exposes `get_revit_status`) uses non-fixed ports (pyRevit Routes
~48884+, or a Python MCP on 127.0.0.1:8000) that vary per instance. Revit 2027's built-in MCP server is
"coming soon," read-oriented, with no published port/health spec. These are different processes.
**Fix (adopted):** port is a **user-editable persisted setting** (default 48884) + Refresh; never
build-discovered. The **write-capable community server** is the bridge (the read-only 2027 built-in can't do
§5 visual flagging/write-back QA). All correctness stays independent of the probe.

---

## Medium

### M1. Nice3point IS 2027/.NET 10 ready — but on *preview* Revit API packages
`Nice3point.Revit.Api.RevitAPI` for 2027 is `2027.0.0-preview.1` (Jan 2026); API surface may shift before
RTM. The template's "assembly isolation" is the standard per-add-in ALC / 2026 manifest dedup; it does not
by itself resolve NPOI's `System.Drawing.Common`. A live forum thread reports the 2026 ManifestSettings
dedup mechanism appears missing/changed in 2027 — the isolation feature we lean on is in flux.
**Fix (adopted):** pin a known-good preview; expect to re-verify at 2027 RTM; add explicit `PublicAssemblies`
/ `UseAllContextsForDependencyResolution` entries for NPOI + System.Drawing.Common in the 2027 manifest;
smoke-test the load.

### M2. NPOI System.Drawing.Common on .NET 10 / Windows
`System.Drawing.Common` 6+ is Windows-only; the version Revit preloads may differ from NPOI's transitive ref.
Confirm the pinned NPOI version's exact transitive graph (some 4.x lines pull SixLabors).
**Fix (adopted):** lock NPOI version; `dotnet list package --include-transitive`; co-deploy the exact set;
smoke-test workbook write inside Revit 2025 AND 2027 at milestone 1.

### M3. Phases / design options / worksets
Schedule phase affects which rows/values show; design-option elements appear only when active; writing a
param on an element in a non-active design option may affect the wrong variant; cross-model import amplifies
this.
**Fix (adopted):** record phase/active-design-option in `cowork_meta`; warn on mismatch at import; skip writes
to elements in a non-editable workset (report).

### M4. "Itemize every instance" OFF + multi-value collapsing has no anchor
A collapsed row represents N elements → no single UniqueId.
**Fix (adopted):** export path renders display rows from `GetCellText` independently of any anchor, so
non-itemized still produces a faithful read-only workbook; round-trip disabled (per H3).

### M5. Type-binding classification must be per-row, not per-column
A shared param bound as **type in one family and instance in another** within a multi-category/multi-family
column means a single column has mixed binding per row; the per-(typeId,paramId) grouping assumes uniform.
**Fix (adopted):** classify binding **per (row element, field)**; store at row granularity in meta; handle
mixed columns per-row.

---

## Low

- **L1.** Reading both `GetCellText` and per-element Parameters doubles read cost — cache aggressively; add a
  cell/element-count guard.
- **L2.** Where a field is writable+unit-bearing, asserting the hidden value's re-formatted form == visible
  `GetCellText` is a useful fidelity-bug test.

---

## Decisions the plan must make (now resolved in IMPLEMENTATION_PLAN.md)
1. Row order owner = **`GetCellText`** (drop C# sort/group/filter). *(C1/C2)*
2. Anchor strategy = **spike both** (working-copy hidden-ID-field read vs enumeration-match) in milestone 1–2, pick winner.
3. Type-param = **literal value per row** + conflict check (not formula mirror). *(H1)*
4. Anchor located by **header sentinel**; `excelRow` advisory; **reject** on missing anchor. *(H2)*
5. Round-trippable kinds gated; **display-only** for material-takeoff/embedded/linked/non-itemized. *(H3/M4)*
6. Bridge = **write-capable community revit-mcp**; port = **user setting (default 48884)**. *(H4)*
7. 2027 manifest = explicit `PublicAssemblies` for NPOI + System.Drawing.Common; preview API, re-verify at RTM. *(M1/M2)*
8. Binding classified **per-row**. *(M5)*

## Testing gaps
- Build a test model with one of each problem schedule kind (key, embedded, multi-category, material takeoff,
  linked, non-itemized, phased, design-option) — milestone-9 round-trip currently only covers happy itemized.
- Assert the core hybrid invariant: visible `GetCellText` vs hidden round-trip value agree where they should.
- 2027 path unverifiable until 2027 install + built-in MCP spec exist — keep a separate milestone; don't block
  the 2025 deliverable.

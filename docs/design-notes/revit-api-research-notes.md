# Revit API research notes (consolidated)

Consolidated 2026-07-13 from the retired `research-kb/` folder (June 2026 research campaign; full
topic files archived locally outside the repo). Only the findings that still underpin **shipped
functionality** are kept here. Code line references were accurate as of mid-June 2026 — treat them
as pointers, not gospel. Items marked VERIFIED-LIVE were confirmed on a real model during the
2026-06-11/12 test campaign.

## Parameter writes

- **`Parameter.Set` can return true without the value changing** (coerce/clamp/no-op on derived
  params). The re-read verification (`VerifyWrite`/`VerifyParsed`) is the required mitigation —
  keep it on every write path.
- Read-only writes can **throw `InvalidOperationException`** (not just return false); write sites
  rely on the `IsReadOnly` pre-check. Internal units are decimal feet; parse/format user text via
  `UnitFormatUtils.TryParse`/`Format` against the document's `Units`.
- String comparison rule: trim before comparing (Revit trims on store); doubles to ~1e-6/1e-9.
- Editability sampling caveat: column-level greying that samples one element can be wrong in
  mixed-family schedules (`IsReadOnly` is per-element); the apply path re-checks per element, so
  mis-greying is cosmetic only.

## Transactions & batching

- **Batching is correct**: one transaction with N `Parameter.Set` calls is the Autodesk-recommended
  pattern; regeneration happens once at commit. Trade-off = atomicity: one unresolved
  error-severity failure rolls back ALL writes — `Commit()` returns `RolledBack` **without
  throwing**, so the return value must be checked (VERIFIED-LIVE: full-rollback semantics).
- **Handles die after rollback**: any `Element`/`ScheduleDefinition` touched by a rolled-back
  transaction throws `InvalidObjectException` on reuse — re-fetch everything for post-rollback
  reporting, including after a failed commit.
- Close-without-save fully reverts a committed apply, including Revit-internal deletions
  (VERIFIED-LIVE).

## Failure handling

- **Duplicate Type Mark is a WARNING** (`BuiltInFailures.GeneralFailures.DuplicateValue`), not an
  error: commit proceeds, both types keep the value, one warning line in the apply log
  (VERIFIED-LIVE, 13 elements). By contrast **type RENAME throws `ArgumentException` immediately**
  on a duplicate name — a per-write hazard, not a collectable failure.
- **Auto-resolution is gated FAIL-CLOSED** (mgmt decision, June 2026): the import failure collector
  resolves only the id-allow-list `{CannotKeepJoined}` (default resolution = Unjoin/Detach class,
  benign). Never blind-resolve: unseen failures can default to `DeleteElements`/`UnlockConstraints`
  (destructive). Growth process: observe a new failure id live, verify its default resolution is
  benign, then add it to the list.
- **Revit auto-deletes un-regenerable geometry during apply — unpreventable**; it happens during
  regen BEFORE the failure preprocessor runs (VERIFIED-LIVE: "Delete Splitting Element"). Detection
  = `Application.DocumentChanged` → `GetDeletedElementIds()` scoped by transaction name, minus
  intended deletions → surfaced in the apply report (`ChangeSet.RevitDeletions`). Reporting only,
  never a pipeline gate.
- `DialogBoxShowing` backstop works for stray modal warnings (VERIFIED-LIVE:
  `Dialog_Revit_DocWarnDialog` auto-dismissed).

## Type vs instance

- Type edits propagate instantly to all instances; the preview must report the real instance
  fan-out count. Multi-type (aggregated) schedule rows fan a type edit out to every listed type.
- Shared params can be type-bound in one family and instance-bound in another — binding must be
  resolved per row against the LIVE model (schedule-field classification first, live param
  presence fallback), never trusted from export-time metadata alone.

## Schedules API

- **There is no row→element API** — that's why the hidden UniqueId anchor column exists at all.
  `GetCellText` renders the visible sheet (calculated/combined/subtotal fields included) and is the
  only faithful source of display values/order.
- Editing a **sort/group/filter key** genuinely reorders/merges/hides rows at the post-commit
  regen — the yellow "drift/advisory" mechanism is justified; post-apply comparisons must match by
  uid, never row position.

## Worksharing

- Edits auto-borrow; a single element **owned by another user = error-severity failure = the whole
  import rolls back**. Dormant risk while single-user; cheap future mitigation =
  `WorksharingUtils.GetCheckoutStatus` pre-flight during preview, freeze contested rows.
- Never Synchronize with Central during a test run (standing rule).

## Groups (why the current design is what it is)

- **No API "Edit Group" mode exists through Revit 2027** — audited R25/R26/R27, no
  version-conditional code needed. Editing a geometry-driving built-in on a group member is only
  possible by driving the UI (Transom UI-Assist / ClickHelper), which is exactly what the
  Claude-assist staging path does.
- **Built-in DATA params (Mark, Comments, Number, Finish) write directly on grouped instances** —
  they vary natively and need no special handling (`GroupMode.None`).
- The API-side "definition-swap dance" was researched exhaustively and **retired**: `ChangeTypeId`
  repoints delete+recreate members, dangling external dimensions/constraints crash un-catchably
  (0xe0434352), group origins drift, and market analysis showed no demand for the niche. The
  research files (dance methods, scatter taxonomy, origin drift, market analysis) live in the
  local archive if it's ever revisited.
- **`SetAllowVaryBetweenGroups` ordering (drives option 2b)**: a freshly-created shared instance
  param defaults vary-OFF; writing divergent values to multi-instance group members then rolls the
  whole transaction back. Required order: commit the binding (Regenerate) → fetch the bound
  **InternalDefinition** (the fresh `ExternalDefinition` has no vary setter) →
  `SetAllowVaryBetweenGroups(true)` → Regenerate → re-read `VariesAcrossGroups` to confirm it
  stuck. This is the BUG-1 fix inside `ApplyNewParam`.

## Combined-parameter fields

- `ScheduleField.GetCombinedParameters()` → ordered `TableCellCombinedParameterData`
  (ParamId/Prefix/Suffix/Separator; default separator "/"). Combined cells are READ-ONLY in Revit
  and there is **no API to distribute a combined string back to components** — only per-component
  `Set`. Safety rule (shipped as the §17 fail-closed path): auto-distribute ONLY when the separator
  provably can't appear in the values, all parts are settable, and the re-assembled tokens
  round-trip; otherwise fall back to the components' own (hidden) columns. The #1 hazard is a
  separator-in-value collision producing a silent wrong write.

## API context & threading

- Model work only in a valid API context (`ExternalEvent` handlers); `ExternalEvent.Raise()` calls
  **coalesce** — N raises can produce one Execute, so handlers must drain queued state, not assume
  1:1. Mid-transaction `doc.Regenerate()` is required before reading back schedule-table state that
  depends on just-made changes.

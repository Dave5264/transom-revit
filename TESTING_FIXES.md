# Transom — testing findings & fixes

Running list from the live testing session (started 2026-06-03). Each item: what / where / repro / proposed fix. Nothing here is applied yet.

## Open fixes

### 1. Type-param edits report a misleading "1 inst" scope (big undercount)
- **Severity:** medium — understates blast radius; risk of unintended mass edits.
- **Where:** `source/Transom/Core/Importer.cs`
  - `ProposedChange.Scope` getter (~L49-60) → renders `type · {InstancesAffected} inst`.
  - `InstancesAffected` for type edits is set to `tc.Cells.Count` (schedule **rows**), in `ResolveTypeGroups` (~L790) and `TypeChange` (~L1246).
- **Repro:** Edit UA **Width** (a *type* param) in the export → Preview shows `type · 1 inst`. But the UA type has **15 instances** in the model (only **9** are in this schedule — it's filtered). Applying changes all 15.
- **Proposed fix:** For type-bound changes, count actual instances of the type — ideally those in the schedule, with a note when the type reaches beyond the schedule's filter (e.g. `type · 9 in sched / 15 in model`). Don't label a schedule-row count as "inst". (Instance-bound edits already count correctly via `BulkInstanceIds.Count` → "all N inst".)

### 2. Type-param length entries skip the unit reformat-confirm; preview shows raw text
- **Severity:** medium — silent unit reinterpretation on type fields.
- **Where:** `source/Transom/Core/Importer.cs`
  - Instance double path (~L308-326) runs `ExcelCorrector.SameFormat(...)` and adds a `Reformat` (confirm) suggestion when the entry isn't in canonical format.
  - The **type** double path in `ResolveTypeGroups` (~L818-827) parses and commits directly with `NewValue = raw cellText` — no SameFormat check, no canonical display, no confirm.
- **Repro:** Enter `1` in UA **Width** (type) → parses to `1'-0"` but Preview shows **New = `1`** (not `1'-0"`) and gives no "interpreted as 1'-0" — confirm?" prompt. The same entry on an *instance* length would prompt.
- **Proposed fix:** In the type path, when parseable: set `NewValue` to the canonical formatted value, and when `!SameFormat(cellText, canonical)` route through the same Reformat-confirm flow the instance path uses (parity between type and instance length handling).

### 4. Group-conflict dialog radios aren't mutually exclusive (can't switch off the default Skip) — FIX APPLIED (pending rebuild + restart)
- **Severity:** high — garbles the resolution choice; found live during testing.
- **Where:** `source/Transom/Views/GroupResolutionDialog.xaml.cs` — `AddOption(...)` puts each `RadioButton` in its own per-option `StackPanel` with no `GroupName`. WPF only auto-groups radios that share a parent, so they're independent toggles: the default-checked **Skip** can't be unchecked, and multiple can be lit at once. (`Apply_Click` returns the *first checked* in list order, so a single pick of option 1/2/3 still "wins" over Skip — but you can't switch back to Skip, and clicking two options lets the earlier one win.)
- **Repro:** Open the dialog → option 5 (Skip) is pre-selected; clicking 1/2/3 lights up but Skip stays lit and won't turn off.
- **Fix (applied in working tree — needs rebuild + Revit restart to verify):** added a shared `GroupName = "GroupResolution"` to every RadioButton in `AddOption`. One group → proper mutual exclusion; Skip can be switched off; exactly one ever checked.

### 6. Apply silently rolls back on Revit *errors* but reports "applied: N" — CRITICAL
- **Severity:** critical — data-integrity/trust: the user is told the import applied when the model didn't change at all.
- **Confirmed live (user verified no undo):** the option-2 round (apply log 21:23, reported "applied: 194") persisted **nothing** — the whole model was verified back at baseline immediately after clicking Apply, with no revert in between.
- **Root cause (confirmed in `Importer.cs`):**
  1. `ApplyFailureCollector.PreprocessFailures` (~L843-855) only **deletes warnings**; **errors are logged but not resolved**, and it returns `FailureProcessingResult.Continue`. Unresolved errors → Revit posts the error dialog (`Dialog_Revit_DocWarnDialog`) → the apply's `DialogBoxShowing` auto-dismiss (`OverrideResult(7)`) cancels it → Revit **rolls back the entire (single) transaction**.
  2. `tx.Commit()` (L916) **ignores the returned `TransactionStatus`**. A rollback returns `RolledBack` without throwing, so the `catch` never runs and the method builds `"Applied {applied} change(s)"` from the in-transaction counter → reports success.
- **Trigger observed:** 14× `[Error] An insert in the main model cannot be hosted by an element in a Design Option` (door resizes interacting with design-option hosts).
- **Fix:** `var st = tx.Commit(); if (st != TransactionStatus.Committed) return "Apply rolled back — 0 changes persisted (N Revit error(s), see log)";`. Plus reconsider error handling: errors can't be auto-deleted like warnings — consider a pre-flight, surfacing "these errors will discard the apply," and not auto-cancelling the error dialog in a way that forces an unreported rollback.

### 7. VerifyWrite flags false "unverified" for values Revit trims (leading/trailing whitespace)
- **Severity:** medium — false negatives in the apply report; would also wrongly drop legit edits whose values have surrounding spaces from the `applied` count.
- **Indicated by:** all 30 "unverified" entries in the option-2 run were space-padded (`' s'`, `'yt '`, `' res'`, `'ewa '`, …). Revit trims leading/trailing whitespace on string params, so `VerifyWrite`'s exact-string compare fails even though the trimmed value did write. (Couldn't confirm in-model — the apply rolled back per #6.)
- **Fix:** in `VerifyWrite`, compare trimmed/normalized strings for string params (or trim the expected value before `Set`). Re-test after #6.

## Needs decision (bug vs. intended)

### 3. Multi-type Type Mark (UJ) exports as a non-round-trippable "group header" row
- **Severity:** medium — values look correct but the row silently can't be imported back, and is visually disguised as a header.
- **Observed:** In the export, **UJ** row is painted header-grey, has a **blank `__transom_uid__`** anchor, is tagged `kind:"groupHeader"` in `cowork_meta`, and is **omitted from the round-trip `baseline`**.
- **Root cause (verified):** Type Mark "UJ" is shared by **2 distinct door types** (18 instances) — the only mark in the schedule mapping to >1 type. Its type-level Head/Jamb differ → schedule shows `<varies>`, and Transom (which anchors each row to one type) can't anchor it.
- **Decision needed:** Is dropping it from the round-trip intended (defensive), or should Transom anchor/flag UJ explicitly (e.g. split per type, or mark it clearly non-editable) rather than masquerading as a header? Likely in the exporter row-classification.

## Enhancements / safeguards

### 5. Warn before applying an edit to a field the schedule filters or sorts/groups on
- **Severity:** low–medium — UX safeguard, not a correctness bug.
- **Context (found live):** Renaming three Type Marks (UE→x, UH→aa, UM→bb) silently removed those rows from "DOOR SCHEDULE - UNIT DOORS - FULL LIST", which filters **Type Mark begins with "U"**. The edits applied correctly; the doors just left the schedule (correct Revit behavior) — but easy to miss, since the rows vanish on the next refresh/re-export.
- **Proposed:** In Preview, detect when a proposed change targets a column the schedule uses in a **filter** or **sort/group**, and flag it (note/icon: "this value drives the schedule's filter/sort — changing it may drop this row from the schedule or reorder it"). Read `ScheduleDefinition.GetFilterCount()/GetFilter()` and `GetSortGroupFieldCount()/GetSortGroupField()`, cross-reference against the edited columns.
- **Where:** build it where change rows / Scope are assembled in `Importer.cs`; surface in the preview grid (`TransomView.xaml` Changes grid) and the diagnostic log.

## Resolved
**2026-06-03 build — implemented (compiles clean), pending live re-test:** #1, #2, #4, #5, #6, #7.
- #2 caveat: the canonical New-value *display* is in (preview now shows `1'-0"`, not `1`); the full reformat-confirm *prompt* for type-length entries (full parity with the instance path) still needs the row/sheet context threaded into the type-resolution pass — a small follow-up.
- #5 surfaces as a yellow cell-diagnostic (shows in the colour-coded report + the `cell diagnostics` count/log), not yet a badge in the preview grid.

**#3 (UJ multi-type row) — IMPLEMENTED (compiles clean), NEEDS a live round-trip test.** Per your call: a Type Mark shared by 2+ types stays **one editable row anchored to ALL its types** — a *type* edit fans out to every type (`Importer.RecordTypeAll`), an *instance* edit bulk-writes their union, and each cell's hint colour is the **worst case** across the aggregated types (unioned sets + ExcelWriter's grey&gt;blue&gt;yellow&gt;green precedence). Spans `ScheduleReader` (detect multi-type groups + `BuildMultiTypeRow`), `ScheduleTable`/`ExcelWriter`/`ExcelReader` (carry `aggregatedTypeUids`), `Importer` (fan-out). **Untested end-to-end** — export the UJ schedule → that row should now anchor + colour worst-case → edit a `<varies>` cell → import should write it to both UJ types.

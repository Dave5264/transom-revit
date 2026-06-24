# Design note — Option-2 new-column heading, and Phase-1 apply atomicity

Two `Importer.cs` topics: (1) the heading of the new column Option-2 creates, and (2) whether one
bad value should roll back an entire apply.

- **Issue 1b — implemented** (`ProposedChange.SourceHeading` + `CleanHeading` + heading set in
  `AddOrReplaceField`).
- **Issue 2 — analysis + recommended approach** for the all-or-nothing Phase-1 transaction.

---

## Issue 1 — Option-2 UX: the new column's heading

### What the code does
- The new shared TYPE param is named `var name = $"{sample.Field} (Transom)";`. `sample.Field` is the
  **parameter/field name** (`ScheduleField.GetName()`, e.g. `Comments`, `WD_FireRating`), not the
  friendlier display heading — `ProposedChange.Field` is populated from `col.FieldName`, and
  `col.FieldName` = `f.GetName()` (`ScheduleReader.cs:168`). The schedule's display heading
  (`col.Header` = `ScheduleField.ColumnHeading`, `ScheduleReader.cs:169`) was **not** carried into
  `ProposedChange`, so it wasn't available at apply time.
- `AddFieldToSchedules` called `def.AddField(sf)` with **no positional argument** and **never set
  `ColumnHeading`**. Revit appends to the END of the field order, and a freshly added field's
  `ColumnHeading` defaults to the field/parameter name. Net result was a new **far-right** column whose
  header read the verbose `"<Field> (Transom)"`.

### Sub-issue 1a — insert next to the source column? → No.
Option-2 is **additive-only by design: it never reorders columns and never edits existing
headings/sort/group/filters**, and re-import identifies the new column by `ParameterId`. The integrity
guarantee is "exactly ONE field appended at the END; every pre-existing field identical in (ordered
name, ColumnHeading, IsHidden, FieldType, ParameterId.Value)" with `GetSortGroupFields()`/`GetFilters()`
byte-identical. `ScheduleDefinition` has no "insert at index" API; repositioning requires reordering
pre-existing fields and risks perturbing sort/group/filter `FieldId`s — i.e. it would break the
additive-only invariant. The far-right append is the safe, intended behavior. (A future product decision
to insert-near-source would require redefining that integrity contract.)

### Sub-issue 1b — cleaner `ColumnHeading` on the NEW field → done.
The additive-only invariant constrains only **pre-existing** fields' `ColumnHeading`. The newly
appended field's heading is unconstrained. `ScheduleField.ColumnHeading` is writable and independent of
`GetName()` (the underlying parameter name stays `"<Field> (Transom)"`, preserving round-trip
identification by `ParameterId`), so the *visible column title* can be given a cleaner value without
touching order or any pre-existing field.

The heading used = the **source column's display heading** when known, else the field name — WITHOUT
the `(Transom)` suffix in the visible title. Two pieces:

**(b1) Carry the source column's display heading into the change** so apply can use it:
```csharp
// Importer.cs — ProposedChange (after `public string Field`)
/// <summary>The source schedule column's DISPLAY heading (ScheduleField.ColumnHeading), when known.
/// Used by option 2 to give the new column a clean visible title instead of the verbose param name.
/// Empty falls back to Field.</summary>
public string SourceHeading { get; set; } = "";
```
`ImportColumn` already carries the display heading as `Header` (`ScheduleReader.cs:169`). Populate
`SourceHeading = col.Header` in the change factories that route to option 2 — `BulkChange` and
`InstanceChange`:
```csharp
// BulkChange(...) initializer:
ElementName = SafeName(typeEl), Field = col.FieldName, SourceHeading = col.Header,
    OldValue = oldDisp, NewValue = newDisp,
// InstanceChange(...) initializer:
Field = col.FieldName, SourceHeading = col.Header, OldValue = oldDisp, NewValue = newDisp,
```
(Built-in grouped edits route through `Mark(BulkChange(...))` / `Mark(InstanceChange(...))`; `Mark`
doesn't clear the field, so `SourceHeading` survives.)

**(b2) Set the new field's `ColumnHeading` after `AddField`** — on *only the field just added*:
```csharp
// new helper near ApplyNewTypeParam:
/// <summary>Visible column title for the new option-2 field: the source column's display heading when we
/// captured it, else the parameter/field name — WITHOUT the "(Transom)" suffix the underlying param carries.</summary>
private static string CleanHeading(ProposedChange sample) =>
    !string.IsNullOrWhiteSpace(sample.SourceHeading) ? sample.SourceHeading : sample.Field;

// AddFieldToSchedules: after AddField, set ColumnHeading on the returned ScheduleFieldId only:
var newFid = def.AddField(sf);                 // AddField returns the new ScheduleFieldId
if (!string.IsNullOrWhiteSpace(heading))
{
    try { def.GetField(newFid).ColumnHeading = heading; } // new field only — pre-existing untouched
    catch { /* heading is cosmetic; never fail the add over it */ }
}
```
Guardrails:
- Use `AddField`'s returned `ScheduleFieldId` so the heading is set on the *added* field only, never a
  pre-existing one (keeps the integrity diff green).
- `ColumnHeading` is cosmetic and may occasionally throw on exotic fields; the inner `try` means a
  heading failure never aborts the (verified) value write or the field add.
- Underlying param NAME stays `"<Field> (Transom)"`, so round-trip / dup-name disambiguation in
  `EnsureSharedTypeParam` and re-import matching by `ParameterId` are unaffected.

---

## Issue 2 — Phase-1 atomic rollback: one error reverts ALL valid edits

### What the code does
`Importer.Apply` wraps **every** non-group, non-dance change in **one** `Transaction` with a
`FailuresPreprocessor` (`ApplyFailureCollector`) that **deletes WARNINGs** but **leaves ERRORs**, and
`fho.SetClearAfterRollback(true)`. The per-change loop stamps each change Applied/Failed/Unverified, but
if any **unresolved error-severity failure** remains at `tx.Commit()`, Revit discards the **entire**
transaction and returns `TransactionStatus.RolledBack` (not Committed) *without throwing*. The code
handles this honestly (re-stamps every Applied/Unverified change to **Failed**, returns "Apply ROLLED
BACK by Revit — 0 of N saved"). So **one bad value that posts an error Revit won't auto-resolve nukes
every other valid edit in the same apply.**

Severity nuance: most bad cells are caught BEFORE the write as `Skipped`/red diagnostics (unparseable,
read-only, param-not-found) and never enter the transaction; `param.Set(...)` returning false is handled
per-change. The atomic-rollback hazard is specifically writes that **Set() accepts but Revit then
rejects with an ERROR-severity Failure at commit** (a value violating a model constraint/formula/
uniqueness, a constraint-driven dimension). Rare but real, and when it hits the blast radius is the
whole apply.

### Architectural precedent for isolation (already in this codebase)
`GroupDanceApplier` opens **one top-level `Transaction` per group TYPE** with the same
`SetClearAfterRollback(true)`, so a failure rolls back **that group only** while others succeed;
`ImportEventHandler` runs the dance AFTER and OUTSIDE the import transaction for exactly this isolation
reason. There is **no `SubTransaction` usage anywhere** in the source. So per-change isolation is
consistent with existing design, just not applied to the Phase-1 direct writes.

### Why naive `SubTransaction` is NOT sufficient
A `SubTransaction` rolls back on **programmatic** rollback / exception, but **failure-severity
resolution is processed by the FailuresPreprocessor at the enclosing Transaction's COMMIT**, not at
`SubTransaction.Commit()`. An error posted by a write isn't surfaced/rolled back at the sub-transaction
boundary — it still poisons the outer commit. The reliable isolation primitive is a **real Transaction
per change (or per safe batch), each committed independently**, mirroring the dance.

### Recommended approach — per-change Transaction isolation
**Option A (correctness):** replace the single outer Transaction with **one Transaction per change**,
each with its own `ApplyFailureCollector` + `SetClearAfterRollback(true)`. A change whose commit rolls
back is stamped `Failed` and the loop continues; valid edits in other transactions persist. The
Option-2 NewTypeParam pass still needs ONE shared transaction (it creates a shared param + binding +
`AddField` across schedules and must be atomic) — run it first in its own transaction.

```csharp
// 1) Option-2 bulk pass — ONE transaction (atomic for the new param/field add).
var newParamChanges = cs.Changes.Where(c => c.Resolution == GroupResolution.NewTypeParam && !c.Frozen).ToList();
if (newParamChanges.Count > 0)
{
    using var ptx = new Transaction(doc, "Transom: import — new type parameter");
    var pcol = new ApplyFailureCollector();
    var pfho = ptx.GetFailureHandlingOptions();
    pfho.SetFailuresPreprocessor(pcol); pfho.SetClearAfterRollback(true);
    ptx.Start(); ptx.SetFailureHandlingOptions(pfho);
    try
    {
        newParamNote = ApplyNewTypeParam(doc, newParamChanges, cs.ImportedScheduleNames, failed);
        if (ptx.Commit() != TransactionStatus.Committed)
        { foreach (var c in newParamChanges) if (c.Outcome is ApplyOutcome.Applied or ApplyOutcome.Unverified) c.Outcome = ApplyOutcome.Failed; }
    }
    catch (Exception ex)
    { if (ptx.GetStatus() == TransactionStatus.Started) ptx.RollBack(); StampFailed(newParamChanges); failed.Add("option 2 — " + ex.Message); }
    revitMessages.AddRange(pcol.Messages);
}

// 2) Direct writes — ONE TRANSACTION PER CHANGE so a single bad value can't revert the rest.
foreach (var ch in cs.Changes)
{
    if (ch.Frozen) continue;
    if (ch.Resolution == GroupResolution.NewTypeParam) continue;   // handled above
    if (ch.Resolution == GroupResolution.GroupDance) continue;     // handled after, by GroupDanceApplier
    if (ch.GroupMode == GroupMode.BuiltinDance) continue;          // staged for Claude-assist

    using var tx = new Transaction(doc, "Transom: import edit");
    var collector = new ApplyFailureCollector();
    var fho = tx.GetFailureHandlingOptions();
    fho.SetFailuresPreprocessor(collector); fho.SetClearAfterRollback(true);
    tx.Start(); tx.SetFailureHandlingOptions(fho);
    try
    {
        bool ok = ApplyOneChange(doc, ch, failed, unverified, ref applied);   // the existing per-change body, factored out
        if (tx.Commit() != TransactionStatus.Committed)
        {
            if (ch.Outcome is ApplyOutcome.Applied or ApplyOutcome.Unverified)
            { ch.Outcome = ApplyOutcome.Failed; applied = Math.Max(0, applied - CountApplied(ch)); }
            failed.Add(Label(ch) + " — rolled back by Revit (constraint/error)");
        }
    }
    catch (Exception ex)
    { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); ch.Outcome = ApplyOutcome.Failed; failed.Add(Label(ch) + " — " + ex.Message); }
    revitMessages.AddRange(collector.Messages);
}
```
`ApplyOneChange(...)` is the current per-change body (bulk-instance branch + single-host branch),
returning whether it stamped Applied; `CountApplied(ch)` reflects how many `applied++` that change
contributed. `EnsureVary`, `SetValue`, `VerifyWrite` stay as-is.

**Option B (perf fast-path):** the common case is "all good." Attempt the current single-transaction
commit first; only if it returns `RolledBack` fall back to the per-change re-apply (Option A) to salvage
the valid edits and isolate the offender(s). Keeps one-commit performance for the 99% path.

### Trade-offs
- **Perf:** per-change transactions ⇒ N commits + N implicit regenerations. For large imports this is
  materially slower than one commit. Option B (fallback-only) or per-*schedule*/per-*batch* transactions
  mitigate.
- **Undo stack:** N transactions ⇒ N undo entries. To keep a single "Transom: import edits" undo, wrap
  the per-change transactions in a **`TransactionGroup`** and `Assimilate()` on success (merges committed
  children into one undo entry while still allowing individual children to have rolled back). Recommended
  alongside Option A.
- **Bulk-instance cells:** under Option A each bulk *cell* is its own transaction (all its instances
  together), so one instance's error rolls back that whole CELL, not the entire apply — the correct
  granularity (a cell is the unit the user sees). Going finer (per instance) is not recommended.
- **Behavioral change:** a partially-bad apply **partially succeeds** instead of fully rolling back.
  Run-results reporting already distinguishes Applied/Failed per change, so the report stays truthful.

**Recommendation:** Option A + `TransactionGroup.Assimilate` for correctness + single-undo, or Option B
if import size makes N regenerations a perf concern. Do NOT rely on `SubTransaction` for failure
isolation (it won't isolate commit-time error failures).

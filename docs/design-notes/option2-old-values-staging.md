# Option-2 old-values: warning-first + Claude-staged cleanup (2026-07-18)

User-directed rework of the option-2a/2b "what happens to the old values" step.

## The defect this fixes

The disposition pass (`Importer.ApplyNewParam`) wrote the OLD parameter **directly on grouped
members**, justified by a comment claiming built-in DATA params "write directly (established since
v1.4.1)". That claim came from a stale bullet in `revit-api-research-notes.md` and is contradicted by
the later live-verified finding (2026-06-19, `ScheduleReader.IsInstanceIdentityBuiltin`): only
Mark / door Number / room Number commit on a direct grouped write; ordinary built-ins (Comments,
Finish…) are Revit-refused — which is **the entire reason the column was yellow and went through
option 2 in the first place**. If the claim were true, option 2 would be redundant. Symptoms: the
apply note could report "old values cleared on N element(s)" while grouped members silently kept
them, or (worst case) a posted failure at commit rolled back the whole import.

## The new flow (all-or-nothing per column — never "just the ungrouped ones")

After a committed 2a/2b conversion (and the extra-schedules checklist):

1. **Claude Assist OFF** → `Option2OldValuesDialog` is a **warning only**: the old data has been left
   in place — Revit doesn't allow editing the parameter on group members outside Edit Group mode, and
   Transom won't change only part of a column. OK/close = done.
2. **Claude Assist ON** → same warning, plus one opt-in button: **"Have Claude update them…"**.
3. **Opting in** reveals the cleanup choice — **Clear** (string columns only) or **Replace** with one
   uniform value; Cancel = leave. The choice applies to the whole column:
   - **API-writable** targets (ungrouped elements, type-hosted params, vary-enabled shared params,
     identity built-ins) are written **directly during apply**, verified-write rules unchanged.
   - **Grouped members** are collected into `ChangeSet.Option2OldValueStaged` and, after apply
     completes, staged to `transom_old_value_edits.json` (same shape/guide as the option-3
     group-edits artifact; `purpose: "option2-old-values"`) for Claude's Edit Group pass.

## Invariants preserved / added

- **Verified-write only** (2026-07-13 hardening): the disposition — direct or staged — never touches
  an element whose new-param copy didn't verify (`dispositionUids`), so an old value that is the only
  copy is never destroyed. §10 divergent-blank instances stay excluded.
- **Coverage gate (new):** an Edit Group write lands on the group *definition* — every instance of
  the group type, including instances outside the imported schedule's filter. A member is staged only
  when its verified count equals the group type's live instance count in the model; otherwise it's
  reported as "left unchanged (not every instance of their group was covered)".
- **Explicit choice:** stage 2 has no "Leave" radio and no default selection — Continue without a
  choice is a validation stop; Cancel/close means leave. Nothing is ever destroyed by a default.
- **Staging cancelled = nothing lost:** unlike option 3 (where un-staged grouped edits are dropped),
  a cancelled old-values save simply leaves the old values in place — they were never written.

## Files

- `Views/Option2OldValuesDialog.xaml(.cs)` — two-stage dialog (warning → opt-in choice).
- `Core/GroupModels.cs` — `Option2OldValuesPrompt.AssistEnabled`, `OldValueStagedEdit`.
- `Core/Importer.cs` — disposition rework (group-writability check, staging collection, coverage
  gate), `ChangeSet.Option2OldValueStaged`.
- `ViewModels/TransomViewModel.cs` — prompt wiring, post-apply `StageOldValueEditsInteractive` /
  `StageOldValueEdits` (hooked in `OnApplied`, before `_lastChangeSet` is dropped).
- `docs/design-notes/revit-api-research-notes.md` — corrected the stale "Comments writes directly"
  bullet that caused the defect.

## Test plan (live Revit, test model)

1. Yellow Comments column with grouped + loose doors → 2b → opt in → Clear. Expect: loose doors'
   Comments blanked, grouped members staged, file written after apply, report counts match.
2. Same but Assist OFF. Expect: warning only, no choices, nothing written to the old param.
3. Filtered schedule that excludes one instance of the group → expect the coverage gate to skip that
   member with the "not every instance covered" note.
4. Cancel at the save prompt → old values untouched, note says "not staged".

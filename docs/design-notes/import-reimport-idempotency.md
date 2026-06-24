# Design note — import idempotency: don't re-propose an already-applied grouped edit

**Status: implemented** (`Importer.AlreadyHas` + the bulk-instance filter in `HandleTypeRow`).

After an option-1 (Vary) apply writes per-instance values to a grouped row, clicking **Preview**
again on the SAME imported xlsx re-showed the edit and re-fired the GroupResolutionDialog, as if the
write never happened.

## TL;DR
It is **not** a literal stale read — `EnsureVary` + `VerifyWrite` + `VerifyApplied` all re-read the
live model and confirm the new value landed (the 154-instance blue vary was verified). The real
defect: the **grouped instance-bulk edit branch in `HandleTypeRow` was missing the "live value
already equals the new value → don't re-propose" no-op guard** that the normal-element and
type-parameter branches both have. So on re-preview it diffed the xlsx cell only against the
**export-time baseline** (frozen in `cowork_meta`, never refreshed because re-preview re-reads the same
file), found `cellText != baseline`, and re-emitted the bulk change — even though every instance now
already held `cellText`. Symptom = "re-read returns the old value"; mechanism = an absent idempotency
check on the bulk path.

## Where the values come from (so the asymmetry is unambiguous)
- `baseline` = the **exported cell text** captured at export. `ExcelWriter.BuildBaseline` writes
  `t.Cells[i][col].Text` into `cowork_meta`; `ExcelReader` reloads it into `ImportSheet.Baseline`.
  Preview/re-preview do **not** re-export — `ImportEventHandler.Execute(Mode.Preview)` just
  `new ExcelReader().Read(WorkbookPath)` of the same file, so `baseline` is constant across
  re-previews. (`ExcelWriter.cs:438,445-468`; `ExcelReader.cs:149-157`; `ImportEventHandler.cs:34-56`.)
- `edited` everywhere = `baseline != null ? cellText != baseline : <fallback>`. After a vary apply,
  `cellText` (new) still differs from `baseline` (old export) → `edited` stays true **forever**. That
  is by design and is fine — the no-op is meant to be caught by a *second*, live-value guard.

## The asymmetry (the bug)
Normal-element branch — `Importer.cs:306-341` — guards every storage type against the LIVE value:
```
String : else if ((param.AsString() ?? "") != cellText)            // line 310
Integer: else if (param.AsInteger() != iv)                          // line 320
Double : else if (Math.Abs(param.AsDouble() - parsed) >= 1e-9)      // line 333
```
Type-parameter branch — same protection downstream: `ResolveTypeGroups` skips a type whose current
value already matches (`if (value == tc.CurString) continue;` `Importer.cs:901`; the double path,
`Importer.cs:913`). So a re-applied **green** edit correctly collapses to "0 changes / 1 drift".

Grouped instance-bulk branch — `HandleTypeRow`, `Importer.cs:625-718` — had **no such guard**. It
computed `edited` from the baseline, resolved the representative instance, parsed the cell, and then
unconditionally built the `BulkChange`. It never asked "do the instances already hold this value?"
Hence the re-proposal after vary. (The option-1 vary path lands here: grouped non-itemized rows
resolve `binding == "instance"`, route through `BulkChange` + `Mark(...)` → `GroupMode.ProjectVary`.)

Related: `HandleGroupHeaderRow` (`Importer.cs:728-817`) shares the identical missing-guard shape and
is a latent twin; the same `AlreadyHas` filter would apply there if the symptom ever surfaces on the
header path.

## Why the existing post-apply checks don't save us
`VerifyApplied`/`VerifyWrite` only run on **Apply**, and they prove the write succeeded — they do not
feed back into a later Preview. Preview has no memory of a prior apply; its only "did the model
already change?" signal is comparing `cellText` to the live parameter, which is exactly the guard the
bulk branch omitted.

## The fix
**File:** `source/Transom/Core/Importer.cs` · **Method:** `HandleTypeRow`, the `binding == "instance"`
branch. Before building the bulk change, drop instances whose live value already equals the new value;
if none remain, emit nothing (and surface model drift as a yellow diagnostic so the user still sees
"changed since export").

Tail of the instance branch becomes:

```csharp
                // Idempotency: drop instances that ALREADY hold the new value (e.g. a prior option-1
                // vary apply already wrote them). Without this, a re-preview re-proposes the change —
                // the baseline is the frozen export value, so cellText != baseline stays true forever;
                // only a live-value compare can tell the write already happened. Mirrors the live-value
                // no-op guards on the normal-element path (String/Int/Double) and ResolveTypeGroups.
                var pending = new List<string>();
                int already = 0;
                foreach (var uid in ids)
                {
                    var inst = doc.GetElement(uid);
                    var ip = inst == null ? null : GetParam(inst, col.ParameterId);
                    if (ip != null && AlreadyHas(ip, isString, str, isInt, iv, dbl)) { already++; continue; }
                    pending.Add(uid);
                }
                // Every targeted instance already matches -> nothing to write. Surface it as drift so the
                // user still sees the cell "changed since export", consistent with the normal/type paths.
                if (pending.Count == 0)
                {
                    if (already > 0)
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                            $"changed since export (model already set to '{cellText}')", cellText));
                    continue;
                }

                // Group members can't be written directly — split them out so the rest still applies.
                var ungrouped = new List<string>();
                var grouped = new List<string>();
                string gName = "";
                foreach (var uid in pending)
                {
                    var inst = doc.GetElement(uid);
                    var (gi, gn) = inst == null ? (false, "") : GroupInfo(doc, inst);
                    if (gi) { grouped.Add(uid); if (gName == "") gName = gn; }
                    else ungrouped.Add(uid);
                }
                if (ungrouped.Count > 0)
                    cs.Changes.Add(BulkChange(nameEl, col, ungrouped, oldDisp, cellText, isString, str, dbl, isInt, iv));
                if (grouped.Count > 0)
                    cs.Changes.Add(Mark(BulkChange(nameEl, col, grouped, oldDisp, cellText, isString, str, dbl, isInt, iv), true, gName));
```

A small helper next to `VerifyWrite`/`Drifted` — the per-instance equivalent of `VerifyWrite`, reused
for the pre-write idempotency test:

```csharp
    /// <summary>True when a parameter already holds the value a change would write (within unit tolerance).
    /// Lets the bulk-instance path skip already-applied writes so a re-preview after an option-1 vary apply
    /// doesn't re-propose the change (string trimmed to match Revit's storage trim, doubles to 1e-9).</summary>
    private static bool AlreadyHas(Parameter p, bool isString, string str, bool isInt, int iv, double dbl)
    {
        try
        {
            if (p.StorageType == StorageType.String && isString)
                return (p.AsString() ?? "").Trim() == (str ?? "").Trim();
            if (p.StorageType == StorageType.Integer && isInt)
                return p.AsInteger() == iv;
            if (p.StorageType == StorageType.Double && !isString && !isInt)
                return Math.Abs(p.AsDouble() - dbl) <= 1e-9;
            return false;
        }
        catch { return false; }
    }
```

### Rationale / correctness notes
- **Targeted, not behavioral-broad:** only the bulk-instance branch changes. First-time edits still
  produce the change (instances don't yet match), so option-1 vary, normal bulk writes, and the Apply
  path are unaffected on the initial run. This purely removes the re-proposal of an already-applied write.
- **Tolerances match the writers** so the guard never disagrees with `VerifyWrite`: string `.Trim()`
  (Revit trims on store — `Importer.cs:1397`), integer exact, double `1e-9` (the same epsilon the normal
  path uses at `Importer.cs:333`; `VerifyWrite` uses 1e-6 for post-write slack — the tighter 1e-9 here is
  safe and consistent with the normal preview path).
- **Per-instance, not representative-only:** filtering each uid keeps the partial case correct — if some
  instances were varied and others weren't (or one drifted back), only the truly-unset instances are
  written, and the count in `BulkChange`/`Scope` reflects reality.
- **Drift visibility preserved:** when the whole row is already applied we emit a yellow "changed since
  export" diagnostic instead of a silent drop, matching how the normal/type paths report a re-applied edit.
- **Group-broken / option gating downstream is unaffected:** `ComputeGroupBroken` /
  `ComputeOption2Eligibility` iterate `cs.Changes`; with no change emitted there's nothing to gate, which
  is correct (no dialog should fire for a no-op column).

### How to verify
1. UNIT DOORS (or WINDOW) blue column → edit one grouped value → import → Preview → Apply → option-1
   Vary (writes all instances; confirm a sample instance holds the new value).
2. **Without re-exporting**, click **Preview** again on the SAME xlsx. Expect: that column now shows
   **0 changes** (optionally a yellow "changed since export / model already set"), and **no**
   GroupResolutionDialog fires for it on a subsequent Apply.
3. Regression: a fresh edit to a *different* grouped value in the same re-previewed file must still
   produce its change normally (proves the guard is value-scoped, not column-disabling).

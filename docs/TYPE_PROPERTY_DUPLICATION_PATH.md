<!--
  STATUS: Proposed design option — NOT implemented as of v1.3.1.
  One of several candidate approaches for editing instance parameters on grouped elements.
  Sits alongside: the vary-flag (blue) path, the definition-swap "dance", the isolate trick,
  and the RevitGroupClick UI-automation path. See the group-member-edits notes for the others.
  Applies ONLY to type-uniform columns in type-sorted schedules (see §5). Authored from the
  SAMPLE PROJECT_ARCH_R25 investigation.
-->

# The Type Property Duplication Path

**A non-destructive way to make "instance" schedule columns bulk-editable across
grouped elements — by relocating type-intended data to where it semantically belongs.**

> **Scope / when this applies.** This is **one possible path**, valid only when the
> values in a column are *genuinely intended to align by type* — i.e. every instance
> of a given type is meant to share the same value. It does **not** apply when a column
> legitimately varies per instance. Transom should only *suggest* it after detecting
> both conditions below (§5). Many real schedules — door, window, plumbing-fixture
> "type" schedules — are built exactly this way, so it has broad reach, but it is not
> universal.

---

## 1. The problem it solves

Editing an **instance** parameter on an element that lives inside a Revit **group** is
blocked by Revit:

> *Changes to groups are allowed only in group edit mode. Use the Edit Group command to
> change all instances of a group type. You may use the "Ungroup" option to proceed…*

(Confirmed live in `SAMPLE PROJECT_ARCH_R25` — a real value change to a grouped
door's `Hardware Group` raised exactly this modal dialog at transaction commit.)

The Revit API has **no `EditGroup` / group-edit-mode entry point** (still true in
2025–2026), so an add-in cannot do the clean manual move. The usual workarounds all
carry an integrity cost:

- **`SetAllowVaryBetweenGroups` (vary by group instance)** — sanctioned, but only for
  certain data types, and it permanently flips a model-wide flag; it also makes the
  param *independent* per instance, which is the opposite of "keep them identical."
- **Ungroup → edit → regroup → swap types ("the dance")** — touches group associativity
  and can proliferate group types.

Neither is satisfying when the value is supposed to be **uniform per type** anyway.

---

## 2. The key insight

In the investigated model, the door schedules are **type-anchored** (one row per door
*type* / FamilySymbol), and the real doors are **instances inside unit model groups**
(9 group instances across 2 group types, ~14 doors each). The `Hardware Group` values
tracked the door *type* one-to-one — i.e. the data was **type-intended but stored as an
instance parameter**. That mismatch is the entire source of the group-edit pain.

Proof the reframe is sound: **`Fire Rating` in the same model is a *type* parameter and
edits cleanly**, grouped or not — because editing a type parameter modifies the *type
definition*, not the grouped *instance*, so Revit's group-edit guard never fires.

> **Type-level edits never touch group members.** That single fact is what makes this
> path possible: groups stay grouped and are never opened.

---

## 3. The Type Property Duplication Path — algorithm

Add **one new type parameter**, copy the per-type values into it, repoint the schedule
column, verify, then remove the old field. Everything is a type-level or document-level
operation — no group member is ever modified.

1. **Eligibility detection** (see §5) — schedule rows resolve to types, and the target
   column is an instance parameter whose values are uniform within each type.

2. **Per-type uniformity check — the load-bearing safeguard.** For each type, gather
   *all* instances and confirm they share one value for the column:
   - all agree → safe to collapse to type level;
   - any disagree → **flag and exclude that type**, and surface it to the user. Never
     silently flatten a real per-instance distinction. This check is also the basis of
     the final verification.

3. **Create one new type parameter** — a shared parameter (temporary shared-parameter
   file, new GUID), bound to the element's category with a **`TypeBinding`**. One
   parameter holding a *per-type value* — not one parameter per type.

4. **Populate per type** — write each type's captured uniform value onto the **type**.
   Type edit ⇒ no group conflict ⇒ applies to every instance in every group at once.

5. **Repoint the schedule column** — in the `ScheduleDefinition`, replace the old field
   with the new one in the same position, copying the old field's heading text, width,
   alignment, and any conditional formatting so the column looks identical.

6. **Verify** — re-read the schedule and confirm every row's new value equals the
   original captured value, and confirm the (still-present) original instance values are
   unchanged. Wrap the whole operation in one transaction group = a single clean undo.

---

## 4. Clearing the original data (to avoid "which field do I edit?" confusion)

After a verified swap, two fields hold the same data, which is confusing. The original
should be eliminated — but **how** matters, and there is a trap:

> **Do NOT clear the old instance *values*.** Writing blanks to the instance parameter is
> still a modification of grouped members → it reintroduces the exact group-edit-mode
> block. "Clear the values" is self-defeating.

Instead, **remove the parameter, not its values:**

- **Project/shared instance params (e.g. `Hardware Group`):** delete the parameter
  **binding** (`doc.ParameterBindings.Remove(definition)`). This is a document/schema
  operation that strips the parameter from every element at once — including grouped
  ones — *without* editing any group member, so it does not trip the group guard. It also
  disappears from the Properties palette, so nothing stale remains to edit.

  Gate removal on:
  1. the new type parameter is **verified to match** first;
  2. a **usage check** — if the old parameter is referenced by another schedule, a tag,
     or a family formula, removal breaks those; skip-and-report instead;
  3. a **backup snapshot** of the original per-instance values *before* removal — removing
     the binding discards those values, and re-adding it later returns blanks. (Transom's
     export/inspect dump serves as this rollback artifact.)

- **Built-in instance params (e.g. `Comments` / REMARKS) — the asymmetry.** A built-in's
  binding cannot be removed, and its values cannot be cleared without editing group
  members. So the old data **cannot be cleanly wiped**. The best available step is to
  **drop the column from the schedule** (so it is no longer presented as the editable
  field) and accept that the instance value remains underneath. For type-uniform remarks,
  prefer the existing built-in **`Type Comments`** as the new field rather than a new
  shared param. This limitation should be stated plainly to the user.

Net final flow: *verify match → usage check → snapshot originals → remove binding
(shared) or drop column (built-in) → confirm gone from Properties.* Reversible except for
the discarded values, which the snapshot covers.

---

## 5. When Transom should *suggest* this

Trigger the suggestion when the schedule being analyzed for import meets **both**:

1. **Type-sorted structure** — the data rows resolve to types (Transom's `ScheduleReader`
   already tags these `kind:"type"`); the schedule is not itemized per instance. This is
   how a large fraction of production schedules are built.
2. **A type-uniform instance column** — at least one instance-parameter column whose
   values are uniform within every type (per the §2 check).

Example prompt: *"HARDWARE is an instance parameter, but its values are uniform per door
type. Promote it to a type parameter? This keeps all unit groups intact and makes the
column bulk-editable without group-edit conflicts."*

This replaces — for the qualifying columns — Transom's entire "blue/yellow grouped cell"
machinery (vary flag, ungroup dance, the bridge's group-member writes).

---

## 6. Trade-offs / disadvantages

- **Loss of per-instance override.** Once a column is type-level, making one element of a
  type differ requires creating a *new type*. Acceptable only when the data is truly
  type-aligned — which is exactly the precondition. The uniformity check (§2) is what
  guarantees we only convert columns where this loss is a no-op today.
- **Migration is a real operation, not a flag flip.** New shared param + per-type writes +
  schedule edit + (optional) binding removal. Must be transactional and verified.
- **Built-in columns can't be fully cleaned** (§4) — partial result for `Comments`-style
  fields.
- **Shared-parameter file dependency** and **column-formatting fidelity** need care
  (copy formatting from the old field; use/create a temp shared-param file).
- **Redundant data window** — between the swap and the removal, both fields exist; keep
  the order strict and the snapshot in hand.

---

## 7. Related bridge fixes uncovered during this investigation

Independent of this path, the live probe exposed defects in `BridgeTools` that should be
fixed before *any* grouped write is trusted:

1. **Premature verification.** `ApplyEdit` does `Set()` + re-read *inside* the
   transaction; the group conflict only fires at **commit**, so it reported
   `verified:true` for a change that was then rolled back. Verify after a successful
   commit (or treat a blocked commit as failure).
2. **No `IFailuresPreprocessor`.** The grouped write surfaced an interactive Revit dialog
   ("Ungroup / Cancel"). On a headless, Claude-driven bridge that dialog would hang the
   session — and an "Ungroup" default would be destructive. Register a failure handler
   that catches the group-edit failure and rolls back silently.
3. **Missing `EnsureVary`.** Unlike `Importer.cs`, `BridgeTools.ApplyEdit` never calls
   `SetAllowVaryBetweenGroups` before writing project/shared params on group members, so
   it can only ever walk into the group-edit block for those.

The Type Property Duplication Path makes (3) moot for the qualifying columns (no grouped
writes at all), but (1) and (2) are still worth fixing for the general write path.

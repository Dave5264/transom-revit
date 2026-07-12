# Export tab — color legend copy (draft for review)

Edit the wording below, then hand it back for implementation. Two tiers:

- **Legend** = the at-a-glance line next to each swatch on the Export tab. Goal: a new user
  instantly knows what happens if they edit a cell of that color. One short clause, no jargon
  (no "vary by group instance", no option numbers, no parameter taxonomy).
- **More information** = a "More information" button below the legend opens a dialog (same style
  as the "How Claude works" window) with one section per color carrying the full technical story.

The header line above the legend also changes, from "In the exported file:" to something that
frames the colors as edit guidance.

---

## Legend (at-a-glance)

Header line: **"Cell colors show what an edit will touch on import:"**

| Swatch | Term | Legend text |
|---|---|---|
| white | normal | instance parameter - edits the element(s) in that row, nothing else |
| green | green | type parameter (shared value) — edits every element of the type (or under the header) |
| blue | blue | project parameter in a model group — imports fine; each element keeps its own value |
| yellow | yellow | built-in data parameter in a model group — Transom asks how to apply it on import |
| red | red | geometry-driving parameter — may only be changed through group edit mode (requires Claude-Assist to automate) |
| grey | grey | not importable — Revit computes it or locks it |

---

## "More information" dialog

Title: **Export cell colors — the full story**

Intro paragraph:
> Transom colors each editable cell by how the edit can be applied back to the model. White and
> green cells import directly. Blue, yellow, and red mark elements inside Revit **model groups** —
> Revit restricts writes to group members, so Transom resolves each case differently. Grey cells
> can't be written at all. Colors are painted at export time from the live model, so a re-export
> always reflects the current grouping.

### normal (white)
> A writable **instance parameter** that needs no special handling. The edit's scope is exactly
> the element(s) the row represents: on an itemized schedule that's one element; on a
> non-itemized schedule, where one row stands for several elements, the edit writes to **every
> member of that row** (the import preview shows the exact count first). Identity built-ins like
> Mark stay white even on grouped elements — Revit accepts a direct write for those.

### green — shared value
> The value is shared **beyond the row**: a **type parameter** (every instance of that type shows
> it, including instances on other schedules) or an **editable group-header cell** (the header's
> value is the grouping parameter of every row under it). Editing it writes to every element the
> value covers, in one transaction — the import preview shows the exact element count before
> anything is written. The white/green distinction is scope: white stays inside the row, green
> reaches past it.

### blue — grouped, resolves automatically
> A **project or shared instance parameter** on an element inside a model group. Revit normally
> forces group members to match the group definition, but these parameters support **"vary by
> group instance"**. On import, Transom enables that flag on the parameter (a one-time, one-way
> model change — this is "option 1" in the group dialog) and then writes each element's own
> value. Nothing is ungrouped and other instances of the group are untouched.

### yellow — grouped, needs a decision
> A **built-in data parameter** (Comments, Mark, Finish…) on a group member. Built-ins can't
> "vary by group instance" and can't be written on a member from outside group-edit mode, so
> there is no silent fix — the import group dialog asks you to pick one:
> - **Option 2a / 2b** — Transom creates a **new type (2a) or instance (2b) parameter**, moves the
>   column onto it in the schedule, and writes your edits there. Nothing is ungrouped; the
>   original built-in stays as-is underneath.
> - **Claude-Assist (option 3)** — Claude drives Revit's Edit Group mode and sets the built-in
>   itself. The value lands on the **group definition**, so every instance of that group type gets
>   the same value.
> - **Skip** — leave the column unchanged.

### red — grouped, geometry-driving
> A parameter that **drives geometry** (sill height, head height…) on a group member. Replacing
> it with a new parameter (the yellow fix) would only change a number in the schedule — the model
> geometry wouldn't move, and the schedule would silently disagree with the model. So Transom
> never offers option 2 here: the only paths are **Claude-Assist** (Claude edits the group the
> way a person would, and verifies the result) or editing it yourself in **Edit Group mode**.
>
> When Claude Assist is **off** at export time, red cells render with **grey text** — effectively
> locked until you turn Claude Assist on (in Settings) and re-export.

### grey — not importable
> Anything Transom can't write back: calculated and combined fields, counts and geometry-derived
> values, parameters the family/type drives read-only, header cells that don't round-trip (the
> blanks under merged super-headers), and every data cell of a schedule that can't round-trip at
> all (display-only export). Edit them in the spreadsheet if it helps your downstream use —
> Transom ignores them on import and lists them in the skipped panel.

Closing line:
> Ungrouped elements never show blue/yellow/red — those three exist only because Revit locks
> group members. If a schedule has no model groups, everything is white, green, or grey.

### The import group dialog — options in depth

Shown after the color sections, since blue/yellow/red all reference it.

Intro:
> White and green cells never see this dialog — Transom writes them with a standard edit (a
> normal parameter write inside the import transaction), so there is nothing to resolve. The
> dialog appears only when an Apply touches parameters on **model-group members**: Transom
> pauses per affected column and asks how to resolve it. Which options appear depends on the
> column's parameter — the cell color predicts it: blue offers option 1, yellow offers
> options 2/3, red offers only 3. Skip is always available.

> **Option 1 — "Vary by group instance"** (blue cells). Project/shared instance parameters
> support per-instance values inside groups once the parameter's **"vary by group instance"**
> flag is on. Transom flips that flag, then writes each element's own value. The flag is a
> property of the parameter itself, project-wide, and is effectively a one-way switch — turning
> it back off later collapses members to matching values again. Side effect to know: a schedule
> cell spanning group instances shows **`<varies>`** when their values differ. No new parameter,
> nothing ungrouped.

> **Option 2a — replace the column with a new type parameter** (yellow cells, when every
> instance of a type shares one value). Transom creates a new **shared parameter** bound to the
> category as a **type** parameter, merges the column's existing values with your edits into it,
> and repoints the schedule field so the column keeps its heading. Every instance of the type
> shows the value. You confirm the parameter's name first (pre-filled "<field> (Transom)").
> Not offered when the schedule is itemized with differing per-instance values — a single type
> parameter can't hold them.

> **Option 2b — replace the column with a new instance parameter** (yellow cells). Same
> mechanics as 2a but **instance**-bound, so each element keeps its own value. Offered for
> built-ins, where option 1 is impossible; for blue cells it's suppressed because option 1
> already preserves per-instance values without adding a parameter. Never ungroups and is safe
> with excluded group members.
>
> Both option-2 paths also write the column's **ungrouped** elements onto the new parameter, so
> the displayed column stays consistent; the original built-in keeps its old values underneath.

> **Option 3 — Claude-Assist** (yellow and red cells; requires Claude Assist on in Settings).
> Transom stages the edits to `transom_group_edits.json` (with a how-to file beside it) and
> Claude drives Revit's **Edit Group** mode the way a person would — enter the group, set the
> parameter, finish, verify the value and that the member count is unchanged. Because this edits
> the **group definition**, every instance of that group type receives the same value. Works even
> with excluded members, attached detail groups, and nested groups.

> **Skip** — leaves the whole column unchanged, including its ungrouped elements (they aren't
> written either, so the model never half-applies a column).

> Why red never offers option 2: a geometry-driving parameter (sill height, head height…) that's
> replaced by a new parameter would just be a number in a schedule — the model geometry wouldn't
> move, and the two would silently disagree. Option 3 (or manual Edit Group) is the only honest
> path.

---

## UI notes (not copy — how it gets wired)

- "More information" is a small link-style button directly under the legend rows
  (like the "Advanced" expander style, or a normal small button — implementer's choice).
- The dialog is a new modal window matching ClaudeAssistHelpDialog's look (same palette,
  scrollable, "Got it" close button), with each color section headed by its swatch + term.
- Legend text lives in TransomView.xaml (~lines 197–236); the current six rows stay, only the
  strings shrink. The header "In the exported file:" is replaced.

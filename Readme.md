<div align="center">

<table align="center"><tr><td><pre>
████████╗██████╗  █████╗ ███╗   ██╗███████╗ ██████╗ ███╗   ███╗
╚══██╔══╝██╔══██╗██╔══██╗████╗  ██║██╔════╝██╔═══██╗████╗ ████║
   ██║   ██████╔╝███████║██╔██╗ ██║███████╗██║   ██║██╔████╔██║
   ██║   ██╔══██╗██╔══██║██║╚██╗██║╚════██║██║   ██║██║╚██╔╝██║
   ██║   ██║  ██║██║  ██║██║ ╚████║███████║╚██████╔╝██║ ╚═╝ ██║
   ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝╚══════╝ ╚═════╝ ╚═╝     ╚═╝
</pre></td></tr></table>


**Edit Revit schedules anywhere and import them back safely — and drive the live model from Claude Code.**

[![Latest release](https://img.shields.io/github/v/release/Dave5264/transom-revit?label=latest%20release&color=2ea44f&logo=github)](https://github.com/Dave5264/transom-revit/releases/latest)

### ⬇ [Download the installer](https://github.com/Dave5264/transom-revit/releases/download/v1.9.5/Transom-1.9.5-SingleUser.msi)

**One click, no admin rights** — installs into your per-user Revit add-ins folder.
Double-click the `.msi`, then start Revit. Supports **Revit 2025, 2026 & 2027**. Free.

</div>

Transom is an Autodesk Revit add-in that facilitates **schedule editing** and provides **integration with Claude
Code**. Most project schedules can be exported, edited anywhere, and imported back into the model safely — and
the export **color-codes every cell by what can actually be written back**, so you know before you type. The
awkward Revit cases are handled rather than excluded: type parameters, group headers, non-itemized rows, and
parameters on elements inside model groups each have a defined path back into the model.

Both halves stand alone. The schedule round-trip works with no Claude client anywhere in sight; the Claude
layer works on any part of the model, schedules or not. Neither needs administrator rights.

### Schedules — export, edit anywhere, import back

Export the schedules you tick (each becomes a sheet) to `.xlsx` or `.xls` — or `.csv` for a display-only
copy. The sheet looks like Revit does: merged headers, grouping, subtotals, fonts, colors, hidden columns,
calculated/combined/percentage fields, and Revit's own row order. Edit it in Excel, in Sheets, on a machine
with no Revit installed, or hand it to a consultant.

Bring it back through **Import → Preview → Apply**. The preview lists every change, the elements each one
touches, and which schedules will be affected; format-mismatched edits (`2'6` for `2'-6"`) surface as pending
rows and **Apply stays greyed until each is confirmed or discarded**. Everything that survives is written in
a **single transaction** — including **type parameters** — and each value is re-read and verified afterward.
Cells Transom can't write are ignored and listed, never half-applied.

**Cell colors** are painted at export time from the live model, so they always reflect the current state:

| color | means |
|---|---|
| **white** | writable instance parameter — the edit touches exactly the element(s) in that row |
| **green** | a shared value — a type parameter, or an editable group-header cell — the edit reaches every element it covers (the preview counts them first) |
| **blue** | project/shared instance parameter on an element **inside a model group** — imports fine; Transom enables "vary by group instance" and each element keeps its own value |
| **yellow** | **built-in** parameter (Comments, Finish…) on a group member — Revit forbids the write from outside group-edit mode, so import asks you how to apply it |
| **red** | a **geometry-driving** parameter on a group member — only Edit Group mode can honestly change it (Claude-Assist automates that; grey text means Claude Assist is off) |
| **grey** | not importable — Revit computes or locks the value (calculated and combined fields, counts, read-only family/type-driven parameters) |

Ungrouped elements never show blue, yellow, or red — those three exist only because Revit locks group
members. A model with no groups is all white, green, and grey. The Export tab carries the same legend, with a
**More information…** dialog explaining each color and each import option in full.

### Claude Code integration

The Claude Code integration layer connects the live Revit model to Claude through a **local bridge** —
loopback only (`127.0.0.1`), authenticated with a per-session token, bundled with the add-in. No separate
install, no admin rights, nothing leaves your machine.

With it on, you can ask Claude Code to work on the model in natural language: read and cross-check schedules,
apply staged edits, create sheets and views, run bulk edits, tag things, and carry out follow-up work — the
in-process `execute_revit_code` tool gives it the **full Revit API**, alongside ~35 purpose-built tools for
views, elements, creation and MEP that were each live-verified against a real project model. A second server, `transom-ui-assist`, lets Claude **take over the
Revit interface** where the API simply has no path — most importantly **Edit Group mode**, for built-in and
geometry-driving parameters on group members (see [below](#grouped-parameters--claude-assist)).

A built-in **skill library** (Schedule Hub → Claude Skills) keeps reusable Claude workflows as `.md` files in
a per-user folder, so they're available in **every project** rather than living in one repo — import your
own, keep the ones Claude writes for you, share the files with colleagues, and **Stage** copies a skill's path
straight to your clipboard to paste into Claude Code. Two ship with the add-in: a read-only **schedule
inventory** (a safe first thing to try) and **elevation door/window tagging** that recognises visible openings
optically, so openings hidden behind the facade never get tagged.

Turning it on, once:

1. Turn **Claude Assist** on in Transom Settings (Schedule Hub → Settings tab) — the first ON registers
   Transom's MCP servers and starts the bridge; a status panel shows every layer at a glance.
2. Restart Claude Code so it launches the shim and picks up the new servers.

Then drop the guidance file from [`claude/`](claude/) (`CLAUDE.md`) into your project root (or `~/.claude/CLAUDE.md`)
so Claude Code already knows the tools and the safe-write workflow. See [`claude/README.md`](claude/README.md) for
exactly where it goes. Not sure what to ask for? **Show me what you can do** exports a scripted demo Claude
can run in a fresh project while you watch. The supported client is **Claude Code** (Claude Cowork runs in a
VM that can't reach a host-side bridge). For UI-assist runs, start Claude Code with bypass permissions on —
otherwise its permission prompts steal focus from Revit and clicks silently miss.

> **Status:** v1.9.5 released (Revit 2025/2026/2027) — **five fixes from live option-2 UI testing.** "Apply selected" no longer stays enabled after a preview that found nothing to change, and the modal encouragement pop-up is suppressed while Claude Assist is on so it can't block the bridge mid-automation. The staged-handoff guide now spells out the bypass-permissions/focus caveat for the Edit Group pass.
>
> **Previously:** v1.9.4 made the Claude Skills tab readable (the list rendered blank rows since the tab shipped) and added a shipped **elevation door/window tagging** skill that recognises visible openings optically, so ones hidden behind the facade are never tagged.
>
> **Previously:** v1.9.2 fixed schedule export: the Excel engine (Transom.Office + NPOI) had been dropped from the v1.8.0–v1.9.1 installers, so Export failed with a misleading "open in Excel" error. The engine now builds and ships automatically, and the installer refuses to pack without it.
>
> v1.9.0–v1.9.1 eased the on-ramp to Claude Assist — Settings opens from a bare Revit session
> (Export/Import tabs enable the moment a project opens), the Claude Assist guide exports as a standalone `.md`,
> a **Show me what you can do** button exports a scripted first demo, and Claude-running detection sees Claude
> Code under any host via Transom's own MCP processes. v1.8.0 closed the import **confirm gate** — format-mismatched edits (e.g. `2'6` for `2'-6"`)
> surface as pending rows and **Apply stays greyed until each is confirmed or discarded** — and broadened the
> option-2 "replace column with a new parameter" path (repoint other schedules to the new param, choose what
> happens to the old values); exports now write **directly** to the chosen file. v1.7.0 consolidated every Claude
> control into a single persisted **Claude Assist** toggle with an always-visible status checklist. v1.6.0–v1.6.1
> added the **extended bridge tools** (element/selection/view workflows a Claude client can drive
> directly, live-reviewed), plus a revision confirm step and anchor-pass hardening. v1.5.0 made
> **`execute_revit_code` work alongside other add-ins that load Roslyn** (isolated load context — no more
> `FileLoadException`).

### Grouped parameters — Claude-Assist

Parameters on elements inside a Revit **model group** are the case that stops most schedule tools, and
Transom's yellow/blue/red cells are exactly that case. Revit lets a project or shared *instance* parameter
vary per group instance, so Transom just enables the flag and writes it (blue, "option 1"). But a **built-in**
parameter (Comments, Finish…) or a **geometry-driving** one can only be changed from inside **Edit Group**
mode — no API path exists, at all. On import, Transom offers a resolution dialog per affected column:

- **Option 2a / 2b** — create a new **type** (2a) or **instance** (2b) parameter, repoint the schedule column
  onto it, and write your edits there. Nothing is ungrouped and the original built-in stays as-is underneath.
  Never offered for geometry-driving (red) parameters: a new parameter would change a number in a schedule
  while the geometry stayed put, and the two would silently disagree.
- **Option 3 — Claude-Assist** — Claude drives the Revit UI the way a person would. Transom writes a staging
  `.json` plus a step-by-step `.md` to a folder you choose (it does **not** touch the model itself); the
  **same Claude Code session** already connected to the bridge reads those files, enters Edit Group mode, sets
  the parameter in the Properties palette, finishes the group, then verifies the value and the member count
  against the model. One client the whole way — nothing to hand off to a second app. Works through excluded
  members, attached detail groups, and nested groups.
- **Skip** — leave the column untouched, including its ungrouped elements.

Editing a built-in through Edit Group mode sets it **uniformly for every instance of that group type** —
that's how Revit groups work. If you need **different** values per instance, take option 2b instead. Notes:
Claude-Assist drives the live UI, so run it on a **throwaway / non-production model first**, and if the model
is **workshared, never Synchronize with Central mid-run** (you control sync).

## Install

The quickest way is the **[per-user installer](https://github.com/Dave5264/transom-revit/releases/latest)** — no administrator rights required:

1. Download **`Transom-…-SingleUser.msi`** from the [latest release](https://github.com/Dave5264/transom-revit/releases/latest).
2. Double-click it. It installs into `%AppData%\Autodesk\Revit\Addins\` for the current user only.
3. Launch Revit — Transom is on the ribbon. To remove it later: *Apps & features → Transom*.

**Always use the SingleUser installer above** — the one-click download link on this page points to it
deliberately. Transom is designed to install per-user with **no admin rights**, and Claude-Assist's
install-time setup only runs in the per-user installer.

A machine-wide **MultiUser** installer is also built from this codebase, but it is **intentionally not
offered as a download link here** so no one installs it by mistake. It exists only for **IT / firm-wide
deployment** (an administrator installing Transom once for every user on a shared or imaged machine). It
**requires admin rights** and, because a per-machine install runs as `SYSTEM` and can't write each user's
`%LocalAppData%`, the Claude-Assist MCP shim is placed on Revit's **first launch** instead of at install
time (slightly less seamless, but it still works). If you genuinely need the machine-wide installer, build
it from source (see [Building](#building)) or ask — it's kept ready in the repo, just not surfaced as a
one-click download.

## How it works (architecture in one paragraph)

The visible sheet is rendered straight from `ViewSchedule.GetCellText` / `GetTableData`, so calculated,
combined, percentage and subtotal fields — and Revit's row order — come out exactly as shown. A separate
*anchor pass* stamps each row's `UniqueId` into a hidden, sentinel-headed column, which drives a safe,
durable round-trip on import: itemized schedules anchor per instance, non-itemized ones per type, and
type-less grouped schedules (rooms, areas) by matching their visible group-field values. Cell colors come
from classifying each column's binding and each row's group membership during that same pass. A schedule
falls back to display-only when nothing can be anchored — material takeoffs, linked-element rows, empty
schedules, and hierarchical grouping whose group fields aren't visible columns.

## Targets

| Revit | Runtime | Build configuration |
|-------|---------|---------------------|
| 2025  | .NET 8  | `Debug.R25` / `Release.R25` |
| 2026  | .NET 8  | `Debug.R26` / `Release.R26` |
| 2027  | .NET 10 | `Debug.R27` / `Release.R27` |

Built on the [Nice3point Revit SDK](https://github.com/Nice3point/RevitTemplates) (multi-version + dynamic-
loading isolation); Excel via [NPOI](https://github.com/nissl-lab/npoi).

## Building

```shell
# Revit 2025 (.NET 8)
dotnet build source/Transom/Transom.csproj -c Debug.R25

# Revit 2027 (.NET 10)
dotnet build source/Transom/Transom.csproj -c Debug.R27
```

A successful build deploys the add-in to `%AppData%\Autodesk\Revit\Addins\<version>\`. The MSI installer and
all-versions build run through the `build/` ModularPipelines project (`cd build; dotnet run -- pack`).

## Repository layout

| Path | Description |
|------|-------------|
| `source/Transom/` | the add-in (commands, views, view-models, core logic) |
| `build/`, `install/` | ModularPipelines build + WiX installer |
| `branding/` | ribbon icon + generator |
| `docs/` | design docs: `design-notes/` (shipped-fix rationale + consolidated Revit-API research notes) and `parity-tool-status.md` (bridge-tool review state) |
| `tools/` | standalone dev tools (`transom_verify.py`) |

<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/transom-title-dark.svg">
  <img alt="Transom" src="branding/transom-title-light.svg" width="600">
</picture>


**Round-trip Revit schedules through spreadsheets — with full visual fidelity and type-parameter–safe write-back.**

[![Latest release](https://img.shields.io/github/v/release/Dave5264/transom-revit?label=latest%20release&color=2ea44f&logo=github)](https://github.com/Dave5264/transom-revit/releases/latest)

### ⬇ [Download the installer](https://github.com/Dave5264/transom-revit/releases/download/v1.4.8/Transom-1.4.8-SingleUser.msi)

**One click, no admin rights** — installs into your per-user Revit add-ins folder.
Double-click the `.msi`, then start Revit. Supports **Revit 2025, 2026 & 2027**.

</div>

Transom is an Autodesk Revit add-in (C#) that exports schedules to `.xlsx` / `.csv` / `.xls` exactly as Revit
displays them — merged headers, grouping, subtotals, fonts, colors, hidden columns — and imports edited values
back into the model, **including type parameters**, safely and inside a single transaction. Optionally, a
connected **Claude** client can act on the live model over a local MCP bridge — review a round-trip, apply
staged edits, and run any Revit API operation or follow-up you ask for (via an in-process `execute_revit_code`
tool) — while the add-in stays **fully standalone** without it.

### Claude MCP bridge

The add-in bundles a local loopback-only MCP bridge (`127.0.0.1`) plus a self-contained shim — no admin
rights, no separate install, nothing leaves your machine. To let a Claude client (Claude Code)
read and write the live model:

1. Click **"Set up Claude"** in the Transom ribbon (one-time — it registers Transom's MCP servers).
2. Restart Claude Code so it launches the shim and picks up the new server.

Then drop the guidance file from [`claude/`](claude/) (`CLAUDE.md`) into your project root (or `~/.claude/CLAUDE.md`)
so Claude Code already knows the tools and the safe-write workflow. See [`claude/README.md`](claude/README.md) for
exactly where it goes.

> **Status:** v1.4.8 released (Revit 2025/2026/2027) — **Claude-Assist gains full Revit API access.** A new
> `execute_revit_code` bridge tool lets a connected Claude client run arbitrary Revit API code in the live model
> (compiled in-process via Roslyn), advertised in the MCP shim's tool list alongside the schedule tools. The bridge
> status probe was also corrected (it now binds the right loopback port and asserts on the live status body), and
> the in-app help points at the Claude Code path. Built on v1.4.7, where **Claude-Assist first actually connected**:
> the bundled MCP shim framing was corrected (newline-delimited JSON-RPC over stdio, plus a `protocolVersion` echo on
> initialize) so a Claude client (Claude Code) completes the handshake and stays connected — in v1.4.6 the bridge
> installed but the connection never succeeded. The three Claude ribbon buttons are also consolidated into one **"Set
> up Claude"**. Built on v1.4.6's **seamless setup**: the per-user installer places the MCP shim into `%LocalAppData%`
> at **install time**, so the bridge has the current shim without needing to open Revit first; and the Claude-Assist
> guidance directs the **manual "Edit Group" mode** path (drive the Revit UI to edit a grouped built-in) instead of
> an API group-rebuild, with richer staging files (doc path + GUID, old→new values, the new-parameter name) so
> Claude Code can apply and verify reliably. Built on v1.4.5
> (grouped-import correctness + option-2 UX): when a column edit touches members of a model group, the chosen
> resolution now applies to the column's **ungrouped**
> instances too: Skip skips them (they no longer write anyway), Claude-Assist stages them with the grouped ones,
> and the new-parameter (option-2a/2b) path writes the edit to the new param for ungrouped members instead of
> leaving it on the old hidden column. The group-conflict dialog now lets you **confirm/rename the new parameter**
> before it's created, drops the misleading "Recommended" tag, offers the new-instance-param path only when
> per-instance Vary isn't available, suppresses the type-param option on a per-type conflict, and the replacement
> column inherits the original column's text justification. The installer dialogs were also fixed so their title
> text is readable (was dark-on-dark). Built on v1.4.3, which fixed option-2a value routing on type-organized
> schedules + read-only-edit reporting; and v1.4.2, which refreshed the installer art plus import-UX:
> "Apply selected" stays greyed until you Preview, format-mismatched values become inline confirm rows
> (you confirm "2.5 → 2'-6"" before it applies), the conflict picker accepts any entered value, and the
> bundled Claude-Assist helper got UI-automation hardening. Built on v1.4.1, which hardened grouped-schedule import:
> type- and group-organized schedules now key their import baseline **per rendered row**, so editing one row no
> longer collapses the whole type to a single value or produces phantom edits; grouped built-in *data* params
> (Mark, Comments, Number, Finish) write directly while only geometry-driving built-ins route through
> Claude-Assist; and conflict-only schedules stay selectable so their type conflicts reach the resolution dialog.
> v1.4.0 added **editable column headers** (rename a column caption or a grouped super-header in the spreadsheet
> and it writes back), **hidden-group instance resolution**, and a clearer **per-parameter color contract** —
> every cell gets a definite color: no-fill = directly editable instance, green = type/bulk, blue = grouped
> instance Transom resolves without AI, yellow = grouped geometry-driving built-in (Claude-Assist only),
> grey = not settable. v1.3.1 bundled the MCP shim and auto-registers it on first launch, so the Claude bridge
> connects with no manual setup. Requirements are locked in
> [`SPEC.md`](SPEC.md); the build approach is in [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md).

### Using Claude-Assist

A connected Claude client can do far more than round-trip QA — through the bridge's `execute_revit_code` tool it
has the **full Revit API**: read and write the live model, create sheets/views, run bulk edits, and carry out
follow-up actions you ask for. The one thing the API genuinely **can't** do is edit a **built-in** parameter
(Comments, Finish, …) on an element **inside a Revit model group** — Revit only allows that in "Edit Group"
mode. For that case, Transom hands the edit to **Claude-Assist**, which drives the Revit UI like a person would:

1. **Export** the schedule, edit the cells in the spreadsheet, then **Import** it back and click **Preview**.
2. On a grouped built-in cell, **Apply** prompts a resolution dialog — pick **"Claude-Assist"**.
3. Transom writes a small **staging `.json`** (plus a step-by-step `.md`) to a folder you choose — it does **not**
   change the model itself.
4. The **same Claude Code session** that's already connected to the bridge drives the rest: pointed at that folder,
   it reads the staging files, opens "Edit Group" mode, sets the parameter via the Properties palette, finishes the
   group, and verifies the result against the model. You stay in one client the whole time — there's no second app
   to hand anything to.

Editing a built-in this way sets it **uniformly for every instance of that group type** (that's how Revit
groups work). If you need **different** values per instance, use the new-**instance**-parameter option (2b) in
the resolution dialog instead. Notes: Claude-Assist drives the live UI, so run it on a **throwaway / non-production
model first**, and if the model is **workshared, never Synchronize with Central mid-run** (you control sync).

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
*anchor pass* stamps each element row's `UniqueId` into a hidden, sentinel-headed column, which drives a
safe, durable round-trip on import. Round-trip is enabled only where each row maps cleanly to one writable
element; material-takeoff, embedded, linked-element and non-itemized schedules export display-only. See
[`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) and the two design audits ([`AUDIT.md`](AUDIT.md),
[`AUDIT2.md`](AUDIT2.md)).

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
| `SPEC.md`, `BRIEF.md`, `IMPLEMENTATION_PLAN.md` | locked requirements, context, coding plan |
| `AUDIT.md`, `AUDIT2.md` | independent viability audits |
| `mockup.html` | dialog visual mockup |

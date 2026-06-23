<div align="center">

# Transom

**Round-trip Revit schedules through spreadsheets — with full visual fidelity and type-parameter–safe write-back.**

[![Latest release](https://img.shields.io/github/v/release/Dave5264/transom-revit?label=latest%20release&color=2ea44f&logo=github)](https://github.com/Dave5264/transom-revit/releases/latest)

### ⬇ [Download the installer](https://github.com/Dave5264/transom-revit/releases/download/v1.4.5/Transom-1.4.5-SingleUser.msi)

**One click, no admin rights** — installs into your per-user Revit add-ins folder.
Double-click the `.msi`, then start Revit. Supports **Revit 2025, 2026 & 2027**.

</div>

Transom is an Autodesk Revit add-in (C#) that exports schedules to `.xlsx` / `.csv` / `.xls` exactly as Revit
displays them — merged headers, grouping, subtotals, fonts, colors, hidden columns — and imports edited values
back into the model, **including type parameters**, safely and inside a single transaction. An optional
Claude-assisted QA layer can reconcile exports and pre-flight imports against the live model over a local MCP
bridge, but the add-in is fully standalone without it.

### Claude MCP bridge

The add-in bundles a local loopback-only MCP bridge (`127.0.0.1`) plus a self-contained shim — no admin
rights, no separate install, nothing leaves your machine. To let a Claude client (Claude Code / Cowork)
read and write the live model:

1. Click **"Register Claude Bridge"** in the Transom ribbon (one-time — it registers the `transom` MCP server).
2. Restart your Claude client so it launches the shim and picks up the new server.

Then drop the guidance file from [`claude/`](claude/) (`CLAUDE.md`) where your client auto-loads instructions
so Claude already knows the tools and the safe-write workflow. See [`claude/README.md`](claude/README.md) for
exactly where each file goes.

> **Status:** v1.4.5 released (Revit 2025/2026/2027) — grouped-import correctness + option-2 UX. When a
> column edit touches members of a model group, the chosen resolution now applies to the column's **ungrouped**
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

## Install

The quickest way is the **[per-user installer](https://github.com/Dave5264/transom-revit/releases/latest)** — no administrator rights required:

1. Download **`Transom-…-SingleUser.msi`** from the [latest release](https://github.com/Dave5264/transom-revit/releases/latest).
2. Double-click it. It installs into `%AppData%\Autodesk\Revit\Addins\` for the current user only.
3. Launch Revit — Transom is on the ribbon. To remove it later: *Apps & features → Transom*.

A machine-wide `MultiUser.msi` (all users, requires admin) is built alongside it. Prefer to build from
source instead? See [Building](#building).

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

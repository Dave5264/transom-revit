<div align="center">

<table align="center"><tr><td><pre>
████████╗██████╗  █████╗ ███╗   ██╗███████╗ ██████╗ ███╗   ███╗
╚══██╔══╝██╔══██╗██╔══██╗████╗  ██║██╔════╝██╔═══██╗████╗ ████║
   ██║   ██████╔╝███████║██╔██╗ ██║███████╗██║   ██║██╔████╔██║
   ██║   ██╔══██╗██╔══██║██║╚██╗██║╚════██║██║   ██║██║╚██╔╝██║
   ██║   ██║  ██║██║  ██║██║ ╚████║███████║╚██████╔╝██║ ╚═╝ ██║
   ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝╚══════╝ ╚═════╝ ╚═╝     ╚═╝
</pre></td></tr></table>


**Round-trip Revit schedules through spreadsheets — with full visual fidelity and type-parameter–safe write-back.**

[![Latest release](https://img.shields.io/github/v/release/Dave5264/transom-revit?label=latest%20release&color=2ea44f&logo=github)](https://github.com/Dave5264/transom-revit/releases/latest)

### ⬇ [Download the installer](https://github.com/Dave5264/transom-revit/releases/download/v1.9.1/Transom-1.9.1-SingleUser.msi)

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

1. Turn **Claude Assist** on in Transom Settings (Schedule Hub → Settings tab) — the first time, it
   registers Transom's MCP servers and starts the bridge; a status panel shows every layer at a glance.
2. Restart Claude Code so it launches the shim and picks up the new server.

Then drop the guidance file from [`claude/`](claude/) (`CLAUDE.md`) into your project root (or `~/.claude/CLAUDE.md`)
so Claude Code already knows the tools and the safe-write workflow. See [`claude/README.md`](claude/README.md) for
exactly where it goes.

> **Status:** v1.9.1 released (Revit 2025/2026/2027) — **easier on-ramp to Claude Assist: Settings works with no project open, standalone connection-guide exports, and a "Show me what you can do" guided demo.** (v1.9.1: the demo dialog now reminds first-time users to restart Claude Code after setup.)
> Settings now opens from a bare Revit session (Export/Import tabs enable the moment a project opens), the
> Claude Assist guide exports as a standalone `.md` so Claude Code can connect and drive Revit without a staged
> edit, a **Show me what you can do** button exports a scripted first demo (Claude builds a small shed on your
> go), Claude-running detection now sees Claude Code under any host via Transom's own MCP processes (with a "?"
> helper on the status row when it can't), and settings copy consistently says **Claude Code** so nobody tries
> the chat app.
>
> **Previously:** v1.8.0 closed the import **confirm gate** — format-mismatched edits (e.g. `2'6` for `2'-6"`)
> surface as pending rows and **Apply stays greyed until each is confirmed or discarded** — and broadened the
> option-2 "replace column with a new parameter" path (repoint other schedules to the new param, choose what
> happens to the old values); exports now write **directly** to the chosen file. v1.7.0 consolidated every Claude
> control into a single persisted **Claude Assist** toggle with an always-visible status checklist. v1.6.0–v1.6.1
> added the **extended bridge tools** (element/selection/view workflows a Claude client can drive
> directly, live-reviewed), plus a revision confirm step and anchor-pass hardening. v1.5.0 made
> **`execute_revit_code` work alongside other add-ins that load Roslyn** (isolated load context — no more
> `FileLoadException`).

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
element; material-takeoff, embedded, linked-element and non-itemized schedules export display-only.

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

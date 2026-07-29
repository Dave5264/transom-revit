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

### ⬇ [Download the installer](https://github.com/Dave5264/transom-revit/releases/download/v1.9.8/Transom-1.9.8-SingleUser.msi)

**One click, no admin rights** — installs into your per-user Revit add-ins folder.
Double-click the `.msi`, then start Revit. Supports **Revit 2025, 2026 & 2027**. Free.

</div>

Transom is an Autodesk Revit add-in that facilitates **schedule editing** and provides **integration with
Claude Code**. Most project schedules can be exported, edited anywhere, and imported back into the model
safely — and the export **color-codes every cell by what can actually be written back**, so you know before
you type. The awkward Revit cases are handled rather than excluded: type parameters, group headers,
non-itemized rows, and parameters on elements inside model groups each have a defined path back.

Both halves stand alone — the round-trip needs no Claude client, and the Claude layer works on any part of the
model, schedules or not. Neither needs administrator rights.

### Schedules — export, edit anywhere, import back

Export the schedules you tick (each becomes a sheet) to `.xlsx` / `.xls`, or `.csv` for a display-only copy.
The sheet looks like Revit does — merged headers, grouping, subtotals, fonts, colors, hidden columns,
calculated and combined fields, Revit's own row order — so you can edit it in Excel, in Sheets, or on a
machine with no Revit installed.

Bring it back through **Import → Preview → Apply**. The preview lists every change, what each one touches,
and which schedules are affected; format-mismatched edits (`2'6` for `2'-6"`) hold **Apply** greyed until you
confirm or discard them. What you apply is written in a **single transaction** — type parameters included —
and each value is re-read to verify. Cells Transom can't write are listed and skipped, never half-applied.

Every cell is colored by **what an edit to it will actually touch**, classified at export time from the live
model. White and green import directly — green meaning the value is shared beyond the row, like a type
parameter. Blue, yellow and red mark elements inside **model groups**, which Revit locks down: blue imports
cleanly, yellow and red need a decision first ([see below](#grouped-parameters--claude-assist)). Grey can't be
written at all — Revit computes or locks it. A model with no groups is only ever white, green and grey. The
legend below is on the Export tab itself, and its **More information…** dialog covers every color and every
import option in full.

<table>
<tr>
<td width="50%"><img src="docs/images/hub-export.jpg" alt="Transom Schedule Hub, Export tab. The cell-color legend reads: normal — instance parameter, edits the element(s) in that row, nothing else; green — type parameter (shared value), edits every element of the type or under the header; blue — project parameter in a model group, imports fine, each element keeps its own value; yellow — built-in data parameter in a model group, Transom asks how to apply it on import; red — geometry-driving parameter, may only be changed through group edit mode, requires Claude-Assist to automate; grey — not importable, Revit computes it or locks it. Below the legend is a checklist of the model's schedules." width="100%"></td>
<td width="50%"><img src="docs/images/hub-import-preview.jpg" alt="Transom Schedule Hub, Import tab: a preview listing 11 changes with element, field, old value, new value and scope, plus the schedules the import will change" width="100%"></td>
</tr>
<tr>
<td><em><b>Export</b> — tick the schedules to write out; the color legend sits above the list.</em></td>
<td><em><b>Import → Preview</b> — every change with its old and new value, the scope it will reach, and which schedules it touches. Rows on group members are flagged for a decision before anything is written.</em></td>
</tr>
</table>

### Claude Code integration

The integration layer connects the live Revit model to Claude through a **local bridge** — loopback only
(`127.0.0.1`), authenticated with a per-session token, bundled with the add-in. No separate install, no admin
rights, nothing leaves your machine.

Ask Claude Code for model work in plain language: cross-check a schedule, apply staged edits, create sheets
and views, run bulk edits, tag things. The in-process `execute_revit_code` tool gives it the **full Revit
API**, alongside ~35 purpose-built tools (views, elements, creation, MEP) each live-verified against a real
project model. A second server, `transom-ui-assist`, lets Claude **take over the Revit interface** where the
API has no path at all — above all **Edit Group mode** ([below](#grouped-parameters--claude-assist)).

A built-in **skill library** (Schedule Hub → Claude Skills) keeps reusable workflows as `.md` files in a
per-user folder, so they're available in **every project**: import your own, keep the ones Claude writes for
you, and **Stage** copies a skill's path to your clipboard to paste into Claude Code. Two ship with the
add-in — a read-only **schedule inventory** (a safe first thing to try) and **elevation door/window
tagging** that recognises visible openings optically, so ones hidden behind the facade never get tagged.

Turning it on, once:

1. Turn **Claude Assist** on in Transom Settings (Schedule Hub → Settings tab) — the first ON registers
   Transom's MCP servers and starts the bridge.
2. Restart Claude Code so it launches the shim and picks up the new servers.

<table>
<tr>
<td width="50%"><img src="docs/images/hub-settings-claude-assist.jpg" alt="Transom Schedule Hub, Settings tab: the Claude Assist toggle switched on, with a status checklist showing the bridge listening on 127.0.0.1:48810, the session token, the deployed MCP shim, both servers registered with Claude Code, and Claude connected" width="100%"></td>
<td width="50%"><img src="docs/images/hub-claude-skills.jpg" alt="Transom Schedule Hub, Claude Skills tab: the skill library listing elevation-door-window-tags and schedule-inventory, with Stage, Import and Remove buttons, the selected skill's description, and the live bridge status checklist" width="100%"></td>
</tr>
<tr>
<td><em><b>Settings</b> — one <b>Claude Assist</b> toggle, and a status checklist that shows every layer between Revit and Claude Code so a broken connection names itself.</em></td>
<td><em><b>Claude Skills</b> — the skill library: import your own, keep the ones Claude writes, and <b>Stage</b> one to paste straight into Claude Code.</em></td>
</tr>
</table>

Drop the guidance file from [`claude/`](claude/) (`CLAUDE.md`) into your project root or `~/.claude/CLAUDE.md`
so Claude Code already knows the tools and the safe-write workflow — see [`claude/README.md`](claude/README.md)
for exactly where it goes. Not sure what to ask for? **Show me what you can do** exports a demo script Claude
can run in a fresh project while you watch. The supported client is **Claude Code** (Claude Cowork runs in a
VM that can't reach a host-side bridge), and it should run with bypass permissions on — otherwise its
permission prompts steal focus from Revit and UI-assist clicks silently miss.

> **Status:** v1.9.8 (Revit 2025/2026/2027) — export now reports **data rows** instead of "element rows", so a
> type-anchored schedule (a door schedule grouped by Type Mark, say) no longer says "0 element rows" after a
> perfectly good export. The UI-Assist MCP server also ships expanded operating instructions, so every Claude
> session that drives Revit's interface starts knowing the window-size, unsigned-add-in and model-load pitfalls.
> Before that, v1.9.7 made Transom roughly **half the size on disk**. The three bundled helper
> executables each carried an uncompressed copy of the .NET runtime, and Roslyn's compiler messages shipped
> translated into 13 languages that nothing reads; compressing the single-file bundles and dropping the unused
> translations takes the payload from ~318 MB to ~163 MB per Revit version, with no functional change. Before
> that, v1.9.6 was a housekeeping release: the installer now lists **Transom** as
> the Publisher in Add/Remove Programs instead of the Windows username of whoever cut the build, the revision
> narrative no longer hard-codes a firm name into the project-number line (edit it in the confirm dialog), and
> the elevation-tagging skill discovers elevation sheets from the model instead of assuming an A300-series
> numbering convention. Before that, v1.9.5 shipped five fixes from live option-2 UI testing, and v1.9.2
> restored the Excel engine that had been dropped from the v1.8.0–v1.9.1 installers. Full history on the
> [releases page](https://github.com/Dave5264/transom-revit/releases).

### Grouped parameters — Claude-Assist

Parameters on elements inside a Revit **model group** are the case that stops most schedule tools — Transom's
blue, yellow and red cells. A project or shared *instance* parameter can vary per group instance, so Transom
enables that flag and writes it (blue). But a **built-in** parameter (Comments, Finish…) or a
**geometry-driving** one can only be changed from inside **Edit Group** mode; no API path exists at all. On
import, Transom asks per affected column:

- **New parameter (2a type / 2b instance)** — create one, repoint the schedule column onto it, and write your
  edits there. Nothing is ungrouped and the built-in keeps its old values underneath. Never offered for
  geometry-driving parameters, where a new parameter would change the schedule while the geometry stayed put.
- **Claude-Assist** — Transom stages a `.json` plus a step-by-step `.md` and doesn't touch the model itself;
  the Claude Code session already on the bridge enters Edit Group mode, sets the parameter in the Properties
  palette, finishes the group, then verifies the value and the member count. Works through excluded members,
  attached detail groups and nested groups.
- **Skip** — leave the column untouched, including its ungrouped elements.

An Edit Group edit lands on the **group definition**, so every instance of that group type gets the same
value; for per-instance values take 2b instead. Claude-Assist drives the live UI, so try it on a throwaway
model first, and never Synchronize with Central mid-run on a workshared model.

## Install

The **[per-user installer](https://github.com/Dave5264/transom-revit/releases/latest)** needs no
administrator rights:

1. Download **`Transom-…-SingleUser.msi`** from the [latest release](https://github.com/Dave5264/transom-revit/releases/latest).
2. Double-click it — it installs into `%AppData%\Autodesk\Revit\Addins\` for the current user only.
3. Launch Revit; Transom is on the ribbon. To remove it later: *Apps & features → Transom*.

**Always use the SingleUser installer** — Claude-Assist's install-time setup only runs there. A machine-wide
**MultiUser** MSI is built from this codebase for **IT / firm-wide deployment**, but deliberately isn't linked
here: it requires admin rights and, running as `SYSTEM`, defers the MCP shim to Revit's first launch. Build it
from source if you genuinely need it.

<details>
<summary><b>Building from source</b> — targets, build commands, repo layout</summary>

| Revit | Runtime | Build configuration |
|-------|---------|---------------------|
| 2025  | .NET 8  | `Debug.R25` / `Release.R25` |
| 2026  | .NET 8  | `Debug.R26` / `Release.R26` |
| 2027  | .NET 10 | `Debug.R27` / `Release.R27` |

Built on the [Nice3point Revit SDK](https://github.com/Nice3point/RevitTemplates) (multi-version +
dynamic-loading isolation); Excel via [NPOI](https://github.com/nissl-lab/npoi).

```shell
dotnet build source/Transom/Transom.csproj -c Debug.R25   # Revit 2025 (.NET 8)
dotnet build source/Transom/Transom.csproj -c Debug.R27   # Revit 2027 (.NET 10)
```

A successful build deploys the add-in to `%AppData%\Autodesk\Revit\Addins\<version>\`. The MSI installer and
all-versions build run through the `build/` ModularPipelines project (`cd build; dotnet run -- pack`).

| Path | Description |
|------|-------------|
| `source/Transom/` | the add-in (commands, views, view-models, core logic) |
| `build/`, `install/` | ModularPipelines build + WiX installer |
| `branding/` | ribbon icon + generator |
| `docs/` | `design-notes/` (shipped-fix rationale + Revit-API research notes), `parity-tool-status.md` (bridge-tool review state), `images/` (screenshots on this page) |
| `tools/` | standalone dev tools (`transom_verify.py`) |

</details>

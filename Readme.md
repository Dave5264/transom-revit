<div align="center">

# Transom

**Round-trip Revit schedules through spreadsheets — with full visual fidelity and type-parameter–safe write-back.**

</div>

Transom is an Autodesk Revit add-in (C#) that exports schedules to `.xlsx` / `.csv` / `.xls` exactly as Revit
displays them — merged headers, grouping, subtotals, fonts, colors, hidden columns — and imports edited values
back into the model, **including type parameters**, safely and inside a single transaction. An optional
Claude-assisted QA layer can reconcile exports and pre-flight imports against the live model over a local MCP
bridge, but the add-in is fully standalone without it.

> **Status:** scaffolding complete; core export/import in progress. Requirements are locked in
> [`SPEC.md`](SPEC.md); the build approach is in [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md).

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

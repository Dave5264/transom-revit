---
name: schedule-inventory
description: Read-only survey of every schedule in the open model — name, column list, and row count — reported as a compact markdown table. A safe first skill to try; changes nothing.
---

# Schedule inventory

Produce a read-only inventory of every schedule in the open Revit model, using Transom's `transom` bridge
tools. This skill changes nothing — no writes, no transactions.

## Steps

1. Call `mcp__transom__status` and confirm `ok: true`. Tell the user which document you're connected to.
   If the tools are missing or status fails, stop and point the user to Transom's Settings tab (Claude
   Assist must be on, Revit open, and Claude Code restarted after first setup).
2. Call `mcp__transom__list_schedules` for the full schedule list.
3. For each schedule, call `mcp__transom__read_schedule` and note: the schedule name, its column headings
   (in order), and the number of data rows.
4. Report one markdown table: **Schedule | Columns | Rows**, sorted by schedule name. Keep the column list
   compact (join headings with ", "). After the table, flag anything notable — empty schedules, duplicate
   names, or schedules that failed to read (include the error).

## Rules

- Read-only: do not call any tool that writes (`set_parameter`, `execute_revit_code` with a transaction,
  create/modify/delete tools).
- If the model has more than ~30 schedules, ask the user whether they want all of them or a subset before
  reading every one.

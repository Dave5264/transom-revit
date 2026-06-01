using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Transom.Core;

public sealed class ProposedChange
{
    public string UniqueId = "";
    public long TypeId;
    public int ParameterId;
    public string Binding = "instance";

    /// <summary>When set, this is a bulk instance write: NewValue is applied to each of these instance UniqueIds.</summary>
    public List<string>? BulkInstanceIds;

    public bool Selected { get; set; } = true;
    public string ElementName { get; set; } = "";
    public string Field { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public int InstancesAffected { get; set; } = 1;
    public string Scope => BulkInstanceIds != null ? $"all {InstancesAffected} inst"
        : Binding == "type" ? $"type · {InstancesAffected} inst" : "instance";

    public bool IsString;
    public string NewString = "";
    public double NewDouble;
}

public sealed class SkippedItem
{
    public string Reason { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class ConflictOption
{
    public string Display = "";
    public bool IsString;
    public string NewString = "";
    public double NewDouble;
    public bool Parseable = true;
}

public sealed class TypeConflict
{
    public long TypeId;
    public int ParameterId;
    public string Field = "";
    public string TypeName = "";
    public string CurrentDisplay = "";
    public int InstancesAffected;
    public List<ConflictOption> Options = new();
}

/// <summary>A flagged cell for the colour-coded import report. Severity: "red" (can't write) or "yellow" (changed since export).</summary>
public sealed class CellDiagnostic
{
    public string SheetTabName = "";
    public int ExcelRow;
    public int Col;
    public string FieldName = "";
    public string ElementLabel = "";
    public string Severity = "";   // red | yellow
    public string Reason = "";
    public string Value = "";
}

public sealed class ChangeSet
{
    public string ScheduleName = "";
    public bool CrossModel;
    public List<ProposedChange> Changes = new();
    public List<SkippedItem> Skipped = new();
    public List<TypeConflict> Conflicts = new();
    public List<CellDiagnostic> Diagnostics = new();
    public string ReportPath = "";
}

/// <summary>
///     Diffs an imported workbook against the live model (read-only) into a change set + diagnostics, and
///     applies a confirmed change set in one transaction. Uses a three-way compare (exported baseline vs
///     current model vs spreadsheet): only cells you actually edited (spreadsheet ≠ baseline) become writes;
///     model drift (current ≠ baseline) is flagged "changed since export"; unwritable cells are flagged red.
/// </summary>
public sealed class Importer
{
    private sealed class TypeCandidate
    {
        public ImportColumn Col = null!;
        public string SheetTab = "";
        public string TypeName = "";
        public bool IsString;
        public string? SpecTypeId;
        public string CurString = "";
        public double CurDouble;
        public string CurDisplay = "";
        public readonly List<(int excelRow, string value, string label)> Cells = new();
    }

    public ChangeSet BuildChangeSet(Document doc, ImportWorkbook wb)
    {
        var cs = new ChangeSet { CrossModel = wb.SourceModelGuid != doc.CreationGUID.ToString() };
        var units = doc.GetUnits();

        foreach (var sheet in wb.Sheets)
        {
            cs.ScheduleName = sheet.ScheduleName;
            if (!sheet.RoundTrippable)
            {
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "display-only schedule",
                    Detail = $"{sheet.ScheduleName} — not itemized, so rows don't map to individual elements and " +
                             "values can't be written back. Turn on 'Itemize every instance' (the schedule's " +
                             "Sorting/Grouping tab) and re-export to round-trip.",
                });
                continue;
            }

            foreach (var unmatched in sheet.Columns.Where(c => c.Writable && !c.Matched))
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "column not in spreadsheet",
                    Detail = $"'{(string.IsNullOrEmpty(unmatched.Header) ? unmatched.FieldName : unmatched.Header)}' (renamed or removed)",
                });

            var typeGroups = new Dictionary<(long, int), TypeCandidate>();

            foreach (var row in sheet.Rows)
            {
                if (row.Kind == "type")
                {
                    sheet.Baseline.TryGetValue(row.UniqueId, out var baseRowT);
                    sheet.RowBindings.TryGetValue(row.UniqueId, out var rbT);
                    HandleTypeRow(doc, sheet, row, cs, typeGroups, units, rbT, baseRowT);
                    continue;
                }

                var label = row.Cells.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? row.UniqueId;
                var el = doc.GetElement(row.UniqueId);
                if (el == null)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "element deleted", Detail = label });
                    foreach (var col in sheet.Columns.Where(c => c.Writable && c.Matched && c.ExcelCol < row.Cells.Length))
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "element no longer exists", row.Cells[col.ExcelCol]));
                    continue;
                }

                sheet.Baseline.TryGetValue(row.UniqueId, out var baseRow);
                sheet.RowBindings.TryGetValue(row.UniqueId, out var rowBindings);

                foreach (var col in sheet.Columns)
                {
                    if (!col.Writable || !col.Matched || col.ExcelCol >= row.Cells.Length) continue;
                    var cellText = row.Cells[col.ExcelCol] ?? "";
                    var baseline = baseRow != null && baseRow.TryGetValue(col.Col, out var bv) ? bv : null;

                    // Per-element binding wins (a shared param can be instance on one family, type on
                    // another within a multi-category schedule); fall back to the schedule-level field type.
                    var binding = rowBindings != null && rowBindings.TryGetValue(col.Col, out var rbv)
                        ? rbv : col.Binding;

                    var host = binding == "type"
                        ? (el.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(el.GetTypeId()) : null)
                        : binding == "instance" ? el : null;
                    var param = host == null ? null : GetParam(host, col.ParameterId);

                    if (param == null)
                    {
                        if (baseline == null || cellText != baseline)
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "parameter not found", Detail = $"{col.FieldName} ({label})" });
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter not found", cellText));
                        }
                        continue;
                    }

                    var current = CurrentDisplay(param);
                    bool edited = baseline != null ? cellText != baseline : cellText != current;

                    if (param.IsReadOnly)
                    {
                        if (edited)
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "read-only", Detail = $"{col.FieldName} ({label})" });
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter is read-only", cellText));
                        }
                        continue;
                    }

                    // Drift: current model value differs from what was exported (only knowable with a baseline).
                    if (baseline != null && Drifted(param, baseline, units, col.SpecTypeId))
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                            $"changed since export (was '{baseline}', now '{current}')", cellText));

                    if (!edited) continue; // not edited -> no write (never revert model drift)

                    if (param.StorageType == StorageType.String)
                    {
                        if (binding == "type")
                            RecordType(typeGroups, sheet, col, host!, param, label, row.ExcelRow, cellText);
                        else if ((param.AsString() ?? "") != cellText)
                            cs.Changes.Add(InstanceChange(el, col, param.AsString() ?? "", cellText, true, cellText, 0));
                    }
                    else if (param.StorageType == StorageType.Double && col.SpecTypeId != null)
                    {
                        if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out double parsed))
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                            continue;
                        }
                        if (binding == "type")
                            RecordType(typeGroups, sheet, col, host!, param, label, row.ExcelRow, cellText);
                        else if (Math.Abs(param.AsDouble() - parsed) >= 1e-9)
                            cs.Changes.Add(InstanceChange(el, col, param.AsValueString() ?? "", cellText, false, "", parsed));
                    }
                    else
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "unsupported parameter type", Detail = $"{col.FieldName} ({label})" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "unsupported parameter type", cellText));
                    }
                }
            }

            ResolveTypeGroups(cs, typeGroups, units);
        }

        return cs;
    }

    /// <summary>
    ///     Grouped schedules: the row maps to a type. Type-parameter edits write once to the type (via the
    ///     shared conflict-grouping path); instance-parameter edits bulk-apply the value to every instance the
    ///     row represents (the list captured at export), with deleted/unwritable instances flagged.
    /// </summary>
    private void HandleTypeRow(Document doc, ImportSheet sheet, ImportRow row, ChangeSet cs,
        Dictionary<(long, int), TypeCandidate> typeGroups, Units units,
        Dictionary<int, string>? rowBindings, Dictionary<int, string>? baseRow)
    {
        var label = row.Cells.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? row.UniqueId;
        var typeEl = doc.GetElement(row.UniqueId);
        if (typeEl == null)
        {
            cs.Skipped.Add(new SkippedItem { Reason = "type deleted", Detail = label });
            foreach (var col in sheet.Columns.Where(c => c.Writable && c.Matched && c.ExcelCol < row.Cells.Length))
                cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "type no longer exists", row.Cells[col.ExcelCol]));
            return;
        }

        foreach (var col in sheet.Columns)
        {
            if (!col.Writable || !col.Matched || col.ExcelCol >= row.Cells.Length) continue;
            var cellText = row.Cells[col.ExcelCol] ?? "";
            var baseline = baseRow != null && baseRow.TryGetValue(col.Col, out var bv) ? bv : null;
            var binding = rowBindings != null && rowBindings.TryGetValue(col.Col, out var rbv) ? rbv : col.Binding;

            if (binding == "type")
            {
                var param = GetParam(typeEl, col.ParameterId);
                if (param == null)
                {
                    if (baseline == null || cellText != baseline)
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "parameter not found", Detail = $"{col.FieldName} ({label})" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter not found", cellText));
                    }
                    continue;
                }
                var current = CurrentDisplay(param);
                bool edited = baseline != null ? cellText != baseline : cellText != current;
                if (param.IsReadOnly)
                {
                    if (edited)
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "read-only", Detail = $"{col.FieldName} ({label})" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter is read-only", cellText));
                    }
                    continue;
                }
                if (baseline != null && Drifted(param, baseline, units, col.SpecTypeId))
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                        $"changed since export (was '{baseline}', now '{current}')", cellText));
                if (!edited) continue;

                if (param.StorageType == StorageType.String)
                    RecordType(typeGroups, sheet, col, typeEl, param, label, row.ExcelRow, cellText);
                else if (param.StorageType == StorageType.Double && col.SpecTypeId != null)
                {
                    if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out _))
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                        continue;
                    }
                    RecordType(typeGroups, sheet, col, typeEl, param, label, row.ExcelRow, cellText);
                }
                else
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "unsupported parameter type", Detail = $"{col.FieldName} ({label})" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "unsupported parameter type", cellText));
                }
            }
            else if (binding == "instance")
            {
                bool edited = baseline != null ? cellText != baseline : !string.IsNullOrEmpty(cellText);
                if (!edited) continue;

                if (row.InstanceIds == null || row.InstanceIds.Count == 0)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "ambiguous instance scope", Detail = $"{col.FieldName} ({label}) — type spans multiple rows" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "instance scope ambiguous (type spans rows)", cellText));
                    continue;
                }

                // Resolve the live instances; a representative drives storage-type / read-only checks.
                var ids = row.InstanceIds.Where(uid => doc.GetElement(uid) != null).ToList();
                int missing = row.InstanceIds.Count - ids.Count;
                if (missing > 0)
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                        $"{missing} of {row.InstanceIds.Count} instance(s) no longer exist", cellText));
                if (ids.Count == 0)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "all instances deleted", Detail = $"{col.FieldName} ({label})" });
                    continue;
                }

                var repr = doc.GetElement(ids[0]);
                var rparam = repr == null ? null : GetParam(repr, col.ParameterId);
                if (rparam == null)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "parameter not found", Detail = $"{col.FieldName} ({label})" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter not found", cellText));
                    continue;
                }
                if (rparam.IsReadOnly)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "read-only", Detail = $"{col.FieldName} ({label})" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter is read-only", cellText));
                    continue;
                }

                var oldDisp = string.IsNullOrEmpty(baseline) ? "(varies)" : baseline;
                if (rparam.StorageType == StorageType.String)
                    cs.Changes.Add(BulkChange(typeEl, col, ids, oldDisp, cellText, true, cellText, 0));
                else if (rparam.StorageType == StorageType.Double && col.SpecTypeId != null)
                {
                    if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out double parsed))
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                        continue;
                    }
                    cs.Changes.Add(BulkChange(typeEl, col, ids, oldDisp, cellText, false, "", parsed));
                }
                else
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "unsupported parameter type", Detail = $"{col.FieldName} ({label})" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "unsupported parameter type", cellText));
                }
            }
            // binding == "none" -> parameter lives on neither host for this type; nothing to write.
        }
    }

    private static ProposedChange BulkChange(Element typeEl, ImportColumn col, List<string> instanceIds,
        string oldDisp, string newDisp, bool isString, string str, double dbl) => new()
    {
        ParameterId = col.ParameterId, Binding = "instance", BulkInstanceIds = instanceIds,
        ElementName = SafeName(typeEl), Field = col.FieldName, OldValue = oldDisp, NewValue = newDisp,
        InstancesAffected = instanceIds.Count, IsString = isString, NewString = str, NewDouble = dbl,
    };

    private static void RecordType(Dictionary<(long, int), TypeCandidate> typeGroups, ImportSheet sheet,
        ImportColumn col, Element host, Parameter param, string label, int excelRow, string cellText)
    {
        var key = (host.Id.Value, col.ParameterId);
        if (!typeGroups.TryGetValue(key, out var tc))
        {
            tc = new TypeCandidate { Col = col, SheetTab = sheet.SheetTabName, TypeName = SafeName(host), SpecTypeId = col.SpecTypeId };
            if (param.StorageType == StorageType.String)
            {
                tc.IsString = true;
                tc.CurString = param.AsString() ?? "";
                tc.CurDisplay = tc.CurString;
            }
            else
            {
                tc.IsString = false;
                tc.CurDouble = param.AsDouble();
                tc.CurDisplay = param.AsValueString() ?? "";
            }
            typeGroups[key] = tc;
        }
        tc.Cells.Add((excelRow, cellText, label));
    }

    private static void ResolveTypeGroups(ChangeSet cs, Dictionary<(long, int), TypeCandidate> typeGroups, Units units)
    {
        foreach (var kv in typeGroups)
        {
            var tc = kv.Value;
            var distinct = tc.Cells.Select(c => c.value).Distinct().ToList();
            if (distinct.Count > 1)
            {
                var conflict = new TypeConflict
                {
                    TypeId = kv.Key.Item1,
                    ParameterId = kv.Key.Item2,
                    Field = tc.Col.FieldName,
                    TypeName = tc.TypeName,
                    CurrentDisplay = tc.CurDisplay,
                    InstancesAffected = tc.Cells.Count,
                };
                foreach (var v in distinct)
                {
                    var opt = new ConflictOption { Display = v, IsString = tc.IsString, NewString = v };
                    if (!tc.IsString)
                    {
                        opt.Parseable = UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId!), v, out double d);
                        opt.NewDouble = opt.Parseable ? d : 0;
                    }
                    conflict.Options.Add(opt);
                }
                cs.Conflicts.Add(conflict);
                foreach (var cell in tc.Cells)
                    cs.Diagnostics.Add(new CellDiagnostic
                    {
                        SheetTabName = tc.SheetTab, ExcelRow = cell.excelRow, Col = tc.Col.ExcelCol, FieldName = tc.Col.FieldName,
                        ElementLabel = cell.label, Severity = "red", Reason = "type conflict — pick a value", Value = cell.value,
                    });
                continue;
            }

            var value = distinct[0];
            if (tc.IsString)
            {
                if (value == tc.CurString) continue;
                cs.Changes.Add(TypeChange(kv.Key, tc, value, true, value, 0));
            }
            else
            {
                if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId!), value, out double parsed))
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{tc.Col.FieldName} = '{value}'" });
                    continue;
                }
                if (Math.Abs(parsed - tc.CurDouble) < 1e-9) continue;
                cs.Changes.Add(TypeChange(kv.Key, tc, value, false, "", parsed));
            }
        }
    }

    public static ProposedChange ResolveToChange(TypeConflict c, ConflictOption opt) => new()
    {
        TypeId = c.TypeId, ParameterId = c.ParameterId, Binding = "type", ElementName = c.TypeName,
        Field = c.Field, OldValue = c.CurrentDisplay, NewValue = opt.Display, InstancesAffected = c.InstancesAffected,
        IsString = opt.IsString, NewString = opt.NewString, NewDouble = opt.NewDouble,
    };

    public string Apply(Document doc, ChangeSet cs)
    {
        int applied = 0, failed = 0, unverified = 0;
        using var tx = new Transaction(doc, "Transom: import edits");
        tx.Start();
        try
        {
            foreach (var ch in cs.Changes)
            {
                // Bulk instance write (grouped schedule): apply to every instance the row represented.
                if (ch.BulkInstanceIds != null)
                {
                    foreach (var uid in ch.BulkInstanceIds)
                    {
                        var inst = doc.GetElement(uid);
                        var ip = inst == null ? null : GetParam(inst, ch.ParameterId);
                        if (ip == null || ip.IsReadOnly) { failed++; continue; }
                        bool iok = ch.IsString ? ip.Set(ch.NewString) : ip.Set(ch.NewDouble);
                        if (!iok) { failed++; continue; }
                        if (!VerifyWrite(ip, ch)) { unverified++; continue; }
                        applied++;
                    }
                    continue;
                }

                var host = ch.Binding == "type" ? doc.GetElement(new ElementId(ch.TypeId)) : doc.GetElement(ch.UniqueId);
                var param = host == null ? null : GetParam(host, ch.ParameterId);
                if (param == null || param.IsReadOnly) { failed++; continue; }
                bool ok = ch.IsString ? param.Set(ch.NewString) : param.Set(ch.NewDouble);
                if (!ok) { failed++; continue; }

                // H3: re-read the parameter and confirm the value actually landed (a Set can return
                // true yet be coerced/clamped, or silently no-op on some derived parameters).
                if (!VerifyWrite(param, ch)) { unverified++; continue; }
                applied++;
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            tx.RollBack();
            return "Apply failed (rolled back): " + ex.Message;
        }

        var msg = $"Applied {applied} change(s)";
        if (failed > 0) msg += $", {failed} failed";
        if (unverified > 0) msg += $", {unverified} unverified (value didn't take)";
        return msg + $". {cs.Skipped.Count} skipped.";
    }

    /// <summary>Re-reads a just-written parameter to confirm the new value persisted (within unit tolerance).</summary>
    private static bool VerifyWrite(Parameter param, ProposedChange ch)
    {
        try
        {
            if (param.StorageType == StorageType.String)
                return (param.AsString() ?? "") == (ch.NewString ?? "");
            if (param.StorageType == StorageType.Double)
                return Math.Abs(param.AsDouble() - ch.NewDouble) <= 1e-6;
            return true; // other storage types aren't written by Transom
        }
        catch { return false; }
    }

    // --- helpers ---

    private static ProposedChange InstanceChange(Element el, ImportColumn col, string oldDisp, string newDisp,
        bool isString, string str, double dbl) => new()
    {
        UniqueId = el.UniqueId, ParameterId = col.ParameterId, Binding = "instance", ElementName = SafeName(el),
        Field = col.FieldName, OldValue = oldDisp, NewValue = newDisp, IsString = isString, NewString = str, NewDouble = dbl,
    };

    private static ProposedChange TypeChange((long, int) key, TypeCandidate tc, string newDisp,
        bool isString, string str, double dbl) => new()
    {
        TypeId = key.Item1, ParameterId = key.Item2, Binding = "type", ElementName = tc.TypeName,
        Field = tc.Col.FieldName, OldValue = tc.CurDisplay, NewValue = newDisp, InstancesAffected = tc.Cells.Count,
        IsString = isString, NewString = str, NewDouble = dbl,
    };

    private static CellDiagnostic Diag(ImportSheet sheet, ImportRow row, ImportColumn col, string label,
        string severity, string reason, string value) => new()
    {
        SheetTabName = sheet.SheetTabName, ExcelRow = row.ExcelRow, Col = col.ExcelCol, FieldName = col.FieldName,
        ElementLabel = label, Severity = severity, Reason = reason, Value = value,
    };

    private static string CurrentDisplay(Parameter param) =>
        param.StorageType == StorageType.String ? (param.AsString() ?? "") : (param.AsValueString() ?? "");

    private static bool Drifted(Parameter param, string baseline, Units units, string? spec)
    {
        if (param.StorageType == StorageType.String)
            return (param.AsString() ?? "") != baseline;
        if (param.StorageType == StorageType.Double && spec != null)
        {
            if (UnitFormatUtils.TryParse(units, new ForgeTypeId(spec), baseline, out double b))
                return Math.Abs(param.AsDouble() - b) > 1e-9;
            return (param.AsValueString() ?? "") != baseline;
        }
        return false;
    }

    private static Parameter? GetParam(Element host, int parameterId)
    {
        if (parameterId < 0)
            return host.get_Parameter((BuiltInParameter)parameterId);
        foreach (Parameter p in host.Parameters)
            if (p.Id.Value == parameterId) return p;
        return null;
    }

    private static string SafeName(Element e)
    {
        try { return e.Name; }
        catch { return e.Id.ToString(); }
    }
}

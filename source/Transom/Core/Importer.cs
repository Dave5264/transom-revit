using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Transom.Core;

public sealed class ProposedChange
{
    public string UniqueId = "";   // instance writes
    public long TypeId;            // type writes (0 = instance)
    public int ParameterId;
    public string Binding = "instance";

    // Bound in the preview grid -> must be properties.
    public bool Selected { get; set; } = true;
    public string ElementName { get; set; } = "";
    public string Field { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public int InstancesAffected { get; set; } = 1;
    public string Scope => Binding == "type" ? $"type · {InstancesAffected} inst" : "instance";

    public bool IsString;
    public string NewString = "";
    public double NewDouble;
}

public sealed class SkippedItem
{
    public string Reason { get; set; } = "";   // unparseable | conflict | unmatched | notRoundtrippable
    public string Detail { get; set; } = "";
}

/// <summary>One candidate value for a type-parameter conflict (pre-parsed in API context).</summary>
public sealed class ConflictOption
{
    public string Display = "";
    public bool IsString;
    public string NewString = "";
    public double NewDouble;
    public bool Parseable = true; // false when a measurable value couldn't be parsed
}

/// <summary>
///     A type parameter whose rows disagree. Surfaced for interactive resolution (pick one value to
///     apply to all instances of the type) rather than silently skipped.
/// </summary>
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

public sealed class ChangeSet
{
    public string ScheduleName = "";
    public bool CrossModel;
    public List<ProposedChange> Changes = new();
    public List<SkippedItem> Skipped = new();
    public List<TypeConflict> Conflicts = new();
}

/// <summary>
///     Diffs an imported workbook against the live model (read-only) to produce a change set, and applies
///     a confirmed change set inside one transaction. Type params are conflict-checked across ALL rows of
///     a type (not just edited ones) and written once.
/// </summary>
public sealed class Importer
{
    private sealed class TypeCandidate
    {
        public ImportColumn Col = null!;
        public string TypeName = "";
        public bool IsString;
        public string? SpecTypeId;
        public string CurString = "";
        public double CurDouble;
        public string CurDisplay = "";
        public readonly List<string> CellValues = new(); // every instance row's cell for this type+param
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
                cs.Skipped.Add(new SkippedItem { Reason = "notRoundtrippable", Detail = sheet.ScheduleName });
                continue;
            }

            var typeGroups = new Dictionary<(long, int), TypeCandidate>();

            foreach (var row in sheet.Rows)
            {
                var el = doc.GetElement(row.UniqueId);
                if (el == null)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "unmatched", Detail = row.UniqueId });
                    continue;
                }

                foreach (var col in sheet.Columns)
                {
                    if (!col.Writable || col.Col >= row.Cells.Length) continue;
                    var cellText = row.Cells[col.Col] ?? "";

                    var host = col.Binding == "type"
                        ? (el.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(el.GetTypeId()) : null)
                        : el;
                    if (host == null) continue;

                    var param = GetParam(host, col.ParameterId);
                    if (param == null || param.IsReadOnly) continue; // read-only (e.g. computed Length) skipped silently

                    bool measurableDouble = param.StorageType == StorageType.Double && col.SpecTypeId != null;
                    if (param.StorageType != StorageType.String && !measurableDouble) continue; // unsupported storage

                    if (col.Binding == "type")
                    {
                        var key = (host.Id.Value, col.ParameterId);
                        if (!typeGroups.TryGetValue(key, out var tc))
                        {
                            tc = new TypeCandidate { Col = col, TypeName = SafeName(host), SpecTypeId = col.SpecTypeId };
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
                        tc.CellValues.Add(cellText); // record EVERY row, changed or not
                        continue;
                    }

                    // instance binding
                    if (param.StorageType == StorageType.String)
                    {
                        var cur = param.AsString() ?? "";
                        if (cur == cellText) continue;
                        cs.Changes.Add(InstanceChange(el, col, cur, cellText, isString: true, str: cellText, dbl: 0));
                    }
                    else
                    {
                        if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId!), cellText, out double parsed))
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                            continue;
                        }
                        if (Math.Abs(param.AsDouble() - parsed) < 1e-9) continue;
                        cs.Changes.Add(InstanceChange(el, col, param.AsValueString() ?? "", cellText, isString: false, str: "", dbl: parsed));
                    }
                }
            }

            ResolveTypeGroups(cs, typeGroups, units);
        }

        return cs;
    }

    private static void ResolveTypeGroups(ChangeSet cs, Dictionary<(long, int), TypeCandidate> typeGroups, Units units)
    {
        foreach (var kv in typeGroups)
        {
            var tc = kv.Value;
            var distinct = tc.CellValues.Distinct().ToList();
            if (distinct.Count > 1)
            {
                // Disagreement -> surface for interactive resolution (pre-parse each candidate now).
                var conflict = new TypeConflict
                {
                    TypeId = kv.Key.Item1,
                    ParameterId = kv.Key.Item2,
                    Field = tc.Col.FieldName,
                    TypeName = tc.TypeName,
                    CurrentDisplay = tc.CurDisplay,
                    InstancesAffected = tc.CellValues.Count,
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
                continue;
            }

            var value = distinct.Count == 1 ? distinct[0] : tc.CurDisplay;

            if (tc.IsString)
            {
                if (value == tc.CurString) continue; // no change
                cs.Changes.Add(TypeChange(kv.Key, tc, value, isString: true, str: value, dbl: 0));
            }
            else
            {
                if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId!), value, out double parsed))
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{tc.Col.FieldName} = '{value}'" });
                    continue;
                }
                if (Math.Abs(parsed - tc.CurDouble) < 1e-9) continue; // no change
                cs.Changes.Add(TypeChange(kv.Key, tc, value, isString: false, str: "", dbl: parsed));
            }
        }
    }

    private static ProposedChange InstanceChange(Element el, ImportColumn col, string oldDisp, string newDisp,
        bool isString, string str, double dbl) => new()
    {
        UniqueId = el.UniqueId,
        ParameterId = col.ParameterId,
        Binding = "instance",
        ElementName = SafeName(el),
        Field = col.FieldName,
        OldValue = oldDisp,
        NewValue = newDisp,
        IsString = isString,
        NewString = str,
        NewDouble = dbl,
    };

    private static ProposedChange TypeChange((long, int) key, TypeCandidate tc, string newDisp,
        bool isString, string str, double dbl) => new()
    {
        TypeId = key.Item1,
        ParameterId = key.Item2,
        Binding = "type",
        ElementName = tc.TypeName,
        Field = tc.Col.FieldName,
        OldValue = tc.CurDisplay,
        NewValue = newDisp,
        InstancesAffected = tc.CellValues.Count,
        IsString = isString,
        NewString = str,
        NewDouble = dbl,
    };

    /// <summary>Builds a type-write change from a user-resolved conflict choice.</summary>
    public static ProposedChange ResolveToChange(TypeConflict c, ConflictOption opt) => new()
    {
        TypeId = c.TypeId,
        ParameterId = c.ParameterId,
        Binding = "type",
        ElementName = c.TypeName,
        Field = c.Field,
        OldValue = c.CurrentDisplay,
        NewValue = opt.Display,
        InstancesAffected = c.InstancesAffected,
        IsString = opt.IsString,
        NewString = opt.NewString,
        NewDouble = opt.NewDouble,
    };

    /// <summary>Applies a confirmed change set inside one transaction. Returns a summary line.</summary>
    public string Apply(Document doc, ChangeSet cs)
    {
        int applied = 0, failed = 0;
        using var tx = new Transaction(doc, "Transom: import edits");
        tx.Start();
        try
        {
            foreach (var ch in cs.Changes)
            {
                var host = ch.Binding == "type"
                    ? doc.GetElement(new ElementId(ch.TypeId))
                    : doc.GetElement(ch.UniqueId);
                var param = host == null ? null : GetParam(host, ch.ParameterId);
                if (param == null || param.IsReadOnly) { failed++; continue; }

                bool ok = ch.IsString ? param.Set(ch.NewString) : param.Set(ch.NewDouble);
                if (ok) applied++; else failed++;
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            tx.RollBack();
            return "Apply failed (rolled back): " + ex.Message;
        }

        return $"Applied {applied} change(s)" + (failed > 0 ? $", {failed} failed" : "") +
               $". {cs.Skipped.Count} skipped.";
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

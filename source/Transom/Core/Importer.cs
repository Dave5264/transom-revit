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
    public string ElementName = "";
    public string Field = "";
    public string OldValue = "";
    public string NewValue = "";
    public int InstancesAffected = 1;

    public bool IsString;
    public string NewString = "";
    public double NewDouble;
}

public sealed class SkippedItem
{
    public string Reason = "";   // unparseable | conflict | unmatched | notRoundtrippable
    public string Detail = "";
}

public sealed class ChangeSet
{
    public string ScheduleName = "";
    public bool CrossModel;
    public List<ProposedChange> Changes = new();
    public List<SkippedItem> Skipped = new();
}

/// <summary>
///     Diffs an imported workbook against the live model (read-only) to produce a change set, and applies
///     a confirmed change set inside one transaction. Type params are conflict-checked and written once.
/// </summary>
public sealed class Importer
{
    private sealed class TypeCandidate
    {
        public ImportColumn Col = null!;
        public string TypeName = "";
        public string OldDisplay = "";
        public readonly List<string> NewDisplays = new();
        public bool IsString;
        public string NewString = "";
        public double NewDouble;
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

                    if (param.StorageType == StorageType.String)
                    {
                        var cur = param.AsString() ?? "";
                        if (cur == cellText) continue;
                        Record(cs, typeGroups, col, host, el, isString: true,
                            newString: cellText, newDouble: 0, oldDisplay: cur, newDisplay: cellText);
                    }
                    else if (param.StorageType == StorageType.Double && col.SpecTypeId != null)
                    {
                        if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out double parsed))
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                            continue;
                        }
                        if (Math.Abs(param.AsDouble() - parsed) < 1e-9) continue;
                        Record(cs, typeGroups, col, host, el, isString: false,
                            newString: "", newDouble: parsed, oldDisplay: param.AsValueString() ?? "", newDisplay: cellText);
                    }
                    // other storage types (ElementId/Integer) not handled in this slice
                }
            }

            // Resolve type-param groups: conflicting values are skipped; consistent ones become one write.
            foreach (var kv in typeGroups)
            {
                var tc = kv.Value;
                var distinct = tc.NewDisplays.Distinct().ToList();
                if (distinct.Count > 1)
                {
                    cs.Skipped.Add(new SkippedItem
                    {
                        Reason = "conflict",
                        Detail = $"{tc.Col.FieldName} on type '{tc.TypeName}': {string.Join(" / ", distinct)}",
                    });
                    continue;
                }
                cs.Changes.Add(new ProposedChange
                {
                    TypeId = kv.Key.Item1,
                    ParameterId = kv.Key.Item2,
                    Binding = "type",
                    ElementName = tc.TypeName,
                    Field = tc.Col.FieldName,
                    OldValue = tc.OldDisplay,
                    NewValue = distinct[0],
                    InstancesAffected = tc.NewDisplays.Count,
                    IsString = tc.IsString,
                    NewString = tc.NewString,
                    NewDouble = tc.NewDouble,
                });
            }
        }

        return cs;
    }

    private static void Record(ChangeSet cs, Dictionary<(long, int), TypeCandidate> typeGroups,
        ImportColumn col, Element host, Element instance,
        bool isString, string newString, double newDouble, string oldDisplay, string newDisplay)
    {
        if (col.Binding == "type")
        {
            var key = (host.Id.Value, col.ParameterId);
            if (!typeGroups.TryGetValue(key, out var tc))
            {
                tc = new TypeCandidate
                {
                    Col = col, TypeName = SafeName(host), OldDisplay = oldDisplay,
                    IsString = isString, NewString = newString, NewDouble = newDouble,
                };
                typeGroups[key] = tc;
            }
            tc.NewDisplays.Add(newDisplay);
            tc.NewString = newString;
            tc.NewDouble = newDouble;
        }
        else
        {
            cs.Changes.Add(new ProposedChange
            {
                UniqueId = instance.UniqueId,
                ParameterId = col.ParameterId,
                Binding = "instance",
                ElementName = SafeName(instance),
                Field = col.FieldName,
                OldValue = oldDisplay,
                NewValue = newDisplay,
                IsString = isString,
                NewString = newString,
                NewDouble = newDouble,
            });
        }
    }

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

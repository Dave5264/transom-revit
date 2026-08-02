using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Shared plumbing for the extended bridge tools (BridgeToolsViews / BridgeToolsElements /
///     BridgeToolsCreate). Contract inherited from the ported server: ALL point/length values cross the wire
///     in MILLIMETERS (Revit internal = decimal feet, factor 304.8); every write runs in its own transaction
///     with warnings suppressed (the bridge is headless — a modal would hang the HTTP request); handlers never
///     throw — they return Err(...) JSON.
/// </summary>
public static partial class BridgeTools
{
    /// <summary>
    ///     Set by <see cref="BridgeEventHandler"/> for the duration of each request: returns true once the
    ///     waiting HTTP thread has GIVEN UP on this request (timed out). A long write must consult this right
    ///     before committing and roll back instead — otherwise it commits silently after the client was told
    ///     "not applied", and a client retry double-applies. Only one request runs at a time, so a plain static
    ///     is safe. Null when no request is active (treated as "not abandoned").
    /// </summary>
    internal static Func<bool>? RequestAbandoned;

    /// <summary>True when the in-flight request has been abandoned by its waiter (see <see cref="RequestAbandoned"/>).</summary>
    internal static bool Abandoned() { try { return RequestAbandoned?.Invoke() ?? false; } catch { return false; } }

    internal const double MmPerFoot = 304.8;
    internal static double MmToFt(double mm) => mm / MmPerFoot;
    internal static double FtToMm(double ft) => ft * MmPerFoot;

    /// <summary>Reads a {x,y,z} object (mm) into an internal-units XYZ. Missing axes default to 0.</summary>
    internal static XYZ? PointArg(JsonElement args, string prop)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(prop, out var p)
            || p.ValueKind != JsonValueKind.Object) return null;
        return PointOf(p);
    }

    /// <summary>Reads a {x,y,z} JSON object (mm) directly into an internal-units XYZ.</summary>
    internal static XYZ PointOf(JsonElement p) => new(
        MmToFt(DblOf(p, "x")), MmToFt(DblOf(p, "y")), MmToFt(DblOf(p, "z")));

    internal static double DblOf(JsonElement obj, string prop, double fallback = 0)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : fallback;

    internal static string? StrArg(JsonElement args, string prop)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double? DblArg(JsonElement args, string prop)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    internal static bool BoolArg(JsonElement args, string prop, bool fallback = false)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v)
           && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;

    internal static int IntArg(JsonElement args, string prop, int fallback)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v) && TryGetInt(v, out var i)
            ? i : fallback;

    /// <summary>Reads an array of element ids (numbers or numeric strings) into ElementIds.</summary>
    internal static List<ElementId> IdListArg(JsonElement args, string prop)
        => IdListArg(args, prop, out _);

    /// <summary>
    ///     Reads an array of element ids, reporting how many entries could NOT be parsed. Callers must not
    ///     ignore <paramref name="dropped"/>: silently discarding an entry means a destructive tool acts on
    ///     fewer elements than the caller asked for and still reports success (e.g. delete_elements with
    ///     [123, "124", null, 126] deleting three and saying so, with nothing naming the fourth).
    /// </summary>
    internal static List<ElementId> IdListArg(JsonElement args, string prop, out int dropped)
    {
        var ids = new List<ElementId>();
        dropped = 0;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
            {
                if (TryGetLong(el, out var id)) ids.Add(new ElementId(id));
                else dropped++;
            }
        return ids;
    }

    /// <summary>Element by numeric id (arg name "element_id"), or null.</summary>
    internal static Element? ElementArg(Document doc, JsonElement args, string prop = "element_id")
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v) && TryGetLong(v, out var id)
            ? doc.GetElement(new ElementId(id)) : null;

    /// <summary>Level by name, or (name null/absent) the lowest level, or null when the model has none.</summary>
    internal static Level? FindLevel(Document doc, string? name)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        if (levels.Count == 0) return null;
        if (!string.IsNullOrEmpty(name))
            return levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        return levels.OrderBy(l => l.Elevation).First();
    }

    /// <summary>Non-template view by exact name (any view class), or null.</summary>
    internal static View? FindView(Document doc, string? name)
        => string.IsNullOrEmpty(name)
            ? null
            : new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     FamilySymbol lookup: family and/or type name, optionally category-filtered. Matches exact
    ///     (case-insensitive) on "Family: Type", family name, or type name; falls back to contains.
    /// </summary>
    internal static FamilySymbol? FindSymbol(Document doc, string? familyName, string? typeName, BuiltInCategory? cat = null)
    {
        var col = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
        if (cat.HasValue) col = col.OfCategory(cat.Value);
        var symbols = col.Cast<FamilySymbol>().ToList();

        bool NameMatch(string? want, string have) =>
            !string.IsNullOrEmpty(want) && string.Equals(want, have, StringComparison.OrdinalIgnoreCase);

        var exact = symbols.FirstOrDefault(s =>
            (NameMatch(familyName, s.FamilyName) || string.IsNullOrEmpty(familyName))
            && (NameMatch(typeName, s.Name) || string.IsNullOrEmpty(typeName))
            && !(string.IsNullOrEmpty(familyName) && string.IsNullOrEmpty(typeName)));
        if (exact != null) return exact;

        return symbols.FirstOrDefault(s =>
            (!string.IsNullOrEmpty(familyName) && s.FamilyName.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrEmpty(typeName) && s.Name.IndexOf(typeName!, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    /// <summary>Resolves "OST_Walls" / "Walls" / "walls" to a BuiltInCategory, or null.</summary>
    internal static BuiltInCategory? FindBuiltInCategory(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var n = name!.Trim();
        if (!n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) n = "OST_" + n.Replace(" ", "");
        if (Enum.TryParse<BuiltInCategory>(n, true, out var bic)) return bic;
        // Friendly fallback: match by the category's display name.
        foreach (BuiltInCategory c in Enum.GetValues(typeof(BuiltInCategory)))
        {
            try
            {
                var label = LabelUtils.GetLabelFor(c);
                if (string.Equals(label, name, StringComparison.OrdinalIgnoreCase)) return c;
            }
            catch { /* not all values have labels */ }
        }
        return null;
    }

    /// <summary>Deletes warnings and rolls back on errors so a headless bridge call never shows a modal.</summary>
    internal sealed class BridgeFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            var failures = a.GetFailureMessages();
            if (failures.Count == 0) return FailureProcessingResult.Continue;
            foreach (var f in failures)
            {
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                else return FailureProcessingResult.ProceedWithRollBack;
            }
            return FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    ///     Runs <paramref name="body"/> inside a warning-suppressed transaction. The body returns the response
    ///     JSON (string); a body that returns an Err(...) — detected by its ok:false — rolls back instead of
    ///     committing, and any exception rolls back and becomes Err. This is the ONLY transaction wrapper the
    ///     parity tools should use.
    /// </summary>
    /// <summary>True when a tool response's TOP-LEVEL "ok" is true. Unparseable or missing ⇒ false
    /// (fail closed: an envelope we can't read must not be treated as a success worth committing).</summary>
    private static bool ResponseIsOk(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("ok", out var okEl)
                   && okEl.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    internal static string InTransaction(Document doc, string name, Func<string> body)
    {
        using var tx = new Transaction(doc, name);
        var fho = tx.GetFailureHandlingOptions();
        fho.SetFailuresPreprocessor(new BridgeFailurePreprocessor());
        fho.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(fho);
        tx.Start();
        try
        {
            var result = body();
            // Parse the envelope's ok field rather than substring-matching the serialized text. The old
            // `!result.Contains("\"ok\":false")` was correct only because JsonOpts happens to set
            // WriteIndented = false — flipping that to true while debugging a payload would emit
            // `"ok": false` (with a space), the check would never match, and EVERY failed write would
            // COMMIT instead of rolling back. It also misfired the other way for any batch tool that
            // nested a per-item `"ok":false` inside a success envelope.
            bool ok = ResponseIsOk(result);
            // Roll back a write whose waiter already timed out — committing now would be a silent change the
            // client was told didn't happen (and a retry would double-apply).
            if (ok && Abandoned())
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                return Err("request timed out before commit — rolled back (not applied)");
            }
            if (ok && tx.GetStatus() == TransactionStatus.Started)
            {
                // Commit() returns RolledBack WITHOUT throwing when an error-severity failure discards
                // the transaction (VERIFIED-LIVE, docs/design-notes/revit-api-research-notes.md) — the
                // preprocessor above triggers exactly that, so an unchecked commit reports a wholly
                // discarded write as success. Don't touch element handles here: they die with the rollback.
                TransactionStatus st;
                try { st = tx.Commit(); } catch { st = TransactionStatus.RolledBack; }
                if (st != TransactionStatus.Committed)
                    return Err("Revit rolled back the transaction at commit (error-severity failure) — no changes were applied");
            }
            else if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return result;
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return Err(ex.Message);
        }
    }

    /// <summary>Compact id/name/category summary used by list-style responses.</summary>
    internal static Dictionary<string, object?> Describe(Element e) => new()
    {
        ["id"] = e.Id.Value,
        ["name"] = ElemName(e),
        ["category"] = e.Category?.Name,
    };

    /// <summary>Element.Name that never throws (some elements reject the getter).</summary>
    internal static string ElemName(Element e) { try { return e.Name ?? ""; } catch { return ""; } }

    /// <summary>{x,y,z} of an internal-units (feet) point converted to millimeters, for wire responses.</summary>
    internal static Dictionary<string, object?> MmPoint(XYZ p) => new()
    { ["x"] = FtToMm(p.X), ["y"] = FtToMm(p.Y), ["z"] = FtToMm(p.Z) };
}

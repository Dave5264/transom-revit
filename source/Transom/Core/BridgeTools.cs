using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     The single seam the in-Revit bridge server (F1) calls per request. Parses a
///     <c>{"tool":"&lt;name&gt;","args":{...}}</c> envelope, resolves the target document, dispatches the
///     tool, and returns a JSON response string. NEVER throws: any failure is caught and returned as
///     <c>{"ok":false,"error":"..."}</c> so the bridge can always write a 200 with a JSON body.
///
///     The write path (set_parameter / set_parameters) mirrors the safety patterns in
///     <see cref="Importer"/>: live binding resolution (instance vs type), read-only / unsupported-storage
///     refusal, checking the <see cref="Parameter"/>.Set return value, re-reading to verify the value
///     actually landed, and rolling the transaction back when verification fails. Instance params on a
///     Revit group member are written directly ONLY when Revit permits it — identity built-ins
///     (Mark/Number) or a project/shared param already varying by group instance; anything else is
///     refused up front with an actionable error (the vary flag is a one-way door the bridge never sets).
/// </summary>
public static partial class BridgeTools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static string Handle(UIApplication app, string requestJson)
    {
        try
        {
            using var json = JsonDocument.Parse(requestJson);
            var root = json.RootElement;

            var tool = root.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                ? a
                : default;

            // AIRE tools are document-independent (image files + HTTPS only) — dispatch them before
            // document resolution so they work on the zero-doc Home screen too.
            var aire = DispatchAire(tool, args);
            if (aire != null) return aire;

            var doc = DocUtil.Resolve(app, "") ?? app.ActiveUIDocument?.Document;
            if (doc == null) return Err("no active document");

            return tool switch
            {
                "status"             => Status(doc),
                "list_schedules"     => ListSchedules(doc),
                "read_schedule"      => ReadSchedule(app, doc, args),
                "set_parameter"      => SetParameter(doc, args),
                "set_parameters"     => SetParameters(doc, args),
                "execute_revit_code" => ExecuteRevitCode(app, args),
                ""                   => Err("missing 'tool'"),
                // Everything else falls through to the extended handlers (BridgeToolsDispatch).
                _                    => DispatchParity(app, doc, tool, args) ?? Err($"unknown tool '{tool}'"),
            };
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    // --- status ------------------------------------------------------------

    private static string Status(Document doc)
    {
        string version;
        try { version = string.IsNullOrEmpty(AppInfo.Version) ? "1" : AppInfo.Version; }
        catch { version = "1"; }

        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["tool"] = "Transom",
            ["version"] = version,
            ["doc"] = SafeTitle(doc),
        });
    }

    // --- list_schedules ----------------------------------------------------

    private static string ListSchedules(Document doc)
    {
        var schedules = DocUtil.UserSchedules(doc)
            .Select(s => new Dictionary<string, object?> { ["id"] = s.id, ["name"] = s.name })
            .Cast<object?>()
            .ToList();

        return Json(new Dictionary<string, object?> { ["ok"] = true, ["schedules"] = schedules });
    }

    // --- read_schedule -----------------------------------------------------

    private static string ReadSchedule(UIApplication app, Document doc, JsonElement args)
    {
        var vs = FindSchedule(doc, args);
        if (vs == null) return Err("schedule not found");

        // UiApp arms the dialog suppressor around the rolled-back UID stamp — without it a side-effect
        // prompt (e.g. stamping a Level name) blocks the API thread until a human dismisses it.
        ScheduleTable table;
        try { table = new ScheduleReader(doc) { UiApp = app }.Read(vs); }
        catch (Exception ex) { return Err("read failed: " + ex.Message); }

        var columns = table.Columns.Select(c => new Dictionary<string, object?>
        {
            ["col"] = c.Col,
            ["fieldName"] = c.FieldName,
            ["header"] = c.Header,
            ["binding"] = c.Binding,
            ["parameterId"] = c.ParameterId,
            // Writable here = a real, importable column (not calculated/combined, not read-only/unsupported).
            // Group-member instance params still report writable=true: they're writable via set_parameter.
            ["writable"] = c.Writable && c.ImportEditable,
        }).Cast<object?>().ToList();

        var rows = new List<object?>();
        foreach (var r in table.Rows)
        {
            var cells = new List<object?>();
            if (r.ExcelRow >= 0 && r.ExcelRow < table.Cells.Length)
            {
                var cellRow = table.Cells[r.ExcelRow];
                for (int c = 0; c < cellRow.Length; c++)
                    cells.Add(cellRow[c]?.Text ?? "");
            }

            // Only element/type/group rows carry a writable anchor; header/blank/groupHeader rows have null.
            bool anchored = r.Kind is "element" or "type" or "group" && r.UniqueId != null;
            rows.Add(new Dictionary<string, object?>
            {
                ["uniqueId"] = anchored ? r.UniqueId : null,
                ["kind"] = r.Kind,
                ["cells"] = cells,
            });
        }

        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["name"] = table.ScheduleName,
            ["roundTrippable"] = table.RoundTrippable,
            ["columns"] = columns,
            ["rows"] = rows,
        });
    }

    private static ViewSchedule? FindSchedule(Document doc, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("id", out var idEl) && TryGetLong(idEl, out long id))
            {
                if (doc.GetElement(new ElementId(id)) is ViewSchedule byId) return byId;
            }
            if (args.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrEmpty(name))
                    return new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                        .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            }
        }
        return null;
    }

    // --- set_parameter -----------------------------------------------------

    private static string SetParameter(Document doc, JsonElement args)
    {
        var edit = ParseEdit(args);
        if (edit.Error != null) return Err(edit.Error);

        using var tx = new Transaction(doc, "Transom: set parameter");
        tx.Start();
        try
        {
            var result = ApplyEdit(doc, edit);
            bool ok = result.TryGetValue("ok", out var okv) && okv is true;
            if (ok && Abandoned()) { tx.RollBack(); return Err("request timed out before commit — rolled back (not applied)"); }
            if (ok)
            {
                // Commit() returns RolledBack without throwing when an error-severity failure discards the
                // transaction — report that as the failure it is, not as success.
                TransactionStatus st;
                try { st = tx.Commit(); } catch { st = TransactionStatus.RolledBack; }
                if (st != TransactionStatus.Committed)
                    return Err("write rolled back by Revit at commit — not applied");
            }
            else tx.RollBack();
            return Json(result);
        }
        catch (Exception ex)
        {
            // ApplyEdit calls into the Revit API (Set/parse/regen) which can throw — never leave the
            // transaction open (a dangling open tx breaks every later command this session).
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return Err("write failed (rolled back): " + ex.Message);
        }
    }

    // --- set_parameters ----------------------------------------------------

    private static string SetParameters(Document doc, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("edits", out var editsEl)
            || editsEl.ValueKind != JsonValueKind.Array)
            return Err("missing 'edits' array");

        var parsed = editsEl.EnumerateArray().Select(ParseEdit).ToList();

        var results = new List<object?>();
        bool allOk = true;

        using var tx = new Transaction(doc, "Transom: set parameters");
        tx.Start();
        try
        {
            foreach (var edit in parsed)
            {
                Dictionary<string, object?> r;
                if (edit.Error != null) r = Fail(edit.Error);
                else r = ApplyEdit(doc, edit);

                results.Add(r);
                if (!(r.TryGetValue("ok", out var ok) && ok is true)) allOk = false;
            }

            // All-or-nothing: any failed/unverified edit rolls back the whole batch. A batch that outran its
            // waiter's timeout also rolls back rather than committing behind the client's back.
            if (allOk && Abandoned())
            {
                tx.RollBack();
                return Err("request timed out before commit — rolled back (not applied)");
            }
            if (allOk)
            {
                // Same commit-status check as set_parameter: a Revit-side rollback at commit must not be
                // reported as a successfully applied batch.
                TransactionStatus st;
                try { st = tx.Commit(); } catch { st = TransactionStatus.RolledBack; }
                if (st != TransactionStatus.Committed)
                    return Err("batch rolled back by Revit at commit — no edits applied");
            }
            else tx.RollBack();
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return Err("batch failed (rolled back): " + ex.Message);
        }

        return Json(new Dictionary<string, object?> { ["ok"] = allOk, ["results"] = results });
    }

    // --- execute_revit_code (G2/G3) ----------------------------------------

    /// <summary>
    ///     Runs an arbitrary Revit API C# snippet in-process on the API thread (full API reach). See
    ///     <see cref="RevitCodeExecutor"/> for the transaction/timeout/audit policy and the SECURITY note: the
    ///     loopback + per-session token + Origin/Host gate (in BridgeServer) are the ONLY boundary for this tool.
    /// </summary>
    private static string ExecuteRevitCode(UIApplication app, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("code", out var codeEl)
            || codeEl.ValueKind != JsonValueKind.String)
            return Err("missing 'code' (a C# snippet string)");

        string code = codeEl.GetString() ?? "";
        bool readOnly = args.TryGetProperty("readOnly", out var roEl)
                        && (roEl.ValueKind == JsonValueKind.True
                            || (roEl.ValueKind == JsonValueKind.String && bool.TryParse(roEl.GetString(), out var b) && b));

        // Audit every executed snippet to the bridge's stderr log (the host captures it).
        var result = RevitCodeExecutor.Execute(app, code, readOnly,
            msg => Console.Error.WriteLine("[transom-bridge] " + msg));
        return Json(result);
    }

    // --- the core write (one edit, inside a caller-managed transaction) -----

    private sealed class EditSpec
    {
        public string? Error;
        public string UniqueId = "";
        public int? ParameterId;
        public string? FieldName;
        public string Value = "";
        public string? Binding; // requested "instance"|"type", optional
    }

    private static EditSpec ParseEdit(JsonElement args)
    {
        var e = new EditSpec();
        if (args.ValueKind != JsonValueKind.Object) { e.Error = "edit must be an object"; return e; }

        if (!args.TryGetProperty("uniqueId", out var uidEl) || uidEl.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(uidEl.GetString()))
        { e.Error = "missing 'uniqueId'"; return e; }
        e.UniqueId = uidEl.GetString()!;

        if (args.TryGetProperty("parameterId", out var pidEl) && TryGetInt(pidEl, out int pid))
            e.ParameterId = pid;

        if (args.TryGetProperty("fieldName", out var fnEl) && fnEl.ValueKind == JsonValueKind.String)
            e.FieldName = fnEl.GetString();

        if (e.ParameterId == null && string.IsNullOrEmpty(e.FieldName))
        { e.Error = "specify 'parameterId' or 'fieldName'"; return e; }

        if (args.TryGetProperty("value", out var valEl))
            e.Value = valEl.ValueKind == JsonValueKind.String ? valEl.GetString() ?? "" : valEl.GetRawText();
        else
        { e.Error = "missing 'value'"; return e; }

        if (args.TryGetProperty("binding", out var bEl) && bEl.ValueKind == JsonValueKind.String)
        {
            var b = bEl.GetString();
            if (b is "instance" or "type") e.Binding = b;
            else if (!string.IsNullOrEmpty(b)) { e.Error = $"invalid binding '{b}' (use instance|type)"; return e; }
        }

        return e;
    }

    /// <summary>
    ///     Applies one edit inside an already-started transaction. Returns the per-edit result dictionary; the
    ///     caller commits on ok or rolls back otherwise. Resolves binding live, refuses read-only /
    ///     family-or-type-driven params, sets, checks the Set return AND re-reads to verify.
    /// </summary>
    private static Dictionary<string, object?> ApplyEdit(Document doc, EditSpec edit)
    {
        var el = doc.GetElement(edit.UniqueId);
        if (el == null) return Fail("element not found");

        // Resolve the parameter id from a field name if needed (instance first, then type).
        int? parameterId = edit.ParameterId;
        if (parameterId == null)
        {
            parameterId = FindParamIdByName(doc, el, edit.FieldName!);
            if (parameterId == null) return Fail($"parameter '{edit.FieldName}' not found on element or its type");
        }
        int pid = parameterId.Value;

        // Resolve binding against the LIVE model (a stale/requested binding can't misroute the write).
        var binding = ResolveBindingLive(doc, el, pid, edit.Binding ?? "");
        if (binding == "none") return Fail("parameter not found on element or its type");

        // If the caller insisted on a binding the live model can't satisfy, surface that rather than silently
        // writing the other host.
        if (edit.Binding != null && edit.Binding != binding)
            return Fail($"requested binding '{edit.Binding}' but parameter resolves to '{binding}' on this element");

        Element? host = binding == "type"
            ? (el.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(el.GetTypeId()) : null)
            : el;
        if (host == null) return Fail("could not resolve parameter host");

        var param = GetParam(host, pid);
        if (param == null) return Fail("parameter not found");

        if (param.IsReadOnly)
            return Fail("parameter is read-only (driven by the family or type)");

        // ElementId / family-or-type-selection params aren't text-settable.
        bool measurableDouble = param.StorageType == StorageType.Double && GetSpecTypeId(param) != null;
        if (param.StorageType != StorageType.String
            && param.StorageType != StorageType.Integer
            && !measurableDouble)
            return Fail("parameter is set by a family/type selection, not by text (unsupported storage type)");

        var oldDisplay = CurrentDisplay(param);

        // instancesAffected: 1 for an instance write; for a type write, count instances of that type.
        int instancesAffected = binding == "type" ? CountInstancesOfType(doc, el.GetTypeId()) : 1;

        var (inGroup, groupName) = GroupInfo(doc, el);

        // A grouped member's instance write only lands for the curated identity built-ins (Mark/Number,
        // live-verified 2026-06-19) or a project/shared param ALREADY varying by group instance. Everything
        // else Revit refuses or rolls back at commit — and the generic "write not verified" error gives the
        // caller nothing to act on, so detect it up front and say which path applies. The vary flag is a
        // one-way door, so the bridge never enables it itself.
        if (inGroup && binding == "instance" && !ScheduleReader.IsInstanceIdentityBuiltin(pid))
        {
            bool varies = false;
            try { varies = param.Definition is InternalDefinition idef && idef.VariesAcrossGroups; } catch { /* treat as not varying */ }
            if (!varies)
                return Fail(pid > 0
                    ? $"parameter is on a member of group '{groupName}' and does not vary by group instance — enable 'vary by group instance' for it in Revit (or run the import through the Schedule Hub, which stages this), then retry"
                    : $"built-in parameter on a member of group '{groupName}' — Revit only allows this write in Edit Group mode; use the Schedule Hub's Claude-Assist staged flow, not set_parameter");
        }

        // Parse + set per storage type.
        bool setOk;
        if (param.StorageType == StorageType.String)
        {
            setOk = param.Set(edit.Value);
        }
        else if (param.StorageType == StorageType.Integer)
        {
            if (!TryParseInteger(IsYesNo(param), edit.Value, out int iv))
                return Fail($"unparseable integer value '{edit.Value}'");
            setOk = param.Set(iv);
        }
        else // measurable double
        {
            var spec = GetSpecTypeId(param)!;
            if (!UnitFormatUtils.TryParse(doc.GetUnits(), new ForgeTypeId(spec), edit.Value, out double dv))
                return Fail($"unparseable value '{edit.Value}' for this parameter's units");
            setOk = param.Set(dv);
        }

        if (!setOk)
            return Fail("Set() returned false — the value could not be applied");

        // Re-read to confirm the displayed value actually changed/landed (Set can return true yet be coerced
        // or silently no-op on derived parameters).
        var newDisplay = CurrentDisplay(param);
        bool verified = VerifyWrite(doc, param, edit.Value);
        if (!verified)
            return Fail("write not verified — value did not take (rolled back)");

        var note = inGroup && binding == "instance"
            ? $"instance parameter on group member '{groupName}' (identity built-in or varies by group instance)"
            : binding == "type"
                ? $"type parameter — affects {instancesAffected} instance(s) of this type"
                : "instance parameter";

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["uniqueId"] = edit.UniqueId,
            ["parameterId"] = pid,
            ["old"] = oldDisplay,
            ["new"] = newDisplay,
            ["verified"] = true,
            ["binding"] = binding,
            ["instancesAffected"] = instancesAffected,
            ["note"] = note,
        };
    }

    // --- helpers (duplicated from Importer/ScheduleReader; do not edit those) ---

    private static int? FindParamIdByName(Document doc, Element el, string fieldName)
    {
        foreach (Parameter p in el.Parameters)
            if (ParamName(p) == fieldName) return (int)p.Id.Value;

        var tid = el.GetTypeId();
        if (tid != ElementId.InvalidElementId)
        {
            var type = doc.GetElement(tid);
            if (type != null)
                foreach (Parameter p in type.Parameters)
                    if (ParamName(p) == fieldName) return (int)p.Id.Value;
        }
        return null;
    }

    private static string ParamName(Parameter p)
    {
        try { return p.Definition?.Name ?? ""; }
        catch { return ""; }
    }

    private static Parameter? GetParam(Element host, int parameterId)
    {
        if (parameterId < 0)
            return host.get_Parameter((BuiltInParameter)parameterId);
        foreach (Parameter p in host.Parameters)
            if (p.Id.Value == parameterId) return p;
        return null;
    }

    /// <summary>Mirrors Importer.ResolveBindingLive: where this parameter actually lives on the live model.</summary>
    private static string ResolveBindingLive(Document doc, Element instance, int parameterId, string requestedBinding)
    {
        bool onInstance = GetParam(instance, parameterId) != null;
        bool onType = false;
        var tid = instance.GetTypeId();
        if (tid != ElementId.InvalidElementId)
        {
            var t = doc.GetElement(tid);
            onType = t != null && GetParam(t, parameterId) != null;
        }

        if (requestedBinding == "type" && onType) return "type";
        if (requestedBinding == "instance" && onInstance) return "instance";
        if (onInstance) return "instance";
        if (onType) return "type";
        return "none";
    }

    private static string? GetSpecTypeId(Parameter param)
    {
        try
        {
            var spec = param.Definition.GetDataType();
            if (spec != null && !spec.Empty() && UnitUtils.IsMeasurableSpec(spec)) return spec.TypeId;
        }
        catch { /* string/int/no-spec */ }
        return null;
    }

    /// <summary>
    ///     PERF-01: how many instances a type write affects. This used to be
    ///     <c>WhereElementIsNotElementType().Where(e =&gt; e.GetTypeId() == typeId).Count()</c> — the type match
    ///     was a client-side LINQ predicate, so it materialised and inspected EVERY non-type element in the
    ///     document (hundreds of thousands on a real project) inside the open write transaction, on the API
    ///     thread, while the shim's request waiter counted down toward its abandonment check. It bought only a
    ///     cosmetic <c>instancesAffected</c> field, and it was paid on every type-bound edit whether the caller
    ///     read the field or not. Revit can do this server-side: an <see cref="ElementParameterFilter"/> on
    ///     <c>ELEM_FAMILY_AND_TYPE_PARAM</c> pushes the comparison into the element iterator.
    /// </summary>
    private static int CountInstancesOfType(Document doc, ElementId typeId)
    {
        if (typeId == ElementId.InvalidElementId) return 0;
        try
        {
            var rule = ParameterFilterRuleFactory.CreateEqualsRule(
                new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM), typeId);
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementParameterFilter(rule))
                .GetElementCount();
        }
        catch
        {
            // Not every element category exposes ELEM_FAMILY_AND_TYPE_PARAM (system families, some sketch
            // elements). Fall back to the exhaustive walk rather than reporting a wrong count.
            try
            {
                return new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.GetTypeId() == typeId)
                    .Count();
            }
            catch { return 0; }
        }
    }

    private static string CurrentDisplay(Parameter param) =>
        param.StorageType == StorageType.String ? (param.AsString() ?? "") : (param.AsValueString() ?? "");

    /// <summary>Re-reads a just-written parameter to confirm the new value persisted (within unit tolerance).</summary>
    private static bool VerifyWrite(Document doc, Parameter param, string requested)
    {
        try
        {
            if (param.StorageType == StorageType.String)
                return (param.AsString() ?? "") == requested;
            if (param.StorageType == StorageType.Integer)
                return TryParseInteger(IsYesNo(param), requested, out int iv) && param.AsInteger() == iv;
            if (param.StorageType == StorageType.Double)
            {
                var spec = GetSpecTypeId(param);
                if (spec == null) return true;
                if (!UnitFormatUtils.TryParse(doc.GetUnits(), new ForgeTypeId(spec), requested, out double d))
                    return false;
                return Math.Abs(param.AsDouble() - d) <= 1e-6;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsYesNo(Parameter param)
    {
        try { return param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
        catch { return false; }
    }

    /// <summary>Parses an integer or Yes/No cell. Yes/No accepts Yes/No/Y/N/True/False/1/0 (blank = No).</summary>
    private static bool TryParseInteger(bool yesNo, string text, out int value)
    {
        value = 0;
        text = (text ?? "").Trim();
        if (yesNo)
        {
            switch (text.ToLowerInvariant())
            {
                case "yes": case "y": case "true": case "1": value = 1; return true;
                case "no": case "n": case "false": case "0": case "": value = 0; return true;
                default: return false;
            }
        }
        return int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static (bool inGroup, string name) GroupInfo(Document doc, Element e)
    {
        var gid = e.GroupId;
        if (gid == null || gid == ElementId.InvalidElementId) return (false, "");
        var g = doc.GetElement(gid);
        if (g == null) return (true, "Group");
        // The group TYPE name — what the Project Browser's Groups node and "Edit Group" show, and how
        // users and the rest of the UI identify a group. Element.Name on the INSTANCE returns "Group 1",
        // "Group 2", which tells nobody which group was touched.
        try
        {
            var tid = g.GetTypeId();
            if (tid != null && tid != ElementId.InvalidElementId && doc.GetElement(tid) is { } gt)
            {
                var tn = SafeName(gt);
                if (!string.IsNullOrEmpty(tn)) return (true, tn);
            }
        }
        catch { /* fall back to the instance name */ }
        return (true, SafeName(g));
    }

    private static string SafeName(Element e)
    {
        try { return e.Name; }
        catch { return e.Id.ToString(); }
    }

    private static string SafeTitle(Document doc)
    {
        try { return doc.Title; }
        catch { return ""; }
    }

    // --- json plumbing -----------------------------------------------------

    private static bool TryGetLong(JsonElement el, out long value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out value)) return true;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out value)) return true;
        return false;
    }

    private static bool TryGetInt(JsonElement el, out int value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value)) return true;
        return false;
    }

    private static Dictionary<string, object?> Fail(string error) =>
        new() { ["ok"] = false, ["error"] = error };

    private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonOpts);

    private static string Err(string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = message }, JsonOpts);
}

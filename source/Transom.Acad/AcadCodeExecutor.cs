using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using static Transom.Acad.Json;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Runner = System.Func<object, System.Threading.CancellationToken, System.Collections.Generic.Dictionary<string, object?>>;

namespace Transom.Acad;

/// <summary>
///     The <c>execute_autocad_code</c> tool, AutoCAD's counterpart of Transom's execute_revit_code. Compiles a
///     C# snippet on the bridge thread (Roslyn lives in an isolated load context, reached by reflection only:
///     keep this file free of Microsoft.CodeAnalysis typerefs), then runs it on AutoCAD's main thread.
///
///     SECURITY: arbitrary code runs in-process, so the only boundary is the bridge's transport gate (loopback
///     bind, per-session token, Origin/Host checks in <see cref="BridgeServer"/>). Every snippet is logged.
///
///     TIMEOUT: the cancellation cap is cooperative. A loop with no statement boundary never observes it and
///     freezes AutoCAD; managed code on the main thread cannot be force-aborted.
///
///     VISIBILITY: must stay public. The snippet compiles as a separate assembly, so a Globals type nested in an
///     internal class is invisible to it (CS0122 on every global).
/// </summary>
public static class AcadCodeExecutor
{
    private static readonly TimeSpan MaxRun = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AppTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] Imports =
    {
        "System", "System.Linq", "System.Collections.Generic",
        "Autodesk.AutoCAD.ApplicationServices", "Autodesk.AutoCAD.DatabaseServices",
        "Autodesk.AutoCAD.EditorInput", "Autodesk.AutoCAD.Geometry", "Autodesk.AutoCAD.Colors",
    };

    /// <summary>Globals the snippet sees. In application context with no drawing open, doc/db/ed are null and
    /// tr/ms are always null.</summary>
    public sealed class Globals
    {
        public Document doc = null!;
        public Database db = null!;
        public Editor ed = null!;
        public Transaction tr = null!;

        internal Func<BlockTableRecord>? OpenModelSpace;
        private BlockTableRecord? _ms;

        /// <summary>Model space, opened through <c>tr</c> on first use (for write unless readOnly). Lazy so a
        /// snippet that runs commands via ed.Command doesn't find model space already held open.</summary>
        public BlockTableRecord ms => _ms ??= OpenModelSpace?.Invoke()!;

        private readonly StringBuilder _log = new();
        /// <summary>Write a line to the log returned with the result.</summary>
        public void Print(object? value) => _log.AppendLine(value?.ToString() ?? "null");
        internal string LogText => _log.ToString();
    }

    internal static string Execute(JsonElement args)
    {
        var code = Str(args, "code");
        if (string.IsNullOrWhiteSpace(code)) return Error("missing 'code'");
        bool readOnly = Bool(args, "readOnly");
        bool application = string.Equals(Str(args, "context"), "application", StringComparison.OrdinalIgnoreCase);
        Log.Write($"execute_autocad_code (context={(application ? "application" : "document")}, readOnly={readOnly}): {Log.Truncate(code, 4000)}");

        // Compile HERE, on the bridge thread: Roslyn's first compile takes seconds and must not freeze AutoCAD.
        Runner runner;
        try
        {
            var outcome = Compile(code);
            switch (outcome["status"] as string)
            {
                case "ok":
                    runner = (Runner)outcome["runner"]!;
                    break;
                case "compile":
                    return Error("compile error: " + outcome.GetValueOrDefault("error"));
                default:
                    return Error("script host error: " + outcome.GetValueOrDefault("error"));
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            return Error("script host error: " + tie.InnerException.Message);
        }
        catch (System.Exception ex)
        {
            return Error("script host error: " + ex.Message);
        }

        if (application)
            return MainThread.Invoke(done => done(Serialize(RunInApplication(runner))), AppTimeout);
        // readOnly skips command context so it leaves no empty undo step (see Dispatcher.OnDocumentRead).
        // The cost: ed.Command is unavailable to a read-only snippet.
        if (readOnly)
            return Dispatcher.OnDocumentRead(doc => RunInDocument(doc, runner, readOnly: true));
        return Dispatcher.OnDocument(doc => Task.FromResult(RunInDocument(doc, runner, readOnly: false)));
    }

    private static Dictionary<string, object?> Compile(string code)
    {
        var host = ScriptIsolation.ScriptHostAssembly.GetType("Transom.Acad.Scripting.ScriptHost")
                   ?? throw new InvalidOperationException("Transom.Acad.Scripting.ScriptHost type not found");
        var compile = host.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static)
                      ?? throw new InvalidOperationException("ScriptHost.Compile method not found");
        return (Dictionary<string, object?>)compile.Invoke(null, new object?[] { code, typeof(Globals), References(), Imports })!;
    }

    private static Assembly[] References()
    {
        var list = new List<Assembly>
        {
            typeof(Document).Assembly,  // accoremgd
            typeof(AcApp).Assembly,     // acmgd
            typeof(Database).Assembly,  // acdbmgd
            typeof(Editor).Assembly,
            typeof(Point3d).Assembly,
            typeof(Enumerable).Assembly,
            typeof(object).Assembly,
            typeof(List<>).Assembly,
            typeof(Globals).Assembly,   // Transom.Acad (the snippet's globals type)
        };
        // AutoCAD's assemblies are compiled against these facades; Roslyn needs them to unify core types.
        foreach (var name in new[] { "System.Runtime", "System.Collections", "netstandard" })
        {
            try { list.Add(Assembly.Load(new AssemblyName(name))); }
            catch { /* optional */ }
        }
        return list.Distinct().ToArray();
    }

    /// <summary>
    ///     Write: command context, document locked, one transaction that commits on success and aborts on any failure.
    ///     Read-only: application context (Dispatcher.OnDocumentRead) with NO document lock and an
    ///     OpenCloseTransaction that is always aborted. The lock matters: taken from application context it opens an
    ///     undo group of its own, so every read-only snippet left an empty step on the undo stack (verified live
    ///     2026-10-30: write, read-only snippet, U, and the write survived; every lock-free read tool was clean).
    ///     Without the lock any attempt to write fails with eLockViolation, which is the read-only guard.
    /// </summary>
    private static Dictionary<string, object?> RunInDocument(Document doc, Runner runner, bool readOnly)
    {
        var db = doc.Database;
        using var docLock = readOnly ? null : doc.LockDocument();
        using Transaction tr = readOnly
            ? db.TransactionManager.StartOpenCloseTransaction()
            : db.TransactionManager.StartTransaction();
        var globals = new Globals
        {
            doc = doc,
            db = db,
            ed = doc.Editor,
            tr = tr,
            OpenModelSpace = () => (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), readOnly ? OpenMode.ForRead : OpenMode.ForWrite),
        };

        Dictionary<string, object?> result;
        using (var cts = new CancellationTokenSource(MaxRun))
            result = Interpret(runner(globals, cts.Token), globals); // describe while objects are still open

        bool ok = result["ok"] is true;
        if (ok && !readOnly)
        {
            try
            {
                tr.Commit();
                result["committed"] = true;
            }
            catch (System.Exception ex)
            {
                return new() { ["ok"] = false, ["error"] = "commit failed, no changes applied: " + ex.Message, ["log"] = globals.LogText };
            }
        }
        else
        {
            try { tr.Abort(); } catch { /* the snippet may have ended it already */ }
            result["committed"] = false;
            if (!ok) result["error"] = result["error"] + ReadOnlyHint(readOnly, result) + " (transaction aborted, no changes applied)";
        }
        return result;
    }

    /// <summary>Turns the two bare AutoCAD errors a read-only snippet hits by design into an explanation.</summary>
    private static string ReadOnlyHint(bool readOnly, Dictionary<string, object?> result)
    {
        if (!readOnly) return "";
        var error = result.GetValueOrDefault("error") as string ?? "";
        var stack = result.GetValueOrDefault("stack") as string ?? "";
        if (stack.Contains("Editor.Command"))
            return ". Read-only snippets run outside a command, where ed.Command is unavailable; run it without readOnly";
        if (error.Contains("eLockViolation"))
            return ". Read-only snippets hold no document lock, so they can't write; run it without readOnly";
        return "";
    }

    /// <summary>Application context: no lock, no transaction. For opening, closing and switching drawings.</summary>
    private static Dictionary<string, object?> RunInApplication(Runner runner)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var globals = new Globals { doc = doc!, db = doc?.Database!, ed = doc?.Editor! };
        using var cts = new CancellationTokenSource(MaxRun);
        return Interpret(runner(globals, cts.Token), globals);
    }

    private static Dictionary<string, object?> Interpret(Dictionary<string, object?> outcome, Globals globals)
    {
        switch (outcome["status"] as string)
        {
            case "ok":
                return new()
                {
                    ["ok"] = true,
                    ["result"] = Describe(outcome.GetValueOrDefault("value"), globals.tr),
                    ["log"] = globals.LogText,
                };
            case "cancelled":
                return new()
                {
                    ["ok"] = false,
                    ["error"] = $"execution timed out (> {MaxRun.TotalSeconds:0}s) and was cancelled",
                    ["log"] = globals.LogText,
                };
            default:
                return new()
                {
                    ["ok"] = false,
                    ["error"] = outcome.GetValueOrDefault("error") as string ?? "unknown script error",
                    ["stack"] = outcome.GetValueOrDefault("stack") as string ?? "",
                    ["log"] = globals.LogText,
                };
        }
    }

    /// <summary>JSON-friendly description of the snippet's return value. Depth-bounded so a self-referential
    /// collection can't recurse into an uncatchable StackOverflowException that would kill AutoCAD.</summary>
    private static object? Describe(object? value, Transaction? tr, int depth = 0)
    {
        if (depth > 8) return "…(nesting too deep)";
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case double dbl: return R(dbl);
            case float f: return R(f);
            case bool or int or long or short or byte or decimal: return value;
            case ObjectId id:
                return id.IsNull ? null : new Dictionary<string, object?> { ["handle"] = id.Handle.ToString(), ["type"] = Tools.DxfName(id) };
            case Handle h: return h.ToString();
            case Point3d p3: return P(p3);
            case Point2d p2: return P(p2);
            case Vector3d v: return new[] { R(v.X), R(v.Y), R(v.Z) };
            case Entity e:
                return new Dictionary<string, object?> { ["handle"] = e.Handle.ToString(), ["type"] = Tools.DxfName(e.ObjectId), ["layer"] = e.Layer };
            case DBObject o:
                return new Dictionary<string, object?> { ["handle"] = o.Handle.ToString(), ["type"] = Tools.DxfName(o.ObjectId) };
            case IDictionary dict:
                var map = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dict) map[entry.Key.ToString() ?? ""] = Describe(entry.Value, tr, depth + 1);
                return map;
            case IEnumerable seq:
                var items = new List<object?>();
                foreach (var item in seq)
                {
                    items.Add(Describe(item, tr, depth + 1));
                    if (items.Count >= 500) { items.Add("…(truncated)"); break; }
                }
                return items;
        }

        var type = value.GetType();
        if (type.IsDefined(typeof(CompilerGeneratedAttribute), false) && type.Name.Contains("AnonymousType"))
        {
            var props = new Dictionary<string, object?>();
            foreach (var prop in type.GetProperties()) props[prop.Name] = Describe(prop.GetValue(value), tr, depth + 1);
            return props;
        }
        try { return value.ToString(); }
        catch { return type.Name; }
    }
}

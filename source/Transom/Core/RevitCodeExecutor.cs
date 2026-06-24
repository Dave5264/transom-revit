using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Transom.Core;

/// <summary>
///     G2/G3 — the bridge's <c>execute_revit_code</c> tool: compile + run an arbitrary Revit API C# snippet
///     IN-PROCESS on the Revit API thread (so transactions and API calls are valid in context), giving Claude
///     full API reach (print sets, multi-doc via <c>app.Documents</c>, improvise) beyond the fixed tool set.
///
///     SECURITY — load-bearing: arbitrary code execution removes the per-tool verify/rollback guarantees, so the
///     ONLY boundary is the bridge's transport gate — the loopback bind (127.0.0.1, never 0.0.0.0), the
///     per-session bridge.token, and the Origin/Host checks in <see cref="BridgeServer"/>. Those MUST stay intact;
///     do not weaken them. Every executed snippet is logged (caller passes a logger) for auditability. The owner
///     ships this ON by default (no opt-in gate) behind that token+loopback boundary.
///
///     TRANSACTION policy: <c>readOnly=false</c> (default) auto-wraps the snippet in a <see cref="Transaction"/>
///     and ROLLS BACK on any exception (commits on success); <c>readOnly=true</c> runs with NO transaction (a
///     write attempt then fails as Revit requires an open transaction — that's the intended guard).
///
///     TIMEOUT: a CancellationToken with a hard cap is passed to the scripting engine; it cancels cooperatively
///     between statements / at awaits. A pathological tight CPU loop with no statement boundary can still run to
///     the cap on the API thread (managed code can't be force-aborted) — documented limit, not a guarantee.
/// </summary>
public static class RevitCodeExecutor
{
    private static readonly TimeSpan MaxRun = TimeSpan.FromSeconds(8);

    /// <summary>Globals exposed to the snippet: the active doc, the UIApplication, the Application (for
    /// multi-doc via <c>app.Documents</c>), and a <c>Print(...)</c> sink whose output is returned to Claude.</summary>
    public sealed class Globals
    {
        public Document doc = null!;
        public UIApplication uiapp = null!;
        public Autodesk.Revit.ApplicationServices.Application app = null!;

        private readonly StringBuilder _log = new();
        /// <summary>Write a line to the captured log returned in the response (so Claude sees what happened).</summary>
        public void Print(object? value) => _log.AppendLine(value?.ToString() ?? "null");
        internal string LogText => _log.ToString();
    }

    /// <summary>Runs <paramref name="code"/> and returns the result dictionary the bridge serializes. Never throws.</summary>
    /// <param name="logSnippet">Audit sink — called with the snippet before it runs (stderr/log).</param>
    public static Dictionary<string, object?> Execute(UIApplication uiapp, string code, bool readOnly, Action<string> logSnippet)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "missing 'code'" };

        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no active document" };

        // AUDIT: log every executed snippet (truncated) before running it.
        try { logSnippet($"execute_revit_code (readOnly={readOnly}): {Truncate(code, 4000)}"); } catch { /* never fail on logging */ }

        var globals = new Globals { doc = doc, uiapp = uiapp, app = uiapp.Application };
        var options = ScriptOptions.Default
            .WithReferences(
                typeof(Document).Assembly,        // RevitAPI
                typeof(UIApplication).Assembly,   // RevitAPIUI
                typeof(Enumerable).Assembly,      // System.Linq
                typeof(object).Assembly)          // System.Private.CoreLib
            .WithImports(
                "System", "System.Linq", "System.Collections.Generic",
                "Autodesk.Revit.DB", "Autodesk.Revit.UI", "Autodesk.Revit.ApplicationServices");

        using var cts = new CancellationTokenSource(MaxRun);

        if (readOnly)
            return Run(code, globals, options, cts.Token, readOnly: true);

        // WRITE path: wrap in a transaction; commit on success, roll back on any failure/exception.
        using var tx = new Transaction(doc, "Transom: execute_revit_code");
        try
        {
            tx.Start();
            var result = Run(code, globals, options, cts.Token, readOnly: false);
            bool ok = result.TryGetValue("ok", out var okv) && okv is true;
            if (ok && tx.GetStatus() == TransactionStatus.Started) tx.Commit();
            else if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return result;
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return Failure(ex, globals.LogText, " (transaction rolled back)");
        }
    }

    private static Dictionary<string, object?> Run(string code, Globals globals, ScriptOptions options,
        CancellationToken token, bool readOnly)
    {
        try
        {
            // Roslyn scripting is async; we're on the Revit API thread and MUST stay on it (transactions are
            // thread-affine), so block here. The CancellationToken enforces the time cap cooperatively.
            var state = CSharpScript.RunAsync(code, options, globals, typeof(Globals), token)
                .GetAwaiter().GetResult();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["readOnly"] = readOnly,
                ["result"] = Describe(state.ReturnValue),
                ["log"] = globals.LogText,
            };
        }
        catch (CompilationErrorException ce)
        {
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = "compile error: " + string.Join("; ", ce.Diagnostics.Select(d => d.ToString())),
                ["log"] = globals.LogText,
            };
        }
        catch (OperationCanceledException)
        {
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = $"execution timed out (> {MaxRun.TotalSeconds:0}s) and was cancelled" + (readOnly ? "" : " (transaction rolled back)"),
                ["log"] = globals.LogText,
            };
        }
        catch (Exception ex)
        {
            return Failure(ex, globals.LogText, readOnly ? "" : " (transaction rolled back)");
        }
    }

    /// <summary>Best-effort JSON-friendly description of the snippet's return value (ids, counts, strings).</summary>
    private static object? Describe(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case bool or int or long or double or float or decimal: return value;
            case ElementId id: return id.Value;
            case Element el:
                try { return new Dictionary<string, object?> { ["id"] = el.Id.Value, ["name"] = el.Name, ["category"] = el.Category?.Name }; }
                catch { return new Dictionary<string, object?> { ["id"] = el.Id.Value }; }
            case System.Collections.IEnumerable seq:
                var items = new List<object?>();
                foreach (var item in seq) { items.Add(Describe(item)); if (items.Count >= 500) { items.Add("…(truncated)"); break; } }
                return items;
            default:
                try { return value.ToString(); } catch { return value.GetType().Name; }
        }
    }

    private static Dictionary<string, object?> Failure(Exception ex, string log, string suffix) => new()
    {
        ["ok"] = false,
        ["error"] = ex.Message + suffix,
        ["stack"] = ex.StackTrace ?? "",
        ["log"] = log,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}

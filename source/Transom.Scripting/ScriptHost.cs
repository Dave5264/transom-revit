using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Transom.Scripting;

/// <summary>
///     Compiles + runs a C# snippet via Roslyn scripting, entirely inside the isolated AssemblyLoadContext this
///     assembly was loaded into (see Transom's Core/ScriptIsolation.cs). Called by Transom.dll via REFLECTION
///     ONLY — never add a signature that forces the caller to reference a Roslyn type, and never throw across
///     the boundary: every outcome (including compile errors and cancellation) is reported through the returned
///     dictionary so the caller needs no Roslyn exception types to catch.
///
///     All parameter and return types are framework types (CoreLib is shared across every load context), so
///     type identity holds across the boundary. The globals object is typed by its OWN runtime type — an
///     assembly living in the default context — which Roslyn only reflects over, never re-loads.
/// </summary>
public static class ScriptHost
{
    /// <summary>
    ///     Returns a status envelope: {"status":"ok","value":&lt;raw return value&gt;} |
    ///     {"status":"compile","error":…} | {"status":"cancelled"} | {"status":"error","error":…,"stack":…}.
    /// </summary>
    public static Dictionary<string, object?> Run(
        string code, object globals, Assembly[] references, string[] imports, CancellationToken token)
    {
        var options = ScriptOptions.Default
            .WithReferences(references)
            .WithImports(imports);

        // We're on the Revit API thread (WPF UI thread), which carries a SynchronizationContext. Blocking it
        // with GetResult() while Roslyn's async pipeline posts continuations BACK to that same (now-blocked)
        // thread would deadlock until the cancellation cap fires. Suppress the context so continuations go to
        // the thread pool; the script BODY still executes on this thread (transactions are thread-affine).
        var prevCtx = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            // Pin context-free reflection loads (Assembly.Load by name — e.g. resolving the in-memory script
            // submission's references) to OUR isolated context first, so Roslyn's own family resolves here and
            // everything else falls through to the default context.
            using (AssemblyLoadContext.GetLoadContext(typeof(ScriptHost).Assembly)?.EnterContextualReflection())
            {
                var state = CSharpScript.RunAsync(code, options, globals, globals.GetType(), token)
                    .GetAwaiter().GetResult();
                return new Dictionary<string, object?> { ["status"] = "ok", ["value"] = state.ReturnValue };
            }
        }
        catch (CompilationErrorException ce)
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "compile",
                ["error"] = string.Join("; ", ce.Diagnostics.Select(d => d.ToString())),
            };
        }
        catch (OperationCanceledException)
        {
            return new Dictionary<string, object?> { ["status"] = "cancelled" };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "error",
                ["error"] = ex.Message,
                ["stack"] = ex.StackTrace ?? "",
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevCtx);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace Transom.Acad.Scripting;

/// <summary>
///     Compiles a C# snippet via Roslyn scripting inside the isolated AssemblyLoadContext this assembly was
///     loaded into (see Transom.Acad's IsolatedAssembly.cs). Called by Transom.Acad.dll via REFLECTION ONLY, so
///     every parameter and return type is a framework type and nothing throws across the boundary.
///
///     Compile and run are split on purpose: <see cref="Compile"/> runs on the bridge's background thread so
///     Roslyn's multi-second first compile never freezes AutoCAD, and only the returned runner is invoked on
///     AutoCAD's main thread.
/// </summary>
public static class ScriptHost
{
    /// <summary>
    ///     Returns {"status":"ok","runner":Func&lt;object, CancellationToken, Dictionary&gt;} |
    ///     {"status":"compile","error":…} | {"status":"error","error":…}. The runner returns
    ///     {"status":"ok","value":…} | {"status":"cancelled"} | {"status":"error","error":…,"stack":…}.
    /// </summary>
    public static Dictionary<string, object?> Compile(
        string code, Type globalsType, Assembly[] references, string[] imports)
    {
        try
        {
            var options = ScriptOptions.Default
                .WithReferences(references)
                .WithImports(imports);

            using (AssemblyLoadContext.GetLoadContext(typeof(ScriptHost).Assembly)?.EnterContextualReflection())
            {
                // Pin every reference to its LIVE assembly instance, so Roslyn never re-loads Transom.Acad.dll
                // from disk into a second identity (the Globals cast would then fail).
                var loader = new InteractiveAssemblyLoader();
                foreach (var reference in references) loader.RegisterDependency(reference);
                var script = CSharpScript.Create(code, options, globalsType, loader);
                var runner = script.CreateDelegate();

                Func<object, CancellationToken, Dictionary<string, object?>> run = (globals, token) => Run(runner, globals, token);
                return new Dictionary<string, object?> { ["status"] = "ok", ["runner"] = run };
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
        catch (Exception ex)
        {
            return new Dictionary<string, object?> { ["status"] = "error", ["error"] = ex.Message, ["stack"] = ex.StackTrace ?? "" };
        }
    }

    private static Dictionary<string, object?> Run(ScriptRunner<object> runner, object globals, CancellationToken token)
    {
        // AutoCAD's main thread carries a WinForms SynchronizationContext. Blocking it with GetResult() while
        // Roslyn posts continuations back to it would deadlock, so suppress the context for the run. The
        // script BODY still executes on this thread, which is what AutoCAD's document lock requires.
        var prevCtx = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using (AssemblyLoadContext.GetLoadContext(typeof(ScriptHost).Assembly)?.EnterContextualReflection())
            {
                Task<object> task = runner(globals, token);
                var value = task.GetAwaiter().GetResult();
                return new Dictionary<string, object?> { ["status"] = "ok", ["value"] = value };
            }
        }
        catch (OperationCanceledException)
        {
            return new Dictionary<string, object?> { ["status"] = "cancelled" };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?> { ["status"] = "error", ["error"] = ex.Message, ["stack"] = ex.StackTrace ?? "" };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevCtx);
        }
    }
}

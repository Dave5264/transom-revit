using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Transom.Core;

/// <summary>
///     Loads Transom.Scripting.dll (the only assembly that references Roslyn) into a DEDICATED
///     AssemblyLoadContext, so Microsoft.CodeAnalysis* binds happen in our own context — never in Revit's
///     shared default context, where another add-in (e.g. pyRevit, which ships Microsoft.CodeAnalysis 4.10)
///     may already hold an older version and win the bind (FileLoadException 0x80131621, load-order dependent).
///
///     Why a separate assembly at all: Revit 2025/2026 loads every add-in into the ONE default context
///     (EnableDynamicLoading only emits deps.json — the host must create the isolated context itself, which
///     Revit does not). Any Roslyn typeref inside Transom.dll would therefore resolve in the default context
///     no matter what we pin at run time. Moving every Roslyn touch into Transom.Scripting.dll and loading
///     THAT into our own context is the documented .NET plugin-isolation pattern
///     (https://learn.microsoft.com/dotnet/core/tutorials/creating-app-with-plugin-support).
/// </summary>
internal static class ScriptIsolation
{
    private static readonly object Gate = new();
    private static Assembly? _scriptHost;

    /// <summary>The Transom.Scripting assembly, loaded (once) into the isolated context.</summary>
    public static Assembly ScriptHostAssembly
    {
        get
        {
            lock (Gate) return _scriptHost ??= LoadIsolated();
        }
    }

    private static Assembly LoadIsolated()
    {
        var dir = Path.GetDirectoryName(typeof(ScriptIsolation).Assembly.Location) ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, "Transom.Scripting.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Transom.Scripting.dll is missing next to Transom.dll — reinstall Transom.", path);
        return new RoslynLoadContext(path, dir).LoadFromAssemblyPath(path);
    }

    private sealed class RoslynLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;
        private readonly string _dir;

        public RoslynLoadContext(string scriptHostPath, string dir) : base("Transom.Roslyn")
        {
            _dir = dir;
            // Resolves via Transom.Scripting.deps.json when deployed; optional — prefix matching below covers it.
            try { _resolver = new AssemblyDependencyResolver(scriptHostPath); }
            catch { _resolver = null; }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null) return null;

            // Never shadow the shared world: Revit API assemblies, Transom.dll itself, and the framework must
            // keep resolving in the DEFAULT context, or types passed across the boundary (Document, Globals,
            // the compiled script's own references) would have two identities and nothing would cast.
            bool isScriptHost = name.Name.Equals("Transom.Scripting", StringComparison.OrdinalIgnoreCase);
            if (!isScriptHost &&
                (name.Name.StartsWith("Transom", StringComparison.OrdinalIgnoreCase) ||
                 name.Name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)))
                return null;

            // Roslyn + its pinned transitive closure (per deps.json) load from OUR folder into THIS context.
            var viaDeps = _resolver?.ResolveAssemblyToPath(name);
            if (viaDeps != null) return LoadFromAssemblyPath(viaDeps);

            if (isScriptHost || name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            {
                var local = Path.Combine(_dir, name.Name + ".dll");
                if (File.Exists(local)) return LoadFromAssemblyPath(local);
            }

            return null; // everything else falls back to the default context (framework, RevitAPI, Transom)
        }
    }
}

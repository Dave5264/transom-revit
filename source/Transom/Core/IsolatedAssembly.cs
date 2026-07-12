using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Transom.Core;

/// <summary>
///     Loads one satellite assembly into a DEDICATED AssemblyLoadContext so a configurable family of
///     library dependencies (matched by name prefix, plus the satellite's deps.json closure when deployed)
///     binds privately — never in Revit's shared default context, where another add-in may already hold a
///     different version and win the bind (FileLoadException 0x80131621, load-order dependent).
///
///     Why a separate assembly per library family: Revit loads every add-in into the ONE default context
///     (EnableDynamicLoading only emits deps.json — the host must create the isolated context itself, which
///     Revit does not). Any isolated-library typeref inside Transom.dll would therefore resolve in the
///     default context no matter what we pin at run time. Moving every touch of the library into the
///     satellite and loading THAT here is the documented .NET plugin-isolation pattern
///     (https://learn.microsoft.com/dotnet/core/tutorials/creating-app-with-plugin-support).
///
///     Two instances exist: <see cref="ScriptIsolation"/> (Transom.Scripting.dll / Roslyn) and
///     <see cref="OfficeIsolation"/> (Transom.Office.dll / the NPOI closure).
/// </summary>
internal sealed class IsolatedAssembly
{
    private readonly object _gate = new();
    private readonly string _dllName;
    private readonly string _contextName;
    private readonly string[] _prefixes;
    private Assembly? _assembly;

    /// <param name="dllName">Satellite file name next to Transom.dll, e.g. "Transom.Office.dll".</param>
    /// <param name="contextName">Diagnostic name of the AssemblyLoadContext.</param>
    /// <param name="prefixes">Assembly-name prefixes that must resolve from the add-in folder INTO this
    /// context (the isolated library family). Everything else falls back to the default context.</param>
    public IsolatedAssembly(string dllName, string contextName, string[] prefixes)
    {
        _dllName = dllName;
        _contextName = contextName;
        _prefixes = prefixes;
    }

    /// <summary>The satellite assembly, loaded (once) into its isolated context.</summary>
    public Assembly Assembly
    {
        get
        {
            lock (_gate) return _assembly ??= Load();
        }
    }

    private Assembly Load()
    {
        var dir = Path.GetDirectoryName(typeof(IsolatedAssembly).Assembly.Location) ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, _dllName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{_dllName} is missing next to Transom.dll — reinstall Transom.", path);
        return new SatelliteLoadContext(_contextName, path, dir, _dllName, _prefixes).LoadFromAssemblyPath(path);
    }

    private sealed class SatelliteLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;
        private readonly string _dir;
        private readonly string _satelliteName;
        private readonly string[] _prefixes;

        public SatelliteLoadContext(string name, string satellitePath, string dir, string dllName, string[] prefixes)
            : base(name)
        {
            _dir = dir;
            _satelliteName = Path.GetFileNameWithoutExtension(dllName);
            _prefixes = prefixes;
            // Resolves via the satellite's deps.json when deployed; optional — prefix matching below covers it.
            try { _resolver = new AssemblyDependencyResolver(satellitePath); }
            catch { _resolver = null; }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null) return null;

            // Never shadow the shared world: Revit API assemblies, Transom.dll itself, and the framework must
            // keep resolving in the DEFAULT context, or types passed across the boundary (Document, the DTOs,
            // the IOfficeEngine interface) would have two identities and nothing would cast.
            bool isSatellite = name.Name.Equals(_satelliteName, StringComparison.OrdinalIgnoreCase);
            if (!isSatellite &&
                (name.Name.StartsWith("Transom", StringComparison.OrdinalIgnoreCase) ||
                 name.Name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)))
                return null;

            // The isolated family + its pinned transitive closure (per deps.json) load from OUR folder into
            // THIS context.
            var viaDeps = _resolver?.ResolveAssemblyToPath(name);
            if (viaDeps != null) return LoadFromAssemblyPath(viaDeps);

            if (isSatellite || MatchesPrefix(name.Name))
            {
                var local = Path.Combine(_dir, name.Name + ".dll");
                if (File.Exists(local)) return LoadFromAssemblyPath(local);
            }

            return null; // everything else falls back to the default context (framework, RevitAPI, Transom)
        }

        private bool MatchesPrefix(string simpleName)
        {
            foreach (var p in _prefixes)
                if (simpleName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}

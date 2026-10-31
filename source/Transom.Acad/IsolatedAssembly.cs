using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Transom.Acad;

/// <summary>
///     Loads Transom.Acad.Scripting.dll into a DEDICATED AssemblyLoadContext so Roslyn binds privately, never in
///     AutoCAD's default context (AutoCAD 2027 ships Microsoft.CodeAnalysis 4.10 next to acad.exe). Ported from
///     Transom's IsolatedAssembly, which solved the same clash against pyRevit inside Revit.
/// </summary>
internal static class ScriptIsolation
{
    private const string SatelliteDll = "Transom.Acad.Scripting.dll";
    private static readonly object Gate = new();
    private static Assembly? _assembly;

    public static Assembly ScriptHostAssembly
    {
        get { lock (Gate) return _assembly ??= Load(); }
    }

    private static Assembly Load()
    {
        var dir = Path.GetDirectoryName(typeof(ScriptIsolation).Assembly.Location) ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, SatelliteDll);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{SatelliteDll} is missing next to Transom.Acad.dll; redeploy the bundle.", path);
        return new SatelliteLoadContext(path, dir).LoadFromAssemblyPath(path);
    }

    private sealed class SatelliteLoadContext : AssemblyLoadContext
    {
        /// <summary>The context Transom.Acad.dll lives in. Shared-world names (Transom.Acad, the AutoCAD managed
        /// API) must resolve through it to keep one type identity across the boundary.</summary>
        private static readonly AssemblyLoadContext HostContext =
            GetLoadContext(typeof(ScriptIsolation).Assembly) ?? Default;

        private static readonly string[] SharedPrefixes = { "Transom.Acad", "acmgd", "acdbmgd", "accoremgd" };
        private const string SatelliteName = "Transom.Acad.Scripting";

        private readonly AssemblyDependencyResolver? _resolver;
        private readonly string _dir;

        public SatelliteLoadContext(string satellitePath, string dir) : base("Transom.Acad.Roslyn")
        {
            _dir = dir;
            try { _resolver = new AssemblyDependencyResolver(satellitePath); }
            catch { _resolver = null; }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null) return null;

            bool isSatellite = name.Name.Equals(SatelliteName, StringComparison.OrdinalIgnoreCase);
            if (!isSatellite && StartsWithAny(name.Name, SharedPrefixes))
            {
                try { return HostContext.LoadFromAssemblyName(name); }
                catch { return null; }
            }

            var viaDeps = _resolver?.ResolveAssemblyToPath(name);
            if (viaDeps != null) return LoadFromAssemblyPath(viaDeps);

            if (isSatellite || name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            {
                var local = Path.Combine(_dir, name.Name + ".dll");
                if (File.Exists(local)) return LoadFromAssemblyPath(local);
            }

            return null; // framework and everything else falls back to the default context
        }

        private static bool StartsWithAny(string simpleName, string[] prefixes)
        {
            foreach (var p in prefixes)
                if (simpleName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}

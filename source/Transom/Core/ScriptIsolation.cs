using System.Reflection;

namespace Transom.Core;

/// <summary>
///     Loads Transom.Scripting.dll (the only assembly that references Roslyn) into a dedicated
///     AssemblyLoadContext, so Microsoft.CodeAnalysis* binds happen in our own context — never in Revit's
///     shared default context, where another add-in (e.g. pyRevit, which ships Microsoft.CodeAnalysis 4.10)
///     may already hold an older version and win the bind (FileLoadException 0x80131621, load-order
///     dependent). Mechanics live in <see cref="IsolatedAssembly"/> (shared with <see cref="OfficeIsolation"/>).
/// </summary>
internal static class ScriptIsolation
{
    private static readonly IsolatedAssembly Iso = new(
        "Transom.Scripting.dll", "Transom.Roslyn", new[] { "Microsoft.CodeAnalysis" });

    /// <summary>The Transom.Scripting assembly, loaded (once) into the isolated context.</summary>
    public static Assembly ScriptHostAssembly => Iso.Assembly;
}

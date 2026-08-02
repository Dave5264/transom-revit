using System;

namespace Transom.Core;

/// <summary>
///     Entry point to the spreadsheet/document engine (Transom.Office.dll), loaded into a dedicated
///     AssemblyLoadContext so the NPOI closure (NPOI, SharpZipLib, BouncyCastle, MathNet, ImageSharp, …)
///     binds privately and can never clash with another add-in's copy in Revit's shared default context —
///     the same isolation pattern as <see cref="ScriptIsolation"/>/Roslyn. The rule that keeps it working is
///     stated on <see cref="IOfficeEngine"/> and in Transom.Scripting.csproj's header: those two projects are
///     the ONLY ones allowed to reference their respective closures, and no closure type may cross the
///     boundary in a signature.
///     Transom.Office compiles against Transom.dll, but at run time the context delegates Transom*/RevitAPI*/
///     framework binds back to the default context, so <see cref="IOfficeEngine"/> and the DTOs keep a single
///     type identity across the boundary — the cast below is safe and calls are ordinary interface dispatch.
/// </summary>
internal static class OfficeIsolation
{
    private static readonly IsolatedAssembly Iso = new(
        "Transom.Office.dll", "Transom.Office",
        new[]
        {
            "NPOI", "ICSharpCode", "BouncyCastle", "MathNet", "Enums.NET", "ExtendedNumerics",
            "Microsoft.IO.RecyclableMemoryStream", "SixLabors",
        });

    private static readonly Lazy<IOfficeEngine> Instance = new(() =>
    {
        var type = Iso.Assembly.GetType("Transom.Office.OfficeEngine")
                   ?? throw new InvalidOperationException("Transom.Office.OfficeEngine not found in Transom.Office.dll — reinstall Transom.");
        return (IOfficeEngine)Activator.CreateInstance(type)!;
    });

    /// <summary>The engine singleton (lazy: first export/import pays the satellite load, like Roslyn).</summary>
    public static IOfficeEngine Engine => Instance.Value;
}

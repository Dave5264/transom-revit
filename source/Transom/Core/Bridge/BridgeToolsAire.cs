using System.Text.Json;

namespace Transom.Core;

/// <summary>
///     AIRE's bridge tools, as reached over Revit's HTTP bridge.
///     <para>
///     The implementation is NOT here any more — it lives in <see cref="AireBridge"/> inside
///     Transom.Aire.Core, because AIRE never needed Revit. It spends against files and HTTPS, has no
///     document, and the old arrangement meant Claude could not touch AIRE unless Revit happened to be
///     open. The MCP shim now hosts the same code in-process and answers aire_* itself.
///     </para>
///     <para>
///     This forwarder stays for the HTTP bridge: anything already pointed at
///     <c>http://127.0.0.1:&lt;port&gt;/call</c> keeps working, and an AIRE job started through Revit shares
///     the in-process <see cref="AireJobManager"/> with the ribbon's AIRE window, so the window's Cancel
///     Batch can still stop it. Jobs the shim starts live in the shim — the cross-process lock file in
///     <see cref="AireJobManager"/> is what stops the two ever spending at the same time.
///     </para>
/// </summary>
public static partial class BridgeTools
{
    /// <summary>Routes the aire_* tools; null for anything else so Handle can fall through.</summary>
    internal static string? DispatchAire(string tool, JsonElement args) => AireBridge.Dispatch(tool, args);
}

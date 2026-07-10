using System;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;

namespace Transom.Commands;

/// <summary>
///     Read-only "Bridge Status" ribbon button: one dialog summarizing every layer Claude-assist depends on —
///     add-in version, bridge listener state (+ port), session token, deployed shim, and the Claude client
///     registration — so a user can see at a glance which step of the setup chain is missing. Performs no
///     writes and stays enabled even when Claude isn't running (that absence is itself useful status).
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class BridgeStatusCommand : ExternalCommand
{
    public override void Execute()
    {
        int port = 48810;
        try { port = TransomSettings.Load().BridgeSelfHostPort; } catch { /* default */ }

        bool bridgeOn = BridgeToggleCommand.IsBridgeRunning;
        bool tokenPresent = File.Exists(BridgeToggleCommand.TokenFilePath);
        bool shimPresent = McpRegistration.ShimPresent();
        string shimDate = "";
        if (shimPresent)
        {
            try { shimDate = File.GetLastWriteTime(McpRegistration.ShimPath).ToString("yyyy-MM-dd HH:mm"); }
            catch { /* date is cosmetic */ }
        }
        var (dataRegistered, uiRegistered) = ReadClaudeRegistration();

        string docTitle = "";
        try { docTitle = UiApplication.ActiveUIDocument?.Document?.Title ?? ""; }
        catch { /* no active doc is fine */ }

        string Mark(bool ok) => ok ? "✓" : "✗";

        var dialog = new TaskDialog("Transom — Bridge Status")
        {
            MainInstruction = bridgeOn
                ? $"Claude Bridge is ON — listening on 127.0.0.1:{port}"
                : "Claude Bridge is OFF",
            MainContent =
                $"{Mark(bridgeOn)}  Bridge listener ({(bridgeOn ? $"port {port}" : "toggle Claude Bridge on the ribbon to start it")})\n" +
                $"{Mark(tokenPresent)}  Session token ({(tokenPresent ? "written for this session" : "written when the bridge turns on")})\n" +
                $"{Mark(shimPresent)}  MCP shim deployed{(shimPresent ? $" ({shimDate})" : " — reinstall Transom or restart Revit to self-heal")}\n" +
                $"{Mark(dataRegistered)}  Data bridge registered with Claude (“transom”)\n" +
                $"{Mark(uiRegistered)}  UI-Assist registered with Claude (“transom-ui-assist”)\n" +
                (string.IsNullOrEmpty(docTitle) ? "" : $"\nActive model: {docTitle}\n") +
                $"\nTransom {AppInfo.Version}",
            FooterText = bridgeOn && tokenPresent && shimPresent && dataRegistered
                ? "Everything Claude needs is in place. In Claude Code, the transom “status” tool should answer with this version."
                : "Fix any ✗ above, then re-check. One-time setup: “Set up Claude” on the ribbon, restart Claude Code, then toggle “Claude Bridge” on.",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.Show();
    }

    /// <summary>
    ///     Best-effort read of the Claude client config (~/.claude.json): are the two Transom MCP servers
    ///     registered? Any parse/IO failure just reports "not registered" — this dialog never throws.
    /// </summary>
    private static (bool Data, bool Ui) ReadClaudeRegistration()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return (false, false);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
                return (false, false);
            return (servers.TryGetProperty("transom", out _), servers.TryGetProperty("transom-ui-assist", out _));
        }
        catch
        {
            return (false, false);
        }
    }
}

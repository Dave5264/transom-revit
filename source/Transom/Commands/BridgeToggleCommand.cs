using System;
using System.IO;
using System.Security.Cryptography;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;

namespace Transom.Commands;

/// <summary>
///     Toggles the admin-free Claude-assist bridge on and off from the ribbon. On first enable it
///     creates the <see cref="BridgeServer"/>, a <see cref="BridgeEventHandler"/> and its
///     <see cref="ExternalEvent"/>, wires the server's loopback dispatch delegate to marshal each
///     request onto Revit's API thread via <see cref="BridgeEventHandler.RunOnRevitThread"/>, reads
///     the port from <see cref="TransomSettings.BridgeSelfHostPort"/>, and starts listening on
///     <c>127.0.0.1</c>. Toggling again stops the server. The server + handler are held in static
///     fields so the toggle state persists across command invocations.
///     <para>
///     Ribbon registration of this command is added in Application.cs during integration
///     (e.g. <c>panel.AddPushButton&lt;BridgeToggleCommand&gt;("Claude\nBridge")</c>).
///     </para>
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class BridgeToggleCommand : ExternalCommand
{
    private static BridgeServer? _server;
    private static BridgeEventHandler? _handler;
    private static Autodesk.Revit.UI.ExternalEvent? _event;

    public override void Execute()
    {
        // If it's already running, this invocation stops it.
        if (_server is { IsRunning: true })
        {
            _server.Stop();
            DeleteToken();
            TaskDialog.Show("Transom — Claude Bridge", "The Claude-assist bridge has been turned off.");
            return;
        }

        var settings = TransomSettings.Load();
        var port = settings.BridgeSelfHostPort;

        // Lazily build the handler + ExternalEvent once; reuse across enable/disable cycles.
        _handler ??= new BridgeEventHandler();
        _event ??= Autodesk.Revit.UI.ExternalEvent.Create(_handler);
        _server ??= new BridgeServer();

        var handler = _handler;
        var evt = _event;

        // Per-session capability token: written to a per-user file the shim reads, and required on every
        // /call. This is the authorization boundary (loopback alone is not) — a web page can't read the
        // file, so CSRF-to-localhost writes are blocked even if they reach the port.
        var token = NewToken();
        WriteToken(token);

        try
        {
            // The server's accept loop calls this on a background thread for every request; the handler
            // hops the work onto Revit's API thread and returns the JSON response.
            _server.Start(port, json => handler.RunOnRevitThread(json, evt), token);

            TaskDialog.Show("Transom — Claude Bridge",
                $"The Claude-assist bridge is on.\n\n" +
                $"Listening on 127.0.0.1:{port} (loopback only — no admin, no firewall prompt).\n\n" +
                "Toggle this button again to turn it off.");
        }
        catch (System.Exception ex)
        {
            try { _server.Stop(); } catch { /* ignore */ }
            DeleteToken();
            Transom.Views.ReportDialog.Show("Transom — Claude Bridge",
                $"Couldn't start the Claude-assist bridge on port {port}.",
                "Another instance may already be using that port. Change BridgeSelfHostPort in settings and retry.\n\n" +
                "--- error ---\n" + ex,
                isError: true);
        }
    }

    /// <summary>The per-user token file the MCP shim reads to authenticate to the bridge (user-only location).</summary>
    internal static string TokenFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Transom", "bridge.token");

    private static string NewToken()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static void WriteToken(string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenFilePath)!);
            File.WriteAllText(TokenFilePath, token);
        }
        catch { /* best-effort; if it can't be written the shim simply can't authenticate */ }
    }

    private static void DeleteToken()
    {
        try { if (File.Exists(TokenFilePath)) File.Delete(TokenFilePath); } catch { /* ignore */ }
    }
}

using System;
using System.IO;
using System.Security.Cryptography;

namespace Transom.Core;

/// <summary>
///     Owns the Claude-assist bridge lifecycle (listener + session token), decoupled from any ribbon command
///     so the Settings toggle — modeless WPF, no Revit API context — can start and stop it.
///     <para>
///     <see cref="Initialize"/> MUST be called from a valid API context (Application.OnStartup): it eagerly
///     creates the <see cref="BridgeEventHandler"/> and its <see cref="Autodesk.Revit.UI.ExternalEvent"/>,
///     which Revit only allows inside an API context. After that, <see cref="Start"/>/<see cref="Stop"/> are
///     safe from any thread. Replaces the retired BridgeToggleCommand ("Claude Bridge" ribbon toggle).
///     </para>
/// </summary>
public static class BridgeRuntime
{
    private static BridgeServer? _server;
    private static BridgeEventHandler? _handler;
    private static Autodesk.Revit.UI.ExternalEvent? _event;

    /// <summary>Live listener state (read by the Settings status panel; never mutates).</summary>
    public static bool IsRunning => _server is { IsRunning: true };

    /// <summary>The port the listener was last started on (0 = never started).</summary>
    public static int RunningPort { get; private set; }

    /// <summary>Create the handler + ExternalEvent + server. Call once from Application.OnStartup (API context).</summary>
    public static void Initialize()
    {
        _handler ??= new BridgeEventHandler();
        _event ??= Autodesk.Revit.UI.ExternalEvent.Create(_handler);
        _server ??= new BridgeServer();
    }

    /// <summary>
    ///     Start listening on 127.0.0.1:<paramref name="port"/> (writes a fresh session token first).
    ///     Returns null on success, else a user-facing error message. No-op success if already running.
    /// </summary>
    public static string? Start(int port)
    {
        if (_server is { IsRunning: true }) return null;
        if (_server is null || _handler is null || _event is null)
            return "The bridge wasn't initialized at startup — restart Revit and try again.";

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
            RunningPort = port;
            return null;
        }
        catch (Exception ex)
        {
            try { _server.Stop(); } catch { /* ignore */ }
            DeleteToken();
            return $"Couldn't start the Claude-assist bridge on port {port}. " +
                   "Another instance may already be using that port — change the bridge port in Settings and retry.\n\n" + ex.Message;
        }
    }

    /// <summary>Stop the listener and delete the session token. Safe to call when not running.</summary>
    public static void Stop()
    {
        try { _server?.Stop(); } catch { /* ignore */ }
        DeleteToken();
    }

    /// <summary>The per-user token file the MCP shim reads to authenticate to the bridge (user-only location).</summary>
    public static string TokenFilePath =>
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

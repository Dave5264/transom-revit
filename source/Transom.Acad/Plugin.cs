using System;
using System.IO;
using System.Security.Cryptography;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(Transom.Acad.Plugin))]
[assembly: CommandClass(typeof(Transom.Acad.Commands))]

namespace Transom.Acad;

/// <summary>
///     Entry point. The autoloader bundle loads this on AutoCAD startup, and Initialize runs on AutoCAD's main
///     thread, which is what <see cref="MainThread.Init"/> needs. Unlike the Revit add-in (Claude Assist toggle),
///     installing the AutoCAD feature IS the opt-in, so the bridge starts immediately and the MCP shim is
///     installed and registered on every startup (<see cref="McpSetup"/>, idempotent, off the main thread).
/// </summary>
public sealed class Plugin : IExtensionApplication
{
    public const int DefaultPort = 48830;
    public static readonly string Version = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal static BridgeServer? Server;
    internal static int Port;
    internal static string? StartError;
    private static string? _token;

    public void Initialize()
    {
        try
        {
            MainThread.Init();
            Port = ResolvePort();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var server = new BridgeServer();
            server.Start(Port, Dispatcher.Dispatch, token);
            // Written only after the bind succeeded, so a second AutoCAD that lost the port never overwrites
            // the token the first instance is serving.
            Paths.WriteToken(token);
            _token = token;
            Server = server;
            Log.Write($"Transom for AutoCAD {Version} bridge listening on 127.0.0.1:{Port}");
        }
        catch (System.Exception ex)
        {
            StartError = ex.Message;
            Log.Write($"bridge failed to start: {ex}");
        }

        // File copies and a JSON merge into the user's Claude configs: nothing that needs AutoCAD's main thread,
        // and nothing startup should wait on.
        var port = Port == 0 ? DefaultPort : Port;
        System.Threading.Tasks.Task.Run(() => McpSetup.EnsureShimAndRegister(port));
    }

    public void Terminate()
    {
        try { Server?.Stop(); } catch { /* AutoCAD is exiting */ }
        if (_token != null) Paths.DeleteTokenIfOwned(_token);
    }

    private static int ResolvePort()
    {
        var env = Environment.GetEnvironmentVariable("TRANSOMACAD_BRIDGE_PORT");
        return int.TryParse(env, out var p) && p > 1024 ? p : DefaultPort;
    }
}

public sealed class Commands
{
    /// <summary>TRANSOMACAD: prints the bridge state to the command line, so a user can confirm the load.</summary>
    [CommandMethod("TRANSOMACAD")]
    public void Status()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        if (Plugin.Server?.IsRunning == true)
            ed.WriteMessage($"\nTransom for AutoCAD {Plugin.Version}: bridge listening on 127.0.0.1:{Plugin.Port}. Log: {Paths.LogFile}");
        else
            ed.WriteMessage($"\nTransom for AutoCAD {Plugin.Version}: bridge NOT running ({Plugin.StartError ?? "unknown reason"}). Log: {Paths.LogFile}");
        ed.WriteMessage($"\nClaude MCP server '{McpSetup.ServerKey}': {McpSetup.ShimPath}");
    }
}

internal static class Paths
{
    /// <summary>%LocalAppData%\Transom\acad: beside the Revit add-in's own Transom folder, but never sharing
    /// its bridge.token (the two bridges run side by side on different ports).</summary>
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transom", "acad");
    public static readonly string TokenFile = Path.Combine(Root, "bridge.token");
    public static readonly string LogFile = Path.Combine(Root, "bridge.log");

    public static void WriteToken(string token)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(TokenFile, token);
    }

    public static void DeleteTokenIfOwned(string token)
    {
        try
        {
            if (File.Exists(TokenFile) && File.ReadAllText(TokenFile).Trim() == token) File.Delete(TokenFile);
        }
        catch { /* best effort */ }
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Root);
                var info = new FileInfo(Paths.LogFile);
                if (info.Exists && info.Length > 2 * 1024 * 1024) info.Delete();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* never fail a tool call on logging */ }
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}

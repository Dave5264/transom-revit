using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Transom.Core;

/// <summary>One ✓/✗ row in the Settings status panel.</summary>
public sealed record ClaudeStatusRow(bool Ok, string Label, string Detail);

/// <summary>
///     Point-in-time snapshot of every layer Claude-assist depends on — bridge listener, session token,
///     deployed shim, and the two Claude client registrations. Feeds the always-visible status panel on the
///     Settings tab (replaces the retired "Bridge Status" ribbon dialog). All checks are read-only and
///     best-effort: any IO/parse failure just reports that layer as missing.
/// </summary>
public sealed class ClaudeStatus
{
    public bool BridgeOn { get; init; }
    public bool TokenPresent { get; init; }
    public bool ShimPresent { get; init; }
    public bool DataRegistered { get; init; }
    public bool UiRegistered { get; init; }
    public bool ClaudeAppRunning { get; init; }
    public int Port { get; init; }
    public string ShimDate { get; init; } = "";

    /// <summary>"Not set up" = nothing registered and no shim — the state before the first toggle-on.</summary>
    public bool IsSetUp => ShimPresent || DataRegistered || UiRegistered;

    public bool AllReady => BridgeOn && TokenPresent && ShimPresent && DataRegistered;

    public static ClaudeStatus Capture(int port)
    {
        bool shimPresent = McpRegistration.ShimPresent();
        string shimDate = "";
        if (shimPresent)
        {
            try { shimDate = File.GetLastWriteTime(McpRegistration.ShimPath).ToString("yyyy-MM-dd HH:mm"); }
            catch { /* date is cosmetic */ }
        }
        var (data, ui) = ReadClaudeRegistration();

        return new ClaudeStatus
        {
            BridgeOn = BridgeRuntime.IsRunning,
            TokenPresent = File.Exists(BridgeRuntime.TokenFilePath),
            ShimPresent = shimPresent,
            ShimDate = shimDate,
            DataRegistered = data,
            UiRegistered = ui,
            ClaudeAppRunning = ClaudeDetector.IsRunning(),
            Port = port,
        };
    }

    /// <summary>The panel rows, in the same order the old Bridge Status dialog listed them.</summary>
    public IReadOnlyList<ClaudeStatusRow> Rows() => new[]
    {
        new ClaudeStatusRow(BridgeOn, "Bridge listening", BridgeOn ? $"127.0.0.1:{Port}" : "off"),
        new ClaudeStatusRow(TokenPresent, "Session token", TokenPresent ? "written for this session" : "written when the bridge turns on"),
        new ClaudeStatusRow(ShimPresent, "MCP shim deployed", ShimPresent ? ShimDate : "reinstall Transom or restart Revit to self-heal"),
        new ClaudeStatusRow(DataRegistered, "Data bridge registered with Claude", "“transom”"),
        new ClaudeStatusRow(UiRegistered, "UI-Assist registered with Claude", "“transom-ui-assist”"),
        new ClaudeStatusRow(ClaudeAppRunning, "Claude app running", ClaudeAppRunning ? "detected" : "start Claude Desktop / Claude Code to connect"),
    };

    /// <summary>
    ///     Best-effort read of the Claude client config (~/.claude.json): are the two Transom MCP servers
    ///     registered? Any parse/IO failure just reports "not registered" — this check never throws.
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

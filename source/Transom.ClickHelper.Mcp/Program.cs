using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transom.ClickHelper.Mcp;

/// <summary>
/// Transom Click Helper MCP — a minimal Model Context Protocol server over stdio (JSON-RPC 2.0,
/// NEWLINE-DELIMITED per the MCP stdio transport: one line of JSON per message, no embedded newlines,
/// stdout carries only valid MCP messages). It exposes Revit UI-clicking tools to Claude and fulfils each
/// one by running the Transom.ClickHelper engine exe, which performs UI Automation against the live Revit
/// window from its own process.
///
/// Framing mirrors Transom.McpShim so this server plugs into the same host. stdout is the protocol
/// channel only; all diagnostics go to stderr.
/// </summary>
internal static class Program
{
    /// <summary>Newest MCP protocol version this server knows — used only when the client's initialize
    /// omits protocolVersion; otherwise the server ECHOES the client's requested version.</summary>
    private const string FallbackProtocolVersion = "2025-06-18";
    private const string ServerName = "transom-ui-assist";
    private const string ServerVersion = "1.0.0";

    private static string _exePath = "";

    private static int Main(string[] args)
    {
        _exePath = ResolveExePath(args);
        Log($"Click Helper MCP starting. Helper exe: {(_exePath.Length == 0 ? "<not found>" : _exePath)}");

        // MCP stdio transport is newline-delimited JSON: one '\n'-terminated line per message.
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        try { RunLoop(stdin, stdout); }
        catch (Exception ex) { Log($"Fatal: {ex}"); return 1; }

        Log("Click Helper MCP: stdin closed, exiting.");
        return 0;
    }

    private static void RunLoop(TextReader stdin, TextWriter stdout)
    {
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;     // tolerate blank lines

            JsonNode? response;
            try { response = HandleMessage(line); }
            catch (Exception ex) { Log($"Error handling message: {ex.Message}"); response = null; }

            if (response is not null) WriteMessage(stdout, response);
        }
    }

    // ----------------------------------------------------------------- dispatch

    private static JsonNode? HandleMessage(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException ex) { Log($"Invalid JSON: {ex.Message}"); return Error(null, -32700, "Parse error"); }

        if (root is not JsonObject obj) return Error(null, -32600, "Invalid Request");

        string? method = obj["method"]?.GetValue<string>();
        JsonNode? id = obj["id"];
        bool isNotification = id is null;

        if (method is null) return isNotification ? null : Error(id, -32600, "Invalid Request");

        switch (method)
        {
            case "initialize": return Result(id, HandleInitialize(obj["params"] as JsonObject));
            case "notifications/initialized":
            case "initialized": return null;
            case "tools/list": return Result(id, HandleToolsList());
            case "tools/call": return Result(id, HandleToolsCall(obj["params"] as JsonObject));
            case "ping": return Result(id, new JsonObject());
            default:
                if (isNotification) { Log($"Ignoring unknown notification: {method}"); return null; }
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject HandleInitialize(JsonObject? prms)
    {
        // MCP spec: echo the client's requested protocolVersion (this server is version-agnostic at the
        // JSON-RPC level); fall back to our newest-known only when the client omits the field.
        string version = prms?["protocolVersion"]?.GetValue<string>() is { Length: > 0 } v
            ? v
            : FallbackProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
            // Operating manual handed to the model (distilled from the field-tested learning log). These
            // rules are load-bearing — ignoring them is why earlier attempts failed.
            ["instructions"] = Instructions,
        };
    }

    /// <summary>
    ///     Condensed, field-tested guidance for driving Revit's UI via these tools. Surfaced to the model
    ///     through the MCP initialize result so it uses the techniques that actually work.
    /// </summary>
    private const string Instructions =
        "These tools drive Revit's UI for commands that have NO Revit API (Edit Group, Finish, modal " +
        "dialogs, editing a parameter value in the Properties palette). Pair them with the Transom data " +
        "bridge (Revit API) for selection and verification.\n\n" +
        "ALWAYS, in order:\n" +
        "1. revit_tile FIRST — Revit must be visible and side-by-side, or clicks/keys land on the wrong " +
        "window.\n" +
        "2. The Revit API is DEAD while in group-edit mode (it times out). Do all selection/reads/writes " +
        "via the API BEFORE entering edit mode and AFTER finishing — never during.\n" +
        "3. To pinpoint a member to click: override its colour via the API, then revit_screenshot " +
        "(screen=true to see the model viewport; the default PrintWindow shows UI chrome but a black " +
        "drawing area), then click its highlight. NOTE: a selected group shows the override as the INVERSE " +
        "colour — select none to confirm the true colour.\n" +
        "4. Keyboard needs a focus click in the SAME step: revit_keys takes a canvas x,y (for view " +
        "shortcuts like 'tl'); revit_type takes the value cell's x,y and types into it (set enter=true to " +
        "commit). A separate click then type loses focus to a permission prompt.\n" +
        "5. The Properties palette value cells are NOT exposed to UI Automation — revit_type (click+type) " +
        "is the only way to set a parameter value. Use revit_scroll to bring the parameter into view first.\n" +
        "6. Editing a member inside Edit Group edits the group DEFINITION, so it propagates to ALL " +
        "instances of that group.\n\n" +
        "Group-parameter-edit workflow: API selects the group -> revit_edit_group -> click the member " +
        "(revit_click_xy on its highlight) -> revit_scroll to the parameter -> revit_type (enter=true) -> " +
        "revit_finish_group -> verify with the API. If a modal blocks you, revit_list_dialogs + " +
        "revit_click_dialog.";

    private static JsonObject HandleToolsList()
    {
        JsonObject PidProp() => Prop("number", "Optional Revit process id to target when several Revit " +
                                               "instances are open (from revit_status).");

        var tools = new JsonArray
        {
            Tool("revit_status",
                "Check that Revit is running and report the process id and main-window title. Call this " +
                "first to confirm a Revit window is available before clicking.",
                ObjSchema(new() { ["pid"] = PidProp() })),

            Tool("revit_edit_group",
                "Click the 'Edit Group' ribbon button to enter group-edit mode. A single model group must " +
                "already be selected (use the Transom bridge / Revit API to select it first). There is no " +
                "Revit API for this command — it is driven through the UI.",
                ObjSchema(new() { ["pid"] = PidProp() })),

            Tool("revit_finish_group",
                "Click 'Finish' to finish editing a group (commits the group edit). You must currently be " +
                "in group-edit mode. No Revit API exists for this; it is driven through the UI.",
                ObjSchema(new() { ["pid"] = PidProp() })),

            Tool("revit_cancel_group",
                "Click 'Cancel' to cancel group-edit mode without committing changes. You must currently be " +
                "in group-edit mode.",
                ObjSchema(new() { ["pid"] = PidProp() })),

            Tool("revit_click_by_id",
                "General element-first click: invoke any Revit ribbon/dialog control by its UI Automation " +
                "AutomationId (e.g. 'ID_FINISH_GROUP_EDIT_MODE', or '2007'). Use revit_find to discover ids.",
                ObjSchema(new()
                {
                    ["automationId"] = Prop("string", "The control's UI Automation AutomationId."),
                    ["pid"] = PidProp(),
                }, required: new[] { "automationId" })),

            Tool("revit_click_xy",
                "Pixel fallback: left-click an absolute screen coordinate (physical pixels; may be negative " +
                "on a left-hand monitor). Use ONLY when there is no named element to target — prefer " +
                "revit_click_by_id. Coordinates typically come from revit_find (centerX/centerY) or by " +
                "reasoning over a revit_screenshot plus its reported x/y offset.",
                ObjSchema(new()
                {
                    ["x"] = Prop("number", "Absolute screen X in physical pixels."),
                    ["y"] = Prop("number", "Absolute screen Y in physical pixels."),
                    ["pid"] = PidProp(),
                }, required: new[] { "x", "y" })),

            Tool("revit_find",
                "Discover Revit controls whose name/help/AutomationId contains the given text. Returns each " +
                "match's name, controlType, automationId, enabled/onscreen/invokable flags, and click center " +
                "(centerX/centerY). Use this to find what to feed revit_click_by_id or revit_click_xy.",
                ObjSchema(new()
                {
                    ["text"] = Prop("string", "Substring to search for (case-insensitive), e.g. 'finish'."),
                    ["pid"] = PidProp(),
                }, required: new[] { "text" })),

            Tool("revit_screenshot",
                "Capture the Revit window as a PNG and return it as an image so you can SEE the current UI " +
                "(ribbon, dialogs). By default uses PrintWindow, which captures Revit's own pixels even when " +
                "it is behind other windows and without stealing focus (the 3D viewport may be blank this " +
                "way). Set screen=true to instead bring Revit forward and capture the composited screen " +
                "(faithful viewport, but it takes focus). The reply also reports the window's screen x/y so " +
                "you can convert in-image positions to absolute coordinates for revit_click_xy.",
                ObjSchema(new()
                {
                    ["screen"] = Prop("boolean", "If true, foreground + screen-grab (faithful viewport). Default false."),
                    ["pid"] = PidProp(),
                })),

            Tool("revit_tile",
                "Tile Revit and Claude side-by-side on Revit's monitor so Revit is visible and not occluded. " +
                "Do this FIRST in any UI-driving session: clicks land by screen coordinate and keystrokes go " +
                "to the visible foreground window, so an occluded Revit can't be driven. Revit goes left by default.",
                ObjSchema(new()
                {
                    ["revitSide"] = Prop("string", "Which half Revit takes: 'left' (default) or 'right'."),
                    ["pid"] = PidProp(),
                })),

            Tool("revit_keys",
                "Send a Revit keyboard SHORTCUT (e.g. 'tl' = Thin Lines, 'vg' = Visibility/Graphics, 'zf' = Zoom " +
                "Fit). Provide x,y of a point in the drawing CANVAS — the tool clicks there first to give the " +
                "canvas keyboard focus, then sends the shortcut, atomically (so a permission prompt can't drop " +
                "focus in between). The focus-click may change selection, which is harmless for a shortcut.",
                ObjSchema(new()
                {
                    ["shortcut"] = Prop("string", "Shortcut letters, e.g. 'tl'."),
                    ["x"] = Prop("number", "Screen X of a canvas point to click for keyboard focus (recommended)."),
                    ["y"] = Prop("number", "Screen Y of a canvas point to click for keyboard focus (recommended)."),
                    ["pid"] = PidProp(),
                }, required: new[] { "shortcut" })),

            Tool("revit_type",
                "Type text into a Properties value cell (or any field) — the way to edit a parameter value, " +
                "since the Properties palette cells aren't exposed to UI Automation. Provide x,y of the FIELD: " +
                "the tool clicks it to focus and types in one atomic step (so a permission prompt can't drop the " +
                "field focus). Set enter=true to commit the cell (Enter) in the same step. Use revit_scroll first " +
                "if the parameter isn't visible.",
                ObjSchema(new()
                {
                    ["text"] = Prop("string", "The text to type into the field."),
                    ["x"] = Prop("number", "Screen X of the value cell to click + type into."),
                    ["y"] = Prop("number", "Screen Y of the value cell."),
                    ["enter"] = Prop("boolean", "Press Enter after typing to commit the cell. Default false."),
                    ["pid"] = PidProp(),
                }, required: new[] { "text", "x", "y" })),

            Tool("revit_scroll",
                "Scroll the mouse wheel at a screen point — e.g. to bring a parameter into view in the Properties " +
                "palette before typing into its cell. Negative notches scroll down.",
                ObjSchema(new()
                {
                    ["x"] = Prop("number", "Screen X to scroll at (e.g. over the Properties palette)."),
                    ["y"] = Prop("number", "Screen Y to scroll at."),
                    ["notches"] = Prop("number", "Wheel notches; negative scrolls down (e.g. -4)."),
                    ["pid"] = PidProp(),
                }, required: new[] { "x", "y", "notches" })),

            Tool("revit_list_dialogs",
                "List Revit's open modal dialogs — these are SEPARATE top-level windows the main-window tools " +
                "can't see (e.g. the 'changes to groups are allowed only in group edit mode' error). Returns each " +
                "dialog's title, message text, and button names. Use before revit_click_dialog.",
                ObjSchema(new() { ["pid"] = PidProp() })),

            Tool("revit_click_dialog",
                "Click a button in an open Revit modal dialog (e.g. 'Cancel', 'OK', 'Ungroup'). With no button it " +
                "safe-dismisses by trying Cancel then Close. Brings the dialog to the foreground first.",
                ObjSchema(new()
                {
                    ["button"] = Prop("string", "Button label to click; omit to safe-dismiss (Cancel/Close)."),
                    ["pid"] = PidProp(),
                })),
        };

        return new JsonObject { ["tools"] = tools };
    }

    private static JsonObject HandleToolsCall(JsonObject? prms)
    {
        string? name = prms?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return ToolError("tools/call is missing the 'name' parameter.");

        if (_exePath.Length == 0 || !File.Exists(_exePath))
            return ToolError("Click Helper engine not found. Set TRANSOM_CLICKHELPER_EXE to the full path " +
                             "of Transom.ClickHelper.exe, or pass --exe <path> when launching this server.");

        JsonObject a = prms?["arguments"] as JsonObject ?? new JsonObject();

        List<string> cliArgs;
        try { cliArgs = BuildArgs(name, a); }
        catch (ToolArgException ex) { return ToolError(ex.Message); }

        if (cliArgs.Count == 0) return ToolError($"Unknown tool '{name}'.");

        // Always machine-readable.
        cliArgs.Add("--json");

        var (exit, stdout, stderr) = RunExe(cliArgs);
        if (stdout.Length == 0 && exit != 0)
            return ToolError($"Click Helper exited {exit} with no output. stderr: {stderr}");

        // The screenshot tool additionally returns the PNG as an image content item.
        if (name == "revit_screenshot")
            return ScreenshotResult(stdout);

        bool isError = ReportedError(stdout);
        return new JsonObject
        {
            ["content"] = new JsonArray { TextItem(stdout.Trim().Length > 0 ? stdout.Trim() : stderr.Trim()) },
            ["isError"] = isError,
        };
    }

    private sealed class ToolArgException(string message) : Exception(message);

    /// <summary>Translates an MCP tool name + arguments into Click Helper CLI arguments.</summary>
    private static List<string> BuildArgs(string tool, JsonObject a)
    {
        List<string> args = tool switch
        {
            "revit_status"        => new() { "status" },
            "revit_edit_group"    => new() { "edit" },
            "revit_finish_group"  => new() { "finish" },
            "revit_cancel_group"  => new() { "cancel" },
            "revit_click_by_id"   => new() { "click-id", ReqString(a, "automationId") },
            "revit_click_xy"      => new() { "click-xy", ReqNum(a, "x"), ReqNum(a, "y") },
            "revit_find"          => new() { "find", ReqString(a, "text") },
            "revit_screenshot"    => BuildScreenshotArgs(a),
            "revit_tile"          => BuildTileArgs(a),
            "revit_keys"          => BuildKeysArgs(a),
            "revit_type"          => BuildTypeArgs(a),
            "revit_scroll"        => new() { "scroll", ReqNum(a, "x"), ReqNum(a, "y"), ReqNum(a, "notches") },
            "revit_list_dialogs"  => new() { "dialogs" },
            "revit_click_dialog"  => BuildClickDialogArgs(a),
            _                     => new(),
        };

        // Optional pid applies to every tool.
        if (a["pid"] is JsonNode pid && pid.GetValueKind() == JsonValueKind.Number)
        {
            args.Add("--pid");
            args.Add(pid.GetValue<double>().ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }
        return args;
    }

    private static List<string> BuildScreenshotArgs(JsonObject a)
    {
        var args = new List<string> { "screenshot" };
        if (a["screen"]?.GetValueKind() == JsonValueKind.True) args.Add("--screen");
        return args;
    }

    private static List<string> BuildTileArgs(JsonObject a)
    {
        var args = new List<string> { "tile" };
        if (a["revitSide"]?.GetValue<string>() is { } side && (side == "left" || side == "right"))
        { args.Add("--revit-side"); args.Add(side); }
        return args;
    }

    private static List<string> BuildKeysArgs(JsonObject a)
    {
        // --at (option) must precede the positional shortcut; the engine treats positional[1..] as the sequence.
        var args = new List<string> { "keys" };
        if (HasNum(a, "x") && HasNum(a, "y")) args.Add($"--at={ReqNum(a, "x")},{ReqNum(a, "y")}");
        args.Add(ReqString(a, "shortcut"));
        return args;
    }

    private static List<string> BuildTypeArgs(JsonObject a)
    {
        // Order matters: --enter BEFORE --at (so the engine parses --enter as a flag, not as the text's value),
        // then --at, then the positional text last.
        var args = new List<string> { "type" };
        if (a["enter"]?.GetValueKind() == JsonValueKind.True) args.Add("--enter");
        args.Add($"--at={ReqNum(a, "x")},{ReqNum(a, "y")}");
        args.Add(ReqString(a, "text"));
        return args;
    }

    private static List<string> BuildClickDialogArgs(JsonObject a)
    {
        var args = new List<string> { "click-dialog" };
        if (a["button"]?.GetValue<string>() is { Length: > 0 } b) args.Add(b);
        return args;
    }

    private static bool HasNum(JsonObject a, string key) => a[key]?.GetValueKind() == JsonValueKind.Number;

    private static string ReqString(JsonObject a, string key)
    {
        var v = a[key];
        if (v is null || v.GetValueKind() != JsonValueKind.String || string.IsNullOrEmpty(v.GetValue<string>()))
            throw new ToolArgException($"missing or empty required argument '{key}'.");
        return v.GetValue<string>();
    }

    private static string ReqNum(JsonObject a, string key)
    {
        var v = a[key];
        if (v is null || v.GetValueKind() != JsonValueKind.Number)
            throw new ToolArgException($"missing required numeric argument '{key}'.");
        return v.GetValue<double>().ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Builds the tools/call result for revit_screenshot: image item + metadata text item.</summary>
    private static JsonObject ScreenshotResult(string stdout)
    {
        if (ReportedError(stdout))
            return new JsonObject { ["content"] = new JsonArray { TextItem(stdout.Trim()) }, ["isError"] = true };

        string? path = null;
        try { path = JsonNode.Parse(stdout)?["path"]?.GetValue<string>(); }
        catch { /* fall through */ }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new JsonObject { ["content"] = new JsonArray { TextItem(stdout.Trim()) }, ["isError"] = true };

        string b64;
        try { b64 = Convert.ToBase64String(File.ReadAllBytes(path)); }
        catch (Exception ex) { return ToolError($"captured screenshot but could not read it: {ex.Message}"); }

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "image", ["data"] = b64, ["mimeType"] = "image/png" },
                TextItem(stdout.Trim()),   // path + window x/y/width/height + method
            },
            ["isError"] = false,
        };
    }

    // ------------------------------------------------------------- run the exe

    private static (int exit, string stdout, string stderr) RunExe(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "failed to start helper process");
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return (-1, so, "helper timed out"); }
            return (p.ExitCode, so, se);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    // ------------------------------------------------------- exe path resolution

    private static string ResolveExePath(string[] args)
    {
        // 1) explicit --exe <path>
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--exe" && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith("--exe=", StringComparison.Ordinal)) return args[i]["--exe=".Length..];
        }
        // 2) environment variable
        var env = Environment.GetEnvironmentVariable("TRANSOM_CLICKHELPER_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        // 3) candidate paths relative to this server's base directory
        string baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[]
                 {
                     "Transom.ClickHelper.exe",                                               // co-deployed sibling (shipped)
                     Path.Combine("..", "..", "..", "..", "Transom.ClickHelper", "bin", "Release", "net8.0-windows", "Transom.ClickHelper.exe"),
                     Path.Combine("..", "..", "..", "..", "Transom.ClickHelper", "bin", "Debug",   "net8.0-windows", "Transom.ClickHelper.exe"),
                 })
        {
            try { var full = Path.GetFullPath(Path.Combine(baseDir, rel)); if (File.Exists(full)) return full; }
            catch { /* ignore */ }
        }
        return "";
    }

    // -------------------------------------------------------- schema helpers

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name, ["description"] = description, ["inputSchema"] = inputSchema,
    };

    private static JsonObject ObjSchema(Dictionary<string, JsonNode?> props, string[]? required = null)
    {
        var p = new JsonObject();
        foreach (var kv in props) p[kv.Key] = kv.Value;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = p, ["additionalProperties"] = false };
        if (required is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var r in required) arr.Add(r);
            schema["required"] = arr;
        }
        return schema;
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject TextItem(string text) => new() { ["type"] = "text", ["text"] = text };

    private static bool ReportedError(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject o && o.TryGetPropertyValue("ok", out var ok) && ok is not null)
                return ok.GetValueKind() == JsonValueKind.False;
        }
        catch { return true; }   // non-JSON output is itself an error
        return false;
    }

    // -------------------------------------------------- JSON-RPC envelopes

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonObject ToolError(string text) => new()
    {
        ["content"] = new JsonArray { TextItem(text) }, ["isError"] = true,
    };

    // ----------------------------------------------------------------- framing

    /// <summary>Write one MCP message as a single line of JSON + '\n', then flush. ToJsonString emits
    /// single-line JSON (no embedded newlines), satisfying the stdio framing requirement.</summary>
    private static void WriteMessage(TextWriter stdout, JsonNode message)
    {
        stdout.Write(message.ToJsonString());
        stdout.Write('\n');
        stdout.Flush();
    }

    private static void Log(string message) => Console.Error.WriteLine($"[click-helper-mcp] {message}");
}

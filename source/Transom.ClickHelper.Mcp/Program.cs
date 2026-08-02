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
            // A request WITH an id must always get a reply — swallowing the exception leaves the client
            // waiting on that id until its own timeout, with only a stderr line to explain it.
            catch (Exception ex) { Log($"Error handling message: {ex.Message}"); response = ErrorReplyFor(line, ex); }

            if (response is not null) WriteMessage(stdout, response);
        }
    }

    /// <summary>Best-effort error reply for a message that threw during dispatch: carries the request's id
    /// when one can still be parsed from the raw line, else null (a notification gets no reply).</summary>
    private static JsonNode? ErrorReplyFor(string body, Exception ex)
    {
        try
        {
            var id = (JsonNode.Parse(body) as JsonObject)?["id"];
            return id is null ? null : Error(id, -32603, $"Internal error: {ex.Message}");
        }
        catch { return null; }
    }

    // ----------------------------------------------------------------- dispatch

    private static JsonNode? HandleMessage(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException ex) { Log($"Invalid JSON: {ex.Message}"); return Error(null, -32700, "Parse error"); }

        if (root is not JsonObject obj) return Error(null, -32600, "Invalid Request");

        string? method = obj["method"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
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
        "These tools drive Revit's UI for actions with NO Revit API — Edit Group mode, modal dialogs, and " +
        "the Properties palette. Pair them with the Revit API (the Transom 'transom' bridge / " +
        "execute_revit_code) for selection and verification.\n\n" +
        "ALWAYS FIRST: revit_tile, so Revit is visible side-by-side. Clicks land by screen coordinate and " +
        "keystrokes go to the foreground window, so an occluded Revit can't be driven. The split is Revit " +
        "~2/3 of the screen, Claude ~1/3 (revit_tile's default; pass revitFrac only to deviate). READ THE " +
        "GEOMETRY IN THE REPLY (e.g. revit=0,0,1280,1032 on a 1920px screen). This is not cosmetic: Revit's " +
        "ribbon reflows to window width, and at ~50/50 the tab strip OVERFLOWS and silently hides the " +
        "right-hand tabs — including the Transom tab itself — behind a '>>' chevron. A missing tab or ribbon " +
        "button is far more often a too-narrow window than a broken add-in, so re-check the width BEFORE " +
        "reporting anything as broken. If a tool that should default to 2/3 returns a 50/50 split, the " +
        "helper exe in %LocalAppData%\\Transom\\mcp\\ is STALE (older than the installed add-in) — say so " +
        "rather than trusting its behaviour.\n\n" +
        "STARTUP TRAPS (things that look like failures but are not):\n" +
        "  - After ANY add-in rebuild/upgrade, Revit shows a 'Security - Unsigned Add-In' dialog at startup " +
        "and THE ADD-IN DOES NOT LOAD until it is answered. Clear it first: revit_find('Always Load') then " +
        "revit_click_by_id on the returned AutomationId. revit_list_dialogs can return a button list " +
        "polluted with main-window controls here, so revit_find is the reliable path.\n" +
        "  - 'timed out waiting for Revit (request not applied)' while a large model is still opening is NOT " +
        "a failure — the bridge socket accepts before Revit's API thread is free. Poll status and retry.\n" +
        "  - Revit's Home screen is INVISIBLE to UI Automation and MainWindowTitle is empty until a document " +
        "is open. That is not a crash.\n" +
        "  - A screenshot that returns a tiny image rooted onto a TOOLTIP window under the cursor; re-tile " +
        "or re-foreground Revit and retake it.\n\n" +
        "DON'T DESTROY THE USER'S DATA: never open an older .rvt in a newer Revit — it upgrades the file " +
        "IRREVERSIBLY. Test a newer Revit against a copy or a throwaway Ctrl+N project, and answer 'do not " +
        "save' on close. Never force-kill Revit.\n\n" +
        "TRUST THE ARTIFACT, NOT THE STATUS LINE: a status message is a claim. Verify the file that was " +
        "written (.xlsx/.docx are zips — check for the 'PK' magic bytes, then read xl/worksheets/sheet1.xml " +
        "or word/document.xml and count rows against the model). Status text has been wrong while the file " +
        "was right.\n\n" +
        "LOAD-BEARING RULE: inside Edit Group mode the Revit API is DEAD (it times out). Do ALL selection / " +
        "reads / writes / verification via the API BEFORE entering and AFTER finishing — never during. " +
        "Inside the group, only these revit_* tools work.\n\n" +
        "SCREENSHOT DISCIPLINE: take revit_screenshot after almost every click/type/scroll, READ it, and " +
        "confirm the expected state before the next action — wrong-element picks, mis-focused fields, and " +
        "missed buttons are all SILENT. The default (PrintWindow) shows the UI chrome but a BLACK viewport; " +
        "pass screen=true to see the model viewport (brings Revit forward).\n\n" +
        "COORDINATES: revit_click_xy / revit_keys / revit_type use ABSOLUTE SCREEN pixels. Do not hardcode " +
        "positions — the Hub and dialogs open at non-deterministic spots and stale coords miss SILENTLY. " +
        "Get live coords from revit_find (centerX/centerY); prefer revit_click_by_id (AutomationId) when a " +
        "control is invokable.\n" +
        "  EXCEPTION — THE TRANSOM HUB: its tab CONTENT is NOT exposed to UI Automation (only ~18 " +
        "window-chrome controls are), so revit_find and revit_click_by_id CANNOT reach its tabs, schedule " +
        "list, filter box, text fields or buttons. Inside the Hub, read coordinates off a fresh " +
        "revit_screenshot instead. Re-screenshot after EVERY open: the Hub centres itself on the MONITOR " +
        "work area, not on Revit's window, so with Revit tiled to ~2/3 it sits off-centre and can overhang " +
        "Revit's right edge — coords reused from a previous open miss silently.\n\n" +
        "LOCATING A MEMBER: override its colour via the API, then revit_screenshot(screen=true) and click " +
        "the highlight. A SELECTED group shows the override as the INVERSE colour — deselect to confirm the " +
        "true colour.\n\n" +
        "KEYBOARD + TYPING (atomic focus): revit_keys takes a canvas x,y — it clicks there for focus, then " +
        "sends the shortcut (e.g. 'tl' = Thin Lines) in one step. revit_type takes the value-cell x,y — it " +
        "clicks to focus and types in one step (enter=true commits). A separate click-then-type loses focus " +
        "to a permission prompt. Properties value cells are NOT exposed to UI Automation, so revit_type is " +
        "the only way to set them; revit_scroll the parameter into view first.\n\n" +
        "DIALOGS are SEPARATE top-level windows the main-window tools can't see: revit_list_dialogs to read " +
        "title/text/buttons, revit_click_dialog to click one (omit the button to safe-dismiss).\n\n" +
        "NO RIGHT-CLICK, AND POPUPS ARE UNOBSERVABLE — don't burn turns here:\n" +
        "  - There is NO right-click tool. revit_click_xy is LEFT-click only and SILENTLY IGNORES a 'button' " +
        "argument, so an attempted right-click quietly lands as a LEFT click — which may tick a checkbox or " +
        "select a row and make it look like the menu failed to open. Context menus cannot be opened.\n" +
        "  - Transient popups (context menus, combo-box dropdowns) also cannot be SEEN: screen=true brings " +
        "Revit forward, which DISMISSES the popup before the capture; the default PrintWindow grabs only the " +
        "main frame, which the popup is not part of; and revit_find is rooted on the main window. So a popup " +
        "is invisible to every tool here.\n" +
        "  - Therefore: reach the same outcome another way (a ribbon/dialog button, or the Revit API), and " +
        "confirm the EFFECT through a side channel — an API read-back, the clipboard, or a file the action " +
        "writes. Do not report 'the menu is broken' from these tools alone; you cannot observe it either way.\n\n" +
        "GROUP EDITS CHANGE THE DEFINITION: a built-in edited inside Edit Group is set UNIFORMLY for every " +
        "instance of that group type. Per-instance divergent built-ins aren't possible while grouped (use " +
        "an instance shared parameter / import option 2b instead).\n\n" +
        "TOOLS: revit_status, revit_tile, revit_find, revit_screenshot, revit_click_by_id, revit_click_xy, " +
        "revit_keys, revit_type, revit_scroll, revit_edit_group, revit_finish_group, revit_cancel_group, " +
        "revit_list_dialogs, revit_click_dialog.\n\n" +
        "FILE DIALOGS: never use key combos to clear/replace text. Save As -> accept the default filename " +
        "(revit_click_dialog with 'save'), rename on disk after. Open -> revit_find the file row, click it, " +
        "then click Open. To set any field: get it empty first, then type once.\n\n" +
        "GROUP-EDIT WORKFLOW (full step-by-step ships in the staged 'Transom - Apply staged edits with " +
        "Claude.md'): API selects the group -> revit_edit_group -> pick the member by its red colour " +
        "(revit_click_xy) -> reveal the param (revit_scroll; Properties palette OR Edit Type) -> set it " +
        "(revit_type enter=true, or operate the dropdown/checkbox) -> revit_finish_group -> verify with " +
        "the API.";

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
                "to the visible foreground window, so an occluded Revit can't be driven. Revit takes 2/3 of " +
                "the screen width and Claude 1/3 by default (the user-preferred layout), Revit on the left.",
                ObjSchema(new()
                {
                    ["revitSide"] = Prop("string", "Which side Revit takes: 'left' (default) or 'right'."),
                    ["revitFrac"] = Prop("number", "Fraction of the work-area width Revit gets (default 0.667, clamped 0.5-0.85)."),
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
        // OptString, not GetValue<string>(): a wrong-typed optional arg (revitSide: 0) threw
        // InvalidOperationException past the ToolArgException guard, and RunLoop's catch-all then wrote
        // NO reply at all — hanging a request that carried an id.
        if (OptString(a, "revitSide") is { } side && (side == "left" || side == "right"))
        { args.Add("--revit-side"); args.Add(side); }
        if (a["revitFrac"] is { } fracNode && double.TryParse(fracNode.ToString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var frac))
        { args.Add("--revit-frac"); args.Add(frac.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
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
        if (OptString(a, "button") is { Length: > 0 } b) args.Add(b);
        return args;
    }

    private static bool HasNum(JsonObject a, string key) => a[key]?.GetValueKind() == JsonValueKind.Number;

    /// <summary>An OPTIONAL string argument: null when absent or not a string. Never throws — a wrong-typed
    /// optional arg must not escape as an unhandled exception (that path writes no reply at all).</summary>
    private static string? OptString(JsonObject a, string key)
    {
        var v = a[key];
        return v is not null && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
    }

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
            // ASYNC reads + wait-then-collect. ReadToEnd() returns only at EOF (i.e. when the child exits),
            // so reading synchronously first made the 30s WaitForExit unreachable: a helper hung inside a
            // cross-process UI-Automation COM call blocked here forever and, because RunLoop is
            // single-threaded, stalled EVERY later request for the session. Reading both streams
            // concurrently also avoids the classic stdout/stderr pipe-buffer deadlock.
            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(true); } catch { /* already gone */ }
                // Kill closes the pipes, so the readers complete; bound the wait anyway.
                try { Task.WaitAll(new Task[] { soTask, seTask }, 2000); } catch { /* ignore */ }
                return (-1, soTask.IsCompletedSuccessfully ? soTask.Result : "", "helper timed out");
            }
            try { Task.WaitAll(new Task[] { soTask, seTask }, 5000); } catch { /* ignore */ }
            string so = soTask.IsCompletedSuccessfully ? soTask.Result : "";
            string se = seTask.IsCompletedSuccessfully ? seTask.Result : "";
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

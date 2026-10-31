using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transom.Acad.McpShim;

/// <summary>
/// Minimal MCP server over stdio (newline-delimited JSON-RPC 2.0), ported from Transom's shim. It forwards each
/// <c>tools/call</c> to the Transom.Acad loopback bridge inside AutoCAD:
///   POST http://127.0.0.1:&lt;port&gt;/call  body {"tool":name,"args":args}
/// and returns the bridge's JSON as a text content item. A body carrying an "image" object
/// ({mimeType, data}) is split into a text item plus an MCP image item, so Claude sees the picture.
///
/// stdout is the protocol channel only. All diagnostics go to stderr.
/// </summary>
internal static class Program
{
    private const string FallbackProtocolVersion = "2025-06-18";
    private const string ServerName = "transom-acad";
    private static readonly string ServerVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private const int DefaultPort = 48830;

    private static int _port = DefaultPort;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static int Main(string[] args)
    {
        _port = ResolvePort(args);
        Log($"Transom for AutoCAD MCP shim {ServerVersion} starting. Bridge target: http://127.0.0.1:{_port}/");

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        try
        {
            RunLoop(stdin, stdout);
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex}");
            return 1;
        }

        Log("stdin closed, exiting.");
        return 0;
    }

    private static void RunLoop(TextReader stdin, TextWriter stdout)
    {
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;

            JsonNode? response;
            try
            {
                response = HandleMessage(line);
            }
            catch (Exception ex)
            {
                // A request WITH an id must still get a reply, or the client waits until its own timeout.
                Log($"Error handling message: {ex.Message}");
                response = ErrorReplyFor(line, ex);
            }

            if (response is not null)
            {
                stdout.Write(response.ToJsonString());
                stdout.Write('\n');
                stdout.Flush();
            }
        }
    }

    private static JsonNode? ErrorReplyFor(string body, Exception ex)
    {
        try
        {
            var id = (JsonNode.Parse(body) as JsonObject)?["id"];
            return id is null ? null : Error(id, -32603, $"Internal error: {ex.Message}");
        }
        catch { return null; }
    }

    // -------------------------------------------------------------- dispatch

    private static JsonNode? HandleMessage(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            Log($"Invalid JSON: {ex.Message}");
            return Error(null, -32700, "Parse error");
        }

        if (root is not JsonObject obj) return Error(null, -32600, "Invalid Request");

        string? method = obj["method"] is JsonValue mv && mv.TryGetValue<string>(out var methodStr) ? methodStr : null;
        JsonNode? id = obj["id"];
        bool isNotification = id is null;

        if (method is null) return isNotification ? null : Error(id, -32600, "Invalid Request");

        switch (method)
        {
            case "initialize":
                return Result(id, HandleInitialize(obj["params"] as JsonObject));
            case "notifications/initialized":
            case "initialized":
                return null;
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = ToolList() });
            case "tools/call":
                return Result(id, HandleToolsCall(obj["params"] as JsonObject));
            case "ping":
                return Result(id, new JsonObject());
            default:
                if (isNotification)
                {
                    Log($"Ignoring unknown notification: {method}");
                    return null;
                }
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject HandleInitialize(JsonObject? prms)
    {
        // Echo the client's protocol version: this server is version-agnostic at the JSON-RPC level.
        string version = prms?["protocolVersion"]?.GetValue<string>() is { Length: > 0 } v ? v : FallbackProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
        };
    }

    private static JsonArray ToolList() => new()
    {
        Tool("status",
            "Check that the Transom for AutoCAD bridge is reachable. Reports the AutoCAD product and version, "
            + "the open drawings (which one is active, read-only, saved), the active drawing's units, layout and layer, "
            + "and any command in progress. Call this first.",
            Schema()),

        Tool("list_layers",
            "List the layers in the active drawing: name, on, frozen, locked, color, linetype, plottable, whether it is "
            + "the current layer, and whether it comes from an xref.",
            Schema()),

        Tool("get_entities",
            "Read entities from the active drawing. Filter by space ('model' by default, 'current', or a layout name), "
            + "by types (DXF names such as LINE, LWPOLYLINE, INSERT, TEXT, MTEXT, CIRCLE, ARC, DIMENSION, HATCH, AEC_WALL), "
            + "by layer (* and ? wildcards), or fetch exact entities by handle from any space. Returns a count by type for "
            + "the whole filtered set plus up to 'limit' entities, each with handle, type, layer, color, extents and "
            + "type-specific data: line endpoints, polyline vertices (with bulges), text, block name with attributes and "
            + "dynamic properties, dimension measurement. Page through large sets with offset.",
            Schema(
                ("space", Prop("string", "'model' (default), 'current', or a layout name.")),
                ("types", ArrayProp("DXF type names to include, e.g. [\"LINE\",\"LWPOLYLINE\"].")),
                ("layer", Prop("string", "Layer name filter; supports * and ? wildcards.")),
                ("handles", ArrayProp("Exact entity handles (hex strings). Overrides space.")),
                ("limit", Prop("number", "Maximum entities returned (default 200, max 2000).")),
                ("offset", Prop("number", "Skip this many matching entities first (default 0).")))),

        Tool("run_command",
            "Run an AutoCAD command in the active drawing, the way a user types it. Pass each answer to a prompt as its own "
            + "token, for example [\"_.LINE\",\"0,0\",\"100,0\",\"\"], where \"\" is Enter. Use underscore-prefixed English "
            + "names (_.ZOOM, _E) so it works in any language version. Runs in a command context and undoes with U. "
            + "Returns LASTPROMPT. If the tokens don't finish the command, it stays active and blocks the other tools "
            + "until cancel_command is called.",
            Schema(new[] { "tokens" },
                ("tokens", ArrayProp("The command name followed by one string per prompt answer.")))),

        Tool("cancel_command",
            "Send Esc twice to the active drawing to cancel whatever command is running. Use it when other tools report "
            + "that AutoCAD is busy. It can't close a modal dialog.",
            Schema()),

        Tool("execute_autocad_code",
            "Run an AutoCAD .NET API C# snippet in-process on AutoCAD's main thread, for anything the fixed tools don't "
            + "cover. context 'document' (default) runs like a command in the active drawing, with the document locked "
            + "and a transaction open. Globals: Document doc, Database db, Editor ed, Transaction tr, BlockTableRecord ms "
            + "(model space, opened through tr on first use, for write unless readOnly), and Print(value) to log lines "
            + "back. The transaction COMMITS on success and ABORTS on any exception, and a committed run is one undo "
            + "step (U). readOnly:true runs outside a command with no document lock and always aborts, so it leaves no "
            + "undo step, can't write, and can't use ed.Command. "
            + "context 'application' runs outside any command with NO lock and NO transaction (doc/db/ed are null when "
            + "no drawing is open); use it to open, close or switch drawings. Return a value to get it back; ObjectIds "
            + "and DBObjects come back as handle + type. Pre-imported: System, System.Linq, System.Collections.Generic, "
            + "Autodesk.AutoCAD.ApplicationServices, .DatabaseServices, .EditorInput, .Geometry, .Colors. Capped at 20s, "
            + "but a loop that never yields (while(true){}) freezes AutoCAD.",
            Schema(new[] { "code" },
                ("code", Prop("string", "The C# snippet, e.g. \"var l = new Line(Point3d.Origin, new Point3d(100,0,0)); ms.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true); return l.ObjectId;\"")),
                ("readOnly", Prop("boolean", "If true, the transaction is always aborted (reads only). Default false.")),
                ("context", Prop("string", "'document' (default) or 'application'.")))),

        Tool("get_view_image",
            "Capture the active drawing's current view as a PNG so you can see it. Zoom first with run_command "
            + "([\"_.ZOOM\",\"_E\"]) if needed. AutoCAD keeps the viewport's aspect ratio, so the returned width "
            + "can differ from the one requested; the result reports the actual size.",
            Schema(
                ("width", Prop("number", "Image width in pixels (default 1280, max 2560).")),
                ("height", Prop("number", "Image height in pixels (default 800, max 2560).")))),

        Tool("open_drawing",
            "Open a DWG from disk and make it the active drawing. Switches to it if it is already open.",
            Schema(new[] { "path" },
                ("path", Prop("string", "Full path to the .dwg file.")),
                ("readOnly", Prop("boolean", "Open read-only. Default false.")))),

        Tool("new_drawing",
            "Create a new drawing from a template and make it active.",
            Schema(("template", Prop("string", "Template file name or full path. Default acad.dwt, found on AutoCAD's template path.")))),

        Tool("close_drawing",
            "Close a drawing (the active one by default). Refuses when there are unsaved changes unless save:true "
            + "(saveAs is required for a drawing that was never saved) or discardChanges:true.",
            Schema(
                ("name", Prop("string", "File name or full path of an open drawing. Default: the active drawing.")),
                ("save", Prop("boolean", "Save before closing.")),
                ("saveAs", Prop("string", "Full .dwg path to save to.")),
                ("discardChanges", Prop("boolean", "Close without saving, dropping unsaved changes.")))),
    };

    private static JsonObject HandleToolsCall(JsonObject? prms)
    {
        string? name = prms?["name"] is JsonValue nv && nv.TryGetValue<string>(out var nameStr) ? nameStr : null;
        if (string.IsNullOrEmpty(name)) return ToolError("tools/call is missing the 'name' parameter.");

        var requestBody = new JsonObject
        {
            ["tool"] = name,
            ["args"] = prms?["arguments"]?.DeepClone() ?? new JsonObject(),
        };

        string bridgeJson;
        try
        {
            bridgeJson = CallBridge(requestBody.ToJsonString());
        }
        catch (Exception ex)
        {
            Log($"Bridge call failed: {ex.Message}");
            return ToolError(
                $"Could not reach the Transom for AutoCAD bridge at http://127.0.0.1:{_port}/. Make sure AutoCAD 2027 is "
                + "running with the Transom AutoCAD plug-in loaded (type TRANSOMACAD at the AutoCAD command line to check). "
                + $"Underlying error: {ex.Message}");
        }

        return ToolResult(bridgeJson);
    }

    /// <summary>Wraps the bridge body in MCP content, lifting an "image" member out into an image item.</summary>
    private static JsonObject ToolResult(string body)
    {
        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(body); }
        catch (JsonException) { /* non-JSON body is reported as an error below */ }

        var content = new JsonArray();
        bool isError = parsed is not JsonObject;

        if (parsed is JsonObject obj)
        {
            isError = obj["ok"] is JsonValue ok && ok.GetValueKind() == JsonValueKind.False;
            if (obj["image"] is JsonObject image
                && image["data"] is JsonValue data && data.TryGetValue<string>(out var b64)
                && image["mimeType"] is JsonValue mime && mime.TryGetValue<string>(out var mimeType))
            {
                obj.Remove("image");
                content.Add(new JsonObject { ["type"] = "text", ["text"] = obj.ToJsonString() });
                content.Add(new JsonObject { ["type"] = "image", ["data"] = b64, ["mimeType"] = mimeType });
                return new JsonObject { ["content"] = content, ["isError"] = isError };
            }
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = body });
        return new JsonObject { ["content"] = content, ["isError"] = isError };
    }

    private static string CallBridge(string requestJson)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/call")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        // Loopback is not an authorization boundary; the per-session token the plugin wrote is.
        string? token = ReadToken();
        if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("X-Transom-Acad-Token", token);
        req.Headers.ConnectionClose = true; // the bridge closes every connection

        using HttpResponseMessage resp = Http.Send(req);
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static string? ReadToken()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transom", "acad", "bridge.token");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    // -------------------------------------------------------- schema helpers

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject Schema(params (string Name, JsonObject Prop)[] props) => Schema(Array.Empty<string>(), props);

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Prop)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, prop) in props) properties[name] = prop;
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return schema;
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject ArrayProp(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    // -------------------------------------------------- JSON-RPC envelopes

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonObject ToolError(string text) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = true,
    };

    // ------------------------------------------------------------- plumbing

    private static int ResolvePort(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p)) return p;
            if (args[i].StartsWith("--port=", StringComparison.Ordinal) && int.TryParse(args[i]["--port=".Length..], out int p2)) return p2;
        }
        string? env = Environment.GetEnvironmentVariable("TRANSOMACAD_BRIDGE_PORT");
        return int.TryParse(env, out int ep) ? ep : DefaultPort;
    }

    private static void Log(string message) => Console.Error.WriteLine($"[transom-acad-mcp] {message}");
}

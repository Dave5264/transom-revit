using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transom.McpShim;

/// <summary>
/// Minimal Model Context Protocol (MCP) server over stdio (JSON-RPC 2.0,
/// NEWLINE-DELIMITED). Per the MCP stdio transport spec: each message is a single
/// line of JSON terminated by '\n' (and contains no embedded newlines), and stdout
/// carries ONLY valid MCP messages. It forwards each <c>tools/call</c> to the Transom
/// loopback HTTP bridge running inside Revit:
///   POST http://127.0.0.1:&lt;port&gt;/call  body {"tool":name,"args":args}
/// and returns the bridge's JSON as a single text content item.
///
/// stdout is the protocol channel only. All diagnostics go to stderr.
/// </summary>
internal static class Program
{
    /// <summary>Newest MCP protocol version this server knows — used only when the client's
    /// initialize request omits protocolVersion. Otherwise the server ECHOES the client's
    /// requested version back (it is version-agnostic at the JSON-RPC level).</summary>
    private const string FallbackProtocolVersion = "2025-06-18";
    private const string ServerName = "transom";
    private const string ServerVersion = "1.0.0";
    private const int DefaultPort = 48810;

    private static int _port = DefaultPort;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static int Main(string[] args)
    {
        _port = ResolvePort(args);
        Log($"Transom MCP shim starting. Bridge target: http://127.0.0.1:{_port}/");

        // MCP stdio transport is NEWLINE-DELIMITED JSON. Read one '\n'-terminated line per message;
        // write each response as a single line + '\n'. UTF-8 both ways; stdout is protocol-only.
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

        Log("Transom MCP shim: stdin closed, exiting.");
        return 0;
    }

    /// <summary>Read newline-delimited JSON-RPC messages from stdin until EOF; reply one line each.</summary>
    private static void RunLoop(TextReader stdin, TextWriter stdout)
    {
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue; // tolerate blank lines between messages
            }

            JsonNode? response;
            try
            {
                response = HandleMessage(line);
            }
            catch (Exception ex)
            {
                // Never crash on a single bad message.
                Log($"Error handling message: {ex.Message}");
                response = null;
            }

            if (response is not null)
            {
                WriteMessage(stdout, response);
            }
        }
    }

    // ----------------------------------------------------------------- framing

    /// <summary>Write one MCP message as a single line of JSON + '\n', then flush. JsonNode.ToJsonString
    /// emits single-line JSON (no embedded newlines), satisfying the stdio framing requirement.</summary>
    private static void WriteMessage(TextWriter stdout, JsonNode message)
    {
        stdout.Write(message.ToJsonString());
        stdout.Write('\n');
        stdout.Flush();
    }

    // -------------------------------------------------------------- dispatch

    /// <summary>
    /// Returns the JSON-RPC response node, or null for notifications (no reply).
    /// </summary>
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
            return Error(null, -32700, "Parse error"); // JSON-RPC: parse errors reply with id null
        }

        if (root is not JsonObject obj)
        {
            // A non-object (array batch, bare value) is an invalid single request — reply, don't hang the client.
            return Error(null, -32600, "Invalid Request");
        }

        string? method = obj["method"]?.GetValue<string>();
        JsonNode? id = obj["id"];
        bool isNotification = id is null;

        if (method is null)
        {
            // A request that carries an id MUST get a response (else the client waits forever).
            return isNotification ? null : Error(id, -32600, "Invalid Request");
        }

        switch (method)
        {
            case "initialize":
                return Result(id, HandleInitialize(obj["params"] as JsonObject));

            case "notifications/initialized":
            case "initialized":
                return null; // no-op notification

            case "tools/list":
                return Result(id, HandleToolsList());

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
        // MCP spec: if the server supports the client's requested protocol version it MUST reply with the
        // SAME version. This server is version-agnostic at the JSON-RPC level, so ECHO whatever the client
        // sent; fall back to our newest-known only when the client omits the field.
        string version = prms?["protocolVersion"]?.GetValue<string>() is { Length: > 0 } v
            ? v
            : FallbackProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
        };
    }

    private static JsonObject HandleToolsList()
    {
        var tools = new JsonArray
        {
            Tool(
                "status",
                "Check that the Transom bridge inside Revit is reachable and report the active "
                + "document title and Transom version. Call this first to confirm Revit is running "
                + "with a document open before attempting reads or writes.",
                NoArgsSchema()),

            Tool(
                "list_schedules",
                "List the user-visible schedules in the active Revit document, each with its numeric "
                + "id and name. Use this to discover which schedule to read.",
                NoArgsSchema()),

            Tool(
                "read_schedule",
                "Read a schedule and return a compact view (columns with header/binding/writable flags "
                + "and rows keyed by element UniqueId) so you can 'see' its data before editing. "
                + "Identify the schedule by numeric id or by name.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = Prop("number", "Numeric schedule id (from list_schedules)."),
                        ["name"] = Prop("string", "Schedule name (alternative to id)."),
                    },
                    ["additionalProperties"] = false,
                }),

            Tool(
                "set_parameter",
                "Write a parameter value back to a Revit element by UniqueId; handles group members and "
                + "type params; verifies the write. Resolves the binding live (instance vs type), refuses "
                + "read-only or family/type-driven params with a reason, sets the value inside a transaction, "
                + "re-reads to confirm, and rolls back on failure. Identify the parameter by parameterId or "
                + "by fieldName.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["uniqueId"] = Prop("string", "UniqueId of the target element."),
                        ["parameterId"] = Prop("number", "Numeric id of the parameter to set (alternative to fieldName)."),
                        ["fieldName"] = Prop("string", "Schedule field name identifying the parameter (alternative to parameterId)."),
                        ["value"] = Prop("string", "New value as a string; the bridge coerces it to the parameter's storage type."),
                        ["binding"] = Prop("string", "Optional binding hint: 'instance' or 'type'. Usually omit and let the bridge resolve it."),
                    },
                    ["required"] = new JsonArray { "uniqueId", "value" },
                    ["additionalProperties"] = false,
                }),

            Tool(
                "set_parameters",
                "Apply multiple parameter edits in a single transaction (batch). Each edit is verified "
                + "individually and the whole batch rolls back on a fatal error. Returns a per-edit result. "
                + "Prefer this over many set_parameter calls when editing several cells at once.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["edits"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "List of edits, each shaped like a set_parameter argument object "
                                + "({uniqueId, parameterId?|fieldName?, value, binding?}).",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["uniqueId"] = Prop("string", "UniqueId of the target element."),
                                    ["parameterId"] = Prop("number", "Numeric parameter id (alternative to fieldName)."),
                                    ["fieldName"] = Prop("string", "Schedule field name (alternative to parameterId)."),
                                    ["value"] = Prop("string", "New value as a string."),
                                    ["binding"] = Prop("string", "Optional binding hint: 'instance' or 'type'."),
                                },
                                ["required"] = new JsonArray { "uniqueId", "value" },
                                ["additionalProperties"] = false,
                            },
                        },
                    },
                    ["required"] = new JsonArray { "edits" },
                    ["additionalProperties"] = false,
                }),
        };

        return new JsonObject { ["tools"] = tools };
    }

    private static JsonObject HandleToolsCall(JsonObject? prms)
    {
        string? name = prms?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            return ToolError("tools/call is missing the 'name' parameter.");
        }

        // arguments may be absent (e.g. status / list_schedules).
        JsonNode? arguments = prms?["arguments"];
        JsonNode argsNode = arguments?.DeepClone() ?? new JsonObject();

        var requestBody = new JsonObject
        {
            ["tool"] = name,
            ["args"] = argsNode,
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
                $"Could not reach the Transom bridge at http://127.0.0.1:{_port}/. "
                + "Make sure Revit is running with a document open and the Transom bridge has been "
                + "started from the ribbon (the toggle button). "
                + $"Underlying error: {ex.Message}");
        }

        bool isError = BridgeReportedError(bridgeJson);

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = bridgeJson,
                },
            },
            ["isError"] = isError,
        };
    }

    /// <summary>POST the request to the loopback bridge and return its body.</summary>
    private static string CallBridge(string requestJson)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/call")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        // Authenticate to the bridge with the per-session token the add-in wrote to a per-user file.
        // (Loopback is not an authorization boundary; the token is what blocks other local/web callers.)
        string? token = ReadToken();
        if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("X-Transom-Token", token);
        // The bridge closes every connection; don't let HttpClient reuse a server-closed socket.
        req.Headers.ConnectionClose = true;

        // Synchronous wait is fine: the stdio loop is single-threaded by design.
        using HttpResponseMessage resp = Http.Send(req);
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    /// <summary>Reads the per-session bridge token written by the add-in (see BridgeToggleCommand.TokenFilePath).</summary>
    private static string? ReadToken()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Transom", "bridge.token");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>True when the bridge body parses to an object with "ok": false.</summary>
    private static bool BridgeReportedError(string bridgeJson)
    {
        try
        {
            if (JsonNode.Parse(bridgeJson) is JsonObject obj
                && obj.TryGetPropertyValue("ok", out JsonNode? ok)
                && ok is not null)
            {
                return ok.GetValueKind() == System.Text.Json.JsonValueKind.False;
            }
        }
        catch (JsonException)
        {
            // Non-JSON body from the bridge is itself an error condition.
            return true;
        }
        return false;
    }

    // -------------------------------------------------------- schema helpers

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject NoArgsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
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
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    /// <summary>A tools/call result that flags an error to the model (not a transport error).</summary>
    private static JsonObject ToolError(string text) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            },
        },
        ["isError"] = true,
    };

    // ------------------------------------------------------------- plumbing

    private static int ResolvePort(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
            {
                return p;
            }
            if (args[i].StartsWith("--port=", StringComparison.Ordinal)
                && int.TryParse(args[i]["--port=".Length..], out int p2))
            {
                return p2;
            }
        }

        string? env = Environment.GetEnvironmentVariable("TRANSOM_BRIDGE_PORT");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out int ep))
        {
            return ep;
        }

        return DefaultPort;
    }

    /// <summary>Diagnostics go to stderr only — stdout is the MCP protocol channel.</summary>
    private static void Log(string message) =>
        Console.Error.WriteLine($"[transom-mcp] {message}");
}

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transom.McpShim;

/// <summary>
/// Minimal Model Context Protocol (MCP) server over stdio (JSON-RPC 2.0,
/// Content-Length framed). It forwards each <c>tools/call</c> to the Transom
/// loopback HTTP bridge running inside Revit:
///   POST http://127.0.0.1:&lt;port&gt;/call  body {"tool":name,"args":args}
/// and returns the bridge's JSON as a single text content item.
///
/// stdout is the protocol channel only. All diagnostics go to stderr.
/// </summary>
internal static class Program
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "transom";
    private const string ServerVersion = "1.0.0";
    private const int DefaultPort = 48810;

    private static int _port = DefaultPort;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static int Main(string[] args)
    {
        _port = ResolvePort(args);
        Log($"Transom MCP shim starting. Bridge target: http://127.0.0.1:{_port}/");

        // Raw byte streams so we control Content-Length framing precisely.
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

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

    /// <summary>Read framed JSON-RPC messages from stdin until EOF.</summary>
    private static void RunLoop(Stream stdin, Stream stdout)
    {
        while (true)
        {
            string? body = ReadMessage(stdin);
            if (body is null)
            {
                return; // EOF
            }

            if (body.Length == 0)
            {
                continue;
            }

            JsonNode? response;
            try
            {
                response = HandleMessage(body);
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

    /// <summary>
    /// Read one Content-Length framed message. Returns null at EOF.
    /// MCP stdio framing: ASCII headers, CRLF separated, blank line, then the
    /// UTF-8 JSON body of exactly Content-Length bytes.
    /// </summary>
    private static string? ReadMessage(Stream stdin)
    {
        int contentLength = -1;

        while (true)
        {
            string? line = ReadHeaderLine(stdin);
            if (line is null)
            {
                return null; // EOF mid-stream / before any header
            }

            if (line.Length == 0)
            {
                break; // end of headers
            }

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string name = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out int len))
                {
                    contentLength = len;
                }
            }
        }

        if (contentLength < 0)
        {
            Log("Message missing Content-Length header; skipping.");
            return string.Empty;
        }

        var buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stdin.Read(buffer, read, contentLength - read);
            if (n <= 0)
            {
                return null; // EOF before full body
            }
            read += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, contentLength);
    }

    /// <summary>Read a single CRLF/LF-terminated ASCII header line. Null at EOF.</summary>
    private static string? ReadHeaderLine(Stream stdin)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = stdin.ReadByte()) != -1)
        {
            if (b == '\n')
            {
                int len = sb.Length;
                if (len > 0 && sb[len - 1] == '\r')
                {
                    sb.Length = len - 1;
                }
                return sb.ToString();
            }
            sb.Append((char)b);
        }

        // EOF: return any partial content as a final line, else null.
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void WriteMessage(Stream stdout, JsonNode message)
    {
        string json = message.ToJsonString();
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");

        stdout.Write(header, 0, header.Length);
        stdout.Write(payload, 0, payload.Length);
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
                return Result(id, HandleInitialize());

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

    private static JsonObject HandleInitialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Transom.Core;

/// <summary>
///     Admin-free loopback HTTP bridge. A <see cref="TcpListener"/> bound to
///     <see cref="IPAddress.Loopback"/> on a configurable high port (default 48810) — never
///     all-interfaces, never <c>HttpListener</c> (whose URL prefixes need an admin netsh
///     reservation). Loopback + port &gt; 1024 ⇒ no URL ACL, no firewall dialog, no admin.
///     <para>
///     A background accept loop runs on its own thread. Each connection is parsed as a minimal
///     HTTP/1.1 request (request line + headers + Content-Length body). <c>GET /status</c> and
///     <c>POST /call</c> are routed through the <see cref="Func{T,TResult}"/> dispatch delegate
///     supplied to <see cref="Start"/>; everything else returns 404. The dispatch delegate is the
///     single seam to the Revit side — this file holds no Revit API references.
///     </para>
/// </summary>
public sealed class BridgeServer
{
    private readonly object _gate = new();
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private Func<string, string>? _dispatch;
    private string? _token;
    private volatile bool _running;

    /// <summary>True while the accept loop is listening for connections.</summary>
    public bool IsRunning => _running;

    /// <summary>
    ///     Binds a loopback listener on <paramref name="port"/> and starts the background accept loop.
    ///     <paramref name="dispatch"/> receives the request body JSON (<c>{"tool":...,"args":...}</c>)
    ///     and returns the response JSON. For <c>GET /status</c> the body sent to dispatch is
    ///     <c>{"tool":"status","args":{}}</c> so the Revit side can answer with live document info.
    /// </summary>
    public void Start(int port, Func<string, string> dispatch, string? token = null)
    {
        if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
        lock (_gate)
        {
            if (_running) return;

            // Loopback ONLY — never IPAddress.Any. This is what keeps the bridge admin-free.
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            _listener = listener;
            _dispatch = dispatch;
            _token = token;
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "Transom.BridgeServer",
            };
            _acceptThread.Start();
        }
    }

    /// <summary>Stops the accept loop and releases the listener. Safe to call when already stopped.</summary>
    public void Stop()
    {
        Thread? toJoin;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;

            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            _dispatch = null;
            _token = null;
            toJoin = _acceptThread;
            _acceptThread = null;
        }

        // Join outside the lock so the accept thread can exit cleanly.
        if (toJoin != null && toJoin != Thread.CurrentThread)
            try { toJoin.Join(2000); } catch { /* ignore */ }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                var listener = _listener;
                if (listener == null) break;
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                // listener.Stop() unblocks AcceptTcpClient with an exception — expected on shutdown.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (!_running) break;
                continue;
            }

            // Swallow every per-connection error so one bad request never kills the loop.
            try { HandleConnection(client); }
            catch { /* ignore */ }
        }
    }

    private void HandleConnection(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            // H3: never let a silent/slow client pin the (single-threaded) accept loop forever.
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            using var stream = client.GetStream();
            stream.ReadTimeout = 10000;
            stream.WriteTimeout = 10000;

            if (!TryReadRequestHead(stream, out var method, out var path, out var headers))
            {
                WriteResponse(stream, 400, "Bad Request", "{\"ok\":false,\"error\":\"malformed request\"}");
                return;
            }

            // --- Security gate (the bridge is loopback-only, but loopback is NOT an authorization boundary:
            // any local process, and any web page via a cross-origin POST, can reach 127.0.0.1). ---
            // 1) Reject anything carrying an Origin header — a browser always sends Origin on cross-origin
            //    fetch/XHR, so this blocks CSRF-to-localhost / DNS-rebinding driven writes. Our shim never does.
            if (headers.ContainsKey("origin"))
            {
                WriteResponse(stream, 403, "Forbidden", "{\"ok\":false,\"error\":\"cross-origin requests are not allowed\"}");
                return;
            }
            // 2) Host must be loopback (blunts DNS-rebinding, where Host would be the attacker's domain).
            if (headers.TryGetValue("host", out var host) && !IsLoopbackHost(host))
            {
                WriteResponse(stream, 403, "Forbidden", "{\"ok\":false,\"error\":\"invalid host\"}");
                return;
            }

            int contentLength = 0;
            if (headers.TryGetValue("content-length", out var clRaw)) int.TryParse(clRaw, out contentLength);
            bool hasContentLength = headers.ContainsKey("content-length");
            string body = contentLength > 0 ? ReadBody(stream, contentLength) : "";
            var dispatch = _dispatch;

            if (dispatch == null)
            {
                WriteResponse(stream, 503, "Service Unavailable", "{\"ok\":false,\"error\":\"bridge not running\"}");
                return;
            }

            try
            {
                if (method == "GET" && path == "/status")
                {
                    // /status is a harmless health check — allowed without the token (still Origin/Host-gated above).
                    WriteResponse(stream, 200, "OK", dispatch("{\"tool\":\"status\",\"args\":{}}"));
                }
                else if (method == "POST" && path == "/call")
                {
                    // 3) Writes require the shared session token (read from a per-user file by the shim).
                    if (!string.IsNullOrEmpty(_token) &&
                        (!headers.TryGetValue("x-transom-token", out var tok) || !FixedEquals(tok, _token!)))
                    {
                        WriteResponse(stream, 401, "Unauthorized", "{\"ok\":false,\"error\":\"missing or invalid token\"}");
                        return;
                    }
                    // C5: a POST with no Content-Length would otherwise dispatch an empty body as a silent no-op.
                    if (!hasContentLength || contentLength <= 0 || string.IsNullOrEmpty(body))
                    {
                        WriteResponse(stream, 400, "Bad Request", "{\"ok\":false,\"error\":\"missing request body\"}");
                        return;
                    }
                    WriteResponse(stream, 200, "OK", dispatch(body));
                }
                else
                {
                    WriteResponse(stream, 404, "Not Found", "{\"ok\":false,\"error\":\"unknown route\"}");
                }
            }
            catch (Exception ex)
            {
                WriteResponse(stream, 500, "Internal Server Error",
                    "{\"ok\":false,\"error\":" + JsonString(ex.Message) + "}");
            }
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        var h = host.Trim();
        int colon = h.LastIndexOf(':');
        if (colon > 0 && h.IndexOf(':') == colon) h = h.Substring(0, colon); // strip :port (not an IPv6 literal)
        h = h.Trim('[', ']');
        return h.Equals("127.0.0.1") || h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.Equals("::1");
    }

    /// <summary>Length-constant string compare so the token check doesn't leak via timing.</summary>
    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>
    ///     Reads the request line + headers byte-by-byte up to the blank line, returning the method, path,
    ///     and a case-insensitive header map (keys lower-cased).
    /// </summary>
    private static bool TryReadRequestHead(NetworkStream stream, out string method, out string path,
        out Dictionary<string, string> headers)
    {
        method = "";
        path = "";
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var head = ReadHeaderBlock(stream);
        if (string.IsNullOrEmpty(head)) return false;

        var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return false;

        method = requestLine[0].Trim().ToUpperInvariant();
        path = requestLine[1].Trim();

        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            var val = line.Substring(colon + 1).Trim();
            if (key.Length > 0) headers[key] = val;
        }

        return method.Length > 0 && path.Length > 0;
    }

    private static string ReadHeaderBlock(NetworkStream stream)
    {
        var sb = new StringBuilder();
        int b;
        int matched = 0; // tracks the trailing "\r\n\r\n" sequence
        // Cap header size to avoid an unbounded read from a misbehaving client.
        const int maxHeader = 64 * 1024;
        while (sb.Length < maxHeader && (b = stream.ReadByte()) != -1)
        {
            char c = (char)b;
            sb.Append(c);
            if ((matched == 0 || matched == 2) && c == '\r') matched++;
            else if ((matched == 1 || matched == 3) && c == '\n') matched++;
            else matched = 0;
            if (matched == 4) break; // reached blank line
        }
        return sb.ToString();
    }

    private static string ReadBody(NetworkStream stream, int contentLength)
    {
        var buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stream.Read(buffer, read, contentLength - read);
            if (n <= 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static void WriteResponse(NetworkStream stream, int status, string reason, string jsonBody)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody ?? "");
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");

        var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (bodyBytes.Length > 0) stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    private static string JsonString(string? s)
    {
        s ??= "";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

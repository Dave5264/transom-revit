using System;
using System.Threading;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Marshals a single bridge request onto Revit's API thread. The <see cref="BridgeServer"/>
///     accept loop runs on a background thread, but Revit API calls must happen in an
///     <see cref="IExternalEventHandler"/>. <see cref="RunOnRevitThread"/> stashes the request,
///     raises the supplied <see cref="ExternalEvent"/>, then blocks (with a timeout) on a
///     <see cref="ManualResetEventSlim"/> until <see cref="Execute"/> has produced the result.
///     <para>
///     A lock serializes requests so only one is ever in flight — the bridge is request/response,
///     one tool call at a time. <see cref="Execute"/> delegates the actual work to
///     <c>BridgeTools.Handle(uiapp, requestJson)</c> (built by F2), which never throws.
///     </para>
/// </summary>
public sealed class BridgeEventHandler : IExternalEventHandler
{
    private readonly object _flightGate = new();
    private readonly ManualResetEventSlim _done = new(false);

    private string _pendingRequest = "";
    private string _result = "";
    private int _ticket;                  // incremented per request (under _flightGate)
    private volatile int _inFlight = -1;  // current ticket Execute may publish; -1 = none (abandoned/idle)

    /// <summary>
    ///     Sets the pending request, raises <paramref name="evt"/>, and waits up to
    ///     <paramref name="timeoutMs"/> for the Revit thread to answer. Returns a JSON timeout error
    ///     if the deadline passes. Only one request runs at a time (guarded by a lock).
    /// </summary>
    public string RunOnRevitThread(string requestJson, ExternalEvent evt, int timeoutMs = 30000)
    {
        if (evt == null) throw new ArgumentNullException(nameof(evt));

        lock (_flightGate)
        {
            _pendingRequest = requestJson ?? "{}";
            _result = "";
            _done.Reset();
            _inFlight = ++_ticket;

            evt.Raise();

            if (!_done.Wait(timeoutMs))
            {
                // Abandon this ticket so a late Execute (the Raise that finally fires) doesn't publish a stale
                // result into the next request, nor double-apply it. Execute is gated on _inFlight.
                _inFlight = -1;
                return "{\"ok\":false,\"error\":\"timed out waiting for Revit (request not applied)\"}";
            }

            return _result;
        }
    }

    /// <summary>
    ///     Runs on Revit's API thread (serialized by Revit). Hands the pending request to
    ///     <c>BridgeTools.Handle</c> and signals completion — but only if the request is still the active
    ///     ticket (so a stray Raise from a timed-out request is ignored, never re-applied). Any failure is
    ///     turned into a JSON error so the waiter always unblocks.
    /// </summary>
    public void Execute(UIApplication app)
    {
        int my = _inFlight;
        if (my < 0) return; // no active waiter — ignore a leftover Raise

        string result;
        // Let long write tools detect that this request's waiter has already timed out, so they roll back
        // instead of committing behind the client's back (a commit after "not applied" → double-apply on retry).
        BridgeTools.RequestAbandoned = () => _inFlight != my;
        try
        {
            result = BridgeTools.Handle(app, _pendingRequest);
        }
        catch (Exception ex)
        {
            result = "{\"ok\":false,\"error\":" + JsonString(ex.Message) + "}";
        }
        finally
        {
            BridgeTools.RequestAbandoned = null;
        }

        // Publish only if this is still the in-flight ticket; clearing it collapses a duplicate Raise.
        if (_inFlight == my)
        {
            _result = result;
            _inFlight = -1;
            _done.Set();
        }
    }

    public string GetName() => "Transom Bridge";

    private static string JsonString(string? s)
    {
        s ??= "";
        var sb = new System.Text.StringBuilder(s.Length + 2);
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

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Transom.Acad;

/// <summary>
///     AutoCAD's counterpart of Transom's ExternalEvent hand-off. The AutoCAD API is only valid on the main
///     thread, so the bridge thread posts each request through a hidden WinForms control created on that thread
///     (the main message loop dispatches it even while AutoCAD sits at the command prompt) and blocks until the
///     work reports back or the timeout fires.
///
///     A posted callback runs in APPLICATION context: fine for opening, closing and switching drawings. Work on
///     the drawing itself hops into COMMAND context with <see cref="InCommandContext"/>, where the document is
///     locked like a command, Editor.CommandAsync is legal, and the change lands as one undo step.
/// </summary>
internal static class MainThread
{
    private static Control? _invoker;

    /// <summary>Must be called on AutoCAD's main thread (IExtensionApplication.Initialize).</summary>
    public static void Init()
    {
        var control = new Control();
        _ = control.Handle; // an invisible control only gets its window handle when asked for it
        _invoker = control;
    }

    /// <summary>
    ///     Posts <paramref name="start"/> to the main thread and waits for it to call its completion callback
    ///     with the response JSON. The callback may fire later than <paramref name="start"/> returns (after a
    ///     command-context hop); extra calls are ignored.
    /// </summary>
    public static string Invoke(Action<Action<string>> start, TimeSpan timeout)
    {
        var invoker = _invoker ?? throw new InvalidOperationException("main-thread invoker not initialised");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int state = 0; // 0 = queued, 1 = started on the main thread, 2 = abandoned by the caller

        invoker.BeginInvoke(new Action(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return; // caller already gave up: never run late
            try { start(json => tcs.TrySetResult(json)); }
            catch (System.Exception ex) { tcs.TrySetResult(Json.Error(ex.Message)); }
        }));

        if (tcs.Task.Wait(timeout)) return tcs.Task.Result;

        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            return Json.Error($"timed out after {timeout.TotalSeconds:0}s waiting for AutoCAD's main thread (request NOT applied). "
                              + "AutoCAD is probably showing a modal dialog or busy with a long operation.");
        return Json.Error($"timed out after {timeout.TotalSeconds:0}s; the request STARTED and may still be running, so its "
                          + "effect is unknown. Call status before retrying.");
    }

    /// <summary>
    ///     Runs <paramref name="body"/> in command context on the active drawing and reports its JSON. async void
    ///     is deliberate: the caller is a posted main-thread callback with nothing to await, and every exception
    ///     is caught and reported through <paramref name="done"/>.
    /// </summary>
    public static async void InCommandContext(Func<Task<string>> body, Action<string> done)
    {
        try
        {
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(async _ =>
            {
                string result;
                try { result = await body(); }
                catch (System.Exception ex) { result = Json.Error(ex.Message); }
                done(result);
            }, null);
        }
        catch (System.Exception ex)
        {
            done(Json.Error("could not enter a command context: " + ex.Message));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;

namespace Transom.Acad;

/// <summary>
///     Routes a bridge request ({"tool":…,"args":…}) to its tool, choosing the execution context each tool
///     needs. Runs on the bridge thread; nothing here touches the AutoCAD API directly.
/// </summary>
internal static class Dispatcher
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);

    internal const string NoDrawing =
        "no drawing is active in AutoCAD (the Start tab may be showing). Call open_drawing or new_drawing first.";

    public static string Dispatch(string body)
    {
        string tool;
        JsonElement args;
        try
        {
            using var parsed = JsonDocument.Parse(body);
            var root = parsed.RootElement;
            tool = root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
        }
        catch (JsonException ex)
        {
            return Json.Error("invalid request JSON: " + ex.Message);
        }

        if (tool != "status") Log.Write($"call {tool}");

        switch (tool)
        {
            case "status":
                return MainThread.Invoke(done => done(Json.Serialize(Tools.Status())), StatusTimeout);
            case "cancel_command":
                return OnApplication(Tools.CancelCommand, busyCheck: false);
            case "open_drawing":
                return OnApplication(() => Tools.OpenDrawing(args));
            case "new_drawing":
                return OnApplication(() => Tools.NewDrawing(args));
            case "close_drawing":
                return OnApplication(() => Tools.CloseDrawing(args));
            case "list_layers":
                return OnDocumentRead(Tools.ListLayers);
            case "get_entities":
                return OnDocumentRead(doc => Tools.GetEntities(doc, args));
            case "get_view_image":
                return OnDocumentRead(doc => Tools.GetViewImage(doc, args));
            case "run_command":
                return OnDocument(doc => Tools.RunCommand(doc, args));
            case "execute_autocad_code":
                return AcadCodeExecutor.Execute(args);
            default:
                return Json.Error($"unknown tool '{tool}'");
        }
    }

    /// <summary>Application context: outside any command, for opening, closing and switching drawings.</summary>
    internal static string OnApplication(Func<Dictionary<string, object?>> body, bool busyCheck = true) =>
        MainThread.Invoke(done =>
        {
            if (busyCheck && BusyError(Application.DocumentManager.MdiActiveDocument) is { } busy)
            {
                done(busy);
                return;
            }
            done(Json.Serialize(body()));
        }, CallTimeout);

    /// <summary>
    ///     Reads on the active drawing, in APPLICATION context. Command context would work too, but every hop
    ///     into it lands on the undo stack as an empty EXECUTEFUNCTION step, so after a few reads the user's U
    ///     undoes nothing visible. Reads stay out of command context to keep U pointed at real changes.
    /// </summary>
    internal static string OnDocumentRead(Func<Document, Dictionary<string, object?>> body) =>
        MainThread.Invoke(done =>
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                done(Json.Error(NoDrawing));
                return;
            }
            if (BusyError(doc) is { } busy)
            {
                done(busy);
                return;
            }
            done(Json.Serialize(body(doc)));
        }, CallTimeout);

    /// <summary>Command context on the active drawing: locked like a command, undoable as one step.</summary>
    internal static string OnDocument(Func<Document, Task<Dictionary<string, object?>>> body) =>
        MainThread.Invoke(done =>
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                done(Json.Error(NoDrawing));
                return;
            }
            if (BusyError(doc) is { } busy)
            {
                done(busy);
                return;
            }
            MainThread.InCommandContext(async () => Json.Serialize(await body(doc)), done);
        }, CallTimeout);

    /// <summary>
    ///     A user command (or a dialog a command opened) owns the drawing; running alongside it could corrupt
    ///     its state, so refuse with a reason instead of queueing behind it.
    /// </summary>
    private static string? BusyError(Document? doc)
    {
        var cmd = doc?.CommandInProgress;
        return string.IsNullOrEmpty(cmd)
            ? null
            : Json.Error($"AutoCAD is busy: command '{cmd}' is in progress in {Path.GetFileName(doc!.Name)} "
                         + "(a user command or a dialog). Wait for it to finish, or call cancel_command.");
    }
}

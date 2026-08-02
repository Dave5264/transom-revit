using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>Resolves which open project Transom operates on (the user can switch projects in the dialog).</summary>
public static class DocUtil
{
    /// <summary>
    ///     A requested title that is not found returns NULL — never the active document. Falling back
    ///     would silently retarget the caller's operation (export/import/apply) at whatever model happens
    ///     to be active, which is exactly the wrong-model write the callers' "project not found" guards
    ///     exist to prevent. Only an empty title (caller wants "whatever is active") uses the fallback.
    /// </summary>
    public static Document? Resolve(UIApplication app, string title)
    {
        if (!string.IsNullOrEmpty(title))
        {
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked && d.Title == title)
                    return d;
            return null;
        }
        return app.ActiveUIDocument?.Document;
    }

    /// <summary>
    ///     A stable path identity for the document: the central model path for workshared docs (the local
    ///     copy's PathName differs per user/session), else PathName. Used as the cross-model tiebreaker —
    ///     two of a user's models can SHARE a CreationGUID (Save-As), so title+GUID alone can pick the
    ///     wrong doc; the path disambiguates. Empty for never-saved documents.
    /// </summary>
    public static string StablePath(Document doc)
    {
        try
        {
            if (doc.IsWorkshared && doc.GetWorksharingCentralModelPath() is { } central)
            {
                var p = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                if (!string.IsNullOrEmpty(p)) return p;
            }
        }
        catch { /* fall through to PathName */ }
        try { return doc.PathName ?? ""; } catch { return ""; }
    }

    /// <summary>
    ///     The user-visible schedules of a document, sorted by name. Excludes view templates, titleblock
    ///     revision schedules, and the hidden "&lt;name&gt; Internal" copies Revit spawns when a schedule is
    ///     placed on a sheet — those report <see cref="ViewPlacementOnSheetStatus.NotApplicable"/> (a placeable
    ///     schedule reports NotPlaced/Partially/CompletelyPlaced), so one schedule on five sheets shows once, not six.
    /// </summary>
    public static List<(long id, string name)> UserSchedules(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule && !IsInternalSheetCopy(v))
            .OrderBy(v => v.Name)
            .Select(v => (v.Id.Value, v.Name))
            .ToList();

    /// <summary>True for the internal schedule-graphics copy Revit creates per sheet placement (hidden from the user).</summary>
    private static bool IsInternalSheetCopy(ViewSchedule v)
    {
        try { return v.GetPlacementOnSheetStatus() == ViewPlacementOnSheetStatus.NotApplicable; }
        catch { return false; } // unknown -> show it rather than risk hiding a real schedule
    }
}

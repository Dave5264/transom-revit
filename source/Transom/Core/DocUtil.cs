using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>Resolves which open project Transom operates on (the user can switch projects in the dialog).</summary>
public static class DocUtil
{
    public static Document? Resolve(UIApplication app, string title)
    {
        if (!string.IsNullOrEmpty(title))
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked && d.Title == title)
                    return d;
        return app.ActiveUIDocument?.Document;
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

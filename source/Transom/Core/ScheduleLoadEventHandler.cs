using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>Loads the schedule list for a chosen project (when the user switches projects), in API context.</summary>
public sealed class ScheduleLoadEventHandler : IExternalEventHandler
{
    public string DocTitle = "";
    public Action<long, List<(long id, string name)>> OnLoaded = (_, _) => { };

    public void Execute(UIApplication app)
    {
        // Never let an exception escape Execute — an unhandled throw here surfaces as a raw Revit external-event
        // error dialog. On failure, hand back an empty list so the UI resolves cleanly instead of hanging.
        try
        {
            var doc = DocUtil.Resolve(app, DocTitle);
            if (doc == null) { OnLoaded(0, new List<(long, string)>()); return; }

            long activeId = 0;
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc != null && uiDoc.Document.Title == doc.Title && uiDoc.ActiveView is ViewSchedule avs)
                activeId = avs.Id.Value;

            OnLoaded(activeId, DocUtil.UserSchedules(doc));
        }
        catch
        {
            try { OnLoaded(0, new List<(long, string)>()); } catch { /* callback itself failed — nothing more to do */ }
        }
    }

    public string GetName() => "Transom Load Schedules";
}

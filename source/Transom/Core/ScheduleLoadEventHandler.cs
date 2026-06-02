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
        var doc = DocUtil.Resolve(app, DocTitle);
        if (doc == null) return;

        long activeId = 0;
        var uiDoc = app.ActiveUIDocument;
        if (uiDoc != null && uiDoc.Document.Title == doc.Title && uiDoc.ActiveView is ViewSchedule avs)
            activeId = avs.Id.Value;

        OnLoaded(activeId, DocUtil.UserSchedules(doc));
    }

    public string GetName() => "Transom Load Schedules";
}

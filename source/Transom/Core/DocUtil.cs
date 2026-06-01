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
}

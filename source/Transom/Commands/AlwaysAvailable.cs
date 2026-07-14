using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Commands;

/// <summary>
///     Availability controller for ribbon buttons that must work with NO document open — Revit's default
///     greys external commands at zero documents. Settings uses it: Claude Assist setup, its status panel,
///     and the guide exports don't need a model (the Hub's model tabs stay disabled until one opens).
/// </summary>
public class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

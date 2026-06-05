using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;
using Transom.Views;

namespace Transom.Commands;

/// <summary>
///     Opens Schedule Hub focused on its Settings tab — the "Settings" button in the Claude Assist ribbon group.
///     Reuses the exact window/UI as Schedule Hub's Settings tab (no separate settings window to keep in sync).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SettingsCommand : ExternalCommand
{
    public override void Execute() => StartupCommand.OpenOrActivate(Application).SelectSettingsTab();
}

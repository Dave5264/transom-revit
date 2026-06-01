using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;
using Transom.Views;

namespace Transom.Commands;

/// <summary>Opens the Transom help &amp; support window.</summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class HelpCommand : ExternalCommand
{
    public override void Execute()
    {
        var dlg = new HelpDialog();
        new WindowInteropHelper(dlg) { Owner = Application.MainWindowHandle };
        dlg.ShowDialog();
    }
}

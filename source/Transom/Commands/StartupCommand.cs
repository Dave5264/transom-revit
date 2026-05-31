using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;
using Transom.ViewModels;
using Transom.Views;

namespace Transom.Commands;

/// <summary>
///     Opens the Transom Export/Import dialog, modal and owned to the Revit main window.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class StartupCommand : ExternalCommand
{
    public override void Execute()
    {
        var viewModel = new TransomViewModel();
        var view = new TransomView(viewModel);
        new WindowInteropHelper(view) { Owner = Application.MainWindowHandle };
        view.ShowDialog();
    }
}

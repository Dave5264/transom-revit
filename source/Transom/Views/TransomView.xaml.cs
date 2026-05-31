using Transom.ViewModels;

namespace Transom.Views;

public sealed partial class TransomView
{
    /// <summary>The single open instance (modeless singleton), or null when closed.</summary>
    public static TransomView? Instance { get; private set; }

    public TransomView(TransomViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Instance = this;
        Closed += (_, _) => Instance = null;

        // Show a modal resolver for each type-param conflict during import preview.
        viewModel.ConflictResolver = conflict =>
        {
            var dlg = new ConflictDialog(conflict) { Owner = this };
            dlg.ShowDialog();
            return dlg.Result;
        };
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}

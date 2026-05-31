using Transom.ViewModels;

namespace Transom.Views;

public sealed partial class TransomView
{
    public TransomView(TransomViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
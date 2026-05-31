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
    }
}

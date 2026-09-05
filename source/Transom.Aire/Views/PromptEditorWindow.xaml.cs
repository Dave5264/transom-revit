using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     The AIRE prompt box, popped out into a resizable window. The inline box on the main window is small by
///     necessity — it shares the left column with the settings card — which makes editing a long, multi-clause
///     enhancement prompt awkward. This is the same text in a window the user can size (or maximise onto a
///     second monitor).
///     <para>
///     Not a copy-and-return dialog: the editor's Text is TWO-WAY bound straight to the main window's PromptBox,
///     so the cost estimate, a Save to the prompt library, and a batch started while this is open all see the
///     current text with no OK/Cancel step to forget. Modeless on purpose — the user can still work the queue.
///     </para>
/// </summary>
public sealed partial class PromptEditorWindow
{
    public PromptEditorWindow(Window owner, TextBox source)
    {
        InitializeComponent();

        // Share the owner's live palette rather than copying it. ApplyTheme MUTATES the SolidColorBrush
        // instances in that dictionary, so a merged reference repaints this window too when the theme is
        // switched on the main window — which a cloned dictionary would not.
        Resources.MergedDictionaries.Add(owner.Resources);
        Owner = owner; // also closes this window automatically when AIRE closes

        BindingOperations.SetBinding(EditorBox, TextBox.TextProperty, new Binding(nameof(TextBox.Text))
        {
            Source = source,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });

        EditorBox.TextChanged += (_, _) => UpdateCount();
        CloseButton.Click += (_, _) => Close();
        Loaded += (_, _) =>
        {
            EditorBox.Focus();
            EditorBox.CaretIndex = EditorBox.Text.Length;
            UpdateCount();
        };
    }

    /// <summary>Character count plus the same prompt-token estimate that feeds the cost figure, so a user
    /// writing a long prompt can see what it is adding.</summary>
    private void UpdateCount()
    {
        var text = EditorBox.Text ?? "";
        CountLabel.Text = $"{text.Length:N0} characters  ·  ~{AireEngine.EstimateTextTokens(text):N0} prompt tokens";
    }
}

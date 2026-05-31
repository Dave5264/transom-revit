using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Transom.Core;

namespace Transom.Views;

public partial class ConflictDialog : Window
{
    private readonly List<(RadioButton rb, ConflictOption opt)> _map = new();

    public ConflictOption? Result { get; private set; }

    public ConflictDialog(TypeConflict c)
    {
        InitializeComponent();

        HeadingText.Text = $"“{c.Field}” conflict on type ‘{c.TypeName}’";
        SubText.Text = $"Different values were entered for this type parameter, which applies to all " +
                       $"{c.InstancesAffected} instance(s) of the type. Current value: “{c.CurrentDisplay}”. " +
                       "Choose the value to apply:";

        var first = true;
        foreach (var opt in c.Options)
        {
            var rb = new RadioButton
            {
                Content = opt.Parseable ? opt.Display : $"{opt.Display}   (can't parse — unavailable)",
                IsEnabled = opt.Parseable,
                Margin = new Thickness(0, 4, 0, 4),
                IsChecked = first && opt.Parseable,
            };
            if (first && opt.Parseable) first = false;
            _map.Add((rb, opt));
            OptionsPanel.Children.Add(rb);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (rb, opt) in _map)
            if (rb.IsChecked == true) { Result = opt; break; }
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
        Close();
    }
}

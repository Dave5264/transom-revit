using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     A batch log, in English. AIRE already produces good, specific failure sentences — it just wrote them
///     into a spreadsheet column and then told the user where the spreadsheet was. This is where those
///     sentences land.
///     <para>
///     A second Window rather than an in-window overlay, because a log wants to be resized, scrolled, and left
///     open beside the queue while the next batch is set up. Modeless singleton like
///     <see cref="PromptEditorWindow"/>: a second click re-fronts the one that is open.
///     </para>
///     <para>
///     It always reads from the FILE, never from a job object. That is what makes View Log work for a batch
///     Claude started through the bridge, for a run from before this window was reopened, and for last Tuesday.
///     </para>
/// </summary>
public sealed partial class LogViewerWindow
{
    private AireLogReport? _report;
    private bool _suppressComboEvents;

    public LogViewerWindow(Window owner, string csvPath)
    {
        InitializeComponent();

        // Share the owner's live palette rather than copying it — ApplyTheme mutates those brush instances, so
        // a merged reference repaints this window when the theme is switched on the main window.
        Resources.MergedDictionaries.Add(owner.Resources);
        Owner = owner;

        OpenCsvButton.Click += (_, _) => Launch(_report?.CsvPath ?? "");
        OpenFolderButton.Click += (_, _) => Launch(OutputFolderOf(_report?.CsvPath ?? ""));
        CopyButton.Click += (_, _) => CopyReport();
        CloseButton.Click += (_, _) => Close();
        LogCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressComboEvents) return;
            if (LogCombo.SelectedItem is LogChoice choice) LoadLog(choice.Path);
        };

        LoadLog(csvPath);
    }

    /// <summary>Re-points an already-open viewer at another log — what the second View Log click does.</summary>
    public void LoadLog(string csvPath)
    {
        try
        {
            _report = AireLogReport.Load(csvPath);
        }
        catch (Exception ex)
        {
            // A log that cannot be read must say so in the window, not throw out of a button handler.
            _report = null;
            HeadlineLabel.Text = "This log could not be read.";
            SubLabel.Text = ex.Message;
            RowList.ItemsSource = null;
            StoppedPanel.Visibility = System.Windows.Visibility.Collapsed;
            PathLabel.Text = csvPath;
            return;
        }

        Title = $"{_report.FileName} — AIRE";
        HeadlineLabel.Text = _report.HeadlineText();

        var when = _report.RunAt is { } t ? t.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) : "";
        var cost = _report.CostText();
        SubLabel.Text = string.Join("  ·  ", new[] { when, cost }.Where(s => s.Length > 0));
        if (_report.Warning.Length > 0)
            SubLabel.Text = SubLabel.Text.Length > 0 ? SubLabel.Text + "  ·  " + _report.Warning : _report.Warning;

        RowList.ItemsSource = _report.Rows;

        StoppedPanel.Visibility = _report.StoppedReason.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        StoppedLabel.Text = _report.StoppedReason.Length > 0
            ? "Batch stopped here. " + _report.StoppedReason
              + " The remaining renders were not attempted, so nothing was charged for them."
            : "";

        PathLabel.Text = _report.CsvPath;
        RefreshLogList(_report.CsvPath);
    }

    /// <summary>Every log in this output folder, newest first. Cheap, and it turns "what happened on the run
    /// before this one?" from an Explorer trip into a click.</summary>
    private void RefreshLogList(string csvPath)
    {
        _suppressComboEvents = true;
        try
        {
            var folder = OutputFolderOf(csvPath);
            var choices = AireLogReport.LogsIn(folder).Select(p => new LogChoice(p)).ToList();
            if (!choices.Any(c => SamePath(c.Path, csvPath)))
                choices.Insert(0, new LogChoice(csvPath)); // a log opened from outside the current output folder
            LogCombo.ItemsSource = choices;
            LogCombo.SelectedItem = choices.FirstOrDefault(c => SamePath(c.Path, csvPath));
            LogCombo.IsEnabled = choices.Count > 1;
        }
        finally { _suppressComboEvents = false; }
    }

    private void CopyReport()
    {
        if (_report == null) return;
        try
        {
            Clipboard.SetText(_report.ToPlainText());
            PathLabel.Text = "Copied — paste it into an email or a message.";
        }
        catch
        {
            // Another process can hold the clipboard open; that is not worth a dialog.
            PathLabel.Text = "The clipboard was busy — try Copy again.";
        }
    }

    /// <summary>
    ///     Two paths naming the same file. Compared through <see cref="Path.GetFullPath"/>, which normalises
    ///     separators and any "." / ".." — a caller's "C:/out/logs/x.csv" and the enumerator's
    ///     "C:\out\logs\x.csv" are the same log, and a raw string compare would put it in the dropdown twice.
    /// </summary>
    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>The output folder a log belongs to: logs live in &lt;output&gt;\logs\.</summary>
    private static string OutputFolderOf(string csvPath)
    {
        try
        {
            var logs = Path.GetDirectoryName(csvPath);
            return logs == null ? "" : Path.GetDirectoryName(logs) ?? logs;
        }
        catch { return ""; }
    }

    private static void Launch(string path)
    {
        if (path.Length == 0) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* shell launch is best-effort, exactly as elsewhere in AIRE */ }
    }

    /// <summary>One entry in the Log dropdown. ToString is what the combo renders.</summary>
    private sealed class LogChoice
    {
        public LogChoice(string path)
        {
            Path = path;
            var name = System.IO.Path.GetFileName(path);
            DateTime? when = null;
            try { when = File.Exists(path) ? File.GetLastWriteTime(path) : null; } catch { }
            Label = when is { } w ? $"{name}   ({w:d MMM, HH:mm})" : name;
        }

        public string Path { get; }
        private string Label { get; }
        public override string ToString() => Label;
    }
}

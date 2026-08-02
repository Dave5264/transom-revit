using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     AIRE — AI Render Enhancer. WPF port of the stand-alone AIRE.exe (PySide6): batch-enhances
///     architectural render images through OpenAI's image-edit API with cost confirmation, a checkable
///     drag-drop queue, light/dark themes, progress reporting, and a CSV log per batch. Modeless singleton
///     like the Schedule Hub. All processing runs through <see cref="AireJobManager"/> — the same engine
///     the Claude bridge's aire_enhance tool uses, so a job started either way shows up here.
/// </summary>
public sealed partial class AireView
{
    /// <summary>The single open instance (modeless singleton), or null when closed.</summary>
    public static AireView? Instance { get; private set; }

    private readonly ObservableCollection<AireQueueItem> _queue = new();
    private AireJob? _job;

    public AireView()
    {
        InitializeComponent();
        Instance = this;
        QueueList.ItemsSource = _queue;

        ModelCombo.ItemsSource = AireEngine.ModelSizeOptions.Keys.ToList();
        QualityCombo.ItemsSource = AireEngine.QualityOptions;
        ThemeCombo.ItemsSource = new[] { "Light", "Dark" };

        LoadSettings();

        ModelCombo.SelectionChanged += (_, _) => { PopulateSizeCombo(); UpdateEstimate(); };
        SizeCombo.SelectionChanged += (_, _) => UpdateEstimate();
        ThemeCombo.SelectionChanged += (_, _) => ApplyTheme((ThemeCombo.SelectedItem as string) == "Dark");
        PromptBox.TextChanged += (_, _) => UpdateEstimate();

        InputBrowseButton.Click += (_, _) => BrowseFolder(InputFolderBox, "Choose Input Folder");
        OutputBrowseButton.Click += (_, _) => BrowseFolder(OutputFolderBox, "Choose Output Folder");
        BillingButton.Click += (_, _) => OpenUrl(AireEngine.OpenAiBillingUrl);
        // System.Windows.Visibility spelled out: Window has its own Visibility PROPERTY, so the bare
        // enum name binds to that instance member and does not compile.
        ApiKeyHelpButton.Click += (_, _) => ApiKeyHelpOverlay.Visibility = System.Windows.Visibility.Visible;
        ApiKeyHelpCloseButton.Click += (_, _) => ApiKeyHelpOverlay.Visibility = System.Windows.Visibility.Collapsed;
        ApiKeyHelpOpenButton.Click += (_, _) => OpenUrl(AireEngine.OpenAiApiKeysUrl);
        ScanButton.Click += (_, _) => ScanInputFolder();
        ProcessButton.Click += (_, _) => ProcessChecked();
        RemoveButton.Click += (_, _) => RemoveChecked();
        ClearButton.Click += (_, _) => ClearList();
        OpenOutputButton.Click += (_, _) => OpenOutputFolder();

        QueueList.DragEnter += OnQueueDrag;
        QueueList.DragOver += OnQueueDrag;
        QueueList.Drop += OnQueueDrop;

        Closed += (_, _) => { SaveSettings(); Instance = null; };

        // A batch started from the bridge (or before this window was reopened) keeps running in
        // AireJobManager — re-attach so its progress shows here instead of a stale "Ready.".
        var running = AireJobManager.RunningJob;
        if (running != null) Attach(running);

        UpdateEstimate();
    }

    // ---- settings ------------------------------------------------------------

    private void LoadSettings()
    {
        var s = AireSettings.Load();
        ApiKeyBox.Password = s.GetApiKey();
        InputFolderBox.Text = s.InputFolder;
        OutputFolderBox.Text = s.OutputFolder;
        PromptBox.Text = string.IsNullOrWhiteSpace(s.Prompt) ? AireEngine.DefaultPrompt : s.Prompt;
        ModelCombo.SelectedItem = AireEngine.ModelSizeOptions.ContainsKey(s.Model) ? s.Model : AireEngine.DefaultModel;
        PopulateSizeCombo();
        if (SizeCombo.Items.Contains(s.Size)) SizeCombo.SelectedItem = s.Size;
        QualityCombo.SelectedItem = AireEngine.QualityOptions.Contains(s.Quality) ? s.Quality : AireEngine.DefaultQuality;
        ThemeCombo.SelectedItem = s.Theme == "Dark" ? "Dark" : "Light";
        ApplyTheme(s.Theme == "Dark");
    }

    /// <summary>Persists everything including the DPAPI-protected key — which is also what the bridge's
    /// aire_enhance tool reads, so saving here is what makes Claude able to run AIRE at all.</summary>
    private void SaveSettings()
    {
        var s = AireSettings.Load();
        s.SetApiKey(ApiKeyBox.Password);
        s.InputFolder = InputFolderBox.Text.Trim();
        s.OutputFolder = OutputFolderBox.Text.Trim();
        s.Prompt = PromptBox.Text;
        s.Model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        s.Size = SizeCombo.SelectedItem as string ?? AireEngine.DefaultSize;
        s.Quality = QualityCombo.SelectedItem as string ?? AireEngine.DefaultQuality;
        s.Theme = ThemeCombo.SelectedItem as string ?? "Light";
        s.Save();
    }

    private void PopulateSizeCombo()
    {
        var model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        var current = SizeCombo.SelectedItem as string;
        var sizes = AireEngine.ModelSizeOptions.TryGetValue(model, out var list) ? list : new[] { "auto" };
        SizeCombo.ItemsSource = sizes;
        SizeCombo.SelectedItem = current != null && sizes.Contains(current) ? current : sizes[0];
    }

    // ---- queue ---------------------------------------------------------------

    private void BrowseFolder(System.Windows.Controls.TextBox target, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (dlg.ShowDialog(this) == true) target.Text = dlg.FolderName;
    }

    private void ScanInputFolder()
    {
        var folder = InputFolderBox.Text.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            MessageBox.Show(this, "Please choose a valid input folder.", "Input folder missing",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int added = AddFiles(AireEngine.ScanFolder(folder));
        ProgressLabel.Text = $"Added {added} image(s) from input folder.";
        UpdateEstimate();
    }

    private void OnQueueDrag(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnQueueDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path) && AireEngine.IsEnhanceableImage(path)) files.Add(path);
            else if (Directory.Exists(path)) files.AddRange(AireEngine.ScanFolder(path));
        }
        int added = AddFiles(files);
        ProgressLabel.Text = $"Added {added} dropped image(s).";
        UpdateEstimate();
        e.Handled = true;
    }

    private int AddFiles(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(_queue.Select(q => q.FullPath), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var path in paths)
        {
            if (!existing.Add(path)) continue;
            var (tokens, w, h) = AireEngine.EstimateImageTokensFromFile(path);
            var item = new AireQueueItem(path, tokens, w.HasValue && h.HasValue ? $"[{w}x{h}]" : "[unknown]");
            item.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AireQueueItem.IsChecked)) UpdateEstimate(); };
            _queue.Add(item);
            added++;
        }
        if (added == 0 && _queue.Count > 0)
            ProgressLabel.Text = "Those image(s) were already in the list.";
        return added;
    }

    private List<AireQueueItem> CheckedItems() => _queue.Where(q => q.IsChecked).ToList();

    private void RemoveChecked()
    {
        var check = CheckedItems();
        foreach (var item in check) _queue.Remove(item);
        UpdateEstimate();
        ProgressLabel.Text = $"Removed {check.Count} checked image(s).";
    }

    private void ClearList()
    {
        if (_queue.Count == 0) return;
        var answer = MessageBox.Show(this,
            "Remove all pending images from the list?\n\nThis does not delete the actual image files.",
            "Clear list", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _queue.Clear();
        UpdateEstimate();
        ProgressLabel.Text = "Pending list cleared.";
    }

    private void OpenOutputFolder()
    {
        var folder = OutputFolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            MessageBox.Show(this, "Please choose an output folder.", "Missing output folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "The output folder does not exist yet.", "Folder not found",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch { /* explorer launch is best-effort */ }
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* browser launch is best-effort */ }
    }

    // ---- estimation ----------------------------------------------------------

    private void UpdateEstimate()
    {
        if (CostLabel == null) return; // combo events can fire during InitializeComponent
        int textTokens = AireEngine.EstimateTextTokens(PromptBox.Text.Trim());
        int outputTokens = AireEngine.EstimateImageTokensFromSize(SizeCombo.SelectedItem as string ?? "auto");
        double total = CheckedItems().Sum(item => AireEngine.EstimateCost(item.Tokens, outputTokens, textTokens));
        CostLabel.Text = $"Estimated checked cost: ${total:0.0000}";
    }

    // ---- processing ----------------------------------------------------------

    private void ProcessChecked()
    {
        var apiKey = ApiKeyBox.Password.Trim();
        var outputFolder = OutputFolderBox.Text.Trim();
        var prompt = PromptBox.Text.Trim();
        var model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        var size = SizeCombo.SelectedItem as string ?? AireEngine.DefaultSize;
        var quality = QualityCombo.SelectedItem as string ?? AireEngine.DefaultQuality;
        var files = CheckedItems();

        if (apiKey.Length == 0)
        {
            MessageBox.Show(this, "Please paste your OpenAI API key.", "Missing API key",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (outputFolder.Length == 0)
        {
            MessageBox.Show(this, "Please choose an output folder.", "Missing output folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Please check at least one image.", "No images checked",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int textTokens = AireEngine.EstimateTextTokens(prompt);
        int outputTokens = AireEngine.EstimateImageTokensFromSize(size);
        double estimate = files.Sum(f => AireEngine.EstimateCost(f.Tokens, outputTokens, textTokens));

        var answer = MessageBox.Show(this,
            $"You are about to process {files.Count} image(s).\n\nModel: {model}\nResolution: {size}\nQuality: {quality}"
            + $"\n\nEstimated cost: ${estimate:0.0000}\n\nContinue?",
            "Confirm API usage", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SaveSettings(); // remember everything (incl. the key) the moment the user commits to a run

        var job = AireJobManager.Start(files.Select(f => f.FullPath), outputFolder, prompt, model, size, quality,
            apiKey, out var error);
        if (job == null)
        {
            MessageBox.Show(this, error, "AIRE busy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        JobProgressBar.Value = 0;
        ProgressLabel.Text = "Starting...";
        Attach(job);
    }

    /// <summary>Subscribes the window to a job (freshly started here, or one already running via the bridge).</summary>
    private void Attach(AireJob job)
    {
        _job = job;
        SetBusy(true);
        RenderJobState(job);
        job.Progress += OnJobProgress;
    }

    private void OnJobProgress(AireJob job) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(job, _job)) return;
            RenderJobState(job);
            if (job.IsFinished)
            {
                job.Progress -= OnJobProgress;
                _job = null;
                SetBusy(false);
                ShowSummary(job);
            }
        });

    private void RenderJobState(AireJob job)
    {
        JobProgressBar.Value = job.Total == 0 ? 0 : job.Done * 100.0 / job.Total;
        if (!job.IsFinished)
            ProgressLabel.Text = job.CurrentFile.Length > 0
                ? $"Processing {Math.Min(job.Done + 1, job.Total)}/{job.Total}: {job.CurrentFile}"
                : "Starting...";
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        ProcessButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
    }

    private void ShowSummary(AireJob job)
    {
        if (job.Status == "failed")
        {
            ProgressLabel.Text = $"Batch failed: {job.Error}";
            MessageBox.Show(this, $"The batch did not complete.\n\n{job.Error}", "Batch failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var message = $"Batch complete.\n\nSuccess: {job.SuccessCount}\nFailed: {job.FailureCount}"
                      + $"\nTotal time: {AireEngine.SecondsToText(job.TotalTimeSeconds)}"
                      + $"\nEstimated successful cost: ${job.EstimatedCostUsd:0.0000}"
                      + $"\n\nLog saved to:\n{job.LogFile}";
        ProgressLabel.Text = message;
        MessageBox.Show(this, message, "Batch complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- theming -------------------------------------------------------------

    /// <summary>
    ///     Retints every palette brush in Window.Resources. MUTATES the existing SolidColorBrush instances
    ///     (Freezable change notification repaints every consumer, including sealed template visuals) instead
    ///     of replacing the dictionary entries — replacement updated the property system but left already-
    ///     rendered TemplateBinding visuals stale (seen live: standard buttons kept the old theme).
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        void Set(string key, string hex)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            if (Resources[key] is System.Windows.Media.SolidColorBrush existing && !existing.IsFrozen)
                existing.Color = color;
            else
                Resources[key] = new System.Windows.Media.SolidColorBrush(color);
        }

        if (dark)
        {
            Set("AppBg", "#0f172a"); Set("TextPrimary", "#e5e7eb"); Set("TitleFg", "#f8fafc");
            Set("SubtitleFg", "#94a3b8"); Set("MutedFg", "#94a3b8"); Set("CostFg", "#38bdf8");
            Set("CardBg", "#111827"); Set("CardBorder", "#1f2937"); Set("HeroBorder", "#243244");
            Set("InputBg", "#020617"); Set("InputBorder", "#334155"); Set("InputFocusBorder", "#38bdf8");
            Set("SelectionBg", "#2563eb");
            Set("BtnBg", "#1e293b"); Set("BtnHoverBg", "#334155"); Set("BtnHoverBorder", "#475569");
            Set("BtnPressedBg", "#172033"); Set("BtnFg", "#e5e7eb");
            Set("PrimaryBg", "#2563eb"); Set("PrimaryHoverBg", "#1d4ed8"); Set("PrimaryBorder", "#3b82f6");
            Set("PrimaryFg", "#ffffff");
            Set("DangerBg", "#451a1a"); Set("DangerHoverBg", "#7f1d1d"); Set("DangerBorder", "#7f1d1d");
            Set("DangerFg", "#fecaca");
            Set("LinkBg", "#082f49"); Set("LinkHoverBg", "#075985"); Set("LinkBorder", "#0369a1");
            Set("LinkFg", "#bae6fd");
            Set("ListBg", "#0b1220"); Set("ListItemHoverBg", "#1e293b"); Set("ListItemSelectedBg", "#2563eb");
            Set("ProgressBg", "#020617"); Set("ProgressChunk", "#38bdf8");
        }
        else
        {
            Set("AppBg", "#f3f4f6"); Set("TextPrimary", "#111827"); Set("TitleFg", "#0f172a");
            Set("SubtitleFg", "#64748b"); Set("MutedFg", "#64748b"); Set("CostFg", "#2563eb");
            Set("CardBg", "#ffffff"); Set("CardBorder", "#d1d5db"); Set("HeroBorder", "#d1d5db");
            Set("InputBg", "#ffffff"); Set("InputBorder", "#cbd5e1"); Set("InputFocusBorder", "#2563eb");
            Set("SelectionBg", "#93c5fd");
            Set("BtnBg", "#e5e7eb"); Set("BtnHoverBg", "#d1d5db"); Set("BtnHoverBorder", "#94a3b8");
            Set("BtnPressedBg", "#cbd5e1"); Set("BtnFg", "#111827");
            Set("PrimaryBg", "#2563eb"); Set("PrimaryHoverBg", "#1d4ed8"); Set("PrimaryBorder", "#3b82f6");
            Set("PrimaryFg", "#ffffff");
            Set("DangerBg", "#fee2e2"); Set("DangerHoverBg", "#fecaca"); Set("DangerBorder", "#fecaca");
            Set("DangerFg", "#991b1b");
            Set("LinkBg", "#dbeafe"); Set("LinkHoverBg", "#bfdbfe"); Set("LinkBorder", "#93c5fd");
            Set("LinkFg", "#1d4ed8");
            Set("ListBg", "#ffffff"); Set("ListItemHoverBg", "#e0f2fe"); Set("ListItemSelectedBg", "#93c5fd");
            Set("ProgressBg", "#e5e7eb"); Set("ProgressChunk", "#2563eb");
        }
    }
}

/// <summary>One row in the image queue: path + cached token estimate + display resolution.</summary>
public sealed class AireQueueItem : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public AireQueueItem(string fullPath, int tokens, string resolutionText)
    {
        FullPath = fullPath;
        Tokens = tokens;
        ResolutionText = resolutionText;
    }

    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);
    /// <summary>Input-image token estimate cached at add time (the original re-decoded per estimate).</summary>
    public int Tokens { get; }
    public string ResolutionText { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

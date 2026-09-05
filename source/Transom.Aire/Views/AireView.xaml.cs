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

    /// <summary>Set when Cancel is pressed, so progress text stops claiming the batch is still processing.</summary>
    private bool _cancelRequested;

    /// <summary>The open pop-out prompt editor, or null. One at a time — a second would be two views of one
    /// bound string arguing over the caret.</summary>
    private PromptEditorWindow? _promptEditor;

    /// <summary>Set while the preset dropdowns are being rebuilt. Assigning ItemsSource/SelectedItem raises
    /// SelectionChanged, which would otherwise "load" a preset over the text the user just saved.</summary>
    private bool _suppressPresetEvents;

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
        PopOutPromptButton.Click += (_, _) => PopOutPrompt();
        PromptPresetCombo.SelectionChanged += (_, _) => LoadSelectedPrompt();
        SavePromptButton.Click += (_, _) => SaveCurrentPrompt();
        DeletePromptButton.Click += (_, _) => DeleteSelectedPrompt();
        ApiKeyPresetCombo.SelectionChanged += (_, _) => LoadSelectedApiKey();
        SaveApiKeyButton.Click += (_, _) => SaveCurrentApiKey();
        DeleteApiKeyButton.Click += (_, _) => DeleteSelectedApiKey();
        ScanButton.Click += (_, _) => ScanInputFolder();
        ProcessButton.Click += (_, _) => ProcessChecked();
        CancelButton.Click += (_, _) => CancelBatch();
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
        PromptNameBox.Text = s.SelectedPromptName;
        ApiKeyNameBox.Text = s.SelectedApiKeyName;
        RefreshPresetCombos(s, s.SelectedPromptName, s.SelectedApiKeyName);
        ApplyTheme(s.Theme == "Dark");
    }

    /// <summary>Persists everything including the DPAPI-protected key — which is also what the bridge's
    /// aire_enhance tool reads, so saving here is what makes Claude able to run AIRE at all.</summary>
    private void SaveSettings()
    {
        // Reloaded from disk rather than kept in a field on purpose: the prompt/key libraries below are
        // written the moment they change, and a second AIRE (the standalone app alongside Revit's) may have
        // added to them since this window opened. Re-reading means closing this window preserves those
        // instead of writing back a stale copy.
        var s = AireSettings.Load();
        s.SetApiKey(ApiKeyBox.Password);
        s.SelectedPromptName = PromptPresetCombo.SelectedItem as string ?? "";
        s.SelectedApiKeyName = ApiKeyPresetCombo.SelectedItem as string ?? "";
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

    // ---- saved prompts & API keys --------------------------------------------

    /// <summary>
    ///     Rebuilds both preset dropdowns from a settings snapshot and re-selects by name (case-insensitively,
    ///     since the stored entry keeps its original casing when overwritten). Always guarded — see
    ///     <see cref="_suppressPresetEvents"/>.
    /// </summary>
    private void RefreshPresetCombos(AireSettings s, string? promptName = null, string? keyName = null)
    {
        _suppressPresetEvents = true;
        try
        {
            var prompts = s.SavedPrompts.Select(p => p.Name).ToList();
            PromptPresetCombo.ItemsSource = prompts;
            PromptPresetCombo.SelectedItem = MatchName(prompts, promptName);

            var keys = s.SavedApiKeys.Select(k => k.Name).ToList();
            ApiKeyPresetCombo.ItemsSource = keys;
            ApiKeyPresetCombo.SelectedItem = MatchName(keys, keyName);
        }
        finally { _suppressPresetEvents = false; }
    }

    private static string? MatchName(List<string> names, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted)
            ? null
            : names.FirstOrDefault(n => string.Equals(n, wanted.Trim(), StringComparison.OrdinalIgnoreCase));

    private void LoadSelectedPrompt()
    {
        if (_suppressPresetEvents) return;
        if (PromptPresetCombo.SelectedItem is not string name) return;
        var entry = AireSettings.Load().FindPrompt(name);
        if (entry == null) return;
        PromptBox.Text = entry.Text; // TextChanged re-estimates, and the pop-out editor is bound to this box
        PromptNameBox.Text = entry.Name; // so editing then re-Saving updates this preset instead of orphaning it
        ProgressLabel.Text = $"Loaded saved prompt \"{entry.Name}\".";
    }

    private void SaveCurrentPrompt()
    {
        var name = PromptNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Type a name for this prompt first — that is what the dropdown will show.",
                "Name the prompt", MessageBoxButton.OK, MessageBoxImage.Warning);
            PromptNameBox.Focus();
            return;
        }

        // Written straight to disk rather than waiting for SaveSettings on close: a prompt worth naming is
        // worth surviving a crash, and it is also how the OTHER AIRE process gets to see it.
        var s = AireSettings.Load();
        if (s.FindPrompt(name) != null && MessageBox.Show(this,
                $"A saved prompt called \"{name}\" already exists.\n\nReplace it?",
                "Replace saved prompt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        s.UpsertPrompt(name, PromptBox.Text);
        s.SelectedPromptName = name;
        s.Save();
        RefreshPresetCombos(s, name, ApiKeyPresetCombo.SelectedItem as string);
        ProgressLabel.Text = $"Saved prompt \"{name}\".";
    }

    private void DeleteSelectedPrompt()
    {
        if (PromptPresetCombo.SelectedItem is not string name)
        {
            MessageBox.Show(this, "Pick a saved prompt in the dropdown first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Delete the saved prompt \"{name}\"?\n\nThe text in the Prompt box is kept.",
                "Delete saved prompt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var s = AireSettings.Load();
        s.RemovePrompt(name);
        s.SelectedPromptName = "";
        s.Save();
        RefreshPresetCombos(s, null, ApiKeyPresetCombo.SelectedItem as string);
        ProgressLabel.Text = $"Deleted saved prompt \"{name}\".";
    }

    /// <summary>
    ///     Switches to a saved account key. Takes effect immediately — including in the stored settings —
    ///     because the bridge's aire_enhance tool reads the stored key, so leaving the switch until the window
    ///     closes would let a Claude-started batch spend against the account the user just moved away from.
    /// </summary>
    private void LoadSelectedApiKey()
    {
        if (_suppressPresetEvents) return;
        if (ApiKeyPresetCombo.SelectedItem is not string name) return;

        var s = AireSettings.Load();
        var key = s.GetSavedApiKey(name);
        if (key.Length == 0)
        {
            MessageBox.Show(this,
                $"The saved key \"{name}\" could not be read.\n\nSaved keys are encrypted for one Windows user "
                + "account, so a key saved under a different profile (or a copied settings file) cannot be "
                + "decrypted here. Paste the key again and save it.",
                "Key unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApiKeyBox.Password = key;
        ApiKeyNameBox.Text = s.FindApiKey(name)?.Name ?? name;
        s.SetApiKey(key);
        s.SelectedApiKeyName = name;
        s.Save();
        ProgressLabel.Text = $"Now using API key \"{name}\".";
    }

    private void SaveCurrentApiKey()
    {
        var name = ApiKeyNameBox.Text.Trim();
        var key = ApiKeyBox.Password.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Type a name for this account first, e.g. Studio or Personal.",
                "Name the key", MessageBoxButton.OK, MessageBoxImage.Warning);
            ApiKeyNameBox.Focus();
            return;
        }
        if (key.Length == 0)
        {
            MessageBox.Show(this, "Paste the key into the API Key box before saving it.", "No key to save",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ApiKeyBox.Focus();
            return;
        }

        var s = AireSettings.Load();
        if (s.FindApiKey(name) != null && MessageBox.Show(this,
                $"A saved key called \"{name}\" already exists.\n\nReplace it?",
                "Replace saved key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        s.UpsertApiKey(name, key);
        s.SetApiKey(key); // the key just saved is also the one now in use
        s.SelectedApiKeyName = name;
        s.Save();
        RefreshPresetCombos(s, PromptPresetCombo.SelectedItem as string, name);
        ProgressLabel.Text = $"Saved API key \"{name}\".";
    }

    private void DeleteSelectedApiKey()
    {
        if (ApiKeyPresetCombo.SelectedItem is not string name)
        {
            MessageBox.Show(this, "Pick a saved key in the dropdown first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
                $"Delete the saved key \"{name}\"?\n\nThis only forgets it here — the key itself stays valid on "
                + "your OpenAI account, and the key currently in the API Key box is left as it is.",
                "Delete saved key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var s = AireSettings.Load();
        s.RemoveApiKey(name);
        s.SelectedApiKeyName = "";
        s.Save();
        RefreshPresetCombos(s, PromptPresetCombo.SelectedItem as string, null);
        ProgressLabel.Text = $"Deleted saved key \"{name}\".";
    }

    /// <summary>Opens (or re-fronts) the resizable prompt editor. It binds to PromptBox, so there is nothing
    /// to copy back — see <see cref="PromptEditorWindow"/>.</summary>
    private void PopOutPrompt()
    {
        if (_promptEditor != null)
        {
            // Spelled out: Window has its own WindowState PROPERTY, so the bare enum name binds to that
            // instance member and does not compile (same trap as System.Windows.Visibility below).
            if (_promptEditor.WindowState == System.Windows.WindowState.Minimized)
                _promptEditor.WindowState = System.Windows.WindowState.Normal;
            _promptEditor.Activate();
            return;
        }
        var editor = new PromptEditorWindow(this, PromptBox);
        editor.Closed += (_, _) => _promptEditor = null;
        _promptEditor = editor;
        editor.Show();
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

    /// <summary>
    ///     Requests cancellation of the running batch. The run loop stops before the next image; the one
    ///     already in flight is abandoned locally but may still be generated — and billed — by OpenAI, so
    ///     say so rather than implying the spend stops dead.
    /// </summary>
    private void CancelBatch()
    {
        var job = _job;
        if (job == null || job.IsFinished) return;
        _cancelRequested = true;
        CancelButton.IsEnabled = false; // one press is enough; the run loop does the rest
        ProgressLabel.Text = "Cancelling — the image already generating may still finish (and be billed)...";
        job.Cancel();
    }

    /// <summary>Subscribes the window to a job (freshly started here, or one already running via the bridge).</summary>
    private void Attach(AireJob job)
    {
        _job = job;
        // A bridge-started job may already have been cancelled by Claude before this window attached.
        _cancelRequested = job.CancelRequested;
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
        if (job.IsFinished) return;
        ProgressLabel.Text = _cancelRequested || job.CancelRequested
            ? "Cancelling — the image already generating may still finish (and be billed)..."
            : job.CurrentFile.Length > 0
                ? $"Processing {Math.Min(job.Done + 1, job.Total)}/{job.Total}: {job.CurrentFile}"
                : "Starting...";
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        ProcessButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        // Cancel is the one control that is live only DURING a batch, and only until it has been pressed.
        CancelButton.IsEnabled = busy && !_cancelRequested;
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
        // A cancelled batch still reaches "completed" (it stops cleanly and writes its log), so report it
        // as cancelled rather than complete — and account for the images that were never attempted.
        var cancelled = job.CancelRequested;
        var notAttempted = Math.Max(0, job.Total - job.Done);
        var message = (cancelled ? "Batch cancelled." : "Batch complete.")
                      + $"\n\nSuccess: {job.SuccessCount}\nFailed: {job.FailureCount}"
                      + (cancelled && notAttempted > 0 ? $"\nNot attempted: {notAttempted}" : "")
                      + $"\nTotal time: {AireEngine.SecondsToText(job.TotalTimeSeconds)}"
                      + $"\nEstimated successful cost: ${job.EstimatedCostUsd:0.0000}"
                      + $"\n\nLog saved to:\n{job.LogFile}";
        ProgressLabel.Text = message;
        MessageBox.Show(this, message, cancelled ? "Batch cancelled" : "Batch complete",
            MessageBoxButton.OK, MessageBoxImage.Information);
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

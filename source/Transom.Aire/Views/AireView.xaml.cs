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
///     <para>
///     Since the Video tab (AireView.Video.cs) the window is two tabs under one hero card: Enhance is this
///     file, unchanged in behaviour; Video turns one finished render into a short clip through Higgsfield,
///     behind the same cost confirmation and the same one-job-at-a-time lock.
///     </para>
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

    /// <summary>The open log reader, or null. Modeless singleton like the prompt editor: a second View Log
    /// click re-points the open window at the newly chosen log instead of stacking another one.</summary>
    private LogViewerWindow? _logViewer;

    /// <summary>The CSV View Log will open: the finished job's log, or failing that the newest one under the
    /// output folder — which is what makes the button live on a fresh open, and for a batch Claude ran.</summary>
    private string _viewLogTarget = "";

    /// <summary>
    ///     Set when OpenAI has actually refused the key in the box for verification, so the banner leads with
    ///     what happened rather than with a generic invitation. Cleared when the key changes or the user
    ///     acknowledges again.
    /// </summary>
    private string _verificationRefusal = "";

    /// <summary>Set while the preset dropdowns are being rebuilt. Assigning ItemsSource/SelectedItem raises
    /// SelectionChanged, which would otherwise "load" a preset over the text the user just saved.</summary>
    private bool _suppressPresetEvents;

    /// <summary>
    ///     The user's own Fidelity choice, held separately from the checkbox. While the box is disabled it is
    ///     forced ticked (the model reads at high fidelity whether you ask or not), and writing that back to
    ///     settings would silently overwrite a deliberate "low" the moment a supported model reappears.
    /// </summary>
    private bool _fidelityPreference = true;

    /// <summary>The Fidelity tooltip for a model that DOES expose the setting. Lives here rather than in the
    /// XAML because <see cref="UpdateFidelityAvailability"/> swaps between it and the engine's refusal note.</summary>
    private const string FidelitySupportedTip =
        "Sends input_fidelity: high, the API parameter that does what the default prompt spends four sentences "
        + "asking for: keep the camera, geometry, mullions and composition. Turn it off to send the API default "
        + "(low). It may raise the input-token cost slightly; the CSV log records what each image billed.";

    public AireView()
    {
        InitializeComponent();
        Instance = this;
        QueueList.ItemsSource = _queue;

        ModelCombo.ItemsSource = AireEngine.Models;
        // Sizes and qualities are both per model — see PopulateSizeCombo / PopulateQualityCombo.
        ThemeCombo.ItemsSource = new[] { "Light", "Dark" };

        var settings = AireSettings.Load();
        LoadSettings(settings);

        ModelCombo.SelectionChanged += (_, _) =>
        {
            PopulateSizeCombo();
            PopulateQualityCombo();
            // Fidelity is per model exactly like size and quality — it belongs in the same refresh.
            UpdateFidelityAvailability();
            UpdateEstimate();
        };
        // Record the user's own toggle only while the box is live; a forced tick on an unsupported model is
        // AIRE stating a fact about the API, not a preference to remember.
        FidelityCheck.Checked += (_, _) => { if (FidelityCheck.IsEnabled) _fidelityPreference = true; };
        FidelityCheck.Unchecked += (_, _) => { if (FidelityCheck.IsEnabled) _fidelityPreference = false; };
        SizeCombo.SelectionChanged += (_, _) => UpdateEstimate();
        // Quality is the setting that decides the output-token spend (36x between low and max at 4K), so
        // the number on screen has to move when it does.
        QualityCombo.SelectionChanged += (_, _) => UpdateEstimate();
        ThemeCombo.SelectionChanged += (_, _) => ApplyTheme((ThemeCombo.SelectedItem as string) == "Dark");
        PromptBox.TextChanged += (_, _) => UpdateEstimate();
        MigrationNoticeCloseButton.Click += (_, _) => MigrationNotice.Visibility = System.Windows.Visibility.Collapsed;

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
        // The verification banner follows the KEY, not the preset name: verification is per OpenAI
        // organization, and a user with a Studio key and a Personal key has two of them.
        ApiKeyBox.PasswordChanged += (_, _) => { _verificationRefusal = ""; UpdateVerificationNotice(); };
        VerificationNoticeButton.Click += (_, _) => ShowVerificationOverlay();
        VerificationOpenButton.Click += (_, _) => OpenUrl(AireEngine.OpenAiVerifyOrganizationUrl);
        VerificationCloseButton.Click += (_, _) => VerificationOverlay.Visibility = System.Windows.Visibility.Collapsed;
        // Disabled until the box is ticked: that is what stops the claim being clicked past on reflex.
        VerificationAckCheck.Checked += (_, _) => VerificationEnableButton.IsEnabled = true;
        VerificationAckCheck.Unchecked += (_, _) => VerificationEnableButton.IsEnabled = false;
        VerificationEnableButton.Click += (_, _) => AcknowledgeVerification();

        ViewLogButton.Click += (_, _) => OpenLogViewer(_viewLogTarget);
        VideoViewLogButton.Click += (_, _) => OpenLogViewer(_videoLogTarget);
        // A log lives in <output>\logs, so which log View Log opens moves with this box.
        OutputFolderBox.TextChanged += (_, _) => UpdateViewLogAvailability();

        ScanButton.Click += (_, _) => ScanInputFolder();
        ProcessButton.Click += (_, _) => ProcessChecked();
        CancelButton.Click += (_, _) => CancelBatch();
        RemoveButton.Click += (_, _) => RemoveChecked();
        ClearButton.Click += (_, _) => ClearList();
        OpenOutputButton.Click += (_, _) => OpenOutputFolder();

        QueueList.DragEnter += OnQueueDrag;
        QueueList.DragOver += OnQueueDrag;
        QueueList.Drop += OnQueueDrop;

        InitVideoTab(settings);

        Closed += (_, _) => { _estimateTimer.Stop(); SaveSettings(); Instance = null; };

        // A job started from the bridge (or before this window was reopened) keeps running in
        // AireJobManager — re-attach so its progress shows here instead of a stale "Ready.".
        var running = AireJobManager.RunningJob;
        if (running is AireJob batch) Attach(batch);
        else if (running is AireVideoJob clip) AttachVideo(clip);

        // Both run last: LoadSettings filled the key box and the folders before these handlers existed.
        UpdateVerificationNotice();
        UpdateViewLogAvailability();
        UpdateEstimate();
    }

    // ---- settings ------------------------------------------------------------

    private void LoadSettings(AireSettings s)
    {
        ApiKeyBox.Password = s.GetApiKey();
        InputFolderBox.Text = s.InputFolder;
        OutputFolderBox.Text = s.OutputFolder;
        PromptBox.Text = string.IsNullOrWhiteSpace(s.Prompt) ? AireEngine.DefaultPrompt : s.Prompt;

        // A saved model OpenAI is retiring lands on the current default with its quality mapped onto the
        // new ladder, and the window SAYS so: landing there silently would change what the next batch
        // spends and produces without the user touching a control. Shown once by construction — the next
        // SaveSettings writes the new model, so the next open finds nothing to migrate.
        var model = s.Model;
        var quality = s.Quality;
        if (AireEngine.IsRetiredModel(model))
        {
            model = AireEngine.DefaultModel;
            quality = AireEngine.MigrateQuality(s.Quality);
            ShowMigrationNotice(s.Model, AireEngine.RetiredModels[s.Model], s.Quality, quality);
        }
        else if (!AireEngine.IsKnownModel(model)) model = AireEngine.DefaultModel;

        ModelCombo.SelectedItem = model;
        PopulateSizeCombo();
        if (SizeCombo.Items.Contains(s.Size)) SizeCombo.SelectedItem = s.Size;
        PopulateQualityCombo();
        QualityCombo.SelectedItem = AireEngine.IsValidQuality(model, quality) ? quality : AireEngine.DefaultQualityFor(model);
        // The SAVED choice goes into the preference, not straight onto the box — the box may be about to be
        // disabled and forced ticked, and that must not become the value written back on close.
        _fidelityPreference = s.HighInputFidelity;
        UpdateFidelityAvailability();
        ThemeCombo.SelectedItem = s.Theme == "Dark" ? "Dark" : "Light";
        PromptNameBox.Text = s.SelectedPromptName;
        ApiKeyNameBox.Text = s.SelectedApiKeyName;
        RefreshPresetCombos(s, s.SelectedPromptName, s.SelectedApiKeyName);
        ApplyTheme(s.Theme == "Dark");
    }

    /// <summary>Persists everything including the DPAPI-protected keys — which is also what the bridge's
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
        s.Quality = QualityCombo.SelectedItem as string ?? AireEngine.DefaultQualityFor(s.Model);
        s.HighInputFidelity = _fidelityPreference; // never the disabled box's forced value — see the field's remarks
        s.Theme = ThemeCombo.SelectedItem as string ?? "Light";
        SaveVideoSettings(s);
        s.Save();
    }

    private void PopulateSizeCombo()
    {
        var model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        var current = SizeCombo.SelectedItem as string;
        var sizes = AireEngine.SizeOptionsFor(model);
        SizeCombo.ItemsSource = sizes;
        SizeCombo.SelectedItem = current != null && sizes.Contains(current) ? current : sizes[0];
    }

    /// <summary>
    ///     Same shape as <see cref="PopulateSizeCombo"/>: the selected model's rungs, keeping a still-valid
    ///     selection and otherwise falling back to the model's default (index 0). Switching gpt-image-2 →
    ///     2.5 keeps "high", which on 2.5 is a cheaper rung — the estimate line is what shows that.
    /// </summary>
    private void PopulateQualityCombo()
    {
        var model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        var current = QualityCombo.SelectedItem as string;
        var rungs = AireEngine.QualityOptionsFor(model);
        QualityCombo.ItemsSource = rungs;
        QualityCombo.SelectedItem = current != null && rungs.Contains(current) ? current : rungs[0];
    }

    /// <summary>
    ///     Third of the per-model refreshers, beside <see cref="PopulateSizeCombo"/> and
    ///     <see cref="PopulateQualityCombo"/>. Not cosmetic: no model in the current catalog accepts
    ///     <c>input_fidelity</c>, and until v1.9.18 the engine sent it anyway on every request, so the shipped
    ///     default failed every image. The engine no longer sends it (AireEngine.SupportsInputFidelity); this is
    ///     how the user is TOLD, rather than left with a live control that quietly does nothing.
    /// </summary>
    private void UpdateFidelityAvailability()
    {
        var model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        bool supported = AireEngine.SupportsInputFidelity(model);

        // Order matters: IsEnabled first, so the IsChecked assignment below reaches the Checked/Unchecked
        // handlers with the box already disabled and is therefore not recorded as a preference.
        FidelityCheck.IsEnabled = supported;
        // The LABEL changes, not the layout: a caption line under the box would push the Prompt card past the
        // bottom of the left column (the star-row trap the Video-tab work already paid for).
        FidelityCheck.Content = supported
            ? "High input fidelity: stay close to the source"
            : "High input fidelity: always on for this model";
        FidelityCheck.IsChecked = supported ? _fidelityPreference : true;
        FidelityCheck.ToolTip = supported
            ? FidelitySupportedTip
            : AireEngine.InputFidelityNote(model)
              + " This setting only applies to models that let you turn it down.";
    }

    /// <summary>The in-window notice for a retired saved model — wording is the feature in a tool that spends money.</summary>
    private void ShowMigrationNotice(string oldModel, string shutdownDate, string oldQuality, string newQuality)
    {
        var mapped = string.Equals(oldQuality, newQuality, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $", and Quality has been moved from {oldQuality} to {newQuality}, the rung that spends about the same";
        MigrationNoticeText.Text =
            $"AIRE was set to {oldModel}, which OpenAI switches off on {shutdownDate}. It has been changed to "
            + $"{AireEngine.DefaultModel}, the current model{mapped}. Your prompt, folders and saved keys are unchanged."
            + "\n\nQuality settings are not directly comparable between the old and new models. Check the estimate "
            + "before your first batch. If your renders look softer than you expect, move Quality up a step; if "
            + "they cost more than you expect, move it down.";
        MigrationNotice.Visibility = System.Windows.Visibility.Visible;
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

            // OpenAI keys only — the Video tab's dropdown lists the Higgsfield pairs.
            var keys = s.SavedApiKeysFor(AireSettings.ProviderOpenAi).Select(k => k.Name).ToList();
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

    // ---- OpenAI organization verification ------------------------------------

    /// <summary>
    ///     Shows or hides the amber banner for the key currently in the box. Verification is a one-time check on
    ///     the ORGANIZATION and OpenAI publishes no endpoint that reports it, so AIRE can only ask — and the
    ///     answer is remembered against a fingerprint of the key, which is what makes it follow the account
    ///     rather than the preset name.
    /// </summary>
    private void UpdateVerificationNotice()
    {
        if (VerificationNotice == null) return; // events can fire during InitializeComponent
        var key = ApiKeyBox.Password.Trim();
        bool needed = key.Length > 0 && !AireSettings.Load().IsVerificationAcknowledged(key);

        VerificationNotice.Visibility = needed
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        if (!needed) return;

        bool refused = _verificationRefusal.Length > 0;
        VerificationNoticeTitle.Text = refused
            ? "OpenAI refused this account."
            : "This OpenAI account has not been verified yet.";
        VerificationNoticeText.Text = refused
            ? _verificationRefusal
              + " Verification is a one-time identity check on the organization, separate from billing credit."
            : "OpenAI will not generate images for an account that has not completed organization verification. "
              + "This is separate from adding billing credit — an account with money on it is still refused "
              + "until it verifies.";
    }

    private void ShowVerificationOverlay()
    {
        // Always re-armed: the acknowledgement is a fresh claim about the key in the box right now.
        VerificationAckCheck.IsChecked = false;
        VerificationEnableButton.IsEnabled = false;
        VerificationOverlay.Visibility = System.Windows.Visibility.Visible;
    }

    /// <summary>
    ///     Records the user's claim for the key in the box. Deliberately does NOT start the batch: the spend
    ///     confirmation is its own explicit act, and folding it into this click would turn a "yes, I verified"
    ///     into a "yes, spend the money".
    /// </summary>
    private void AcknowledgeVerification()
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            MessageBox.Show(this, "Paste your OpenAI API key first — the acknowledgement is recorded against "
                                  + "the account that key belongs to.", "No key",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AireSettings.Load().AcknowledgeVerification(key);
        _verificationRefusal = "";
        VerificationOverlay.Visibility = System.Windows.Visibility.Collapsed;
        UpdateVerificationNotice();
        ProgressLabel.Text = "GPT Image models enabled for this account. Click Process Checked when you are ready.";
    }

    /// <summary>
    ///     Withdraws the acknowledgement after OpenAI has refused the account, and brings the banner back
    ///     carrying the refusal. This is what keeps the claim honest rather than a checkbox that launders a
    ///     guess. Uses the key in the box: the job never carries one (it stays inside the run closure), and a
    ///     bridge-started batch spends the stored key, which is the same key this box holds.
    /// </summary>
    private void OnVerificationRefused()
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length > 0) AireSettings.Load().ClearVerification(key);
        _verificationRefusal = $"OpenAI refused this account on {DateTime.Now:d MMM} at {DateTime.Now:HH:mm}: "
                               + "the organization is not verified.";
        UpdateVerificationNotice();
    }

    // ---- the log reader ------------------------------------------------------

    /// <summary>
    ///     Decides what View Log would open, and whether it is live at all: the log of the batch that just
    ///     finished, or failing that the newest CSV under the output folder. The second half is what makes the
    ///     button useful on a fresh open and after a batch Claude ran through the bridge.
    /// </summary>
    private void UpdateViewLogAvailability(string? preferred = null)
    {
        if (ViewLogButton == null) return;
        if (!string.IsNullOrEmpty(preferred) && File.Exists(preferred)) _viewLogTarget = preferred!;
        else if (_viewLogTarget.Length == 0 || !File.Exists(_viewLogTarget))
            _viewLogTarget = AireLogReport.NewestLogIn(OutputFolderBox.Text.Trim()) ?? "";
        ViewLogButton.IsEnabled = _viewLogTarget.Length > 0;
    }

    /// <summary>Opens (or re-points) the modeless log reader.</summary>
    private void OpenLogViewer(string csvPath)
    {
        if (csvPath.Length == 0 || !File.Exists(csvPath))
        {
            MessageBox.Show(this, "There is no batch log to read yet. A log is written into <output folder>\\logs "
                                  + "each time a batch finishes.", "No log",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_logViewer != null)
        {
            if (_logViewer.WindowState == System.Windows.WindowState.Minimized)
                _logViewer.WindowState = System.Windows.WindowState.Normal;
            _logViewer.LoadLog(csvPath);
            _logViewer.Activate();
            return;
        }
        var viewer = new LogViewerWindow(this, csvPath);
        viewer.Closed += (_, _) => _logViewer = null;
        _logViewer = viewer;
        viewer.Show();
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
        var model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        var quality = QualityCombo.SelectedItem as string ?? AireEngine.DefaultQualityFor(model);
        int outputTokens = AireEngine.EstimateOutputTokens(model, SizeCombo.SelectedItem as string ?? "auto", quality);
        double total = CheckedItems().Sum(item => AireEngine.EstimateCost(item.Tokens, outputTokens, textTokens));
        _enhanceCostText = $"Estimated checked cost: ${total:0.0000}";
        UpdateHeroForTab();
    }

    // ---- processing ----------------------------------------------------------

    private void ProcessChecked()
    {
        var apiKey = ApiKeyBox.Password.Trim();
        var outputFolder = OutputFolderBox.Text.Trim();
        var prompt = PromptBox.Text.Trim();
        var model = ModelCombo.SelectedItem as string ?? AireEngine.DefaultModel;
        var size = SizeCombo.SelectedItem as string ?? AireEngine.DefaultSize;
        var quality = QualityCombo.SelectedItem as string ?? AireEngine.DefaultQualityFor(model);
        // IsEnabled too: on a model that refuses the parameter the box is ticked to state a fact, and passing
        // that through as "the user asked for high" would put it back on the wire.
        var highFidelity = FidelityCheck.IsEnabled && FidelityCheck.IsChecked == true;
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

        // The verification gate, before anything about cost is asked: an unverified organization is refused by
        // every GPT Image model, so a user who has not been through it is about to buy nothing at all. One
        // click, once per OpenAI account, on this machine — not a nag. The overlay IS the message; putting a
        // MessageBox in front of it would just be something else to dismiss.
        if (!AireSettings.Load().IsVerificationAcknowledged(apiKey))
        {
            ShowVerificationOverlay();
            return;
        }

        // Say "busy" in words BEFORE the cost dialog — a video clip may be running on the other tab.
        var busy = AireJobManager.BusyReason();
        if (busy != null)
        {
            MessageBox.Show(this, busy, "AIRE busy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int textTokens = AireEngine.EstimateTextTokens(prompt);
        int outputTokens = AireEngine.EstimateOutputTokens(model, size, quality);
        double estimate = files.Sum(f => AireEngine.EstimateCost(f.Tokens, outputTokens, textTokens));

        // The per-image figure is the number a person can sanity-check against the folder they are about to
        // point at; a batch total on its own hides a factor-of-ten mistake. And the log sentence is what
        // makes the estimate honest now that the billed cost is recorded afterwards.
        // The fidelity line is DROPPED entirely on a model that does not take the parameter: a confirmation
        // dialog must never list a setting that has no effect on what is about to be bought.
        var fidelityLine = AireEngine.SupportsInputFidelity(model)
            ? $"\nInput fidelity: {(highFidelity ? "high" : "low")}"
            : "";
        var answer = MessageBox.Show(this,
            $"You are about to process {files.Count} image(s).\n\nModel: {model}\nResolution: {size}\nQuality: {quality}"
            + fidelityLine
            + $"\n\nEstimated cost: ${estimate:0.0000}  (${estimate / files.Count:0.0000} per image)"
            + "\n\nThis is an estimate. The exact cost of each image is recorded in the CSV log when the batch finishes."
            + "\n\nContinue?",
            "Confirm API usage", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SaveSettings(); // remember everything (incl. the key) the moment the user commits to a run

        var job = AireJobManager.Start(files.Select(f => f.FullPath), outputFolder, prompt, model, size, quality,
            apiKey, out var error, highFidelity);
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

    private void OnJobProgress(AireJobBase job) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (job is not AireJob batch || !ReferenceEquals(batch, _job)) return;
            RenderJobState(batch);
            if (batch.IsFinished)
            {
                batch.Progress -= OnJobProgress;
                _job = null;
                SetBusy(false);
                ShowSummary(batch);
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
        // The acknowledgement was wrong, so withdraw it and bring the banner back — before the dialog, so the
        // window behind it already tells the truth.
        if (job.AbortKind == "verification") OnVerificationRefused();
        UpdateViewLogAvailability(job.LogFile);

        if (job.Status == "failed")
        {
            ProgressLabel.Text = $"Batch failed: {job.Error}";
            MessageBox.Show(this, $"The batch did not complete.\n\n{job.Error}", "Batch failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // A cancelled or self-stopped batch still reaches "completed" (it stops cleanly and writes its log), so
        // report what actually happened — and account for the images that were never attempted.
        var cancelled = job.CancelRequested;
        var notAttempted = job.NotAttempted;
        // "Stopped" only claims that something was lost when something was: a refusal on the LAST render in the
        // queue ends the loop too, but there was nothing left to skip.
        var headline = job.Aborted
            ? (notAttempted > 0
                  ? "Batch stopped — the same refusal would have come back for every remaining render."
                  : "The last render was refused, and the refusal was not about that image.")
              + "\n\n" + job.AbortReason + "\n"
            : cancelled ? "Batch cancelled." : "Batch complete.";
        var message = headline
                      + $"\n\nSuccess: {job.SuccessCount}\nFailed: {job.FailureCount}"
                      + ((cancelled || job.Aborted) && notAttempted > 0
                          ? $"\nNot attempted: {notAttempted}"
                            + (job.Aborted ? " (the same refusal would have come back for each one)" : "")
                          : "")
                      + $"\nTotal time: {AireEngine.SecondsToText(job.TotalTimeSeconds)}"
                      + $"\nEstimated successful cost: ${job.EstimatedCostUsd:0.0000}"
                      + ActualCostLine(job);
        ProgressLabel.Text = message;

        var title = job.Aborted ? "Batch stopped" : cancelled ? "Batch cancelled" : "Batch complete";

        // The "Log saved to: <path>" line was the wrong answer to "what happened?" — offer the thing that
        // answers it instead. WPF's MessageBox cannot relabel its buttons, so the prompt is phrased as the
        // question Yes/No actually answers; the View Log button in the Processing Status card is the named
        // route to the same reader, and it is live from here on.
        if (job.AbortKind == "verification")
        {
            var answer = MessageBox.Show(this, message + "\n\nOpen the OpenAI verification page now?",
                title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) OpenUrl(AireEngine.OpenAiVerifyOrganizationUrl);
            return;
        }
        if ((job.FailureCount > 0 || job.Aborted) && job.LogFile.Length > 0)
        {
            var answer = MessageBox.Show(this, message + "\n\nRead the log now?",
                title, MessageBoxButton.YesNo,
                job.Aborted ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) OpenLogViewer(job.LogFile);
            return;
        }
        MessageBox.Show(this, message + $"\n\nLog saved to:\n{job.LogFile}", title,
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>What OpenAI billed, beside the estimate, once at least one response carried a usage block.
    /// Says so when only some images reported, rather than passing a partial sum off as the total.</summary>
    private static string ActualCostLine(AireJob job)
    {
        if (!job.HasActualCost) return "";
        var partial = job.ActualCostImages < job.SuccessCount
            ? $" ({job.ActualCostImages} of {job.SuccessCount} images reported usage)"
            : "";
        return $"\nActual cost billed by OpenAI: ${job.ActualCostUsd:0.0000}{partial}";
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
            Set("NoticeBg", "#3d2a06"); Set("NoticeBorder", "#92400e"); Set("NoticeFg", "#fcd34d");
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
            Set("NoticeBg", "#fef3c7"); Set("NoticeBorder", "#fcd34d"); Set("NoticeFg", "#92400e");
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

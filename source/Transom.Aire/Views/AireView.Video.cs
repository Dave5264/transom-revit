using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     The Video tab: one finished render → one short generated clip through Higgsfield. Everything that
///     spends goes through <see cref="AireJobManager.StartVideo"/>, behind the same cost confirmation and
///     the same cross-process lock as the Enhance tab. The cost shown is never computed locally — it is
///     the vendor's own estimate for exactly the request that would be sent, refreshed whenever a
///     parameter changes, and Generate is refused outright if that estimate cannot be obtained.
/// </summary>
public sealed partial class AireView
{
    /// <summary>A dropdown row for a per-model parameter: the raw API value plus what the user reads.</summary>
    private sealed class Choice
    {
        public string Raw = "";
        public string Display = "";
        public override string ToString() => Display;
    }

    /// <summary>A dropdown row for a camera preset; Id "" is the "none" entry.</summary>
    private sealed class PresetChoice
    {
        public string Id = "";
        public string Name = "";
        public override string ToString() => Name;
    }

    private const string NoPreset = "— none —";

    /// <summary>A syntactically valid URL for the cost estimate, which does not depend on the image. Only if
    /// the vendor insists on a real one is the render uploaded early (see <see cref="FetchEstimateAsync"/>).</summary>
    private const string EstimatePlaceholderUrl = "https://aire.transom.invalid/source.png";

    private const string DefaultVideoPrompt =
        "Slow, gentle push-in on the building. Keep the camera nearly still and keep every wall, roofline, window "
        + "and material exactly as drawn — only the light, sky and foliage move.";

    private const string EnhanceSubtitle =
        "Batch-enhance architectural renders with controlled model, resolution, quality, and cost confirmation.";

    private const string VideoSubtitle =
        "Turn one finished render into a short generated clip — a hero shot with motion, not a walkthrough.";

    private HiggsfieldCatalog _catalog = new();
    private AireVideoJob? _videoJob;
    private PromptEditorWindow? _videoPromptEditor;
    private bool _suppressVideoEvents;
    private string _videoSource = "";
    private int _videoSourceW, _videoSourceH;
    private string _lastClip = "";

    private readonly DispatcherTimer _estimateTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <summary>Ticks once a second while a clip runs, so the status line shows elapsed time and when the
    /// vendor was last asked. A Master-tier clip can sit in in_progress for eight minutes; without this the
    /// window looked hung.</summary>
    private readonly DispatcherTimer _videoTicker = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _estimateSeq;
    private HiggsfieldEstimate? _estimate;
    private string _estimateKey = "";
    private string _videoCostText = "Estimated clip cost: —";
    private string _enhanceCostText = "Estimated checked cost: $0.0000";

    /// <summary>public_url of a render already uploaded this session, keyed by path + size + mtime, so the
    /// same 4K PNG is not sent twice (once for an estimate, once for the clip).</summary>
    private readonly Dictionary<string, string> _uploadCache = new(StringComparer.OrdinalIgnoreCase);

    private HiggsfieldModel? SelectedVideoModel => VideoModelCombo.SelectedItem as HiggsfieldModel;

    private HiggsfieldCredentials CurrentVideoCredentials() =>
        new(VideoKeyIdBox.Text.Trim(), VideoSecretBox.Password.Trim());

    /// <summary>A folder path as typed or pasted — Explorer's "Copy as path" wraps it in quotes, which would
    /// otherwise make a perfectly good folder "not exist".</summary>
    private static string CleanPath(string text) => (text ?? "").Trim().Trim('"').Trim();

    // ---- setup ---------------------------------------------------------------

    private void InitVideoTab(AireSettings s)
    {
        _catalog = HiggsfieldCatalog.Load();
        // No DisplayMemberPath anywhere: the item classes render through ToString(), exactly like the Enhance
        // tab's string items, so every combo draws its text the same proven way in both themes.
        VideoModelCombo.ItemsSource = _catalog.Models;

        VideoModelCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressVideoEvents) return;
            RefreshVideoUiForModel();
            UpdateVideoSourceInfo();
            ScheduleEstimate();
        };
        foreach (var combo in new[] { VideoDurationCombo, VideoResolutionCombo, VideoAspectCombo, VideoMotion1Combo, VideoMotion2Combo })
        {
            var c = combo;
            c.SelectionChanged += (_, _) =>
            {
                if (_suppressVideoEvents) return;
                if (ReferenceEquals(c, VideoAspectCombo)) UpdateVideoSourceInfo();
                ScheduleEstimate();
            };
        }
        VideoMotion1Slider.ValueChanged += (_, _) =>
        {
            VideoMotion1Value.Text = VideoMotion1Slider.Value.ToString("0.00", CultureInfo.CurrentCulture);
            if (!_suppressVideoEvents) ScheduleEstimate();
        };
        VideoMotion2Slider.ValueChanged += (_, _) =>
        {
            VideoMotion2Value.Text = VideoMotion2Slider.Value.ToString("0.00", CultureInfo.CurrentCulture);
            if (!_suppressVideoEvents) ScheduleEstimate();
        };
        VideoPromptBox.TextChanged += (_, _) => { if (!_suppressVideoEvents) ScheduleEstimate(); };
        VideoKeyIdBox.TextChanged += (_, _) => { if (!_suppressVideoEvents) ScheduleEstimate(); };
        VideoSecretBox.PasswordChanged += (_, _) => { if (!_suppressVideoEvents) ScheduleEstimate(); };

        VideoOutputBrowseButton.Click += (_, _) => BrowseFolder(VideoOutputFolderBox, "Choose Video Output Folder");
        VideoBrowseImageButton.Click += (_, _) => BrowseVideoImage();
        UseCheckedRenderButton.Click += (_, _) => UseCheckedRender();
        VideoDropZone.DragEnter += OnQueueDrag;
        VideoDropZone.DragOver += OnQueueDrag;
        VideoDropZone.Drop += OnVideoDrop;

        VideoKeyPresetCombo.SelectionChanged += (_, _) => LoadSelectedVideoKey();
        SaveVideoKeyButton.Click += (_, _) => SaveCurrentVideoKey();
        DeleteVideoKeyButton.Click += (_, _) => DeleteSelectedVideoKey();
        VideoPromptPresetCombo.SelectionChanged += (_, _) => LoadSelectedVideoPrompt();
        SaveVideoPromptButton.Click += (_, _) => SaveCurrentVideoPrompt();
        DeleteVideoPromptButton.Click += (_, _) => DeleteSelectedVideoPrompt();
        PopOutVideoPromptButton.Click += (_, _) => PopOutVideoPrompt();

        VideoKeyHelpButton.Click += (_, _) => VideoKeyHelpOverlay.Visibility = System.Windows.Visibility.Visible;
        VideoKeyHelpCloseButton.Click += (_, _) => VideoKeyHelpOverlay.Visibility = System.Windows.Visibility.Collapsed;
        VideoKeyHelpOpenButton.Click += (_, _) => OpenUrl(HiggsfieldClient.CloudUrl);
        VideoCloudButton.Click += (_, _) => OpenUrl(HiggsfieldClient.CloudUrl);
        LoadPresetsButton.Click += (_, _) => _ = LoadPresetsAsync();

        RefreshEstimateButton.Click += (_, _) => { _estimateKey = ""; _estimateTimer.Stop(); _ = RunEstimateAsync(); };
        GenerateButton.Click += (_, _) => _ = GenerateClipAsync();
        VideoCancelButton.Click += (_, _) => CancelVideo();
        // Open the folder the last clip actually landed in, not whatever the box says now.
        VideoOpenFolderButton.Click += (_, _) => OpenFolder(_lastClip.Length > 0
            ? Path.GetDirectoryName(_lastClip) ?? CleanPath(VideoOutputFolderBox.Text)
            : CleanPath(VideoOutputFolderBox.Text));
        VideoPlayButton.Click += (_, _) => PlayLastClip();

        _estimateTimer.Tick += (_, _) => { _estimateTimer.Stop(); _ = RunEstimateAsync(); };
        _videoTicker.Tick += (_, _) => { if (_videoJob != null) RenderVideoJobState(_videoJob); else _videoTicker.Stop(); };

        // SelectionChanged bubbles up from every ComboBox inside the tabs; only the tab strip itself counts.
        AireTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, AireTabs)) UpdateHeroForTab(); };

        LoadVideoSettings(s);

        if (_catalog.Warning != null) VideoStatusLabel.Text = _catalog.Warning;
        else if (_catalog.Models.Count == 0) VideoStatusLabel.Text = "No video models are available — the model catalog is empty.";
    }

    private void LoadVideoSettings(AireSettings s)
    {
        _suppressVideoEvents = true;
        try
        {
            var creds = s.GetVideoCredentials();
            VideoKeyIdBox.Text = creds.KeyId;
            VideoSecretBox.Password = creds.Secret;
            VideoKeyNameBox.Text = s.SelectedVideoKeyName;
            // Defaults to the Enhance output folder: the clip belongs next to the render it came from.
            VideoOutputFolderBox.Text = s.VideoOutputFolder.Length > 0 ? s.VideoOutputFolder : s.OutputFolder;
            VideoPromptBox.Text = string.IsNullOrWhiteSpace(s.VideoPrompt) ? DefaultVideoPrompt : s.VideoPrompt;
            VideoPromptNameBox.Text = s.SelectedVideoPromptName;
            RefreshVideoPresetCombos(s, s.SelectedVideoPromptName, s.SelectedVideoKeyName);

            VideoModelCombo.SelectedItem = _catalog.FindModel(s.VideoModel) ?? _catalog.DefaultModel;
            RefreshVideoUiForModel(s.VideoDuration, s.VideoResolution, s.VideoAspectRatio, s.VideoMotion1, s.VideoMotion2);
            VideoMotion1Slider.Value = Math.Clamp(s.VideoMotion1Strength, 0, 1);
            VideoMotion2Slider.Value = Math.Clamp(s.VideoMotion2Strength, 0, 1);
            VideoMotion1Value.Text = VideoMotion1Slider.Value.ToString("0.00", CultureInfo.CurrentCulture);
            VideoMotion2Value.Text = VideoMotion2Slider.Value.ToString("0.00", CultureInfo.CurrentCulture);

            if (s.VideoSourceImage.Length > 0 && File.Exists(s.VideoSourceImage)) SetVideoSource(s.VideoSourceImage);
            else UpdateVideoSourceInfo();

            if (s.ActiveTab == "Video") AireTabs.SelectedItem = VideoTab;
        }
        finally { _suppressVideoEvents = false; }
        UpdateHeroForTab();
        ScheduleEstimate();
    }

    private void SaveVideoSettings(AireSettings s)
    {
        s.SetVideoCredentials(VideoKeyIdBox.Text.Trim(), VideoSecretBox.Password.Trim());
        s.SelectedVideoKeyName = VideoKeyPresetCombo.SelectedItem as string ?? "";
        s.SelectedVideoPromptName = VideoPromptPresetCombo.SelectedItem as string ?? "";
        s.VideoOutputFolder = CleanPath(VideoOutputFolderBox.Text);
        s.VideoPrompt = VideoPromptBox.Text;
        s.VideoSourceImage = _videoSource;
        s.VideoModel = SelectedVideoModel?.Path ?? "";
        s.VideoDuration = CurrentRaw(VideoDurationCombo);
        s.VideoResolution = CurrentRaw(VideoResolutionCombo);
        s.VideoAspectRatio = CurrentRaw(VideoAspectCombo);
        s.VideoMotion1 = CurrentPresetId(VideoMotion1Combo);
        s.VideoMotion2 = CurrentPresetId(VideoMotion2Combo);
        s.VideoMotion1Strength = VideoMotion1Slider.Value;
        s.VideoMotion2Strength = VideoMotion2Slider.Value;
        s.ActiveTab = ReferenceEquals(AireTabs.SelectedItem, VideoTab) ? "Video" : "Enhance";
    }

    private void UpdateHeroForTab()
    {
        if (AireTabs == null || SubtitleLabel == null || CostLabel == null) return;
        bool video = ReferenceEquals(AireTabs.SelectedItem, VideoTab);
        SubtitleLabel.Text = video ? VideoSubtitle : EnhanceSubtitle;
        CostLabel.Text = video ? _videoCostText : _enhanceCostText;
    }

    // ---- per-model controls ------------------------------------------------------

    private static string CurrentRaw(ComboBox combo) => (combo.SelectedItem as Choice)?.Raw ?? "";
    private static string CurrentPresetId(ComboBox combo) => (combo.SelectedItem as PresetChoice)?.Id ?? "";

    /// <summary>
    ///     Rebuilds the duration / resolution / aspect dropdowns and the camera-preset controls for the selected
    ///     model, from the catalog — never hard-coded. A parameter the model lacks shows as disabled with a word
    ///     of explanation; presets are disabled with a stated reason on every non-DoP model.
    /// </summary>
    private void RefreshVideoUiForModel(string? wantDuration = null, string? wantResolution = null,
        string? wantAspect = null, string? wantMotion1 = null, string? wantMotion2 = null)
    {
        var model = SelectedVideoModel;
        var was = _suppressVideoEvents;
        _suppressVideoEvents = true;
        try
        {
            // Short placeholders: the three combos share one row and a long one is clipped.
            FillChoiceCombo(VideoDurationCombo, model, "duration", wantDuration ?? CurrentRaw(VideoDurationCombo), "default");
            FillChoiceCombo(VideoResolutionCombo, model, "resolution", wantResolution ?? CurrentRaw(VideoResolutionCombo), "default");
            FillChoiceCombo(VideoAspectCombo, model, "aspect_ratio", wantAspect ?? CurrentRaw(VideoAspectCombo), "as image");

            bool motions = model?.SupportsMotions == true;
            var presets = new List<PresetChoice> { new() { Id = "", Name = NoPreset } };
            presets.AddRange(_catalog.MotionPresets.Select(p => new PresetChoice { Id = p.Id, Name = p.Name }));
            FillPresetCombo(VideoMotion1Combo, presets, wantMotion1 ?? CurrentPresetId(VideoMotion1Combo));
            FillPresetCombo(VideoMotion2Combo, presets, wantMotion2 ?? CurrentPresetId(VideoMotion2Combo));
            VideoMotion1Combo.IsEnabled = VideoMotion2Combo.IsEnabled = motions;
            VideoMotion1Slider.IsEnabled = VideoMotion2Slider.IsEnabled = motions;
            VideoMotion2Label.Visibility = VideoMotion2Combo.Visibility = VideoMotion2Slider.Visibility = VideoMotion2Value.Visibility =
                model != null && model.MaxMotions < 2 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            // One or two lines at most: every extra line here comes straight out of the prompt box below.
            VideoMotionNote.Text = model == null
                ? ""
                : !motions
                    ? $"Camera presets are for the Higgsfield DoP models only — {model.Label} has no motion parameter, "
                      + "so describe the move in the prompt."
                    : _catalog.MotionPresets.Count == 0
                        ? $"Up to {model.MaxMotions} camera presets — none loaded yet. Click Load Presets, or add motion_presets "
                          + "to the catalog override (path in the tooltip)."
                        : $"Up to {model.MaxMotions} camera presets, strength 0–1. Gentle moves at low strength keep walls straight.";
            VideoMotionNote.ToolTip = $"Catalog override file: {HiggsfieldCatalog.OverridePath}\n"
                                      + "Add motion_presets entries there as {{\"id\": \"<Higgsfield preset UUID>\", \"name\": \"Push in\"}}.";
        }
        finally { _suppressVideoEvents = was; }
    }

    private static void FillChoiceCombo(ComboBox combo, HiggsfieldModel? model, string param, string wantedRaw, string absentText)
    {
        var p = model?.Param(param);
        List<Choice> items;
        if (p == null)
        {
            items = new List<Choice> { new() { Raw = "", Display = absentText } };
            combo.IsEnabled = false;
        }
        else
        {
            var values = p.Enum;
            // Seedance declares duration as an integer range (2–12 s) rather than an enum.
            if (values == null && p.Type == "integer" && p.Minimum.HasValue && p.Maximum.HasValue
                && p.Maximum.Value - p.Minimum.Value <= 60)
                values = Enumerable.Range((int)p.Minimum.Value, (int)(p.Maximum.Value - p.Minimum.Value) + 1)
                    .Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
            if (values == null || values.Count == 0)
            {
                items = new List<Choice> { new() { Raw = "", Display = "default" } };
                combo.IsEnabled = false;
            }
            else
            {
                items = values.Select(v => new Choice { Raw = v, Display = HiggsfieldCatalog.Display(param, v) }).ToList();
                combo.IsEnabled = true;
            }
        }
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(i => i.Raw == wantedRaw && i.Raw.Length > 0)
                             ?? items.FirstOrDefault(i => p?.Default != null && i.Raw == p.Default)
                             ?? items[0];
    }

    private static void FillPresetCombo(ComboBox combo, List<PresetChoice> presets, string wantedId)
    {
        combo.ItemsSource = presets;
        combo.SelectedItem = presets.FirstOrDefault(p => p.Id.Length > 0 && string.Equals(p.Id, wantedId, StringComparison.OrdinalIgnoreCase))
                             ?? presets[0];
    }

    // ---- source image ----------------------------------------------------------

    private void BrowseVideoImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose the render to animate",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|All files (*.*)|*.*",
        };
        var start = VideoOutputFolderBox.Text.Trim();
        if (start.Length > 0 && Directory.Exists(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) == true) SetVideoSource(dlg.FileName, announce: true);
    }

    /// <summary>The intended chain: the ticked render on the Enhance tab, preferring its _enhanced.png output.</summary>
    private void UseCheckedRender()
    {
        var checkedItems = CheckedItems();
        if (checkedItems.Count == 0)
        {
            MessageBox.Show(this, "Tick a render on the Enhance tab first — this button takes the checked image "
                                  + "(or its enhanced output, if it has one).", "Nothing checked",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var item = checkedItems[0];
        var enhanced = Path.Combine(OutputFolderBox.Text.Trim(),
            Path.GetFileNameWithoutExtension(item.FullPath) + "_enhanced." + AireEngine.OutputFormat);
        var more = checkedItems.Count > 1 ? $" (first of {checkedItems.Count} checked)" : "";
        if (File.Exists(enhanced))
        {
            SetVideoSource(enhanced);
            VideoStatusLabel.Text = $"Using the enhanced output {Path.GetFileName(enhanced)}{more}.";
        }
        else
        {
            SetVideoSource(item.FullPath);
            VideoStatusLabel.Text = $"Using {item.FileName}{more} — it has no _enhanced output in the Enhance output folder yet.";
        }
    }

    private void OnVideoDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // Unlike the Enhance queue, an _enhanced.png is exactly what belongs here.
        var file = paths.FirstOrDefault(p => File.Exists(p)
                                             && AireEngine.SupportedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        if (file != null) SetVideoSource(file, announce: true);
        else VideoStatusLabel.Text = "Drop one image file (.png, .jpg, .jpeg or .webp).";
        e.Handled = true;
    }

    private void SetVideoSource(string path, bool announce = false)
    {
        _videoSource = path;
        var (_, w, h) = AireEngine.EstimateImageTokensFromFile(path);
        _videoSourceW = w ?? 0;
        _videoSourceH = h ?? 0;
        try
        {
            // Decode a thumbnail, not the 4K bitmap — the full image is only ever read as bytes for upload.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 800;
            bmp.EndInit();
            bmp.Freeze();
            VideoThumbnail.Source = bmp;
        }
        catch { VideoThumbnail.Source = null; }
        UpdateVideoSourceInfo();
        if (announce) VideoStatusLabel.Text = $"Source: {Path.GetFileName(path)}.";
        ScheduleEstimate();
    }

    /// <summary>Filename + [W×H] + ratio, and the aspect warning when the chosen model cannot make that ratio.</summary>
    private void UpdateVideoSourceInfo()
    {
        if (VideoSourceLabel == null) return;
        if (_videoSource.Length == 0)
        {
            VideoThumbnail.Source = null;
            VideoDropHint.Visibility = System.Windows.Visibility.Visible;
            VideoSourceLabel.Text = "No render selected.";
            VideoAspectWarning.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }
        VideoDropHint.Visibility = System.Windows.Visibility.Collapsed;
        var aspect = HiggsfieldCatalog.AspectLabel(_videoSourceW, _videoSourceH);
        var size = _videoSourceW > 0 ? $"[{_videoSourceW}x{_videoSourceH}]" : "[unknown]";
        VideoSourceLabel.Text = $"{Path.GetFileName(_videoSource)}    {size}    {aspect}";

        string? warning = null;
        var model = SelectedVideoModel;
        var allowed = model?.AllowedValues("aspect_ratio");
        if (model != null && allowed is { Count: > 0 } && _videoSourceW > 0)
        {
            var chosen = CurrentRaw(VideoAspectCombo);
            var fit = allowed.FirstOrDefault(a => HiggsfieldCatalog.RatioMatches(_videoSourceW, _videoSourceH, a));
            if (fit == null)
                warning = $"This render is {aspect}, but {model.Label} only makes {string.Join(" or ", allowed)}. Higgsfield will "
                          + $"crop or pad it to {chosen} silently — crop it yourself first if the framing matters.";
            else if (chosen.Length > 0 && !HiggsfieldCatalog.RatioMatches(_videoSourceW, _videoSourceH, chosen))
                warning = $"This render is {aspect} but {chosen} is selected, so Higgsfield will crop or pad it. "
                          + $"Choose {fit} to keep the framing.";
        }
        VideoAspectWarning.Text = warning ?? "";
        VideoAspectWarning.Visibility = warning == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    // ---- cost estimate -----------------------------------------------------------

    private void ScheduleEstimate()
    {
        if (_estimateTimer == null) return;
        _estimateTimer.Stop();
        _estimateTimer.Start();
    }

    private Dictionary<string, string> ChosenParameters()
    {
        var chosen = new Dictionary<string, string>();
        void Take(ComboBox combo, string name)
        {
            var raw = CurrentRaw(combo);
            if (combo.IsEnabled && raw.Length > 0) chosen[name] = raw;
        }
        Take(VideoDurationCombo, "duration");
        Take(VideoResolutionCombo, "resolution");
        Take(VideoAspectCombo, "aspect_ratio");
        return chosen;
    }

    private List<HiggsfieldMotion> ChosenMotions()
    {
        var list = new List<HiggsfieldMotion>();
        if (SelectedVideoModel?.SupportsMotions != true) return list;
        void Take(ComboBox combo, Slider slider)
        {
            if (combo.SelectedItem is PresetChoice { Id.Length: > 0 } p)
                list.Add(new HiggsfieldMotion { Id = p.Id, Name = p.Name, Strength = Math.Round(slider.Value, 2) });
        }
        Take(VideoMotion1Combo, VideoMotion1Slider);
        if (VideoMotion2Combo.Visibility == System.Windows.Visibility.Visible) Take(VideoMotion2Combo, VideoMotion2Slider);
        return list;
    }

    /// <summary>Everything the request body depends on, plus the credentials — the estimate is keyed by this.</summary>
    private string EstimateKey(HiggsfieldCredentials creds, HiggsfieldModel model) =>
        string.Join("|", creds.KeyId, creds.Secret.Length, model.Path, VideoPromptBox.Text.Trim(),
            string.Join(",", ChosenParameters().Select(kv => kv.Key + "=" + kv.Value)),
            string.Join(",", ChosenMotions().Select(m => m.Id + "@" + m.Strength.ToString(CultureInfo.InvariantCulture))));

    private void SetVideoCost(string value, string? note)
    {
        _videoCostText = "Estimated clip cost: " + value;
        VideoCostLabel.Text = _videoCostText;
        VideoCostNote.Text = note ?? "";
        VideoCostNote.Visibility = string.IsNullOrEmpty(note) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        UpdateHeroForTab();
    }

    private void ShowEstimate(HiggsfieldEstimate est) =>
        SetVideoCost($"${est.UsdText}  ({est.CreditsText} credits)", "From Higgsfield, for exactly this request. Refreshes when anything changes.");

    private async Task RunEstimateAsync()
    {
        var model = SelectedVideoModel;
        if (model == null) { SetVideoCost("—", "Pick a model to see the cost."); return; }
        var creds = CurrentVideoCredentials();
        if (!creds.IsComplete) { SetVideoCost("—", "Enter the Higgsfield Key ID and Secret to see the real cost."); return; }
        if (VideoPromptBox.Text.Trim().Length == 0) { SetVideoCost("—", "Write a motion prompt to see the cost."); return; }

        var key = EstimateKey(creds, model);
        if (_estimate != null && key == _estimateKey) { ShowEstimate(_estimate); return; }

        int seq = ++_estimateSeq;
        SetVideoCost("estimating…", "Asking Higgsfield…");
        try
        {
            var est = await FetchEstimateAsync(creds, model).ConfigureAwait(true);
            if (seq != _estimateSeq) return; // a newer edit superseded this answer
            _estimate = est;
            _estimateKey = key;
            ShowEstimate(est);
        }
        catch (Exception ex) when (seq == _estimateSeq)
        {
            _estimate = null;
            _estimateKey = "";
            SetVideoCost("—", ex.Message);
        }
        catch { /* superseded */ }
    }

    /// <summary>
    ///     The vendor's estimate for the request as currently configured. Uses a placeholder image URL, since
    ///     the price does not depend on the pixels; if Higgsfield ever rejects that with a validation error
    ///     naming image_url, the real render is uploaded (free) and the estimate retried — which also proves the
    ///     upload path before any money is involved.
    /// </summary>
    private async Task<HiggsfieldEstimate> FetchEstimateAsync(HiggsfieldCredentials creds, HiggsfieldModel model)
    {
        var prompt = VideoPromptBox.Text.Trim();
        var chosen = ChosenParameters();
        var motions = ChosenMotions();
        var cached = CachedUploadUrl();
        var url = cached ?? EstimatePlaceholderUrl;
        try
        {
            var body = model.BuildBody(prompt, url, chosen, motions);
            return await Task.Run(() => HiggsfieldClient.EstimateAsync(creds, model.Path, body, CancellationToken.None))
                .ConfigureAwait(true);
        }
        catch (HiggsfieldApiException ex) when (ex.Kind == HiggsfieldErrorKind.Validation && cached == null
                                                && ex.Detail.Contains("image_url", StringComparison.OrdinalIgnoreCase)
                                                && _videoSource.Length > 0 && File.Exists(_videoSource))
        {
            var real = await UploadSourceAsync(creds).ConfigureAwait(true);
            var body = model.BuildBody(prompt, real, chosen, motions);
            return await Task.Run(() => HiggsfieldClient.EstimateAsync(creds, model.Path, body, CancellationToken.None))
                .ConfigureAwait(true);
        }
    }

    private string UploadCacheKey()
    {
        try
        {
            var info = new FileInfo(_videoSource);
            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return _videoSource; }
    }

    private string? CachedUploadUrl() =>
        _videoSource.Length > 0 && _uploadCache.TryGetValue(UploadCacheKey(), out var url) ? url : null;

    private async Task<string> UploadSourceAsync(HiggsfieldCredentials creds)
    {
        var cached = CachedUploadUrl();
        if (cached != null) return cached;
        var path = _videoSource;
        VideoStatusLabel.Text = $"Uploading {Path.GetFileName(path)} to Higgsfield (free)…";
        var url = await Task.Run(() => HiggsfieldClient.UploadFileAsync(creds, path, CancellationToken.None)).ConfigureAwait(true);
        _uploadCache[UploadCacheKey()] = url;
        VideoStatusLabel.Text = $"Uploaded {Path.GetFileName(path)}.";
        return url;
    }

    // ---- generate / cancel ------------------------------------------------------

    private async Task GenerateClipAsync()
    {
        try
        {
            var creds = CurrentVideoCredentials();
            var model = SelectedVideoModel;
            var outputFolder = CleanPath(VideoOutputFolderBox.Text);
            var prompt = VideoPromptBox.Text.Trim();

            if (!creds.IsComplete)
            {
                MessageBox.Show(this, "Paste your Higgsfield Key ID and Secret first (both come from cloud.higgsfield.ai).",
                    "Missing credentials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (model == null)
            {
                MessageBox.Show(this, "Pick a model.", "No model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_videoSource.Length == 0 || !File.Exists(_videoSource))
            {
                MessageBox.Show(this, "Choose the render to animate — browse, drop an image, or take the checked render from the Enhance tab.",
                    "No source render", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (outputFolder.Length == 0)
            {
                MessageBox.Show(this, "Please choose an output folder.", "Missing output folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (prompt.Length == 0)
            {
                MessageBox.Show(this, "Write a short motion prompt — what should move, and how gently.", "Empty prompt",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                VideoPromptBox.Focus();
                return;
            }

            // Say "busy" in words BEFORE the cost dialog, not after the user has agreed to spend.
            var busy = AireJobManager.BusyReason();
            if (busy != null)
            {
                MessageBox.Show(this, busy, "AIRE busy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The founding rule: nothing spends without a confirmation that shows the real cost. No estimate,
            // no dialog — and no clip.
            GenerateButton.IsEnabled = false;
            HiggsfieldEstimate est;
            try
            {
                var key = EstimateKey(creds, model);
                if (_estimate != null && key == _estimateKey) est = _estimate;
                else
                {
                    VideoStatusLabel.Text = "Getting the exact cost from Higgsfield…";
                    est = await FetchEstimateAsync(creds, model).ConfigureAwait(true);
                    _estimate = est;
                    _estimateKey = key;
                    ShowEstimate(est);
                }
            }
            catch (Exception ex)
            {
                VideoStatusLabel.Text = "Generate is blocked until Higgsfield returns a cost for this request.";
                SetVideoCost("—", ex.Message);
                MessageBox.Show(this,
                    "Generate is blocked until Higgsfield returns a cost for this request.\n\n" + ex.Message,
                    "No cost estimate", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally { GenerateButton.IsEnabled = true; }

            var request = new AireVideoRequest
            {
                SourceImage = _videoSource,
                OutputFolder = outputFolder,
                Prompt = prompt,
                Model = model,
                Parameters = ChosenParameters(),
                Motions = ChosenMotions(),
                CachedPublicUrl = CachedUploadUrl(),
                EstimatedCreditsText = est.CreditsText,
                EstimatedUsdText = est.UsdText,
            };

            string Line(string name, string absent) =>
                request.Parameters.TryGetValue(name, out var v) ? HiggsfieldCatalog.Display(name, v) : absent;
            var camera = request.Motions.Count == 0
                ? (model.SupportsMotions ? "none (prompt only)" : "n/a for this model")
                : string.Join(" + ", request.Motions.Select(m => $"{m.Name} @ {m.Strength.ToString("0.00", CultureInfo.CurrentCulture)}"));
            var size = _videoSourceW > 0 ? $"[{_videoSourceW}x{_videoSourceH}]" : "";

            var answer = MessageBox.Show(this,
                "You are about to generate one clip.\n\n"
                + $"Model: {model.Label}\n"
                + $"Duration: {Line("duration", "model default")}\n"
                + $"Resolution: {Line("resolution", "model default (1080p max)")}\n"
                + $"Aspect: {Line("aspect_ratio", "follows the image")}\n"
                + $"Camera: {camera}\n"
                + $"Source: {Path.GetFileName(_videoSource)} {size}\n"
                + $"Saves to: {outputFolder}\n\n"
                + $"Estimated cost: ${est.UsdText}  ({est.CreditsText} credits)\n\n"
                + "Continue?",
                "Confirm Higgsfield usage", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            SaveSettings(); // remember everything (incl. the pair) the moment the user commits to a run

            var job = AireJobManager.StartVideo(request, creds, out var error);
            if (job == null)
            {
                MessageBox.Show(this, error, "AIRE busy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            VideoResultPanel.Visibility = System.Windows.Visibility.Collapsed;
            VideoProgressBar.Value = 0;
            AttachVideo(job);
        }
        catch (Exception ex)
        {
            GenerateButton.IsEnabled = true;
            VideoStatusLabel.Text = "Could not start: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Could not start the clip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    ///     Cancel is a QUEUE operation at Higgsfield, not an abort — the button is live only while the job is
    ///     uploading (nothing sent yet) or queued (the vendor refunds it). Once generation starts it is disabled
    ///     with the reason in its tooltip rather than left to look like it failed.
    /// </summary>
    private void CancelVideo()
    {
        var job = _videoJob;
        if (job == null || !job.CanCancel) return;
        var queued = job.Status == "queued";
        job.Cancel();
        VideoCancelButton.IsEnabled = false;
        VideoStatusLabel.Text = queued
            ? "Cancelling — asking Higgsfield to cancel the queued request (it is refunded if it had not started)…"
            : "Cancelling — nothing has been sent to Higgsfield yet.";
    }

    /// <summary>Subscribes the window to a clip job (freshly started here, or one already running when the window reopened).</summary>
    private void AttachVideo(AireVideoJob job)
    {
        _videoJob = job;
        SetVideoBusy(true);
        RenderVideoJobState(job);
        job.Progress += OnVideoJobProgress;
        _videoTicker.Start();
    }

    private void OnVideoJobProgress(AireJobBase job) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (job is not AireVideoJob video || !ReferenceEquals(video, _videoJob)) return;
            RenderVideoJobState(video);
            if (video.IsFinished)
            {
                _videoTicker.Stop();
                video.Progress -= OnVideoJobProgress;
                _videoJob = null;
                SetVideoBusy(false);
                ShowVideoSummary(video);
            }
        });

    private void RenderVideoJobState(AireVideoJob job)
    {
        VideoProgressBar.Value = job.Status switch
        {
            "uploading" => 12,
            "queued" => 30,
            "in_progress" => 62,
            "downloading" => 90,
            "completed" => 100,
            _ => VideoProgressBar.Value,
        };
        VideoCancelButton.IsEnabled = job.CanCancel;
        VideoCancelButton.ToolTip = job.IsFinished ? null
            : job.CanCancel ? "Stop now — nothing has been charged yet."
            : job.CancelRequested ? "Cancel requested…"
            : "Higgsfield can only cancel a request that has not started generating. This clip has started, "
              + "so it will finish and be charged.";
        if (job.IsFinished) return;

        var name = Path.GetFileName(job.Request.SourceImage);
        var elapsed = AireEngine.SecondsToText((DateTime.UtcNow - job.StartedUtc).TotalSeconds);
        var checkedAgo = job.LastPollUtc.HasValue
            ? $"last checked {(DateTime.UtcNow - job.LastPollUtc.Value).TotalSeconds:0} s ago"
            : "first check shortly";
        VideoStatusLabel.Text = job.CancelRequested
            ? $"Cancelling… ({elapsed})"
            : job.Status switch
            {
                "uploading" => $"Uploading {name} to Higgsfield… {elapsed} elapsed. Nothing charged yet.",
                "queued" => $"Queued at Higgsfield (request {Short(job.RequestId)}) — {elapsed} elapsed, {checkedAgo}. "
                            + "Cancel is available until generation starts.",
                "in_progress" => $"Generating — {elapsed} elapsed, {checkedAgo}. Higgsfield has started, so this clip can no longer "
                                 + "be cancelled and will be charged. A 10 s clip on a Pro or Master model can take 5–10 minutes; "
                                 + "the window is still polling.",
                "downloading" => $"Finished after {elapsed} — downloading the clip…",
                _ => job.Status,
            };
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] + "…" : id;

    private void SetVideoBusy(bool busy)
    {
        GenerateButton.IsEnabled = !busy;
        VideoBrowseImageButton.IsEnabled = !busy;
        UseCheckedRenderButton.IsEnabled = !busy;
        RefreshEstimateButton.IsEnabled = !busy;
        if (!busy) VideoCancelButton.IsEnabled = false;
    }

    private void ShowVideoSummary(AireVideoJob job)
    {
        var elapsed = AireEngine.SecondsToText(job.TotalTimeSeconds);
        var log = job.LogFile.Length > 0 ? $"\n\nLog saved to:\n{job.LogFile}" : "";
        switch (job.Status)
        {
            case "completed":
                _lastClip = job.OutputFile;
                VideoResultLabel.Text = $"{Path.GetFileName(job.OutputFile)}\n{job.OutputFile}\n"
                                        + $"Elapsed {elapsed}   ·   Cost ${job.CostUsdText}  ({job.CostCreditsText} credits, as estimated)"
                                        + (job.Note.Length > 0 ? "\n" + job.Note : "");
                VideoResultPanel.Visibility = System.Windows.Visibility.Visible;
                VideoStatusLabel.Text = "Clip saved." + (job.LogFile.Length > 0 ? $"  Log: {job.LogFile}" : "");
                MessageBox.Show(this, "Clip complete.\n\n" + VideoResultLabel.Text + log, "Clip complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case "canceled":
                VideoStatusLabel.Text = "Cancelled. " + job.Error;
                MessageBox.Show(this, job.Error + log, "Clip cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case "nsfw":
                VideoStatusLabel.Text = job.Error;
                MessageBox.Show(this, job.Error + log, "Rejected by the content filter", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            default:
                VideoStatusLabel.Text = "Clip failed: " + job.Error;
                MessageBox.Show(this, "The clip did not complete.\n\n" + job.Error + log, "Clip failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    private void PlayLastClip()
    {
        if (_lastClip.Length == 0 || !File.Exists(_lastClip))
        {
            MessageBox.Show(this, "No clip from this session to play yet.", "Nothing to play",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Hand off to whatever plays MP4 on this machine — no embedded player.
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastClip) { UseShellExecute = true }); }
        catch (Exception ex) { VideoStatusLabel.Text = "Could not open the clip: " + ex.Message; }
    }

    private void OpenFolder(string folder)
    {
        folder = CleanPath(folder);
        if (folder.Length == 0)
        {
            MessageBox.Show(this, "Please choose an output folder.", "Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(folder))
        {
            // The job creates it at Generate time; before that, offer to, rather than just saying no.
            if (MessageBox.Show(this, $"The folder does not exist yet:\n{folder}\n\nCreate it now?", "Folder not found",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not create the folder:\n{ex.Message}", "Folder", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { VideoStatusLabel.Text = "Could not open the folder: " + ex.Message; }
    }

    // ---- presets: camera list ------------------------------------------------------

    private async Task LoadPresetsAsync()
    {
        var creds = CurrentVideoCredentials();
        if (!creds.IsComplete)
        {
            MessageBox.Show(this, "Enter the Higgsfield Key ID and Secret first — the preset list is read from your account.",
                "Credentials needed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LoadPresetsButton.IsEnabled = false;
        VideoStatusLabel.Text = "Asking Higgsfield for the camera preset list…";
        try
        {
            var presets = await Task.Run(() => HiggsfieldClient.GetMotionPresetsAsync(creds, CancellationToken.None)).ConfigureAwait(true);
            if (presets.Count == 0)
            {
                VideoStatusLabel.Text = "Higgsfield returned an empty preset list.";
                return;
            }
            HiggsfieldCatalog.SaveMotionsCache(presets);
            var known = new HashSet<string>(_catalog.MotionPresets.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var p in presets) if (known.Add(p.Id)) _catalog.MotionPresets.Add(p);
            _catalog.MotionPresets = _catalog.MotionPresets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RefreshVideoUiForModel();
            VideoStatusLabel.Text = $"Loaded {presets.Count} camera presets from Higgsfield (cached in {HiggsfieldCatalog.MotionsCachePath}).";
        }
        catch (Exception ex)
        {
            VideoStatusLabel.Text = "Could not load presets: " + ex.Message
                                    + $"  You can add them by hand as motion_presets in {HiggsfieldCatalog.OverridePath}.";
        }
        finally { LoadPresetsButton.IsEnabled = true; }
    }

    // ---- presets: saved keys and prompts ------------------------------------------

    private void RefreshVideoPresetCombos(AireSettings s, string? promptName, string? keyName)
    {
        var was = _suppressVideoEvents;
        _suppressVideoEvents = true;
        try
        {
            var prompts = s.SavedVideoPrompts.Select(p => p.Name).ToList();
            VideoPromptPresetCombo.ItemsSource = prompts;
            VideoPromptPresetCombo.SelectedItem = MatchName(prompts, promptName);

            var keys = s.SavedApiKeysFor(AireSettings.ProviderHiggsfield).Select(k => k.Name).ToList();
            VideoKeyPresetCombo.ItemsSource = keys;
            VideoKeyPresetCombo.SelectedItem = MatchName(keys, keyName);
        }
        finally { _suppressVideoEvents = was; }
    }

    /// <summary>Switches to a saved pair. Takes effect in the stored settings immediately, for the same reason
    /// the OpenAI dropdown does: the stored pair is what any future bridge tool would read.</summary>
    private void LoadSelectedVideoKey()
    {
        if (_suppressVideoEvents) return;
        if (VideoKeyPresetCombo.SelectedItem is not string name) return;

        var s = AireSettings.Load();
        var pair = s.GetSavedKeyPair(name);
        if (!pair.IsComplete)
        {
            MessageBox.Show(this,
                $"The saved key \"{name}\" could not be read.\n\nSaved keys are encrypted for one Windows user account, "
                + "so a pair saved under a different profile (or a copied settings file) cannot be decrypted here. "
                + "Paste the Key ID and Secret again and save them.",
                "Key unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _suppressVideoEvents = true;
        try
        {
            VideoKeyIdBox.Text = pair.KeyId;
            VideoSecretBox.Password = pair.Secret;
            VideoKeyNameBox.Text = s.FindApiKey(name, AireSettings.ProviderHiggsfield)?.Name ?? name;
        }
        finally { _suppressVideoEvents = false; }
        s.SetVideoCredentials(pair.KeyId, pair.Secret);
        s.SelectedVideoKeyName = name;
        s.Save();
        VideoStatusLabel.Text = $"Now using Higgsfield key \"{name}\".";
        _estimateKey = "";
        ScheduleEstimate();
    }

    private void SaveCurrentVideoKey()
    {
        var name = VideoKeyNameBox.Text.Trim();
        var creds = CurrentVideoCredentials();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Type a name for this account first, e.g. Studio or Personal.",
                "Name the key", MessageBoxButton.OK, MessageBoxImage.Warning);
            VideoKeyNameBox.Focus();
            return;
        }
        if (!creds.IsComplete)
        {
            MessageBox.Show(this, "Paste both the Key ID and the Secret before saving them.", "Incomplete key",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            (creds.KeyId.Length == 0 ? (Control)VideoKeyIdBox : VideoSecretBox).Focus();
            return;
        }

        var s = AireSettings.Load();
        if (s.FindApiKey(name, AireSettings.ProviderHiggsfield) != null && MessageBox.Show(this,
                $"A saved Higgsfield key called \"{name}\" already exists.\n\nReplace it?",
                "Replace saved key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        s.UpsertKeyPair(name, creds.KeyId, creds.Secret);
        s.SetVideoCredentials(creds.KeyId, creds.Secret); // the pair just saved is also the one now in use
        s.SelectedVideoKeyName = name;
        s.Save();
        RefreshVideoPresetCombos(s, VideoPromptPresetCombo.SelectedItem as string, name);
        VideoStatusLabel.Text = $"Saved Higgsfield key \"{name}\" (encrypted for this Windows account).";
    }

    private void DeleteSelectedVideoKey()
    {
        if (VideoKeyPresetCombo.SelectedItem is not string name)
        {
            MessageBox.Show(this, "Pick a saved key in the dropdown first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
                $"Delete the saved Higgsfield key \"{name}\"?\n\nThis only forgets it here — the key itself stays valid on "
                + "your Higgsfield account, and the values currently in the boxes are left as they are.",
                "Delete saved key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var s = AireSettings.Load();
        s.RemoveApiKey(name, AireSettings.ProviderHiggsfield);
        s.SelectedVideoKeyName = "";
        s.Save();
        RefreshVideoPresetCombos(s, VideoPromptPresetCombo.SelectedItem as string, null);
        VideoStatusLabel.Text = $"Deleted saved key \"{name}\".";
    }

    private void LoadSelectedVideoPrompt()
    {
        if (_suppressVideoEvents) return;
        if (VideoPromptPresetCombo.SelectedItem is not string name) return;
        var entry = AireSettings.Load().FindVideoPrompt(name);
        if (entry == null) return;
        VideoPromptBox.Text = entry.Text;
        VideoPromptNameBox.Text = entry.Name;
        VideoStatusLabel.Text = $"Loaded saved motion prompt \"{entry.Name}\".";
    }

    private void SaveCurrentVideoPrompt()
    {
        var name = VideoPromptNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Type a name for this prompt first — that is what the dropdown will show.",
                "Name the prompt", MessageBoxButton.OK, MessageBoxImage.Warning);
            VideoPromptNameBox.Focus();
            return;
        }
        var s = AireSettings.Load();
        if (s.FindVideoPrompt(name) != null && MessageBox.Show(this,
                $"A saved motion prompt called \"{name}\" already exists.\n\nReplace it?",
                "Replace saved prompt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        s.UpsertVideoPrompt(name, VideoPromptBox.Text);
        s.SelectedVideoPromptName = name;
        s.Save();
        RefreshVideoPresetCombos(s, name, VideoKeyPresetCombo.SelectedItem as string);
        VideoStatusLabel.Text = $"Saved motion prompt \"{name}\".";
    }

    private void DeleteSelectedVideoPrompt()
    {
        if (VideoPromptPresetCombo.SelectedItem is not string name)
        {
            MessageBox.Show(this, "Pick a saved prompt in the dropdown first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Delete the saved motion prompt \"{name}\"?\n\nThe text in the box is kept.",
                "Delete saved prompt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var s = AireSettings.Load();
        s.RemoveVideoPrompt(name);
        s.SelectedVideoPromptName = "";
        s.Save();
        RefreshVideoPresetCombos(s, null, VideoKeyPresetCombo.SelectedItem as string);
        VideoStatusLabel.Text = $"Deleted saved motion prompt \"{name}\".";
    }

    private void PopOutVideoPrompt()
    {
        if (_videoPromptEditor != null)
        {
            if (_videoPromptEditor.WindowState == System.Windows.WindowState.Minimized)
                _videoPromptEditor.WindowState = System.Windows.WindowState.Normal;
            _videoPromptEditor.Activate();
            return;
        }
        var editor = new PromptEditorWindow(this, VideoPromptBox) { Title = "Motion prompt — AIRE Video" };
        editor.Closed += (_, _) => _videoPromptEditor = null;
        _videoPromptEditor = editor;
        editor.Show();
    }
}

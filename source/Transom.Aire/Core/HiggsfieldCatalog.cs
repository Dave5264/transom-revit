using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transom.Core;

/// <summary>One request parameter of a Higgsfield model, as declared in its OpenAPI schema.</summary>
public sealed class HiggsfieldParam
{
    public string Name = "";
    public string Title = "";
    /// <summary>JSON kind: integer | number | string | boolean | array | object. Decides how a chosen value
    /// is serialised — Kling wants duration as the number 5, Veo wants the string "6".</summary>
    public string Type = "string";
    public bool Required;
    /// <summary>Allowed values as strings (the raw API value), or null when the parameter is free-form.</summary>
    public List<string>? Enum;
    public string? Default;
    public double? Minimum;
    public double? Maximum;

    /// <summary>The raw API value turned into a JSON node of the declared kind.</summary>
    public JsonNode? ToNode(string raw)
    {
        raw = (raw ?? "").Trim();
        switch (Type)
        {
            case "integer":
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? JsonValue.Create(i) : null;
            case "number":
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null;
            case "boolean":
                return bool.TryParse(raw, out var b) ? JsonValue.Create(b) : null;
            default:
                return JsonValue.Create(raw);
        }
    }
}

/// <summary>A camera-motion choice for the DoP models: preset id + 0–1 strength (+ name for the log).</summary>
public sealed class HiggsfieldMotion
{
    public string Id = "";
    public string Name = "";
    public double Strength = 0.6;
}

/// <summary>One image-to-video endpoint from the catalog, with everything the tab needs to shape a request.</summary>
public sealed class HiggsfieldModel
{
    public string Path = "";
    public string Label = "";
    public string Family = "";
    public string ImageParam = "image_url";
    public bool SupportsMotions;
    public int MaxMotions;
    public List<HiggsfieldParam> Params = new();

    public HiggsfieldParam? Param(string name) =>
        Params.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Has(string name) => Param(name) != null;

    /// <summary>Allowed values for an enum parameter, or null when the model lacks it or it is free-form.</summary>
    public List<string>? AllowedValues(string name) => Param(name)?.Enum;

    public override string ToString() => Label;

    /// <summary>
    ///     Builds the request body as a parameter bag: prompt + the image + only those catalog parameters the
    ///     caller chose (anything else is left to the vendor's default) + motions when the model takes them.
    ///     Values are serialised in the JSON kind the spec declares. Throws when a chosen value is not one the
    ///     model allows — that is the tab's bug, not something to send and let the vendor reject.
    /// </summary>
    public JsonObject BuildBody(string prompt, string imageUrl, IReadOnlyDictionary<string, string> chosen,
        IReadOnlyList<HiggsfieldMotion> motions)
    {
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            [ImageParam] = imageUrl,
        };
        foreach (var (name, raw) in chosen)
        {
            var p = Param(name);
            if (p == null || string.IsNullOrWhiteSpace(raw)) continue;
            if (p.Enum != null && !p.Enum.Contains(raw))
                throw new InvalidOperationException($"{Label} does not offer {name} = {raw} (allowed: {string.Join(", ", p.Enum)}).");
            var node = p.ToNode(raw)
                       ?? throw new InvalidOperationException($"{name} = {raw} is not a valid {p.Type} for {Label}.");
            body[name] = node;
        }
        if (SupportsMotions && motions.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var m in motions.Take(Math.Max(1, MaxMotions)))
                arr.Add(new JsonObject
                {
                    ["id"] = m.Id,
                    ["strength"] = Math.Round(Math.Clamp(m.Strength, 0, 1), 2),
                });
            body["motions"] = arr;
        }
        return body;
    }
}

/// <summary>
///     The Video tab's model catalog. Generated from Higgsfield's OpenAPI spec (Catalog/generate_catalog.py)
///     and embedded in Transom.Aire.dll, so the dropdown is never empty; a copy at
///     %AppData%\Transom\higgsfield-models.json overrides it, which is how the user adds motion preset ids
///     and names (not published anywhere machine-readable) or a model the vendor shipped after this build.
///     A malformed override is reported and the built-in catalog used — never a crash, never an empty list.
/// </summary>
public sealed class HiggsfieldCatalog
{
    public const string EmbeddedResourceName = "Transom.Aire.higgsfield-models.json";

    public string GeneratedOn = "";
    public string DefaultModelPath = "";
    public List<HiggsfieldModel> Models = new();
    public List<HiggsfieldMotionPreset> MotionPresets = new();

    /// <summary>"built-in" or the override file's path.</summary>
    public string Source = "built-in";

    /// <summary>Set when the override file existed but could not be used (and the built-in catalog was loaded instead).</summary>
    public string? Warning;

    public static string OverridePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transom", "higgsfield-models.json");

    /// <summary>Where a live "Load Presets" fetch is cached, so presets survive a restart without a second call.</summary>
    public static string MotionsCachePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transom", "higgsfield-motions.json");

    public HiggsfieldModel? FindModel(string path) =>
        Models.FirstOrDefault(m => string.Equals(m.Path, (path ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    public HiggsfieldModel? DefaultModel => FindModel(DefaultModelPath) ?? Models.FirstOrDefault();

    public static HiggsfieldCatalog Load()
    {
        HiggsfieldCatalog catalog;
        string? warning = null;
        var overridePath = OverridePath;
        if (File.Exists(overridePath))
        {
            try
            {
                catalog = Parse(File.ReadAllText(overridePath));
                if (catalog.Models.Count == 0) throw new InvalidDataException("it lists no models");
                catalog.Source = overridePath;
                MergeCachedMotions(catalog);
                return catalog;
            }
            catch (Exception ex)
            {
                warning = $"The catalog override at {overridePath} could not be used ({ex.Message}); "
                          + "the built-in catalog is in use instead.";
            }
        }

        try
        {
            catalog = Parse(ReadEmbedded());
        }
        catch (Exception ex)
        {
            // Should be impossible (the file is compiled in), but the dropdown must still explain itself.
            catalog = new HiggsfieldCatalog { Warning = $"The built-in model catalog could not be read: {ex.Message}" };
            return catalog;
        }
        catalog.Source = "built-in";
        catalog.Warning = warning;
        MergeCachedMotions(catalog);
        return catalog;
    }

    private static string ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
                           ?? throw new FileNotFoundException(EmbeddedResourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Presets fetched live from Higgsfield are cached beside the settings; they are added to the
    /// catalog's own (hand-edited) list, id-deduplicated, catalog entries winning on a clash.</summary>
    private static void MergeCachedMotions(HiggsfieldCatalog catalog)
    {
        try
        {
            if (!File.Exists(MotionsCachePath)) return;
            var cached = JsonSerializer.Deserialize<List<HiggsfieldMotionPreset>>(File.ReadAllText(MotionsCachePath));
            if (cached == null) return;
            var known = new HashSet<string>(catalog.MotionPresets.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var p in cached)
                if (!string.IsNullOrWhiteSpace(p.Id) && known.Add(p.Id)) catalog.MotionPresets.Add(p);
            catalog.MotionPresets = catalog.MotionPresets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { /* a bad cache just means no presets from it */ }
    }

    public static void SaveMotionsCache(List<HiggsfieldMotionPreset> presets)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MotionsCachePath)!);
        File.WriteAllText(MotionsCachePath, JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Parses the catalog JSON. Lenient about the enum/default kinds (ints or strings in the spec).</summary>
    public static HiggsfieldCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the file is not a JSON object");
        var catalog = new HiggsfieldCatalog
        {
            GeneratedOn = Str(root, "generated_on"),
            DefaultModelPath = Str(root, "default_model"),
        };
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("it has no \"models\" array");

        foreach (var m in models.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var model = new HiggsfieldModel
            {
                Path = Str(m, "path"),
                Label = Str(m, "label"),
                Family = Str(m, "family"),
                ImageParam = Str(m, "image_param"),
                SupportsMotions = m.TryGetProperty("supports_motions", out var sm) && sm.ValueKind == JsonValueKind.True,
                MaxMotions = m.TryGetProperty("max_motions", out var mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetInt32() : 0,
            };
            if (model.Path.Length == 0 || !model.Path.StartsWith('/'))
                throw new InvalidDataException("a model entry has no \"path\" starting with /");
            if (model.Label.Length == 0) model.Label = model.Path;
            if (model.ImageParam.Length == 0) model.ImageParam = "image_url";
            if (m.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var param = new HiggsfieldParam
                    {
                        Name = Str(p, "name"),
                        Title = Str(p, "title"),
                        Type = Str(p, "type"),
                        Required = p.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True,
                    };
                    if (param.Name.Length == 0) continue;
                    if (param.Type.Length == 0) param.Type = "string";
                    if (param.Title.Length == 0) param.Title = param.Name;
                    if (p.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
                        param.Enum = en.EnumerateArray().Select(RawValue).Where(v => v.Length > 0).ToList();
                    if (p.TryGetProperty("default", out var df) && df.ValueKind != JsonValueKind.Null)
                        param.Default = RawValue(df);
                    if (p.TryGetProperty("minimum", out var mn) && mn.ValueKind == JsonValueKind.Number) param.Minimum = mn.GetDouble();
                    if (p.TryGetProperty("maximum", out var mx) && mx.ValueKind == JsonValueKind.Number) param.Maximum = mx.GetDouble();
                    model.Params.Add(param);
                }
            catalog.Models.Add(model);
        }

        if (root.TryGetProperty("motion_presets", out var presets) && presets.ValueKind == JsonValueKind.Array)
            foreach (var p in presets.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var id = Str(p, "id");
                if (id.Length == 0) continue;
                var name = Str(p, "name");
                catalog.MotionPresets.Add(new HiggsfieldMotionPreset
                {
                    Id = id, Name = name.Length > 0 ? name : id, Description = Str(p, "description"),
                });
            }
        return catalog;
    }

    /// <summary>An enum/default value as the raw API string: "5" for the number 5, "16:9" for the string.</summary>
    private static string RawValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- display helpers -----------------------------------------------------------

    /// <summary>"5" → "5 s", "1080" → "1080p", "768P" → "768p", "16:9" → "16:9".</summary>
    public static string Display(string paramName, string raw)
    {
        switch (paramName)
        {
            case "duration":
                return raw + " s";
            case "resolution":
                return raw.EndsWith("p", StringComparison.OrdinalIgnoreCase) ? raw.ToLowerInvariant() : raw + "p";
            default:
                return raw;
        }
    }

    private static readonly (string Label, double Ratio)[] KnownRatios =
    {
        ("16:9", 16 / 9.0), ("9:16", 9 / 16.0), ("4:3", 4 / 3.0), ("3:4", 3 / 4.0), ("1:1", 1.0),
        ("21:9", 21 / 9.0), ("3:2", 1.5), ("2:3", 2 / 3.0), ("5:4", 1.25), ("4:5", 0.8),
    };

    /// <summary>Tolerance for calling a render "16:9": 2.5 % covers 1920×1080 vs 1920×1081-style crops.</summary>
    private const double RatioTolerance = 0.025;

    /// <summary>The nearest conventional label for a pixel size ("16:9"), or "1.78:1" when none is close.</summary>
    public static string AspectLabel(int width, int height)
    {
        if (width <= 0 || height <= 0) return "unknown";
        double r = (double)width / height;
        foreach (var (label, ratio) in KnownRatios)
            if (Math.Abs(r - ratio) / ratio <= RatioTolerance) return label;
        return r.ToString("0.00", CultureInfo.InvariantCulture) + ":1";
    }

    /// <summary>True when the pixel size is (within tolerance) the given "a:b" ratio.</summary>
    public static bool RatioMatches(int width, int height, string ratio)
    {
        if (width <= 0 || height <= 0) return false;
        var parts = (ratio ?? "").Split(':');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            || a <= 0 || b <= 0)
            return false;
        double want = a / b, have = (double)width / height;
        return Math.Abs(have - want) / want <= RatioTolerance;
    }
}

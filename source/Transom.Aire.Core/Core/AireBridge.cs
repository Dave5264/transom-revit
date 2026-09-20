using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Transom.Core;

/// <summary>
///     The aire_* tool surface, implemented once and hosted by whoever asks.
///     <para>
///     This used to live in Transom.dll's bridge, which meant every aire_* call needed Revit running — the
///     MCP shim does nothing but POST to a listener the Revit add-in owns. AIRE spends against files and
///     HTTPS and has never needed a document, a Revit session, or the AIRE window, so the implementation
///     moved here, into the BCL-only assembly all three hosts already share:
///     </para>
///     <list type="bullet">
///       <item>Transom.McpShim calls <see cref="Dispatch"/> in-process — no Revit, no window.</item>
///       <item>Transom.dll's BridgeToolsAire forwards to it, so the HTTP bridge keeps working.</item>
///       <item>The AIRE window drives the same <see cref="AireJobManager"/> from the other side.</item>
///     </list>
///     <para>
///     <b>Jobs are per-process.</b> A job started by the shim lives in the shim; the AIRE window cannot see
///     or cancel it, and vice versa. That is safe rather than merely tolerable, because the spend guard in
///     <see cref="AireJobManager"/> is a LOCK FILE, not an in-process lock — a second host is refused with
///     the holder's name instead of starting a second paid run. Use <c>aire_cancel_job</c> to stop what
///     Claude started.
///     </para>
///     <para>
///     <b>Credentials are never tool arguments.</b> Both the OpenAI key and the Higgsfield key pair are read
///     from the DPAPI-protected store the AIRE window writes (%AppData%\Transom\aire.json). They never
///     transit Claude, the shim's stdio, or the loopback socket. There is deliberately no tool that sets or
///     reads them: saving a key stays a thing the human does in the window, once.
///     </para>
/// </summary>
public static class AireBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Every tool name this surface answers to. The shim uses it to decide what to keep local.</summary>
    public static readonly string[] ToolNames =
    {
        "aire_enhance", "aire_video", "aire_video_models", "aire_job_status", "aire_cancel_job", "aire_log",
    };

    public static bool Handles(string tool) => Array.IndexOf(ToolNames, tool) >= 0;

    /// <summary>Routes an aire_* tool; null for anything else so a host can fall through to its own tools.</summary>
    public static string? Dispatch(string tool, JsonElement args)
    {
        try
        {
            return tool switch
            {
                "aire_enhance" => Enhance(args),
                "aire_video" => Video(args),
                "aire_video_models" => VideoModels(args),
                "aire_job_status" => JobStatus(args),
                "aire_cancel_job" => CancelJob(args),
                "aire_log" => LogReport(args),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // A throw here would surface as a transport failure with no hint of which tool died.
            return Err($"{tool} failed: {ex.Message}");
        }
    }

    // ---- aire_enhance (OpenAI image batch) ---------------------------------------

    private static string Enhance(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return Err("aire_enhance needs arguments (at least output_folder plus images or input_folder)");

        var files = new List<string>();
        if (args.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            foreach (var el in imgs.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) files.Add(el.GetString() ?? "");

        var inputFolder = Str(args, "input_folder");
        if (!string.IsNullOrWhiteSpace(inputFolder))
        {
            if (!Directory.Exists(inputFolder)) return Err($"input_folder not found: {inputFolder}");
            files.AddRange(AireEngine.ScanFolder(inputFolder));
        }

        var missing = files.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0) return Err("image file(s) not found: " + string.Join("; ", missing.Take(5)));

        var skipped = files.Where(f => !AireEngine.IsEnhanceableImage(f)).ToList();
        files = files.Where(AireEngine.IsEnhanceableImage).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return Err("no enhanceable images (supported: .png/.jpg/.jpeg/.webp; files ending in _enhanced are outputs and are skipped)");

        var outputFolder = Str(args, "output_folder");
        if (string.IsNullOrWhiteSpace(outputFolder)) return Err("output_folder is required");

        var model = Str(args, "model");
        if (string.IsNullOrWhiteSpace(model)) model = AireEngine.DefaultModel;
        if (AireEngine.RetirementMessage(model!) is { } retired) return Err(retired);
        if (!AireEngine.ModelSizeOptions.TryGetValue(model!, out var validSizes))
            return Err($"unknown model '{model}' (valid: {string.Join(", ", AireEngine.Models)})");

        var size = Str(args, "size");
        if (string.IsNullOrWhiteSpace(size)) size = "auto";
        if (!validSizes.Contains(size!))
            return Err($"size '{size}' is not valid for {model} (valid: {string.Join(", ", validSizes)})");

        var quality = Str(args, "quality");
        if (string.IsNullOrWhiteSpace(quality)) quality = AireEngine.DefaultQualityFor(model!);
        if (!AireEngine.IsValidQuality(model!, quality!))
            return Err($"quality '{quality}' is not valid for {model} (valid: {string.Join(", ", AireEngine.QualityOptionsFor(model!))})");

        var fidelity = Str(args, "input_fidelity");
        if (string.IsNullOrWhiteSpace(fidelity)) fidelity = "high";
        if (fidelity != "high" && fidelity != "low")
            return Err($"input_fidelity '{fidelity}' is not valid (valid: high, low)");

        bool modelTakesFidelity = AireEngine.SupportsInputFidelity(model!);
        if (!modelTakesFidelity && fidelity == "low")
            return Err($"input_fidelity 'low' cannot be honoured: {AireEngine.InputFidelityNote(model!)} "
                       + "Either accept high fidelity or choose a model that exposes the setting.");

        var prompt = Str(args, "prompt");
        if (string.IsNullOrWhiteSpace(prompt)) prompt = AireEngine.DefaultPrompt;

        var apiKey = AireSettings.Load().GetApiKey();
        if (apiKey.Length == 0)
            return Err("No OpenAI API key is saved. Ask the user to open the AIRE window (Transom ribbon → "
                       + "AI Render Enhancer, or the standalone AIRE app), paste their key, and run or close it "
                       + "once — the key is then remembered (encrypted per-user) and this tool can use it.");

        int textTokens = AireEngine.EstimateTextTokens(prompt!);
        int outputTokens = AireEngine.EstimateOutputTokens(model!, size!, quality!);
        double estimate = files.Sum(f => AireEngine.EstimateCost(
            AireEngine.EstimateImageTokensFromFile(f).Tokens, outputTokens, textTokens));

        var job = AireJobManager.Start(files, outputFolder!, prompt!, model!, size!, quality!, apiKey,
            out var error, fidelity == "high");
        if (job == null) return Err(error);

        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["job_id"] = job.Id,
            ["kind"] = "enhance",
            ["images_queued"] = files.Count,
            ["skipped_non_images"] = skipped.Count,
            ["model"] = model,
            ["size"] = size,
            ["quality"] = quality,
            ["input_fidelity"] = fidelity,
            ["input_fidelity_note"] = modelTakesFidelity ? null : AireEngine.InputFidelityNote(model!),
            ["output_folder"] = outputFolder,
            ["estimated_cost_usd"] = Math.Round(estimate, 4),
            ["estimated_cost_per_image_usd"] = Math.Round(files.Count == 0 ? 0 : estimate / files.Count, 4),
            ["host"] = HostLabel(),
            ["note"] = "Job started in the background (each image can take minutes). Poll aire_job_status "
                       + "with this job_id — do not assume completion. Report the estimated cost to the user now; "
                       + "once the job finishes, report actual_cost_usd from aire_job_status instead (it is what "
                       + "OpenAI billed, from the usage block of each response).",
        });
    }

    // ---- aire_video (Higgsfield image-to-video) ----------------------------------

    /// <summary>
    ///     Starts one clip, or with <c>estimate_only</c> just prices it. Mirrors the window's Generate path
    ///     exactly, including its founding rule: no vendor estimate, no clip. A model that is not on the
    ///     account answers the estimate call with 404, which is how an unavailable model is detected rather
    ///     than trusted from the catalog snapshot.
    /// </summary>
    private static string Video(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return Err("aire_video needs arguments (at least source_image, output_folder and prompt)");

        var catalog = HiggsfieldCatalog.Load();

        var sourceImage = Str(args, "source_image");
        if (string.IsNullOrWhiteSpace(sourceImage)) return Err("source_image is required (the render to animate)");
        if (!File.Exists(sourceImage)) return Err($"source_image not found: {sourceImage}");

        var outputFolder = Str(args, "output_folder");
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            outputFolder = AireSettings.Load().VideoOutputFolder;
            if (string.IsNullOrWhiteSpace(outputFolder))
                return Err("output_folder is required (nothing is remembered from the AIRE window yet)");
        }

        var prompt = Str(args, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return Err("prompt is required — on Kling models the camera move comes ENTIRELY from the prompt "
                       + "(they accept no motion presets), so an empty prompt yields a near-static clip that is "
                       + "still charged in full.");

        var modelPath = Str(args, "model");
        if (string.IsNullOrWhiteSpace(modelPath)) modelPath = AireSettings.Load().VideoModel;
        if (string.IsNullOrWhiteSpace(modelPath)) modelPath = catalog.DefaultModelPath;
        var model = catalog.FindModel(modelPath!);
        if (model == null)
            return Err($"unknown video model '{modelPath}'. Call aire_video_models for the list this build ships "
                       + "(the catalog is a snapshot of Higgsfield's spec and the line-up changes often).");

        // Per-model parameters, validated against the catalog's own enums so a doomed request never reaches
        // the vendor. Anything the model does not declare is refused by name rather than silently dropped.
        var chosen = new Dictionary<string, string>();
        foreach (var name in new[] { "duration", "resolution", "aspect_ratio", "seed" })
        {
            var raw = ScalarArg(args, name);
            if (raw == null) continue;
            var param = model.Param(name);
            if (param == null)
                return Err($"{model.Label} has no '{name}' parameter — it is not a setting on this model "
                           + $"(it accepts: {ParamList(model)}). Remove it, or pick a model that has it.");
            if (param.Enum is { Count: > 0 } && !param.Enum.Contains(raw))
                return Err($"{name} '{raw}' is not valid for {model.Label} (valid: {string.Join(", ", param.Enum)})");
            if (param.Minimum.HasValue && param.Maximum.HasValue
                && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var num)
                && (num < param.Minimum || num > param.Maximum))
                return Err($"{name} '{raw}' is out of range for {model.Label} "
                           + $"(allowed {param.Minimum}–{param.Maximum})");
            chosen[name] = raw;
        }

        // Motion presets. Only the Higgsfield DoP family accepts them; every Kling/Veo/Sora/Seedance/Hailuo/Wan
        // model declares supports_motions:false, and quietly ignoring a caller's motions there would produce a
        // clip that does not move the way they asked and still bills them.
        var motions = new List<HiggsfieldMotion>();
        if (args.TryGetProperty("motions", out var motionsEl) && motionsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in motionsEl.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var id = Str(m, "id") ?? "";
                var name = Str(m, "name") ?? "";
                if (id.Length == 0 && name.Length == 0) continue;

                var preset = catalog.MotionPresets.FirstOrDefault(p =>
                                 string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                             ?? catalog.MotionPresets.FirstOrDefault(p =>
                                 string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (preset == null && id.Length == 0)
                    return Err($"unknown motion preset '{name}'. Call aire_video_models to see the cached list, "
                               + "or open the AIRE window's Video tab and press Load Presets to refresh it.");

                var strength = Dbl(m, "strength") ?? 0.6;
                if (strength < 0 || strength > 1) return Err($"motion strength {strength} is out of range (0–1)");
                motions.Add(new HiggsfieldMotion
                {
                    Id = preset?.Id ?? id,
                    Name = preset?.Name ?? name,
                    Strength = strength,
                });
            }
        }

        if (motions.Count > 0 && !model.SupportsMotions)
            return Err($"{model.Label} does not accept motion presets — its catalog entry declares "
                       + "supports_motions:false, and the vendor ignores them. Describe the camera move in the "
                       + "prompt instead (\"slow orbit to the right, staying level\"), or switch to a Higgsfield "
                       + "DoP model, the only family that takes presets.");
        if (motions.Count > model.MaxMotions && model.MaxMotions > 0)
            return Err($"{model.Label} accepts at most {model.MaxMotions} motion preset(s); {motions.Count} were given.");

        var creds = AireSettings.Load().GetVideoCredentials();
        if (!creds.IsComplete)
            return Err("No Higgsfield credentials are saved. Higgsfield authenticates with a KEY PAIR (Key ID "
                       + "and Secret, both from cloud.higgsfield.ai) — not a single key like OpenAI. Ask the user "
                       + "to paste both into the AIRE window's Video tab once; they are then remembered "
                       + "(encrypted per-user) and this tool can use them.");

        bool estimateOnly = Bool(args, "estimate_only");

        // Say "busy" before pricing, so a blocked call does not spend a vendor round-trip to find out.
        if (!estimateOnly && AireJobManager.BusyReason() is { } busy) return Err(busy);

        // The founding rule: nothing spends without a real cost from the vendor. The estimate goes out with a
        // placeholder image URL because price does not depend on pixels; if the vendor rejects that naming
        // image_url, the render is uploaded (free) and the estimate retried — which also proves the upload path
        // before any money is involved.
        HiggsfieldEstimate est;
        string? uploadedUrl = null;
        try
        {
            var body = model.BuildBody(prompt!, EstimatePlaceholderUrl, chosen, motions);
            est = HiggsfieldClient.EstimateAsync(creds, model.Path, body, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (HiggsfieldApiException ex) when (ex.Kind == HiggsfieldErrorKind.Validation
                                                && ex.Detail.Contains("image_url", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                uploadedUrl = HiggsfieldClient.UploadFileAsync(creds, sourceImage!, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var body = model.BuildBody(prompt!, uploadedUrl, chosen, motions);
                est = HiggsfieldClient.EstimateAsync(creds, model.Path, body, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception inner)
            {
                return Err("Generate is blocked until Higgsfield returns a cost for this request. " + inner.Message);
            }
        }
        catch (Exception ex)
        {
            return Err("Generate is blocked until Higgsfield returns a cost for this request. " + ex.Message);
        }

        var (_, w, h) = AireEngine.EstimateImageTokensFromFile(sourceImage!);
        var shape = new Dictionary<string, object?>
        {
            ["model"] = model.Path,
            ["model_label"] = model.Label,
            ["duration"] = chosen.TryGetValue("duration", out var d) ? d : "model default",
            ["resolution"] = chosen.TryGetValue("resolution", out var r) ? r : "model default",
            ["aspect_ratio"] = chosen.TryGetValue("aspect_ratio", out var a) ? a
                : model.Has("aspect_ratio") ? "model default" : "follows the source image (no aspect parameter)",
            ["motions"] = motions.Count == 0
                ? (model.SupportsMotions ? "none (prompt only)" : "n/a — this model takes no presets")
                : string.Join(" + ", motions.Select(m => $"{m.Name} @ {m.Strength.ToString("0.00", CultureInfo.InvariantCulture)}")),
            ["source_image"] = sourceImage,
            ["source_resolution"] = w.HasValue && h.HasValue ? $"{w}x{h}" : "unknown",
            ["estimated_cost_usd"] = est.UsdText,
            ["estimated_credits"] = est.CreditsText,
            ["host"] = HostLabel(),
        };

        if (estimateOnly)
        {
            shape["ok"] = true;
            shape["estimate_only"] = true;
            shape["note"] = "Priced only — nothing was generated and nothing was charged. Report this cost to "
                            + "the user and call aire_video again without estimate_only to actually spend it.";
            return Json(shape);
        }

        var request = new AireVideoRequest
        {
            SourceImage = sourceImage!,
            OutputFolder = outputFolder!,
            Prompt = prompt!,
            Model = model,
            Parameters = chosen,
            Motions = motions,
            CachedPublicUrl = uploadedUrl,
            EstimatedCreditsText = est.CreditsText,
            EstimatedUsdText = est.UsdText,
        };

        var job = AireJobManager.StartVideo(request, creds, out var error);
        if (job == null) return Err(error);

        shape["ok"] = true;
        shape["job_id"] = job.Id;
        shape["kind"] = "video";
        shape["output_folder"] = outputFolder;
        shape["note"] = "Clip started (generation usually takes minutes — a Kling Master 10s clip runs about 8). "
                        + "Poll aire_job_status with this job_id; do not assume completion. Tell the user the "
                        + "estimated cost NOW. Cancelling only refunds while the request is still queued — once "
                        + "Higgsfield reports in_progress the clip will be charged whatever happens.";
        return Json(shape);
    }

    /// <summary>Price is independent of the pixels, so the estimate does not need a real upload.</summary>
    private const string EstimatePlaceholderUrl = "https://aire.transom.invalid/source.png";

    // ---- aire_video_models -------------------------------------------------------

    /// <summary>
    ///     What this build can actually ask for: every model with its real parameter names, enums and ranges,
    ///     plus whether it takes motion presets. Without this Claude has to guess durations and aspect support
    ///     from memory, and guesses become 422s or — worse — a silently ignored setting on a paid clip.
    /// </summary>
    private static string VideoModels(JsonElement args)
    {
        var catalog = HiggsfieldCatalog.Load();
        bool includeMotions = Bool(args, "include_motions", true);

        var models = catalog.Models.Select(m => new Dictionary<string, object?>
        {
            ["path"] = m.Path,
            ["label"] = m.Label,
            ["family"] = m.Family,
            ["supports_motions"] = m.SupportsMotions,
            ["max_motions"] = m.MaxMotions,
            ["parameters"] = m.Params.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["type"] = p.Type,
                ["required"] = p.Required,
                ["allowed"] = p.Enum is { Count: > 0 } ? p.Enum : null,
                ["minimum"] = p.Minimum,
                ["maximum"] = p.Maximum,
                ["default"] = p.Default,
            }).Cast<object?>().ToList(),
        }).Cast<object?>().ToList();

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["default_model"] = catalog.DefaultModelPath,
            ["catalog_source"] = catalog.Source,
            ["catalog_generated_on"] = catalog.GeneratedOn,
            ["catalog_warning"] = catalog.Warning,
            ["models"] = models,
            ["note"] = "The catalog is a SNAPSHOT of Higgsfield's spec, not a live list — a model here may not "
                       + "be on this account (the estimate call answers 404), and new models may exist that are "
                       + "not here. Only the Higgsfield DoP family accepts motion presets; on every other model "
                       + "the camera move must be written into the prompt.",
        };

        if (includeMotions)
        {
            payload["motion_presets"] = catalog.MotionPresets
                .Select(p => (object?)new Dictionary<string, object?> { ["id"] = p.Id, ["name"] = p.Name })
                .ToList();
            payload["motion_presets_note"] = catalog.MotionPresets.Count == 0
                ? "No presets are cached yet. They are fetched live by the AIRE window's Video tab (Load Presets) "
                  + "and cached to %AppData%\\Transom\\higgsfield-motions.json. They only matter for DoP models."
                : "Cached from Higgsfield. Pass one to aire_video as motions:[{name:\"Arc Right\",strength:0.4}] — "
                  + "DoP models only.";
        }

        return Json(payload);
    }

    // ---- aire_job_status / aire_cancel_job ---------------------------------------

    private static string JobStatus(JsonElement args)
    {
        var job = ResolveJob(args, out var err);
        if (job == null) return Err(err);
        return job switch
        {
            AireVideoJob video => Json(VideoStatus(video)),
            AireJob batch => Json(BatchStatus(batch)),
            _ => Err($"job {job.Id} has an unrecognised kind '{job.Kind}'"),
        };
    }

    private static Dictionary<string, object?> BatchStatus(AireJob job)
    {
        var results = job.Results.Select(r => (object?)new Dictionary<string, object?>
        {
            ["original_file"] = r.OriginalFile,
            ["output_file"] = r.OutputFile,
            ["input_resolution"] = r.InputResolution,
            ["status"] = r.Status,
            ["time_seconds"] = r.TimeSeconds,
            ["estimated_cost_usd"] = r.EstimatedCostUsd,
            ["actual_cost_usd"] = r.ActualCostUsd,
            ["output_tokens"] = r.OutputTokens,
            ["actual_size"] = r.ActualSize.Length > 0 ? r.ActualSize : null,
            ["error_message"] = r.ErrorMessage,
            ["error_kind"] = r.ErrorKind.Length > 0 ? r.ErrorKind : null,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["job_id"] = job.Id,
            ["kind"] = "enhance",
            ["status"] = job.Status,
            ["cancelled"] = job.CancelRequested,
            ["aborted_reason"] = job.Aborted ? job.AbortReason : null,
            ["aborted_kind"] = job.Aborted ? job.AbortKind : null,
            ["not_attempted"] = job.NotAttempted,
            ["done"] = job.Done,
            ["total"] = job.Total,
            ["current_file"] = job.CurrentFile,
            ["success_count"] = job.SuccessCount,
            ["failure_count"] = job.FailureCount,
            ["estimated_cost_usd"] = Math.Round(job.EstimatedCostUsd, 4),
            ["actual_cost_usd"] = job.HasActualCost ? Math.Round(job.ActualCostUsd, 4) : null,
            ["actual_cost_images"] = job.ActualCostImages,
            ["total_time"] = job.IsFinished ? AireEngine.SecondsToText(job.TotalTimeSeconds) : null,
            ["log_file"] = job.LogFile.Length > 0 ? job.LogFile : null,
            ["error"] = job.Error.Length > 0 ? job.Error : null,
            ["results"] = results,
        };
    }

    private static Dictionary<string, object?> VideoStatus(AireVideoJob job)
    {
        var elapsed = (DateTime.UtcNow - job.StartedUtc).TotalSeconds;
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["job_id"] = job.Id,
            ["kind"] = "video",
            // Higgsfield's own enum, with two local states either side:
            // uploading -> queued -> in_progress -> downloading -> completed | failed | nsfw | canceled.
            ["status"] = job.Status,
            ["finished"] = job.IsFinished,
            ["cancelled"] = job.CancelRequested,
            // False once the vendor starts generating: from then on the clip is charged whatever happens.
            ["can_cancel"] = job.CanCancel,
            ["committed_to_spend"] = job.Committed,
            ["source_image"] = job.Request.SourceImage,
            ["input_resolution"] = job.InputResolution,
            ["model"] = job.Request.Model.Path,
            ["duration"] = job.Request.ParameterText("duration"),
            ["output_file"] = job.OutputFile.Length > 0 ? job.OutputFile : null,
            // Higgsfield keeps outputs about a week; the local file is the durable copy.
            ["video_url"] = job.VideoUrl.Length > 0 ? job.VideoUrl : null,
            ["request_id"] = job.RequestId.Length > 0 ? job.RequestId : null,
            ["estimated_cost_usd"] = job.Request.EstimatedUsdText,
            ["estimated_credits"] = job.Request.EstimatedCreditsText,
            // What it actually cost: the estimate on success, zero for failed / nsfw / queued-cancelled,
            // all of which Higgsfield does not bill.
            ["charged_cost_usd"] = job.IsFinished ? job.CostUsdText : null,
            ["elapsed_seconds"] = Math.Round(elapsed, 1),
            ["poll_count"] = job.PollCount,
            ["last_checked_seconds_ago"] = job.LastPollUtc.HasValue
                ? Math.Round((DateTime.UtcNow - job.LastPollUtc.Value).TotalSeconds, 1)
                : (double?)null,
            ["total_time"] = job.IsFinished ? AireEngine.SecondsToText(job.TotalTimeSeconds) : null,
            ["log_file"] = job.LogFile.Length > 0 ? job.LogFile : null,
            ["note"] = job.Note.Length > 0 ? job.Note : null,
            ["error"] = job.Error.Length > 0 ? job.Error : null,
        };
    }

    private static string CancelJob(JsonElement args)
    {
        var job = ResolveJob(args, out var err);
        if (job == null) return Err(err);
        if (job.IsFinished) return Err($"job {job.Id} already finished ({job.Status}) — nothing to cancel");

        // A video clip past the queue cannot be un-spent. Say so instead of implying the money comes back.
        if (job is AireVideoJob video && !video.CanCancel)
        {
            job.Cancel();
            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["job_id"] = job.Id,
                ["kind"] = "video",
                ["refundable"] = false,
                ["note"] = "Cancel requested, but Higgsfield had already started generating this clip. It cannot "
                           + "be stopped and WILL be charged. AIRE waits for it and still downloads the file so "
                           + "the money is not wasted — keep polling aire_job_status.",
            });
        }

        job.Cancel();
        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["job_id"] = job.Id,
            ["kind"] = job.Kind,
            ["refundable"] = job is AireVideoJob,
            ["note"] = job is AireVideoJob
                ? "Cancel requested while the request was still queued — Higgsfield refunds those. Poll "
                  + "aire_job_status until it reports canceled."
                : "Cancel requested. The image currently uploading/generating may still complete; poll "
                  + "aire_job_status until the job reports completed.",
        });
    }

    // ---- aire_log ----------------------------------------------------------------

    /// <summary>
    ///     The plain-English read of a run's CSV, same as the window's View Log. Lets Claude explain a finished
    ///     or failed run without the user opening AIRE, and without Claude re-parsing CSV columns by hand.
    /// </summary>
    private static string LogReport(JsonElement args)
    {
        var path = Str(args, "log_file");
        if (string.IsNullOrWhiteSpace(path))
        {
            var job = AireJobManager.LatestAny;
            path = job?.LogFile;
            if (string.IsNullOrWhiteSpace(path))
                return Err("no log_file given and the most recent job has not written one yet "
                           + "(a job writes its CSV when it finishes)");
        }
        if (!File.Exists(path)) return Err($"log file not found: {path}");

        var report = AireLogReport.Load(path!);
        var rows = report.Rows.Select(r => (object?)new Dictionary<string, object?>
        {
            ["name"] = r.Name,
            ["source"] = r.SourcePath,
            ["output"] = r.OutputPath,
            ["status"] = r.Status,
            ["succeeded"] = r.Succeeded,
            ["message"] = r.Message.Length > 0 ? r.Message : null,
            ["error_kind"] = r.Kind.Length > 0 ? r.Kind : null,
            // The same "What to do" sentence the window shows, so Claude relays the fix rather than
            // re-deriving one from the raw error text.
            ["what_to_do"] = r.Advice.Length > 0 ? r.Advice : null,
            ["estimated_cost_usd"] = r.EstimatedCostUsd,
            ["actual_cost_usd"] = r.ActualCostUsd,
        }).ToList();

        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["log_file"] = path,
            ["kind"] = report.Kind,
            ["run_at"] = report.RunAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            ["success_count"] = report.SuccessCount,
            ["failure_count"] = report.FailureCount,
            ["total_time"] = AireEngine.SecondsToText(report.TotalSeconds),
            ["estimated_cost_usd"] = Math.Round(report.EstimatedCostUsd, 4),
            ["actual_cost_usd"] = report.ActualCostUsd.HasValue ? Math.Round(report.ActualCostUsd.Value, 4) : null,
            ["actual_cost_rows"] = report.ActualCostRows,
            // Set when the run stopped ITSELF because the failure would repeat. Report this, not "N failed".
            ["stopped_reason"] = report.StoppedReason.Length > 0 ? report.StoppedReason : null,
            ["warning"] = report.Warning.Length > 0 ? report.Warning : null,
            ["rows"] = rows,
        });
    }

    // ---- helpers -----------------------------------------------------------------

    private static AireJobBase? ResolveJob(JsonElement args, out string error)
    {
        string? id = args.ValueKind == JsonValueKind.Object ? Str(args, "job_id") : null;
        var job = string.IsNullOrWhiteSpace(id) ? AireJobManager.LatestAny : AireJobManager.FindAny(id!);
        error = job != null ? ""
            : string.IsNullOrWhiteSpace(id)
                ? "no AIRE job has been started in this process yet. Jobs live in whichever process started "
                  + "them — a clip started from the AIRE window is not visible here, and vice versa."
                : $"unknown job_id '{id}' (jobs are kept in memory for the life of the process that started them)";
        return job;
    }

    private static string ParamList(HiggsfieldModel model) =>
        model.Params.Count == 0 ? "no per-model parameters" : string.Join(", ", model.Params.Select(p => p.Name));

    /// <summary>Which process answered, so a confusing "no job" reply is traceable to the right AIRE.</summary>
    private static string HostLabel()
    {
        try { return $"{System.Diagnostics.Process.GetCurrentProcess().ProcessName} (pid {Environment.ProcessId})"; }
        catch { return $"pid {Environment.ProcessId}"; }
    }

    /// <summary>Numbers and strings both arrive for duration/seed depending on the caller; accept either.</summary>
    private static string? ScalarArg(JsonElement args, string prop)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    private static string? Str(JsonElement args, string prop)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Dbl(JsonElement args, string prop)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement args, string prop, bool fallback = false)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v)
           && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;

    private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonOpts);

    private static string Err(string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = message }, JsonOpts);
}

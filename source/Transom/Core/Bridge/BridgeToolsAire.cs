using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Transom.Core;

/// <summary>
///     AIRE (AI Render Enhancer) bridge tools — lets Claude run the same batch-enhancement engine the
///     AIRE window uses. Every bridge request must answer well inside the 30-second cap while one 4K
///     image takes minutes, so the surface is asynchronous by design: <c>aire_enhance</c> starts a job
///     and returns immediately; <c>aire_job_status</c> polls it; <c>aire_cancel_job</c> stops it.
///     <para>
///     The OpenAI API key comes ONLY from the DPAPI-protected store the AIRE window writes
///     (%AppData%\Transom\aire.json) — it is never accepted as a tool argument, so the key never
///     transits Claude, the shim, or the loopback socket. These tools are document-independent
///     (pure files + HTTPS) and are dispatched before document resolution in <see cref="Handle"/>.
///     </para>
/// </summary>
public static partial class BridgeTools
{
    /// <summary>Routes the aire_* tools; null for anything else so Handle can fall through.</summary>
    internal static string? DispatchAire(string tool, JsonElement args) => tool switch
    {
        "aire_enhance" => AireEnhance(args),
        "aire_job_status" => AireJobStatus(args),
        "aire_cancel_job" => AireCancelJob(args),
        _ => null,
    };

    private static string AireEnhance(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return Err("aire_enhance needs arguments (at least output_folder plus images or input_folder)");

        // --- gather inputs -------------------------------------------------
        var files = new List<string>();
        if (args.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            foreach (var el in imgs.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) files.Add(el.GetString() ?? "");

        var inputFolder = StrArg(args, "input_folder");
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

        var outputFolder = StrArg(args, "output_folder");
        if (string.IsNullOrWhiteSpace(outputFolder)) return Err("output_folder is required");

        // --- options (defaults mirror the AIRE window) ---------------------
        var model = StrArg(args, "model");
        if (string.IsNullOrWhiteSpace(model)) model = AireEngine.DefaultModel;
        if (!AireEngine.ModelSizeOptions.TryGetValue(model, out var validSizes))
            return Err($"unknown model '{model}' (valid: {string.Join(", ", AireEngine.ModelSizeOptions.Keys)})");

        var size = StrArg(args, "size");
        if (string.IsNullOrWhiteSpace(size)) size = "auto";
        if (!validSizes.Contains(size))
            return Err($"size '{size}' is not valid for {model} (valid: {string.Join(", ", validSizes)})");

        var quality = StrArg(args, "quality");
        if (string.IsNullOrWhiteSpace(quality)) quality = AireEngine.DefaultQuality;
        if (!AireEngine.QualityOptions.Contains(quality))
            return Err($"unknown quality '{quality}' (valid: {string.Join(", ", AireEngine.QualityOptions)})");

        var prompt = StrArg(args, "prompt");
        if (string.IsNullOrWhiteSpace(prompt)) prompt = AireEngine.DefaultPrompt;

        // --- API key: stored-only, never a tool argument -------------------
        var apiKey = AireSettings.Load().GetApiKey();
        if (apiKey.Length == 0)
            return Err("No OpenAI API key is saved. Ask the user to open the AIRE window (Transom ribbon → "
                       + "AI Render Enhancer), paste their key, and run or close it once — the key is then "
                       + "remembered (encrypted per-user) and this tool can use it.");

        // --- cost estimate (returned so the caller can report it) ----------
        int textTokens = AireEngine.EstimateTextTokens(prompt);
        int outputTokens = AireEngine.EstimateImageTokensFromSize(size);
        double estimate = files.Sum(f => AireEngine.EstimateCost(
            AireEngine.EstimateImageTokensFromFile(f).Tokens, outputTokens, textTokens));

        var job = AireJobManager.Start(files, outputFolder, prompt, model, size, quality, apiKey, out var error);
        if (job == null) return Err(error);

        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["job_id"] = job.Id,
            ["images_queued"] = files.Count,
            ["skipped_non_images"] = skipped.Count,
            ["model"] = model,
            ["size"] = size,
            ["quality"] = quality,
            ["output_folder"] = outputFolder,
            ["estimated_cost_usd"] = Math.Round(estimate, 4),
            ["note"] = "Job started in the background (each image can take minutes). Poll aire_job_status "
                       + "with this job_id — do not assume completion, and report the estimated cost to the user.",
        });
    }

    private static string AireJobStatus(JsonElement args)
    {
        var job = ResolveJob(args, out var err);
        if (job == null) return Err(err);

        var results = job.Results.Select(r => new Dictionary<string, object?>
        {
            ["original_file"] = r.OriginalFile,
            ["output_file"] = r.OutputFile,
            ["input_resolution"] = r.InputResolution,
            ["status"] = r.Status,
            ["time_seconds"] = r.TimeSeconds,
            ["estimated_cost_usd"] = r.EstimatedCostUsd,
            ["error_message"] = r.ErrorMessage,
        }).Cast<object?>().ToList();

        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["job_id"] = job.Id,
            ["status"] = job.Status,
            ["done"] = job.Done,
            ["total"] = job.Total,
            ["current_file"] = job.CurrentFile,
            ["success_count"] = job.SuccessCount,
            ["failure_count"] = job.FailureCount,
            ["estimated_cost_usd"] = Math.Round(job.EstimatedCostUsd, 4),
            ["total_time"] = job.IsFinished ? AireEngine.SecondsToText(job.TotalTimeSeconds) : null,
            ["log_file"] = job.LogFile.Length > 0 ? job.LogFile : null,
            ["error"] = job.Error.Length > 0 ? job.Error : null,
            ["results"] = results,
        });
    }

    private static string AireCancelJob(JsonElement args)
    {
        var job = ResolveJob(args, out var err);
        if (job == null) return Err(err);
        if (job.IsFinished) return Err($"job {job.Id} already finished ({job.Status}) — nothing to cancel");
        job.Cancel();
        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["job_id"] = job.Id,
            ["note"] = "Cancel requested. The image currently uploading/generating may still complete; "
                       + "poll aire_job_status until the job reports completed.",
        });
    }

    /// <summary>job_id arg → that job; omitted → the latest job. Null (with message) when nothing matches.</summary>
    private static AireJob? ResolveJob(JsonElement args, out string error)
    {
        string? id = args.ValueKind == JsonValueKind.Object ? StrArg(args, "job_id") : null;
        var job = string.IsNullOrWhiteSpace(id) ? AireJobManager.Latest : AireJobManager.Find(id!);
        error = job != null ? ""
            : string.IsNullOrWhiteSpace(id)
                ? "no AIRE job has been started this session"
                : $"unknown job_id '{id}' (jobs are kept in memory for the Revit session only)";
        return job;
    }
}

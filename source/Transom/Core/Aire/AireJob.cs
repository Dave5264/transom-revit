using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Transom.Core;

/// <summary>One image's outcome inside an AIRE batch (mirrors the original app's CSV row).</summary>
public sealed class AireResult
{
    public string OriginalFile = "";
    public string OutputFile = "";
    public string InputResolution = "unknown";
    public string Status = "";          // "Success" | "Failed"
    public double TimeSeconds;
    public double EstimatedCostUsd;
    public string ErrorMessage = "";
}

/// <summary>
///     A batch-enhancement run: processes the queued images sequentially through
///     <see cref="AireEngine.EditImageAsync"/>, writes "&lt;stem&gt;_enhanced.png" outputs and a CSV log
///     (output\logs\enhancement_log_yyyyMMdd_HHmmss.csv), and exposes pollable progress. Runs entirely on
///     background threads — started by the AIRE window or the bridge's aire_enhance tool, and polled by
///     aire_job_status, which is what keeps every bridge request far under its 30-second cap.
/// </summary>
public sealed class AireJob
{
    public string Id { get; } = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
    public IReadOnlyList<string> InputFiles { get; }
    public string OutputFolder { get; }
    public string Prompt { get; }
    public string Model { get; }
    public string Size { get; }
    public string Quality { get; }

    // Volatile snapshot state — written by the run loop, read by the UI dispatcher and bridge pollers.
    public string Status { get; private set; } = "queued";   // queued | running | completed | failed
    public int Done { get; private set; }
    public int Total => InputFiles.Count;
    public string CurrentFile { get; private set; } = "";
    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }
    public double TotalTimeSeconds { get; private set; }
    public double EstimatedCostUsd { get; private set; }
    public string LogFile { get; private set; } = "";
    public string Error { get; private set; } = "";

    private readonly List<AireResult> _results = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised after every state change, on a background thread — UI must marshal to its dispatcher.</summary>
    public event Action<AireJob>? Progress;

    public AireJob(IEnumerable<string> inputFiles, string outputFolder, string prompt,
        string model, string size, string quality)
    {
        InputFiles = inputFiles.ToList();
        OutputFolder = outputFolder;
        Prompt = prompt;
        Model = model;
        Size = size;
        Quality = quality;
    }

    public IReadOnlyList<AireResult> Results { get { lock (_gate) return _results.ToList(); } }

    public bool IsFinished => Status is "completed" or "failed";

    public void Cancel() => _cts.Cancel();

    /// <summary>Starts the run on the thread pool. The api key stays inside the closure — never on job state.</summary>
    internal Task Start(string apiKey) => Task.Run(() => RunAsync(apiKey));

    private async Task RunAsync(string apiKey)
    {
        Status = "running";
        Notify();
        var batch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(OutputFolder);
            var logsFolder = Path.Combine(OutputFolder, "logs");
            Directory.CreateDirectory(logsFolder);
            var logPath = Path.Combine(logsFolder, $"enhancement_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            int textTokens = AireEngine.EstimateTextTokens(Prompt);
            int outputTokens = AireEngine.EstimateImageTokensFromSize(Size);

            for (int i = 0; i < InputFiles.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var inputPath = InputFiles[i];
                CurrentFile = Path.GetFileName(inputPath);
                Notify();

                var result = new AireResult { OriginalFile = inputPath };
                var oneImage = Stopwatch.StartNew();
                try
                {
                    var (inputTokens, w, h) = AireEngine.EstimateImageTokensFromFile(inputPath);
                    result.InputResolution = w.HasValue && h.HasValue ? $"{w}x{h}" : "unknown";
                    var cost = AireEngine.EstimateCost(inputTokens, outputTokens, textTokens);

                    var outputPath = Path.Combine(OutputFolder,
                        Path.GetFileNameWithoutExtension(inputPath) + "_enhanced." + AireEngine.OutputFormat);

                    var png = await AireEngine.EditImageAsync(apiKey, Model, inputPath, Prompt, Size, Quality, _cts.Token)
                        .ConfigureAwait(false);
                    await File.WriteAllBytesAsync(outputPath, png, CancellationToken.None).ConfigureAwait(false);

                    result.OutputFile = outputPath;
                    result.Status = "Success";
                    result.EstimatedCostUsd = Math.Round(cost, 6);
                    SuccessCount++;
                    EstimatedCostUsd += cost;
                }
                catch (OperationCanceledException)
                {
                    result.Status = "Failed";
                    result.ErrorMessage = "cancelled";
                    FailureCount++;
                }
                catch (Exception ex)
                {
                    result.Status = "Failed";
                    result.ErrorMessage = ex.Message;
                    FailureCount++;
                }
                result.TimeSeconds = Math.Round(oneImage.Elapsed.TotalSeconds, 2);

                lock (_gate) _results.Add(result);
                Done = i + 1;
                Notify();

                if (_cts.Token.IsCancellationRequested) break;
            }

            WriteCsv(logPath);
            LogFile = logPath;
            TotalTimeSeconds = batch.Elapsed.TotalSeconds;
            Status = "completed";
        }
        catch (Exception ex)
        {
            TotalTimeSeconds = batch.Elapsed.TotalSeconds;
            Error = ex is OperationCanceledException ? "cancelled" : ex.Message;
            Status = "failed";
        }
        CurrentFile = "";
        Notify();
    }

    private void WriteCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("original_file,output_file,input_resolution,model,output_size,quality,status,time_seconds,estimated_cost_usd,error_message");
        lock (_gate)
            foreach (var r in _results)
                sb.AppendLine(string.Join(",",
                    Csv(r.OriginalFile), Csv(r.OutputFile), Csv(r.InputResolution), Csv(Model), Csv(Size),
                    Csv(Quality), Csv(r.Status),
                    r.TimeSeconds.ToString(CultureInfo.InvariantCulture),
                    r.EstimatedCostUsd.ToString(CultureInfo.InvariantCulture),
                    Csv(r.ErrorMessage)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private void Notify()
    {
        try { Progress?.Invoke(this); } catch { /* observers must not kill the run */ }
    }
}

/// <summary>
///     Registry of AIRE jobs, shared by the WPF window and the bridge tools. One job runs at a time —
///     a second start while one is running is refused (both entry points funnel through here, so the UI
///     and Claude can't double-spend concurrently). Finished jobs are kept for status polling.
/// </summary>
public static class AireJobManager
{
    private static readonly object Gate = new();
    private static readonly List<AireJob> Jobs = new();

    /// <summary>The most recently started job, or null.</summary>
    public static AireJob? Latest { get { lock (Gate) return Jobs.LastOrDefault(); } }

    public static AireJob? Find(string id) { lock (Gate) return Jobs.FirstOrDefault(j => j.Id == id); }

    public static AireJob? RunningJob { get { lock (Gate) return Jobs.FirstOrDefault(j => !j.IsFinished); } }

    /// <summary>Creates and starts a job. Returns null (with <paramref name="error"/>) when refused.</summary>
    public static AireJob? Start(IEnumerable<string> inputFiles, string outputFolder, string prompt,
        string model, string size, string quality, string apiKey, out string error)
    {
        lock (Gate)
        {
            var running = Jobs.FirstOrDefault(j => !j.IsFinished);
            if (running != null)
            {
                error = $"An AIRE job is already running (job_id {running.Id}, {running.Done}/{running.Total} done). "
                        + "Wait for it to finish or cancel it first.";
                return null;
            }

            var job = new AireJob(inputFiles, outputFolder, prompt, model, size, quality);
            Jobs.Add(job);
            if (Jobs.Count > 16) Jobs.RemoveAt(0); // keep memory bounded; old finished jobs age out
            job.Start(apiKey);
            error = "";
            return job;
        }
    }
}

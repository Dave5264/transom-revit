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

    /// <summary>True once a cancel has been requested — the run may still be finishing its current image.</summary>
    public bool CancelRequested => _cts.IsCancellationRequested;

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
                // Break, don't throw: an exception here escapes to the outer catch and skips WriteCsv,
                // losing the log for images already generated and billed. Cancelling before the first
                // image hit that path every time — rare from the bridge, easy to hit with a Cancel button.
                if (_cts.Token.IsCancellationRequested) break;
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

    /// <summary>
    ///     Cross-process spend guard. <see cref="Gate"/> only stops the AIRE window and the Claude bridge
    ///     from double-starting inside ONE process; it says nothing about a second process. Once AIRE can be
    ///     launched standalone, Revit's copy and the standalone app are two processes that would each think
    ///     they were the only one — and each start a batch against the same paid account.
    ///     <para>
    ///     A lock FILE, not a named mutex or semaphore: the OS drops the handle when a process dies, so a
    ///     killed Revit cannot leave AIRE permanently wedged. A <see cref="System.Threading.Mutex"/> is also
    ///     thread-affine (release must happen on the acquiring thread), which an async run loop that hops
    ///     thread-pool threads cannot honour.
    ///     </para>
    /// </summary>
    private static FileStream? _spendLock;

    private static string SpendLockPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transom", "aire.job.lock");

    /// <summary>
    ///     Takes the cross-process lock. Returns false only when another process demonstrably holds it —
    ///     any other failure (odd profile, permissions) returns true, because refusing to spend is the
    ///     caller's decision to make and a guard file must never become the reason a batch can't run.
    /// </summary>
    private static bool TryAcquireSpendLock(out string holder)
    {
        holder = "";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SpendLockPath)!);
            // Read so a blocked process can still see who holds it; Delete because DeleteOnClose leaves the
            // file delete-pending, and Windows then refuses ANY later open whose share mode omits
            // FILE_SHARE_DELETE — without it the "who holds it?" read below always fails. Withholding
            // Write is what actually does the excluding.
            _spendLock = new FileStream(SpendLockPath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            var stamp = Encoding.UTF8.GetBytes(
                $"{CurrentProcessLabel()} since {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _spendLock.Write(stamp, 0, stamp.Length);
            _spendLock.Flush();
            return true;
        }
        catch (IOException)
        {
            holder = ReadSpendLockHolder();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            holder = ReadSpendLockHolder();
            return false;
        }
        catch
        {
            _spendLock = null;
            return true;
        }
    }

    private static string CurrentProcessLabel()
    {
        try { return $"{Process.GetCurrentProcess().ProcessName} (pid {Environment.ProcessId})"; }
        catch { return $"pid {Environment.ProcessId}"; }
    }

    private static string ReadSpendLockHolder()
    {
        try
        {
            using var fs = new FileStream(SpendLockPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd().Trim();
            return text.Length > 0 ? text : "another Transom process on this machine";
        }
        catch { return "another Transom process on this machine"; }
    }

    private static void ReleaseSpendLock()
    {
        try { _spendLock?.Dispose(); } catch { /* the handle dies with the process anyway */ }
        _spendLock = null;
    }

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

            if (!TryAcquireSpendLock(out var holder))
            {
                error = $"An AIRE job is already running — {holder}. "
                        + "Wait for it to finish, or cancel it there first.";
                return null;
            }

            var job = new AireJob(inputFiles, outputFolder, prompt, model, size, quality);
            Jobs.Add(job);
            if (Jobs.Count > 16) Jobs.RemoveAt(0); // keep memory bounded; old finished jobs age out

            // Hand the lock back the moment the run ends, however it ends. Attached BEFORE Start so a job
            // that fails instantly still releases.
            void ReleaseWhenFinished(AireJob finished)
            {
                if (!finished.IsFinished) return;
                finished.Progress -= ReleaseWhenFinished;
                lock (Gate) ReleaseSpendLock();
            }
            job.Progress += ReleaseWhenFinished;

            job.Start(apiKey);
            error = "";
            return job;
        }
    }
}

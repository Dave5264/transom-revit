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
///     What every AIRE job has in common, whichever provider it spends against: an id, a status string the
///     UI and bridge poll, a cancel token, a log file, and a progress event. <see cref="AireJobManager"/>
///     tracks jobs by this type so an image batch and a video clip can never run at the same time — the
///     spend lock is one lock, not one per provider.
/// </summary>
public abstract class AireJobBase
{
    public string Id { get; } = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>"enhance" (OpenAI image batch) or "video" (Higgsfield clip).</summary>
    public abstract string Kind { get; }

    /// <summary>One clause for busy messages: "enhancing images (2/8 done)".</summary>
    public abstract string Summary { get; }

    public string Status { get; protected set; } = "queued";
    public abstract bool IsFinished { get; }
    public string LogFile { get; protected set; } = "";
    public string Error { get; protected set; } = "";
    public double TotalTimeSeconds { get; protected set; }

    protected readonly CancellationTokenSource Cts = new();

    public virtual void Cancel() => Cts.Cancel();

    /// <summary>True once a cancel has been requested — the run may still be finishing its current step.</summary>
    public bool CancelRequested => Cts.IsCancellationRequested;

    /// <summary>Raised after every state change, on a background thread — UI must marshal to its dispatcher.</summary>
    public event Action<AireJobBase>? Progress;

    protected void Notify()
    {
        try { Progress?.Invoke(this); } catch { /* observers must not kill the run */ }
    }

    protected static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }
}

/// <summary>
///     A batch-enhancement run: processes the queued images sequentially through
///     <see cref="AireEngine.EditImageAsync"/>, writes "&lt;stem&gt;_enhanced.png" outputs and a CSV log
///     (output\logs\enhancement_log_yyyyMMdd_HHmmss.csv), and exposes pollable progress. Runs entirely on
///     background threads — started by the AIRE window or the bridge's aire_enhance tool, and polled by
///     aire_job_status, which is what keeps every bridge request far under its 30-second cap.
/// </summary>
public sealed class AireJob : AireJobBase
{
    public IReadOnlyList<string> InputFiles { get; }
    public string OutputFolder { get; }
    public string Prompt { get; }
    public string Model { get; }
    public string Size { get; }
    public string Quality { get; }

    // Volatile snapshot state — written by the run loop, read by the UI dispatcher and bridge pollers.
    // Status: queued | running | completed | failed
    public int Done { get; private set; }
    public int Total => InputFiles.Count;
    public string CurrentFile { get; private set; } = "";
    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }
    public double EstimatedCostUsd { get; private set; }

    private readonly List<AireResult> _results = new();
    private readonly object _gate = new();

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

    public override string Kind => "enhance";
    public override string Summary => $"enhancing images ({Done}/{Total} done)";

    public IReadOnlyList<AireResult> Results { get { lock (_gate) return _results.ToList(); } }

    public override bool IsFinished => Status is "completed" or "failed";

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
                if (Cts.Token.IsCancellationRequested) break;
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

                    var png = await AireEngine.EditImageAsync(apiKey, Model, inputPath, Prompt, Size, Quality, Cts.Token)
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

                if (Cts.Token.IsCancellationRequested) break;
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
}

/// <summary>
///     Registry of AIRE jobs, shared by the WPF window and the bridge tools. One job runs at a time —
///     a second start while one is running is refused, whichever kind either is (both entry points and
///     both providers funnel through here, so the UI and Claude can't double-spend concurrently, and an
///     image batch and a video clip can't run together). Finished jobs are kept for status polling.
/// </summary>
public static class AireJobManager
{
    private static readonly object Gate = new();
    private static readonly List<AireJobBase> Jobs = new();

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

    /// <summary>The most recently started image batch, or null. Image jobs only — this is the bridge's
    /// view, and aire_job_status reports batch fields a video clip does not have.</summary>
    public static AireJob? Latest { get { lock (Gate) return Jobs.OfType<AireJob>().LastOrDefault(); } }

    public static AireJob? Find(string id) { lock (Gate) return Jobs.OfType<AireJob>().FirstOrDefault(j => j.Id == id); }

    /// <summary>The most recently started video clip, or null.</summary>
    public static AireVideoJob? LatestVideo { get { lock (Gate) return Jobs.OfType<AireVideoJob>().LastOrDefault(); } }

    /// <summary>Whatever is running in this process right now — an image batch or a video clip — or null.</summary>
    public static AireJobBase? RunningJob { get { lock (Gate) return Jobs.FirstOrDefault(j => !j.IsFinished); } }

    /// <summary>
    ///     Why a new job would be refused right now, in words, or null when AIRE is free. Lets the window say
    ///     "AIRE is busy" BEFORE showing a cost confirmation, rather than after the user has said yes. This is
    ///     a preview: <see cref="Start"/> / <see cref="StartVideo"/> still make the binding check.
    /// </summary>
    public static string? BusyReason()
    {
        lock (Gate)
        {
            var running = Jobs.FirstOrDefault(j => !j.IsFinished);
            if (running != null) return InProcessBusyMessage(running);
            if (_spendLock != null) return null; // we hold it, so nothing else can
            if (!TryAcquireSpendLock(out var holder)) return CrossProcessBusyMessage(holder);
            ReleaseSpendLock();
            return null;
        }
    }

    private static string InProcessBusyMessage(AireJobBase running) =>
        $"An AIRE job is already running — {running.Summary} (job_id {running.Id}). "
        + "Wait for it to finish or cancel it first.";

    private static string CrossProcessBusyMessage(string holder) =>
        $"An AIRE job is already running — {holder}. Wait for it to finish, or cancel it there first.";

    /// <summary>Creates and starts an image batch. Returns null (with <paramref name="error"/>) when refused.</summary>
    public static AireJob? Start(IEnumerable<string> inputFiles, string outputFolder, string prompt,
        string model, string size, string quality, string apiKey, out string error) =>
        Launch(() => new AireJob(inputFiles, outputFolder, prompt, model, size, quality),
            job => job.Start(apiKey), out error);

    /// <summary>Creates and starts a video clip. Returns null (with <paramref name="error"/>) when refused.
    /// The credentials stay inside the closure — never on job state.</summary>
    public static AireVideoJob? StartVideo(AireVideoRequest request, HiggsfieldCredentials credentials, out string error) =>
        Launch(() => new AireVideoJob(request), job => job.Start(credentials), out error);

    private static T? Launch<T>(Func<T> create, Action<T> start, out string error) where T : AireJobBase
    {
        lock (Gate)
        {
            var running = Jobs.FirstOrDefault(j => !j.IsFinished);
            if (running != null)
            {
                error = InProcessBusyMessage(running);
                return null;
            }

            if (!TryAcquireSpendLock(out var holder))
            {
                error = CrossProcessBusyMessage(holder);
                return null;
            }

            var job = create();
            Jobs.Add(job);
            if (Jobs.Count > 16) Jobs.RemoveAt(0); // keep memory bounded; old finished jobs age out

            // Hand the lock back the moment the run ends, however it ends. Attached BEFORE Start so a job
            // that fails instantly still releases.
            void ReleaseWhenFinished(AireJobBase finished)
            {
                if (!finished.IsFinished) return;
                finished.Progress -= ReleaseWhenFinished;
                lock (Gate) ReleaseSpendLock();
            }
            job.Progress += ReleaseWhenFinished;

            start(job);
            error = "";
            return job;
        }
    }
}

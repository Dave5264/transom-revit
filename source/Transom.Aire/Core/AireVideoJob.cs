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

/// <summary>Everything a video clip needs, gathered by the tab before the confirmation dialog.</summary>
public sealed class AireVideoRequest
{
    public string SourceImage = "";
    public string OutputFolder = "";
    public string Prompt = "";
    public HiggsfieldModel Model = null!;
    /// <summary>Chosen per-model values by parameter name, as raw API strings ("5", "1080", "16:9").</summary>
    public Dictionary<string, string> Parameters = new();
    public List<HiggsfieldMotion> Motions = new();
    /// <summary>A public URL for <see cref="SourceImage"/> uploaded earlier this session (for the estimate).
    /// Reused instead of sending a 4K PNG a second time.</summary>
    public string? CachedPublicUrl;
    /// <summary>The vendor's figures shown in the confirmation dialog, kept verbatim for the log.</summary>
    public string EstimatedCreditsText = "";
    public string EstimatedUsdText = "";

    public string ParameterText(string name) => Parameters.TryGetValue(name, out var v) ? v : "";
}

/// <summary>
///     One image-to-video generation at Higgsfield: upload the render, submit, poll until terminal, download
///     the MP4 next to the render it came from, write a CSV row. States walk the vendor's own enum with two
///     local ones either side of it — <c>uploading → queued → in_progress → downloading → completed</c>, or
///     <c>failed | nsfw | canceled</c>. Runs on the thread pool like <see cref="AireJob"/>, holds the same
///     cross-process spend lock through <see cref="AireJobManager"/>, and reports through the same
///     <see cref="AireJobBase.Progress"/> event.
///     <para>
///     Cancel semantics differ from the image batch and the UI must not paper over it: Higgsfield can only
///     cancel a request that has NOT started (and refunds it). So <see cref="CanCancel"/> is true while
///     uploading (nothing sent yet) and while queued (a remote cancel is issued), and false once the vendor
///     reports in_progress — from then on the clip will finish and be charged.
///     </para>
/// </summary>
public sealed class AireVideoJob : AireJobBase
{
    public AireVideoRequest Request { get; }

    public string RequestId { get; private set; } = "";
    public string StatusUrl { get; private set; } = "";
    public string CancelUrl { get; private set; } = "";
    public string PublicImageUrl { get; private set; } = "";
    public string OutputFile { get; private set; } = "";
    public string InputResolution { get; private set; } = "unknown";
    /// <summary>A sentence the summary should carry even on success, e.g. that a late cancel was ignored.</summary>
    public string Note { get; private set; } = "";
    /// <summary>The vendor's output URL, kept for the log — outputs are retained about a week.</summary>
    public string VideoUrl { get; private set; } = "";

    /// <summary>When the job started, and when the vendor was last asked — so the window can show elapsed
    /// time and "last checked N s ago" while a long generation is otherwise silent.</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? LastPollUtc { get; private set; }
    public int PollCount { get; private set; }

    /// <summary>Polling ceiling. Generations take minutes; anything past this is reported, with the request id,
    /// rather than waited on forever.</summary>
    public static TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(30);

    public AireVideoJob(AireVideoRequest request)
    {
        Request = request;
        Status = "uploading";
    }

    public override string Kind => "video";

    public override string Summary => Status switch
    {
        "uploading" => "uploading a render for a video clip",
        "queued" => "a video clip is queued at Higgsfield",
        "in_progress" => "Higgsfield is generating a video clip",
        "downloading" => "downloading a finished video clip",
        _ => "generating a video clip",
    };

    public override bool IsFinished => Status is "completed" or "failed" or "nsfw" or "canceled";

    /// <summary>True only while cancelling would actually stop the spend — see the class remarks.</summary>
    public bool CanCancel => !IsFinished && !CancelRequested && Status is "uploading" or "queued";

    /// <summary>True once the vendor has started generating: the clip will be charged whatever happens next.</summary>
    public bool Committed => Status is "in_progress" or "downloading" or "completed";

    /// <summary>What this clip cost: the vendor's estimate when it completed, otherwise nothing (failed, nsfw
    /// and queued-cancelled requests are all unbilled).</summary>
    public string CostUsdText => Status == "completed" ? Request.EstimatedUsdText : "0";
    public string CostCreditsText => Status == "completed" ? Request.EstimatedCreditsText : "0";

    internal Task Start(HiggsfieldCredentials credentials) => Task.Run(() => RunAsync(credentials));

    private async Task RunAsync(HiggsfieldCredentials creds)
    {
        var clock = Stopwatch.StartNew();
        string logPath = "";
        try
        {
            Directory.CreateDirectory(Request.OutputFolder);
            var logsFolder = Path.Combine(Request.OutputFolder, "logs");
            Directory.CreateDirectory(logsFolder);
            logPath = Path.Combine(logsFolder, $"video_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var (_, w, h) = AireEngine.EstimateImageTokensFromFile(Request.SourceImage);
            InputResolution = w.HasValue && h.HasValue ? $"{w}x{h}" : "unknown";

            // 1. Upload — free, idempotent, cancellable. Nothing has been asked of the vendor yet.
            Status = "uploading";
            Notify();
            Cts.Token.ThrowIfCancellationRequested();
            PublicImageUrl = !string.IsNullOrEmpty(Request.CachedPublicUrl)
                ? Request.CachedPublicUrl
                : await HiggsfieldClient.UploadFileAsync(creds, Request.SourceImage, Cts.Token).ConfigureAwait(false);

            // 2. Submit — NOT cancellable mid-flight on purpose: a token that fires after the vendor enqueued
            //    the request but before we read the request_id would leave a paid clip nobody can find.
            Cts.Token.ThrowIfCancellationRequested();
            var body = Request.Model.BuildBody(Request.Prompt, PublicImageUrl, Request.Parameters, Request.Motions);
            var submitted = await HiggsfieldClient.SubmitAsync(creds, Request.Model.Path, body, CancellationToken.None)
                .ConfigureAwait(false);
            RequestId = submitted.RequestId;
            StatusUrl = submitted.StatusUrl;
            CancelUrl = submitted.CancelUrl;
            Status = submitted.Status.Length > 0 ? submitted.Status : "queued";
            Notify();

            // 3. Poll to a terminal state. A cancel here becomes a remote cancel — see WaitAsync.
            var final = await WaitAsync(creds).ConfigureAwait(false);

            // 4. Land it.
            switch (final.Status)
            {
                case "completed":
                    Status = "downloading";
                    Notify();
                    VideoUrl = final.VideoUrl ?? "";
                    if (VideoUrl.Length == 0)
                        throw new InvalidOperationException(
                            "Higgsfield reported the clip complete but returned no video URL (unexpected response shape).");
                    OutputFile = NextOutputPath();
                    // The clip is paid for; finish the download even if the user has since asked to cancel.
                    await HiggsfieldClient.DownloadAsync(creds, VideoUrl, OutputFile, CancellationToken.None).ConfigureAwait(false);
                    Status = "completed";
                    break;
                case "nsfw":
                    Status = "nsfw";
                    Error = "Higgsfield's content filter rejected this request (status nsfw). Nothing was charged. "
                            + "Architectural renders rarely trip it — try rewording the prompt.";
                    break;
                case "canceled":
                    Status = "canceled";
                    Error = "Cancelled while still queued. Higgsfield refunds a request cancelled before it starts.";
                    break;
                default:
                    Status = "failed";
                    Error = (string.IsNullOrWhiteSpace(final.Error) ? "Generation failed" : final.Error)
                            + " (Higgsfield status: " + final.Status + "). Failed requests are not charged.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Status = "canceled";
            Error = "Cancelled before anything was sent to Higgsfield. Nothing was charged.";
        }
        catch (Exception ex)
        {
            Status = "failed";
            Error = ex.Message;
        }

        TotalTimeSeconds = clock.Elapsed.TotalSeconds;
        try
        {
            if (logPath.Length > 0)
            {
                WriteCsv(logPath);
                LogFile = logPath;
            }
        }
        catch (Exception ex)
        {
            Note = (Note.Length > 0 ? Note + " " : "") + $"The CSV log could not be written: {ex.Message}";
        }
        Notify();
    }

    /// <summary>
    ///     Polls with the vendor's recommended backoff (2 s, ×1.5, cap 10 s, plus jitter). If the job's token
    ///     fires while queued, a remote cancel is posted and polling continues with an uncancellable token until
    ///     the vendor confirms — or answers 400, meaning generation had begun, in which case the clip is
    ///     waited for and the late cancel recorded in <see cref="Note"/>.
    /// </summary>
    private async Task<HiggsfieldRequestStatus> WaitAsync(HiggsfieldCredentials creds)
    {
        try
        {
            return await PollAsync(creds, Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (RequestId.Length > 0)
        {
            try
            {
                await HiggsfieldClient.CancelAsync(creds, CancelUrl, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HiggsfieldApiException ex) when (ex.Kind == HiggsfieldErrorKind.CannotCancel)
            {
                Note = "Cancel arrived after Higgsfield had started generating, so it could not be stopped; "
                       + "the clip finished and was charged.";
                Status = "in_progress";
                Notify();
            }
            return await PollAsync(creds, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<HiggsfieldRequestStatus> PollAsync(HiggsfieldCredentials creds, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var delay = TimeSpan.FromSeconds(2);
        var rng = new Random();
        while (true)
        {
            await Task.Delay(delay + TimeSpan.FromMilliseconds(rng.Next(0, 500)), ct).ConfigureAwait(false);
            var s = await HiggsfieldClient.GetStatusAsync(creds, StatusUrl, ct).ConfigureAwait(false);
            LastPollUtc = DateTime.UtcNow;
            PollCount++;
            if (s.Status.Length > 0 && s.Status != Status && !s.IsTerminal)
            {
                Status = s.Status;
                Notify();
            }
            if (s.IsTerminal) return s;
            if (started.Elapsed > MaxWait)
                throw new TimeoutException(
                    $"Higgsfield had not finished after {MaxWait.TotalMinutes:0} minutes. The request may still complete "
                    + $"on their side — request id {RequestId}; check cloud.higgsfield.ai.");
            delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 1.5));
        }
    }

    /// <summary>"&lt;stem&gt;_clip.mp4" beside the enhance outputs; a second clip from the same render gets _clip_2.</summary>
    private string NextOutputPath()
    {
        var stem = Path.GetFileNameWithoutExtension(Request.SourceImage);
        var first = Path.Combine(Request.OutputFolder, stem + "_clip.mp4");
        if (!File.Exists(first)) return first;
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(Request.OutputFolder, $"{stem}_clip_{i}.mp4");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void WriteCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("source_file,output_file,input_resolution,model,duration,resolution,aspect_ratio,motions,"
                      + "request_id,status,time_seconds,estimated_credits,estimated_cost_usd,charged_cost_usd,error_message");
        var motions = string.Join("; ", Request.Motions.Select(m =>
            $"{(m.Name.Length > 0 ? m.Name : m.Id)} @ {m.Strength.ToString("0.00", CultureInfo.InvariantCulture)}"));
        sb.AppendLine(string.Join(",",
            Csv(Request.SourceImage), Csv(OutputFile), Csv(InputResolution), Csv(Request.Model.Path),
            Csv(Request.ParameterText("duration")), Csv(Request.ParameterText("resolution")),
            Csv(Request.ParameterText("aspect_ratio")), Csv(motions), Csv(RequestId), Csv(Status),
            Math.Round(TotalTimeSeconds, 2).ToString(CultureInfo.InvariantCulture),
            Csv(Request.EstimatedCreditsText), Csv(Request.EstimatedUsdText), Csv(CostUsdText),
            Csv(Error.Length > 0 ? Error : Note)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}

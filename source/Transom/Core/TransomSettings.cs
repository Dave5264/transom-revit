using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Transom.Core;

/// <summary>Persisted add-in settings (bridge port + Claude exchange folder), stored under %AppData%\Transom.</summary>
public sealed class TransomSettings
{
    /// <summary>Port for Transom's own in-process Claude-assist bridge (loopback TcpListener). High port,
    /// user-configurable. This is the ONLY bridge port — the retired external-pyRevit probe port (48884) was
    /// removed (G1): Transom never listened on it, so probing it gave false offline/false positive readings.</summary>
    public int BridgeSelfHostPort { get; set; } = 48810;

    public string ExchangeFolder { get; set; } = "";

    /// <summary>Occasionally show a cheerful message after an action. On by default — toggle in Settings.</summary>
    public bool EncouragingMessages { get; set; } = true;

    /// <summary>The bridge port the MCP shim was last auto-registered with (0 = never). Drives one-time
    /// first-run registration of the bundled shim with Claude, and re-registration when the port changes.</summary>
    public int McpRegisteredPort { get; set; }

    /// <summary>Master Claude-Assist switch (Settings toggle). While true the bridge auto-starts with Revit,
    /// exports write a run-log to the exchange folder, and grouped built-in edits may be staged as a Claude
    /// group-edits JSON on Apply. (The old stage/Finalize EXPORT flow was removed 2026-07-12, user-directed.)
    /// Replaces the old unpersisted Off/Verify/Assist "Claude mode" (true = the old Assist).</summary>
    public bool ClaudeAssistEnabled { get; set; }

    // ---- Revision Narrative: conventions that were compiled in as one practice's house style (GEN-01..GEN-04).
    // Every one of these defaults to empty, which means "use the built-in default" — so an installation that
    // never touches settings.json behaves exactly as before. They exist so another practice, jurisdiction or
    // language can adapt the narrative without a rebuild; there is deliberately no Settings UI for them (they
    // are set-once, per-firm values, not things a user changes between runs).

    /// <summary>Boilerplate intro sentence. <c>{title}</c> and <c>{date}</c> are substituted with the
    /// referenced plan set's title and date. Empty = the built-in US-English addendum phrasing
    /// ("The drawings for the above-referenced project titled {title}, dated {date} are revised by, but not
    /// limited to, the following items:").</summary>
    public string RevisionIntroTemplate { get; set; } = "";

    /// <summary>Sheet-number prefix → discipline name, as <c>"PREFIX=Discipline"</c> entries. LIST ORDER IS THE
    /// DISCIPLINE ORDER IN THE DOCUMENT, and longer prefixes are matched first regardless of position, so
    /// <c>"FP=Fire Protection"</c> wins over <c>"F=…"</c>. Sheets matching nothing fall under "Other", which is
    /// always listed last. Empty = the built-in map (A/G→Architectural, S, M, E, P, C, L, FP, FA) — note it files
    /// G (general) under Architectural, which matches common US narrative practice but is a choice, not a rule.
    /// Example for a practice using AD for demolition and T for telecom:
    /// <c>["A=Architectural","AD=Architectural","G=Architectural","S=Structural","T=Telecom"]</c>.</summary>
    public List<string> RevisionDisciplineMap { get; set; } = new();

    /// <summary>Title-block parameter names tried, in order, for the building name. Empty = just
    /// <c>"Building Name"</c>. A template that calls it <c>BuildingName</c>, <c>Building_Name</c> or a localized
    /// equivalent otherwise silently falls through to Project Information. The built-in Project Information
    /// "Building Name" is always tried last, whatever this list contains.</summary>
    public List<string> RevisionBuildingNameParameters { get; set; } = new();

    /// <summary>Words the title-caser leaves lowercase when they aren't the first word. Empty = the built-in
    /// English connector list. The built-in list also carries the Romance particles "de" and "la", which
    /// mis-lowercase English proper nouns ("DE SOTO AVENUE" → "de Soto Avenue"); override this to commit to
    /// one language.</summary>
    public List<string> RevisionTitleCaseSmallWords { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transom", "settings.json");

    public static TransomSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<TransomSettings>(File.ReadAllText(FilePath)) ?? new TransomSettings();
        }
        catch { /* fall through to defaults */ }
        return new TransomSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write-to-temp + rename: WriteAllText truncates in place, so a crash/kill/full-disk
            // mid-write leaves a torn settings.json that Load() silently replaces with defaults —
            // the user's port, exchange folder and ClaudeAssistEnabled all quietly reset.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* settings are best-effort */ }
    }
}

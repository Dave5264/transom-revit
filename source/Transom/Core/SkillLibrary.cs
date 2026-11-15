using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Transom.Core;

/// <summary>One skill in the library — a list row on the Claude Skills tab.</summary>
/// <remarks>These MUST stay auto-properties, not fields: the tab's ListBox ItemTemplate binds Name and
/// FileName, and WPF data binding cannot bind to a field — it resolves to nothing and the rows render
/// blank (the 2026-07-18 tab shipped with fields and showed an empty-looking list).</remarks>
public sealed class SkillEntry
{
    /// <summary>Frontmatter `name:` when present, else the folder or file name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The row hint: "&lt;folder&gt;\SKILL.md" for a folder skill, the .md file name for a flat one.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Full path of the skill's markdown (SKILL.md for a folder skill) — what Stage copies.</summary>
    public string Path { get; set; } = "";
    /// <summary>Frontmatter `description:` (the tab's detail pane); body fallback.</summary>
    public string Description { get; set; } = "";
}

/// <summary>
///     The Claude Skills library: reusable Claude workflow instructions with YAML frontmatter (`name:` / `description:`),
///     listed on the Hub's Claude Skills tab. Since v1.9.20 a shipped skill is a FOLDER (SKILL.md plus scripts/,
///     reference/, hooks/), and the shipped set is a hierarchy: `transom` is the index that routes to `transom-model`
///     (and its eleven stage skills), `transom-ui`, `transom-schedules` and `transom-elevation-tags`.
///     <para>On startup <see cref="EnsureSeeded"/> installs every shipped skill into two places:
///     the Transom library (%LocalAppData%\Transom\skills — the Hub tab, the Stage button) and Claude Code's personal
///     skills folder (%USERPROFILE%\.claude\skills), where Claude finds them by their descriptions without the user
///     pasting a path. Updates replace a file only when the user has not edited it (see <see cref="SyncFolder"/>).</para>
///     Flat .md files (the pre-1.9.20 format, and anything the user imports) are still listed.
/// </summary>
public static class SkillLibrary
{
    /// <summary>The per-user library folder, alongside the other Transom per-user state (mcp, bridge.token).</summary>
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transom", "skills");

    /// <summary>Claude Code's personal skills folder. Only used when %USERPROFILE%\.claude exists (Claude Code is
    /// installed); Transom never creates .claude on a machine that has no Claude Code.</summary>
    public static string ClaudeCodeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

    /// <summary>Per-destination record of what Transom last installed: relative path → SHA-256 of the shipped file.
    /// A destination file whose hash still equals its recorded hash is unedited and may be replaced or removed.</summary>
    private const string ManifestName = ".transom-shipped.json";

    /// <summary>The flat skills shipped before v1.9.20, with the SHA-256 of every version that ever shipped (LF and CRLF, since a
    /// Windows checkout ships CRLF). An
    /// installed copy that matches is the unedited shipped file, so it is removed now that the skill lives in a folder
    /// (`transom-elevation-tags`, `transom-schedules`); an edited copy is left alone.</summary>
    private static readonly Dictionary<string, string[]> LegacyFlat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["elevation-door-window-tags.md"] = new[] { "89CA96B3B19EE98F71FAA57C04D51F4628C2AED5FD88C275D351B258B4EB69AC", "7A73C2F38DB502B0ECF22D06B50B5A15D250CA8AB9D1EAE7695942B105773A5A", "D59AC9C01F2CC81070F174903BD77D8E61B7CABD54F9722941C5EB7EDD676809", "D82C26121BAEE338C2A95AF38AA68B5B19B34277F1BCA84A7D9CD22B4D92BAC5" },
        ["schedule-inventory.md"] = new[] { "CF9D1A10B33D4B93DA4F3D7009B5EC7BBCBA48EC807E1FD52CFA5412B6F75C5B", "0C80CF94940EEB97CC632ACC3BEC22831D031B92B9DA9A5E3AAD6758AFEAA667", "A71429BC63E4D18C62FD984F8CB1BED4C9C95702AAAE8787CA67BC8927E93FFD", "3339A3FE0C17C0F2AB2B0E50BC0EC2548E4DB834FC808B06C6CA7DDD7450A75E" },
    };

    /// <summary>Installs the shipped skills (a "skills" folder next to Transom.dll) into the library and, when Claude
    /// Code is installed, into its skills folder. Never throws: seeding is best-effort and the tab shows what exists.</summary>
    public static void EnsureSeeded()
    {
        try
        {
            var payload = Path.Combine(Path.GetDirectoryName(typeof(SkillLibrary).Assembly.Location) ?? "", "skills");
            Seed(payload, Dir, Directory.Exists(Path.GetDirectoryName(ClaudeCodeDir)) ? ClaudeCodeDir : null);
        }
        catch { /* best-effort seeding — the tab just shows what is there */ }
    }

    /// <summary>The seeding itself, with explicit folders so it can be exercised against a scratch tree.
    /// Returns one line per action for the log.</summary>
    public static List<string> Seed(string payload, string libraryDir, string? claudeDir)
    {
        var log = new List<string>();
        Directory.CreateDirectory(libraryDir);
        if (!Directory.Exists(payload)) { log.Add("no payload at " + payload); return log; }
        foreach (var skill in Directory.EnumerateDirectories(payload))
        {
            if (!File.Exists(Path.Combine(skill, "SKILL.md"))) continue;
            var name = Path.GetFileName(skill);
            log.AddRange(SyncFolder(skill, Path.Combine(libraryDir, name)));
            if (claudeDir != null) log.AddRange(SyncFolder(skill, Path.Combine(claudeDir, name)));
        }
        // flat .md files in the payload root (older format) are seeded into the library only, never overwritten
        foreach (var src in Directory.EnumerateFiles(payload, "*.md"))
        {
            var dst = Path.Combine(libraryDir, Path.GetFileName(src));
            if (!File.Exists(dst)) { File.Copy(src, dst); log.Add("seeded " + dst); }
        }
        // retire the unedited pre-1.9.20 flat copies that now live in folders
        foreach (var kv in LegacyFlat)
        {
            var old = Path.Combine(libraryDir, kv.Key);
            if (File.Exists(old) && kv.Value.Contains(Sha256(old), StringComparer.OrdinalIgnoreCase))
            {
                File.Delete(old);
                log.Add("retired " + old + " (moved into a skill folder)");
            }
        }
        return log;
    }

    /// <summary>Makes <paramref name="dst"/> match the shipped <paramref name="src"/> folder WITHOUT losing edits:
    /// <list type="bullet">
    /// <item>a missing file is copied;</item>
    /// <item>a file whose hash equals what Transom last shipped (unedited) is replaced by the new version;</item>
    /// <item>a file the user edited (hash differs from the record) is kept, and reported;</item>
    /// <item>a file Transom shipped before but no longer ships is deleted only if unedited.</item>
    /// </list>
    /// The record lives in <c>.transom-shipped.json</c> inside the destination folder.</summary>
    public static List<string> SyncFolder(string src, string dst)
    {
        var log = new List<string>();
        Directory.CreateDirectory(dst);
        var manifestPath = Path.Combine(dst, ManifestName);
        var manifest = ReadManifest(manifestPath);
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            var shipped = Sha256(file);
            next[rel] = shipped;
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                log.Add("installed " + target);
                continue;
            }
            var current = Sha256(target);
            if (string.Equals(current, shipped, StringComparison.OrdinalIgnoreCase)) continue;   // already current
            if (manifest.TryGetValue(rel, out var recorded) && string.Equals(current, recorded, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, target, overwrite: true);
                log.Add("updated " + target);
            }
            else
            {
                next[rel] = manifest.TryGetValue(rel, out var keep) ? keep : "";   // keep the user's edit; remember the old record
                log.Add("kept the user's edit " + target);
            }
        }
        foreach (var kv in manifest.Where(kv => !next.ContainsKey(kv.Key)))
        {
            var target = Path.Combine(dst, kv.Key);
            if (File.Exists(target) && string.Equals(Sha256(target), kv.Value, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                log.Add("removed " + target + " (no longer shipped)");
            }
        }
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        return log;
    }

    private static Dictionary<string, string> ReadManifest(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new Dictionary<string, string>();
        }
        catch { /* a broken record: treat every file as possibly edited (nothing gets overwritten) */ }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Every skill in the library — folder skills (their SKILL.md) and flat .md files — name-sorted, with the
    /// frontmatter parsed for the list and detail pane.</summary>
    public static List<SkillEntry> List()
    {
        var result = new List<SkillEntry>();
        try
        {
            Directory.CreateDirectory(Dir);
            var files = Directory.EnumerateDirectories(Dir)
                .Select(d => Path.Combine(d, "SKILL.md")).Where(File.Exists)
                .Concat(Directory.EnumerateFiles(Dir, "*.md"));
            foreach (var f in files)
            {
                var (name, desc) = ParseFrontmatter(f);
                bool folder = string.Equals(Path.GetFileName(f), "SKILL.md", StringComparison.OrdinalIgnoreCase);
                var fallback = folder ? Path.GetFileName(Path.GetDirectoryName(f)!) : Path.GetFileNameWithoutExtension(f);
                result.Add(new SkillEntry
                {
                    Name = string.IsNullOrWhiteSpace(name) ? fallback : name,
                    FileName = folder ? fallback + "\\SKILL.md" : Path.GetFileName(f),
                    Path = f,
                    Description = desc,
                });
            }
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        }
        catch { /* unreadable folder → empty list; the tab's status line reports individual op failures */ }
        return result;
    }

    /// <summary>Copies a skill file into the library, uniquifying on a name collision (never overwrites —
    /// an existing skill may be the user's edited copy). Returns the destination path.</summary>
    public static string Import(string sourcePath)
    {
        Directory.CreateDirectory(Dir);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var dst = Path.Combine(Dir, baseName + ".md");
        for (int i = 2; File.Exists(dst); i++) dst = Path.Combine(Dir, $"{baseName} ({i}).md");
        File.Copy(sourcePath, dst);
        return dst;
    }

    /// <summary>Deletes a skill (the tab confirms with the user first): a folder skill's whole folder, or a flat file.
    /// A shipped skill comes back on the next start; removing it from Claude Code is a separate, manual step.</summary>
    public static void Remove(string path)
    {
        if (string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        else
            File.Delete(path);
    }

    /// <summary>Reads the leading YAML frontmatter block for `name:` / `description:`. A skill without a
    /// frontmatter description falls back to the first non-blank, non-heading body line, so hand-written
    /// files still show something useful in the detail pane.</summary>
    private static (string name, string description) ParseFrontmatter(string path)
    {
        try
        {
            var lines = File.ReadLines(path).Take(80).ToList();
            string name = "", desc = "";
            int bodyStart = 0;
            if (lines.Count > 0 && lines[0].Trim() == "---")
            {
                for (int i = 1; i < lines.Count; i++)
                {
                    var l = lines[i].Trim();
                    if (l == "---") { bodyStart = i + 1; break; }
                    if (l.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                        name = Unquote(l.Substring(5));
                    else if (l.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                        desc = Unquote(l.Substring(12));
                }
            }
            if (string.IsNullOrWhiteSpace(desc))
                desc = lines.Skip(bodyStart)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#") && l != "---") ?? "";
            return (name, desc);
        }
        catch { return ("", ""); }
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\''))
            s = s.Substring(1, s.Length - 2);
        return s.Trim();
    }
}

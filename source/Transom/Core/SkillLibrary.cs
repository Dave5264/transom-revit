using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Transom.Core;

/// <summary>One skill file in the library — a list row on the Claude Skills tab.</summary>
public sealed class SkillEntry
{
    public string Name = "";        // frontmatter `name:` when present, else the file name without extension
    public string FileName = "";    // the .md file name (shown as the row hint; what Stage copies refers to)
    public string Path = "";        // full path inside the library folder
    public string Description = ""; // frontmatter `description:` (the tab's detail pane); body fallback
}

/// <summary>
///     The Claude Skills library (user request 2026-07-18): a per-user folder of skill .md files — reusable
///     Claude workflow instructions with YAML frontmatter (`name:` / `description:`) — listed on the Hub's
///     Claude Skills tab. Skills shipped in the add-in payload's "skills" subfolder are seeded into the
///     library on startup; Claude (told how via the staged-edits / Assist guides) and the tab's Import button
///     add more. Stage copies a skill's path to the clipboard for pasting into Claude Code.
/// </summary>
public static class SkillLibrary
{
    /// <summary>The per-user library folder, alongside the other Transom per-user state (mcp, bridge.token).</summary>
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transom", "skills");

    /// <summary>Creates the library folder and copies any skills shipped with the add-in (a "skills" subfolder
    /// next to Transom.dll) that aren't already present — same first-launch pattern as the bundled MCP shim.
    /// Existing files are never overwritten (the user may have edited a shipped skill). Never throws.</summary>
    public static void EnsureSeeded()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var payload = Path.Combine(
                Path.GetDirectoryName(typeof(SkillLibrary).Assembly.Location) ?? "", "skills");
            if (!Directory.Exists(payload)) return;
            foreach (var src in Directory.EnumerateFiles(payload, "*.md"))
            {
                var dst = Path.Combine(Dir, Path.GetFileName(src));
                if (!File.Exists(dst)) File.Copy(src, dst);
            }
        }
        catch { /* best-effort seeding — the tab just shows an empty library */ }
    }

    /// <summary>Every .md in the library, name-sorted, with its frontmatter parsed for the list + detail pane.</summary>
    public static List<SkillEntry> List()
    {
        var result = new List<SkillEntry>();
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var f in Directory.EnumerateFiles(Dir, "*.md")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var (name, desc) = ParseFrontmatter(f);
                result.Add(new SkillEntry
                {
                    Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(f) : name,
                    FileName = Path.GetFileName(f),
                    Path = f,
                    Description = desc,
                });
            }
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

    /// <summary>Deletes a skill file (the tab confirms with the user first).</summary>
    public static void Remove(string path) => File.Delete(path);

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

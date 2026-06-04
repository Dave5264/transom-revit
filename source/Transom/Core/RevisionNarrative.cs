using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace Transom.Core;

/// <summary>
///     Stand-alone (no Claude/MCP) builder for a Revision Narrative. Given a selected revision it collects
///     every revision cloud, reads each cloud's <c>Comments</c> field, resolves the sheet(s) the cloud
///     appears on plus the view's detail number on each sheet, groups by discipline (sheet-number prefix)
///     then by sheet, orders the notes, and normalizes the text into the firm's narrative style.
///
///     Data-model findings it must tolerate (validated live on SAMPLE PROJECT, Addendum A, 67 clouds):
///       • clouds live inside views (46) OR directly on sheets (21) — both resolved via GetSheetIds();
///       • a cloud can appear on MORE THAN ONE sheet (dependent views) — the note is listed under each (#5);
///       • a cloud can appear on NO sheet (legend/working view) — skipped and reported, never dropped silently;
///       • a detail number is OFTEN absent (on-sheet clouds, dependent views) — ordering falls back to
///         the cloud Mark, then element id.
/// </summary>
public static class RevisionNarrative
{
    // ---- options the caller can override (some have no Revit field; sensible defaults / prompts) ----
    public sealed class Options
    {
        /// <summary>Plan-set title used in the boilerplate sentence (no native Revit field — defaulted/prompted).</summary>
        public string PlanSetTitle = "Permit Plans";
        /// <summary>Plan-set date used in the boilerplate sentence.</summary>
        public string PlanSetDate = "";
        /// <summary>Firm name prefixing the project number line, e.g. "Sample Firm".</summary>
        public string FirmName = "Sample Firm";
        /// <summary>Normalize comment text to house style (sentence case + trailing period). Answer #4 = yes.</summary>
        public bool Normalize = true;
    }

    // ---- output model (consumed by RevisionNarrativeDocxWriter) ----
    public sealed class Note { public string Text = ""; public string DetailNumber = ""; public string Mark = ""; public long CloudId; }
    public sealed class Sheet { public string Number = ""; public string Name = ""; public readonly List<Note> Notes = new(); }
    public sealed class Discipline { public string Name = ""; public readonly List<Sheet> Sheets = new(); }

    public sealed class Data
    {
        public string IssueDate = "";
        public string AddendumLabel = "";
        public string BuildingName = "";   // shown above ProjectName when present (and used in the title block)
        public string ProjectName = "";
        public List<string> AddressLines = new();
        public string ProjectNumberLine = "";
        public string IntroSentence = "";
        public readonly List<Discipline> Disciplines = new();
        public readonly List<string> Warnings = new();
    }

    // Sheet-number prefix -> discipline name. G (general) is filed under Architectural to match the firm's
    // existing narratives. Order of this list is the discipline order in the document.
    private static readonly (string Prefix, string Name)[] DisciplineMap =
    {
        ("A", "Architectural"), ("G", "Architectural"),
        ("S", "Structural"),
        ("M", "Mechanical"),
        ("E", "Electrical"),
        ("P", "Plumbing"),
        ("C", "Civil"),
        ("L", "Landscape"),
        ("FP", "Fire Protection"), ("FA", "Fire Alarm"),
    };
    private static readonly string[] DisciplineOrder =
        { "Architectural", "Structural", "Mechanical", "Electrical", "Plumbing", "Civil", "Landscape", "Fire Protection", "Fire Alarm", "Other" };

    public static Data Build(Document doc, ElementId revisionId, Options opts)
    {
        opts ??= new Options();
        var data = new Data();

        var rev = doc.GetElement(revisionId) as Revision;
        if (rev == null) { data.Warnings.Add("Revision not found."); return data; }

        // ---- header metadata (#6: pull from Revit) ----
        var pinfo = doc.ProjectInformation;
        // Building name is read FROM the title block, so it only appears when it's actually used there
        // (and uses the exact value shown on the sheets). Empty => not shown.
        data.BuildingName = SentenceCase(BuildingNameFromTitleBlock(doc));
        data.ProjectName = SentenceCase(SafeStr(() => pinfo?.Name));
        foreach (var line in SafeStr(() => pinfo?.Address).Replace("\r", "").Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) data.AddressLines.Add(SentenceCase(line.Trim()));
        var projNo = SafeStr(() => pinfo?.Number);
        data.ProjectNumberLine = $"{opts.FirmName} Project Number: {projNo}";

        data.AddendumLabel = SentenceCase(SafeStr(() => rev.Description));
        var revDate = SafeStr(() => rev.RevisionDate);
        data.IssueDate = FormatDate(revDate);
        if (string.IsNullOrWhiteSpace(opts.PlanSetDate)) opts.PlanSetDate = revDate;
        data.IntroSentence =
            $"The drawings for the above-referenced project titled {opts.PlanSetTitle}, dated {opts.PlanSetDate} " +
            "are revised by, but not limited to, the following items:";

        // ---- collect clouds for this revision ----
        var clouds = new FilteredElementCollector(doc)
            .OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>()
            .Where(c => c.RevisionId == revisionId)
            .ToList();

        // Precompute viewport placements once: a view (AND its dependents, keyed by the primary's id) ->
        // [(sheet, detail number)]. This is what lets us (a) list the detail number for a cloud that sits in
        // a viewport, (b) resolve dependent-view placements, and (c) find the sheet a LEGEND is placed on.
        var byView = BuildViewportIndex(doc);

        int noComment = 0, orphan = 0;
        // discipline name -> (sheet key -> Sheet)
        var byDiscipline = new Dictionary<string, Dictionary<string, Sheet>>();

        foreach (var c in clouds)
        {
            var comment = NormalizeText(c.LookupParameter("Comments")?.AsString() ?? "", opts.Normalize);
            if (string.IsNullOrWhiteSpace(comment)) { noComment++; continue; }
            var mark = SafeStr(() => c.LookupParameter("Mark")?.AsString());

            // Resolve the sheet placement(s) for this cloud, each with its detail number where discoverable.
            // sheetId.Value -> (sheetId, detail) ; detail "" means "on a sheet but no discernible detail".
            var placements = new Dictionary<long, (ElementId sheet, string detail)>();
            if (byView.TryGetValue(c.OwnerViewId.Value, out var vpList))
                foreach (var (s, d) in vpList) placements[s.Value] = (s, d);
            ISet<ElementId> getSheets;
            try { getSheets = c.GetSheetIds(); } catch { getSheets = new HashSet<ElementId>(); }
            foreach (var sid in getSheets)
                if (!placements.ContainsKey(sid.Value)) placements[sid.Value] = (sid, ""); // on sheet, detail unknown -> [insert]
            if (doc.GetElement(c.OwnerViewId) is ViewSheet ownerSheet && !placements.ContainsKey(ownerSheet.Id.Value))
                placements[ownerSheet.Id.Value] = (ownerSheet.Id, ""); // cloud drawn directly on the sheet -> [insert]

            if (placements.Count == 0) { orphan++; data.Warnings.Add($"Cloud {c.Id.Value} (\"{Trunc(comment)}\") could not be attributed to a sheet — skipped."); continue; }

            foreach (var kv in placements)
            {
                if (doc.GetElement(kv.Value.sheet) is not ViewSheet sh) continue;
                var number = SafeStr(() => sh.SheetNumber);
                var name = SafeStr(() => sh.Name);

                var disc = DisciplineFor(number);
                if (!byDiscipline.TryGetValue(disc, out var sheetMap)) { sheetMap = new Dictionary<string, Sheet>(); byDiscipline[disc] = sheetMap; }
                if (!sheetMap.TryGetValue(number, out var sheet)) { sheet = new Sheet { Number = number, Name = name }; sheetMap[number] = sheet; }
                sheet.Notes.Add(new Note { Text = comment, DetailNumber = kv.Value.detail, Mark = mark, CloudId = c.Id.Value });
            }
        }

        if (noComment > 0) data.Warnings.Add($"{noComment} cloud(s) for this revision have no Comments text — not included. Populate the cloud's Comments field to list them.");
        if (orphan > 0) data.Warnings.Add($"{orphan} cloud(s) could not be attributed to a sheet.");

        // ---- order: disciplines (fixed order), sheets (natural by number), notes (detail# -> mark -> id) ----
        foreach (var discName in DisciplineOrder)
        {
            if (!byDiscipline.TryGetValue(discName, out var sheetMap)) continue; // #7: only disciplines with clouds
            var d = new Discipline { Name = discName };
            foreach (var sheet in sheetMap.Values.OrderBy(s => s.Number, NaturalComparer.Instance))
            {
                var ordered = sheet.Notes
                    .OrderBy(n => DetailSortKey(n.DetailNumber))
                    .ThenBy(n => n.Mark, NaturalComparer.Instance)
                    .ThenBy(n => n.CloudId)
                    .ToList();
                var s = new Sheet { Number = sheet.Number, Name = sheet.Name };
                s.Notes.AddRange(ordered);
                d.Sheets.Add(s);
            }
            data.Disciplines.Add(d);
        }
        return data;
    }

    private static string DisciplineFor(string sheetNumber)
    {
        var n = (sheetNumber ?? "").TrimStart().ToUpperInvariant();
        // two-letter prefixes first (FP/FA), then single-letter
        foreach (var (prefix, name) in DisciplineMap.OrderByDescending(m => m.Prefix.Length))
            if (n.StartsWith(prefix, StringComparison.Ordinal)) return name;
        return "Other";
    }

    /// <summary>
    ///     Maps every placed view -> [(sheet, detail number)] by scanning all viewports once. Dependent views
    ///     are ALSO indexed under their primary view's id, so a cloud owned by the primary resolves to the
    ///     dependent's sheet + detail number. This is what makes detail numbers, dependent-view placements, and
    ///     legend-on-sheet placements all resolvable.
    /// </summary>
    private static Dictionary<long, List<(ElementId sheet, string detail)>> BuildViewportIndex(Document doc)
    {
        var map = new Dictionary<long, List<(ElementId, string)>>();
        void Add(long key, ElementId sheet, string detail)
        {
            if (!map.TryGetValue(key, out var list)) { list = new List<(ElementId, string)>(); map[key] = list; }
            if (!list.Any(t => t.Item1 == sheet)) list.Add((sheet, detail));
        }
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
        {
            ElementId sheetId, viewId;
            try { sheetId = vp.SheetId; viewId = vp.ViewId; } catch { continue; }
            if (sheetId == ElementId.InvalidElementId || viewId == ElementId.InvalidElementId) continue;
            var detail = "";
            try { detail = vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString() ?? ""; } catch { /* no detail # */ }
            Add(viewId.Value, sheetId, detail);
            try
            {
                if (doc.GetElement(viewId) is View v)
                {
                    var primary = v.GetPrimaryViewId();
                    if (primary != ElementId.InvalidElementId && primary != viewId) Add(primary.Value, sheetId, detail);
                }
            }
            catch { /* primary view lookup unavailable */ }
        }
        return map;
    }

    // detail numbers sort numerically when possible, else lexically; blanks last.
    private static (int, int, string) DetailSortKey(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return (2, int.MaxValue, "");
        return int.TryParse(detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? (0, v, "")
            : (1, 0, detail);
    }

    private static string NormalizeText(string s, bool on)
    {
        s = (s ?? "").Trim();
        if (!on || s.Length == 0) return s;
        if (char.IsLetter(s[0]) && char.IsLower(s[0])) s = char.ToUpper(s[0]) + s.Substring(1);
        if (!s.EndsWith(".") && !s.EndsWith("!") && !s.EndsWith("?")) s += ".";
        return s;
    }

    private static string FormatDate(string raw)
    {
        if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(raw) ? DateTime.Today.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) : raw;
    }

    /// <summary>
    ///     The Building Name as it appears in the title block — i.e. the value of a "Building Name" parameter
    ///     carried by a title block instance placed on a sheet. Returns "" when no title block exposes it (so
    ///     the narrative shows the building name only when it's actually in the title block). Note: this detects
    ///     the common case where Building Name is a shared/project parameter on the title block; a title block
    ///     that labels the built-in Project Information "Building Name" directly is not detected here.
    /// </summary>
    private static string BuildingNameFromTitleBlock(Document doc)
    {
        try
        {
            var onSheets = new HashSet<long>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate).Select(s => s.Id.Value));

            foreach (var tb in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                if (tb.OwnerViewId == ElementId.InvalidElementId || !onSheets.Contains(tb.OwnerViewId.Value)) continue;
                var v = tb.LookupParameter("Building Name")?.AsString();
                if (!string.IsNullOrWhiteSpace(v)) return v!;
            }
        }
        catch { /* best-effort */ }
        return "";
    }

    /// <summary>
    ///     Sentence-cases an ALL-CAPS string (first letter + first letter after a sentence break capitalized,
    ///     standalone single letters kept capital so "ADDENDUM A" -> "Addendum A" and "...STREET N" keeps "N").
    ///     Strings that already contain lowercase are left untouched, so properly-cased Revit data is preserved.
    /// </summary>
    private static string SentenceCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        if (s.Any(char.IsLower)) return s; // already mixed case — assume entered correctly
        var c = s.ToLowerInvariant().ToCharArray();
        bool startSentence = true;
        for (int i = 0; i < c.Length; i++)
        {
            if (startSentence && char.IsLetter(c[i])) { c[i] = char.ToUpperInvariant(c[i]); startSentence = false; }
            else if (c[i] is '.' or '!' or '?') startSentence = true;
        }
        return Regex.Replace(new string(c), @"\b([a-z])\b", m => m.Groups[1].Value.ToUpperInvariant());
    }

    private static string Trunc(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;
    private static string SafeStr(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }

    /// <summary>Compares strings with embedded numbers naturally (A2 &lt; A10, S101 &lt; S202).</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = long.Parse(a.Substring(si, i - si));
                    var nb = long.Parse(b.Substring(sj, j - sj));
                    if (na != nb) return na.CompareTo(nb);
                }
                else
                {
                    int cmp = a[i].CompareTo(b[j]);
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}

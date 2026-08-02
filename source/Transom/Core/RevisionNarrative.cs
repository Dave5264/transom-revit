using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace Transom.Core;

/// <summary>
///     Stand-alone (no Claude/MCP) builder for a Revision Narrative. Given a selected revision it collects
///     every revision cloud, reads each cloud's <c>Comments</c> field, resolves the sheet(s) the cloud
///     appears on plus the view's detail number on each sheet, groups by discipline (sheet-number prefix)
///     then by sheet, orders the notes, and normalizes the text into a consistent narrative style.
///
///     Data-model findings it must tolerate (validated live on a real addendum, 67 clouds):
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
        /// <summary>Override for the boilerplate "titled …" reference. Empty = computed from the revision
        /// sequence (previous revision, or Project Status for the first revision).</summary>
        public string PlanSetTitle = "";
        /// <summary>Override for the boilerplate "dated …" reference. Empty = computed (previous revision's
        /// date, or Project Issue Date for the first revision).</summary>
        public string PlanSetDate = "";
        /// <summary>Optional firm name prefixing the project number line, e.g. "Acme Architects" →
        /// "Acme Architects Project Number: 12345". Empty (the default) omits the prefix entirely.</summary>
        public string FirmName = "";
        /// <summary>Normalize comment text to house style (sentence case + trailing period). Answer #4 = yes.</summary>
        public bool Normalize = true;

        // GEN-01..GEN-04: four conventions that used to be compiled in as one practice's house style. Each is
        // empty/null by default and falls back to the built-in value, so the out-of-the-box narrative is
        // unchanged; TransomSettings supplies overrides (see FromSettings).

        /// <summary>Intro boilerplate with <c>{title}</c>/<c>{date}</c> placeholders. Empty = built-in.</summary>
        public string IntroTemplate = "";
        /// <summary>"PREFIX=Discipline" entries; list order = discipline order. Null/empty = built-in map.</summary>
        public IReadOnlyList<string>? DisciplineMap;
        /// <summary>Title-block parameter names to try for the building name. Null/empty = "Building Name".</summary>
        public IReadOnlyList<string>? BuildingNameParameters;
        /// <summary>Words the title-caser keeps lowercase mid-sentence. Null/empty = built-in English list.</summary>
        public IReadOnlyList<string>? TitleCaseSmallWords;

        /// <summary>Reads the four overridable narrative conventions out of the persisted settings. Anything the
        /// user hasn't set stays empty, i.e. keeps the built-in default.</summary>
        public static Options FromSettings(TransomSettings s) => new()
        {
            IntroTemplate = s.RevisionIntroTemplate ?? "",
            DisciplineMap = s.RevisionDisciplineMap,
            BuildingNameParameters = s.RevisionBuildingNameParameters,
            TitleCaseSmallWords = s.RevisionTitleCaseSmallWords,
        };
    }

    // ---- output model (consumed by RevisionNarrativeDocxWriter) ----
    public sealed class Note
    {
        public string Text = "";
        public string DetailNumber = "";   // #104: group's lowest numbered detail ("" = all-blank); used as a sort key
        public string DetailLabel = "";     // #104: display list — "" all-blank, "1" single, "1, 3, 4" / "5, [insert]" multi
        public bool IsMultiDetail;          // #104: true => the prefix is the plural "Details"
        public string Mark = "";
        public long CloudId;
    }
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
        public string RefTitle = "";       // the issue being revised (exposed so a confirm UI can edit it...)
        public string RefDate = "";        // ...then recompose IntroSentence via ComposeIntro.
        public int SourceCloudCount;        // #104: distinct revision clouds that produced ≥1 note (for the count display)
        public readonly List<Discipline> Disciplines = new();
        public readonly List<string> Warnings = new();
    }

    // Sheet-number prefix -> discipline name. G (general) is filed under Architectural, which matches common
    // narrative practice. Order of this list is the discipline order in the document.
    // GEN-02: this is the DEFAULT map, not the only one — a project using AD for demolition, T for telecom, FS
    // for food service, or a non-US discipline scheme used to fall silently into "Other". Options.DisciplineMap
    // (from TransomSettings.RevisionDisciplineMap) replaces it wholesale when set.
    private static readonly (string Prefix, string Name)[] DefaultDisciplineMap =
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

    /// <summary>The prefix→discipline pairs in effect: the caller's override, else the built-in map. Malformed
    /// entries (no '=', empty prefix or name) are skipped rather than failing the whole run.</summary>
    private static (string Prefix, string Name)[] MapFor(Options opts)
    {
        if (opts.DisciplineMap == null || opts.DisciplineMap.Count == 0) return DefaultDisciplineMap;
        var pairs = new List<(string, string)>();
        foreach (var entry in opts.DisciplineMap)
        {
            var i = (entry ?? "").IndexOf('=');
            if (i <= 0) continue;
            var prefix = entry!.Substring(0, i).Trim().ToUpperInvariant();
            var name = entry.Substring(i + 1).Trim();
            if (prefix.Length > 0 && name.Length > 0) pairs.Add((prefix, name));
        }
        return pairs.Count > 0 ? pairs.ToArray() : DefaultDisciplineMap;
    }

    /// <summary>Discipline output order: the order the map lists them in (first mention wins), with "Other"
    /// always last. Derived rather than a second hardcoded list, so a custom map can't silently order-drop a
    /// discipline the way a fixed order array would.</summary>
    private static string[] DisciplineOrderFor(Options opts)
    {
        var order = new List<string>();
        foreach (var (_, name) in MapFor(opts))
            if (!order.Contains(name)) order.Add(name);
        order.Add("Other");
        return order.ToArray();
    }

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
        data.BuildingName = TitleCaseSmart(BuildingNameFromTitleBlock(doc, opts), opts);
        data.ProjectName = TitleCaseSmart(SafeStr(() => pinfo?.Name), opts);
        foreach (var line in SafeStr(() => pinfo?.Address).Replace("\r", "").Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) data.AddressLines.Add(TitleCaseSmart(line.Trim(), opts));
        var projNo = SafeStr(() => pinfo?.Number);
        data.ProjectNumberLine = string.IsNullOrWhiteSpace(opts.FirmName)
            ? $"Project Number: {projNo}"
            : $"{opts.FirmName} Project Number: {projNo}";

        data.AddendumLabel = TitleCaseSmart(SafeStr(() => rev.Description), opts);
        data.IssueDate = FormatDate(SafeStr(() => rev.RevisionDate));

        // The boilerplate references the ISSUE BEING REVISED — the previous (non-blank) revision in the
        // sequence, or, for the first revision, the original submission (Project Status + Project Issue Date).
        var (refTitleRaw, refDateRaw) = ReferencedIssue(doc, revisionId);
        data.RefTitle = TitleCaseSmart(string.IsNullOrWhiteSpace(opts.PlanSetTitle) ? refTitleRaw : opts.PlanSetTitle, opts);
        data.RefDate = string.IsNullOrWhiteSpace(opts.PlanSetDate) ? refDateRaw : opts.PlanSetDate;
        data.IntroSentence = ComposeIntro(data.RefTitle, data.RefDate, opts.IntroTemplate);

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
            var comment = NormalizeNote(c.LookupParameter("Comments")?.AsString() ?? "", opts.Normalize);
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

                var disc = DisciplineFor(number, opts);
                if (!byDiscipline.TryGetValue(disc, out var sheetMap)) { sheetMap = new Dictionary<string, Sheet>(); byDiscipline[disc] = sheetMap; }
                if (!sheetMap.TryGetValue(number, out var sheet)) { sheet = new Sheet { Number = number, Name = name }; sheetMap[number] = sheet; }
                sheet.Notes.Add(new Note { Text = comment, DetailNumber = kv.Value.detail, Mark = mark, CloudId = c.Id.Value });
            }
        }

        if (noComment > 0) data.Warnings.Add($"{noComment} cloud(s) for this revision have no Comments text — not included. Populate the cloud's Comments field to list them.");
        if (orphan > 0) data.Warnings.Add($"{orphan} cloud(s) could not be attributed to a sheet.");
        data.SourceCloudCount = clouds.Count - noComment - orphan;   // #104: clouds that produced notes (before collapse)

        // ---- order: disciplines (map order), sheets (natural by number), notes (detail# -> mark -> id) ----
        foreach (var discName in DisciplineOrderFor(opts))
        {
            if (!byDiscipline.TryGetValue(discName, out var sheetMap)) continue; // #7: only disciplines with clouds
            var d = new Discipline { Name = discName };
            foreach (var sheet in sheetMap.Values.OrderBy(s => s.Number, NaturalComparer.Instance))
            {
                var s = new Sheet { Number = sheet.Number, Name = sheet.Name };
                s.Notes.AddRange(CollapseNotes(sheet.Notes));   // #104: group same-comment notes + dedup, then order
                d.Sheets.Add(s);
            }
            data.Disciplines.Add(d);
        }
        return data;
    }

    /// <summary>The boilerplate intro sentence, composed from the referenced issue — public so the confirm
    /// dialog can recompose it after the user edits <see cref="Data.RefTitle"/> / <see cref="Data.RefDate"/>.
    /// GEN-01: the default sentence is one jurisdiction's US-English addendum phrasing; <paramref name="template"/>
    /// (from TransomSettings.RevisionIntroTemplate) replaces it, substituting {title} and {date}.</summary>
    public static string ComposeIntro(string refTitle, string refDate, string template = "")
    {
        if (!string.IsNullOrWhiteSpace(template))
            return template.Replace("{title}", refTitle).Replace("{date}", refDate);
        return $"The drawings for the above-referenced project titled {refTitle}, dated {refDate} " +
               "are revised by, but not limited to, the following items:";
    }

    private static string DisciplineFor(string sheetNumber, Options opts)
    {
        var n = (sheetNumber ?? "").TrimStart().ToUpperInvariant();
        // two-letter prefixes first (FP/FA), then single-letter
        foreach (var (prefix, name) in MapFor(opts).OrderByDescending(m => m.Prefix.Length))
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

    /// <summary>
    ///     #104: collapse a sheet's per-(cloud×placement) notes into one entry per distinct comment.
    ///       • (G) one comment across several details → a single "Details 1, 3, 4 - …" line;
    ///       • (D) the same comment repeated on the same detail (or duplicate clouds) → that detail listed once.
    ///     Detail numbers are de-duplicated and sorted (numeric-aware, blanks last). A comment that ALSO has a
    ///     blank (no-detail) placement appends a trailing plain-text "[insert]" (e.g. "Detail 5, [insert]"); a
    ///     comment with ONLY blank placements becomes an all-blank entry (DetailLabel == "") that the writer
    ///     renders with the click-to-fill content control. Plural "Details" iff the group has 2+ numbered details
    ///     (a trailing "[insert]" does NOT flip singular→plural — matches the spec's worked example). Groups are
    ///     emitted ordered by lowest detail then lowest CloudId, so a re-run is byte-stable. Collapse is per sheet
    ///     (the caller invokes it inside the per-sheet loop), so the same comment on two sheets stays under each.
    /// </summary>
    private static List<Note> CollapseNotes(List<Note> notes)
    {
        // Group by comment text, preserving first-appearance order (ordinal — normalization already unified casing,
        // so a second case-fold would wrongly merge genuinely different notes).
        var order = new List<string>();
        var groups = new Dictionary<string, List<Note>>(StringComparer.Ordinal);
        foreach (var n in notes)
        {
            if (!groups.TryGetValue(n.Text, out var g)) { g = new List<Note>(); groups[n.Text] = g; order.Add(n.Text); }
            g.Add(n);
        }

        var collapsed = new List<Note>();
        foreach (var text in order)
        {
            var g = groups[text];
            var numbered = g.Select(n => n.DetailNumber)
                            .Where(d => !string.IsNullOrWhiteSpace(d))
                            .Distinct(StringComparer.Ordinal)        // (D): dedup identical details
                            .OrderBy(DetailSortKey)
                            .ToList();
            bool hasBlank = g.Any(n => string.IsNullOrWhiteSpace(n.DetailNumber));

            string label;
            bool isMulti;
            if (numbered.Count == 0) { label = ""; isMulti = false; }  // all-blank → writer uses the [insert] control
            else
            {
                label = string.Join(", ", numbered) + (hasBlank ? ", [insert]" : "");
                isMulti = numbered.Count >= 2;                          // plural only for 2+ numbered details
            }

            collapsed.Add(new Note
            {
                Text = text,
                DetailNumber = numbered.Count > 0 ? numbered[0] : "",   // lowest detail, kept as the sort key
                DetailLabel = label,
                IsMultiDetail = isMulti,
                CloudId = g.Min(n => n.CloudId),                         // lowest CloudId → stable ordering tiebreak
                Mark = g.Select(n => n.Mark).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "",
            });
        }

        return MergeSameDetail(collapsed
            .OrderBy(n => DetailSortKey(n.DetailNumber))   // lowest detail (blank groups sort last)
            .ThenBy(n => n.CloudId)
            .ToList());
    }

    /// <summary>
    ///     Second collapse pass (inverse of the comment-grouping above): DIFFERENT comments that share the
    ///     same detail list merge into ONE entry whose text is the comments joined as sentences, so a view
    ///     clouded several times renders "Detail 8 - A. B. C." instead of repeating "Detail 8 -" per item.
    ///     Only identical labels merge ("Details 1, 3" and "Detail 1" stay separate items); all-blank
    ///     ([insert]) entries never merge — each is a distinct unknown location the user fills in by hand.
    ///     Input arrives ordered (detail → CloudId), so join order and output order stay byte-stable.
    /// </summary>
    private static List<Note> MergeSameDetail(List<Note> ordered)
    {
        var merged = new List<Note>();
        var byLabel = new Dictionary<string, Note>(StringComparer.Ordinal);
        foreach (var n in ordered)
        {
            if (string.IsNullOrEmpty(n.DetailLabel)) { merged.Add(n); continue; }
            if (byLabel.TryGetValue(n.DetailLabel, out var host)) host.Text = host.Text + " " + n.Text;
            else { byLabel[n.DetailLabel] = n; merged.Add(n); }
        }
        return merged;
    }

    /// <summary>
    ///     Normalizes a revision-cloud comment to sentence case and ensures end punctuation, while PRESERVING
    ///     sheet references / callouts ("3/A501", "S402", "20/S301") and acronyms ("SCEC", "CFM", "SIM") so they
    ///     are never lowercased. A comment typed in ALL CAPS is fully sentence-cased; a mixed-case comment is
    ///     left as written (only its first letter is ensured capital), so intentional casing/acronyms survive.
    /// </summary>
    private static string NormalizeNote(string s, bool on)
    {
        s = (s ?? "").Trim();
        if (!on || s.Length == 0) return s;

        bool entirelyUpper = !s.Any(char.IsLower);
        var sb = new StringBuilder();
        bool capNext = true;
        foreach (var tok in Regex.Split(s, @"(\s+)"))
        {
            if (tok.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(tok)) { sb.Append(tok); continue; }

            if (IsProtectedToken(tok, entirelyUpper))
            {
                sb.Append(tok); // sheet ref / callout / acronym — keep verbatim
            }
            else
            {
                var w = (entirelyUpper ? tok.ToLowerInvariant() : tok).ToCharArray();
                if (capNext)
                    for (int i = 0; i < w.Length; i++)
                        if (char.IsLetter(w[i])) { w[i] = char.ToUpperInvariant(w[i]); break; }
                sb.Append(new string(w));
            }
            capNext = EndsSentence(tok);
        }

        var result = sb.ToString().TrimEnd();
        if (result.Length > 0)
        {
            char last = result[result.Length - 1];
            if (last is not ('.' or '!' or '?' or ':')) result += ".";
        }
        return result;
    }

    /// <summary>Sheet references / callouts (letters+digits, optional "n/" prefix) and — in otherwise mixed-case
    /// text — short ALL-CAPS acronyms, are kept verbatim during note normalization.</summary>
    private static bool IsProtectedToken(string tok, bool entirelyUpper)
    {
        var core = tok.Trim('(', ')', '.', ',', ';', ':', '"');
        // Anchored: the WHOLE token must be a sheet ref/callout. Unanchored, any word with letters+digits
        // embedded (ROOM101) matched on a substring and escaped normalization.
        if (Regex.IsMatch(core, @"^\d*/?[A-Za-z]{1,3}\d{2,4}$")) return true;    // A601, 3/A601, 20/S301
        if (core.Contains('/') && Regex.IsMatch(core, "[A-Za-z]")) return true;  // generic "n/Sheet" callouts
        if (!entirelyUpper && Regex.IsMatch(core, @"^[A-Z][A-Z0-9]{1,5}$")) return true; // SCEC, CFM, SIM, MLO
        return false;
    }

    private static bool EndsSentence(string tok)
    {
        var t = tok.TrimEnd('"', ')');
        return t.EndsWith(".") || t.EndsWith("!") || t.EndsWith("?");
    }

    /// <summary>
    ///     The issue being revised, as (title, date): the previous NON-BLANK revision in the sequence (its
    ///     Description + RevisionDate), or — when the selected revision is the first real one — the original
    ///     submission (Project Status + Project Issue Date). Blank revisions (no description, added to keep
    ///     numbering aligned with other disciplines) are skipped when walking backward.
    /// </summary>
    private static (string title, string date) ReferencedIssue(Document doc, ElementId selectedRevId)
    {
        try
        {
            var ordered = Revision.GetAllRevisionIds(doc); // returned in sequence order
            int idx = -1;
            for (int i = 0; i < ordered.Count; i++) if (ordered[i] == selectedRevId) { idx = i; break; }
            for (int j = idx - 1; j >= 0; j--)
            {
                if (doc.GetElement(ordered[j]) is Revision pr)
                {
                    var desc = SafeStr(() => pr.Description);
                    if (!string.IsNullOrWhiteSpace(desc)) return (desc, SafeStr(() => pr.RevisionDate));
                }
            }
        }
        catch { /* fall through to the original submission */ }

        var pinfo = doc.ProjectInformation;
        return (SafeStr(() => pinfo?.Status), SafeStr(() => pinfo?.IssueDate));
    }

    private static string FormatDate(string raw)
    {
        if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(raw) ? DateTime.Today.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) : raw;
    }

    /// <summary>
    ///     The Building Name to show, gated on the project actually having title blocks on sheets (the proxy
    ///     for "used in the title block"). Two sources, in order:
    ///       (a) a "Building Name" parameter carried by a title block instance (custom/shared-parameter setup);
    ///       (b) the built-in Project Information "Building Name", which title blocks commonly label directly.
    ///     Returns "" when there are no title blocks on sheets or no building name is set. (Revit's API can't
    ///     confirm which built-in a title-block *label* displays, so (b) is gated on title blocks existing
    ///     rather than on inspecting the label — a practical, conservative proxy.)
    /// </summary>
    private static string BuildingNameFromTitleBlock(Document doc, Options opts)
    {
        // GEN-03: "Building Name" was the only name looked up, so a template calling the parameter BuildingName,
        // Building_Name or a localized equivalent matched nothing and fell through silently.
        var names = opts.BuildingNameParameters is { Count: > 0 }
            ? opts.BuildingNameParameters
            : new[] { "Building Name" };

        try
        {
            var onSheets = new HashSet<long>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate).Select(s => s.Id.Value));

            bool titleBlockOnSheet = false;
            foreach (var tb in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                if (tb.OwnerViewId == ElementId.InvalidElementId || !onSheets.Contains(tb.OwnerViewId.Value)) continue;
                titleBlockOnSheet = true;
                foreach (var pname in names)   // (a) custom/shared param on the title block
                {
                    var v = tb.LookupParameter(pname)?.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!;
                }
            }

            // (b) built-in Project Information "Building Name" (what a title block typically labels).
            if (titleBlockOnSheet)
            {
                var builtIn = doc.ProjectInformation?.BuildingName;
                if (!string.IsNullOrWhiteSpace(builtIn)) return builtIn!;
            }
        }
        catch { /* best-effort */ }
        return "";
    }

    // GEN-04: this list mixes languages — the English connectors plus the Romance particles "de" and "la". In an
    // English title-caser those two wrongly lowercase legitimate words: "DE SOTO AVENUE" becomes "de Soto Avenue".
    // Kept in the default for backward compatibility with narratives already produced by this build; a practice
    // that wants one language sets TransomSettings.RevisionTitleCaseSmallWords.
    private static readonly HashSet<string> DefaultSmallWords = new(StringComparer.OrdinalIgnoreCase)
        { "of", "the", "and", "a", "an", "in", "on", "at", "to", "for", "by", "or", "nor", "but", "de", "la" };

    private static HashSet<string> SmallWordsFor(Options opts) =>
        opts.TitleCaseSmallWords is { Count: > 0 }
            ? new HashSet<string>(opts.TitleCaseSmallWords, StringComparer.OrdinalIgnoreCase)
            : DefaultSmallWords;

    /// <summary>
    ///     Title-cases an ALL-CAPS string the way a name/address reads: each significant word capitalized,
    ///     small connector words (of, the, and…) kept lower (except first), standalone single letters kept
    ///     capital (designators "A"/"B", directions "N"/"S"), and tokens starting with a digit untouched
    ///     ("29th", "1200"). Strings that already contain lowercase are left as entered in Revit.
    ///     e.g. "1200 WEST 29TH STREET N" -> "1200 West 29th Street N"; "SPRINGFIELD, KANSAS" -> "Springfield, Kansas".
    /// </summary>
    private static string TitleCaseSmart(string s, Options opts)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        if (s.Any(char.IsLower)) return s; // already mixed case — assume entered correctly

        var smallWords = SmallWordsFor(opts);
        var sb = new StringBuilder();
        bool first = true;
        foreach (var tok in Regex.Split(s.ToLowerInvariant(), @"(\s+)"))
        {
            if (tok.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(tok)) { sb.Append(tok); continue; }

            var core = tok.Trim('(', ')', '.', ',', ';', ':', '"', '#');
            if (core.Length == 1 && char.IsLetter(core[0])) sb.Append(tok.ToUpperInvariant()); // A, B, N, S…
            else if (!first && smallWords.Contains(core)) sb.Append(tok);                        // of, the, and…
            else sb.Append(CapFirstLetter(tok));
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>Capitalizes the first character only when the token starts with a letter (so "29th"/"1200" stay).</summary>
    private static string CapFirstLetter(string tok)
    {
        var c = tok.ToCharArray();
        if (c.Length > 0 && char.IsLetter(c[0])) c[0] = char.ToUpperInvariant(c[0]);
        return new string(c);
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
                    var ra = a.Substring(si, i - si);
                    var rb = b.Substring(sj, j - sj);
                    // Compare the runs numerically WITHOUT parsing — a 19+-digit run overflows
                    // long.Parse, and an exception inside an IComparer aborts the whole OrderBy.
                    // Strip leading zeros: a longer remainder is bigger, then ordinal digits decide.
                    var ta = ra.TrimStart('0');
                    var tb = rb.TrimStart('0');
                    if (ta.Length != tb.Length) return ta.Length.CompareTo(tb.Length);
                    int ncmp = string.CompareOrdinal(ta, tb);
                    if (ncmp != 0) return ncmp;
                    // Numerically equal but padded differently ("A01" vs "A1"): shorter run first,
                    // so two distinct sheet numbers never compare as a tie.
                    if (ra.Length != rb.Length) return ra.Length.CompareTo(rb.Length);
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

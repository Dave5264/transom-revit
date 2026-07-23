using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Transom.Core;

/// <summary>One element (or condition) that makes a model group un-danceable, surfaced to the user.</summary>
public sealed class GroupBlocker
{
    public long ElementId;
    public string Category = "";
    public string Name = "";
    public string Reason = "";

    public override string ToString()
    {
        var who = string.IsNullOrEmpty(Name) ? Category : $"{Name} ({Category})";
        return string.IsNullOrEmpty(who) ? Reason : $"{who} — {Reason}";
    }
}

/// <summary>One named family whose level-anchoring makes the group un-danceable — collected so the user can be
/// told EXACTLY what to fix/re-host to unlock the dance (e.g. "CW_Closet Shelf and Pole"). Deduped by Name.</summary>
public sealed class BrokenFamily
{
    public string Name = "";
    public string Category = "";
    public int Count;                  // how many anchored member instances of this family are in the group

    public override string ToString() =>
        (string.IsNullOrEmpty(Category) ? Name : $"{Name} ({Category})") + (Count > 1 ? $" ×{Count}" : "");
}

/// <summary>Verdict from <see cref="GroupSafety"/> for one model-group type.</summary>
public sealed class GroupSafetyResult
{
    public bool HasAnchored;            // a member is anchored OUTSIDE the group (Level-hosted / sketch-based)
    public bool HasNested;             // contains a nested model group (BYSTANDER — preserved by the swap, not a hard gate)
    public bool HasRotation;           // instances placed at different rotations, SAME handedness (a real breaker)
    public bool HasMirror;             // instances differ only in handedness (a mirror — NOT a breaker; STEP-14b judges)
    public bool HasMultiLevel;         // instances sit on DIFFERENT levels (a real breaker — siblings scatter to A's level)
    public bool SingleInstance;        // < 2 instances (or none): nothing to repoint, dance N/A
    public readonly List<GroupBlocker> Blockers = new();

    /// <summary>The named, level-anchored families that gate the dance — surfaced to the user so they can fix
    /// (re-host) them and unlock option 3. Populated from <see cref="HasAnchored"/> detection (deduped by name).</summary>
    public readonly List<BrokenFamily> BrokenFamilies = new();

    /// <summary>A "broken" group the grouped-edit "dance" (Claude's Edit Group pass) cannot faithfully reproduce → color RED on export
    /// (the broad HINT predicate: includes nested, which the dance actually preserves, for a conservative colour).
    /// Note: a MIRROR is deliberately NOT broken — mirror-only groups now go yellow/danceable.</summary>
    public bool IsBroken => HasAnchored || HasRotation || HasMultiLevel || HasNested;

    /// <summary>The HARD dance gate (4d): the swap is refused only for level-anchored families, true rotation, or
    /// multi-level placement. MIRROR and bystander-NESTED are deliberately EXCLUDED — STEP-14b (per-member centroid
    /// + level backstop) is the arbiter for those. (SingleInstance is handled separately by guard 4a.)</summary>
    public bool IsDanceGateBroken => HasAnchored || HasRotation || HasMultiLevel;

    /// <summary>A "simple" group the dance CAN reproduce (≥ 2 instances, not broad-broken). Mirror-only and
    /// nested-only groups are NOT IsBroken, so they now read danceable.</summary>
    public bool IsDanceable => !IsBroken && !SingleInstance;

    /// <summary>Short, de-duplicated, user-facing explanation of why the group is broken. Leads with the named
    /// broken families (the actionable bit) when present, then any remaining blockers.</summary>
    public string Explain(int max = 6)
    {
        var parts = new List<string>();
        if (BrokenFamilies.Count > 0)
            parts.Add("level-anchored families: " + string.Join(", ", BrokenFamilies.Select(f => f.ToString())));
        parts.AddRange(Blockers
            .Where(b => string.IsNullOrEmpty(b.Name) || BrokenFamilies.All(f => f.Name != b.Name))
            .Select(b => b.ToString()));
        if (parts.Count == 0)
            return IsDanceable ? "simple group" : SingleInstance ? "single-instance group" : "group";
        return string.Join("; ", parts.Distinct().Take(max));
    }
}

/// <summary>
///   Shared detector for model groups the grouped-edit "group dance" (import option 3, Claude's Edit Group pass) cannot faithfully
///   reproduce. Owner-validated criteria (the dance was over-refusing mirror-only and bystander-nested groups):
///   the REAL breakers that gate the dance are
///     * LEVEL-ANCHORED "broken families" — a member is hosted on a Level or is sketch-based (Ceiling/Floor/
///       Roof and their &lt;Sketch&gt;) or its work plane is a Level. Its position resolves against the level,
///       not the group frame, so a sibling repoint flings it (a level-hosted "CW_Closet Shelf and Pole" jumped).
///       Members hosted on a GROUP MEMBER (a door on a group wall, a sink on group casework) ride along fine and
///       are NOT flagged. The offending families are NAMED in <see cref="GroupSafetyResult.BrokenFamilies"/> so
///       the user can re-host them and unlock the dance.
///     * ROTATION — instances placed at different rotations (SAME handedness) get snapped onto the reference
///       instance's aspect (a 90°-rotated casework unit flipped onto the reference). A real breaker.
///     * MULTI-LEVEL — instances sit on different levels; a level-hosted sibling re-resolves to A's level and
///       scatters. A real breaker.
///   Two conditions that LOOK broken but are NOT hard gates (owner-proven swappable; STEP-14b is the arbiter):
///     * MIRROR (handedness flip only) — mirrored unit groups swap fine. Allowed; flagged informationally only.
///     * BYSTANDER NESTED model group — UngroupMembers frees it whole and NewGroup re-nests it; STEP-14 structural
///       parity + STEP-15 commit-stuck catch any circular-reference case. Kept in the broad colour but NOT the gate.
///   <see cref="GroupSafetyResult.IsBroken"/> (broad, includes nested) drives export COLOUR; the narrower
///   <see cref="GroupSafetyResult.IsDanceGateBroken"/> drives the hard 4d dance refusal.
/// </summary>
public static class GroupSafety
{
    /// <summary>Analyze every top-level instance of a model-group type and report whether it is danceable.</summary>
    public static GroupSafetyResult AnalyzeType(Document doc, ElementId groupTypeId)
    {
        var r = new GroupSafetyResult();
        if (doc == null || groupTypeId == null || groupTypeId == ElementId.InvalidElementId) return r;
        if (doc.GetElement(groupTypeId) is not GroupType gt) return r;

        var instances = TopLevelInstances(doc, gt);
        if (instances.Count == 0)
        {
            // Zero-instance type (purged/orphaned definition): nothing to repoint AND no geometry to read. It must
            // NOT read "danceable" (IsDanceable requires !SingleInstance), so flag it single-instance so callers
            // route it to the single-instance message rather than offering an empty dance.
            r.SingleInstance = true;
            return r;
        }
        r.SingleInstance = instances.Count < 2;

        // Nested groups + anchored members: members are congruent across instances, so the representative
        // instance is enough for these membership facts.
        ScanMembers(doc, instances[0], r);

        // Orientation + multi-level need ≥ 2 instances to compare.
        if (instances.Count >= 2)
        {
            var (rotated, mirrored) = Orientation(doc, instances);
            if (rotated)
            {
                r.HasRotation = true;
                r.Blockers.Add(new GroupBlocker { Reason = "instances are placed at different rotations" });
            }
            if (mirrored)
            {
                // A mirror is NOT a breaker — flag it informationally only (no gate), so the user knows why the
                // group still dances. Deliberately NOT added to BrokenFamilies / hard gate.
                r.HasMirror = true;
            }

            if (MultiLevel(doc, instances))
            {
                r.HasMultiLevel = true;
                r.Blockers.Add(new GroupBlocker { Reason = "instances sit on different levels" });
            }
        }
        return r;
    }

    private static void ScanMembers(Document doc, Group g, GroupSafetyResult r)
    {
        foreach (var mid in g.GetMemberIds())
        {
            var e = doc.GetElement(mid);
            if (e == null) continue;
            if (e is Group nested)
            {
                r.HasNested = true;
                r.Blockers.Add(new GroupBlocker
                {
                    ElementId = mid.Value,
                    Category = "Model Groups",
                    Name = Safe(() => nested.Name),
                    Reason = "nested model group (preserved by the swap; verified after regroup)",
                });
                continue;   // do NOT descend — a nested group's members ride with IT, not this group
            }

            var reason = AnchorReason(e);
            if (reason != null)
            {
                r.HasAnchored = true;
                var name = MemberName(e);
                var cat = e.Category?.Name ?? "?";
                r.Blockers.Add(new GroupBlocker
                {
                    ElementId = mid.Value,
                    Category = cat,
                    Name = name,
                    Reason = reason,
                });
                // Name the offending family so the user can be told exactly what to re-host (dedup by name; the
                // members are congruent across instances, so this representative instance names every family once).
                var bf = r.BrokenFamilies.FirstOrDefault(f => f.Name == name);
                if (bf == null) r.BrokenFamilies.Add(new BrokenFamily { Name = name, Category = cat, Count = 1 });
                else bf.Count++;
            }
        }
    }

    /// <summary>Why a (non-group) member is anchored OUTSIDE its group, or null if it rides rigidly with the group.</summary>
    private static string? AnchorReason(Element e)
    {
        var cat = e.Category?.Name;
        if (cat == "Ceilings" || cat == "Floors" || cat == "Roofs")
            return $"sketch-based {cat} (anchored to a level, not the group)";
        if (cat != null && cat.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0)
            return "sketch geometry (anchored to a level, not the group)";

        if (e is FamilyInstance fi)
        {
            // Hosted directly on a Level (the host element is a Level).
            try { if (fi.Host is Level) return "hosted on a Level (not on group geometry)"; } catch { /* ignore */ }
            // Work plane is a Level (work-plane / curve-based families sketched on the level plane).
            try
            {
                var v = e.get_Parameter(BuiltInParameter.SKETCH_PLANE_PARAM)?.AsValueString();
                if (!string.IsNullOrEmpty(v) && v.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "work plane is a Level (not group geometry)";
            }
            catch { /* ignore */ }
        }
        return null;
    }

    // ----- orientation -------------------------------------------------------------------------------------

    /// <summary>
    ///   Splits the instances' placement into ROTATION (a real breaker) vs MIRROR (not a breaker). Uses a
    ///   placement-relative signature built from member centroids by positional role: the planar ANGLE of the
    ///   role0→role1 vector and the SIGN of its cross product with role0→role2 (the handedness).
    ///
    ///   CRITICAL handedness fold: a mirror flips the handedness AND reflects the angle (about the mirror axis),
    ///   so a purely mirrored instance has a DIFFERENT raw angle from the reference even though it is not rotated.
    ///   Comparing raw angles would therefore mis-read every mirror as a rotation and the whole fix would fail.
    ///   For an instance whose handedness DIFFERS from the reference we fold out the mirror's angle flip and keep
    ///   only a genuine ADDITIONAL rotation. A reflection about the X axis is θ → −θ and about the Y axis is
    ///   θ → π − θ; we take the axis-symmetric minimum residual over BOTH principal reflections (and the identity),
    ///   so a mirror across either principal axis folds to ≈0 while a real extra rotation survives all candidates.
    ///   (An earlier Y-only form, <c>Norm((PI - angle) - angle0)</c>, cancelled only the Y-axis mirror and mis-read
    ///   every X/long-axis mirror as rotation — CF-13a. A SKEW (non-principal) mirror axis still reads as rotation
    ///   and is fundamentally un-separable by a centroid-angle fold; that direction only OVER-gates, never under-
    ///   gates, so it stays refuse-over-damage.) Same-handedness instances compare angles directly.
    /// </summary>
    private static (bool rotated, bool mirrored) Orientation(Document doc, List<Group> instances)
    {
        double? angle0 = null;
        int? hand0 = null;
        bool rotated = false, mirrored = false;
        foreach (var g in instances)
        {
            var f = LocalFrame(doc, g);
            if (f == null) continue;
            var (angle, hand) = f.Value;
            int s = Math.Sign(hand);

            if (angle0 == null) { angle0 = angle; if (s != 0) hand0 = s; continue; }

            // Establish the reference handedness from the first instance that has one.
            if (hand0 == null && s != 0) hand0 = s;

            bool flipped = s != 0 && hand0 != null && s != hand0.Value;
            if (flipped) mirrored = true;                                        // handedness differs → a mirror

            // Compare the angle against the reference, folding out a mirror's angle flip when handedness differs so
            // only a TRUE (additional) rotation trips the rotation flag. A mirror reflects the role0→role1 direction
            // about its mirror axis (θ → 2φ − θ); for a PRINCIPAL-axis mirror that is θ → −θ (X axis) or θ → π − θ
            // (Y axis). A single-axis fold would cancel only one of those (the earlier Y-only form mis-read every
            // X/long-axis "back-to-back" mirror as a rotation → re-excluded a whole class of mirrored unit groups —
            // CF-13a). So take the axis-symmetric minimum over both principal reflections AND the identity: a mirror
            // across EITHER principal axis folds to ≈0, while a genuine extra rotation survives all three. (A SKEW
            // mirror axis still reads as rotation — fundamentally un-separable by a centroid-angle fold — which only
            // OVER-gates, never under-gates; STEP-14b backstops the rare residual-aliased case. Principal-axis only.)
            double residual = flipped
                ? Math.Min(Math.Abs(Norm(angle - angle0.Value)),
                    Math.Min(Math.Abs(Norm((Math.PI - angle) - angle0.Value)),
                             Math.Abs(Norm((-angle) - angle0.Value))))
                : Math.Abs(Norm(angle - angle0.Value));
            if (residual > 0.05) rotated = true;                                 // > ~3° → rotated
        }
        return (rotated, mirrored);
    }

    private static (double angle, double hand)? LocalFrame(Document doc, Group g)
    {
        var pts = new List<XYZ>();
        foreach (var mid in g.GetMemberIds())
        {
            var bb = SafeBox(doc.GetElement(mid));
            if (bb != null) pts.Add(bb.Min.Add(bb.Max).Multiply(0.5));
        }
        if (pts.Count < 3) return null;
        int i0 = 0, i1 = pts.Count / 3, i2 = (2 * pts.Count) / 3;
        var v01 = pts[i1].Subtract(pts[i0]);
        var v02 = pts[i2].Subtract(pts[i0]);
        if (v01.GetLength() < 1e-6) return null;
        return (Math.Atan2(v01.Y, v01.X), v01.CrossProduct(v02).Z);
    }

    private static double Norm(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    // ----- multi-level -------------------------------------------------------------------------------------

    /// <summary>
    ///   True when the type's top-level instances are NOT all on the same level. A level-hosted sibling member
    ///   re-resolves to instance A's level during the dance, so an across-level spread scatters it — a real
    ///   breaker. Per-instance level = the Group's own level param (GROUP_LEVEL / FAMILY_LEVEL_PARAM) when set,
    ///   else the level of its first level-bound member (<see cref="MemberLevelId"/>).
    /// </summary>
    private static bool MultiLevel(Document doc, List<Group> instances)
    {
        long? first = null;
        foreach (var g in instances)
        {
            var lid = GroupLevelId(doc, g);
            if (lid == null || lid == ElementId.InvalidElementId) continue;     // unknown level → can't compare; ignore
            if (first == null) first = lid.Value;
            else if (lid.Value != first.Value) return true;                     // two instances on different levels
        }
        return false;
    }

    /// <summary>The level a group INSTANCE sits on: its own level parameter when present, else the level of its
    /// first level-bound member. R25 may not expose GROUP_LEVEL — every BIP read is wrapped so a missing enum
    /// value can never break the analysis (we just fall back to the member level).</summary>
    private static ElementId? GroupLevelId(Document doc, Group g)
    {
        // The group's own level parameter (varies by Revit version; both are tried defensively).
        foreach (var bip in new[] { BuiltInParameter.GROUP_LEVEL, BuiltInParameter.FAMILY_LEVEL_PARAM })
        {
            try
            {
                var p = g.get_Parameter(bip);
                var id = p?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return id;
            }
            catch { /* enum value not resolvable in this API — fall through */ }
        }
        // Else: the level of the first level-bound member.
        foreach (var mid in g.GetMemberIds())
        {
            var lid = MemberLevelId(doc.GetElement(mid));
            if (lid != null && lid != ElementId.InvalidElementId) return lid;
        }
        return null;
    }

    /// <summary>The Level an element is bound to (Host is a Level, else the first level-bearing built-in
    /// parameter), or null/Invalid if it isn't level-bound. Shared by <see cref="MultiLevel"/> and the dance's
    /// STEP-14b per-member backstop so "moved level" means the SAME thing in both. Every BIP read is wrapped so a
    /// missing enum value in some Revit version can never throw.</summary>
    internal static ElementId? MemberLevelId(Element? e)
    {
        if (e == null) return null;
        // Direct host that is a Level (a level-hosted family instance).
        try { if (e is FamilyInstance fi && fi.Host is Level hl) return hl.Id; } catch { /* ignore */ }
        // Else the first level-bearing parameter that resolves to a Level element id.
        foreach (var bip in new[]
                 {
                     BuiltInParameter.FAMILY_LEVEL_PARAM,
                     BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                     BuiltInParameter.LEVEL_PARAM,
                     BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                 })
        {
            try
            {
                var p = e.get_Parameter(bip);
                var id = p?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return id;
            }
            catch { /* enum value not resolvable in this API — try the next */ }
        }
        return null;
    }

    // ----- helpers -----------------------------------------------------------------------------------------

    private static List<Group> TopLevelInstances(Document doc, GroupType gt)
    {
        var list = new List<Group>();
        try
        {
            foreach (Group g in gt.Groups)
            {
                if (g == null || !g.Document.Equals(doc)) continue;
                if (g.GroupId != ElementId.InvalidElementId) continue;           // not nested in another group
                if (g.AttachedParentId != ElementId.InvalidElementId) continue;  // not an attached-detail child
                list.Add(g);
            }
        }
        catch { /* ignore */ }
        return list;
    }

    private static BoundingBoxXYZ? SafeBox(Element? e)
    {
        if (e == null) return null;
        try { return e.get_BoundingBox(null); } catch { return null; }
    }

    private static string MemberName(Element e)
    {
        try { if (e is FamilyInstance fi && fi.Symbol != null) return fi.Symbol.FamilyName; } catch { /* ignore */ }
        return Safe(() => e.Name);
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? ""; } catch { return "?"; }
    }
}

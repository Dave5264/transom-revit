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

/// <summary>Verdict from <see cref="GroupSafety"/> for one model-group type.</summary>
public sealed class GroupSafetyResult
{
    public bool HasAnchored;            // a member is anchored OUTSIDE the group (Level-hosted / sketch-based)
    public bool HasNested;             // contains a nested model group
    public bool HasMixedOrientation;   // instances placed at different orientations (rotation / mirror)
    public bool SingleInstance;        // < 2 instances: nothing to repoint, dance N/A
    public readonly List<GroupBlocker> Blockers = new();

    /// <summary>A "broken" group the definition-swap dance cannot faithfully reproduce → color RED on export.</summary>
    public bool IsBroken => HasAnchored || HasNested || HasMixedOrientation;

    /// <summary>A "simple" group the dance CAN reproduce (≥ 2 same-orientation instances, no anchored/nested members).</summary>
    public bool IsDanceable => !IsBroken && !SingleInstance;

    /// <summary>Short, de-duplicated, user-facing explanation of why the group is broken.</summary>
    public string Explain(int max = 6) =>
        Blockers.Count == 0
            ? (IsDanceable ? "simple group" : "single-instance group")
            : string.Join("; ", Blockers.Select(b => b.ToString()).Distinct().Take(max));
}

/// <summary>
///   Shared detector for "broken" model groups — ones the definition-swap "group dance" (import option 3)
///   CANNOT faithfully reproduce. Verified empirically against the test model: NewGroup + ChangeTypeId
///   silently scatters or re-orients members in three situations, each common in real unit/ceiling groups:
///     * ANCHORED members — a member is hosted on a Level or is sketch-based (Ceiling/Floor/Roof and their
///       &lt;Sketch&gt;). Its position resolves against the level/sketch, not the group frame, so the swap
///       flings it (a level-hosted closet shelf jumped ~70 ft). Members hosted on a GROUP MEMBER (a door on
///       a group wall, a sink on group casework) ride along fine and are NOT flagged.
///     * NESTED model groups — regrouping forms a circular chain of references Revit rejects (the commit
///       rolls back as DocumentCorruption).
///     * MIXED ORIENTATION — instances placed mirrored/rotated relative to one another get snapped onto the
///       reference instance's orientation (a 90°-rotated casework unit flipped onto the reference aspect).
///   Of 37 multi-level group types in the test model, only ~4 were structurally clean, and every real
///   unit/ceiling group hit at least one of these — so option 3 is a narrow tool and most groups route to
///   option 2 / Claude-Assist instead.
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
        if (instances.Count == 0) return r;
        r.SingleInstance = instances.Count < 2;

        // Nested groups + anchored members: members are congruent across instances, so the representative
        // instance is enough for these membership facts.
        ScanMembers(doc, instances[0], r);

        // Orientation needs ≥ 2 instances to compare.
        if (instances.Count >= 2 && MixedOrientation(doc, instances))
        {
            r.HasMixedOrientation = true;
            r.Blockers.Add(new GroupBlocker { Reason = "instances are placed at different orientations (mirror/rotation)" });
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
                    Reason = "nested model group (regrouping forms a circular reference)",
                });
                continue;   // do NOT descend — a nested group's members ride with IT, not this group
            }

            var reason = AnchorReason(e);
            if (reason != null)
            {
                r.HasAnchored = true;
                r.Blockers.Add(new GroupBlocker
                {
                    ElementId = mid.Value,
                    Category = e.Category?.Name ?? "?",
                    Name = MemberName(e),
                    Reason = reason,
                });
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
    ///   True if the instances are NOT all at the same orientation. Uses a placement-relative signature built
    ///   from member centroids by positional role: the planar ANGLE of the role0→role1 vector (catches any
    ///   rotation) and the SIGN of its cross product with role0→role2 (catches mirroring).
    /// </summary>
    private static bool MixedOrientation(Document doc, List<Group> instances)
    {
        double? angle0 = null;
        int? hand0 = null;
        foreach (var g in instances)
        {
            var f = LocalFrame(doc, g);
            if (f == null) continue;
            var (angle, hand) = f.Value;
            if (angle0 == null) angle0 = angle;
            else if (Math.Abs(Norm(angle - angle0.Value)) > 0.05) return true;   // > ~3° → rotated
            int s = Math.Sign(hand);
            if (s != 0)
            {
                if (hand0 == null) hand0 = s;
                else if (s != hand0.Value) return true;                          // handedness flipped → mirrored
            }
        }
        return false;
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

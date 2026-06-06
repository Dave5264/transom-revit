using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

// =====================================================================================================
//  GroupDanceApplier — AUTOMATED, in-process version of Transom's "group dance" (schedule-import
//  group-conflict resolution OPTION 3). Today option 3 only STAGES built-in group-member edits to JSON
//  for Claude to apply by hand (see TransomViewModel.ClaudeGuideMarkdown()); THIS class performs the
//  same sanctioned definition-swap automatically inside Revit.
//
//  WHY: built-in parameters can't "vary by group instance". To change a built-in on elements inside a
//  MODEL GROUP uniformly across every instance of a group type, you must edit the group DEFINITION via:
//      ungroup one instance A  ->  set the target param(s) on the freed members  ->  NewGroup(freed)
//      ->  repoint every OTHER instance to the new type  ->  delete the old type  ->  rename back  -> verify.
//
//  CONTRACT / HOSTING:
//   * This class opens its OWN top-level Transaction(s) (one per group type). It assumes NO ambient
//     caller transaction is active. It is intentionally NOT wired into Importer.Apply yet — a human
//     integrates it later. It MUST be invoked on Revit's API thread, from an IExternalEventHandler
//     context (like ImportEventHandler), because it edits the model and (optionally) subscribes to
//     UIApplication.DialogBoxShowing.
//   * Pass the ambient UIApplication (the one handed to IExternalEventHandler.Execute) so dialog
//     suppression uses the genuine UI context. If null is passed, we best-effort construct one from
//     doc.Application; if that fails, the per-transaction FailuresPreprocessor is still the real
//     non-modal safety net (the dance never uses the modal GroupType property setter).
//
//  SAFETY POSTURE: refuse (SKIP) rather than risk damage. Every guard is evaluated read-only, across
//  ALL instances of a type, BEFORE any edit. Inside the dance, ANY anomaly throws and rolls the whole
//  type back — UngroupMembers is a one-way door, so the per-type transaction rollback is the guarantee
//  that we NEVER leave a half-danced group.
//
//  NESTED GROUPS: a BYSTANDER nested model group (the edited param is on a DIRECT member, not inside the
//  nest) is now PRESERVED rather than skipped — UngroupMembers frees it whole, NewGroup re-nests it, and
//  the post-dance STEP-14 check verifies full structural parity (every role's category/type, nested groups
//  included) so anything the swap would have lost/changed rolls the type back instead. (A target INSIDE a
//  nested group is not a direct member, so the role plan still skips it.)
// =====================================================================================================

/// <summary>Per-group-type outcome of an automated dance.</summary>
public sealed class GroupOutcome
{
    public enum DanceStatus { Danced, Skipped, Failed }

    public string GroupTypeName = "";
    public DanceStatus Status = DanceStatus.Skipped;
    public string Reason = "";              // skip reason / failure message / success note
    public int ParamsApplied;               // distinct (roleIndex, parameterId) set in the dance
    public int InstancesRepointed;          // OTHER instances repointed to the new definition
    public int InstancesVerified;           // instances whose members were read back and matched
}

/// <summary>Result of <see cref="GroupDanceApplier.Apply"/>: three distinct buckets + per-type detail + a plain-text log.</summary>
public sealed class DanceResult
{
    public int Danced;                      // committed & verified
    public int Skipped;                     // guard/conflict refused BEFORE any edit (no transaction opened)
    public int Failed;                      // rolled back mid-dance
    public List<GroupOutcome> Outcomes = new();
    public string Log = "";
}

public sealed class GroupDanceApplier
{
    private const double DoubleTol = 1e-6;          // matches Importer.VerifyWrite double tolerance
    private const int LogCap = 200;                 // matches Importer.BuildApplyLog cap

    /// <summary>
    ///     Applies every <see cref="GroupResolution.GroupDance"/> change in <paramref name="danceChanges"/> by
    ///     performing one automated definition-swap per GROUP TYPE (setting ALL of that type's target params in
    ///     the single dance). Each dance runs in its OWN top-level transaction with clean rollback on any failure.
    ///     Does not throw for per-type failures (they are bucketed); only catastrophic setup issues propagate.
    /// </summary>
    /// <param name="doc">The open document. The dance edits this document only.</param>
    /// <param name="danceChanges">Staged dance changes (Resolution == GroupDance). May be null/empty.</param>
    /// <param name="uiApp">Ambient UIApplication for dialog suppression; null = best-effort construct from doc.Application.</param>
    public DanceResult Apply(Document doc, List<ProposedChange> danceChanges, UIApplication? uiApp = null)
    {
        var result = new DanceResult();
        if (doc == null) { result.Log = "GroupDanceApplier: null document — nothing to do."; return result; }
        if (danceChanges == null || danceChanges.Count == 0)
        {
            result.Log = "GroupDanceApplier: no dance changes — nothing to do.";
            return result;
        }

        // Collector accumulates Revit warnings across all per-type transactions for the final log.
        var revitWarnings = new List<string>();
        var dismissedDialogs = new List<string>();

        // ---- Dialog suppression (belt-and-suspenders over each tx's FailuresPreprocessor) -------------------
        // Some edits raise modal DialogBoxes (not Failures) that would block — auto-dismiss with 7 (No/Cancel),
        // the established Transom convention (ImportEventHandler.cs / ScheduleReader.cs).
        UIApplication? ui = uiApp;
        if (ui == null)
        {
            try { ui = new UIApplication(doc.Application); } catch { ui = null; }
        }
        EventHandler<Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs>? dlg = null;
        if (ui != null)
        {
            dlg = (_, e) =>
            {
                try { dismissedDialogs.Add(e.DialogId); } catch { /* ignore */ }
                try { e.OverrideResult(7); } catch { /* ignore */ }
            };
            try { ui.DialogBoxShowing += dlg; } catch { dlg = null; }
        }

        try
        {
            // ---- STEP 1+2: bucket changes by GROUP TYPE id (read-only) --------------------------------------
            var buckets = new Dictionary<long, List<ProposedChange>>();
            foreach (var ch in danceChanges)
            {
                var memberUids = ch.BulkInstanceIds ?? new List<string> { ch.UniqueId };
                ElementId? groupTypeId = null;
                foreach (var uid in memberUids)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    var el = doc.GetElement(uid);
                    if (el == null) continue;
                    var gid = el.GroupId;
                    if (gid == null || gid == ElementId.InvalidElementId) continue;
                    if (doc.GetElement(gid) is Group g)
                    {
                        groupTypeId = g.GetTypeId();
                        break;
                    }
                }

                if (groupTypeId == null || groupTypeId == ElementId.InvalidElementId)
                {
                    // No resolvable grouped member — can't dance this change. Bucket as Failed (distinct from Skip).
                    result.Failed++;
                    ch.Outcome = ApplyOutcome.Failed;
                    result.Outcomes.Add(new GroupOutcome
                    {
                        GroupTypeName = string.IsNullOrEmpty(ch.GroupName) ? "(unknown group)" : ch.GroupName,
                        Status = GroupOutcome.DanceStatus.Failed,
                        Reason = $"no resolvable grouped member for change '{ChangeLabel(ch)}' (member deleted or no longer in a group)",
                    });
                    continue;
                }

                if (!buckets.TryGetValue(groupTypeId.Value, out var list))
                {
                    list = new List<ProposedChange>();
                    buckets[groupTypeId.Value] = list;
                }
                list.Add(ch);
            }

            // Deterministic processing order (stable logs): by group-type name.
            var ordered = buckets
                .Select(kv => (TypeId: new ElementId(kv.Key), Changes: kv.Value,
                               Name: (doc.GetElement(new ElementId(kv.Key)) as GroupType)?.Name ?? kv.Key.ToString()))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ---- STEP 3..15: one dance per group type -------------------------------------------------------
            foreach (var (typeId, changes, _) in ordered)
            {
                int before = result.Outcomes.Count;
                DanceOneType(doc, typeId, changes, result, revitWarnings);
                // Stamp every change in this type's bucket with the type's outcome (Danced -> Applied, else Failed),
                // so the run-results report can colour them. DanceOneType adds exactly one outcome per call.
                var last = result.Outcomes.Count > before ? result.Outcomes[result.Outcomes.Count - 1] : null;
                var oc = last != null && last.Status == GroupOutcome.DanceStatus.Danced ? ApplyOutcome.Applied : ApplyOutcome.Failed;
                foreach (var ch in changes) { ch.Outcome = oc; if (oc == ApplyOutcome.Failed) ch.OutcomeNote = last?.Reason ?? ""; }
            }
        }
        finally
        {
            if (ui != null && dlg != null)
            {
                try { ui.DialogBoxShowing -= dlg; } catch { /* ignore */ }
            }
        }

        result.Log = BuildLog(result, revitWarnings, dismissedDialogs);
        return result;
    }

    // -------------------------------------------------------------------------------------------------------
    //  ONE GROUP TYPE: guards (read-only) -> conflict check -> transaction (ungroup..verify) -> commit/rollback
    // -------------------------------------------------------------------------------------------------------
    private void DanceOneType(Document doc, ElementId groupTypeId, List<ProposedChange> changes,
        DanceResult result, List<string> revitWarnings)
    {
        var gType = doc.GetElement(groupTypeId) as GroupType;
        if (gType == null)
        {
            Skip(result, "(deleted group type)", "group type no longer exists");
            return;
        }

        // Authoritative rename source: the LIVE type name captured NOW (not ProposedChange.GroupName,
        // which is only a display string).
        string originalName = SafeName(gType);

        // ---- STEP 3: live, top-level model-group instances in THIS doc ----------------------------------
        var instances = new List<Group>();
        try
        {
            foreach (Group g in gType.Groups)
            {
                if (g == null) continue;
                if (!g.Document.Equals(doc)) continue;               // this doc only
                if (g.GroupId != ElementId.InvalidElementId) continue;     // not nested as a member of another group
                if (g.AttachedParentId != ElementId.InvalidElementId) continue; // not an attached-detail child
                instances.Add(g);
            }
        }
        catch (Exception ex)
        {
            Skip(result, originalName, "could not enumerate instances: " + ex.Message);
            return;
        }

        // ---- STEP 4: PRE-FLIGHT SAFETY GUARDS (read-only; skip whole type on first trip) -----------------
        // (4a) single-instance
        if (instances.Count <= 1)
        {
            Skip(result, originalName, "only one instance of this group type — nothing to repoint; edit the single group's member directly");
            return;
        }

        // (4b) pinned
        if (instances.Any(g => SafePinned(g)))
        {
            Skip(result, originalName, "a group instance is pinned");
            return;
        }

        // (4c) attached detail groups (any instance)
        foreach (var g in instances)
        {
            if (HasAttachedDetail(doc, g))
            {
                Skip(result, originalName, "has attached detail groups (the swap could silently lose them)");
                return;
            }
        }

        // (4d) BROKEN-GROUP gate — the definition swap can only faithfully reproduce a SIMPLE group. Refuse
        //      (the user is routed to option 2 / Claude-Assist) when a member is anchored OUTSIDE the group
        //      (hosted on a Level, or a sketch-based Ceiling/Floor/Roof), the group contains a NESTED model
        //      group, or its instances sit at DIFFERENT orientations (mirror/rotation). Each one makes
        //      NewGroup + ChangeTypeId silently scatter or re-orient members — verified empirically; see
        //      GroupSafety. (STEP 14b below is the runtime backstop for anything this pre-flight check misses.)
        var safety = GroupSafety.AnalyzeType(doc, groupTypeId);
        if (safety.IsBroken)
        {
            Skip(result, originalName, "broken group — " + safety.Explain());
            return;
        }

        // Snapshot ordered member ids per instance once (used by 4e/4f + the plan).
        List<IList<ElementId>> memberLists;
        try
        {
            memberLists = instances.Select(g => (IList<ElementId>)g.GetMemberIds()).ToList();
        }
        catch (Exception ex)
        {
            Skip(result, originalName, "could not read member ids: " + ex.Message);
            return;
        }

        // Nested model groups are refused by the broken-group gate (4d) above, so any group reaching here has
        // none — this count is therefore 0 and is kept only so the success-log shape stays uniform.
        int nestedGroupMembers = memberLists[0].Count(mid => doc.GetElement(mid) is Group);

        // (4e) excluded members / mismatched member sets — equal COUNT precondition (Revit 2025 has no
        //      public GetExcludedMemberIds, and GetMemberIds() omits excluded members, so unequal counts
        //      reveal excluded/non-uniform members). Covers BOTH the "excluded members" and
        //      "mismatched member sets" guards.
        int memberCount = memberLists[0].Count;
        if (memberLists.Any(m => m.Count != memberCount))
        {
            Skip(result, originalName, "member sets differ across instances (excluded members or non-uniform groups) — manual edit needed");
            return;
        }

        // (4f) positional congruence — every role index must hold the same KIND of element (category + type)
        //      across all instances, because the repoint/verify map members positionally (GetMemberIds order
        //      is documented to match members across instances, but we hard-verify it).
        var roleSig0 = memberLists[0].Select(id => RoleSignature(doc, id)).ToList();
        for (int i = 1; i < memberLists.Count; i++)
        {
            for (int r = 0; r < memberCount; r++)
            {
                if (RoleSignature(doc, memberLists[i][r]) != roleSig0[r])
                {
                    Skip(result, originalName, "group instances have mismatched member ordering/kinds — manual edit needed");
                    return;
                }
            }
        }

        // ---- STEP 5/6: build the edit plan keyed by POSITIONAL ROLE INDEX + read-only guard + conflict ---
        // Canonical role reference = instance A = instances[0]. uid -> roleIndex within A.
        var A = instances[0];
        var aMembers = memberLists[0];
        var uidToRole = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int r = 0; r < aMembers.Count; r++)
        {
            var mEl = doc.GetElement(aMembers[r]);
            if (mEl != null) uidToRole[mEl.UniqueId] = r;
        }

        // plan: list of (roleIndex, change). A change applies to one role (the member it names in A).
        var plan = new List<(int roleIndex, ProposedChange ch)>();
        foreach (var ch in changes)
        {
            var memberUids = ch.BulkInstanceIds ?? new List<string> { ch.UniqueId };
            int role = -1;
            foreach (var uid in memberUids)
            {
                if (uid != null && uidToRole.TryGetValue(uid, out var rr)) { role = rr; break; }
            }
            if (role < 0)
            {
                // None of this change's members map into instance A — its target role can't be located.
                Skip(result, originalName, $"a staged edit's member is not in the representative instance (change '{ChangeLabel(ch)}')");
                return;
            }
            plan.Add((role, ch));
        }

        // (Guard 6) read-only target parameter — checked on instance A's members BEFORE any edit.
        foreach (var (role, ch) in plan)
        {
            var member = doc.GetElement(aMembers[role]);
            var p = member == null ? null : GetParam(member, ch.ParameterId);
            if (p == null)
            {
                Skip(result, originalName, $"target parameter '{ch.Field}' not found on a member");
                return;
            }
            if (p.IsReadOnly)
            {
                Skip(result, originalName, $"target parameter '{ch.Field}' is read-only on a member");
                return;
            }
        }

        // (Conflict §3) two edits on the SAME (roleIndex, parameterId) with DIFFERENT parsed values can't
        //  both hold across instances of one type — refuse, never guess a winner.
        foreach (var grp in plan.GroupBy(pe => (pe.roleIndex, pe.ch.ParameterId)))
        {
            var distinct = grp.Select(pe => ValueKey(pe.ch)).Distinct().Count();
            if (distinct > 1)
            {
                Skip(result, originalName, "conflicting target values for the same member/parameter — cannot apply while grouped");
                return;
            }
        }

        // Capture the OTHER instances' ids now (still valid). A's id is captured to exclude it from repoint.
        var aId = A.Id;
        var otherInstanceIds = instances.Where(g => g.Id != aId).Select(g => g.Id).ToList();

        // Capture every instance's bounding box BEFORE the swap, so STEP 14b can assert nothing moved.
        var bboxBefore = new Dictionary<ElementId, BoundingBoxXYZ?>();
        foreach (var g in instances) bboxBefore[g.Id] = SafeBoundingBox(g);

        // ---- STEP 7..15: the dance, inside ONE dedicated top-level transaction for THIS type ------------
        using var tx = new Transaction(doc, $"Transom group dance: {originalName}");
        var collector = new DanceFailureCollector();
        try
        {
            tx.Start();
            var fho = tx.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(collector);
            fho.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fho);

            // STEP 9: ungroup A. After this, A is INVALID — never touch it again.
            ICollection<ElementId> freed = A.UngroupMembers();
            doc.Regenerate();

            // Map each freed id back to its roleIndex via the pre-ungroup ordered member list (same membership
            // minus the group wrapper). aMembers came from A before ungroup.
            var roleToFreedId = new Dictionary<int, ElementId>();
            foreach (var fid in freed)
            {
                int idx = IndexOf(aMembers, fid);
                if (idx >= 0) roleToFreedId[idx] = fid;
            }

            // STEP 10: set all target params on the freed members.
            foreach (var (role, ch) in plan)
            {
                if (!roleToFreedId.TryGetValue(role, out var fid))
                    throw new InvalidOperationException($"freed member for role {role} ('{ch.Field}') not found after ungroup");
                var member = doc.GetElement(fid)
                             ?? throw new InvalidOperationException($"freed member element {fid} resolved null");
                var p = GetParam(member, ch.ParameterId)
                        ?? throw new InvalidOperationException($"parameter '{ch.Field}' not found on freed member");
                if (p.IsReadOnly)
                    throw new InvalidOperationException($"parameter '{ch.Field}' is read-only on freed member");
                if (!SetValue(p, ch))
                    throw new InvalidOperationException($"Set returned false for '{ch.Field}'");
            }

            // STEP 11: regenerate, then rebuild the edited definition.
            doc.Regenerate();
            var newGroup = doc.Create.NewGroup(freed);
            var newTypeId = newGroup.GetTypeId();
            doc.Regenerate(); // avoid "group changed outside group edit mode" warning before further edits

            // STEP 12: repoint every OTHER instance to the new type. Re-fetch by id each iteration
            //          (ChangeTypeId invalidates the element; ids stay stable).
            int repointed = 0;
            foreach (var oid in otherInstanceIds)
            {
                if (doc.GetElement(oid) is not Group g) continue; // defensively skip a vanished instance
                if (g.GetTypeId() == newTypeId) continue;          // already repointed (defensive)
                g.ChangeTypeId(newTypeId);
                repointed++;
            }
            doc.Regenerate();

            // STEP 13: delete the now-unused original type, then rename the new type back (delete frees the name).
            string renameNote = "";
            doc.Delete(groupTypeId);
            try
            {
                if (doc.GetElement(newTypeId) is GroupType nt) nt.Name = originalName;
            }
            catch
            {
                renameNote = " (kept generated name; original name was taken)";
            }
            doc.Regenerate();

            // STEP 14: verify across SEVERAL instances (newGroup + every repointed instance) by positional role.
            var verifyInstances = new List<Group>();
            if (doc.GetElement(newTypeId) is GroupType vt)
            {
                foreach (Group g in vt.Groups)
                {
                    if (g == null) continue;
                    if (!g.Document.Equals(doc)) continue;
                    if (g.GroupId != ElementId.InvalidElementId) continue;
                    if (g.AttachedParentId != ElementId.InvalidElementId) continue;
                    verifyInstances.Add(g);
                }
            }
            if (verifyInstances.Count == 0)
                throw new InvalidOperationException("no instances of the new type to verify");
            // Parity: every original instance must now be on the new type (A's regroup + the repointed others).
            // A dropped repoint would slip past the per-member value check, so assert the count explicitly.
            if (verifyInstances.Count != instances.Count)
                throw new InvalidOperationException(
                    $"instance-count mismatch after dance: expected {instances.Count}, found {verifyInstances.Count} (a repoint may have been lost)");

            foreach (var vInst in verifyInstances)
            {
                var vMembers = vInst.GetMemberIds();

                // POSITION PARITY (STEP 14b) — the instance must occupy the SAME bounding box as before the
                // dance. RoleSignature is geometry-blind (a scattered or re-oriented member keeps its category
                // + type), so this is the real "nothing moved" guarantee: it catches anchored-member scatter
                // and orientation snap that would slip past the structural check, and rolls the whole type back.
                var beforeKey = vInst.Id == newGroup.Id ? aId : vInst.Id;
                if (bboxBefore.TryGetValue(beforeKey, out var bb0) && bb0 != null)
                {
                    var bb1 = SafeBoundingBox(vInst);
                    if (bb1 == null || !BoxesMatch(bb0, bb1))
                        throw new InvalidOperationException(
                            "an instance moved during the dance (a member scattered or the group re-oriented) — rolled back");
                }

                // STRUCTURAL PARITY — the "everything else is exactly as before" guarantee. The entire member
                // set (every bystander, INCLUDING any nested model group) must be positionally identical to the
                // pre-dance baseline: same count, and same category/type signature at each role index. A nested
                // group (or any bystander) that the ungroup/regroup/repoint lost, added, re-typed, or reordered
                // trips this and rolls the whole type back. Only the target parameter is allowed to differ — a
                // value change never alters a role's category/type signature, so this stays orthogonal to the
                // per-parameter verification below.
                if (vMembers.Count != memberCount)
                    throw new InvalidOperationException(
                        $"member count changed after dance: expected {memberCount}, found {vMembers.Count} (a nested group or bystander member was lost or added) — rolled back");
                for (int r = 0; r < memberCount; r++)
                {
                    if (RoleSignature(doc, vMembers[r]) != roleSig0[r])
                        throw new InvalidOperationException(
                            $"member structure changed at role {r} after dance (a nested group or bystander member changed kind/type or moved) — rolled back");
                }

                foreach (var (role, ch) in plan)
                {
                    if (role >= vMembers.Count)
                        throw new InvalidOperationException($"role index {role} out of range on a verified instance");
                    var vm = doc.GetElement(vMembers[role])
                             ?? throw new InvalidOperationException("verified member resolved null");
                    var vp = GetParam(vm, ch.ParameterId)
                             ?? throw new InvalidOperationException($"parameter '{ch.Field}' missing on verified member");
                    if (!VerifyWrite(vp, ch))
                        throw new InvalidOperationException($"verification failed for '{ch.Field}' on an instance");
                }
            }

            // STEP 15: commit — and VERIFY it actually stuck. A DocumentCorruption failure (e.g. regrouping a
            // bystander NESTED model group creates a "circular chain of references") makes Revit ROLL BACK the
            // commit: tx.Commit() then returns RolledBack, NOT Committed, and the model silently reverts. Without
            // this check the dance falsely reported "danced" while nothing changed. Treat any non-Committed
            // result as a failure (the model is already back to its pre-dance state).
            var commitStatus = tx.Commit();
            CollectWarnings(collector, revitWarnings, originalName);
            if (commitStatus != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    $"commit did not stick (status {commitStatus}) — Revit rejected the change and rolled the transaction back. "
                    + "Most often this is a nested model group whose regroup forms a circular reference; the model is unchanged.");

            int paramsApplied = plan.Select(pe => (pe.roleIndex, pe.ch.ParameterId)).Distinct().Count();
            result.Danced++;
            var danceNote = renameNote.Length > 0 ? renameNote.Trim() : "";
            if (nestedGroupMembers > 0)
                danceNote = (danceNote.Length > 0 ? danceNote + "; " : "")
                            + $"preserved {nestedGroupMembers} nested group(s) per instance (verified structurally)";
            result.Outcomes.Add(new GroupOutcome
            {
                GroupTypeName = originalName,
                Status = GroupOutcome.DanceStatus.Danced,
                Reason = danceNote,
                ParamsApplied = paramsApplied,
                InstancesRepointed = repointed,
                InstancesVerified = verifyInstances.Count,
            });
        }
        catch (Exception ex)
        {
            try
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            }
            catch { /* ignore rollback failure */ }
            CollectWarnings(collector, revitWarnings, originalName);
            result.Failed++;
            result.Outcomes.Add(new GroupOutcome
            {
                GroupTypeName = originalName,
                Status = GroupOutcome.DanceStatus.Failed,
                Reason = ex.Message,
            });
        }
    }

    // ----- guard helpers -----------------------------------------------------------------------------------

    private static bool SafePinned(Group g)
    {
        try { return g.Pinned; }
        catch { return false; }
    }

    private static bool HasAttachedDetail(Document doc, Group g)
    {
        try
        {
            if (g.GetAvailableAttachedDetailGroupTypeIds().Count > 0) return true;
        }
        catch { /* ignore and try the shown variant */ }
        try
        {
            var view = doc.ActiveView;
            if (view != null && g.GetShownAttachedDetailGroupTypeIds(view).Count > 0) return true;
        }
        catch { /* active view may be null/non-graphical — rely on the available check */ }
        return false;
    }

    /// <summary>Stable per-role signature (category id + type id) to confirm congruent member ordering across instances.</summary>
    private static string RoleSignature(Document doc, ElementId memberId)
    {
        var el = doc.GetElement(memberId);
        if (el == null) return "null";
        long cat = -1;
        try { cat = el.Category?.Id.Value ?? -1; } catch { cat = -1; }
        long typ = -1;
        try { var t = el.GetTypeId(); typ = t == ElementId.InvalidElementId ? -1 : t.Value; } catch { typ = -1; }
        return cat + ":" + typ;
    }

    private static BoundingBoxXYZ? SafeBoundingBox(Element e)
    {
        try { return e.get_BoundingBox(null); } catch { return null; }
    }

    /// <summary>Model bounding boxes match within ~0.1 ft on every corner (position-parity backstop).</summary>
    private static bool BoxesMatch(BoundingBoxXYZ a, BoundingBoxXYZ b, double tol = 0.1) =>
        Math.Abs(a.Min.X - b.Min.X) <= tol && Math.Abs(a.Min.Y - b.Min.Y) <= tol && Math.Abs(a.Min.Z - b.Min.Z) <= tol &&
        Math.Abs(a.Max.X - b.Max.X) <= tol && Math.Abs(a.Max.Y - b.Max.Y) <= tol && Math.Abs(a.Max.Z - b.Max.Z) <= tol;

    /// <summary>Canonical parsed-value key for conflict comparison (mirrors the storage the write uses).</summary>
    private static string ValueKey(ProposedChange ch) =>
        ch.IsString ? "s:" + (ch.NewString ?? "")
        : ch.IsInt ? "i:" + ch.NewInt.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "d:" + Math.Round(ch.NewDouble, 9).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int IndexOf(IList<ElementId> list, ElementId id)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == id) return i;
        return -1;
    }

    // ----- replicated Importer helpers (private there; do NOT call across classes — replicated verbatim) ---

    /// <summary>Replicates Importer.SetValue.</summary>
    private static bool SetValue(Parameter param, ProposedChange ch) =>
        ch.IsString ? param.Set(ch.NewString) : ch.IsInt ? param.Set(ch.NewInt) : param.Set(ch.NewDouble);

    /// <summary>Replicates Importer.VerifyWrite (1e-6 double tolerance).</summary>
    private static bool VerifyWrite(Parameter param, ProposedChange ch)
    {
        try
        {
            if (param.StorageType == StorageType.String)
                return (param.AsString() ?? "") == (ch.NewString ?? "");
            if (param.StorageType == StorageType.Integer)
                return param.AsInteger() == ch.NewInt;
            if (param.StorageType == StorageType.Double)
                return Math.Abs(param.AsDouble() - ch.NewDouble) <= DoubleTol;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Replicates Importer.GetParam (negative id = built-in; positive = project/shared by Id.Value).</summary>
    private static Parameter? GetParam(Element host, int parameterId)
    {
        if (parameterId < 0)
            return host.get_Parameter((BuiltInParameter)parameterId);
        foreach (Parameter p in host.Parameters)
            if (p.Id.Value == parameterId) return p;
        return null;
    }

    /// <summary>Replicates Importer.SafeName.</summary>
    private static string SafeName(Element e)
    {
        try { return e.Name; }
        catch { return e.Id.ToString(); }
    }

    private static string ChangeLabel(ProposedChange ch) =>
        $"{(string.IsNullOrEmpty(ch.ElementName) ? "?" : ch.ElementName)} · {ch.Field}: '{ch.OldValue}' -> '{ch.NewValue}'";

    private void Skip(DanceResult result, string typeName, string reason)
    {
        result.Skipped++;
        result.Outcomes.Add(new GroupOutcome
        {
            GroupTypeName = typeName,
            Status = GroupOutcome.DanceStatus.Skipped,
            Reason = reason,
        });
    }

    private static void CollectWarnings(DanceFailureCollector collector, List<string> sink, string typeName)
    {
        foreach (var m in collector.Messages) sink.Add($"[{typeName}] {m}");
        collector.Messages.Clear();
    }

    // ----- log assembly ------------------------------------------------------------------------------------

    private static string BuildLog(DanceResult result, List<string> revitWarnings, List<string> dismissedDialogs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Transom group dance — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine($"danced: {result.Danced} / skipped: {result.Skipped} / failed: {result.Failed}");

        var skipped = result.Outcomes.Where(o => o.Status == GroupOutcome.DanceStatus.Skipped).ToList();
        sb.AppendLine($"\n== skipped (guard/conflict): {skipped.Count} ==");
        foreach (var o in skipped.Take(LogCap)) sb.AppendLine($"  - {o.GroupTypeName}: {o.Reason}");
        if (skipped.Count > LogCap) sb.AppendLine($"  … +{skipped.Count - LogCap} more");

        var failed = result.Outcomes.Where(o => o.Status == GroupOutcome.DanceStatus.Failed).ToList();
        sb.AppendLine($"\n== failed (rolled back): {failed.Count} ==");
        foreach (var o in failed.Take(LogCap)) sb.AppendLine($"  - {o.GroupTypeName}: {o.Reason}");
        if (failed.Count > LogCap) sb.AppendLine($"  … +{failed.Count - LogCap} more");

        var danced = result.Outcomes.Where(o => o.Status == GroupOutcome.DanceStatus.Danced).ToList();
        sb.AppendLine($"\n== danced (committed & verified): {danced.Count} ==");
        foreach (var o in danced.Take(LogCap))
            sb.AppendLine($"  - {o.GroupTypeName}: {o.ParamsApplied} param(s), {o.InstancesRepointed} instance(s) repointed, "
                          + $"{o.InstancesVerified} verified{(string.IsNullOrEmpty(o.Reason) ? "" : " " + o.Reason)}");
        if (danced.Count > LogCap) sb.AppendLine($"  … +{danced.Count - LogCap} more");

        sb.AppendLine($"\n== Revit warnings during dance: {revitWarnings.Count} ==");
        foreach (var m in revitWarnings.Take(LogCap)) sb.AppendLine("  - " + m);
        if (revitWarnings.Count > LogCap) sb.AppendLine($"  … +{revitWarnings.Count - LogCap} more");

        if (dismissedDialogs.Count > 0)
        {
            sb.AppendLine($"\n== Revit prompts auto-dismissed: {dismissedDialogs.Count} ==");
            foreach (var d in dismissedDialogs.Distinct().Take(LogCap)) sb.AppendLine("  - " + d);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Collects (and quiets) Revit's own warnings during each per-type commit so they land in the log
    ///     instead of blocking on a dialog. Replicates Importer.ApplyFailureCollector. Warnings are deleted
    ///     (commit proceeds); errors are logged as-is.
    /// </summary>
    private sealed class DanceFailureCollector : IFailuresPreprocessor
    {
        public readonly List<string> Messages = new();
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
            {
                var sev = f.GetSeverity();
                string desc;
                try { desc = f.GetDescriptionText(); } catch { desc = "(failure)"; }
                int n = 0; try { n = f.GetFailingElementIds().Count; } catch { /* ignore */ }
                Messages.Add($"[{sev}] {desc}" + (n > 0 ? $" — {n} element(s)" : ""));
                if (sev == FailureSeverity.Warning) { try { a.DeleteWarning(f); } catch { /* ignore */ } }
            }
            return FailureProcessingResult.Continue;
        }
    }
}

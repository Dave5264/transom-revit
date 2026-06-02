using System.Collections.Generic;
using System.Linq;

namespace Transom.Core;

/// <summary>What to do about edits that target elements inside Revit groups.</summary>
public enum GroupDecision
{
    SkipGrouped,   // apply everything else, leave the grouped edits out
    Abort,         // cancel the whole import
    ClaudeHandle,  // hand the grouped edits to Claude (Assist) to open the groups and apply
}

/// <summary>How a group-member edit can be written durably (see project_revit_group_member_edits).</summary>
public enum GroupMode
{
    None,         // not in a group — write directly
    ProjectVary,  // project/shared param: Transom sets "vary by group instance" then writes per-instance (in-process)
    BuiltinDance, // built-in param: can't vary — needs the uniform definition-swap "dance" via Claude-assist
}

/// <summary>Context shown to the user when an import touches group members.</summary>
public sealed class GroupPrompt
{
    public List<ProposedChange> Grouped = new();
    public bool AssistEnabled;

    public List<string> GroupNames => Grouped
        .Select(g => string.IsNullOrEmpty(g.GroupName) ? "Group" : g.GroupName)
        .Distinct()
        .ToList();

    /// <summary>Total element writes blocked by group membership (bulk changes count each instance).</summary>
    public int InstanceCount => Grouped.Sum(g => g.BulkInstanceIds?.Count ?? 1);
}

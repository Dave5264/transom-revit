using System.Collections.Generic;
using System.Linq;

namespace Transom.Core;

/// <summary>How a group-member edit can be written durably (see project_revit_group_member_edits).</summary>
public enum GroupMode
{
    None,         // not in a group — write directly
    ProjectVary,  // project/shared param: Transom sets "vary by group instance" then writes per-instance (in-process)
    BuiltinDance, // built-in param: can't vary — needs the uniform definition-swap "dance" via Claude-assist
}

/// <summary>
///     How the user chose to resolve a group conflict for ONE blue/yellow column (parameter). Presented
///     per-parameter when applying an import that touches grouped members.
/// </summary>
public enum GroupResolution
{
    Vary,         // (BLUE only) flip the project param to "can vary by group instance" and write per-instance
    NewTypeParam, // put the values into a new TYPE parameter and add it to the affected schedules
    GroupDance,   // ungroup/edit/regroup/purge (staged for review; hands to Claude-assist meanwhile)
    ClaudeAssist, // launch ClickHelper and let Claude open each group + edit via API/UI
    Skip,         // leave this column unchanged
}

/// <summary>
///     One blue/yellow column's conflict, surfaced to the user as a multi-option choice (the
///     <c>GroupResolutionDialog</c>). Blue = project/shared param (offers all five paths); yellow =
///     built-in param (no vary path). <see cref="Option2Available"/> gates "new type parameter" to the
///     case where the column's values are consistent per type (a type param holds one value per type).
/// </summary>
public sealed class GroupResolutionPrompt
{
    public string Field = "";              // schedule column header / parameter name (for the heading)
    public int ParameterId;
    public bool IsBuiltin;                 // true = built-in (no vary, option 1); false = project (blue)
    public bool IsBroken;                  // true = "broken" group (member anchored outside the group, a nested
                                           // group, or mixed instance orientation) → dance can't run; RED, opts 2/4/5
    public string BrokenReason = "";       // what makes the group broken (offending elements), for the dialog note
    public bool Option2Available;          // values align per type across this whole column
    public bool AssistEnabled;             // Claude-assist on (gates the Claude-Assist option)
    public List<ProposedChange> Changes = new();

    public List<string> GroupNames => Changes
        .Select(c => string.IsNullOrEmpty(c.GroupName) ? "Group" : c.GroupName)
        .Distinct().ToList();

    /// <summary>Total element writes this column represents (bulk changes count each instance).</summary>
    public int InstanceCount => Changes.Sum(c => c.BulkInstanceIds?.Count ?? 1);
}

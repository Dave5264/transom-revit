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
    Vary,             // (BLUE only) flip the project param to "can vary by group instance" and write per-instance
    NewTypeParam,     // REPLACE the column with a new TYPE parameter (original values merged with edits)
    NewInstanceParam, // REPLACE the column with a new INSTANCE parameter (each element keeps its own value)
    ClaudeAssist,     // launch ClickHelper and let Claude open each group + edit via API/UI
    Skip,             // leave this column unchanged
}

/// <summary>
///     For an Option-2 (replace-the-column) column, how its TYPE-vs-INSTANCE binding was inferred from the
///     source schedule's organization (see <c>Importer.ComputeOption2Mode</c>). Drives which radio(s) the
///     <c>GroupResolutionDialog</c> offers and which one it recommends.
/// </summary>
public enum Option2Mode
{
    None,                   // option 2 not available for this column
    AutoType,               // schedule is grouped-by-type AND values align per type → a single type-param option
    AmbiguousPreferType,    // grouped by type but not cleanly auto → offer both, recommend TYPE
    AmbiguousPreferInstance,// itemized / not grouped by type → offer both, recommend INSTANCE
}

/// <summary>What to do with the OLD (source) parameter's values after an option-2a/2b conversion replaced its
/// column. The old values are no longer displayed (the column now shows the new parameter) but still live on
/// every element — the user decides their fate in the <c>Option2OldValuesDialog</c>.</summary>
public enum OldValueDisposition
{
    Leave,    // keep the old values on the elements (default — no data touched)
    Clear,    // blank the old parameter on every affected element (string columns only)
    SetValue, // write one user-entered value into the old parameter on every affected element
}

/// <summary>One OTHER schedule (not a source of the current import) that displays the source parameter an
/// option-2 conversion is replacing — a candidate for having its column replaced too. Found by
/// <c>Importer.ComputeOption2OtherSchedules</c> at preview time; offered in <c>Option2SchedulesDialog</c>.</summary>
public sealed class Option2ScheduleRef
{
    public string ScheduleName = "";
    public string ScheduleUid = "";
    /// <summary>The schedule's category id — bound to the new shared parameter when this schedule is ticked
    /// (its elements may be a different category than the edited ones, e.g. Comments on a window schedule).</summary>
    public long CategoryId = -1;
}

/// <summary>Prompt for the option-2 "also replace in these schedules" step: the OTHER schedules that display the
/// source parameter, each tickable. The dialog returns the ticked uids via its own Result (consumed by the
/// viewmodel's resolver callback) — this prompt only carries the display inputs.</summary>
public sealed class Option2SchedulesPrompt
{
    public string Field = "";          // the source parameter / column being replaced
    public string NewParamName = "";   // the confirmed new parameter name (for the dialog text)
    public List<Option2ScheduleRef> Schedules = new();
}

/// <summary>Prompt for the option-2 "what happens to the old values" step. The dialog writes the choice (and the
/// uniform replacement value for <see cref="OldValueDisposition.SetValue"/>) back onto this object.</summary>
public sealed class Option2OldValuesPrompt
{
    public string Field = "";          // the old parameter whose values are being dispositioned
    public string NewParamName = "";
    /// <summary>False when the old column stores numbers (a length etc.) — "clear" can't blank a numeric
    /// parameter, so the dialog disables that option with a note.</summary>
    public bool AllowClear = true;
    public OldValueDisposition Choice = OldValueDisposition.Leave;
    public string NewValue = "";
}

/// <summary>
///     One blue/yellow column's conflict, surfaced to the user as a multi-option choice (the
///     <c>GroupResolutionDialog</c>). Blue = project/shared param (offers all five paths); yellow =
///     built-in param (no vary path). <see cref="Option2Available"/> gates "new type parameter" to the
///     case where the column's values are consistent per type (a type param holds one value per type).
/// </summary>
public sealed class GroupResolutionPrompt
{
    public string Field = "";              // the underlying parameter's real name (Definition.Name)
    public string Header = "";             // the schedule column's custom display heading (ScheduleField.ColumnHeading),
                                           // when it differs from Field — shown in the dialog so a renamed column still
                                           // names the real parameter being changed (#93B). Empty / == Field ⇒ not shown.
    public int ParameterId;
    public bool IsBuiltin;                 // true = built-in (no vary, option 1); false = project (blue)
    public bool IsBroken;                  // true = the HARD dance gate tripped (level-anchored families, true
                                           // rotation, or multi-level) → option 3 unavailable. Mirror & bystander
                                           // nested do NOT set this (mapped from GroupSafetyResult.IsDanceGateBroken).
    public string BrokenReason = "";       // what makes the group broken (offending conditions), for the dialog note
    public List<string> BrokenFamilies = new(); // named level-anchored families to re-host to unlock the dance
                                           // ("CW_Closet Shelf and Pole (Casework) ×2"); drives the actionable block
    public bool Option2Available;          // option 2 (replace the column) is available for this column
    public Option2Mode Option2Mode = Option2Mode.None; // how type-vs-instance was inferred (drives the radios offered)
    public string BindingNote = "";        // muted explanation of the recommended binding, shown under the option-2 radios
    public bool AssistEnabled;             // Claude-assist on (gates the Claude-Assist option)
    public bool IsGeometryDriven;          // true = a geometry-driving built-in instance param (Sill/Head Height):
                                           // option 2 is suppressed (replacing it desyncs the 3D) → Claude-Assist or
                                           // Skip only; drives the accurate "why option 2 is unavailable" message.
    public List<ProposedChange> Changes = new();

    /// <summary>#97: the new parameter NAME the user confirmed/edited in the dialog when they chose option 2a (type)
    /// or 2b (instance). The dialog writes it back here; the apply path (<c>Importer.ApplyNewParam</c>) uses it as the
    /// created parameter's visible name instead of the hardcoded "&lt;field&gt; (Transom)" default. Empty ⇒ the user
    /// didn't pick a new-param option, so the default suggestion applies.</summary>
    public string ChosenParamName = "";

    public List<string> GroupNames => Changes
        .Select(c => string.IsNullOrEmpty(c.GroupName) ? "Group" : c.GroupName)
        .Distinct().ToList();

    /// <summary>Total element writes this column represents (bulk changes count each instance).</summary>
    public int InstanceCount => Changes.Sum(c => c.BulkInstanceIds?.Count ?? 1);
}

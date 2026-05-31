using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Transom.Core;

namespace Transom.ViewModels;

public sealed class ScheduleEntry
{
    public ScheduleEntry(ViewSchedule vs)
    {
        Id = vs.Id.Value;
        Name = vs.Name;
    }

    public long Id { get; }
    public string Name { get; }
}

public sealed partial class TransomViewModel : ObservableObject
{
    private readonly ExternalEvent _exportEvent;
    private readonly ExportEventHandler _handler;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    [ObservableProperty] private ScheduleEntry? _selectedSchedule;
    [ObservableProperty] private string _status = "Pick a schedule and export.";

    public TransomViewModel(Document doc, ViewSchedule? active, ExternalEvent exportEvent, ExportEventHandler handler)
    {
        _exportEvent = exportEvent;
        _handler = handler;
        _handler.ReportStatus = s => _ui.Invoke(() => Status = s);

        Schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
            .OrderBy(v => v.Name)
            .Select(v => new ScheduleEntry(v))
            .ToList();

        SelectedSchedule = active != null
            ? Schedules.FirstOrDefault(e => e.Id == active.Id.Value) ?? Schedules.FirstOrDefault()
            : Schedules.FirstOrDefault();
    }

    public List<ScheduleEntry> Schedules { get; }

    [RelayCommand]
    private void Export()
    {
        if (SelectedSchedule == null)
        {
            Status = "No schedule selected.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = SelectedSchedule.Name + ".xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        // Hand off to the ExternalEvent so the model work runs in Revit's API context.
        _handler.ScheduleId = SelectedSchedule.Id;
        _handler.OutputPath = dlg.FileName;
        Status = "Exporting…";
        _exportEvent.Raise();
    }

    [RelayCommand]
    private void CopyStatus()
    {
        try { System.Windows.Clipboard.SetText(Status ?? string.Empty); }
        catch { /* clipboard busy */ }
    }
}

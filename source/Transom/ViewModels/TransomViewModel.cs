using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Transom.Core;

namespace Transom.ViewModels;

public sealed class ScheduleEntry
{
    public ScheduleEntry(ViewSchedule vs)
    {
        Schedule = vs;
        Name = vs.Name;
    }

    public ViewSchedule Schedule { get; }
    public string Name { get; }
}

public sealed partial class TransomViewModel : ObservableObject
{
    private readonly Document _doc;

    [ObservableProperty] private ScheduleEntry? _selectedSchedule;
    [ObservableProperty] private string _status = "Pick a schedule and export.";

    public TransomViewModel(Document doc, ViewSchedule? active)
    {
        _doc = doc;
        Schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
            .OrderBy(v => v.Name)
            .Select(v => new ScheduleEntry(v))
            .ToList();

        SelectedSchedule = active != null
            ? Schedules.FirstOrDefault(e => e.Schedule.Id == active.Id) ?? Schedules.FirstOrDefault()
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

        try
        {
            var table = new ScheduleReader(_doc).Read(SelectedSchedule.Schedule);
            new ExcelWriter().Write(table, dlg.FileName);
            Status = $"Exported {table.ElementRowCount} element rows " +
                     $"({table.RowCount} rows x {table.ColCount} cols) to {dlg.FileName}";
        }
        catch (System.Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void CopyStatus()
    {
        try { System.Windows.Clipboard.SetText(Status ?? string.Empty); }
        catch { /* clipboard busy */ }
    }
}

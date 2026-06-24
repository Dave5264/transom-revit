# Design note — Schedule Hub doc-rebind / filter-reset

**Status: implemented** (`StartupCommand.OpenOrActivate` re-reads the live document and calls
`TransomViewModel.RefreshFromDocument`, which rebuilds Projects, reloads schedules, and clears the filter).

The Schedule Hub window held stale state across a document close+reopen. After an in-place doc reopen it
could fail to find schedules, and the filter retained stale text. Root cause was in the source; a
surgical fix at the proven entry point (`StartupCommand`) addressed it.

## 1. Root cause

### a. The Hub is an app-level modeless singleton that OUTLIVES the document
`TransomView.Instance` is a static singleton, set in the ctor and nulled only on `Closed`
(`Views/TransomView.xaml.cs:10,17-18`). The window survives doc close/reopen — it is never torn down
when the document changes.

### b. Re-invoking the Hub only `Activate()`d — it never refreshed
`Commands/StartupCommand.cs` (old):
```csharp
internal static TransomView OpenOrActivate(UIApplication app)
{
    if (TransomView.Instance != null)
    {
        TransomView.Instance.Activate();   // <-- focus only; NO data refresh, NO filter clear
        return TransomView.Instance;
    }
    ...
}
```
So pressing the **Schedule Hub** ribbon button after a doc reopen just focused the existing window with
whatever state it had.

### c. The schedule list was ONLY (re)loaded in two places, neither firing on doc reopen
1. **Construction** — `TransomViewModel` ctor takes the project list + schedules captured **once** by
   `StartupCommand` at first open. Runs only when the singleton is first created.
2. **User picks a different project in the in-window combo** — `OnSelectedProjectChanged` raises
   `_scheduleLoadEvent` → `ScheduleLoadEventHandler` reloads. Fires **only** on a `SelectedProject` value
   change driven by the ComboBox.

   Critically: after a close+**reopen of the same file**, `SelectedProject` still equalled the old
   `doc.Title` (unchanged on in-place reopen). No property change → no reload. Even when the title
   differed, nothing pushed the new title into `SelectedProject`, and `Projects` was never rebuilt, so the
   new doc might not even be a selectable option.

### d. No Revit document-lifecycle events were wired
Grep across `source\Transom\` for `DocumentOpened | DocumentClosed | ViewActivated |
ActiveDocumentChanged | DocumentChanged | Idling | ApplicationClosing` → **zero matches.** The add-in had
no hook that could refresh the Hub when the active document changed (`Application.cs` only builds the
ribbon + a one-time MCP registration task).

### e. The filter was independent state that nothing reset on re-entry
`ScheduleFilter` is a plain observable, two-way bound to the filter box. It was **never cleared**
programmatically — `SetSchedules` called `ApplyFilter()` but did **not** reset `ScheduleFilter`, so a
stale filter string survived even a successful reload and silently hid the new list.

### f. Stale `Id`s compounded the failure
`ScheduleEntry.Id`/`ActiveSchedule.Id` were the old document's `ElementId.Value`s. After a reopen the new
document has different element ids, so even a name-matched export could target wrong/absent ids if the
list wasn't rebuilt. (The import/export event handlers re-resolve by `DocTitle` via `DocUtil.Resolve`,
which is why writes sometimes still worked — but the **Hub list** the user picked from was stale.)

**Symptom → cause map:** "Hub list fails to find schedules after in-place reopen" = (b)+(c)+(f) no reload
on re-invoke; "filter retained stale text" = (e) filter never reset on re-entry. Observed empirically:
filter fouling persists across a doc close/reopen because the Hub is app-level; only closing+reopening
the Hub itself cleared it, and re-invoking `StartupCommand` merely focused the existing window.

## 2. The fix — refresh + filter-clear on Hub re-invoke

**Strategy:** fix at the single entry point (`StartupCommand.OpenOrActivate`). When the singleton exists,
re-read the live document state from `app` and push it into the ViewModel via `RefreshFromDocument(...)`,
which rebuilds Projects, reloads schedules for the active doc, **and clears the filter**. This avoids
subscribing a modeless window to `ViewActivated`/`DocumentOpened` (extra lifetime/unsubscribe + API
-context risk) and fixes both symptoms at the moment of user re-entry. `OpenOrActivate` already runs in
valid command (API) context and already has the `UIApplication`, so reading documents/active view there
is safe — no `ExternalEvent` needed.

### Change 1 — `source/Transom/Commands/StartupCommand.cs`
```csharp
    internal static TransomView OpenOrActivate(Autodesk.Revit.UI.UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument;
        var doc = uiDoc?.Document;

        // Re-read the LIVE document state so a re-invoke after a doc close/reopen rebinds the Hub
        // instead of showing the previous document's stale schedule list / filter.
        var projects = new List<string>();
        if (app.Application?.Documents != null)
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked && !d.IsFamilyDocument)
                    projects.Add(d.Title);

        var active = uiDoc?.ActiveView as ViewSchedule;
        var schedules = doc != null ? DocUtil.UserSchedules(doc) : new List<(long id, string name)>();

        if (TransomView.Instance != null)
        {
            // Existing window: rebind to the current document, then focus it.
            if (TransomView.Instance.DataContext is TransomViewModel existingVm)
                existingVm.RefreshFromDocument(
                    projects, doc?.Title ?? "", active?.Id.Value ?? 0, schedules);
            TransomView.Instance.Activate();
            return TransomView.Instance;
        }

        var exportHandler = new ExportEventHandler();
        var exportEvent = Autodesk.Revit.UI.ExternalEvent.Create(exportHandler);
        var importHandler = new ImportEventHandler();
        var importEvent = Autodesk.Revit.UI.ExternalEvent.Create(importHandler);
        var loadHandler = new ScheduleLoadEventHandler();
        var loadEvent = Autodesk.Revit.UI.ExternalEvent.Create(loadHandler);

        var viewModel = new TransomViewModel(
            projects, doc?.Title ?? "", active?.Id.Value ?? 0, schedules,
            exportEvent, exportHandler, importEvent, importHandler, loadEvent, loadHandler);
        var view = new TransomView(viewModel);
        new WindowInteropHelper(view) { Owner = app.MainWindowHandle };
        view.Show();
        return view;
    }
```
This widens a couple of derefs to null-safe (`uiDoc?`, `app.Application?`) since on a re-invoke we no
longer assume a schedule view is active. First-open behavior is unchanged when a doc is open.

### Change 2 — `source/Transom/ViewModels/TransomViewModel.cs`
A public refresh method that rebuilds Projects, reloads schedules, **and clears the filter**, reusing
the existing `SetSchedules` (which already calls `ApplyFilter()` + `UpdateSelectionInfo()`):
```csharp
    /// <summary>
    ///     Rebinds the Hub to the (possibly new) active document — used when the Schedule Hub button is
    ///     pressed again after a document close/reopen. Rebuilds the project list, reloads the schedule
    ///     list for the active document, and clears any stale filter so the fresh list isn't hidden.
    /// </summary>
    public void RefreshFromDocument(
        List<string> projects, string activeProjectTitle,
        long activeScheduleId, List<(long id, string name)> schedules)
    {
        // Rebuild the project list in place (preserve the bound ObservableCollection instance).
        Projects.Clear();
        foreach (var p in projects) Projects.Add(p);

        // Point at the active document WITHOUT triggering a redundant async reload: we already have
        // its schedules in hand. Setting the backing field skips OnSelectedProjectChanged's event raise.
        _selectedProject = activeProjectTitle;
        OnPropertyChanged(nameof(SelectedProject));

        ScheduleFilter = "";        // clear stale filter (also re-runs ApplyFilter via its partial)
        SetSchedules(activeScheduleId, schedules);   // ApplyFilter() + UpdateSelectionInfo()
    }
```
Why set the backing field `_selectedProject` rather than the property: the public setter's
`OnSelectedProjectChanged` raises `_scheduleLoadEvent` to async-reload schedules for the chosen title —
but we *already* loaded the correct schedules synchronously in `StartupCommand`, so using the property
would fire a second, redundant reload. Setting the field + manual `OnPropertyChanged` keeps the ComboBox
in sync without the extra round-trip. Setting `ScheduleFilter = ""` is a no-op write-through if it was
already empty, and clears it otherwise — directly fixing the stale-filter symptom.

## 3. Optional hardening (automatic rebind without a re-click)
To rebind the instant the user switches/reopens a document *without* pressing the ribbon button,
subscribe to `UIApplication.ViewActivated` in `Application.OnStartup` and route to the same refresh. This
is larger and higher-risk (event lifetime, must marshal to the WPF dispatcher, must early-out when
`TransomView.Instance == null`), and `ExternalApplication.OnStartup()` would need the
`OnStartup(UIControlledApplication)` overload to wire `ViewActivated`. Deferred in favor of the re-invoke
fix above.

## 4. Risk / regression assessment
- **Scope:** two files, both on the Hub-open path. No change to export/import/group logic, Excel I/O, or
  event handlers. `ScheduleLoadEventHandler` and `OnSelectedProjectChanged` (the in-window project switch)
  are untouched.
- **First-open path:** functionally identical (same gather, same ctor call); only made null-safe.
- **Re-invoke with same doc still open:** Projects rebuilt to the same set, schedules reloaded (cheap
  `FilteredElementCollector`), filter cleared, selection reset to "n of m". Re-pressing the ribbon button
  is an explicit user action; a fresh, correct list is the expected outcome. (If preserving checkboxes on
  a same-doc re-invoke is ever wanted, gate the refresh on a title change.)
- **No doc open at re-invoke:** `schedules` = empty, `SelectedProject` = "", filter cleared; the Hub shows
  an empty list rather than the prior doc's stale one. Safe.
- **Build:** pure C#, no new dependencies; builds under R25/R26/R27.

## 5. How to verify
1. Open model A, open Schedule Hub, type a filter (e.g. `LEVEL`) → list narrows.
2. Close the document, reopen the SAME file in place.
3. Press **Schedule Hub** again. Expect: filter box EMPTY, full schedule list present, schedules findable.
   (Pre-fix: filter retained `LEVEL`, list stale/empty.)
4. Pick a schedule + export → confirm ids resolve against the reopened doc.
5. Regression: the in-window project ComboBox still switches projects (option flows unaffected).

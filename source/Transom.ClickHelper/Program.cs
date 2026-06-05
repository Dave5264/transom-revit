using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Transom.ClickHelper;

/// <summary>
///     Standalone helper that finds and clicks Revit ribbon buttons that have no Revit API
///     equivalent — primarily <c>Edit Group</c> and <c>Finish</c> (finish group edit mode) — by
///     driving the live Revit window through Windows UI Automation.
///
///     <para>
///     Usage:  <c>RevitGroupClick &lt;command&gt; [options]</c>
///     </para>
///     <para>Commands:</para>
///     <list type="bullet">
///       <item><c>edit</c>    — invoke "Edit Group" (a single model group must be selected).</item>
///       <item><c>finish</c>  — invoke "Finish" in the Edit Group panel (must be editing a group).</item>
///       <item><c>cancel</c>  — invoke "Cancel" in the Edit Group panel.</item>
///       <item><c>list</c>    — diagnostic: dump candidate ribbon buttons (name/id/help/patterns).</item>
///       <item><c>status</c>  — report which Revit process/window was found.</item>
///     </list>
///     <para>Options:</para>
///     <list type="bullet">
///       <item><c>--pid N</c>        target a specific Revit process id (default: the single running Revit).</item>
///       <item><c>--timeout MS</c>   how long to keep retrying the search (default 4000 ms).</item>
///       <item><c>--json</c>         emit a single JSON line on stdout (used by the MCP shim).</item>
///       <item><c>--activate</c>     bring the Revit window to the foreground before invoking.</item>
///       <item><c>--all</c>          (with <c>list</c>) dump every button, not just group-related ones.</item>
///     </list>
///
///     Exit code 0 = the button was found and invoked; non-zero = anything else (with a reason).
/// </summary>
internal static class Program
{
    private static bool _json;

    private static int Main(string[] rawArgs)
    {
        // Declare per-monitor DPI awareness BEFORE any window/coordinate work so screen rectangles,
        // cursor positioning, and screen capture all operate in true physical pixels.
        TryBecomeDpiAware();

        var args = new ArgSet(rawArgs);
        _json = args.Has("--json");
        var command = args.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "";

        const string usage = "edit | finish | cancel | click-id <id> | click-xy <x> <y> | " +
                             "key <combo> | type <text> | find <text> | dialogs | click-dialog <button> | " +
                             "tile | screenshot | list | status";
        try
        {
            return command switch
            {
                "edit"         => RunInvoke(args, ButtonSpec.EditGroup),
                "finish"       => RunInvoke(args, ButtonSpec.FinishGroup),
                "cancel"       => RunInvoke(args, ButtonSpec.CancelGroup),
                "click-id"     => RunClickById(args),
                "click-xy"     => RunClickXy(args),
                "key"          => RunKey(args),
                "keys"         => RunKeys(args),
                "type"         => RunType(args),
                "scroll"       => RunScroll(args),
                "find"         => RunFind(args),
                "dialogs"      => RunDialogs(args),
                "click-dialog" => RunClickDialog(args),
                "tile"         => RunTile(args),
                "screenshot"   => RunScreenshot(args),
                "list"         => RunList(args),
                "status"       => RunStatus(args),
                ""             => Fail("no command given. Try: " + usage, 2),
                _              => Fail($"unknown command '{command}'. Try: " + usage, 2),
            };
        }
        catch (Exception ex)
        {
            return Fail("unexpected error: " + ex.Message, 3);
        }
    }

    // ---------------------------------------------------------------- commands

    private static int RunInvoke(ArgSet args, ButtonSpec spec)
    {
        if (!TryGetRevitRoot(args, out var root, out var proc, out var error))
            return Fail(error, 4);

        int timeoutMs = args.GetInt("--timeout", 4000);
        if (args.Has("--activate")) TryActivate(proc!);

        // The contextual ribbon tab (and its buttons) can take a beat to appear after a selection
        // or after entering edit mode, so retry the search until the deadline.
        var deadline = Environment.TickCount64 + timeoutMs;
        AutomationElement? match = null;
        var ambiguity = 0;
        do
        {
            var candidates = FindControls(root!, spec);
            if (candidates.Count > 0)
            {
                match = candidates[0].Element;
                ambiguity = candidates.Count;
                break;
            }
            Thread.Sleep(150);
        } while (Environment.TickCount64 < deadline);

        if (match == null)
            return Fail(
                $"'{spec.DisplayName}' button not found. {spec.NotFoundHint}",
                5);

        var (invoked, how, invokeError) = InvokeElement(match, proc!);
        if (!invoked)
            return Fail($"found '{spec.DisplayName}' but could not invoke it: {invokeError}", 6);

        return Ok(new()
        {
            ["action"] = spec.Verb,
            ["button"] = SafeName(match),
            ["invokedVia"] = how,
            ["candidates"] = ambiguity,
            ["pid"] = proc!.Id,
        });
    }

    /// <summary>
    ///     Element-first general click: resolve a control by its UI Automation AutomationId and invoke it.
    ///     This is how Claude clicks any named Revit command without screenshots — e.g.
    ///     <c>click-id ID_FINISH_GROUP_EDIT_MODE</c>.
    /// </summary>
    private static int RunClickById(ArgSet args)
    {
        string? id = args.Positional.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id)) return Fail("click-id needs an automation id, e.g. click-id 2007", 2);

        if (!TryGetRevitRoot(args, out var root, out var proc, out var error)) return Fail(error, 4);

        int timeoutMs = args.GetInt("--timeout", 4000);
        var deadline = Environment.TickCount64 + timeoutMs;
        AutomationElement? match = null;
        do
        {
            // Prefer an on-screen, invokable element with this id (AdWindows often has an offscreen twin).
            var hits = root!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            match = PickBest(hits);
            if (match != null) break;
            Thread.Sleep(150);
        } while (Environment.TickCount64 < deadline);

        if (match == null) return Fail($"no control with automation id '{id}' is currently available.", 5);

        var (invoked, how, invokeError) = InvokeElement(match, proc!);
        if (!invoked) return Fail($"found id '{id}' but could not invoke it: {invokeError}", 6);

        return Ok(new()
        {
            ["action"] = "click-id",
            ["automationId"] = id,
            ["name"] = SafeName(match),
            ["invokedVia"] = how,
            ["pid"] = proc!.Id,
        });
    }

    /// <summary>
    ///     Pixel fallback: click an absolute screen coordinate. This is the diagram's "Pixel ID for Click"
    ///     path — used only when there is no named element to target.
    /// </summary>
    private static int RunClickXy(ArgSet args)
    {
        var nums = args.Positional.Skip(1).ToList();
        if (nums.Count < 2 ||
            !int.TryParse(nums[0], out int x) || !int.TryParse(nums[1], out int y))
            return Fail("click-xy needs two integers: click-xy <x> <y> (absolute screen pixels)", 2);

        // Bring Revit forward so the click lands on it (best effort; ok if there is no single Revit).
        if (TryGetRevitRoot(args, out _, out var proc, out _)) TryActivate(proc!);

        GetCursorPos(out int savedX, out int savedY);
        ClickAt(x, y);
        if (!args.Has("--keep-cursor")) SetCursorPos(savedX, savedY);

        return Ok(new() { ["action"] = "click-xy", ["x"] = x, ["y"] = y });
    }

    /// <summary>
    ///     Tile Revit and the other foreground app (Claude) side by side on Revit's monitor, so Revit is
    ///     visible and not occluded — which is what makes physical clicks and keyboard input land on it.
    ///     Revit goes left by default (<c>--revit-side right</c> to flip). The "other" window auto-detects
    ///     Claude (Chrome_WidgetWin_1 titled "Claude"), or pass <c>--other &lt;hwnd&gt;</c>.
    /// </summary>
    private static int RunTile(ArgSet args)
    {
        if (!TryGetRevitRoot(args, out _, out var proc, out var error) || proc is null) return Fail(error, 4);
        var revit = GetBestRevitWindow(proc);
        if (revit == IntPtr.Zero) return Fail("no Revit window to tile.", 5);

        if (!GetWorkArea(revit, out var wa)) return Fail("could not read the monitor work area.", 5);
        int halfW = (wa.Right - wa.Left) / 2, h = wa.Bottom - wa.Top;
        bool revitLeft = !string.Equals(args.GetString("--revit-side"), "right", StringComparison.OrdinalIgnoreCase);

        (int x, int y, int w, int h) revitRect = revitLeft
            ? (wa.Left, wa.Top, halfW, h)
            : (wa.Left + halfW, wa.Top, halfW, h);
        (int x, int y, int w, int h) otherRect = revitLeft
            ? (wa.Left + halfW, wa.Top, halfW, h)
            : (wa.Left, wa.Top, halfW, h);

        IntPtr other = IntPtr.Zero;
        if (args.GetString("--other") is { } os && long.TryParse(os, out var oh)) other = new IntPtr(oh);
        else other = FindOtherWindow(proc);

        ShowWindow(revit, SW_RESTORE);
        SetWindowPos(revit, IntPtr.Zero, revitRect.x, revitRect.y, revitRect.w, revitRect.h, SWP_NOZORDER | SWP_NOACTIVATE);

        bool movedOther = false;
        if (other != IntPtr.Zero)
        {
            ShowWindow(other, SW_RESTORE);
            SetWindowPos(other, IntPtr.Zero, otherRect.x, otherRect.y, otherRect.w, otherRect.h, SWP_NOZORDER | SWP_NOACTIVATE);
            movedOther = true;
        }
        ForceForeground(revit);   // leave Revit interactable

        return Ok(new()
        {
            ["action"] = "tile",
            ["revit"] = $"{revitRect.x},{revitRect.y},{revitRect.w},{revitRect.h}",
            ["other"] = movedOther ? $"{otherRect.x},{otherRect.y},{otherRect.w},{otherRect.h}" : "none",
            ["otherHwnd"] = other.ToInt64(),
            ["pid"] = proc.Id,
        });
    }

    /// <summary>Finds the window to tile opposite Revit — prefers the Claude desktop app, else the largest non-Revit window.</summary>
    private static IntPtr FindOtherWindow(Process revit)
    {
        uint rpid = (uint)revit.Id;
        IntPtr claude = IntPtr.Zero, largest = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid == rpid) return true;
            if (!IsWindowVisible(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;

            var title = GetTitle(h);
            var cls = GetClass(h);
            if (cls.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) &&
                title.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
            { claude = h; return false; }   // found Claude — stop

            if (GetWindowRect(h, out var r))
            {
                long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (a > bestArea) { bestArea = a; largest = h; }
            }
            return true;
        }, IntPtr.Zero);
        return claude != IntPtr.Zero ? claude : largest;
    }

    private static bool GetWorkArea(IntPtr hwnd, out RECT wa)
    {
        wa = default;
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        wa = mi.rcWork;
        return true;
    }

    private static string GetTitle(IntPtr h)
    {
        try { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
        catch { return ""; }
    }

    /// <summary>
    ///     Send a key combo to Revit (e.g. <c>ctrl+a</c>, <c>enter</c>, <c>esc</c>, <c>tab</c>, <c>f2</c>).
    ///     Revit is brought to the foreground first so the keystroke lands there (Claude's permission
    ///     prompts can steal focus). Needed because the Properties palette is not UIA-addressable, so
    ///     editing a parameter value must go through real keyboard input.
    /// </summary>
    private static int RunKey(ArgSet args)
    {
        string combo = args.Positional.Skip(1).FirstOrDefault() ?? "";
        if (combo.Length == 0) return Fail("key needs a combo, e.g. key ctrl+a | key enter | key esc", 2);

        if (!TryGetRevitRoot(args, out _, out var proc, out _) || proc is null)
            return Fail("Revit not found.", 4);
        if (!MaybeClickAt(proc, args)) FocusByClick(proc);

        if (!Keyboard.TrySendCombo(combo, out var err)) return Fail(err, 6);
        return Ok(new() { ["action"] = "key", ["combo"] = combo });
    }

    /// <summary>
    ///     Send a sequence of plain key presses to Revit — e.g. <c>keys tl</c> for the Thin Lines shortcut,
    ///     <c>keys gp</c>, etc. Unlike <c>type</c> (which sends Unicode/WM_CHAR for filling a text field),
    ///     this sends real virtual-key down/up events so Revit's keyboard-shortcut handler recognizes them.
    ///     Revit shortcuts are case-insensitive; no Enter is appended.
    /// </summary>
    private static int RunKeys(ArgSet args)
    {
        string seq = string.Concat(args.Positional.Skip(1));
        if (seq.Length == 0) return Fail("keys needs a shortcut, e.g. keys tl", 2);

        if (!TryGetRevitRoot(args, out _, out var proc, out _) || proc is null)
            return Fail("Revit not found.", 4);
        // Refocus IN THE SAME PROCESS, immediately before the keystrokes, so a permission prompt can't
        // steal focus in between. --at=X,Y clicks that screen point (e.g. an empty canvas spot for a view
        // shortcut); with no --at we click the active view tab.
        if (!MaybeClickAt(proc, args)) FocusByClick(proc);
        Keyboard.TypeVkSequence(seq);
        return Ok(new() { ["action"] = "keys", ["keys"] = seq });
    }

    /// <summary>
    ///     Type literal text into Revit's foreground field (e.g. the Properties value cell you just
    ///     clicked into). Does NOT press Enter — follow with <c>key enter</c> to commit.
    /// </summary>
    private static int RunType(ArgSet args)
    {
        // Everything after the verb is the text (so it can contain spaces).
        string text = string.Join(' ', args.Positional.Skip(1));
        if (text.Length == 0) return Fail("type needs text, e.g. type TRANSOM-EG-001", 2);

        // ALWAYS click-to-refocus right before typing, in THIS process, so a permission prompt can't drop
        // the field focus between a separate click and the type. Pass --at=X,Y with the field's screen
        // coords (the cell you want to fill). Without --at we just type to whatever currently has focus.
        IntPtr revit = IntPtr.Zero;
        if (TryGetRevitRoot(args, out _, out var proc, out _) && proc is not null)
        { MaybeClickAt(proc, args); revit = GetBestRevitWindow(proc); }

        IntPtr fg = GetForegroundWindow();          // who actually receives the keystrokes?
        uint sent = Keyboard.TypeText(text);        // SendInput returns how many events it injected
        // --enter commits the field in the SAME process (a separate `key enter` would refocus the view tab
        // and discard the just-typed value).
        if (args.Has("--enter")) Keyboard.TrySendCombo("enter", out _);
        return Ok(new()
        {
            ["action"] = "type",
            ["text"] = text,
            ["committed"] = args.Has("--enter"),
            ["sentEvents"] = (int)sent,
            ["fgIsRevit"] = fg == revit && revit != IntPtr.Zero,
            ["fgHwnd"] = fg.ToInt64(),
            ["revitHwnd"] = revit.ToInt64(),
        });
    }

    /// <summary>
    ///     Scroll the mouse wheel at a screen point — e.g. to bring a parameter into view in the Properties
    ///     palette before clicking its value cell. <c>scroll X Y NOTCHES</c>; negative notches scroll down.
    ///     The wheel goes to the control under the cursor (positional, like a click), then the cursor is
    ///     restored.
    /// </summary>
    private static int RunScroll(ArgSet args)
    {
        var nums = args.Positional.Skip(1).ToList();
        if (nums.Count < 3 || !int.TryParse(nums[0], out int x) || !int.TryParse(nums[1], out int y) || !int.TryParse(nums[2], out int notches))
            return Fail("scroll needs: scroll <x> <y> <notches>  (negative notches scroll down)", 2);

        GetCursorPos(out int sx, out int sy);
        SetCursorPos(x, y);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(notches * 120)), UIntPtr.Zero);
        Thread.Sleep(40);
        SetCursorPos(sx, sy);
        return Ok(new() { ["action"] = "scroll", ["x"] = x, ["y"] = y, ["notches"] = notches });
    }

    /// <summary>Discovery: return controls whose name/help/id contains the given text, with rects + ids.</summary>
    private static int RunFind(ArgSet args)
    {
        string needle = (args.Positional.Skip(1).FirstOrDefault() ?? "").ToLowerInvariant();
        if (needle.Length == 0) return Fail("find needs a search string, e.g. find finish", 2);
        if (!TryGetRevitRoot(args, out var root, out var proc, out var error)) return Fail(error, 4);

        var rows = new List<Dictionary<string, object?>>();
        foreach (AutomationElement e in root!.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string name = SafeName(e);
            string help = SafeProp(e, AutomationElement.HelpTextProperty);
            string autoId = SafeProp(e, AutomationElement.AutomationIdProperty);
            if (!($"{name} {help} {autoId}".ToLowerInvariant().Contains(needle))) continue;
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(autoId)) continue;

            var rect = SafeRect(e);
            rows.Add(new()
            {
                ["name"] = name,
                ["controlType"] = SafeControlType(e),
                ["automationId"] = autoId,
                ["enabled"] = SafeBool(e, AutomationElement.IsEnabledProperty),
                ["onscreen"] = !SafeBool(e, AutomationElement.IsOffscreenProperty),
                ["invokable"] = SafeBool(e, AutomationElement.IsInvokePatternAvailableProperty),
                ["centerX"] = rect is { } r ? (int)(r.Left + r.Width / 2) : (object?)null,
                ["centerY"] = rect is { } r2 ? (int)(r2.Top + r2.Height / 2) : (object?)null,
            });
        }

        if (_json)
            Console.WriteLine(Json.Object(new() { ["ok"] = true, ["pid"] = proc!.Id, ["count"] = rows.Count, ["matches"] = rows }));
        else
        {
            Console.WriteLine($"{rows.Count} match(es) for '{needle}':");
            foreach (var r in rows)
                Console.WriteLine($"  • {Pad(r["name"]?.ToString(), 24)} {Pad(r["controlType"]?.ToString(), 8)} " +
                                  $"id={Pad(r["automationId"]?.ToString(), 24)} on={r["onscreen"]} inv={r["invokable"]} " +
                                  $"center=({r["centerX"]},{r["centerY"]})");
        }
        return 0;
    }

    /// <summary>
    ///     Capture the Revit main window to a PNG and report its path + screen rect. This is the diagram's
    ///     "Screenshots / UI Visual Data" — the MCP server returns the image to Claude.
    /// </summary>
    private static int RunScreenshot(ArgSet args)
    {
        if (!TryGetRevitRoot(args, out _, out var proc, out var error) || proc is null) return Fail(error, 4);

        var hwnd = ResolveTargetWindow(args, proc);
        if (!GetWindowRect(hwnd, out var rc))
            return Fail("could not read the Revit window rectangle.", 5);

        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return Fail("Revit window has no visible area (minimized?).", 5);

        string outPath = args.Positional.Skip(1).FirstOrDefault()
                         ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"revit_ui_{proc.Id}.png");

        // Default: PrintWindow with PW_RENDERFULLCONTENT — captures Revit's OWN pixels even when it is
        // behind other windows, and WITHOUT stealing focus. Ideal for "show me the ribbon" UI inspection.
        // The hardware-accelerated 3D viewport may come back blank this way; pass --screen to instead bring
        // Revit forward and grab the composited screen (faithful viewport, but it takes focus).
        bool useScreen = args.Has("--screen");
        string method = useScreen ? "screen" : "printwindow";

        using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                bool captured = false;
                if (!useScreen)
                {
                    IntPtr hdc = g.GetHdc();
                    try { captured = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (!captured)
                {
                    // Screen-grab fallback needs Revit visible and on top.
                    TryActivate(proc);
                    using var g2 = System.Drawing.Graphics.FromImage(bmp);
                    g2.CopyFromScreen(rc.Left, rc.Top, 0, 0, new System.Drawing.Size(w, h));
                    method = useScreen ? "screen" : "screen-fallback";
                }
            }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        return Ok(new()
        {
            ["action"] = "screenshot",
            ["method"] = method,
            ["path"] = outPath,
            ["x"] = rc.Left, ["y"] = rc.Top, ["width"] = w, ["height"] = h,
            ["pid"] = proc.Id,
        });
    }

    /// <summary>
    ///     List Revit's open modal dialogs. These are SEPARATE top-level windows owned by the Revit
    ///     process (e.g. the "Changes to groups are allowed only in group edit mode" error) — they are
    ///     NOT in the main window's element tree, so the normal find/list won't see them. Enumerated via
    ///     top-level window enumeration, then read with UI Automation for their buttons.
    /// </summary>
    private static int RunDialogs(ArgSet args)
    {
        if (!TryGetRevitRoot(args, out _, out var proc, out var error) || proc is null) return Fail(error, 4);

        var dialogs = ProcessDialogWindows(proc);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var h in dialogs)
        {
            AutomationElement dlg;
            try { dlg = AutomationElement.FromHandle(h); } catch { continue; }
            if (dlg is null) continue;

            var buttons = new List<string>();
            foreach (AutomationElement b in dlg.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                var bn = Normalize(SafeName(b));
                if (bn.Length > 0 && SafeBool(b, AutomationElement.IsEnabledProperty)) buttons.Add(bn);
            }
            // A real dialog has buttons; skip chrome/owned windows that have none.
            if (buttons.Count == 0) continue;

            rows.Add(new()
            {
                ["title"] = SafeName(dlg),
                ["hwnd"] = h.ToInt64(),
                ["text"] = DialogText(dlg),
                ["buttons"] = buttons,
            });
        }

        if (_json)
            Console.WriteLine(Json.Object(new() { ["ok"] = true, ["pid"] = proc.Id, ["count"] = rows.Count, ["dialogs"] = rows }));
        else
        {
            Console.WriteLine($"{rows.Count} open Revit dialog(s):");
            foreach (var r in rows)
                Console.WriteLine($"  • '{r["title"]}'  buttons=[{string.Join(", ", (List<string>)r["buttons"]!)}]\n     text: {r["text"]}");
        }
        return 0;
    }

    /// <summary>
    ///     Click a button in an open Revit modal dialog. Give the button name (e.g. <c>Cancel</c>,
    ///     <c>Ungroup</c>, <c>OK</c>); with no name it safe-dismisses by trying Cancel then Close. Brings
    ///     the dialog to the foreground first (Claude's permission prompts can steal focus), then invokes.
    /// </summary>
    private static int RunClickDialog(ArgSet args)
    {
        if (!TryGetRevitRoot(args, out _, out var proc, out var error) || proc is null) return Fail(error, 4);

        string? want = args.Positional.Skip(1).FirstOrDefault();
        var wants = want != null
            ? new List<string> { Normalize(want) }
            : new List<string> { "cancel", "close" };   // safe default dismiss

        int timeoutMs = args.GetInt("--timeout", 4000);
        var deadline = Environment.TickCount64 + timeoutMs;

        var availableSeen = new List<string>();
        do
        {
            foreach (var h in ProcessDialogWindows(proc))
            {
                AutomationElement dlg;
                try { dlg = AutomationElement.FromHandle(h); } catch { continue; }
                if (dlg is null) continue;

                var buttons = dlg.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                if (buttons.Count == 0) continue;

                // record what's available (for a helpful error)
                foreach (AutomationElement b in buttons)
                {
                    var bn = Normalize(SafeName(b));
                    if (bn.Length > 0 && !availableSeen.Contains(bn)) availableSeen.Add(bn);
                }

                foreach (var w in wants)
                {
                    foreach (AutomationElement b in buttons)
                    {
                        if (!SafeBool(b, AutomationElement.IsEnabledProperty)) continue;
                        var bn = Normalize(SafeName(b));
                        if (bn != w && !bn.Contains(w)) continue;

                        // Capture names BEFORE invoking — the elements are destroyed when the dialog closes.
                        string dlgTitle = SafeName(dlg), btnName = SafeName(b);
                        ForceForeground(h);                       // beat Claude's focus-steal
                        var (ok, how, err) = InvokeButton(b, h);
                        if (ok)
                            return Ok(new()
                            {
                                ["action"] = "click-dialog",
                                ["dialog"] = dlgTitle,
                                ["button"] = btnName,
                                ["invokedVia"] = how,
                                ["pid"] = proc.Id,
                            });
                        return Fail($"found dialog button '{btnName}' but could not invoke it: {err}", 6);
                    }
                }
            }
            Thread.Sleep(150);
        } while (Environment.TickCount64 < deadline);

        if (availableSeen.Count == 0)
            return Fail("no open Revit dialog found.", 5);
        return Fail($"no dialog button matched [{string.Join(", ", wants)}]. Available: [{string.Join(", ", availableSeen)}]", 5);
    }

    /// <summary>Invoke a dialog button: InvokePattern, else mouse-click its rect centre (dialog already foreground).</summary>
    private static (bool ok, string how, string error) InvokeButton(AutomationElement el, IntPtr dialogHwnd)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern inv)
            {
                inv.Invoke();
                return (true, "InvokePattern", "");
            }
        }
        catch (Exception ex) { _ = ex; }

        try
        {
            var rect = SafeRect(el);
            if (rect is not { Width: > 0, Height: > 0 }) return (false, "", "no invoke pattern and no rect");
            int cx = (int)Math.Round(rect.Value.Left + rect.Value.Width / 2);
            int cy = (int)Math.Round(rect.Value.Top + rect.Value.Height / 2);
            ForceForeground(dialogHwnd);
            GetCursorPos(out int sx, out int sy);
            ClickAt(cx, cy);
            SetCursorPos(sx, sy);
            return (true, "MouseClick", "");
        }
        catch (Exception ex) { return (false, "", ex.Message); }
    }

    /// <summary>
    ///     Visible top-level windows owned by the process, excluding its main window — i.e. Revit's native
    ///     modal dialogs. Skips .NET WinForms helper windows (pyRevit output monitor, telemetry, etc.,
    ///     class "WindowsForms10.*") and tiny/parked windows, so a default safe-dismiss never clicks them.
    /// </summary>
    private static List<IntPtr> ProcessDialogWindows(Process proc)
    {
        var main = GetBestRevitWindow(proc);
        var list = new List<IntPtr>();
        uint target = (uint)proc.Id;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid != target) return true;
            if (h == main) return true;
            if (!IsWindowVisible(h)) return true;

            var cls = GetClass(h);
            if (cls.StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return true; // helper windows
            if (GetWindowRect(h, out var r))
            {
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (w < 150 || ht < 90) return true;     // too small to be a real Revit dialog
            }
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string GetClass(IntPtr h)
    {
        try { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
        catch { return ""; }
    }

    /// <summary>First few lines of a dialog's text (the Text elements), for context.</summary>
    private static string DialogText(AutomationElement dlg)
    {
        var parts = new List<string>();
        foreach (AutomationElement t in dlg.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
        {
            var s = SafeName(t).Trim();
            if (s.Length > 0 && !parts.Contains(s)) parts.Add(s);
            if (parts.Count >= 4) break;
        }
        return string.Join(" / ", parts);
    }

    /// <summary>
    ///     Reliably bring a window to the foreground. Plain SetForegroundWindow is blocked by Windows'
    ///     foreground lock when the caller isn't already the foreground process (exactly our case — Claude's
    ///     permission prompts leave Claude foreground). The fix is to AttachThreadInput to the current
    ///     foreground thread for the duration of the call, which lifts the lock. Load-bearing for every
    ///     keystroke and mouse-click — without it, input silently goes to the wrong window.
    /// </summary>
    private static void ForceForeground(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        try
        {
            ShowWindow(h, SW_SHOW);
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint thisThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
            try
            {
                BringWindowToTop(h);
                SetForegroundWindow(h);
                SetFocus(h);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, fgThread, false);
            }
            Thread.Sleep(90);
        }
        catch { /* best effort */ }
    }

    /// <summary>Among same-id/same-name hits, prefer enabled + on-screen + invokable, else first non-null.</summary>
    private static AutomationElement? PickBest(AutomationElementCollection hits)
    {
        AutomationElement? best = null;
        int bestScore = int.MinValue;
        foreach (AutomationElement e in hits)
        {
            int s = 0;
            if (SafeBool(e, AutomationElement.IsEnabledProperty)) s += 4;
            if (!SafeBool(e, AutomationElement.IsOffscreenProperty)) s += 2;
            if (SafeBool(e, AutomationElement.IsInvokePatternAvailableProperty)) s += 1;
            if (s > bestScore) { bestScore = s; best = e; }
        }
        return best;
    }

    private static int RunList(ArgSet args)
    {
        if (!TryGetRevitRoot(args, out var root, out var proc, out var error))
            return Fail(error, 4);

        bool all = args.Has("--all");
        var rows = new List<Dictionary<string, object?>>();

        // Scan ALL control types — the ribbon commands we care about are Panes, not Buttons.
        var els = root!.FindAll(TreeScope.Descendants, Condition.TrueCondition);

        foreach (AutomationElement b in els)
        {
            string name = SafeName(b);
            string help = SafeProp(b, AutomationElement.HelpTextProperty);
            string autoId = SafeProp(b, AutomationElement.AutomationIdProperty);
            string hay = (name + " " + help + " " + autoId).ToLowerInvariant();

            bool relevant = hay.Contains("group") || hay.Contains("finish") ||
                            hay.Contains("cancel") || hay.Contains("edit");
            if (!all && !relevant) continue;
            if (string.IsNullOrEmpty(name)) continue;

            var rect = SafeRect(b);
            rows.Add(new()
            {
                ["name"] = name,
                ["controlType"] = SafeControlType(b),
                ["automationId"] = autoId,
                ["help"] = help,
                ["enabled"] = SafeBool(b, AutomationElement.IsEnabledProperty),
                ["onscreen"] = !SafeBool(b, AutomationElement.IsOffscreenProperty),
                ["invokable"] = SafeBool(b, AutomationElement.IsInvokePatternAvailableProperty),
                ["rect"] = rect is { } r ? $"{(int)r.Left},{(int)r.Top},{(int)r.Width},{(int)r.Height}" : "",
            });
        }

        if (_json)
        {
            Console.WriteLine(Json.Object(new()
            {
                ["ok"] = true,
                ["pid"] = proc!.Id,
                ["count"] = rows.Count,
                ["controls"] = rows,
            }));
        }
        else
        {
            Console.WriteLine($"Revit pid {proc!.Id} — {rows.Count} control(s){(all ? "" : " matching group/edit/finish/cancel")}:");
            foreach (var r in rows)
                Console.WriteLine(
                    $"  • {Pad(r["name"]?.ToString(), 24)} {Pad(r["controlType"]?.ToString(), 8)} " +
                    $"id={Pad(r["automationId"]?.ToString(), 16)} en={r["enabled"]} on={r["onscreen"]} " +
                    $"inv={r["invokable"]} rect={r["rect"]}");
        }
        return 0;
    }

    private static int RunStatus(ArgSet args)
    {
        if (!TryGetRevitRoot(args, out var root, out var proc, out var error))
            return Fail(error, 4);

        var info = new Dictionary<string, object?>
        {
            ["pid"] = proc!.Id,
            ["processName"] = proc.ProcessName,
            ["window"] = SafeName(root!),
            ["mainWindowTitle"] = SafeTitle(proc),
        };
        return Ok(info);
    }

    // ------------------------------------------------------------ control search

    private sealed record Candidate(AutomationElement Element, int Score);

    /// <summary>
    ///     Returns matching controls, best first. Critically, this scans ALL control types — not just
    ///     ControlType.Button — because Revit's AdWindows ribbon exposes its command items as
    ///     ControlType.Pane leaves with no invoke pattern (e.g. "Edit Group" is a Pane, id 2007).
    ///     Matching is by normalized Name; the help text / automation id / ancestor panel names are used
    ///     to disambiguate generic labels like "Finish" / "Cancel" (which also appear in sketch and other
    ///     edit modes) toward the group-editing context.
    /// </summary>
    private static List<Candidate> FindControls(AutomationElement root, ButtonSpec spec)
    {
        var found = new List<Candidate>();

        // TrueCondition over the (small, ~300-element) realized ribbon tree. We can't express a substring
        // name match as an Automation Condition, so filter in code.
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);

        foreach (AutomationElement e in all)
        {
            if (!SafeBool(e, AutomationElement.IsEnabledProperty)) continue;
            if (SafeBool(e, AutomationElement.IsOffscreenProperty)) continue;

            string name = Normalize(SafeName(e));
            if (!spec.NameMatches(name)) continue;

            // Must be clickable somehow: either an invoke pattern OR a real on-screen rectangle.
            bool invokable = SafeBool(e, AutomationElement.IsInvokePatternAvailableProperty);
            var rect = SafeRect(e);
            bool hasRect = rect is { Width: > 1, Height: > 1 };
            if (!invokable && !hasRect) continue;

            string context = (SafeProp(e, AutomationElement.HelpTextProperty) + " " +
                              SafeProp(e, AutomationElement.AutomationIdProperty) + " " +
                              AncestorNames(e)).ToLowerInvariant();

            int score = 100;
            if (spec.ExactNames.Contains(name)) score += 50;          // exact name beats contains-name
            if (invokable) score += 10;                                // prefer a properly invokable element
            if (context.Contains("group")) score += spec.RequiresGroupContext ? 200 : 20;
            else if (spec.RequiresGroupContext) score -= 100;          // generic Finish/Cancel elsewhere: avoid

            found.Add(new Candidate(e, score));
        }

        return found.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>Concatenates a few ancestor Names so context like the "Edit Group" panel can disambiguate.</summary>
    private static string AncestorNames(AutomationElement el)
    {
        var sb = new StringBuilder();
        try
        {
            var walker = TreeWalker.RawViewWalker;
            var cur = walker.GetParent(el);
            for (int i = 0; i < 4 && cur != null; i++)
            {
                sb.Append(' ').Append(SafeName(cur));
                cur = walker.GetParent(cur);
            }
        }
        catch { /* best effort */ }
        return sb.ToString();
    }

    private sealed class ButtonSpec
    {
        public required string Verb;
        public required string DisplayName;
        public required HashSet<string> ExactNames;       // normalized, lower-case
        public string[] ContainsNames = [];               // normalized, lower-case (fallback)
        public bool RequiresGroupContext;                 // Finish/Cancel need the "group" hint
        public required string NotFoundHint;

        public bool NameMatches(string normalizedName)
        {
            if (ExactNames.Contains(normalizedName)) return true;
            foreach (var c in ContainsNames)
                if (normalizedName.Contains(c)) return true;
            return false;
        }

        public static readonly ButtonSpec EditGroup = new()
        {
            Verb = "edit",
            DisplayName = "Edit Group",
            ExactNames = new(StringComparer.Ordinal) { "edit group" },
            ContainsNames = ["edit group"],
            RequiresGroupContext = false,
            NotFoundHint = "Select exactly one model group first — the contextual 'Modify | Model Groups' tab " +
                           "must be showing.",
        };

        public static readonly ButtonSpec FinishGroup = new()
        {
            Verb = "finish",
            DisplayName = "Finish",
            ExactNames = new(StringComparer.Ordinal) { "finish", "finish group" },
            RequiresGroupContext = true,
            NotFoundHint = "You must be editing a group (the green 'Edit Group' panel with Finish/Cancel " +
                           "must be visible).",
        };

        public static readonly ButtonSpec CancelGroup = new()
        {
            Verb = "cancel",
            DisplayName = "Cancel",
            ExactNames = new(StringComparer.Ordinal) { "cancel", "cancel group" },
            RequiresGroupContext = true,
            NotFoundHint = "You must be editing a group (the green 'Edit Group' panel with Finish/Cancel " +
                           "must be visible).",
        };
    }

    // ------------------------------------------------------------- invoking

    private static (bool ok, string how, string error) InvokeElement(AutomationElement el, Process proc)
    {
        // 1) InvokePattern — the correct way to "press" a control, when it's offered. Revit's ribbon
        //    panes usually do NOT offer it, but Finish/Cancel in some modes are real buttons that do.
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern inv)
            {
                inv.Invoke();
                return (true, "InvokePattern", "");
            }
        }
        catch (Exception ex) { _ = ex; /* fall through to a physical click */ }

        // 2) Physical mouse click at the centre of the element's bounding rectangle. This is the path
        //    that actually works for AdWindows ribbon commands (no invoke pattern, no clickable point,
        //    but a valid on-screen rect). We bring Revit forward, move the cursor, click, then restore
        //    the cursor so we don't strand the user's pointer.
        try
        {
            var rect = SafeRect(el);
            if (rect is not { Width: > 0, Height: > 0 })
                return (false, "", "control has no invoke pattern and no usable on-screen rectangle " +
                                   "(is it scrolled off the ribbon or on a collapsed panel?)");

            int cx = (int)Math.Round(rect.Value.Left + rect.Value.Width / 2);
            int cy = (int)Math.Round(rect.Value.Top + rect.Value.Height / 2);

            // A positional click only lands correctly if Revit is the foreground window and the ribbon
            // isn't occluded — so bring it forward first.
            TryActivate(proc);

            GetCursorPos(out var savedX, out var savedY);
            ClickAt(cx, cy);
            SetCursorPos(savedX, savedY);
            return (true, "MouseClick", "");
        }
        catch (Exception ex) { return (false, "", "bounding-rect click failed: " + ex.Message); }
    }

    private static System.Windows.Rect? SafeRect(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            if (double.IsInfinity(r.Width) || double.IsInfinity(r.Height) || r.IsEmpty) return null;
            return r;
        }
        catch { return null; }
    }

    // ------------------------------------------------------ Revit window lookup

    private static bool TryGetRevitRoot(ArgSet args, out AutomationElement? root, out Process? proc, out string error)
    {
        root = null; proc = null; error = "";

        int pid = args.GetInt("--pid", 0);
        Process[] revits = Process.GetProcessesByName("Revit");

        if (pid > 0)
        {
            proc = revits.FirstOrDefault(p => p.Id == pid);
            if (proc == null) { error = $"no Revit process with pid {pid} is running."; return false; }
        }
        else
        {
            // Don't trust Process.MainWindowHandle — it is frequently IntPtr.Zero for Revit (its value is
            // cached at first access and depends on a Win32 heuristic that a transient child window defeats).
            // Resolve the real top-level window per process by enumerating windows instead.
            var withWindows = revits.Where(p => GetBestRevitWindow(p) != IntPtr.Zero).ToArray();
            if (withWindows.Length == 0) { error = "Revit is not running (no Revit window found)."; return false; }
            if (withWindows.Length > 1)
            {
                // Prefer the one that owns the foreground window; otherwise it's genuinely ambiguous.
                IntPtr fg = GetForegroundWindow();
                proc = withWindows.FirstOrDefault(p => GetBestRevitWindow(p) == fg)
                       ?? withWindows.FirstOrDefault(p => OwnsWindow(p, fg));
                if (proc == null)
                {
                    error = "multiple Revit instances are open; pass --pid to choose one (" +
                            string.Join(", ", withWindows.Select(p => p.Id)) + ").";
                    return false;
                }
            }
            else proc = withWindows[0];
        }

        var hwnd = ResolveTargetWindow(args, proc);
        if (hwnd == IntPtr.Zero) { error = $"Revit pid {proc.Id} has no usable top-level window yet."; return false; }

        try { root = AutomationElement.FromHandle(hwnd); }
        catch (Exception ex) { error = "could not attach UI Automation to the Revit window: " + ex.Message; return false; }

        if (root == null) { error = "UI Automation returned no element for the Revit window."; return false; }
        return true;
    }

    /// <summary>
    ///     Returns the best top-level window for a process: the largest visible, titled window it owns.
    ///     Tries the (often-zero) cached MainWindowHandle first, then enumerates as a fallback.
    /// </summary>
    private static IntPtr GetBestRevitWindow(Process proc)
    {
        try
        {
            var cached = proc.MainWindowHandle;
            if (cached != IntPtr.Zero && IsWindowVisible(cached)) return cached;
        }
        catch { /* fall through to enumeration */ }

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        uint target = (uint)proc.Id;

        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid != target) return true;
            if (!IsWindowVisible(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;            // skip invisible/titleless helpers
            if (!GetWindowRect(h, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    /// <summary>The process's visible top-level window whose title contains <paramref name="substring"/>
    /// (case-insensitive) — used by --window to target an owned window (e.g. the "Transom" Schedule Hub window)
    /// instead of Revit's main frame. Largest match wins; IntPtr.Zero if none.</summary>
    private static IntPtr GetWindowByTitle(Process proc, string substring)
    {
        IntPtr best = IntPtr.Zero; long bestArea = -1;
        uint target = (uint)proc.Id;
        var needle = substring.ToLowerInvariant();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid != target || !IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            if (!sb.ToString().ToLowerInvariant().Contains(needle)) return true;
            if (!GetWindowRect(h, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>The window a command targets: the --window title match when given, else Revit's main frame.</summary>
    private static IntPtr ResolveTargetWindow(ArgSet args, Process proc)
    {
        var title = args.GetString("--window");
        if (!string.IsNullOrWhiteSpace(title))
        {
            var h = GetWindowByTitle(proc, title!);
            if (h != IntPtr.Zero) return h;
        }
        return GetBestRevitWindow(proc);
    }

    private static bool OwnsWindow(Process proc, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint wpid);
        return wpid == (uint)proc.Id;
    }

    private static void TryActivate(Process proc) => ForceForeground(GetBestRevitWindow(proc));

    /// <summary>
    ///     If <c>--at=X,Y</c> was supplied, physically click that screen point (bringing Revit forward
    ///     first) and return true. This is the "refocus click" that must happen in the SAME process as the
    ///     keystrokes that follow, so a permission prompt can't steal focus in between. Cursor is restored.
    /// </summary>
    private static bool MaybeClickAt(Process proc, ArgSet args)
    {
        var at = args.GetString("--at");
        if (string.IsNullOrEmpty(at)) return false;
        var parts = at.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out int x) || !int.TryParse(parts[1].Trim(), out int y))
            return false;

        var h = GetBestRevitWindow(proc);
        ForceForeground(h);
        GetCursorPos(out int sx, out int sy);
        ClickAt(x, y);
        SetCursorPos(sx, sy);
        Thread.Sleep(80);
        return true;
    }

    /// <summary>
    ///     Give Revit's drawing area real keyboard focus by physically clicking the ACTIVE view tab.
    ///     SetForegroundWindow alone doesn't hand keyboard focus to the view (shortcuts go nowhere); a
    ///     genuine click does. The already-selected document tab is the safe target: clicking it focuses
    ///     the canvas with NO model side effects (it doesn't select/deselect anything, and — unlike the
    ///     title bar — there are no command buttons to fire by accident).
    /// </summary>
    private static void FocusByClick(Process proc)
    {
        var h = GetBestRevitWindow(proc);
        if (h == IntPtr.Zero) return;
        ForceForeground(h);

        System.Windows.Rect? rect = null;
        try
        {
            var root = AutomationElement.FromHandle(h);
            foreach (AutomationElement t in root.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)))
            {
                if (t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) &&
                    sp is SelectionItemPattern sip && sip.Current.IsSelected)
                { rect = SafeRect(t); if (rect is { Width: > 1 }) break; }
            }
        }
        catch { /* fall back to window centre below */ }

        int cx, cy;
        if (rect is { Width: > 1 } r)
        { cx = (int)(r.Left + r.Width / 2); cy = (int)(r.Top + r.Height / 2); }
        else if (GetWindowRect(h, out var wr))
        { cx = wr.Left + (wr.Right - wr.Left) / 2; cy = wr.Top + 10; }
        else return;

        GetCursorPos(out int sx, out int sy);
        ClickAt(cx, cy);
        SetCursorPos(sx, sy);
        Thread.Sleep(60);
    }

    // ----------------------------------------------------------------- helpers

    private static string Normalize(string s) =>
        string.Join(' ', (s ?? "").Replace('\r', ' ').Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string SafeName(AutomationElement el) => SafeProp(el, AutomationElement.NameProperty);

    private static string SafeControlType(AutomationElement el)
    {
        try
        {
            var ct = el.Current.ControlType?.ProgrammaticName ?? "";
            int dot = ct.LastIndexOf('.');
            return dot >= 0 ? ct[(dot + 1)..] : ct;
        }
        catch { return ""; }
    }

    private static string SafeProp(AutomationElement el, AutomationProperty prop)
    {
        try { return el.GetCurrentPropertyValue(prop)?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static bool SafeBool(AutomationElement el, AutomationProperty prop)
    {
        try { return el.GetCurrentPropertyValue(prop) is true; }
        catch { return false; }
    }

    private static string SafeTitle(Process p)
    {
        try { return p.MainWindowTitle; } catch { return ""; }
    }

    private static string Pad(string? s, int width)
    {
        s ??= "";
        return s.Length >= width ? s : s + new string(' ', width - s.Length);
    }

    // ------------------------------------------------------- output / exit codes

    private static int Ok(Dictionary<string, object?> data)
    {
        data["ok"] = true;
        if (_json) Console.WriteLine(Json.Object(data));
        else
        {
            var sb = new StringBuilder("OK");
            foreach (var kv in data)
                if (kv.Key != "ok") sb.Append($"  {kv.Key}={kv.Value}");
            Console.WriteLine(sb.ToString());
        }
        return 0;
    }

    private static int Fail(string message, int code)
    {
        if (_json) Console.WriteLine(Json.Object(new() { ["ok"] = false, ["error"] = message }));
        else Console.Error.WriteLine("ERROR: " + message);
        return code;
    }

    // ------------------------------------------------------------- native interop

    private const int SW_SHOW = 5;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>Best-effort per-monitor-v2 DPI awareness; falls back to system-DPI-aware on older Windows.</summary>
    private static void TryBecomeDpiAware()
    {
        try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; }
        catch { /* API not present pre-1703 */ }
        try { SetProcessDPIAware(); } catch { /* ignore */ }
    }

    private static void GetCursorPos(out int x, out int y)
    {
        if (GetCursorPos(out POINT p)) { x = p.X; y = p.Y; }
        else { x = 0; y = 0; }
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
}

/// <summary>Tiny positional + flag/value argument splitter. No dependency on a CLI library.</summary>
internal sealed class ArgSet
{
    private readonly List<string> _positional = new();
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);

    public ArgSet(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = a.IndexOf('=');
                if (eq > 0) { _opts[a[..eq]] = a[(eq + 1)..]; continue; }
                // value-taking option if the next token isn't another flag
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    _opts[a] = args[++i];
                else _opts[a] = null; // boolean flag
            }
            else _positional.Add(a);
        }
    }

    public IReadOnlyList<string> Positional => _positional;
    public bool Has(string flag) => _opts.ContainsKey(flag);
    public string? GetString(string flag) => _opts.TryGetValue(flag, out var v) ? v : null;
    public int GetInt(string flag, int fallback) =>
        _opts.TryGetValue(flag, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;
}

/// <summary>
///     Keyboard synthesis via SendInput. Used to drive UI that has no UI-Automation pattern — notably
///     Revit's Properties palette, whose value cells are custom-drawn and not addressable by name.
/// </summary>
internal static class Keyboard
{
    // The INPUT struct MUST be the full size Win32 expects (40 bytes on x64): a DWORD type plus a union
    // sized for the LARGEST member (MOUSEINPUT). A keyboard-only struct is too small, so SendInput's
    // cbSize check fails and it injects nothing (returns 0). This bit us — keep the union.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int cb);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>Types each character as a Unicode keystroke (no modifiers, no Enter). Returns events injected.</summary>
    public static uint TypeText(string text)
    {
        var seq = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            seq.Add(Unicode(c, up: false));
            seq.Add(Unicode(c, up: true));
        }
        return seq.Count > 0 ? SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<INPUT>()) : 0;
    }

    /// <summary>
    ///     Sends each character as a discrete virtual-key press (down/up) — for Revit keyboard shortcuts
    ///     like "tl". Letters and digits only; other characters are skipped. Case-insensitive (Revit
    ///     shortcuts are), so no Shift is sent.
    /// </summary>
    public static void TypeVkSequence(string text)
    {
        var seq = new List<INPUT>();
        foreach (char ch in text)
        {
            char c = char.ToLowerInvariant(ch);
            ushort vk;
            if (c is >= 'a' and <= 'z') vk = (ushort)(0x41 + (c - 'a'));
            else if (c is >= '0' and <= '9') vk = (ushort)(0x30 + (c - '0'));
            else continue;
            seq.Add(Vk(vk, up: false));
            seq.Add(Vk(vk, up: true));
        }
        if (seq.Count > 0) SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Sends a combo like "ctrl+a", "ctrl+shift+enter", "enter", "esc", "tab", "f2".</summary>
    public static bool TrySendCombo(string combo, out string error)
    {
        error = "";
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { error = "empty combo"; return false; }

        var mods = new List<ushort>();
        ushort? key = null;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods.Add(0x11); break;
                case "shift": mods.Add(0x10); break;
                case "alt": mods.Add(0x12); break;
                default:
                    if (!TryVk(p, out var vk)) { error = $"unknown key '{p}'"; return false; }
                    key = vk; break;
            }
        }
        if (key is null) { error = "combo has no non-modifier key"; return false; }

        var seq = new List<INPUT>();
        foreach (var m in mods) seq.Add(Vk(m, up: false));
        seq.Add(Vk(key.Value, up: false));
        seq.Add(Vk(key.Value, up: true));
        for (int i = mods.Count - 1; i >= 0; i--) seq.Add(Vk(mods[i], up: true));

        SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<INPUT>());
        return true;
    }

    private static bool TryVk(string name, out ushort vk)
    {
        name = name.ToLowerInvariant();
        vk = 0;
        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'a' and <= 'z') { vk = (ushort)(0x41 + (c - 'a')); return true; }
            if (c is >= '0' and <= '9') { vk = (ushort)(0x30 + (c - '0')); return true; }
        }
        switch (name)
        {
            case "enter": case "return": vk = 0x0D; return true;
            case "esc": case "escape": vk = 0x1B; return true;
            case "tab": vk = 0x09; return true;
            case "space": vk = 0x20; return true;
            case "del": case "delete": vk = 0x2E; return true;
            case "back": case "backspace": vk = 0x08; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "f2": vk = 0x71; return true;
            case "f3": vk = 0x72; return true;
            default: return false;
        }
    }

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), time = 0, dwExtraInfo = IntPtr.Zero } },
    };

    private static INPUT Vk(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = up ? KEYEVENTF_KEYUP : 0, time = 0, dwExtraInfo = IntPtr.Zero } },
    };
}

/// <summary>Minimal JSON object emitter (string/number/bool/null + nested dict/list). Single-line.</summary>
internal static class Json
{
    public static string Object(Dictionary<string, object?> map)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in map)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Str(kv.Key)).Append(':').Append(Value(kv.Value));
        }
        return sb.Append('}').ToString();
    }

    private static string Value(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        Dictionary<string, object?> m => Object(m),
        System.Collections.IEnumerable e and not string => Array(e),
        _ => Str(v.ToString() ?? ""),
    };

    private static string Array(System.Collections.IEnumerable items)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var it in items)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Value(it));
        }
        return sb.Append(']').ToString();
    }

    private static string Str(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

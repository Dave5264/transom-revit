# demo_window.ps1 - DEMO: bring Revit or AutoCAD to the front on the left part of the screen and park the Claude window
# on the right, so the audience watches the model change while the conversation stays readable.
# usage: powershell -File demo_window.ps1 -App Revit|acad [-Frac 0.667]
# Revit's ribbon reflows to the window width: below about 2/3 of a 1920 px screen the right-hand tabs (Transom) hide
# behind a chevron, so keep Frac at 0.667 or more.
param([Parameter(Mandatory = $true)][ValidateSet("Revit", "acad")][string]$App, [double]$Frac = 0.667)
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, int f, int e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@

function Get-AppWindow([string]$App) {
    # the process's MainWindowHandle can be a tiny untitled helper window (AutoCAD), so take the titled top-level window
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($proc in (Get-Process $App -ErrorAction SilentlyContinue)) {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
        $best = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond) | Where-Object { $_.Current.Name -match 'Revit|AutoCAD' } |
            Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
        if ($best) { return [IntPtr]$best.Current.NativeWindowHandle }
    }
    return [IntPtr]::Zero
}

Add-Type -AssemblyName System.Windows.Forms
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$left = [int]($wa.Width * $Frac)
$hw = Get-AppWindow $App
if ($hw -eq [IntPtr]::Zero) { "no $App window"; exit 1 }
$c = Get-Process Claude -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($c) { [W]::ShowWindow($c.MainWindowHandle, 9) | Out-Null; [W]::MoveWindow($c.MainWindowHandle, $wa.X + $left, $wa.Y, $wa.Width - $left, $wa.Height, $true) | Out-Null }
[W]::ShowWindow($hw, 9) | Out-Null   # SW_RESTORE (un-maximise so the size sticks)
[W]::MoveWindow($hw, $wa.X, $wa.Y, $left, $wa.Height, $true) | Out-Null
[W]::keybd_event(0x10, 0, 0, 0); [W]::keybd_event(0x10, 0, 2, 0)   # a Shift tap lets a background process take the foreground (Alt shows Revit's ribbon key tips)
[W]::SetForegroundWindow($hw) | Out-Null
[W]::SetCursorPos($wa.X + $left + 60, $wa.Y + $wa.Height - 60) | Out-Null   # park the cursor off the canvas: Revit highlights whatever it hovers when a view changes
"$App in front at 0,0 ${left}x$($wa.Height)" + ($(if ($c) { ", Claude on the right" } else { "" }))

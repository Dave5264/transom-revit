# acad_always_load.ps1 - waits for AutoCAD's "Security - Unsigned Executable File" prompt and clicks "Always Load".
# The prompt appears after the Transom for AutoCAD plug-in is updated; the bridge does not load until it is answered.
# "Always Load" is a Pane with no AutomationId, so it is found by name among the descendants of the desktop that belong
# to acad.exe, and clicked at the centre of its BoundingRectangle.
# usage: powershell -File acad_always_load.ps1 [-TimeoutSec 120]
param([int]$TimeoutSec = 120)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class M { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
$root = [System.Windows.Automation.AutomationElement]::RootElement
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    $acad = Get-Process acad -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($acad) {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $acad.Id)
        foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
            if ($w.Current.Name -match 'Security') {
                $nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Always Load')
                $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
                if ($btn) {
                    $r = $btn.Current.BoundingRectangle
                    [M]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null
                    [M]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
                    [M]::mouse_event(2, 0, 0, 0, 0); [M]::mouse_event(4, 0, 0, 0, 0)
                    "clicked Always Load in '$($w.Current.Name)'"
                    Start-Sleep -Milliseconds 800
                    continue
                }
            }
        }
        if ($acad.MainWindowTitle -match 'AutoCAD' -and $acad.Responding) { "AutoCAD window up: $($acad.MainWindowTitle)"; exit 0 }
    }
    Start-Sleep -Seconds 2
}
"timed out after $TimeoutSec s"; exit 1

# demo_shot.ps1 - DEMO: save what the audience sees. Captures the app's window as it is on screen (front most, so call
# demo_window.ps1 first) into <shots>\<NN>_<name>.png.
# usage: powershell -File demo_shot.ps1 -App Revit|acad -Shots <folder> -Name 03_links
param([Parameter(Mandatory = $true)][string]$App, [Parameter(Mandatory = $true)][string]$Shots, [Parameter(Mandatory = $true)][string]$Name)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public struct RECT { public int L, T, R, B; }
public static class G { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }
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

$hw = Get-AppWindow $App
if ($hw -eq [IntPtr]::Zero) { "no $App window"; exit 1 }
$r = New-Object RECT; [G]::GetWindowRect($hw, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
[G]::SetCursorPos($r.R + 40, $r.B - 40) | Out-Null   # park the cursor off the canvas: a hovered element shows highlighted in blue
Start-Sleep -Milliseconds 300
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
New-Item -ItemType Directory -Force $Shots | Out-Null
$out = Join-Path $Shots ($Name + ".png"); $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"saved $out (${w}x$h)"

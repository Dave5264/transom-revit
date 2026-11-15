# transom_hooks.ps1 - Claude Code hooks for the Transom skills (Revit + AutoCAD through the Transom bridges).
# Registered by the frontmatter of every transom* skill; active for the rest of the session once a skill is invoked
# (a restarted session needs a skill invoked again). Windows PowerShell 5.1, no other dependency.
#   powershell -File transom_hooks.ps1 revit-pre | revit-post | acad-pre | shell-pre | shell-post   (hook JSON on stdin)
#   powershell -File transom_hooks.ps1 selftest
# Rules, each learned the hard way:
#   * Revit writes run with readOnly:true inside their own Transaction with a failure preprocessor. A write in the bridge's
#     own transaction (readOnly:false) that raises even a warning opens Revit's modal failure dialog and the bridge hangs
#     until someone answers it (tested 2026-11-15).
#   * Sync to central and saving a project happen only when the user asked for it.
#   * Never force-kill Revit or AutoCAD. Never script accoreconsole EXPLODE (it hung AutoCAD).
#   * Never save over a source drawing: CAD work happens on <name>-Clean.dwg copies.
#   * The bridge stops waiting after about 30 s and says "timed out ... (request not applied)", but the request keeps
#     running to the end (tested: a 40 s script finished).
# Every execute_revit_code call is logged to <work_dir>\bridge_log.jsonl when the call names a model_config.json, else
# to %LOCALAPPDATA%\Transom\logs\bridge_log.jsonl.
param([string]$Mode = "")
$ErrorActionPreference = "SilentlyContinue"

function Out-Decision($event, $decision, $reason, $context) {
    $h = [ordered]@{ hookEventName = $event; permissionDecision = $decision; permissionDecisionReason = $reason }
    if ($context) { $h.additionalContext = $context }
    [Console]::Out.Write((@{ hookSpecificOutput = $h } | ConvertTo-Json -Compress -Depth 5)); exit 0
}
function Out-Context($event, $text) {
    [Console]::Out.Write((@{ hookSpecificOutput = [ordered]@{ hookEventName = $event; additionalContext = $text } } | ConvertTo-Json -Compress -Depth 5)); exit 0
}
function Get-Referenced([string]$code) {
    # run_file.cs / acad_run_file.cs / run_stage.cs read a .cs file and run it: scan those files too
    $extra = ""
    if ($code -match "ScriptHost") {
        foreach ($m in [regex]::Matches($code, '@?"([A-Za-z]:\\[^"]+?\.cs)"')) {
            $p = $m.Groups[1].Value; if (Test-Path -LiteralPath $p) { $extra += "`n" + [IO.File]::ReadAllText($p) }
        }
    }
    return $code + $extra
}

$WRITE = '\b(Create|NewFamilyInstance|NewFootPrintRoof|NewFascia|Delete|Duplicate|SetCompoundStructure|AddAttachment|MoveElement|RotateElement|CopyElements|LoadFamily|ExcavateBy|SetShape|AddPoints|ModifySubElement|SetProjectPosition|HideElements|Link)\s*\(|\.Set\(|\.Name\s*=[^=]|\.Elevation\s*=[^=]|\.Curve\s*=[^=]'

function RevitPre($d) {
    $code = [string]$d.tool_input.code; $ro = ($d.tool_input.readOnly -eq $true)
    $full = Get-Referenced $code
    if ($full -match "SynchronizeWithCentral") {
        if (-not $ro) { Out-Decision "PreToolUse" "deny" "SynchronizeWithCentral must run with readOnly:true: the bridge's own transaction blocks it." }
        Out-Decision "PreToolUse" "ask" "Synchronize with Central. Sync only when the user asked for it in this conversation."
    }
    if ($full -match '\b(doc|D|R|S|target|model|proj)\s*\.\s*(Save|SaveAs)\s*\(' -or $full -match '\.Close\(\s*true\s*\)') {
        Out-Decision "PreToolUse" "ask" "This saves (or closes with save) a project model. Save only when the user asked; never save a model opened as a library or a client's cloud model."
    }
    if (-not $ro -and $full -match $WRITE) {
        Out-Decision "PreToolUse" "deny" ("Revit writes run with readOnly:true inside their own Transaction with an IFailuresPreprocessor (SetFailuresPreprocessor). " +
            "In the bridge's own transaction a single warning opens Revit's modal failure dialog and the bridge hangs until someone answers it. " +
            "Resend with readOnly:true and the Pre class (transom skill, reference/conventions.md).")
    }
    if ($full -match "new Transaction\(" -and $full -notmatch "SetFailuresPreprocessor") {
        Out-Decision "PreToolUse" "deny" "This starts a Transaction without a failure preprocessor. Add fo.SetFailuresPreprocessor(pre) (the Pre class in reference/conventions.md) so a warning cannot open a modal dialog that blocks the bridge."
    }
    if ($full -match 'new Transaction\(\s*doc\s*,' -and $full -notmatch 'app\.Documents') {
        Out-Context "PreToolUse" "This snippet writes to the ACTIVE document (doc): whatever model is active in Revit. If another model is open (an asset library, a client model), resolve the target by title from app.Documents instead."
    }
    exit 0
}

function RevitPost($d) {
    $code = [string]$d.tool_input.code
    $resp = $d.tool_response; $text = if ($resp -is [string]) { $resp } else { ($resp | ConvertTo-Json -Compress -Depth 6) }
    if ($null -eq $text) { $text = "" }
    $log = Join-Path $env:LOCALAPPDATA "Transom\logs"
    $m = [regex]::Match($code, '\["CFG"\]\s*=\s*@"([^"]+)"')
    if ($m.Success -and (Test-Path -LiteralPath $m.Groups[1].Value)) {
        try { $wd = ([IO.File]::ReadAllText($m.Groups[1].Value) | ConvertFrom-Json).work_dir; if ($wd -and (Test-Path -LiteralPath $wd)) { $log = $wd } } catch { }
    }
    New-Item -ItemType Directory -Force $log | Out-Null
    $files = @([regex]::Matches($code, '@?"([A-Za-z]:\\[^"]+?\.cs)"') | ForEach-Object { $_.Groups[1].Value })
    $stage = [regex]::Match($code, '\["STAGE"\]\s*=\s*"([^"]+)"').Groups[1].Value
    $first = ($code -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1); if ($first) { $first = $first.Trim(); if ($first.Length -gt 160) { $first = $first.Substring(0, 160) } }
    $rec = [ordered]@{ time = (Get-Date -Format s); tool = $d.tool_name; readOnly = $d.tool_input.readOnly; files = $files; stage = $stage;
        first_line = $first; ok = ($text -match '"ok"\s*:\s*true' -or $text -match '\\"ok\\":true'); result = $(if ($text.Length -gt 600) { $text.Substring(0, 600) } else { $text }) }
    try { Add-Content -LiteralPath (Join-Path $log "bridge_log.jsonl") -Value ($rec | ConvertTo-Json -Compress -Depth 4) -Encoding UTF8 } catch { }
    if ($text -match "timed out waiting for Revit") {
        Out-Context "PostToolUse" ("The bridge stopped waiting (about 30 s), but the request is usually STILL RUNNING and finishes on its own. Do not resend a write. " +
            "Check revit_list_dialogs first (a modal dialog also causes this). Then poll the script's own log file or until Revit answers again, and read back what exists before deciding anything.")
    }
    exit 0
}

function AcadPre($d) {
    $code = [string]$d.tool_input.code + " " + (($d.tool_input.tokens, $d.tool_input.command | Where-Object { $_ }) | ConvertTo-Json -Compress)
    $code = Get-Referenced $code
    if ($code -match '(?i)\bEXPLODE\b') {
        Out-Decision "PreToolUse" "ask" "EXPLODE on AutoCAD Architecture content hung AutoCAD before, and exploded dynamic blocks land in the wrong place. Read geometry through a Revit import instead. Confirm only if the user asked for this."
    }
    if ($code -match '(?i)\.(SaveAs|Save)\s*\(|\bQSAVE\b|\bSAVEAS\b') {
        $dwgs = @([regex]::Matches($code, '(?i)"([^"]+\.dwg)"') | ForEach-Object { $_.Groups[1].Value })
        $bad = @($dwgs | Where-Object { $_ -notmatch '(?i)-Clean\.dwg$' -and $_ -notmatch '<' })
        if ($bad.Count -gt 0) {
            Out-Decision "PreToolUse" "deny" ("Never save over a source drawing. CAD cleanup works on <name>-Clean.dwg copies next to the source; an ACA export goes to ACAD-<name>-Clean.dwg (transom-model-02-cad). Paths seen: " + ($bad -join ", "))
        }
        if ($dwgs.Count -eq 0) { Out-Context "PreToolUse" "This saves the ACTIVE drawing. It must be a -Clean copy (mcp__transom-acad__status names it), never a source drawing." }
    }
    exit 0
}

function ShellPre($d) {
    $cmd = [string]$d.tool_input.command
    if ($cmd -match '(?is)(Stop-Process|taskkill|kill\s+-9|\.Kill\(\)|wmic\s+process).{0,200}?(revit|acad|accoreconsole)|(revit|acad).{0,120}?(Stop-Process|\.Kill\(\))') {
        Out-Decision "PreToolUse" "deny" "Never force-kill Revit or AutoCAD: unsaved work is lost and a workshared model can be left with borrowed elements. Close through the app (CloseMainWindow) and answer its dialogs, or ask the user."
    }
    if ($cmd -match '(?i)accoreconsole' -and $cmd -match '(?i)EXPLODE') {
        Out-Decision "PreToolUse" "deny" "accoreconsole EXPLODE hung AutoCAD before. Read DWG geometry through a Revit import instead."
    }
    exit 0
}

function ShellPost($d) {
    $cmd = [string]$d.tool_input.command
    if ($cmd -match '(?i)Start-Process|\bstart\s|&\s*[''"$]' -and $cmd -match '(?i)\b(Revit|acad)\.exe') {
        Out-Context "PostToolUse" ("Revit or AutoCAD was just launched. After an add-in update its first window is an unsigned add-in prompt and the Transom bridge does not load until it is answered: " +
            "Revit - revit_find('Always Load') then revit_click_by_id; AutoCAD - transom/scripts/acad_always_load.ps1. Then wait for the main window and check the bridge status.")
    }
    exit 0
}

function SelfTest {
    $me = $PSCommandPath
    $cases = @(
        @("revit-pre", @{ tool_input = @{ code = "doc.SynchronizeWithCentral(a, b);"; readOnly = $true } }, "ask"),
        @("revit-pre", @{ tool_input = @{ code = "doc.SynchronizeWithCentral(a,b);"; readOnly = $false } }, "deny"),
        @("revit-pre", @{ tool_input = @{ code = "var w = Wall.Create(doc, line, id, lv, 10, 0, false, false);"; readOnly = $false } }, "deny"),
        @("revit-pre", @{ tool_input = @{ code = 'using (var t = new Transaction(D, "x")) { t.Start(); t.Commit(); }'; readOnly = $true } }, "deny"),
        @("revit-pre", @{ tool_input = @{ code = 'var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); var t = new Transaction(D, "x");'; readOnly = $true } }, ""),
        @("revit-pre", @{ tool_input = @{ code = "return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();"; readOnly = $false } }, ""),
        @("revit-pre", @{ tool_input = @{ code = "fd.SaveAs(path, new SaveAsOptions());"; readOnly = $true } }, ""),
        @("revit-pre", @{ tool_input = @{ code = "doc.Save();"; readOnly = $true } }, "ask"),
        @("acad-pre", @{ tool_input = @{ code = 'db.SaveAs(@"C:\x\LowerLevel.dwg", DwgVersion.Current);' } }, "deny"),
        @("acad-pre", @{ tool_input = @{ code = 'copy.SaveAs(@"C:\x\LowerLevel-Clean.dwg", DwgVersion.Current);' } }, ""),
        @("acad-pre", @{ tool_input = @{ tokens = @("EXPLODE", "ALL", "") } }, "ask"),
        @("acad-pre", @{ tool_input = @{ tokens = @("_.QSAVE") } }, ""),
        @("acad-pre", @{ tool_input = @{ tokens = @("-AECEXPORTTOAUTOCAD", "", "C:\x\ACAD-MainLevel-Clean.dwg") } }, ""),
        @("shell-pre", @{ tool_input = @{ command = "Stop-Process -Name Revit -Force" } }, "deny"),
        @("shell-pre", @{ tool_input = @{ command = "taskkill /IM acad.exe /F" } }, "deny"),
        @("shell-pre", @{ tool_input = @{ command = "(Get-Process -Id 472).CloseMainWindow()" } }, ""),
        @("shell-pre", @{ tool_input = @{ command = "git status" } }, "")
    )
    $bad = 0
    foreach ($c in $cases) {
        $json = $c[1] | ConvertTo-Json -Compress -Depth 5
        $out = $json | powershell -NoProfile -ExecutionPolicy Bypass -File $me $c[0]
        $got = ""; if ($out) { try { $got = ($out | ConvertFrom-Json).hookSpecificOutput.permissionDecision } catch { } ; if ($null -eq $got) { $got = "" } }
        $ok = ($got -eq $c[2]); if (-not $ok) { $bad++ }
        "{0} {1} want '{2}' got '{3}' | {4}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $c[0], $c[2], $got, $json.Substring(0, [Math]::Min(80, $json.Length))
    }
    if ($bad -eq 0) { "all passed" } else { "$bad failed" }
    exit $bad
}

if ($Mode -eq "selftest") { SelfTest }
$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $data = $raw | ConvertFrom-Json } catch { exit 0 }
switch ($Mode) {
    "revit-pre" { RevitPre $data }
    "revit-post" { RevitPost $data }
    "acad-pre" { AcadPre $data }
    "shell-pre" { ShellPre $data }
    "shell-post" { ShellPost $data }
}
exit 0

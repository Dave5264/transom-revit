#requires -version 5
<#
.SYNOPSIS
  MCP stdio smoke test for the Transom shims (#106). Drives the BUILT shim exe over real stdio with
  newline-delimited JSON and asserts the framing + protocolVersion echo + tool list the spec requires.

  This is the test that was missing: it would have caught the Content-Length-framing bug and the
  hardcoded protocolVersion. Run it after any change to either shim's stdio handling.

.DESCRIPTION
  For each shim it pipes two newline-delimited messages (initialize, then tools/list) into the exe and
  checks stdout:
    1. exactly one JSON object per line (newline-delimited, no embedded newlines, nothing non-protocol);
    2. the initialize result's protocolVersion EQUALS the version the client sent (echo, not hardcoded);
    3. tools/list returns the expected tool names.

.PARAMETER Configuration
  Build configuration (default Release).
#>
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$sentVersion = "2025-06-18"

function Invoke-ShimSmoke {
    param(
        [string]$ProjPath,        # csproj to build
        [string]$ExeName,         # e.g. Transom.McpShim.exe
        [string[]]$ExpectedTools  # tool names tools/list must contain
    )

    Write-Host "== Smoke: $ExeName ==" -ForegroundColor Cyan

    # Build (NOT publish - we just need the runnable exe; publish is a superset that's slower).
    & dotnet build $ProjPath -c $Configuration --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed for $ProjPath" }

    $exe = Get-ChildItem -Path (Join-Path (Split-Path $ProjPath) "bin\$Configuration") -Recurse -Filter $ExeName |
        Where-Object { $_.FullName -notmatch '\\publish\\' } |   # avoid a stale publish-folder copy
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw "built exe not found: $ExeName" }

    $initialize = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"' + $sentVersion + '","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}'
    $toolsList  = '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    $stdin = "$initialize`n$toolsList`n"

    # Pipe stdin in, capture stdout. (stderr is diagnostics - ignored here.)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe.FullName
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write($stdin)
    $p.StandardInput.Close()
    $stdout = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()

    # 1) newline-delimited: each non-empty stdout line must be one parseable JSON object, no more.
    $lines = $stdout -split "`n" | Where-Object { $_.Trim().Length -gt 0 }
    if ($lines.Count -lt 2) { throw "FAIL: expected >=2 newline-delimited responses, got $($lines.Count). stdout:`n$stdout" }
    $objs = @()
    foreach ($ln in $lines) {
        try { $objs += ($ln | ConvertFrom-Json) } catch { throw "FAIL: a stdout line is not valid JSON: $ln" }
    }

    # 2) protocolVersion echo
    $init = $objs | Where-Object { $_.id -eq 1 } | Select-Object -First 1
    if (-not $init) { throw "FAIL: no initialize result (id=1)" }
    $got = $init.result.protocolVersion
    if ($got -ne $sentVersion) { throw "FAIL: protocolVersion not echoed - sent '$sentVersion', got '$got'" }
    Write-Host "  OK protocolVersion echoed: $got" -ForegroundColor Green

    # 3) tools/list contains the expected tools
    $tl = $objs | Where-Object { $_.id -eq 2 } | Select-Object -First 1
    if (-not $tl) { throw "FAIL: no tools/list result (id=2)" }
    $names = @($tl.result.tools | ForEach-Object { $_.name })
    foreach ($t in $ExpectedTools) {
        if ($names -notcontains $t) { throw "FAIL: tools/list missing '$t' (got: $($names -join ', '))" }
    }
    Write-Host "  OK tools/list: $($names -join ', ')" -ForegroundColor Green
}

Invoke-ShimSmoke `
    -ProjPath (Join-Path $repo "source\Transom.McpShim\Transom.McpShim.csproj") `
    -ExeName "Transom.McpShim.exe" `
    -ExpectedTools @("status","list_schedules","read_schedule","set_parameter","set_parameters")

# The UI-assist shim shares the exact stdio code path; smoke it too (cheap). It has its own tool set -
# assert at least one known tool so a tools/list regression is still caught.
Invoke-ShimSmoke `
    -ProjPath (Join-Path $repo "source\Transom.ClickHelper.Mcp\Transom.ClickHelper.Mcp.csproj") `
    -ExeName "Transom.ClickHelper.Mcp.exe" `
    -ExpectedTools @("revit_status","revit_screenshot")

Write-Host "`nSMOKE PASS: both shims speak newline-delimited MCP and echo protocolVersion." -ForegroundColor Green

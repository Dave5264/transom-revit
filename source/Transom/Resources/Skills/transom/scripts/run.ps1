# run.ps1 - print the run_file.cs snippet for a script, ready to paste into mcp__transom__execute_revit_code (readOnly:true).
# usage: powershell -File run.ps1 <script.cs> [KEY=value ...]
param([Parameter(Mandatory = $true)][string]$Script, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Pairs)
$loader = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'run_file.cs'))
$file = (Resolve-Path $Script).Path
$args2 = ($Pairs | Where-Object { $_ -match '=' } | ForEach-Object { $k, $v = $_ -split '=', 2; "[`"$k`"] = @`"$v`"" }) -join ', '
$loader = $loader -replace 'var FILE = @"[^"]*";', ('var FILE = @"' + $file + '";')
$loader = $loader -replace 'var ARGS = new Dictionary<string, string> \{[^\n]*\};', ('var ARGS = new Dictionary<string, string> { ' + $args2 + ' };')
$loader

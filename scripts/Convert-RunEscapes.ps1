<#
.SYNOPSIS
    Rewrites saved model-eval transcript.json and agent.log files so they hold readable characters instead of \uXXXX escapes.

.DESCRIPTION
    Older runs wrote transcript.json with System.Text.Json's default HTML-safe encoder, so backticks, quotes, '+', '<', '>'
    and arrows appear as `, ", +, <, >, →. New runs no longer do this (ModelAgentRunner writes
    with UnsafeRelaxedJsonEscaping). This script converts the files already on disk. Dry run by default: it reports what would
    change and writes nothing; pass -Apply to rewrite.

    transcript.json: parsed and written back with the same options the runner now uses (indented, relaxed escaping), so the
    values are unchanged and a quote inside a string stays \". A file that does not parse is skipped and listed.

    agent.log: a text pass over the escapes. " becomes \" and \ becomes \\ (the log lines hold JSON text); control
    characters and surrogate halves are left as they are; every other \uXXXX becomes its character. An escape preceded by an odd
    number of backslashes (a literal backslash followed by 'u0060') is left alone.

    Each rewritten file keeps its original creation and last-write times. Folders named Worktree are skipped.

.PARAMETER Root
    Folder to scan. Default: ModelTestingResults under the repo root.

.PARAMETER Kind
    All (default), Transcript or Log.

.PARAMETER Apply
    Rewrite the files. Without it the script only reports.

.PARAMETER MaxFiles
    Stop after this many files that need changes (0 = no limit). Useful for a first small apply.

.EXAMPLE
    pwsh scripts/Convert-RunEscapes.ps1
    Dry run over everything.

.EXAMPLE
    pwsh scripts/Convert-RunEscapes.ps1 -Kind Transcript -Apply -MaxFiles 20
    Rewrite the first 20 transcripts that have escapes.
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ModelTestingResults'),
    [ValidateSet('All', 'Transcript', 'Log')]
    [string]$Kind = 'All',
    [switch]$Apply,
    [int]$MaxFiles = 0
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Root)) {
    throw "Root folder not found: $Root"
}

$transcriptOptions = [System.Text.Json.JsonSerializerOptions]::new()
$transcriptOptions.WriteIndented = $true
$transcriptOptions.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping

# Escape preceded by an even number of backslashes (including none): group 1 keeps those backslashes.
$escapePattern = [regex]::new('(?<!\\)((?:\\\\)*)\\u([0-9A-Fa-f]{4})')
$logEvaluator = [System.Text.RegularExpressions.MatchEvaluator] {
    param($m)
    $code = [Convert]::ToInt32($m.Groups[2].Value, 16)
    $prefix = $m.Groups[1].Value
    if ($code -eq 0x22) { return $prefix + '\"' }
    if ($code -eq 0x5C) { return $prefix + '\\' }
    if ($code -lt 0x20 -or ($code -ge 0xD800 -and $code -le 0xDFFF)) { return $m.Value }
    return $prefix + [string][char]$code
}

$stats = [ordered]@{
    Scanned = 0; WithEscapes = 0; Changed = 0; Failed = 0
    BytesBefore = [long]0; BytesAfter = [long]0
}
$failures = [System.Collections.Generic.List[string]]::new()
$limitReached = $false

function Save-Preserving([string]$path, [byte[]]$bytes) {
    $item = Get-Item -LiteralPath $path
    $created = $item.CreationTimeUtc
    $written = $item.LastWriteTimeUtc
    $temp = "$path.convert.tmp"
    [System.IO.File]::WriteAllBytes($temp, $bytes)
    Move-Item -LiteralPath $temp -Destination $path -Force
    $fresh = Get-Item -LiteralPath $path
    $fresh.CreationTimeUtc = $created
    $fresh.LastWriteTimeUtc = $written
}

function Convert-One([string]$path, [string]$what) {
    $stats.Scanned++
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $encoding = [System.Text.UTF8Encoding]::new($hasBom)
    $text = $encoding.GetString($bytes, $(if ($hasBom) { 3 } else { 0 }), $bytes.Length - $(if ($hasBom) { 3 } else { 0 }))
    if (-not $escapePattern.IsMatch($text)) { return }
    $stats.WithEscapes++

    try {
        if ($what -eq 'Transcript') {
            $node = [System.Text.Json.Nodes.JsonNode]::Parse($text)
            $converted = $node.ToJsonString($transcriptOptions)
        } else {
            $converted = $escapePattern.Replace($text, $logEvaluator)
        }
    } catch {
        $stats.Failed++
        $failures.Add("$path :: $($_.Exception.Message)")
        return
    }

    if ($converted -ceq $text) { return }
    $outBytes = $encoding.GetBytes($converted)
    if ($hasBom) { $outBytes = $encoding.GetPreamble() + $outBytes }
    $stats.Changed++
    $stats.BytesBefore += $bytes.Length
    $stats.BytesAfter += $outBytes.Length
    if ($Apply) { Save-Preserving $path $outBytes }
}

function Get-RunFiles([string]$name) {
    [System.IO.Directory]::EnumerateFiles($Root, $name, [System.IO.SearchOption]::AllDirectories) |
        Where-Object { $_ -notmatch '[\\/]Worktree[\\/]' }
}

$jobs = @()
if ($Kind -in 'All', 'Transcript') { $jobs += , @('transcript.json', 'Transcript') }
if ($Kind -in 'All', 'Log') { $jobs += , @('agent.log', 'Log') }

foreach ($job in $jobs) {
    foreach ($file in Get-RunFiles $job[0]) {
        Convert-One $file $job[1]
        if ($MaxFiles -gt 0 -and $stats.Changed -ge $MaxFiles) { $limitReached = $true; break }
    }
    if ($limitReached) { break }
}

$mode = if ($Apply) { 'APPLIED' } else { 'DRY RUN (nothing written)' }
"[$mode] root=$Root kind=$Kind"
"Scanned:      $($stats.Scanned)"
"With escapes: $($stats.WithEscapes)"
"Changed:      $($stats.Changed)$(if ($limitReached) { ' (MaxFiles reached)' })"
"Failed:       $($stats.Failed)"
"Bytes:        {0:N0} -> {1:N0} ({2:N1} MB saved)" -f $stats.BytesBefore, $stats.BytesAfter, (($stats.BytesBefore - $stats.BytesAfter) / 1MB)
foreach ($f in $failures | Select-Object -First 20) { "FAILED: $f" }

<#
.SYNOPSIS
    Classifies ReplaceSnippet / ReplaceRange calls in model-eval transcripts by outcome, to measure edit-tool friction.

.DESCRIPTION
    Walks transcript.json files under one or more result folders, parses every call of the selected tools from
    Turns[].ToolCalls[] (the tool schema is embedded in every transcript, so a plain text search for a parameter
    name matches nearly every file and measures nothing), and classifies each call:
      ok                    the call succeeded
      compile:<CS codes>    rejected because the edited code would not compile
      shape:not-both        top-level filePath/oldContent/newContent sent together with batchEdits
      shape:item-filePath   a batchEdits item without filePath
      shape:filePath        single form without filePath
      shape:bad-action      an invalid 'action' value
      anchor:not-found      oldContent / line text matched nothing
      anchor:ambiguous      oldContent matched more than once
      disabled              the repeat-failure gate refused the call
      other:<text>          anything else (first 100 chars of the error)

    For compile:CS1501 / CS7036 on a batch call, CalleeDeclInBatch tells whether any batch item's oldContent
    contains a declaration of the method the error names (False = the batch never touched the callee signature,
    so the rejection is the model's omission, not batch ordering).

    ResumeFromTranscript sample folders (...\sample-NN) replay the same transcript prefix, so identical calls
    there are counted once per scenario unless -IncludeResumeDuplicates is given.

    Default output: one summary row per Tool / Batch / Class. -Detail: one row per call (pipe to
    Export-Csv or Where-Object for drill-down).

.PARAMETER Path
    Folders to scan recursively. Default: the model-eval results root (scripts\roslynsentinel-scratch.ps1).

.PARAMETER Since
    Only transcripts whose folder was last written at or after this time.

.PARAMETER Tool
    Tool names to classify. Default: ReplaceSnippet, ReplaceRange.

.EXAMPLE
    .\scripts\Get-EditToolFriction.ps1 -Since 2026-11-21
.EXAMPLE
    .\scripts\Get-EditToolFriction.ps1 -Path $res\http___192_168_1_113_8888_v1 -Detail | Where-Object { $_.Batch -and $_.Class -like 'compile:*' }
#>
[CmdletBinding()]
param(
    [string[]]$Path,
    [datetime]$Since,
    [string[]]$Tool = @('ReplaceSnippet', 'ReplaceRange'),
    [switch]$Detail,
    [switch]$IncludeResumeDuplicates
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')

if (-not $Path) { $Path = @(Get-RoslynSentinelResultsRoot) }

# TODO migrate transcript parsing to scripts/eval-review/EvalRunReader.ps1
function Get-ErrorText {
    param([string]$ResultJson)
    if (-not $ResultJson) { return '' }
    try {
        $r = $ResultJson | ConvertFrom-Json
        if ($r.errorData -and $r.errorData.message) { return [string]$r.errorData.message }
        return $ResultJson
    }
    catch { return $ResultJson }   # some errors (e.g. invalid enum value) arrive as plain text
}

function Get-CallClass {
    param([bool]$IsError, [string]$Message)
    if (-not $IsError) { return 'ok' }
    if ($Message -match 'DISABLED until') { return 'disabled' }
    if ($Message -match 'not both') { return 'shape:not-both' }
    if ($Message -match 'batchEdits\[\d+\]: filePath is required') { return 'shape:item-filePath' }
    if ($Message -match "'filePath' is required") { return 'shape:filePath' }
    if ($Message -match "is not a valid value for parameter 'action'") { return 'shape:bad-action' }
    $codes = [regex]::Matches($Message, 'CS\d{4}') | ForEach-Object Value | Select-Object -Unique
    if ($codes) { return 'compile:' + ($codes -join ',') }
    if ($Message -match 'ambiguous') { return 'anchor:ambiguous' }
    if ($Message -match 'not found|matches nothing|matched nothing') { return 'anchor:not-found' }
    $first = ($Message -split "`n" | Where-Object { $_ -match '\S' } | Select-Object -First 1)
    if ($first.Length -gt 100) { $first = $first.Substring(0, 100) }
    return "other:$first"
}

# Name of the method a CS1501 / CS7036 error is about, or $null.
function Get-CalleeName {
    param([string]$Message)
    if ($Message -match "No overload for method '([A-Za-z_]\w*)'") { return $Matches[1] }
    if ($Message -match "required parameter '\w+' of '(?:[\w.<>]+\.)?([A-Za-z_]\w*)\(") { return $Matches[1] }
    return $null
}

function Get-Hash {
    param([string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))) }
    finally { $sha.Dispose() }
}

$transcripts = foreach ($p in $Path) {
    Get-ChildItem -LiteralPath $p -Recurse -Filter transcript.json -File |
        Where-Object { -not $Since -or $_.Directory.LastWriteTime -ge $Since }
}

$seen = @{}
$rows = foreach ($f in $transcripts) {
    try { $t = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json }
    catch { Write-Warning "unreadable transcript: $($f.FullName)"; continue }

    $runDir = $f.Directory.FullName
    $sampleScenario = if ($runDir -match '\\([^\\]+)\\[^\\]+\\sample-\d+$') { $Matches[1] } else { $null }

    foreach ($turn in $t.Turns) {
        foreach ($c in $turn.ToolCalls) {
            if ($Tool -notcontains $c.ToolName) { continue }

            $message = if ($c.IsError) { Get-ErrorText $c.ResultJson } else { '' }
            $class = Get-CallClass ([bool]$c.IsError) $message

            if ($sampleScenario -and -not $IncludeResumeDuplicates) {
                # Each replay runs in its own RoslynSentinelTests_<guid> temp copy; drop the guid so replays compare equal.
                $normalizedArgs = $c.ArgumentsJson -replace 'RoslynSentinelTests_[0-9a-fA-F-]+', 'RoslynSentinelTests_X'
                $key = $sampleScenario + '|' + (Get-Hash ($normalizedArgs + '|' + $class))
                if ($seen.ContainsKey($key)) { continue }
                $seen[$key] = $true
            }

            $callArgs = $null
            try { $callArgs = $c.ArgumentsJson | ConvertFrom-Json } catch { }
            $items = @(if ($callArgs -and $callArgs.batchEdits) { $callArgs.batchEdits })
            $isBatch = $items.Count -gt 0

            $calleeDecl = $null
            if ($isBatch -and $class -match 'CS1501|CS7036') {
                $callee = Get-CalleeName $message
                if ($callee) {
                    $declPattern = '(?m)^\s*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|new|extern|unsafe|partial)\s+)*[\w<>\[\],.?() ]+\s' +
                        [regex]::Escape($callee) + '\s*(?:<[^>]*>)?\s*\('
                    $calleeDecl = [bool]($items | Where-Object { $_.oldContent -and $_.oldContent -match $declPattern })
                }
            }

            [pscustomobject]@{
                Run               = $runDir
                Turn              = $turn.TurnNumber
                Tool              = $c.ToolName
                Batch             = $isBatch
                Items             = $items.Count
                Action            = if ($callArgs) { $callArgs.action } else { $null }
                Class             = $class
                CalleeDeclInBatch = $calleeDecl
                Error             = ($message -split "`n" | Where-Object { $_ -match '\S' } | Select-Object -First 3) -join ' | '
            }
        }
    }
}

if ($Detail) { return $rows }

Write-Host ("transcripts={0} calls={1}" -f @($transcripts).Count, @($rows).Count)
$rows | Group-Object Tool, Batch, Class | Sort-Object Count -Descending | ForEach-Object {
    $first = $_.Group[0]
    [pscustomobject]@{ Count = $_.Count; Tool = $first.Tool; Batch = $first.Batch; Class = $first.Class }
}

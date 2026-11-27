<#
.SYNOPSIS
    Shared transcript metrics for the model-eval arm scripts: run discovery (evalqueue job files or a folder scan),
    the escape-copy read cache (Q11) and the call categories Write, Read and Other (Q17).
    Read-Run (per-transcript parsing and the outcome, header and argument-failure rules) stays in the caller
    (Get-ToolResultFormatArmTable.ps1). Get-EvalTranscripts emits one object per transcript.json and reads no transcript.
    Dot-source this file; it runs nothing by itself.
#>

$BackslashQuote = '\"'
# Strings of a call that the escape-copy rule scans for backslash-quote lines.
$EscapeScanFields = @('oldContent', 'newContent', 'content')
# Batch lists whose items carry their own filePath and string fields.
$BatchFields = @('batchEdits')
# Fields that carry source text a call writes (Get-CallCategory).
$SourceTextFields = @('oldContent', 'newContent', 'content', 'newMemberSource', 'unifiedDiff', 'changes')
$CategoryBatchFields = @('batchEdits', 'edits')
$WriteToolNames = @('ReplaceSnippet', 'ReplaceRange', 'ApplyDiff', 'EditSnippets', 'WriteFile', 'CreateFile', 'Member', 'MethodSignature')
$ReadToolNames = @('ReadFile', 'GetFileOutline', 'GetMethodSource', 'Search', 'InspectSymbol', 'GetDiagnostics')

function Get-EnvValue($jobEnv, [string]$name) {
    if ($null -eq $jobEnv) { return '-' }
    $p = $jobEnv.PSObject.Properties[$name]
    if ($null -eq $p -or [string]::IsNullOrWhiteSpace([string]$p.Value)) { return '-' }
    return [string]$p.Value
}

function Get-OutcomeToken([string]$text, [string]$pattern) {
    $m = [regex]::Match($text, $pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-PathKey([string]$p) { return ($p -replace '\\', '/').ToLowerInvariant() }

function Get-LineSet([string]$text) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($l in ($text -split "`n")) { [void]$set.Add($l.Trim()) }
    return , $set
}

# One entry per edit/create target: the filePath and the string arguments written by that call (or batch item).
function Get-WriteTargets($callArgs) {
    $fields = $EscapeScanFields
    $out = New-Object System.Collections.Generic.List[object]
    $top = [string]$callArgs.filePath
    $out.Add([pscustomobject]@{ filePath = $top; texts = @(foreach ($f in $fields) { if ($callArgs.$f -is [string]) { $callArgs.$f } }) })
    foreach ($bf in $BatchFields) {
        foreach ($item in @($callArgs.$bf | Where-Object { $null -ne $_ })) {
            $fp = if ($item.filePath) { [string]$item.filePath } else { $top }
            $out.Add([pscustomobject]@{ filePath = $fp; texts = @(foreach ($f in $fields) { if ($item.$f -is [string]) { $item.$f } }) })
        }
    }
    return , $out.ToArray()
}

# Run discovery. Default: the job files under <QueueRoot>\<Machine>\jobs (or -JobFolder); jobs whose purpose is not
# -like -PurposePattern and jobs in state Cancelled are skipped. -Path: every transcript.json under each folder
# (recursive), no job files; Machine, JobId, Test, Purpose, Passed, EnvFormat and EnvThinking are '-' or $null.
# Emits one pscustomobject per transcript.json; callers wrap the call in @().
function Get-EvalTranscripts {
    param(
        [string]$Machine,
        [string]$PurposePattern = '*',
        [string]$JobFolder,
        [string[]]$Path,
        [string]$QueueRoot
    )

    if ($Path) {
        foreach ($p in $Path) {
            foreach ($transcript in Get-ChildItem -LiteralPath $p -Recurse -Filter transcript.json -File -ErrorAction SilentlyContinue) {
                [pscustomobject]@{ TranscriptPath = $transcript.FullName; RunFolder = Split-Path -Parent $transcript.FullName; Machine = '-'; JobId = $null; Test = $null; Purpose = $null; Passed = $null; EnvFormat = '-'; EnvThinking = '-' }
            }
        }
        return
    }

    if ($JobFolder) { $jobsDir = $JobFolder }
    else {
        if (-not $QueueRoot) { $QueueRoot = if ($env:ROSLYNSENTINEL_EVALQUEUE_ROOT) { $env:ROSLYNSENTINEL_EVALQUEUE_ROOT } else { Join-Path $env:LOCALAPPDATA 'RoslynSentinel\evalqueue' } }
        $jobsDir = Join-Path (Join-Path $QueueRoot $Machine) 'jobs'
    }
    if (-not (Test-Path -LiteralPath $jobsDir)) { throw "No jobs folder at $jobsDir." }

    foreach ($file in Get-ChildItem -LiteralPath $jobsDir -Filter '*.json' -File) {
        try { $job = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
        catch { Write-Warning "Skipped malformed job file $($file.Name)."; continue }
        if ([string]$job.purpose -notlike $PurposePattern -or $job.state -eq 'Cancelled') { continue }
        $jobFormat = Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT'
        $jobThinking = Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_THINKING'

        foreach ($folder in @($job.runFolders)) {
            if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { continue }
            foreach ($transcript in Get-ChildItem -LiteralPath $folder -Recurse -Filter transcript.json -File -ErrorAction SilentlyContinue) {
                [pscustomobject]@{ TranscriptPath = $transcript.FullName; RunFolder = Split-Path -Parent $transcript.FullName; Machine = $Machine; JobId = $job.id; Test = $job.test; Purpose = $job.purpose; Passed = $job.passed; EnvFormat = $jobFormat; EnvThinking = $jobThinking }
            }
        }
    }
}

function New-EscapeCopyTracker {
    return [pscustomobject]@{ Cache = @{} }
}

# Escape-copy rows of one call, computed BEFORE the cache update. Rows are returned unrolled: no rows emits nothing,
# so callers wrap the call in @(...). -IsError is accepted for signature stability only:
# error calls count too (the arm table counts them), so it does not change the result.
function Measure-EscapeCopyCall {
    param($Tracker, [string]$Run, $Turn, [string]$ToolName, $Targets, [bool]$IsError)

    $callLines = New-Object 'System.Collections.Generic.HashSet[string]'
    $callRows = New-Object System.Collections.Generic.List[object]
    if ($Targets.Count -gt 0 -and $Tracker.Cache.Count -gt 0) {
        $union = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($set in $Tracker.Cache.Values) { $union.UnionWith([string[]]@($set)) }
        foreach ($tg in $Targets) {
            foreach ($text in $tg.texts) {
                foreach ($l in ($text -split "`n")) {
                    $trim = $l.Trim()
                    if (-not $trim.Contains($BackslashQuote) -or $callLines.Contains($trim)) { continue }
                    $fixed = $trim.Replace($BackslashQuote, '"').Trim()
                    if ($union.Contains($fixed) -and -not $union.Contains($trim)) {
                        [void]$callLines.Add($trim)
                        $callRows.Add([pscustomobject]@{ run = $Run; turn = $Turn; tool = $ToolName; filePath = $tg.filePath; line = $trim })
                    }
                }
            }
        }
    }
    return $callRows.ToArray()
}

# Read cache update after a call. A successful read stores its successData.source under the first target's filePath
# (callers pass Get-WriteTargets of the arguments); a successful non-read drops each target filePath.
# The arguments are not parsed here.
function Update-EscapeCopyTracker {
    param($Tracker, $Targets, [bool]$IsRead, [bool]$IsError, $ResultJson)

    if ($IsError) { return }
    if ($IsRead) {
        $fp = if ($Targets.Count -gt 0) { [string]$Targets[0].filePath } else { '' }
        if ($fp) {
            $src = $null
            if ($ResultJson) { try { $src = [string](($ResultJson | ConvertFrom-Json).successData.source) } catch { } }
            if ($src) { $Tracker.Cache[(Get-PathKey $fp)] = Get-LineSet $src } else { $Tracker.Cache.Remove((Get-PathKey $fp)) }
        }
    }
    else {
        foreach ($tg in $Targets) { if ($tg.filePath) { $Tracker.Cache.Remove((Get-PathKey $tg.filePath)) } }
    }
}

function Test-SourceTextValue($value) {
    if ($null -eq $value) { return $false }
    if ($value -is [string]) { return $value.Length -gt 0 }
    if ($value -is [System.Management.Automation.PSCustomObject]) { return @($value.PSObject.Properties).Count -gt 0 }
    if ($value -is [System.Collections.IEnumerable]) { return @($value).Count -gt 0 }
    return $false
}

# Write, Read or Other for one call (Q17). Unparseable arguments fall back to the tool name lists.
function Get-CallCategory([string]$ToolName, [string]$ArgumentsJson) {
    $callArgs = $null
    if ($ArgumentsJson) {
        try { $callArgs = $ArgumentsJson | ConvertFrom-Json -ErrorAction Stop } catch { $callArgs = $null }
    }
    if ($null -eq $callArgs) {
        if ($WriteToolNames -contains $ToolName) { return 'Write' }
        if ($ReadToolNames -contains $ToolName) { return 'Read' }
        return 'Other'
    }

    foreach ($f in $SourceTextFields) { if (Test-SourceTextValue ($callArgs.$f)) { return 'Write' } }
    foreach ($bf in $CategoryBatchFields) {
        foreach ($item in @($callArgs.$bf | Where-Object { $null -ne $_ })) {
            foreach ($f in $SourceTextFields) { if (Test-SourceTextValue ($item.$f)) { return 'Write' } }
        }
    }

    if ($ReadToolNames -contains $ToolName) { return 'Read' }
    if ($ToolName -eq 'Member' -and [string]$callArgs.operation -ieq 'view') { return 'Read' }
    return 'Other'
}

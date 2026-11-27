<#
.SYNOPSIS
    Shared transcript metrics for the model-eval arm scripts: run discovery (evalqueue job files or a folder scan),
    the escape-copy read cache (Q11) and the call categories Write, Read and Other (Q17).
    Read-Run (per-transcript parsing and the outcome, header and argument-failure rules) stays in the caller
    (Get-ToolResultFormatArmTable.ps1). Get-EvalTranscripts emits one object per transcript.json and reads no transcript;
    it reads one or more machines' job folders (default every machine in eval-hosts.json) and can filter by -Commit.
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

# First non-null value of a job property, or $null when the job has none of the names (jobs written before the
# field existed lack it).
function Get-JobField($job, [string[]]$names) {
    foreach ($n in $names) {
        $p = $job.PSObject.Properties[$n]
        if ($null -ne $p -and $null -ne $p.Value) { return $p.Value }
    }
    return $null
}

# Run discovery. Default: the job files under <QueueRoot>\<machine>\jobs for each -Machine (each element is split on
# commas and trimmed, so `-File x.ps1 -Machine a,b` works); with no -Machine and no -JobFolder/-Path the machines are the
# distinct `machine` values of -HostsFile (default eval-hosts.json beside this file). A missing machine folder is a
# warning and is skipped; it throws only when none exist. -JobFolder is a single-folder override (rows carry the first
# -Machine, else '-'). Jobs whose purpose is not -like -PurposePattern and jobs in state Cancelled are skipped; -Commit
# keeps only jobs whose testedCommit (else commit) starts with it, case-insensitive. -Path: every transcript.json under
# each folder (recursive), no job files; Machine is '-' and JobId, Test, Purpose, Passed, Commit, Dirty and Job are
# $null (EnvFormat and EnvThinking '-'). Job rows add Commit (testedCommit else commit), Dirty (testedDirty else dirty)
# and Job (the parsed job object). Emits one pscustomobject per transcript.json; callers wrap the call in @().
function Get-EvalTranscripts {
    param(
        [string[]]$Machine,
        [string]$PurposePattern = '*',
        [string]$JobFolder,
        [string[]]$Path,
        [string]$QueueRoot,
        [string]$Commit,
        [string]$HostsFile
    )

    if ($Path) {
        foreach ($p in $Path) {
            foreach ($transcript in Get-ChildItem -LiteralPath $p -Recurse -Filter transcript.json -File -ErrorAction SilentlyContinue) {
                [pscustomobject]@{ TranscriptPath = $transcript.FullName; RunFolder = Split-Path -Parent $transcript.FullName; Machine = '-'; JobId = $null; Test = $null; Purpose = $null; Passed = $null; EnvFormat = '-'; EnvThinking = '-'; Commit = $null; Dirty = $null; Job = $null }
            }
        }
        return
    }

    $machines = @($Machine | Where-Object { $null -ne $_ } | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

    # Each source is one jobs folder and the machine its rows are attributed to.
    $sources = New-Object System.Collections.Generic.List[object]
    if ($JobFolder) {
        $label = if ($machines.Count -gt 0) { $machines[0] } else { '-' }
        if (-not (Test-Path -LiteralPath $JobFolder)) { throw "No jobs folder at $JobFolder." }
        $sources.Add([pscustomobject]@{ Machine = $label; Dir = $JobFolder })
    }
    else {
        if (-not $QueueRoot) { $QueueRoot = if ($env:ROSLYNSENTINEL_EVALQUEUE_ROOT) { $env:ROSLYNSENTINEL_EVALQUEUE_ROOT } else { Join-Path $env:LOCALAPPDATA 'RoslynSentinel\evalqueue' } }
        if ($machines.Count -eq 0) {
            if (-not $HostsFile) { $HostsFile = Join-Path $PSScriptRoot 'eval-hosts.json' }
            if (-not (Test-Path -LiteralPath $HostsFile)) { throw "No -Machine given and no hosts file at $HostsFile." }
            $machines = @((Get-Content -LiteralPath $HostsFile -Raw | ConvertFrom-Json).hosts | ForEach-Object { [string]$_.machine } | Where-Object { $_ } | Select-Object -Unique)
            if ($machines.Count -eq 0) { throw "No -Machine given and $HostsFile lists no machines." }
        }
        $searched = New-Object System.Collections.Generic.List[string]
        foreach ($m in ($machines | Select-Object -Unique)) {
            $dir = Join-Path (Join-Path $QueueRoot $m) 'jobs'
            $searched.Add($dir)
            if (Test-Path -LiteralPath $dir) { $sources.Add([pscustomobject]@{ Machine = $m; Dir = $dir }) }
            else { Write-Warning "No jobs folder for machine '$m' at $dir; skipped." }
        }
        if ($sources.Count -eq 0) { throw "No jobs folder at $($searched -join ', ')." }
    }

    foreach ($source in $sources) {
        foreach ($file in Get-ChildItem -LiteralPath $source.Dir -Filter '*.json' -File) {
            try { $job = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
            catch { Write-Warning "Skipped malformed job file $($file.Name)."; continue }
            if ([string]$job.purpose -notlike $PurposePattern -or $job.state -eq 'Cancelled') { continue }
            $jobCommit = Get-JobField $job @('testedCommit', 'commit')
            if ($Commit -and -not ([string]$jobCommit).StartsWith($Commit, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            $jobDirty = Get-JobField $job @('testedDirty', 'dirty')
            $jobFormat = Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT'
            $jobThinking = Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_THINKING'

            foreach ($folder in @($job.runFolders)) {
                if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { continue }
                foreach ($transcript in Get-ChildItem -LiteralPath $folder -Recurse -Filter transcript.json -File -ErrorAction SilentlyContinue) {
                    [pscustomobject]@{ TranscriptPath = $transcript.FullName; RunFolder = Split-Path -Parent $transcript.FullName; Machine = $source.Machine; JobId = $job.id; Test = $job.test; Purpose = $job.purpose; Passed = $job.passed; EnvFormat = $jobFormat; EnvThinking = $jobThinking; Commit = $jobCommit; Dirty = $jobDirty; Job = $job }
                }
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

<#
.SYNOPSIS
    Rolls up the per-instance server-telemetry_<instanceId>.json files into one per-tool summary.

.DESCRIPTION
    Reads <InstancesRoot>\telemetry\server-telemetry_*.json (the copies the launch script saves from swept
    instances) and, with -IncludeLive, <InstancesRoot>\instances\*\server-telemetry_*.json (running instances).
    Files that are unreadable, partial (the server writes them in place), invalid JSON, or have a missing or
    unknown schemaVersion are skipped and listed at the end. The same server run can appear more than once
    (a live file plus its telemetry copy, or two copies): runs are keyed on (instanceId, serverStartedUtc) and
    the file with the latest writtenUtc wins.

    Aggregates per tool: calls, successes, errors, error%, average and max ms, merged errorsByCode and the four
    guidance counters (issued / followed / followed-succeeded / ignored) with follow% and succeed%. Writes
    <OutDir>\rollup_<Date>.json and prints a compact table. Source files are only ever read, never changed.

    The output name (rollup_*.json) can never match the input pattern (server-telemetry_*.json), so a rollup
    is never read back as input, even when OutDir is the telemetry folder.

    Exit code: 0, or 1 when telemetry files existed but none could be read.

.PARAMETER InstancesRoot
    Prod-instances root. Default: $env:ROSLYNSENTINEL_INSTANCES_ROOT, else %USERPROFILE%\RoslynSentinelInstances
    (same resolution as Get-RoslynSentinelInstancesRoot in roslynsentinel-instances-common.ps1).

.PARAMETER IncludeLive
    Also read the live files under <InstancesRoot>\instances\*\.

.PARAMETER OutDir
    Where rollup_<Date>.json is written. Default: <InstancesRoot>\telemetry. Created if missing.

.PARAMETER Date
    Date stamp (yyyy-MM-dd) for the output file name. Default: today.

.PARAMETER Quiet
    Skip the per-tool table (the skipped-file list and the summary line are still printed).

.EXAMPLE
    .\scripts\Get-TelemetryRollup.ps1
    Rolls up the saved telemetry copies under the default root.

.EXAMPLE
    .\scripts\Get-TelemetryRollup.ps1 -IncludeLive -Quiet
    Includes running instances and only writes the rollup file.
#>
[CmdletBinding()]
param(
    [string]$InstancesRoot,
    [switch]$IncludeLive,
    [string]$OutDir,
    [string]$Date,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$InputPattern = 'server-telemetry_*.json'
$KnownSchemaVersion = 1

# ---- helpers -----------------------------------------------------------------------------------------------

function Resolve-Root {
    param([string]$Explicit)
    $candidate = $Explicit
    if (-not $candidate) { $candidate = $env:ROSLYNSENTINEL_INSTANCES_ROOT }
    if (-not $candidate -and $env:USERPROFILE) { $candidate = Join-Path $env:USERPROFILE 'RoslynSentinelInstances' }
    if (-not $candidate) { throw 'No instances root: pass -InstancesRoot or set ROSLYNSENTINEL_INSTANCES_ROOT.' }
    $candidate = [Environment]::ExpandEnvironmentVariables($candidate)
    if (-not [System.IO.Path]::IsPathRooted($candidate) -or $candidate -match '%[^%]+%') {
        throw "Instances root '$candidate' is not an absolute path."
    }
    return [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
}

# Property value or default; safe under StrictMode (a missing property is not an error).
function Get-Prop {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p -or $null -eq $p.Value) { return $Default }
    return $p.Value
}

# Windows PowerShell 5.1 leaves ISO dates as strings, PowerShell 7 turns them into DateTime; accept both.
function ConvertTo-UtcDate {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    $styles = [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal
    $parsed = [DateTime]::MinValue
    if ([DateTime]::TryParse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) { return $parsed }
    throw "unparseable date '$Value'"
}

function Format-Utc {
    param($Value)
    if ($null -eq $Value) { return $null }
    return $Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-Percent {
    param([long]$Part, [long]$Whole)
    if ($Whole -le 0) { return $null }
    return [math]::Round(100.0 * $Part / $Whole, 1)
}

function Format-PercentText {
    param($Value)
    if ($null -eq $Value) { return 'n/a' }
    return ('{0:0.0}' -f $Value)
}

# Reads a file while the server may still be writing it (shared read, no locking of the writer).
function Read-SharedText {
    param([string]$Path)
    $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try {
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

# Parses and validates one file; returns a plain object or throws with a short reason.
function Read-TelemetryFile {
    param([string]$Path)
    $text = Read-SharedText -Path $Path
    if ([string]::IsNullOrWhiteSpace($text)) { throw 'empty file' }
    try { $doc = $text | ConvertFrom-Json } catch { throw 'invalid or partial JSON' }
    if ($null -eq $doc -or $doc -isnot [System.Management.Automation.PSCustomObject]) { throw 'JSON root is not an object' }

    $schema = Get-Prop $doc 'schemaVersion'
    if ($null -eq $schema) { throw 'schemaVersion missing' }
    if ([int]$schema -ne $KnownSchemaVersion) { throw "unknown schemaVersion $schema" }

    $instanceId = Get-Prop $doc 'instanceId'
    if (-not $instanceId) { throw 'instanceId missing' }
    $started = ConvertTo-UtcDate (Get-Prop $doc 'serverStartedUtc')
    if ($null -eq $started) { throw 'serverStartedUtc missing' }
    $written = ConvertTo-UtcDate (Get-Prop $doc 'writtenUtc')
    if ($null -eq $written) { throw 'writtenUtc missing' }

    $tools = New-Object System.Collections.Generic.List[object]
    foreach ($t in @(Get-Prop $doc 'tools' @())) {
        $name = Get-Prop $t 'toolName'
        if (-not $name) { throw 'tool entry without toolName' }
        $codes = @{}
        $ebc = Get-Prop $t 'errorsByCode'
        if ($null -ne $ebc) {
            foreach ($p in $ebc.PSObject.Properties) { $codes[[string]$p.Name] = [long]$p.Value }
        }
        $tools.Add([pscustomobject]@{
                Name           = [string]$name
                Calls          = [long](Get-Prop $t 'calls' 0)
                Successes      = [long](Get-Prop $t 'successes' 0)
                Errors         = [long](Get-Prop $t 'errors' 0)
                Codes          = $codes
                TotalMs        = [long](Get-Prop $t 'totalDurationMs' 0)
                MaxMs          = [long](Get-Prop $t 'maxDurationMs' 0)
                First          = ConvertTo-UtcDate (Get-Prop $t 'firstCallUtc')
                Last           = ConvertTo-UtcDate (Get-Prop $t 'lastCallUtc')
                GIssued        = [long](Get-Prop $t 'guidanceIssued' 0)
                GFollowed      = [long](Get-Prop $t 'guidanceFollowed' 0)
                GSucceeded     = [long](Get-Prop $t 'guidanceFollowedSucceeded' 0)
                GIgnored       = [long](Get-Prop $t 'guidanceIgnored' 0)
            })
    }
    return [pscustomobject]@{
        Path = $Path; InstanceId = [string]$instanceId; Started = $started; Written = $written
        BuildSha = Get-Prop $doc 'buildSha'; Tools = $tools
    }
}

# ---- locate inputs -----------------------------------------------------------------------------------------

$root = Resolve-Root -Explicit $InstancesRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'telemetry' }
if (-not $Date) { $Date = (Get-Date).ToString('yyyy-MM-dd') }
if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "-Date must be yyyy-MM-dd, got '$Date'." }

$files = New-Object System.Collections.Generic.List[string]
$savedDir = Join-Path $root 'telemetry'
if (Test-Path -LiteralPath $savedDir) {
    foreach ($f in @(Get-ChildItem -LiteralPath $savedDir -Filter $InputPattern -File -ErrorAction SilentlyContinue)) { $files.Add($f.FullName) }
}
if ($IncludeLive) {
    $liveDir = Join-Path $root 'instances'
    if (Test-Path -LiteralPath $liveDir) {
        foreach ($d in @(Get-ChildItem -LiteralPath $liveDir -Directory -ErrorAction SilentlyContinue)) {
            foreach ($f in @(Get-ChildItem -LiteralPath $d.FullName -Filter $InputPattern -File -ErrorAction SilentlyContinue)) { $files.Add($f.FullName) }
        }
    }
}

# ---- read, skip bad files, de-duplicate on (instanceId, serverStartedUtc) ----------------------------------

$skipped = New-Object System.Collections.Generic.List[object]
$byRun = @{}
foreach ($path in $files) {
    try { $parsed = Read-TelemetryFile -Path $path }
    catch {
        $skipped.Add([pscustomobject]@{ file = $path; reason = $_.Exception.Message })
        continue
    }
    $key = '{0}|{1}' -f $parsed.InstanceId, $parsed.Started.Ticks
    if (-not $byRun.ContainsKey($key) -or $parsed.Written -gt $byRun[$key].Written) { $byRun[$key] = $parsed }
}
$kept = @($byRun.Values)

# ---- aggregate ---------------------------------------------------------------------------------------------

$agg = @{}
$shas = New-Object System.Collections.Generic.List[string]
$rangeMin = $null
$rangeMax = $null
foreach ($run in $kept) {
    if ($run.BuildSha -and -not $shas.Contains([string]$run.BuildSha)) { $shas.Add([string]$run.BuildSha) }
    foreach ($t in $run.Tools) {
        if (-not $agg.ContainsKey($t.Name)) {
            $agg[$t.Name] = [pscustomobject]@{
                Name = $t.Name; Calls = 0L; Successes = 0L; Errors = 0L; TotalMs = 0L; MaxMs = 0L
                GIssued = 0L; GFollowed = 0L; GSucceeded = 0L; GIgnored = 0L; Codes = @{}
            }
        }
        $a = $agg[$t.Name]
        $a.Calls += $t.Calls; $a.Successes += $t.Successes; $a.Errors += $t.Errors; $a.TotalMs += $t.TotalMs
        if ($t.MaxMs -gt $a.MaxMs) { $a.MaxMs = $t.MaxMs }
        $a.GIssued += $t.GIssued; $a.GFollowed += $t.GFollowed; $a.GSucceeded += $t.GSucceeded; $a.GIgnored += $t.GIgnored
        foreach ($c in $t.Codes.Keys) {
            if ($a.Codes.ContainsKey($c)) { $a.Codes[$c] += $t.Codes[$c] } else { $a.Codes[$c] = $t.Codes[$c] }
        }
        if ($null -ne $t.First -and ($null -eq $rangeMin -or $t.First -lt $rangeMin)) { $rangeMin = $t.First }
        if ($null -ne $t.Last -and ($null -eq $rangeMax -or $t.Last -gt $rangeMax)) { $rangeMax = $t.Last }
    }
}

function New-ToolRow {
    param([string]$Name, $A)
    $avg = $null
    if ($A.Calls -gt 0) { $avg = [math]::Round([double]$A.TotalMs / $A.Calls, 1) }
    $codes = [ordered]@{}
    foreach ($k in ($A.Codes.Keys | Sort-Object)) { $codes[$k] = $A.Codes[$k] }
    $top = @($A.Codes.GetEnumerator() |
            Sort-Object -Property @{ Expression = { $_.Value }; Descending = $true }, @{ Expression = { $_.Key }; Descending = $false } |
            Select-Object -First 5 | ForEach-Object { [pscustomobject]@{ code = [string]$_.Key; count = [long]$_.Value } })
    return [ordered]@{
        toolName                  = $Name
        calls                     = $A.Calls
        successes                 = $A.Successes
        errors                    = $A.Errors
        errorPercent              = Get-Percent $A.Errors $A.Calls
        avgMs                     = $avg
        maxMs                     = $A.MaxMs
        errorsByCode              = $codes
        topErrorCodes             = $top
        guidanceIssued            = $A.GIssued
        guidanceFollowed          = $A.GFollowed
        guidanceFollowedSucceeded = $A.GSucceeded
        guidanceIgnored           = $A.GIgnored
        followPercent             = Get-Percent $A.GFollowed $A.GIssued
        succeedPercent            = Get-Percent $A.GSucceeded $A.GFollowed
    }
}

$rows = @($agg.Values | Sort-Object -Property @{ Expression = { $_.Calls }; Descending = $true }, @{ Expression = { $_.Name }; Descending = $false } |
        ForEach-Object { New-ToolRow -Name $_.Name -A $_ })

$total = [pscustomobject]@{
    Calls = 0L; Successes = 0L; Errors = 0L; TotalMs = 0L; MaxMs = 0L
    GIssued = 0L; GFollowed = 0L; GSucceeded = 0L; GIgnored = 0L; Codes = @{}
}
foreach ($a in $agg.Values) {
    $total.Calls += $a.Calls; $total.Successes += $a.Successes; $total.Errors += $a.Errors; $total.TotalMs += $a.TotalMs
    if ($a.MaxMs -gt $total.MaxMs) { $total.MaxMs = $a.MaxMs }
    $total.GIssued += $a.GIssued; $total.GFollowed += $a.GFollowed; $total.GSucceeded += $a.GSucceeded; $total.GIgnored += $a.GIgnored
    foreach ($c in $a.Codes.Keys) {
        if ($total.Codes.ContainsKey($c)) { $total.Codes[$c] += $a.Codes[$c] } else { $total.Codes[$c] = $a.Codes[$c] }
    }
}
$totalRow = New-ToolRow -Name 'TOTAL' -A $total

# ---- write the rollup (only into OutDir, never over a source file) -----------------------------------------

$rollup = [ordered]@{
    generatedUtc = Format-Utc ([DateTime]::UtcNow)
    sourceFiles  = @($kept | ForEach-Object { $_.Path } | Sort-Object)
    skippedFiles = @($skipped.ToArray())  # NB: @(<empty List>) throws in Windows PowerShell 5.1
    instances    = $kept.Count
    dateRange    = [ordered]@{ firstCallUtc = (Format-Utc $rangeMin); lastCallUtc = (Format-Utc $rangeMax) }
    buildShas    = @($shas.ToArray() | Sort-Object)
    tools        = $rows
    totals       = $totalRow
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$outFile = Join-Path $OutDir ("rollup_{0}.json" -f $Date)
$json = ConvertTo-Json -InputObject $rollup -Depth 8
[System.IO.File]::WriteAllText($outFile, $json, (New-Object System.Text.UTF8Encoding($false)))

# ---- report ------------------------------------------------------------------------------------------------

if (-not $Quiet -and $rows.Count -gt 0) {
    $table = foreach ($r in (@($rows) + @($totalRow))) {
        [pscustomobject][ordered]@{
            Tool       = $r.toolName
            Calls      = $r.calls
            'Err%'     = Format-PercentText $r.errorPercent
            AvgMs      = if ($null -eq $r.avgMs) { 'n/a' } else { '{0:0.0}' -f $r.avgMs }
            MaxMs      = $r.maxMs
            GIssued    = $r.guidanceIssued
            GFollowed  = $r.guidanceFollowed
            GSucceeded = $r.guidanceFollowedSucceeded
            GIgnored   = $r.guidanceIgnored
            'Follow%'  = Format-PercentText $r.followPercent
        }
    }
    Write-Host (($table | Format-Table -AutoSize | Out-String -Width 200).TrimEnd())
}
Write-Host ("Instances: {0}; range: {1} .. {2}; buildShas: {3}" -f $kept.Count, (Format-Utc $rangeMin), (Format-Utc $rangeMax), (($shas.ToArray() | Sort-Object) -join ', '))
Write-Host ("Rollup written: {0}" -f $outFile)
if ($skipped.Count -gt 0) {
    Write-Host ("Skipped {0} unreadable file(s):" -f $skipped.Count)
    foreach ($s in $skipped) { Write-Host ("  {0} - {1}" -f $s.file, $s.reason) }
}
if ($files.Count -gt 0 -and $kept.Count -eq 0) { exit 1 }
exit 0

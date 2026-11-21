#requires -Version 7.0
<#
.SYNOPSIS
    Eval host status: per endpoint reachability, loaded models and queue state (plan_eval_host_table_and_liveness, Step 3).

.DESCRIPTION
    Probes every entry of scripts\eval-hosts.json (or only the entries of -Machine, matched by machine key) in parallel
    with Get-EvalHostModels, then reads the queue snapshot with 'roslynsentinel-evalqueue.ps1 status -Json' unless
    -SkipQueue is given. The queue read is what 'status' always does: it marks Building/Running jobs whose worker has
    gone as Crashed.

    Queue folders are one per machine. Snapshots are grouped by machine: the snapshot's 'machine' field when the queue
    script emits it, otherwise the entry whose suffix equals the folder name, otherwise the folder name itself. Every
    endpoint row shows the queue state of its machine, so two endpoints of one machine show the same queue.

    Output: one row per endpoint with machine, alias, backend, baseUrl, default, reachable, loaded (ids with state
    true; empty when the host reports no state), contextLength (of the first loaded entry), error and queue (the queue
    state of its machine: workerAlive, Active jobs, QueuedCount and Queued, or null when the machine has no queue folder). -Json prints the rows as a JSON array. Default output is a
    table plus one line per machine for its queue.

    Exit code: 0 whether or not hosts answer; 2 when a -Machine key is not in the table.

.PARAMETER Machine
    Machine keys to show, for example 113 or 112,85. Default: every machine in the table.

.PARAMETER TimeoutSec
    Timeout for each endpoint request, in seconds.

.PARAMETER ThrottleLimit
    Number of endpoints probed at once.

.PARAMETER Json
    Print the rows as a JSON array instead of the table.

.PARAMETER SkipQueue
    Do not read the queue snapshot.

.PARAMETER QueueRoot
    Queue root passed to roslynsentinel-evalqueue.ps1 status. Default: the queue script's own default.

.PARAMETER TablePath
    Host table to read. Default: scripts\eval-hosts.json beside this script.

.EXAMPLE
    .\Get-EvalHostStatus.ps1

.EXAMPLE
    .\Get-EvalHostStatus.ps1 -Machine 113 -Json
#>
[CmdletBinding()]
param(
    [string[]]$Machine,
    [int]$TimeoutSec = 5,
    [int]$ThrottleLimit = 8,
    [switch]$Json,
    [switch]$SkipQueue,
    [string]$QueueRoot,
    [string]$TablePath
)

$ErrorActionPreference = 'Stop'
$libraryPath = Join-Path $PSScriptRoot 'roslynsentinel-evalhosts.ps1'
. $libraryPath

if (-not $TablePath) { $TablePath = Get-EvalHostTablePath }
$table = @(Read-EvalHostTable -Path $TablePath)

# 'pwsh -File ... -Machine 112,85' passes one string; split it. An in-process call passes an array. Accept both.
$wanted = @()
if ($Machine) { $wanted = @($Machine | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
foreach ($w in $wanted) {
    if (-not ($table.Machine -contains $w)) {
        $known = (@($table.Machine) | Sort-Object -Unique) -join ', '
        Write-Output "eval host status error: '$w' is not a machine key in $TablePath. Known machine keys: $known."
        exit 2
    }
}

$entries = @($table | Where-Object { ($wanted.Count -eq 0) -or ($wanted -contains $_.Machine) })

# Parallel probes. Runspaces do not see this script's functions, so each one dot-sources the library by path.
$probes = @($entries | ForEach-Object -Parallel {
    $lib = $using:libraryPath
    . $lib
    [pscustomobject]@{ Alias = $_.Alias; Result = (Get-EvalHostModels -Entry $_ -TimeoutSec $using:TimeoutSec) }
} -ThrottleLimit $ThrottleLimit)
$probeByAlias = @{}
foreach ($p in $probes) { $probeByAlias[$p.Alias] = $p.Result }

# ---- queue snapshot ----
$snapshots = @()
$queueError = $null
if (-not $SkipQueue) {
    # The queue script ends in 'exit', so it runs as a child process; its stdout is the status JSON.
    $queueScript = Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.ps1'
    $pwshExe = (Get-Process -Id $PID).Path
    $queueArgs = @('-NoProfile', '-File', $queueScript, 'status', '-Json')
    if ($QueueRoot) { $queueArgs += @('-QueueRoot', $QueueRoot) }
    $queueOut = @(& $pwshExe @queueArgs 2>$null)
    $queueCode = $LASTEXITCODE
    if ($queueCode -ne 0) {
        $queueError = "queue status exited $queueCode"
        if ($queueOut.Count -gt 0) { $queueError += ": $($queueOut -join ' ')" }
    }
    else {
        try { $snapshots = @(($queueOut -join "`n") | ConvertFrom-Json) }
        catch { $queueError = "queue status did not return JSON: $($_.Exception.Message)" }
    }
}

function Resolve-SnapshotMachine($Snapshot, [object[]]$Table) {
    if (($Snapshot.PSObject.Properties.Name -contains 'machine') -and $Snapshot.machine) { return [string]$Snapshot.machine }
    $folder = [string]$Snapshot.host
    $bySuffix = @($Table | Where-Object { $_.Suffix -eq $folder })
    if ($bySuffix.Count -gt 0) { return [string]$bySuffix[0].Machine }
    # Unknown folder: the folder name stands in for its machine key.
    return $folder
}

function New-JobView($Job) {
    $sessions = @($Job.sessions | Where-Object { $_ })
    if ($sessions.Count -eq 0) { $sessions = @([string]$Job.session) }
    [pscustomobject]@{
        Id       = [string]$Job.id
        State    = [string]$Job.state
        Test     = [string]$Job.test
        Model    = [string]$Job.model
        Sessions = $sessions
        Purpose  = [string]$Job.purpose
        Endpoint = [string]$Job.host
    }
}

$queueViews = @()
if (-not $SkipQueue -and -not $queueError) {
    foreach ($s in $snapshots) {
        $machineKey = Resolve-SnapshotMachine $s $table
        if (($wanted.Count -gt 0) -and -not ($wanted -contains $machineKey)) { continue }
        $jobs = @($s.jobs)
        # A retired folder (no machine in the table, e.g. an old URL-keyed folder) with no worker and no Queued, Building or
        # Running job is history: leave it out of the rows and the machine lines.
        $inTable = [bool](@($table | Where-Object { $_.Machine -eq $machineKey }).Count -gt 0)
        $hasLiveJob = [bool](@($jobs | Where-Object { $_.state -in @('Queued', 'Building', 'Running') }).Count -gt 0)
        if (-not $inTable -and -not $hasLiveJob -and -not $s.workerAlive) { continue }
        $queued = @($jobs | Where-Object { $_.state -eq 'Queued' } | Sort-Object -Property @{ Expression = { [int]$_.priority } }, 'id')
        $waiting = [bool]($s.workerAlive -and $s.worker -and ($s.worker.state -eq 'WaitingForHost'))
        $queueViews += [pscustomobject]@{
            Folder        = [string]$s.host
            Machine       = $machineKey
            Error         = $null
            WorkerAlive   = [bool]$s.workerAlive
            WorkerWaiting = $waiting
            Active        = @($jobs | Where-Object { $_.state -in @('Building', 'Running') } | ForEach-Object { New-JobView $_ })
            QueuedCount   = $queued.Count
            Queued        = @($queued | ForEach-Object { New-JobView $_ })
        }
    }
}

# The queue state of one machine: every snapshot folder that maps to it (the machine's own folder and any legacy
# folder of the same machine) merged into one view. Folder is the machine's own folder when there is one.
function Get-MachineQueueView([string]$Machine) {
    $views = @($queueViews | Where-Object { $_.Machine -eq $Machine })
    if ($views.Count -eq 0) { return $null }
    $ordered = @($views | Where-Object { $_.Folder -eq $Machine }) + @($views | Where-Object { $_.Folder -ne $Machine })
    $queued = @($views | ForEach-Object { $_.Queued })
    [pscustomobject]@{
        Folder        = $ordered[0].Folder
        Machine       = $Machine
        Error         = $null
        WorkerAlive   = [bool](@($views | Where-Object { $_.WorkerAlive }).Count -gt 0)
        WorkerWaiting = [bool](@($views | Where-Object { $_.WorkerWaiting }).Count -gt 0)
        Active        = @($views | ForEach-Object { $_.Active })
        QueuedCount   = $queued.Count
        Queued        = $queued
    }
}

# ---- rows: one per endpoint ----
$rows = @(foreach ($e in $entries) {
    $res = $probeByAlias[$e.Alias]
    $loadedModels = @($res.Models | Where-Object { $_.Loaded -eq $true })
    $contextLength = $null
    if ($loadedModels.Count -gt 0) { $contextLength = $loadedModels[0].ContextLength }
    $queue = $null
    if ($queueError) {
        $queue = [pscustomobject]@{ Error = $queueError }
    }
    else {
        $queue = Get-MachineQueueView $e.Machine
    }
    [pscustomobject]@{
        Machine       = $e.Machine
        Alias         = $e.Alias
        Backend       = $e.Backend
        BaseUrl       = $e.BaseUrl
        Default       = $e.IsDefault
        Reachable     = [bool]$res.Reachable
        Loaded        = @($loadedModels | ForEach-Object { $_.Id })
        ContextLength = $contextLength
        Error         = $res.Error
        Queue         = $queue
    }
})

function Get-ShortText($Text, [int]$Max = 60) {
    $t = [string]$Text
    if ($t.Length -gt $Max) { return $t.Substring(0, $Max - 3) + '...' }
    return $t
}

function Format-QueueText($Queue) {
    if ($null -eq $Queue) { return '-' }
    if ($Queue.Error) { return "error: $($Queue.Error)" }
    $worker = 'no worker'
    if ($Queue.WorkerAlive) { $worker = 'worker running' }
    if ($Queue.WorkerWaiting) { $worker = 'worker waiting for host' }
    $parts = @($worker)
    foreach ($a in @($Queue.Active)) { $parts += "$($a.State) $($a.Id) $($a.Test) [$(@($a.Sessions) -join '+')]" }
    $parts += "queued $($Queue.QueuedCount)"
    foreach ($q in @($Queue.Queued)) { $parts += "$($q.Id) [$(@($q.Sessions) -join '+')]" }
    return ($parts -join '; ')
}

if ($Json) {
    ConvertTo-Json -InputObject @($rows) -Depth 8
    return
}

$display = @(foreach ($r in $rows) {
    $loadedText = @($r.Loaded) -join ', '
    if (-not $loadedText) { $loadedText = '-' }
    [pscustomobject]@{
        Machine       = $r.Machine
        Alias         = $r.Alias
        Backend       = $r.Backend
        BaseUrl       = $r.BaseUrl
        Default       = $r.Default
        Reachable     = $r.Reachable
        Loaded        = $loadedText
        ContextLength = $r.ContextLength
        Error         = (Get-ShortText $r.Error)
        Queue         = (Format-QueueText $r.Queue)
    }
})
$tableText = $display | Format-Table -AutoSize | Out-String -Width 400
Write-Output $tableText.TrimEnd()

# One line per machine for its queue (all of its endpoint folders together).
if (-not $SkipQueue) {
    $machines = @(@($entries.Machine) + @($queueViews.Machine) | Select-Object -Unique)
    foreach ($m in $machines) {
        if ($queueError) { Write-Output "Machine ${m}: queue unavailable: $queueError"; continue }
        $views = @($queueViews | Where-Object { $_.Machine -eq $m })
        if ($views.Count -eq 0) { Write-Output "Machine ${m}: no queue folder yet"; continue }
        $alive = @($views | Where-Object { $_.WorkerAlive }).Count -gt 0
        $waiting = @($views | Where-Object { $_.WorkerWaiting }).Count -gt 0
        $workerText = 'no worker'
        if ($alive) { $workerText = 'worker running' }
        if ($waiting) { $workerText = 'worker waiting for host' }
        $line = "Machine ${m}: $workerText"
        foreach ($a in @($views | ForEach-Object { $_.Active })) {
            $line += "; $($a.State) $($a.Id) $($a.Test) on $($a.Endpoint) ($($a.Model)) for $(@($a.Sessions) -join '+') `"$($a.Purpose)`""
        }
        $queued = @($views | ForEach-Object { $_.Queued })
        $line += "; queued $($queued.Count)"
        if ($queued.Count -gt 0) {
            $line += ': ' + (@($queued | ForEach-Object { "$($_.Id) [$(@($_.Sessions) -join '+')]" }) -join ', ')
        }
        Write-Output $line
    }
}
exit 0

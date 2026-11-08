<#
.SYNOPSIS
    Starts a detached background prebuild of the RoslynSentinel server. Called from the git hooks in
    .githooks (post-commit, post-merge, post-rewrite). Windows PowerShell 5.1.

.DESCRIPTION
    Two modes.

    Starter (default). Resolves the instances root from -InstancesRoot, then git config
    roslynsentinel.instancesRoot, then $env:ROSLYNSENTINEL_INSTANCES_ROOT. If none is set it exits 0
    silently (hooks not configured). If the current branch is not the prebuild branch (git config
    roslynsentinel.prebuildBranch, default master) it exits 0. Otherwise it starts the worker fully
    detached and returns at once. The worker is started without -Wait, -NoNewWindow or redirection, so it
    never inherits git's pipes; a child holding them would hang the git operation.

    Worker (-Worker). Single flight under <root>\builds\prebuild.lock. If the lock is held, it sets
    <root>\builds\prebuild.pending and exits. Otherwise it holds the lock and runs
    scripts\roslynsentinel-mcp-launch.ps1 -BuildOnly as a child process, logging to
    <root>\builds\prebuild.log. Each run first deletes the pending file; it runs again while a commit
    raised the pending flag during the run, at most 5 runs in all. A lock older than 30 minutes is treated
    as stale. The worker removes only its own lock on exit and leaves the pending file alone. It releases
    the lock before checking pending, so a commit that sets pending late is not lost: if pending exists
    after release, the worker starts a new detached worker to handle it. If the 5-run cap was hit, no
    hand-off is made and pending is left for the next commit, so a stuck loop cannot respawn forever.

    Never throws and always exits 0, so a git operation is never failed or delayed by it.

    One-time setup per clone: scripts\Install-GitHooks.ps1.

.PARAMETER InstancesRoot
    Prod-instances root, for example C:\Users\Administrator\RoslynSentinelInstances.

.PARAMETER Worker
    Internal. Run as the detached worker instead of the starter.
#>
[CmdletBinding()]
param(
    [string]$InstancesRoot,
    [switch]$Worker
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $repo 'scripts\roslynsentinel-mcp-launch.ps1'
$maxRuns = 5
$staleLockAge = New-TimeSpan -Minutes 30

function Get-GitConfigValue([string]$Name) {
    $value = & git -C $repo config --get $Name 2>$null
    if ($LASTEXITCODE -eq 0 -and $value) { return [string]($value | Select-Object -First 1) }
    return $null
}

function Start-DetachedWorker {
    $workerArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-Worker', '-InstancesRoot', "`"$InstancesRoot`"")
    Start-Process -FilePath powershell.exe -WindowStyle Hidden -ArgumentList $workerArgs | Out-Null
}

function Invoke-Starter {
    $branch = [string](& git -C $repo rev-parse --abbrev-ref HEAD 2>$null | Select-Object -First 1)
    $prebuildBranch = Get-GitConfigValue 'roslynsentinel.prebuildBranch'
    if (-not $prebuildBranch) { $prebuildBranch = 'master' }
    if ($branch -ne $prebuildBranch) { return }
    Start-DetachedWorker
}

function Invoke-Worker {
    $buildsDir = Join-Path $InstancesRoot 'builds'
    $logPath = Join-Path $buildsDir 'prebuild.log'
    $lockPath = Join-Path $buildsDir 'prebuild.lock'
    $pendingPath = Join-Path $buildsDir 'prebuild.pending'

    function Write-PrebuildLog([string]$Message) {
        try {
            Add-Content -LiteralPath $logPath -Encoding ASCII -Value ("Prebuild worker PID={0}: {1} {2}" -f $PID, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
        }
        catch { }
    }

    function Open-Lock {
        return [System.IO.File]::Open($lockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    }

    New-Item -ItemType Directory -Force -Path $buildsDir | Out-Null

    $lockStream = $null
    try {
        $lockStream = Open-Lock
    }
    catch [System.IO.IOException] {
        if ((Test-Path -LiteralPath $lockPath) -and ((Get-Item -LiteralPath $lockPath).LastWriteTime -lt (Get-Date).Add(-$staleLockAge))) {
            Write-PrebuildLog 'lock older than 30 min, treating it as stale and deleting it'
            try {
                Remove-Item -LiteralPath $lockPath -Force -ErrorAction Stop
                $lockStream = Open-Lock
            }
            catch { $lockStream = $null }
        }
    }

    if ($null -eq $lockStream) {
        Set-Content -LiteralPath $pendingPath -Encoding ASCII -Value ("requested by PID {0}" -f $PID)
        Write-PrebuildLog 'lock held, pending set'
        return
    }

    $capped = $false
    try {
        $lockText = "PID={0} started={1}`r`n" -f $PID, (Get-Date -Format o)
        $lockBytes = [System.Text.Encoding]::ASCII.GetBytes($lockText)
        $lockStream.Write($lockBytes, 0, $lockBytes.Length)
        $lockStream.Flush()

        for ($run = 1; $run -le $maxRuns; $run++) {
            if (Test-Path -LiteralPath $pendingPath) { Remove-Item -LiteralPath $pendingPath -Force -ErrorAction SilentlyContinue }
            Write-PrebuildLog ("run {0}: starting launcher -BuildOnly" -f $run)
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "`"$launcher`"" -InstancesRoot "`"$InstancesRoot`"" -BuildOnly
            $exitCode = $LASTEXITCODE
            Write-PrebuildLog ("run {0}: launcher -BuildOnly exited with code {1}" -f $run, $exitCode)
            if (-not (Test-Path -LiteralPath $pendingPath)) { break }
            if ($run -eq $maxRuns) {
                $capped = $true
                Write-PrebuildLog ("pending still set after {0} runs, stopping; pending left for the next commit" -f $maxRuns)
            }
        }
    }
    finally {
        $lockStream.Dispose()
        try {
            if (Test-Path -LiteralPath $lockPath) {
                $ownText = [System.IO.File]::ReadAllText($lockPath)
                if ($ownText.StartsWith("PID=$PID ")) { Remove-Item -LiteralPath $lockPath -Force }
            }
        }
        catch { }
    }

    # Lock is released; a commit that set pending while we held it is handed to a new worker.
    if ((-not $capped) -and (Test-Path -LiteralPath $pendingPath)) {
        Write-PrebuildLog 'pending set after release, handing off to a new worker'
        Start-DetachedWorker
    }
}

try {
    if (-not $InstancesRoot) { $InstancesRoot = Get-GitConfigValue 'roslynsentinel.instancesRoot' }
    if (-not $InstancesRoot) { $InstancesRoot = $env:ROSLYNSENTINEL_INSTANCES_ROOT }
    if (-not $InstancesRoot) { exit 0 }

    if ($Worker) { Invoke-Worker } else { Invoke-Starter }
}
catch { }
exit 0

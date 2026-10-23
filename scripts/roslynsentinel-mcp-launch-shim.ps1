<#
.SYNOPSIS
    Thin entry point for the OPT-IN prod-instances layout: syncs the prod clone, then runs the launcher
    that belongs to the commit it is about to build. What a user-level MCP config can point at instead of
    roslynsentinel-mcp-launch.ps1.

.DESCRIPTION
    Plan: plan_build_isolation_prod_instances_and_test_servers, Step 3.3 item 2.

    1. Resolves the instances root (roslynsentinel-instances-common.ps1): -InstancesRoot, else env
       ROSLYNSENTINEL_INSTANCES_ROOT, else (only if the $UseDefaultInstancesRootWhenUnset switch in that
       file is flipped) %USERPROFILE%\RoslynSentinelInstances. With no root the layout is not opted into
       and this shim simply runs the dev repo's own launcher, i.e. the legacy path.
    2. Clones the dev repo (the repo this shim lives in) into <root>\source on first use and runs
       `git pull --ff-only origin <Branch>`. A failed pull (dev repo mid-rebase, diverged history) keeps
       the current source HEAD and is logged to <root>\launch-startup.log.
    3. Runs <root>\source\scripts\roslynsentinel-mcp-launch.ps1, so the launcher always matches the
       commit it builds, passing -InstancesRoot, -Branch, -DevRepo and -SkipSourceSync plus every
       remaining argument (the server args) verbatim.
    4. FALLBACK: on any shim-side failure before the server is started (clone failure, missing or
       too-old source launcher, launcher exit code 97 = "prod layout could not be set up") the shim logs
       the reason and runs the dev repo's own launcher with -ForceLegacy, so a window always gets a
       server. After the server has been started the shim never starts a second one.

    stdout is the MCP JSON-RPC stream in stdio mode: nothing here writes to it; all diagnostics go to
    the log file. Runs under Windows PowerShell 5.1.

.PARAMETER InstancesRoot
    Opt-in root; overrides ROSLYNSENTINEL_INSTANCES_ROOT.

.PARAMETER Branch
    Branch the prod clone tracks. Default: master.

.PARAMETER SyncOnly
    Diagnostic: clone/pull <root>\source, log, and exit 0 without launching anything.

.EXAMPLE
    "args": ["-NoProfile","-ExecutionPolicy","Bypass","-File","<repo>\scripts\roslynsentinel-mcp-launch-shim.ps1","--mode=...", ...]
    with "env": { "ROSLYNSENTINEL_INSTANCES_ROOT": "C:\\Users\\<you>\\RoslynSentinelInstances" }
#>
# PositionalBinding = $false is REQUIRED: without it Windows PowerShell 5.1 binds the first passthrough server args
# (--mode=..., --include-tools=...) positionally to the first named string parameter instead of
# ServerArgs (confirmed with a splatted array; the server then starts with no --mode).
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$InstancesRoot,
    [string]$Branch = 'master',
    [switch]$SyncOnly,

    # Everything else is forwarded verbatim to the launcher / server.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServerArgs
)

$ErrorActionPreference = 'Stop'
$devRepoRoot = Split-Path $PSScriptRoot -Parent
$devLauncher = Join-Path $PSScriptRoot 'roslynsentinel-mcp-launch.ps1'
$commonScript = Join-Path $PSScriptRoot 'roslynsentinel-instances-common.ps1'

$prodRoot = $null
$commonLoaded = $false
try {
    . $commonScript
    $commonLoaded = $true
    $prodRoot = Get-RoslynSentinelInstancesRoot -Explicit $InstancesRoot
}
catch {
    # Cannot even resolve the layout: behave as if the shim did not exist (the dev launcher decides).
    $prodRoot = $null
}

if (-not $prodRoot) {
    # Not opted in (or the helper is unusable): the legacy launcher, untouched.
    & $devLauncher @ServerArgs
    exit $LASTEXITCODE
}

$startupLog = Join-Path $prodRoot 'launch-startup.log'
function Write-ShimLog {
    param([string]$Message)
    try {
        New-Item -ItemType Directory -Path $prodRoot -Force | Out-Null
        Add-Content -Path $startupLog -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [shim pid=$PID] $Message" -ErrorAction Stop
    }
    catch { }
}

$handedOff = $false
try {
    Write-ShimLog "Shim launched. root=$prodRoot, branch=$Branch, devRepo=$devRepoRoot, args=$($ServerArgs -join ' ')"
    $sourceDir = Join-Path $prodRoot 'source'
    $sync = Sync-RoslynSentinelSource -SourceDir $sourceDir -DevRepo $devRepoRoot -Branch $Branch -Log { param($m) Write-ShimLog $m }

    if ($SyncOnly) {
        Write-ShimLog "SyncOnly: done (sha=$($sync.Sha), pullOk=$($sync.PullOk))."
        exit 0
    }

    $sourceLauncher = Join-Path $sourceDir 'scripts\roslynsentinel-mcp-launch.ps1'
    if (-not (Test-Path -LiteralPath $sourceLauncher)) { throw "Source launcher '$sourceLauncher' does not exist." }
    # A source clone older than this feature has a launcher without these parameters; it would forward
    # them to the server as unknown arguments. Refuse it instead.
    if (-not (Select-String -LiteralPath $sourceLauncher -Pattern 'SkipSourceSync' -Quiet)) {
        throw "Source launcher '$sourceLauncher' predates the prod-instances layout (no -SkipSourceSync parameter)."
    }

    Write-ShimLog "Handing off to $sourceLauncher"
    $handedOff = $true
    & $sourceLauncher -InstancesRoot $prodRoot -Branch $Branch -DevRepo $devRepoRoot -SkipSourceSync @ServerArgs
    $launcherExit = $LASTEXITCODE
    if ($launcherExit -ne 97) { exit $launcherExit }
    $handedOff = $false
    throw "Source launcher exited with 97 (prod layout could not be set up; no server was started)."
}
catch {
    if ($handedOff) {
        # The server may already have run; never start a second one.
        Write-ShimLog "Shim error after hand-off, not falling back: $($_.Exception.Message)"
        exit 1
    }
    Write-ShimLog "SHIM FALLBACK to the dev repo's legacy launcher: $($_.Exception.Message)"
}

& $devLauncher -ForceLegacy @ServerArgs
exit $LASTEXITCODE

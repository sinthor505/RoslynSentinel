<#
.SYNOPSIS
    Per-VS-Code-window launcher for the RoslynSentinel.Server.Advanced stdio MCP server. Invoked by
    C:\Users\Administrator\.mcp.json in place of the .exe directly, so each window gets its own
    isolated build/process.

.DESCRIPTION
    TWO LAYOUTS - the legacy one is the default, the prod-instances one is OPT-IN.

    Opt-in: the prod-instances layout (see "Prod-instances layout" below) is used only when the
    environment variable ROSLYNSENTINEL_INSTANCES_ROOT is set (put it in the "env" block of the MCP
    config), or this script is given -InstancesRoot <path>. With neither, everything below under
    "Legacy layout" runs exactly as it always has. To make the default root
    (%USERPROFILE%\RoslynSentinelInstances) apply when the variable is unset, flip the single switch
    $UseDefaultInstancesRootWhenUnset at the top of roslynsentinel-instances-common.ps1 (default $false).
    Rollback: unset the variable / drop the switch back to $false; nothing in the legacy layout is
    touched by the prod layout. -ForceLegacy forces the legacy layout even when a root is configured
    (used by roslynsentinel-mcp-launch-shim.ps1 as its fallback).

    ---- Legacy layout ----
    Historically, C:\Users\Administrator\.mcp.json invoked
    bin-vscode\Advanced\RoslynSentinel.Server.Advanced.exe directly - one fixed path shared by every
    VS Code window on the machine. build.ps1's Invoke-VSCodeStdioRebuild kept that one binary fresh,
    but rebuilding it meant killing whatever process (any window's) currently held that path - so a
    session editing RoslynSentinel's own source couldn't rebuild without disconnecting every sibling
    window mid-session.

    This script gives each launch its own instance folder, keyed by a short random token generated
    fresh every time this script runs (plus a short hash of the repo path, so the same
    C:\Users\Administrator\.mcp.json entry stays correct if invoked from a different clone of this
    repo). On every launch it:

      1. Derives <instance-id> = "<repoHash>-<random-token>".
      2. Sweeps bin-vscode\ for OTHER <instance-id>-shaped folders and deletes each one with its own
         Remove-Item call (never one bulk delete over bin-vscode\ itself - a locked file from a live
         sibling window would abort or partially corrupt a single recursive delete over the whole
         root; per-folder calls just silently fail on whichever folder is still in use, and get
         retried on a future launch). Because the token is random every launch, this instance never
         collides with its own predecessor's folder either, so the sweep - not a same-key reuse - is
         the only cleanup path; a still-running predecessor (this window's old process, or another
         window's) simply gets swept on ITS next launch instead of blocking this one. A folder younger
         than the minimum sweep age ($minimumAgeForSweep, 3600 s; see the comment at its definition)
         is skipped regardless of lock state, since several windows/tabs launching near-simultaneously
         against a freshly-cleared bin-vscode\ can otherwise race: one window's sweep deleting
         another's folder while its build is still in flight (confirmed 2026-09-18 via a missing
         launch.log after a simultaneous 4-tab VS Code launch).
      3. Builds RoslynSentinel.Server.Advanced.csproj directly (not the .slnx - this already skips
         all Tests* projects) into bin-vscode\<instance-id>\Advanced, every launch, unconditionally -
         no mtime/staleness check. MSBuild's own incremental up-to-date check already makes a no-op
         rebuild cheap.
      4. Launches the built exe with --transport=stdio plus whatever args this script itself was
         given (typically --include-tools=...); when no --solution arg was passed, the cwd is inside
         the repo, and RoslynSentinel.slnx exists, automatically appends --solution=<repoRoot>\RoslynSentinel.slnx.
         C:\Users\Administrator\.mcp.json's command line stays the one place the arg list is kept,
         not duplicated here.

    Full design rationale: docs/current/proposal_per_session_mcp_server.md.
    bin-vscode\Advanced.Http\ (the separate, still-shared standalone HTTP fallback copy, managed by
    roslynsentinel-vscode-control.ps1; its `restart` rebuilds it) is untouched by this script - it
    never matches the <instance-id> naming shape, so the sweep skips it. Throwaway HTTP instances come
    from Launch-RoslynSentinelHttpServer.ps1 (bin-vscode\http-<port>-<timestamp>, also not matching).

    Why not key off %VSCODE_PID%: it looked window-unique but isn't - confirmed 2026-09-18 that
    VSCODE_PID (and every other VS Code-injected env var, including VSCODE_IPC_HOOK, which also
    looked promising and also turned out shared) is the single main/browser process ID for the whole
    VS Code application launch, identical across every window opened from it. Two windows on the
    same repo would derive the same instance-id and fight over one folder's DLLs as live,
    simultaneously-running servers, not just during a rebuild race. No VS Code-injected env var
    reliably distinguishes one window from another, so a random token sidesteps the problem instead
    of chasing a better key. McpServerStatus's serverBinaryPath (which embeds the instance-id) is how
    a session confirms which running instance it is actually talking to.

    ---- Prod-instances layout (opt-in; plan_build_isolation_prod_instances_and_test_servers Step 3.3) ----
    Root = the configured instances root; the dev repo = the checkout that holds the shim/this script
    (-DevRepo, or the origin of <root>\source when this script runs from the prod clone):

      <root>\source\          prod clone of the dev repo (git clone on first use, then
                              git pull --ff-only origin <Branch> on every launch; -SkipSourceSync when the
                              shim already did it). A failed pull keeps the current HEAD and is logged.
      <root>\builds\<sha8>\   immutable build of one commit: Advanced\ (the server) + 'complete' marker +
                              'lastused' stamp. <sha8> is the first 8 chars of the commit sha. Shared
                              read-only by every window on that commit.
      <root>\instances\<id>\  --working-dir of one server process (logs, tool dumps, crash.log, marker).
      <root>\wt\<token>\      throwaway git worktree used to build one commit.
      <root>\launch-startup.log

    On every launch: sync source -> HEAD sha -> if builds\<sha8>\complete exists use it (fast path);
    else build it in a throwaway worktree: `git worktree add --detach wt\<token> <sha>`,
    `dotnet build ... -o builds\<sha8>.<token>\Advanced -p:SourceRevisionId=<sha>` (the server reads the
    full sha back from AssemblyInformationalVersion), write 'complete', Rename-Item to builds\<sha8> (first
    rename wins; a loser deletes its own copy), `git worktree remove --force` + `git worktree prune`.
    A failed build falls back to the newest 'complete' build with a loud warning in launch.log and
    launch-startup.log (and one line on stderr); every launch also writes instances\<id>\launch-outcome.json
    (buildFailed=true when this fallback ran). Then it sweeps and starts
    builds\<sha8>\Advanced\RoslynSentinel.Server.Advanced.exe with --working-dir=<root>\instances\<id>;
    ROSLYNSENTINEL_INSTANCE_ID=<id> is exported to the server process (Build's throwaway output dir).
    No copy per instance, no lock: concurrent windows on one commit produce ONE builds\<sha8>.

    Sweep (same single minimum age for everything, see $minimumAgeForSweep): instances\<id> whose
    process is gone (matched on --working-dir in the Win32_Process command line, because the exe is
    shared); builds\<sha8> with no process whose ExecutablePath is under it (never the build in use or the
    newest 'complete' one); abandoned builds\<sha8>.<token> and wt\* leftovers; and the dev repo's
    .roslynsentinel\build\<id> for dead instance ids.

    Exit code 97 means "prod layout could not be set up, no server was started" (the shim falls back to
    the legacy launcher on it); any other code is the server's own.

.NOTES
    stdout/stderr must stay completely clean for the stdio JSON-RPC transport once the real server
    process is running - so every diagnostic this script itself emits goes to a log file, never to
    the console (the only exception is a single stderr line for the prod-layout last-good-build
    warning, since stderr is not part of the JSON-RPC stream). Two log files, split because instance ID
    derivation itself can fail (it did, 2026-09-18 - see the RandomNumberGenerator.Fill comment below):
      - launch-startup.log (legacy: bin-vscode\; prod: <root>\): one shared, append-only file across
        every launch (of every instance). Written from the very first line the script executes, before
        an instance ID or instance folder exists, through the moment control hands off to the built
        exe. This is the file to check first for a launch that never got as far as producing an
        instance folder at all - it logs the process ID, PSVersionTable, and every step up to instance
        ID derivation, plus a full exception+stack trace for anything unhandled anywhere in the script
        (see the top-level try/catch).
      - <instance folder>\launch.log (legacy: bin-vscode\<instance-id>\; prod: <root>\instances\<id>\):
        per-instance, one step per line (sweep, build start, build result, launch) from the moment its
        instance ID is known onward.
      - <instance folder>\launch-outcome.json: rewritten on every prod launch; the build served (servedSha),
        the HEAD attempted (attemptedSha), buildFailed and up to 3 first compile errors.

    If VS Code never seems to launch this script at all (no entry appears in
    launch-startup.log, not even a "Script launched" line), the fault is usually one
    level up: check C:\Users\Administrator\.mcp.json first. It invokes this script via
    powershell.exe -File plus a hand-edited JSON "args" array (--include-tools=... and the
    --replace-snippet-max-* flags) - a missing comma or a truncated flag there is invalid JSON, so
    VS Code's MCP client silently never gets a valid launch command and this script never runs.
    Validate it (e.g. any JSON parser) before assuming this script itself is broken.

    If launch-startup.log DOES show "Script launched" but nothing (or very little) after it, the
    script itself is crashing early - read the rest of that file, including the last entry, which
    for an unhandled exception will be the full exception type/message/stack trace.

    A cold prod-layout build (a commit with no builds\<sha8> yet) takes longer than a legacy launch, once
    per commit; raise "startup_timeout_sec" in the MCP config accordingly (the first window pays it,
    the others reuse the result).

    Runs under Windows PowerShell 5.1 (what the MCP config's powershell.exe is): no PowerShell 7-only syntax.
#>
# PositionalBinding = $false is REQUIRED: without it Windows PowerShell 5.1 binds the first passthrough server args
# (--mode=..., --include-tools=...) positionally to the first named string parameter instead of
# ServerArgs (confirmed with a splatted array; the server then starts with no --mode).
[CmdletBinding(PositionalBinding = $false)]
param(
    # Opt-in prod-instances root (see .DESCRIPTION). Overrides ROSLYNSENTINEL_INSTANCES_ROOT.
    [string]$InstancesRoot,

    # Branch the prod clone tracks (prod layout only).
    [string]$Branch = 'master',

    # The dev repo (prod layout only). Default: the origin of <root>\source when this script runs from
    # the prod clone, else the repo this script lives in.
    [string]$DevRepo,

    # Prod layout only: the caller (the shim) already cloned/pulled <root>\source; do not pull again.
    [switch]$SkipSourceSync,

    # Run the legacy layout even when an instances root is configured.
    [switch]$ForceLegacy,

    # Captures all passthrough args (e.g. --include-tools=...) so PowerShell's normal named-parameter
    # binding never tries to match them against declared parameters - everything after the named
    # parameters above is forwarded verbatim to the real server exe.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServerArgs
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

# Hard safety check before $repoRoot is used for ANYTHING, especially deriving $binVscodeRoot for
# the sweep's recursive deletes below: $PSScriptRoot is trusted to resolve to this script's real
# location, but if it were ever empty/null/unexpected (a bad copy, an unusual invocation context, a
# future refactor), a silently-wrong $repoRoot would mean the sweep runs its recursive deletes
# against some OTHER directory's "bin-vscode" subfolder entirely, matching folders purely by an
# 8-hex-8-hex NAME pattern with no idea what directory it's actually standing in. A bare "ends in
# 'bin-vscode'" string check would still pass for a WRONG repoRoot (any directory has a plausible
# bin-vscode child) - checking for this specific project's own .csproj instead confirms $repoRoot
# really is this repo, not just that the path shape looks superficially right. Refusing to proceed
# at all otherwise makes a wrong resolution loud (a clear stderr/log message before build, not a
# mystery about missing files afterward, or worse, a silent delete somewhere unexpected).
$repoMarkerProject = Join-Path $repoRoot 'RoslynSentinel.Server.Advanced\RoslynSentinel.Server.Advanced.csproj'
if (-not (Test-Path -LiteralPath $repoMarkerProject)) {
    $message = "Refusing to continue: expected to find $repoMarkerProject but it does not exist - PSScriptRoot may have resolved unexpectedly (PSScriptRoot='$PSScriptRoot', derived repoRoot='$repoRoot'). Not safe to sweep/build here."
    Write-Error $message
    exit 1
}

#region Layout decision (legacy vs opt-in prod-instances)
# $prodRoot is $null for the legacy layout. NB: PowerShell variable names are case-insensitive, so it
# must not be spelled like the $InstancesRoot parameter.
$prodRoot = $null
$commonScript = Join-Path $PSScriptRoot 'roslynsentinel-instances-common.ps1'
if (Test-Path -LiteralPath $commonScript) {
    . $commonScript
    if (-not $ForceLegacy) { $prodRoot = Get-RoslynSentinelInstancesRoot -Explicit $InstancesRoot }
}
elseif (-not $ForceLegacy -and ($InstancesRoot -or $env:ROSLYNSENTINEL_INSTANCES_ROOT)) {
    [Console]::Error.WriteLine("roslynsentinel-mcp-launch.ps1: an instances root is configured but $commonScript is missing; cannot use the prod-instances layout.")
    exit 97
}
$prodMode = [bool]$prodRoot
# Failure exit code before the server is started: 97 in the prod layout (the shim keys its fallback on
# it), 1 in the legacy layout (unchanged).
$setupFailureExitCode = if ($prodMode) { 97 } else { 1 }

if ($prodMode) {
    $sourceDir = Join-Path $prodRoot 'source'
    $buildsRoot = Join-Path $prodRoot 'builds'
    $wtRoot = Join-Path $prodRoot 'wt'
    $instancesDirRoot = Join-Path $prodRoot 'instances'
    try {
        foreach ($d in @($prodRoot, $buildsRoot, $wtRoot, $instancesDirRoot)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }
    }
    catch {
        [Console]::Error.WriteLine("roslynsentinel-mcp-launch.ps1: cannot create the instances root '$prodRoot': $($_.Exception.Message)")
        exit 97
    }
    $logRoot = $prodRoot
}
else {
    $binVscodeRoot = Join-Path $repoRoot 'bin-vscode'
    New-Item -ItemType Directory -Path $binVscodeRoot -Force | Out-Null
    $logRoot = $binVscodeRoot
}
#endregion

# A fixed, pre-instance log target so a crash before the instance ID is even derived (the exact
# failure mode that hid the RandomNumberGenerator.Fill/.NET Framework bug on 2026-09-18 - the
# script died ~430ms in, before $logPath below existed, so nothing was ever written to disk) still
# leaves a trace. Appended to (not overwritten) since multiple launches share it; each entry is
# timestamped so it reads like a history across launches, not a single-run log.
$startupLogPath = Join-Path $logRoot 'launch-startup.log'

# Appends one timestamped line. In the prod layout a logging failure (several windows appending to the
# same file at once can hit a sharing violation) must never abort a launch, so it is swallowed there;
# the legacy layout keeps its original fail-loud behaviour.
function Add-LogLine {
    param([string]$Path, [string]$Message)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    if ($prodMode) {
        try { Add-Content -Path $Path -Value $line -ErrorAction Stop } catch { }
    }
    else {
        Add-Content -Path $Path -Value $line
    }
}

function Write-StartupLog {
    param([string]$Message)
    Add-LogLine -Path $startupLogPath -Message $Message
}

$layoutDescription = if ($prodMode) { "prod-instances (root=$prodRoot, branch=$Branch)" } else { 'legacy (bin-vscode)' }
Write-StartupLog "Script launched. PID=$PID, PSVersion=$($PSVersionTable.PSVersion), layout=$layoutDescription, args=$($ServerArgs -join ' ')"

try {

#region Dev repo
# The checkout whose slnx a server started from its cwd should load, and whose path seeds the
# instance-id hash. Legacy: this script's own repo (unchanged). Prod: -DevRepo, else the origin of the
# prod clone when this script runs from it (the shim runs <root>\source\scripts\...), else this repo.
if ($prodMode) {
    if ($DevRepo) {
        $devRepoRoot = [System.IO.Path]::GetFullPath($DevRepo).TrimEnd('\')
    }
    elseif ($repoRoot.TrimEnd('\') -ieq $sourceDir.TrimEnd('\')) {
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $repoRoot, 'remote', 'get-url', 'origin')
        if ($r.ExitCode -ne 0 -or -not $r.Output.Trim()) { throw "Cannot determine the dev repo (git remote get-url origin failed): $($r.Output)" }
        $devRepoRoot = [System.IO.Path]::GetFullPath($r.Output.Trim()).TrimEnd('\')
    }
    else {
        $devRepoRoot = $repoRoot
    }
}
else {
    $devRepoRoot = $repoRoot
}
#endregion

#region Instance ID derivation
# A fresh random token every launch - see .DESCRIPTION for why this replaced %VSCODE_PID% (which
# turned out shared across every window in one VS Code application launch, not window-unique).
# 4 random bytes as 8 lowercase hex chars - same shape/length as the repoHash below, collision odds
# (1 in 2^32 per pair of concurrent launches) are irrelevant here since a same-repo collision just
# costs one extra sweep-and-retry on next launch, never silent cross-instance corruption.
Write-StartupLog "Deriving instance ID..."
$randomBytes = [byte[]]::new(4)
# RandomNumberGenerator.Fill (static) is .NET 5+ only, like SHA256.HashData below - not available
# under Windows PowerShell 5.1's .NET Framework runtime, which is what C:\Users\Administrator\.mcp.json's
# registered "powershell.exe" command actually runs under (confirmed 2026-09-18: this was silently
# throwing MethodNotFound and killing the script ~430ms after launch, before the build step ever
# ran - every VS Code-spawned launch failed this way while a manual pwsh/dotnet invocation looked
# fine). Create()+GetBytes works on both runtimes.
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $rng.GetBytes($randomBytes)
}
finally {
    $rng.Dispose()
}
$instanceToken = -join ($randomBytes | ForEach-Object { $_.ToString('x2') })

# Short, filesystem-safe secondary key so the same C:\Users\Administrator\.mcp.json entry can't
# collide across different clones of this repo on the same machine - a bare token alone wouldn't
# distinguish which repo's server should be running. (Prod layout: hashed from the DEV repo, not the
# prod clone, so the id prefix is the same whichever launcher copy ran.)
$normalizedRepoRoot = $devRepoRoot.TrimEnd('\').ToLowerInvariant()
# [System.Security.Cryptography.SHA256]::HashData is .NET 5+ only - not available under Windows
# PowerShell 5.1's .NET Framework runtime, which is what C:\Users\Administrator\.mcp.json's
# registered command actually runs under. Create()+ComputeHash works on both.
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $repoHashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalizedRepoRoot))
}
finally {
    $sha256.Dispose()
}
$repoHash = -join ($repoHashBytes[0..3] | ForEach-Object { $_.ToString('x2') })

# repoHash first so folders for the same repo sort/group together in a directory listing when
# multiple repos share one machine's bin-vscode\ (not possible here, since bin-vscode\ is per-repo,
# but this script is invoked with $repoRoot derived fresh each time, so the ordering is cosmetic-only
# and free to pick for readability).
$instanceId = "$repoHash-$instanceToken"
if ($prodMode) {
    $instanceDir = Join-Path $instancesDirRoot $instanceId
    New-Item -ItemType Directory -Path $instanceDir -Force | Out-Null
}
else {
    $instanceDir = Join-Path $binVscodeRoot $instanceId
    $outDir = Join-Path $instanceDir 'Advanced'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$logPath = Join-Path $instanceDir 'launch.log'
function Write-LaunchLog {
    param([string]$Message)
    Add-LogLine -Path $logPath -Message $Message
}

Write-LaunchLog "Instance ID '$instanceId' derived from random token='$instanceToken', repoHash='$repoHash' (repo: $devRepoRoot, layout: $layoutDescription)."
Write-StartupLog "Instance ID '$instanceId' derived OK -> per-instance log is $logPath (subsequent steps logged there, not here)."
#endregion

#region Sweep settings (shared by both layouts)
# Minimum age before a folder is eligible for sweeping - ONE value for every sweep rule in both
# layouts (instance folders, builds\<sha8>, abandoned builds, wt\ leftovers, .roslynsentinel\build\<id>).
# Without it, several VS Code windows/tabs launching near-simultaneously against a freshly-cleared
# root can race: window A creates its instance folder and starts building, then window B launches
# moments later, and B's sweep deletes A's folder out from under its still-running build - wasted work
# and a silently lost instance, confirmed 2026-09-18 via a missing launch.log on one of two folders
# after a simultaneous 4-tab VS Code launch. A cold prod-layout build can take minutes, so the margin is
# an hour, not seconds; the cost is only that actually-stale (long-dead) folders are removed on a later
# launch instead of the next one. ROSLYNSENTINEL_SWEEP_MIN_AGE_SECONDS overrides it (diagnostics/tests).
$minimumAgeForSweep = [TimeSpan]::FromSeconds(3600)
if ($env:ROSLYNSENTINEL_SWEEP_MIN_AGE_SECONDS -match '^\d+$') {
    $minimumAgeForSweep = [TimeSpan]::FromSeconds([int]$env:ROSLYNSENTINEL_SWEEP_MIN_AGE_SECONDS)
}
#endregion

if (-not $prodMode) {

#region Sweep stale instance folders
Write-LaunchLog "Sweeping for stale instance folders..."
# Matches this script's own <8-hex-char-token>-<8-hex-char-hash> shape. Advanced.Http (the shared
# HTTP fallback copy) and http-<port>-<timestamp> (throwaway HTTP instances) never match this and are
# left untouched without needing an explicit exclusion.
$instanceFolderPattern = '^[0-9a-f]{8}-[0-9a-f]{8}$'

# Every currently-running RoslynSentinel.Server.Advanced.exe's own module path, gathered once
# before the sweep loop (one CIM/WMI query instead of one per candidate folder). A live process's
# own .exe is reliably locked on Windows, but sibling files in the same instance folder -
# launch.log, and previously a would-be server.pid marker - are NOT reliably locked, so checking
# Remove-Item's failure alone let a sweep silently, partially delete a still-connected, still-
# running sibling instance's folder out from under it (confirmed 2026-09-18). Matching on the
# running exe's actual path sidesteps needing any cooperation from the launch step (a PID-file
# handoff was tried and reverted - see the Launch region below for why) and needs no new state:
# every live instance's exe path already embeds its own instance folder name.
$liveServerExePaths = @(
    Get-CimInstance Win32_Process -Filter "Name = 'RoslynSentinel.Server.Advanced.exe'" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty ExecutablePath -ErrorAction SilentlyContinue
)
Write-LaunchLog "Sweep: found $($liveServerExePaths.Count) live RoslynSentinel.Server.Advanced.exe process(es) on this machine."

$sweptNames = [System.Collections.Generic.List[string]]::new()
$skippedTooYoungCount = 0
Get-ChildItem -LiteralPath $binVscodeRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match $instanceFolderPattern -and $_.Name -ne $instanceId } |
    ForEach-Object {
        $age = (Get-Date) - $_.CreationTime
        if ($age -lt $minimumAgeForSweep) {
            Write-LaunchLog "Sweep: '$($_.Name)' is only $([int]$age.TotalSeconds)s old (< $([int]$minimumAgeForSweep.TotalSeconds)s minimum) - likely still building from a concurrent launch, skipped."
            $skippedTooYoungCount++
            return
        }

        $candidateExePath = Join-Path $_.FullName 'Advanced\RoslynSentinel.Server.Advanced.exe'
        if ($liveServerExePaths -contains $candidateExePath) {
            Write-LaunchLog "Sweep: '$($_.Name)' has a live server process running $candidateExePath - left in place, will retry next launch."
            return
        }

        # A leftover marker here means this stale instance's own server process never got to
        # consume it (see ServerStartupHelpers.ReadAndConsumeStoppedByScriptMarker) - most likely
        # because roslynsentinel-vscode-control.ps1 stopped it and it was never relaunched. Log it
        # before the folder (and marker with it) is deleted, since this sweep is the only other
        # place that would ever see it.
        $staleMarker = Join-Path $_.FullName 'stopped-by-script.marker'
        if (Test-Path -LiteralPath $staleMarker) {
            $markerContents = Get-Content -LiteralPath $staleMarker -Raw -ErrorAction SilentlyContinue
            Write-LaunchLog "Sweep: '$($_.Name)' had a stopped-by-script marker (never consumed by a relaunch): $markerContents"
        }

        # Second, independent safety check immediately before the actually-destructive call, not
        # just relying on the one-time $binVscodeRoot validation ~80 lines above this loop - this is
        # the line that recursively deletes a whole folder, so it re-confirms every path it is about
        # to act on is really under a 'bin-vscode' directory and really under THIS run's
        # $binVscodeRoot specifically (StartsWith, not just a substring match, so a folder merely
        # named e.g. 'not-bin-vscode-root\bin-vscode-lookalike' can't slip through). Any future
        # refactor of this loop that widens what it iterates over would have to defeat both checks,
        # not just one, to turn into an accidental delete-anything-matching-a-name-pattern script.
        if (-not $_.FullName.StartsWith($binVscodeRoot, [StringComparison]::OrdinalIgnoreCase) -or $_.FullName -notmatch '[\\/]bin-vscode[\\/]') {
            Write-LaunchLog "Sweep: REFUSING to delete '$($_.FullName)' - it is not under the expected bin-vscode root '$binVscodeRoot'. This should be impossible; treating as a bug and skipping rather than deleting."
            return
        }

        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $_.FullName)) {
            $sweptNames.Add($_.Name)
        }
        else {
            Write-LaunchLog "Sweep: '$($_.Name)' still present after delete attempt (likely locked by a running process) - left in place, will retry next launch."
        }
    }
$sweptNamesDisplay = if ($sweptNames.Count -gt 0) { " ($($sweptNames -join ', '))" } else { '' }
Write-LaunchLog "Sweep: removed $($sweptNames.Count) stale instance folder(s)$sweptNamesDisplay, skipped $skippedTooYoungCount too-young to sweep."
#endregion

#region Build
Write-LaunchLog "Build starting..."
$project = Join-Path $repoRoot 'RoslynSentinel.Server.Advanced\RoslynSentinel.Server.Advanced.csproj'
$exePath = Join-Path $outDir 'RoslynSentinel.Server.Advanced.exe'

# NOTE: a per-instance -p:BaseIntermediateOutputPath override was tried here (to stop concurrent
# builds from different windows racing on the SHARED obj\ next to each .csproj - only the -o output
# dir below is per-instance today) and reverted after live testing proved it unsafe as a simple
# flag: overriding BaseIntermediateOutputPath moves which obj\ path the SDK auto-excludes from the
# default **/*.cs compile glob, so the OLD default obj\Debug\ and obj\Release\ folders next to each
# .csproj stop being excluded and their stale generated files (AssemblyInfo.cs etc.) get compiled
# a second time alongside the new ones - confirmed via CS0579 duplicate-attribute errors in a direct
# test build, both with and without an accompanying -p:DefaultItemExcludes append (command-line
# property overrides don't compose with the SDK's own default-excludes computation cleanly). Fixing
# this properly needs more than a one-line flag - e.g. a Directory.Build.props-level conditional
# scoped to instance builds, or pre-cleaning the default obj\/bin\ before redirecting - and is
# tracked separately rather than risking a broken build on every launch. See
# docs/current/blockers/resolved/blocking_error_loadsolution_missing_msbuild_workspaces_assembly.md
# for the concurrent-build race this was meant to close.

$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$buildOutput = & dotnet build $project -c Debug -o $outDir --nologo -v quiet 2>&1
$buildExit = $LASTEXITCODE
$ErrorActionPreference = $previousEap

if ($buildExit -ne 0) {
    Write-LaunchLog "Build FAILED (exit $buildExit). Not launching a stale/partial binary. Build output:`n$($buildOutput -join [Environment]::NewLine)"
    exit 1
}
Write-LaunchLog "Build succeeded -> $exePath"
#endregion

}
else {

#region Prod layout: source sync, per-SHA build, sweep
# Everything here keeps stdout untouched (native output is captured by Invoke-NativeCaptured).
$exePath = $null

# --- source clone: clone on first use, fast-forward on every launch (unless the shim just did) ---
$sha = $null
try {
    if ($SkipSourceSync) {
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $sourceDir, 'rev-parse', 'HEAD')
        $sha = $r.Output.Trim()
        if ($r.ExitCode -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') { throw "git rev-parse HEAD failed in '$sourceDir' (exit $($r.ExitCode)): $($r.Output)" }
        Write-LaunchLog "Source: sync done by the caller; HEAD is $sha."
    }
    else {
        $sync = Sync-RoslynSentinelSource -SourceDir $sourceDir -DevRepo $devRepoRoot -Branch $Branch -Log { param($m) Write-LaunchLog $m }
        $sha = $sync.Sha
    }
}
catch {
    $sha = $null
    $msg = "Source sync failed: $($_.Exception.Message)"
    Write-LaunchLog $msg
    Write-StartupLog "[$instanceId] $msg"
}

# Builds <sha> in a throwaway worktree of the prod clone and publishes it as builds\<sha8> by rename.
# <sha8> is the first 8 chars of the full commit sha (the folder name); the full sha is still used for git
# and -p:SourceRevisionId. Returns $true when builds\<sha8>\complete exists afterwards (built here, or
# published first by a racing launch), $false otherwise. Never throws.
function Invoke-ProdBuild {
    param([string]$Sha)

    $shortSha = $Sha.Substring(0, 8)
    $final = Join-Path $buildsRoot $shortSha
    $buildTmp = Join-Path $buildsRoot "$shortSha.$instanceToken"
    $outTmp = Join-Path $buildTmp 'Advanced'
    $wt = Join-Path $wtRoot $instanceToken
    $worktreeAdded = $false
    try {
        Write-LaunchLog "Build starting for $Sha (worktree $wt -> $outTmp)..."
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $sourceDir, 'worktree', 'add', '--detach', $wt, $Sha)
        if ($r.ExitCode -ne 0) { throw "git worktree add failed (exit $($r.ExitCode)): $($r.Output)" }
        $worktreeAdded = $true

        $project = Join-Path $wt 'RoslynSentinel.Server.Advanced\RoslynSentinel.Server.Advanced.csproj'
        # Same invocation as the legacy build (-c Debug -o <dir> --nologo -v quiet) plus the SHA stamp: the
        # SDK appends '+<sha>' to AssemblyInformationalVersion, which the server reads for git-based staleness.
        $r = Invoke-NativeCaptured -File 'dotnet' -Arguments @('build', $project, '-c', 'Debug', '-o', $outTmp, '--nologo', '-v', 'quiet', "-p:SourceRevisionId=$Sha")
        if ($r.ExitCode -ne 0) {
            $tail = (($r.Output -split "`r?`n") | Select-Object -Last 60) -join [Environment]::NewLine
            throw "dotnet build FAILED (exit $($r.ExitCode)). Last 60 output lines:`n$tail"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $outTmp 'RoslynSentinel.Server.Advanced.exe'))) {
            throw "dotnet build reported success but $outTmp has no RoslynSentinel.Server.Advanced.exe."
        }

        Set-Content -LiteralPath (Join-Path $buildTmp 'complete') -Value "sha=$Sha builtBy=$instanceId at=$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ss')"

        $published = $false
        for ($attempt = 1; $attempt -le 3 -and -not $published; $attempt++) {
            try {
                Rename-Item -LiteralPath $buildTmp -NewName $shortSha -ErrorAction Stop
                $published = $true
                Write-LaunchLog "Build published as $final."
            }
            catch {
                if (Test-Path -LiteralPath (Join-Path $final 'complete')) {
                    Write-LaunchLog "Another launch published $final first; deleting own copy '$buildTmp'."
                    Remove-Item -LiteralPath $buildTmp -Recurse -Force -ErrorAction SilentlyContinue
                    return $true
                }
                # Transient (virus scanner / indexer holding a file): retry.
                Write-LaunchLog "Publish rename attempt $attempt failed: $($_.Exception.Message)"
                Start-Sleep -Seconds 1
            }
        }
        if (-not $published) { throw "Could not rename '$buildTmp' to '$shortSha' after 3 attempts." }
        return $true
    }
    catch {
        $script:lastBuildFailureText = $_.Exception.Message
        Write-LaunchLog "Build of $Sha FAILED: $($_.Exception.Message)"
        Remove-Item -LiteralPath $buildTmp -Recurse -Force -ErrorAction SilentlyContinue
        return $false
    }
    finally {
        if ($worktreeAdded -or (Test-Path -LiteralPath $wt)) {
            $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $sourceDir, 'worktree', 'remove', '--force', $wt)
            if ($r.ExitCode -ne 0) {
                Write-LaunchLog "git worktree remove failed (exit $($r.ExitCode)): $($r.Output) - deleting folder directly; the sweep prunes leftovers."
                Remove-Item -LiteralPath $wt -Recurse -Force -ErrorAction SilentlyContinue
            }
            $null = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $sourceDir, 'worktree', 'prune')
        }
    }
}

# Writes <instance dir>\launch-outcome.json (overwritten every launch): which build this server runs and
# whether the HEAD build failed (buildFailed=true means the last-good fallback ran). The server reads it for
# McpServerStatus.lastLaunch. Never throws; a marker failure is logged and the launch continues.
function Write-LaunchOutcome {
    param([bool]$BuildFailed, [string]$AttemptedSha, [string]$ServedDir)

    try {
        $firstErrors = @()
        if ($BuildFailed) {
            if ($null -eq $script:lastBuildFailureText) {
                $firstErrors = @('no build attempted: source sync failed or HEAD unknown')
            }
            else {
                $errorLines = @($script:lastBuildFailureText -split "`r?`n" |
                    Where-Object { $_ -match 'error [A-Z]+[0-9]+' } |
                    Select-Object -First 3 |
                    ForEach-Object {
                        $line = $_ -replace '^.*\\wt\\[0-9a-f]+\\', ''
                        if ($line.Length -gt 300) { $line.Substring(0, 300) } else { $line }
                    })
                if ($errorLines.Count -gt 0) { $firstErrors = $errorLines }
                else {
                    # No compiler-style line (e.g. git worktree add failed): use the first non-empty line instead.
                    $firstLine = @($script:lastBuildFailureText -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1)
                    if ($firstLine.Count -gt 0) {
                        $line = $firstLine[0] -replace '^.*\\wt\\[0-9a-f]+\\', ''
                        if ($line.Length -gt 300) { $line = $line.Substring(0, 300) }
                        $firstErrors = @($line)
                    }
                }
            }
        }
        $servedSha = Split-Path -Path $ServedDir -Leaf
        $outcome = [ordered]@{
            utc          = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
            attemptedSha = $AttemptedSha
            servedSha    = $servedSha
            buildFailed  = $BuildFailed
            firstErrors  = $firstErrors
        }
        $json = ConvertTo-Json -InputObject $outcome -Compress
        Set-Content -LiteralPath (Join-Path $instanceDir 'launch-outcome.json') -Value $json -Encoding ASCII
        Write-LaunchLog "Launch outcome written: buildFailed=$($BuildFailed.ToString().ToLower()) served=$servedSha"
    }
    catch {
        Write-LaunchLog "Launch outcome marker not written: $($_.Exception.Message)"
    }
}

# --- choose the build: fast path, slow path, or last-good fallback ---
$selectedBuildDir = $null
$usedFallback = $false
$script:lastBuildFailureText = $null
if ($sha) {
    $shaDir = Join-Path $buildsRoot $sha.Substring(0, 8)
    if (Test-Path -LiteralPath (Join-Path $shaDir 'complete')) {
        $selectedBuildDir = $shaDir
        Write-LaunchLog "Fast path: builds\$($sha.Substring(0, 8)) is complete; skipping build."
    }
    elseif (Invoke-ProdBuild -Sha $sha) {
        $selectedBuildDir = $shaDir
    }
}
if (-not $selectedBuildDir) {
    $newest = Get-NewestCompleteBuild -BuildsRoot $buildsRoot
    if ($newest) {
        $selectedBuildDir = $newest.FullName
        $usedFallback = $true
        $warning = "WARNING: could not produce a build for the current source HEAD (sha=$sha); falling back to the newest complete build $($newest.Name). This server is OLDER than the source; see the lines above for the reason."
        Write-LaunchLog $warning
        Write-StartupLog "[$instanceId] $warning"
        [Console]::Error.WriteLine("roslynsentinel-mcp-launch.ps1: $warning")
    }
    else {
        $msg = "No build for HEAD (sha=$sha) and no complete build to fall back to under $buildsRoot. Not launching."
        Write-LaunchLog $msg
        Write-StartupLog "[$instanceId] $msg"
        exit 97
    }
}
$attemptedShaShort = if ($sha) { $sha.Substring(0, 8) } else { '' }
Write-LaunchOutcome -BuildFailed:$usedFallback -AttemptedSha $attemptedShaShort -ServedDir $selectedBuildDir
$exePath = Join-Path $selectedBuildDir 'Advanced\RoslynSentinel.Server.Advanced.exe'
# Stamp the build as in use so another window's sweep (age is measured from this stamp) cannot remove it
# between now and the server process starting.
try { Set-Content -LiteralPath (Join-Path $selectedBuildDir 'lastused') -Value (Get-Date -Format 'yyyy-MM-ddTHH:mm:ss') } catch { }

# Deletes $Path (a folder) after re-checking it lies strictly under $MustBeUnder and is not a reparse
# point (Remove-Item -Recurse on a junction can follow it on Windows PowerShell 5.1). Returns $true when
# the folder is gone afterwards.
function Remove-SweepFolder {
    param([string]$Path, [string]$MustBeUnder)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (-not $item) { return $true }
    if (($Path.TrimEnd('\') -ieq $MustBeUnder.TrimEnd('\')) -or -not (Test-PathUnderRoot -Path $Path -Root $MustBeUnder)) {
        Write-LaunchLog "Sweep: REFUSING to delete '$Path' - not strictly under '$MustBeUnder'. Treating as a bug and skipping."
        return $false
    }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        Write-LaunchLog "Sweep: REFUSING to delete '$Path' - it is a reparse point."
        return $false
    }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    return (-not (Test-Path -LiteralPath $Path))
}

#region Sweep (prod layout)
try {
    Write-LaunchLog "Sweep: starting (minimum age $([int]$minimumAgeForSweep.TotalSeconds)s)."
    $procs = @(Get-RoslynSentinelServerProcesses)
    Write-LaunchLog "Sweep: found $($procs.Count) live RoslynSentinel.Server.Advanced.exe process(es) on this machine."
    $liveWorkingDirs = @($procs | Where-Object { $_.WorkingDir } | ForEach-Object { $_.WorkingDir })
    $liveExePaths = @($procs | Where-Object { $_.ExecutablePath } | ForEach-Object { $_.ExecutablePath })
    $liveInstanceIds = @($liveWorkingDirs | ForEach-Object { Split-Path $_ -Leaf })
    $instanceFolderPattern = '^[0-9a-f]{8}-[0-9a-f]{8}$'
    $swept = [System.Collections.Generic.List[string]]::new()
    $skippedYoung = 0
    $keptLive = 0
    $now = Get-Date

    # instances\<id> whose server process is gone.
    Get-ChildItem -LiteralPath $instancesDirRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $instanceFolderPattern -and $_.Name -ne $instanceId } |
        ForEach-Object {
            if (($now - $_.CreationTime) -lt $minimumAgeForSweep) { $skippedYoung++; return }
            $folderPath = $_.FullName
            if (@($liveWorkingDirs | Where-Object { $_ -ieq $folderPath }).Count -gt 0) { $keptLive++; return }
            $staleMarker = Join-Path $folderPath 'stopped-by-script.marker'
            if (Test-Path -LiteralPath $staleMarker) {
                Write-LaunchLog "Sweep: instance '$($_.Name)' had a stopped-by-script marker (never consumed by a relaunch): $(Get-Content -LiteralPath $staleMarker -Raw -ErrorAction SilentlyContinue)"
            }
            if (Remove-SweepFolder -Path $folderPath -MustBeUnder $instancesDirRoot) { $swept.Add("instances\$($_.Name)") }
            else { Write-LaunchLog "Sweep: instance '$($_.Name)' still present after delete attempt - left in place, will retry next launch." }
        }

    # builds\<sha8> with no process running from it, plus abandoned builds\<sha8>.<token> partials. The build
    # this launch selected and the newest complete build (the "last good" fallback) are never swept.
    # Legacy 40-char folders ({8,40}) are still swept.
    $newestComplete = Get-NewestCompleteBuild -BuildsRoot $buildsRoot
    Get-ChildItem -LiteralPath $buildsRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $folderPath = $_.FullName
            if ($_.Name -match '^[0-9a-f]{8,40}$') {
                if ($folderPath -ieq $selectedBuildDir) { return }
                if ($newestComplete -and $folderPath -ieq $newestComplete.FullName) { return }
                $stampFile = Join-Path $folderPath 'lastused'
                $lastTouched = $_.CreationTime
                if (Test-Path -LiteralPath $stampFile) {
                    $stampTime = (Get-Item -LiteralPath $stampFile).LastWriteTime
                    if ($stampTime -gt $lastTouched) { $lastTouched = $stampTime }
                }
                if (($now - $lastTouched) -lt $minimumAgeForSweep) { $skippedYoung++; return }
                $prefix = $folderPath.TrimEnd('\') + '\'
                if (@($liveExePaths | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { $keptLive++; return }
                if (Remove-SweepFolder -Path $folderPath -MustBeUnder $buildsRoot) { $swept.Add("builds\$($_.Name)") }
                else { Write-LaunchLog "Sweep: build '$($_.Name)' still present after delete attempt - left in place, will retry next launch." }
            }
            elseif ($_.Name -match '^[0-9a-f]{8,40}\.[0-9a-f]{8}$') {
                if (($now - $_.CreationTime) -lt $minimumAgeForSweep) { $skippedYoung++; return }
                if (Remove-SweepFolder -Path $folderPath -MustBeUnder $buildsRoot) { $swept.Add("builds\$($_.Name)") }
            }
        }

    # wt\* worktree leftovers (a launch that died mid-build).
    $prunedWorktrees = $false
    Get-ChildItem -LiteralPath $wtRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            if (($now - $_.CreationTime) -lt $minimumAgeForSweep) { $skippedYoung++; return }
            $folderPath = $_.FullName
            $null = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $sourceDir, 'worktree', 'remove', '--force', $folderPath)
            if (Remove-SweepFolder -Path $folderPath -MustBeUnder $wtRoot) { $swept.Add("wt\$($_.Name)"); $prunedWorktrees = $true }
        }
    if ($prunedWorktrees) { $null = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $sourceDir, 'worktree', 'prune') }

    # The dev repo's .roslynsentinel\build\<id> (Build's throwaway output, keyed by ROSLYNSENTINEL_INSTANCE_ID)
    # for instance ids with no live process.
    $devBuildRoot = Join-Path $devRepoRoot '.roslynsentinel\build'
    Get-ChildItem -LiteralPath $devBuildRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $instanceFolderPattern -and $_.Name -ne $instanceId -and ($liveInstanceIds -notcontains $_.Name) } |
        ForEach-Object {
            if (($now - $_.CreationTime) -lt $minimumAgeForSweep) { $skippedYoung++; return }
            if (Remove-SweepFolder -Path $_.FullName -MustBeUnder $devBuildRoot) { $swept.Add(".roslynsentinel\build\$($_.Name)") }
        }

    $sweptDisplay = if ($swept.Count -gt 0) { " ($($swept -join ', '))" } else { '' }
    Write-LaunchLog "Sweep: removed $($swept.Count) item(s)$sweptDisplay, kept $keptLive live, skipped $skippedYoung too-young."
}
catch {
    # A sweep failure must never stop the server from starting.
    Write-LaunchLog "Sweep: aborted by an error (ignored): $($_.Exception.Message)"
}
#endregion

#endregion

}

#region Launch
if (-not (Test-Path -LiteralPath $exePath)) {
    Write-LaunchLog "Build reported success but $exePath does not exist - refusing to launch."
    exit $setupFailureExitCode
}

$hasSolutionArg = $false
foreach ($serverArg in $ServerArgs) {
    if ($serverArg -match '^--solution(=|$)') { $hasSolutionArg = $true }
}
# The solution a window opened inside the dev repo should load: always the DEV repo's, never the prod clone's.
$defaultSolution = Join-Path $devRepoRoot 'RoslynSentinel.slnx'
$cwd = (Get-Location).Path
$repoRootNormalized = $devRepoRoot.TrimEnd('\')
$cwdInRepo = ($cwd.TrimEnd('\') -ieq $repoRootNormalized) -or $cwd.StartsWith($repoRootNormalized + '\', [StringComparison]::OrdinalIgnoreCase)
$launchArgs = @()
foreach ($serverArg in $ServerArgs) {
    $launchArgs += $serverArg
}
if (-not $hasSolutionArg -and $cwdInRepo -and (Test-Path -LiteralPath $defaultSolution)) {
    $launchArgs += "--solution=$defaultSolution"
    Write-LaunchLog "Auto-load: no --solution passed and cwd '$cwd' is inside the repo; adding --solution=$defaultSolution"
}
else {
    Write-LaunchLog "Auto-load: not adding --solution (passed=$hasSolutionArg, cwd='$cwd', cwdInRepo=$cwdInRepo, slnxExists=$(Test-Path -LiteralPath $defaultSolution))."
}

# This repo keeps .cs files in uniform CRLF (.editorconfig, .gitattributes), so a window opened inside it pins
# the EOL policy to crlf instead of relying on the host OS default. Other repos stay on the server default
# (auto); an explicit --eol / ROSLYNSENTINEL_EOL always wins.
$hasEolArg = $false
foreach ($serverArg in $ServerArgs) {
    if ($serverArg -match '^--eol(=|$)') { $hasEolArg = $true }
}
if (-not $hasEolArg -and $cwdInRepo -and -not $env:ROSLYNSENTINEL_EOL) {
    $launchArgs += '--eol=crlf'
    Write-LaunchLog "EOL policy: no --eol passed and cwd is inside the repo; adding --eol=crlf"
}

# Prod layout only: all per-process output goes under the instance folder, and the id is exported so the
# server's Build/RunTest throwaway output dir (<dev repo>\.roslynsentinel\build\<id>) is keyed by it and
# can be swept. Legacy launches set neither (unchanged).
$extraLaunchArgs = @()
if ($prodMode) {
    $extraLaunchArgs = @("--working-dir=$instanceDir")
    $env:ROSLYNSENTINEL_INSTANCE_ID = $instanceId
}

Write-LaunchLog "Launching: $exePath --transport=stdio $($extraLaunchArgs -join ' ') $($launchArgs -join ' ')"
Write-StartupLog "Handing off to $exePath (instance '$instanceId') - further activity is in that instance's own launch.log, not here."

# `&` (not Start-Process) is required here: this script IS the stdio JSON-RPC transport process VS
# Code holds its pipes open to, and `&` inherits stdin/stdout/stderr directly onto the child with no
# handle games. Start-Process -NoNewWindow -PassThru was tried (to recover the child's real PID for
# the sweep's liveness check, since `&` blocks with no way to learn the PID before the child exits)
# and reverted after live testing showed the server process exiting within ~1 second every time
# under it, logging "transport completed reading messages" immediately - Start-Process's stdin
# handle duplication does not behave the same as `&`'s direct inheritance in this redirected/
# CreateNoWindow context, and the server correctly treats an apparently-closed stdin as the client
# disconnecting. Getting stdio transport wrong here silently breaks every MCP connection, so this
# reverts to the previously-working mechanism rather than risk it - the sweep's liveness check (see
# above) instead scans for any process whose own module path is under a candidate instance folder
# (prod layout: whose command line carries the instance's --working-dir), which needs no cooperation
# from this launch step at all.
& $exePath --transport=stdio @extraLaunchArgs @launchArgs
$serverExitCode = $LASTEXITCODE
Write-LaunchLog "Server process exited with code $serverExitCode."
exit $serverExitCode
#endregion

}
catch {
    # Catches anything unhandled above that isn't one of the deliberate `exit` early-outs (those
    # already logged their own reason and return before reaching here). $logPath may or may not
    # exist yet depending on how early the failure was, so always write to the pre-instance log,
    # and also to the per-instance one if it's been created.
    $errorMessage = "UNHANDLED EXCEPTION: $($_.Exception.GetType().FullName): $($_.Exception.Message)`n$($_.ScriptStackTrace)"
    Write-StartupLog $errorMessage
    if (Get-Command Write-LaunchLog -ErrorAction SilentlyContinue) {
        Write-LaunchLog $errorMessage
    }
    exit $setupFailureExitCode
}

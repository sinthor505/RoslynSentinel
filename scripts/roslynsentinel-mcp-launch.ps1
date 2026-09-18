<#
.SYNOPSIS
    Per-VS-Code-window launcher for the RoslynSentinel.Server.Advanced stdio MCP server. Invoked by
    C:\Users\Administrator\.mcp.json in place of the .exe directly, so each window gets its own
    isolated build/process.

.DESCRIPTION
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
         than 15 seconds is skipped regardless of lock state, since several windows/tabs launching
         near-simultaneously against a freshly-cleared bin-vscode\ can otherwise race: one window's
         sweep deleting another's folder while its build is still in flight (confirmed 2026-09-18 via
         a missing launch.log after a simultaneous 4-tab VS Code launch).
      3. Builds RoslynSentinel.Server.Advanced.csproj directly (not the .slnx - this already skips
         all Tests* projects) into bin-vscode\<instance-id>\Advanced, every launch, unconditionally -
         no mtime/staleness check. MSBuild's own incremental up-to-date check already makes a no-op
         rebuild cheap.
      4. Launches the built exe with --transport=stdio plus whatever args this script itself was
         given (typically --include-tools=...), so C:\Users\Administrator\.mcp.json's command line
         stays the one place that list is kept, not duplicated here.

    Full design rationale: docs/current/proposal_per_session_mcp_server.md.
    bin-vscode\Advanced.Http\ (the separate, still-shared standalone HTTP fallback copy managed by
    build.ps1's Invoke-VSCodeServerRestart) is untouched by this script - it never matches the
    <instance-id> naming shape, so the sweep skips it.

    Why not key off %VSCODE_PID%: it looked window-unique but isn't - confirmed 2026-09-18 that
    VSCODE_PID (and every other VS Code-injected env var, including VSCODE_IPC_HOOK, which also
    looked promising and also turned out shared) is the single main/browser process ID for the whole
    VS Code application launch, identical across every window opened from it. Two windows on the
    same repo would derive the same instance-id and fight over one folder's DLLs as live,
    simultaneously-running servers, not just during a rebuild race. No VS Code-injected env var
    reliably distinguishes one window from another, so a random token sidesteps the problem instead
    of chasing a better key. McpServerStatus's serverBinaryPath (which embeds the instance-id) is how
    a session confirms which running instance it is actually talking to.

.NOTES
    stdout/stderr must stay completely clean for the stdio JSON-RPC transport once the real server
    process is running - so every diagnostic this script itself emits goes to a log file, never to
    the console. Two log files, split because instance ID derivation itself can fail (it did,
    2026-09-18 - see the RandomNumberGenerator.Fill comment below):
      - bin-vscode\launch-startup.log: one shared, append-only file across every launch (of every
        instance). Written from the very first line the script executes, before an instance ID or
        instance folder exists, through the moment control hands off to the built exe. This is the
        file to check first for a launch that never got as far as producing an instance folder at
        all - it logs the process ID, PSVersionTable, and every step up to instance ID derivation,
        plus a full exception+stack trace for anything unhandled anywhere in the script (see the
        top-level try/catch).
      - bin-vscode\<instance-id>\launch.log: per-instance, one step per line (sweep, build start,
        build result, launch) from the moment its instance ID is known onward.

    If VS Code never seems to launch this script at all (no entry appears in
    bin-vscode\launch-startup.log, not even a "Script launched" line), the fault is usually one
    level up: check C:\Users\Administrator\.mcp.json first. It invokes this script via
    powershell.exe -File plus a hand-edited JSON "args" array (--include-tools=... and the
    --replace-snippet-max-* flags) - a missing comma or a truncated flag there is invalid JSON, so
    VS Code's MCP client silently never gets a valid launch command and this script never runs.
    Validate it (e.g. any JSON parser) before assuming this script itself is broken.

    If launch-startup.log DOES show "Script launched" but nothing (or very little) after it, the
    script itself is crashing early - read the rest of that file, including the last entry, which
    for an unhandled exception will be the full exception type/message/stack trace.
#>
[CmdletBinding()]
param(
    # Captures all passthrough args (e.g. --include-tools=...) so PowerShell's normal named-parameter
    # binding never tries to match them against declared parameters - this script declares none of
    # its own, everything after the script path is forwarded verbatim to the real server exe.
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

$binVscodeRoot = Join-Path $repoRoot 'bin-vscode'
New-Item -ItemType Directory -Path $binVscodeRoot -Force | Out-Null

# A fixed, pre-instance log target so a crash before the instance ID is even derived (the exact
# failure mode that hid the RandomNumberGenerator.Fill/.NET Framework bug on 2026-09-18 - the
# script died ~430ms in, before $logPath below existed, so nothing was ever written to disk) still
# leaves a trace. Appended to (not overwritten) since multiple launches share it; each entry is
# timestamped so it reads like a history across launches, not a single-run log.
$startupLogPath = Join-Path $binVscodeRoot 'launch-startup.log'
function Write-StartupLog {
    param([string]$Message)
    Add-Content -Path $startupLogPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
}

Write-StartupLog "Script launched. PID=$PID, PSVersion=$($PSVersionTable.PSVersion), args=$($ServerArgs -join ' ')"

try {

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
# distinguish which repo's server should be running.
$normalizedRepoRoot = $repoRoot.TrimEnd('\').ToLowerInvariant()
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
$instanceDir = Join-Path $binVscodeRoot $instanceId
$outDir = Join-Path $instanceDir 'Advanced'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$logPath = Join-Path $instanceDir 'launch.log'
function Write-LaunchLog {
    param([string]$Message)
    Add-Content -Path $logPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
}

Write-LaunchLog "Instance ID '$instanceId' derived from random token='$instanceToken', repoHash='$repoHash' (repo: $repoRoot)."
Write-StartupLog "Instance ID '$instanceId' derived OK -> per-instance log is $logPath (subsequent steps logged there, not here)."
#endregion

#region Sweep stale instance folders
Write-LaunchLog "Sweeping for stale instance folders..."
# Matches this script's own <8-hex-char-token>-<8-hex-char-hash> shape. Advanced.Http (the shared
# HTTP fallback copy) never matches this and is left untouched without needing an explicit exclusion.
$instanceFolderPattern = '^[0-9a-f]{8}-[0-9a-f]{8}$'

# Minimum folder age before it's eligible for sweeping. Without this, several VS Code windows/tabs
# launching near-simultaneously against a freshly-cleared bin-vscode\ can race: window A creates its
# instance folder and starts building (7-8s typical per docs/current - see this script's own build
# region), then window B launches moments later, and B's sweep deletes A's folder out from under its
# still-running build - wasted work and a silently lost instance, confirmed 2026-09-18 via a missing
# launch.log on one of two folders after a simultaneous 4-tab VS Code launch. A build never taking
# anywhere near 15s makes this a safe margin without meaningfully delaying real cleanup of
# actually-stale (long-dead) folders, which will simply get swept on a later launch instead.
$minimumAgeForSweep = [TimeSpan]::FromSeconds(15)

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

#region Launch
if (-not (Test-Path -LiteralPath $exePath)) {
    Write-LaunchLog "Build reported success but $exePath does not exist - refusing to launch."
    exit 1
}

Write-LaunchLog "Launching: $exePath --transport=stdio $($ServerArgs -join ' ')"
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
# above) instead scans for any process whose own module path is under a candidate instance folder,
# which needs no cooperation from this launch step at all.
& $exePath --transport=stdio @ServerArgs
$serverExitCode = $LASTEXITCODE
Write-LaunchLog "Server process exited with code $serverExitCode."
exit $serverExitCode
#endregion

}
catch {
    # Catches anything unhandled above that isn't one of the deliberate `exit 1` early-outs (those
    # already logged their own reason and return before reaching here). $logPath may or may not
    # exist yet depending on how early the failure was, so always write to the pre-instance log,
    # and also to the per-instance one if it's been created.
    $errorMessage = "UNHANDLED EXCEPTION: $($_.Exception.GetType().FullName): $($_.Exception.Message)`n$($_.ScriptStackTrace)"
    Write-StartupLog $errorMessage
    if (Get-Command Write-LaunchLog -ErrorAction SilentlyContinue) {
        Write-LaunchLog $errorMessage
    }
    exit 1
}

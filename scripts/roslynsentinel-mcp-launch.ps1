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

    This script gives each window its own instance folder, keyed by %VSCODE_PID% (the parent VS
    Code window's process ID - stable for that window's life, distinct across windows) plus a short
    hash of the repo path (so the same C:\Users\Administrator\.mcp.json entry stays correct if
    invoked from a different clone of this repo). On every launch it:

      1. Derives <instance-id> = "<pid>-<repoHash>".
      2. Sweeps bin-vscode\ for other <instance-id>-shaped folders and deletes each one with its own
         Remove-Item call (never one bulk delete over bin-vscode\ itself - a locked file from a live
         sibling window would abort or partially corrupt a single recursive delete over the whole
         root; per-folder calls just silently fail on whichever folder is still in use, and get
         retried on a future launch).
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

.NOTES
    stdout/stderr must stay completely clean for the stdio JSON-RPC transport once the real server
    process is running - so every diagnostic this script itself emits goes to
    bin-vscode\<instance-id>\launch.log, never to the console.

    If VS Code never seems to launch this script at all (no bin-vscode\<instance-id>\ folder
    appears, no launch.log written), the fault is usually one level up: check
    C:\Users\Administrator\.mcp.json first. It invokes this script via powershell.exe -File plus a
    hand-edited JSON "args" array (--include-tools=... and the --replace-snippet-max-* flags) - a
    missing comma or a truncated flag there is invalid JSON, so VS Code's MCP client silently never
    gets a valid launch command and this script never runs. Validate it (e.g. any JSON parser) before
    assuming this script itself is broken.
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
$binVscodeRoot = Join-Path $repoRoot 'bin-vscode'
New-Item -ItemType Directory -Path $binVscodeRoot -Force | Out-Null

#region Instance ID derivation
$pidSource = $env:VSCODE_PID
if ([string]::IsNullOrWhiteSpace($pidSource)) {
    # VSCODE_PID absent (e.g. a manual standalone invocation outside VS Code) - fall back to this
    # script's own process ID rather than failing outright. Logged below so the fallback is visible
    # if it's ever hit unexpectedly during real VS Code use.
    $pidSource = $PID
    $pidSourceLabel = "own PID (VSCODE_PID not set)"
}
else {
    $pidSourceLabel = "VSCODE_PID"
}

# Short, filesystem-safe secondary key so the same C:\Users\Administrator\.mcp.json entry can't
# collide across different clones of this repo on the same machine - a bare PID alone wouldn't
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

$instanceId = "$pidSource-$repoHash"
$instanceDir = Join-Path $binVscodeRoot $instanceId
$outDir = Join-Path $instanceDir 'Advanced'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$logPath = Join-Path $instanceDir 'launch.log'
function Write-LaunchLog {
    param([string]$Message)
    Add-Content -Path $logPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
}

Write-LaunchLog "Instance ID '$instanceId' derived from $pidSourceLabel='$pidSource', repoHash='$repoHash' (repo: $repoRoot)."
#endregion

#region Sweep stale instance folders
# Matches this script's own <pid>-<8-hex-char-hash> shape. Advanced.Http (the shared HTTP fallback
# copy) never matches this and is left untouched without needing an explicit exclusion.
$instanceFolderPattern = '^\d+-[0-9a-f]{8}$'

$sweepCount = 0
Get-ChildItem -LiteralPath $binVscodeRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match $instanceFolderPattern -and $_.Name -ne $instanceId } |
    ForEach-Object {
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

        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $_.FullName)) {
            $sweepCount++
        }
        else {
            Write-LaunchLog "Sweep: '$($_.Name)' still present after delete attempt (likely locked by a running process) - left in place, will retry next launch."
        }
    }
Write-LaunchLog "Sweep: removed $sweepCount stale instance folder(s)."
#endregion

#region Build
$project = Join-Path $repoRoot 'RoslynSentinel.Server.Advanced\RoslynSentinel.Server.Advanced.csproj'
$exePath = Join-Path $outDir 'RoslynSentinel.Server.Advanced.exe'

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
& $exePath --transport=stdio @ServerArgs
exit $LASTEXITCODE
#endregion

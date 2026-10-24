<#
.SYNOPSIS
    Shared helpers for the OPT-IN "prod instances" layout (plan_build_isolation_prod_instances_and_test_servers,
    Step 3.3). Dot-source this file; it runs nothing by itself.

.DESCRIPTION
    Dot-sourced by roslynsentinel-mcp-launch.ps1, roslynsentinel-mcp-launch-shim.ps1,
    roslynsentinel-vscode-control.ps1, Launch-RoslynSentinelHttpServer.ps1 and
    Get-UnknownParameterReport.ps1, so the opt-in decision and the process/working-dir matching live in
    exactly one place.

    Layout under the instances root (default %USERPROFILE%\RoslynSentinelInstances):

      source\                 prod clone of the dev repo (git clone, then git pull --ff-only on launch)
      builds\<sha8>\Advanced\ immutable build of one commit (<sha8> = first 8 chars of the commit sha), plus
                              builds\<sha8>\complete (marker) and
                              builds\<sha8>\lastused (touched by every launch that selects the build)
      instances\<id>\         --working-dir of one server process (logs\, tool_list*.json, crash.log,
                              stopped-by-script.marker, launch.log)
      wt\<token>\             throwaway git worktree used to build one commit; removed after the build
      launch-startup.log      shared, append-only launcher log

    stdout rule: these functions may be called while the caller's stdout is the MCP JSON-RPC stream, so
    nothing here writes to the output stream - native commands are captured, results are returned as
    objects.

    Compatible with Windows PowerShell 5.1 (what the MCP config's powershell.exe runs).
#>

# =====================================================================================================
# OPT-IN SWITCH - flip this one line to make "no env var" mean the default root.
#
#   $false (default): the prod-instances layout is used ONLY when the environment variable
#                     ROSLYNSENTINEL_INSTANCES_ROOT is set (or the launcher is given -InstancesRoot).
#                     Otherwise every script runs its legacy path (bin-vscode\, build on every launch).
#   $true:            when neither is given, the root is %USERPROFILE%\RoslynSentinelInstances, i.e. every
#                     launch from this checkout uses the prod-instances layout.
#
# Rolling back is the reverse: set it to $false (and unset the env var / drop -InstancesRoot).
# =====================================================================================================
$UseDefaultInstancesRootWhenUnset = $false

# Returns the full instances root path, or $null when the prod-instances layout is not in use.
function Get-RoslynSentinelInstancesRoot {
    [CmdletBinding()]
    param([string]$Explicit)

    $candidate = $null
    if ($Explicit) { $candidate = $Explicit }
    elseif ($env:ROSLYNSENTINEL_INSTANCES_ROOT) { $candidate = $env:ROSLYNSENTINEL_INSTANCES_ROOT }
    elseif ($UseDefaultInstancesRootWhenUnset -and $env:USERPROFILE) {
        $candidate = Join-Path $env:USERPROFILE 'RoslynSentinelInstances'
    }
    if (-not $candidate) { return $null }
    # MCP "env" blocks pass %VAR% literally; expand it, and refuse a relative root (GetFullPath would
    # silently resolve it against the current directory, i.e. nest the layout inside the repo).
    $candidate = [Environment]::ExpandEnvironmentVariables($candidate)
    if (-not [System.IO.Path]::IsPathRooted($candidate) -or $candidate -match '%[^%]+%') {
        throw "RoslynSentinel instances root '$candidate' is not an absolute path (unexpanded %VAR% or relative). Use a full path such as C:\Users\<you>\RoslynSentinelInstances."
    }
    return [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
}

# 4 random bytes as 8 lowercase hex chars. RandomNumberGenerator.Fill is .NET 5+ only; Create()+GetBytes
# also works on .NET Framework (Windows PowerShell 5.1).
function New-RoslynSentinelToken {
    $bytes = [byte[]]::new(4)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return (-join ($bytes | ForEach-Object { $_.ToString('x2') }))
}

# True when $Path is $Root itself or lies under it (ordinal, case-insensitive; never a substring match).
function Test-PathUnderRoot {
    param([string]$Path, [string]$Root)
    if (-not $Path -or -not $Root) { return $false }
    $p = $Path.TrimEnd('\')
    $r = $Root.TrimEnd('\')
    return ($p -ieq $r) -or $p.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}

# Runs a native command with stdout+stderr captured (never to the caller's output stream) and without
# $ErrorActionPreference='Stop' turning native stderr text into a terminating error. Returns
# [pscustomobject]@{ ExitCode; Output }. A command that cannot be started throws.
function Invoke-NativeCaptured {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$File, [string[]]$Arguments = @())

    $previousEap = $ErrorActionPreference
    $previousPrompt = $env:GIT_TERMINAL_PROMPT
    $ErrorActionPreference = 'Continue'
    $env:GIT_TERMINAL_PROMPT = '0'
    try {
        # Native stderr lines arrive as ErrorRecords under 2>&1; their message is the raw line (ToString()
        # of a blank one would print the exception type name instead).
        $lines = @(& $File @Arguments 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { "$_" } })
        $exit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousEap
        $env:GIT_TERMINAL_PROMPT = $previousPrompt
    }
    return [pscustomobject]@{ ExitCode = $exit; Output = ($lines -join [Environment]::NewLine) }
}

# Extracts the --working-dir value from a Win32_Process command line, or $null. Handles the forms
# PowerShell 5.1 produces: --working-dir=C:\x, "--working-dir=C:\a b\x" (whole argument quoted) and
# --working-dir="C:\a b\x".
function Get-WorkingDirFromCommandLine {
    param([string]$CommandLine)

    if (-not $CommandLine) { return $null }
    $m = [regex]::Match($CommandLine, '(?i)(?<wrapped>")?--working-dir=')
    if (-not $m.Success) { return $null }
    $rest = $CommandLine.Substring($m.Index + $m.Length)
    if ($m.Groups['wrapped'].Success) {
        $end = $rest.IndexOf('"')
        $value = if ($end -ge 0) { $rest.Substring(0, $end) } else { $rest }
    }
    elseif ($rest.StartsWith('"')) {
        $rest = $rest.Substring(1)
        $end = $rest.IndexOf('"')
        $value = if ($end -ge 0) { $rest.Substring(0, $end) } else { $rest }
    }
    else {
        $m2 = [regex]::Match($rest, '^[^\s"]+')
        $value = $m2.Value
    }
    if (-not $value) { return $null }
    return $value.TrimEnd('\')
}

# Every running RoslynSentinel.Server.Advanced.exe as { ProcessId; ExecutablePath; CommandLine; WorkingDir }.
# One CIM query; WorkingDir is $null for a legacy instance (no --working-dir). Callers wrap in @().
function Get-RoslynSentinelServerProcesses {
    Get-CimInstance Win32_Process -Filter "Name = 'RoslynSentinel.Server.Advanced.exe'" -ErrorAction SilentlyContinue |
        ForEach-Object {
            [pscustomobject]@{
                ProcessId      = $_.ProcessId
                ExecutablePath = $_.ExecutablePath
                CommandLine    = $_.CommandLine
                WorkingDir     = Get-WorkingDirFromCommandLine $_.CommandLine
            }
        }
}

# The newest builds\<sha8> that has a 'complete' marker (the "last good" build), or $null.
# Accepts 8-char short-sha folders and legacy 40-char folders.
function Get-NewestCompleteBuild {
    param([Parameter(Mandatory)][string]$BuildsRoot)

    if (-not (Test-Path -LiteralPath $BuildsRoot)) { return $null }
    Get-ChildItem -LiteralPath $BuildsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^[0-9a-f]{8,40}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'complete')) } |
        Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'complete')).LastWriteTime } -Descending |
        Select-Object -First 1
}

# Clones the dev repo into $SourceDir on first use, then fast-forwards it to origin/$Branch.
# - Clone goes to a sibling temp folder and is renamed into place, so two windows racing on a first
#   launch cannot corrupt each other (first rename wins, the loser deletes its own copy).
# - A failed pull (dev repo mid-rebase, no network equivalent, diverged history) is NOT fatal: the
#   current HEAD is kept and the failure is returned in PullError.
# Returns { Sha; PullOk; PullError; Cloned }. Throws only when no usable source clone exists afterwards.
# $Log is an optional scriptblock taking one message string.
function Sync-RoslynSentinelSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$DevRepo,
        [string]$Branch = 'master',
        [scriptblock]$Log
    )

    function Write-SyncLog([string]$Message) { if ($Log) { & $Log $Message } }

    $cloned = $false
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir '.git'))) {
        if (Test-Path -LiteralPath $SourceDir) {
            throw "Source folder '$SourceDir' exists but is not a git clone; refusing to clone over it."
        }
        $parent = Split-Path $SourceDir -Parent
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $tempClone = "$SourceDir.clone-$(New-RoslynSentinelToken)"
        Write-SyncLog "Source: cloning '$DevRepo' (branch $Branch) -> '$tempClone'."
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('clone', '--branch', $Branch, '--', $DevRepo, $tempClone)
        if ($r.ExitCode -ne 0) {
            Remove-Item -LiteralPath $tempClone -Recurse -Force -ErrorAction SilentlyContinue
            throw "git clone failed (exit $($r.ExitCode)): $($r.Output)"
        }
        try {
            Rename-Item -LiteralPath $tempClone -NewName (Split-Path $SourceDir -Leaf) -ErrorAction Stop
            $cloned = $true
            Write-SyncLog "Source: clone published at '$SourceDir'."
        }
        catch {
            if (Test-Path -LiteralPath (Join-Path $SourceDir '.git')) {
                Write-SyncLog "Source: another launch published the clone first; deleting own copy '$tempClone'."
                Remove-Item -LiteralPath $tempClone -Recurse -Force -ErrorAction SilentlyContinue
            }
            else { throw }
        }
    }

    $pullOk = $true
    $pullError = $null
    $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'rev-parse', '--abbrev-ref', 'HEAD')
    $currentBranch = if ($r.ExitCode -eq 0) { $r.Output.Trim() } else { '' }
    if ($currentBranch -ne $Branch) {
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'checkout', $Branch)
        if ($r.ExitCode -ne 0) {
            $pullOk = $false
            $pullError = "checkout of '$Branch' failed (current '$currentBranch'): $($r.Output)"
        }
    }
    if ($pullOk) {
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'pull', '--ff-only', 'origin', $Branch)
        if ($r.ExitCode -ne 0) {
            $pullOk = $false
            $pullError = "git pull --ff-only origin $Branch failed (exit $($r.ExitCode)): $($r.Output)"
        }
    }
    if (-not $pullOk) { Write-SyncLog "Source: PULL FAILED, continuing on current HEAD. $pullError" }

    $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'rev-parse', 'HEAD')
    $sha = $r.Output.Trim()
    if ($r.ExitCode -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') {
        throw "Could not resolve HEAD of '$SourceDir' (exit $($r.ExitCode)): $($r.Output)"
    }
    Write-SyncLog "Source: HEAD is $sha (pullOk=$pullOk, cloned=$cloned)."
    return [pscustomobject]@{ Sha = $sha; PullOk = $pullOk; PullError = $pullError; Cloned = $cloned }
}

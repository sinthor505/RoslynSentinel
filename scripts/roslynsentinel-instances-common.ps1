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
                              stopped-by-script.marker, launch.log, launch-outcome.json)
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

# Flattens a (possibly multi-line, non-ASCII) git error to one ASCII line of at most $MaxLength chars, for
# launch-outcome.json and the env hand-off from the shim. $null/empty in -> $null out.
function ConvertTo-PullErrorText {
    param([string]$Text, [int]$MaxLength = 300)

    if (-not $Text -or -not $Text.Trim()) { return $null }
    $flat = ($Text -replace '\s+', ' ').Trim()
    $flat = $flat -replace '[^\x20-\x7E]', '?'
    if ($flat.Length -gt $MaxLength) { $flat = $flat.Substring(0, $MaxLength) }
    return $flat
}

# Heals the launcher-owned source clone after an interrupted git operation: removes a STALE .git\index.lock and
# discards half-applied tracked-file changes. Returns { FreshLock; Healed; Actions }.
#   - A lock younger than $LockMaxAge may belong to another launch that is pulling right now: heal NOTHING
#     (FreshLock = $true).
#   - `git reset --hard HEAD` is safe here ONLY because <root>\source is owned by the launcher and never edited by
#     hand (it is a clone that is only ever fast-forwarded), so any tracked-file difference from HEAD is debris of an
#     interrupted pull, never someone's work. Untracked files are left alone. NEVER point this at the dev repo.
# Does not throw; each step is logged through $Log (scriptblock taking one string).
function Repair-RoslynSentinelSourceClone {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [TimeSpan]$LockMaxAge = [TimeSpan]::FromMinutes(10),
        [scriptblock]$Log
    )

    function Write-HealLog([string]$Message) { if ($Log) { & $Log $Message } }

    $actions = New-Object System.Collections.Generic.List[string]
    $result = { param($fresh) [pscustomobject]@{ FreshLock = $fresh; Healed = ($actions.Count -gt 0); Actions = @($actions) } }

    $gitDir = Join-Path $SourceDir '.git'
    if (-not (Test-Path -LiteralPath $gitDir -PathType Container)) {
        Write-HealLog "Source heal: '$SourceDir' has no .git folder; nothing to heal."
        return (& $result $false)
    }

    $lock = Join-Path $gitDir 'index.lock'
    $lockItem = Get-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
    if ($lockItem) {
        $age = [DateTime]::UtcNow - $lockItem.LastWriteTimeUtc
        if ($age -lt $LockMaxAge) {
            Write-HealLog "Source heal: index.lock is only $([int]$age.TotalSeconds)s old (< $([int]$LockMaxAge.TotalSeconds)s); another launch may be pulling, healing nothing."
            return (& $result $true)
        }
        try {
            Remove-Item -LiteralPath $lock -Force -ErrorAction Stop
            $actions.Add('removed stale index.lock')
            Write-HealLog "Source heal: removed stale index.lock ($([int]$age.TotalMinutes) min old)."
        }
        catch {
            Write-HealLog "Source heal: could not remove stale index.lock: $($_.Exception.Message)"
        }
    }

    $st = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'status', '--porcelain', '--untracked-files=no')
    if ($st.ExitCode -ne 0 -or $st.Output.Trim()) {
        Write-HealLog "Source heal: tracked files differ from HEAD (status exit $($st.ExitCode)); running git reset --hard HEAD."
        $rs = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'reset', '--hard', 'HEAD')
        if ($rs.ExitCode -eq 0) { $actions.Add('reset --hard HEAD') }
        else { Write-HealLog "Source heal: git reset --hard HEAD failed (exit $($rs.ExitCode)): $(ConvertTo-PullErrorText $rs.Output)" }
    }
    return (& $result $false)
}

# Clones the dev repo into $SourceDir on first use, then fast-forwards it to origin/$Branch.
# - Clone goes to a sibling temp folder and is renamed into place, so two windows racing on a first
#   launch cannot corrupt each other (first rename wins, the loser deletes its own copy).
# - A failed pull (dev repo mid-rebase, no network equivalent, diverged history) is NOT fatal: the
#   current HEAD is kept and the failure is returned in PullError. Before giving up, a failed pull triggers
#   Repair-RoslynSentinelSourceClone (stale index.lock / half-applied files) and one retry.
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

    # One checkout-if-needed + ff-only pull attempt. Returns { Ok; Error }. Nested so it sees $SourceDir/$Branch.
    function Invoke-SyncPullAttempt {
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'rev-parse', '--abbrev-ref', 'HEAD')
        $currentBranch = if ($r.ExitCode -eq 0) { $r.Output.Trim() } else { '' }
        if ($currentBranch -ne $Branch) {
            $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'checkout', $Branch)
            if ($r.ExitCode -ne 0) {
                return [pscustomobject]@{ Ok = $false; Error = "checkout of '$Branch' failed (current '$currentBranch'): $($r.Output)" }
            }
        }
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'pull', '--ff-only', 'origin', $Branch)
        if ($r.ExitCode -ne 0) {
            return [pscustomobject]@{ Ok = $false; Error = "git pull --ff-only origin $Branch failed (exit $($r.ExitCode)): $($r.Output)" }
        }
        return [pscustomobject]@{ Ok = $true; Error = $null }
    }

    $attempt = Invoke-SyncPullAttempt
    $pullOk = $attempt.Ok
    $pullError = $attempt.Error
    if (-not $pullOk) {
        # Self-heal (an interrupted pull leaves .git\index.lock and half-applied tracked files in this clone,
        # and every later pull then fails the same way), then retry once. Only retried when the heal actually
        # changed something: a plain failure (diverged history, no network) would just fail again.
        Write-SyncLog "Source: pull failed, checking the clone for an interrupted-pull leftover. $pullError"
        $heal = Repair-RoslynSentinelSourceClone -SourceDir $SourceDir -Log $Log
        if ($heal.Healed) {
            $attempt = Invoke-SyncPullAttempt
            $pullOk = $attempt.Ok
            $pullError = $attempt.Error
            if ($pullOk) { Write-SyncLog 'Source: pull succeeded after healing the clone.' }
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

# Finds the newest complete build that can serve HEAD without a rebuild: its sha is an ancestor of HeadSha and
# no commit between the two touches a server-source path (one repo-relative prefix per line in PrefixFile,
# matched with ordinal StartsWith against git's forward-slash paths). Looks at the MaxCandidates newest complete
# builds. Returns [pscustomobject]@{ Directory; BuildSha; ChangedPathCount } or $null.
# Never throws and writes nothing to stdout (the stdio transport owns it); $Log takes one message string.
function Find-ReusableBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BuildsRoot,
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$HeadSha,
        [Parameter(Mandatory)][string]$PrefixFile,
        [scriptblock]$Log,
        [int]$MaxCandidates = 5
    )

    function Write-ReuseLog([string]$Message) {
        if ($Log) { try { & $Log $Message } catch { } }
    }

    try {
        if (-not (Test-Path -LiteralPath $PrefixFile -PathType Leaf)) {
            Write-ReuseLog "Reuse: no server-source rule file at '$PrefixFile'; not reusing a build."
            return $null
        }
        $prefixes = @(Get-Content -LiteralPath $PrefixFile |
            ForEach-Object { "$_".Trim() } |
            Where-Object { $_ -ne '' -and -not $_.StartsWith('#') })
        if ($prefixes.Count -eq 0) {
            Write-ReuseLog "Reuse: '$PrefixFile' lists no server-source prefixes; not reusing a build."
            return $null
        }
        if (-not (Test-Path -LiteralPath $BuildsRoot)) { return $null }

        $candidates = @(Get-ChildItem -LiteralPath $BuildsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^[0-9a-f]{8,40}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'complete')) } |
            Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'complete')).LastWriteTime } -Descending |
            Select-Object -First $MaxCandidates)

        foreach ($dir in $candidates) {
            # The marker's first line is "sha=<full sha> builtBy=..."; fall back to the folder name.
            $candSha = $dir.Name
            $firstLine = @(Get-Content -LiteralPath (Join-Path $dir.FullName 'complete') -TotalCount 1)
            if ($firstLine.Count -gt 0) {
                $m = [regex]::Match("$($firstLine[0])", 'sha=([0-9a-fA-F]{8,40})')
                if ($m.Success) { $candSha = $m.Groups[1].Value.ToLowerInvariant() }
            }
            if ($HeadSha.StartsWith($candSha, [StringComparison]::OrdinalIgnoreCase)) { continue }

            $anc = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'merge-base', '--is-ancestor', $candSha, $HeadSha)
            if ($anc.ExitCode -ne 0) {
                Write-ReuseLog "Reuse: builds\$($dir.Name) (sha $candSha) is not an ancestor of HEAD; skipping it."
                continue
            }

            $diff = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $SourceDir, 'diff', '--name-only', $candSha, $HeadSha)
            if ($diff.ExitCode -ne 0) {
                Write-ReuseLog "Reuse: git diff for builds\$($dir.Name) failed (exit $($diff.ExitCode)); skipping it."
                continue
            }
            $paths = @($diff.Output -split '\r?\n' | Where-Object { $_ -ne '' })

            $stale = $null
            foreach ($p in $paths) {
                foreach ($prefix in $prefixes) {
                    if ($p.StartsWith($prefix, [StringComparison]::Ordinal)) { $stale = $p; break }
                }
                if ($stale) { break }
            }
            if ($stale) {
                Write-ReuseLog "Reuse: builds\$($dir.Name) is stale; '$stale' changed since $candSha."
                continue
            }

            return [pscustomobject]@{ Directory = $dir; BuildSha = $candSha; ChangedPathCount = $paths.Count }
        }
        return $null
    }
    catch {
        Write-ReuseLog "Reuse: could not check existing builds ($($_.Exception.Message)); building instead."
        return $null
    }
}

# Writes builds\latest.json (headSha, buildSha8, selection, buildFailed, utc) atomically: a temp file in the same
# folder, then Move-Item -Force over latest.json. Selection is one of fast, reuse, built, fallback. Never throws;
# a failure is logged through $Log. Written as ASCII (valid UTF-8 without BOM).
function Write-LatestBuildRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BuildsRoot,
        [string]$HeadSha,
        [Parameter(Mandatory)][string]$BuildDir,
        [Parameter(Mandatory)][string]$Selection,
        [bool]$BuildFailed,
        [scriptblock]$Log
    )

    function Write-LatestLog([string]$Message) {
        if ($Log) { try { & $Log $Message } catch { } }
    }

    $tmp = $null
    try {
        $record = [pscustomobject]@{
            headSha     = $HeadSha
            buildSha8   = (Split-Path -Leaf $BuildDir)
            selection   = $Selection
            buildFailed = $BuildFailed
            utc         = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
        $json = ConvertTo-Json -InputObject $record
        New-Item -ItemType Directory -Path $BuildsRoot -Force | Out-Null
        $latest = Join-Path $BuildsRoot 'latest.json'
        $tmp = Join-Path $BuildsRoot ("latest.json.{0}.tmp" -f (New-RoslynSentinelToken))
        [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tmp -Destination $latest -Force
        $tmp = $null
    }
    catch {
        Write-LatestLog "Latest build record not written: $($_.Exception.Message)"
    }
    finally {
        if ($tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

<#
.SYNOPSIS
    Build and test a commit of this repo in a separate git worktree, so a test run never shares
    bin/ or obj/ with the main working tree (where implementers build and RunTest runs).

.DESCRIPTION
    Stopgap for the TODO.md TOOL-GAP "RunTest/Build cannot target a git worktree". Typical use: a
    background agent tests the last committed slice while implementation continues on the next one.

    1. Ensures -RepoPath is a git worktree of THIS repo (creates it detached at -Ref if missing;
       refuses the main working tree, which would defeat the point).
    2. With -Ref, moves the worktree to that commit (detached; refuses a dirty worktree).
    3. Builds RoslynSentinel.slnx once (no MSBuild node reuse, no build servers, so it cannot hold
       locks that the main tree's builds would trip over).
    4. Runs `dotnet test --no-build` per test project, sequentially by default, one TRX each.
    5. Prints a per-project summary plus each failed test's name and first message line, writes
       summary.json beside the TRX files, and exits 0 (all passed), 1 (test failures) or 2 (build
       failed or setup error).

    Does not stop or touch any running RoslynSentinel server (unlike build.ps1).

.PARAMETER RepoPath
    Absolute path of a reusable worktree, e.g. c:\tmp\rs-testtree. Keep it outside the repo so
    searches and diffs of the main tree never see it. Two runs on the same -RepoPath must not
    overlap; use -WorktreeRoot for concurrent runs. Exactly one of -RepoPath / -WorktreeRoot.

.PARAMETER WorktreeRoot
    Absolute parent folder for a fresh, unique worktree per run (e.g. c:\tmp\rs-testtrees), named
    <commit8>-<yyyyMMdd-HHmmss>-<pid>, so several runs (one per committed slice) never block each
    other on a folder. Results go to <WorktreeRoot>\_results\<name>\ (summary.json, TRX, logs) and
    the worktree is removed afterwards unless -KeepWorktree. At most -MaxConcurrent runs build and
    test at once (machine-wide slots); later runs wait for a slot, which keeps CPU contention from
    turning load-sensitive tests into timeouts.

.PARAMETER MaxConcurrent
    With -WorktreeRoot: number of runs allowed to build/test at the same time. Default 2.

.PARAMETER KeepWorktree
    With -WorktreeRoot: keep the worktree after the run (for debugging a failure in place).

.PARAMETER Ref
    Commit, branch or tag to test (checked out detached). Omit to test the worktree as it is.
    Default when the worktree has to be created: HEAD of the main repo.

.PARAMETER Projects
    Test project names (e.g. RoslynSentinel.Tests.Basic). Default: every RoslynSentinel.Tests*
    project except Support libraries, ModelEval (needs a live LLM), PlanStepRunner and SubAgent.

.PARAMETER Filter
    Passed to `dotnet test --filter` for every project. A run with neither -Filter nor -Projects
    that completes for every project resets the main tree's .roslynsentinel/full-test-marker.json
    (the RunTest full-suite staleness marker).

.PARAMETER Parallel
    Run the projects concurrently. Faster, but CPU contention makes the build-timeout tests
    (BuildCascadeFixtureTests.FullBuild_* and similar) time out more often.

.PARAMETER HangTimeoutMinutes
    Optional per-project `--blame-hang-timeout`; a hung test is killed and reported.

.PARAMETER SkipBuild
    Reuse the worktree's existing build (only valid when -Ref did not move it).

.EXAMPLE
    pwsh -File scripts/Invoke-WorktreeTests.ps1 -RepoPath c:\tmp\rs-testtree -Ref HEAD -Projects RoslynSentinel.Tests.Basic,RoslynSentinel.Tests.Tools.Basic

.EXAMPLE
    pwsh -File scripts/Invoke-WorktreeTests.ps1 -RepoPath c:\tmp\rs-testtree -Ref 3e4ed6fa -Filter "FullyQualifiedName~FoldPending"

.EXAMPLE
    pwsh -File scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Ref 1b7879d7 -Projects RoslynSentinel.Tests.Tools.Basic -Filter "FullyQualifiedName~GitCommitSeamParityTests"
#>
[CmdletBinding()]
param(
    [string]$RepoPath,

    [string]$WorktreeRoot,

    [ValidateRange(1, 8)]
    [int]$MaxConcurrent = 2,

    [switch]$KeepWorktree,

    [string]$Ref,

    [string[]]$Projects,

    [string]$Filter,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Parallel,

    [int]$HangTimeoutMinutes = 0,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$mainRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\')
$excludedByDefault = @('ModelEval', 'PlanStepRunner', 'SubAgent')

$isolated = $false
$slot = $null

# Every exit after setup goes through here: release the concurrency slot and, for a -WorktreeRoot
# run, remove the per-run worktree (results live outside it).
function Finish([int]$code) {
    if ($script:slot) { try { $script:slot.ReleaseMutex() } catch { } ; $script:slot.Dispose(); $script:slot = $null }
    if ($script:isolated -and -not $KeepWorktree -and (Test-Path $script:RepoPath)) {
        & git -C $script:mainRoot worktree remove --force $script:RepoPath 2>&1 | Out-Null
        if (Test-Path $script:RepoPath) { Remove-Item $script:RepoPath -Recurse -Force -ErrorAction SilentlyContinue }
        & git -C $script:mainRoot worktree prune 2>&1 | Out-Null
    }
    exit $code
}

function Fail([string]$message) {
    Write-Host "ERROR: $message"
    Finish 2
}

if ([bool]$RepoPath -eq [bool]$WorktreeRoot) { Fail "pass exactly one of -RepoPath (reusable worktree) or -WorktreeRoot (fresh worktree per run)." }
if ($WorktreeRoot) {
    if (-not [System.IO.Path]::IsPathRooted($WorktreeRoot)) { Fail "-WorktreeRoot must be absolute: $WorktreeRoot" }
    $WorktreeRoot = [System.IO.Path]::GetFullPath($WorktreeRoot).TrimEnd('\')
    $refToTest = if ($Ref) { $Ref } else { 'HEAD' }
    $commit = (& git -C $mainRoot rev-parse --verify "$refToTest^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $commit) { Fail "cannot resolve -Ref '$refToTest' in $mainRoot" }
    $Ref = $commit
    $runName = '{0}-{1:yyyyMMdd-HHmmss}-{2}' -f $commit.Substring(0, 8), (Get-Date), $PID
    New-Item -ItemType Directory -Path $WorktreeRoot -Force | Out-Null
    $RepoPath = Join-Path $WorktreeRoot $runName
    $isolated = $true
}

function Get-CommonDir([string]$path) {
    $dir = (& git -C $path rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $dir) { return $null }
    return ((Resolve-Path $dir).Path.TrimEnd('\'))
}

# --- 1. Worktree ---------------------------------------------------------------------------
if (-not [System.IO.Path]::IsPathRooted($RepoPath)) { Fail "-RepoPath must be absolute: $RepoPath" }
$RepoPath = [System.IO.Path]::GetFullPath($RepoPath).TrimEnd('\')
if ($RepoPath -ieq $mainRoot) { Fail "-RepoPath is the main working tree; use a separate worktree (e.g. c:\tmp\rs-testtree)." }

$mainCommon = Get-CommonDir $mainRoot
if (-not $mainCommon) { Fail "cannot read the git common dir of $mainRoot" }

if (-not (Test-Path $RepoPath)) {
    $createAt = if ($Ref) { $Ref } else { 'HEAD' }
    Write-Host "Creating worktree $RepoPath at $createAt (detached)..."
    & git -C $mainRoot worktree add --quiet --detach $RepoPath $createAt 2>&1 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { Fail "git worktree add failed" }
    $SkipBuild = $false
} else {
    $wtCommon = Get-CommonDir $RepoPath
    if (-not $wtCommon) { Fail "$RepoPath exists but is not a git worktree" }
    if ($wtCommon -ine $mainCommon) { Fail "$RepoPath is a worktree of a different repository ($wtCommon)" }

    # --- 2. Move to -Ref ---------------------------------------------------------------------
    if ($Ref) {
        $dirty = & git -C $RepoPath status --porcelain --untracked-files=no
        if ($dirty) { Fail "worktree $RepoPath has uncommitted changes; refusing to move it:`n$($dirty -join "`n")" }
        $target = (& git -C $mainRoot rev-parse --verify "$Ref^{commit}" 2>$null)
        if ($LASTEXITCODE -ne 0) { Fail "cannot resolve -Ref '$Ref' in $mainRoot" }
        $current = (& git -C $RepoPath rev-parse HEAD)
        if ($current -ne $target) {
            & git -C $RepoPath checkout --detach --quiet $target 2>&1 | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { Fail "checkout of $target in $RepoPath failed" }
            $SkipBuild = $false
        }
    }
}

# Some agent definitions under .claude/agents are git-ignored (local config), yet Tests.Server reads
# them (AgentFile_* tests). Mirror the main tree's copies so a worktree run matches a main-tree run.
$agentsSrc = Join-Path $mainRoot '.claude\agents'
if (Test-Path $agentsSrc) {
    $agentsDst = Join-Path $RepoPath '.claude\agents'
    New-Item -ItemType Directory -Path $agentsDst -Force | Out-Null
    # Only the untracked ones: overwriting a tracked file would test the main tree's version and
    # leave the worktree dirty (the next -Ref move refuses a dirty worktree).
    $tracked = @(& git -C $RepoPath ls-files .claude/agents | ForEach-Object { Split-Path $_ -Leaf })
    Get-ChildItem -Path $agentsSrc -Filter '*.md' |
        Where-Object { $tracked -notcontains $_.Name } |
        ForEach-Object { Copy-Item -Path $_.FullName -Destination $agentsDst -Force }
}

$head = (& git -C $RepoPath rev-parse HEAD)
$subject = (& git -C $RepoPath log -1 --format=%s HEAD)
Write-Host "Testing $head  $subject"
Write-Host "Worktree: $RepoPath"

# --- Projects --------------------------------------------------------------------------------
$all = Get-ChildItem -Path $RepoPath -Directory -Filter 'RoslynSentinel.Tests*' |
    ForEach-Object { Join-Path $_.FullName ($_.Name + '.csproj') } |
    Where-Object { Test-Path $_ } |
    Where-Object { (Split-Path $_ -LeafBase) -notmatch '^RoslynSentinel\.Tests\.Support\.' }

if ($Projects) {
    # `pwsh -File` passes "A,B,C" as one string, not an array; accept both forms.
    $Projects = $Projects | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $selected = foreach ($name in $Projects) {
        $match = $all | Where-Object { (Split-Path $_ -LeafBase) -ieq $name }
        if (-not $match) { Fail "unknown test project '$name'. Known: $((($all | ForEach-Object { Split-Path $_ -LeafBase }) -join ', '))" }
        $match
    }
} else {
    # Default selection only takes projects listed in RoslynSentinel.slnx: a detached project
    # (e.g. Tests.Integration) is never built by the solution build, so it would report "no TRX".
    $slnxText = Get-Content (Join-Path $RepoPath 'RoslynSentinel.slnx') -Raw
    $selected = $all | Where-Object {
        $leaf = Split-Path $_ -LeafBase
        (-not ($excludedByDefault | Where-Object { $leaf -like "*.$_" })) -and
            $slnxText.Contains("$leaf.csproj")
    }
}

$resultsDir = if ($isolated) { Join-Path $WorktreeRoot "_results\$runName" } else { Join-Path $RepoPath 'TestResults\worktree-run' }
if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
New-Item -ItemType Directory -Path $resultsDir | Out-Null

# --- Concurrency slot (-WorktreeRoot runs) ------------------------------------------------------
# Named mutexes, one per slot: the OS releases a slot when its process dies (abandoned mutex), so a
# killed run never leaks one (a named semaphore would).
if ($isolated) {
    $waitStart = Get-Date
    $announced = $false
    while (-not $slot) {
        for ($i = 0; $i -lt $MaxConcurrent -and -not $slot; $i++) {
            $m = [System.Threading.Mutex]::new($false, "Global\RoslynSentinelWorktreeTests-slot$i")
            $got = $false
            try { $got = $m.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $got = $true }
            if ($got) { $slot = $m } else { $m.Dispose() }
        }
        if (-not $slot) {
            if (-not $announced) { Write-Host "Waiting for a test slot ($MaxConcurrent in use)..."; $announced = $true }
            Start-Sleep -Seconds 5
        }
    }
    if ($announced) { Write-Host ("Got a slot after {0:n0}s" -f ((Get-Date) - $waitStart).TotalSeconds) }
}

# Keep this run's MSBuild/compiler processes private to it, so they cannot lock files that the
# main tree's builds use, and do not outlive the run.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$sw = [System.Diagnostics.Stopwatch]::StartNew()

# --- 3. Build --------------------------------------------------------------------------------
if (-not $SkipBuild) {
    $buildLog = Join-Path $resultsDir 'build.log'
    Write-Host "Building RoslynSentinel.slnx ($Configuration)..."
    & dotnet build (Join-Path $RepoPath 'RoslynSentinel.slnx') -c $Configuration --nologo -nodeReuse:false --disable-build-servers *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED - see $buildLog"
        Get-Content $buildLog | Select-String -Pattern ': error ' | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
        Finish 2
    }
    Write-Host ("Build OK in {0:n0}s" -f $sw.Elapsed.TotalSeconds)
}

# --- 4. Test ---------------------------------------------------------------------------------
function Get-TestArgs([string]$project) {
    $leaf = Split-Path $project -LeafBase
    $testArgs = @('test', $project, '-c', $Configuration, '--no-build', '--nologo',
        '--results-directory', $resultsDir, '--logger', "trx;LogFileName=$leaf.trx")
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    if ($HangTimeoutMinutes -gt 0) { $testArgs += @('--blame-hang', '--blame-hang-timeout', "$($HangTimeoutMinutes)m") }
    return $testArgs
}

if ($Parallel) {
    $jobs = foreach ($p in $selected) {
        $leaf = Split-Path $p -LeafBase
        $log = Join-Path $resultsDir "$leaf.log"
        Start-Process -FilePath 'dotnet' -ArgumentList (Get-TestArgs $p) -RedirectStandardOutput $log `
            -RedirectStandardError "$log.err" -NoNewWindow -PassThru
    }
    $jobs | Wait-Process
} else {
    foreach ($p in $selected) {
        $leaf = Split-Path $p -LeafBase
        $log = Join-Path $resultsDir "$leaf.log"
        Write-Host "Testing $leaf..."
        & dotnet @(Get-TestArgs $p) *> $log
    }
}

# --- 5. Report -------------------------------------------------------------------------------
$summary = [ordered]@{
    commit   = $head
    subject  = $subject
    worktree = $RepoPath
    filter   = $Filter
    seconds  = [int]$sw.Elapsed.TotalSeconds
    projects = @()
}
$anyFailed = $false

foreach ($p in $selected) {
    $leaf = Split-Path $p -LeafBase
    $trx = Join-Path $resultsDir "$leaf.trx"
    $row = [ordered]@{ project = $leaf; total = 0; passed = 0; failed = 0; skipped = 0; failures = @(); skippedTests = @(); note = $null }
    if (-not (Test-Path $trx)) {
        $row.note = "no TRX (crash, hang or no tests matched) - see $leaf.log"
        $anyFailed = $true
    } else {
        [xml]$x = Get-Content $trx -Raw
        $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
        $c = (Select-Xml -Xml $x -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
        $row.total = [int]$c.total
        $row.passed = [int]$c.passed
        $row.failed = [int]$c.failed + [int]$c.error + [int]$c.timeout + [int]$c.aborted
        # The Counters element leaves notExecuted at 0 for NUnit Ignore/Assume skips (each result still says
        # outcome="NotExecuted"), so skips are counted from the results, with the first line of the reason.
        foreach ($r in (Select-Xml -Xml $x -XPath "//t:UnitTestResult[@outcome!='Passed' and @outcome!='Failed' and @outcome!='Error' and @outcome!='Timeout' and @outcome!='Aborted']" -Namespace $ns)) {
            $why = $r.Node.Output.ErrorInfo.Message
            if (-not $why) { $why = $r.Node.Output.StdOut }
            $first = if ($why) { ($why -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim() } else { '' }
            $row.skippedTests += [ordered]@{ test = $r.Node.testName; outcome = $r.Node.outcome; reason = $first }
        }
        $row.skipped = $row.skippedTests.Count
        foreach ($r in (Select-Xml -Xml $x -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns)) {
            $msg = $r.Node.Output.ErrorInfo.Message
            $first = if ($msg) { ($msg -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim() } else { '' }
            $row.failures += [ordered]@{ test = $r.Node.testName; message = $first }
        }
        if ($row.failed -gt 0) { $anyFailed = $true }
    }
    $summary.projects += $row
}

$summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $resultsDir 'summary.json') -Encoding utf8

# An unfiltered default-selection run that completed for every project counts as a full suite run:
# reset the main tree's RunTest staleness marker (same shape as FullTestMarker in
# RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs), so batch runs here silence the
# fullSuiteStale warning just like RunTest(suite: "full") does.
$allCompleted = -not ($summary.projects | Where-Object { $_.note })
if (-not $Filter -and -not $Projects -and $allCompleted) {
    $markerDir = Join-Path $mainRoot '.roslynsentinel'
    New-Item -ItemType Directory -Path $markerDir -Force | Out-Null
    $marker = [ordered]@{
        lastFullRunUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        totalCount     = [int](($summary.projects | ForEach-Object { $_.total } | Measure-Object -Sum).Sum)
        failedCount    = [int](($summary.projects | ForEach-Object { $_.failed } | Measure-Object -Sum).Sum)
        fastRunsSince  = 0
    }
    $markerPath = Join-Path $markerDir 'full-test-marker.json'
    $tmp = "$markerPath.$PID.tmp"
    $marker | ConvertTo-Json | Set-Content -Path $tmp -Encoding utf8
    Move-Item -Path $tmp -Destination $markerPath -Force
    Write-Host "Full-suite marker updated: $markerPath"
}

Write-Host ''
Write-Host ("{0,-40} {1,7} {2,7} {3,7} {4,7}" -f 'Project', 'Total', 'Passed', 'Failed', 'Skipped')
foreach ($row in $summary.projects) {
    Write-Host ("{0,-40} {1,7} {2,7} {3,7} {4,7}  {5}" -f $row.project, $row.total, $row.passed, $row.failed, $row.skipped, $row.note)
}
foreach ($row in $summary.projects | Where-Object { $_.failures.Count -gt 0 }) {
    Write-Host ''
    Write-Host "Failed in $($row.project):"
    foreach ($f in $row.failures) { Write-Host "  $($f.test)`n      $($f.message)" }
}
Write-Host ''
Write-Host ("Commit {0} - {1} in {2:n0}s. Results: {3}" -f $head.Substring(0, 8), $(if ($anyFailed) { 'FAILURES' } else { 'all passed' }), $sw.Elapsed.TotalSeconds, $resultsDir)
Finish $(if ($anyFailed) { 1 } else { 0 })

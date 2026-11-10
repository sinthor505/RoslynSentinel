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
    Absolute path of the worktree, e.g. c:\tmp\rs-testtree. Keep it outside the repo so searches
    and diffs of the main tree never see it.

.PARAMETER Ref
    Commit, branch or tag to test (checked out detached). Omit to test the worktree as it is.
    Default when the worktree has to be created: HEAD of the main repo.

.PARAMETER Projects
    Test project names (e.g. RoslynSentinel.Tests.Basic). Default: every RoslynSentinel.Tests*
    project except Support libraries, ModelEval (needs a live LLM), PlanStepRunner and SubAgent.

.PARAMETER Filter
    Passed to `dotnet test --filter` for every project.

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
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepoPath,

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

function Fail([string]$message) {
    Write-Host "ERROR: $message"
    exit 2
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
    $selected = foreach ($name in $Projects) {
        $match = $all | Where-Object { (Split-Path $_ -LeafBase) -ieq $name }
        if (-not $match) { Fail "unknown test project '$name'. Known: $((($all | ForEach-Object { Split-Path $_ -LeafBase }) -join ', '))" }
        $match
    }
} else {
    $selected = $all | Where-Object {
        $leaf = Split-Path $_ -LeafBase
        -not ($excludedByDefault | Where-Object { $leaf -like "*.$_" })
    }
}

$resultsDir = Join-Path $RepoPath 'TestResults\worktree-run'
if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
New-Item -ItemType Directory -Path $resultsDir | Out-Null

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
        exit 2
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
    $row = [ordered]@{ project = $leaf; total = 0; passed = 0; failed = 0; skipped = 0; failures = @(); note = $null }
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
        $row.skipped = [int]$c.notExecuted
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
exit $(if ($anyFailed) { 1 } else { 0 })

<#
.SYNOPSIS
    Build & run a RoslynSentinel.Tests.ModelEval real-LM-Studio test against a chosen host,
    without needing to remember the env vars / filter syntax / --artifacts-path incantation.

.DESCRIPTION
    Front door for the two model-eval scenarios exercised most often:

      SizeThreshold    - Model_SizeThresholdSweep ([Explicit]). Sweeps unrelated-method
                          counts x repeats against SizeGraduatedReproducer fixture variants.
                          -Size sets ROSLYNSENTINEL_MODELEVAL_SIZES (single value, e.g. 60).
      MinimalGuidance  - Model_FixesWholeFileRewriteBug_MinimalGuidance (runs by default).
                          Level-3 symptom-only prompt for the whole-file-rewrite bug fix.
      MinimalGuidanceDisambiguated - Same fixture/assertions as MinimalGuidance, but the
                          prompt adds one sentence closing the "reuse that same approach"
                          ambiguity a 2026-08-31 reasoning-level analysis found was the
                          dominant fork between pass (34%) and fail on the plain prompt —
                          see project_minimalguidance_reasoning_pattern_analysis memory.
      PlanOnly          - Model_PlansWholeFileRewriteFix_PrefersCallingHelper. Same
                          disambiguated task, but ApplyDiff/ChangeAccessibility/ModifyModifier/
                          CreateFile/DeleteFile are blocked by a filter — the model can only
                          investigate and state a plan, never edit. Cheaper/faster than the
                          execute-and-verify tests; checks whether the "private" reasoning fork
                          already exists at planning time before any tool-call commitment.
                          Note: calling/exposing the helper is now the CORRECT plan (flipped
                          scoring, see WholeFileRewriteAgentTests.AssertFixApplied).
      PlanThenExecute   - Model_FixesWholeFileRewriteBug_PlanThenExecute. Same disambiguated
                          task and full toolset as MinimalGuidanceDisambiguated, but the prompt
                          also asks the model to state its complete plan in prose before making
                          any edit tool call (not server-enforced - see PlanThenExecuteAgentTests
                          doc comment). Tests whether a plan committed up front changes the
                          execution-time fork rate versus deciding turn-by-turn.
      ScriptedPlan       - Model_FixesWholeFileRewriteBug_ScriptedPlan. Same fixture/assertions
                          as MinimalGuidanceDisambiguated, but the prompt hands the model an
                          exact, already-correct 3-step plan (lifted from a real model's own
                          successful PlanThenExecute run) instead of asking it to find the bug
                          and derive one. Isolates execution fidelity from planning/bug-location:
                          a much higher pass rate here than MinimalGuidanceDisambiguated's points
                          at planning as the bottleneck, not mechanical tool use.
      PlanImplementVerify - Model_FixesWholeFileRewriteBug_PlanImplementVerify. Three separate
                          model calls, each with its own fresh context: a read-only plan phase
                          (same prompt shape as PlanOnly), a full-tool-access implement phase fed
                          THAT MODEL'S OWN plan text (not a hand-picked one, unlike ScriptedPlan),
                          and a read-only verify phase that independently judges the on-disk
                          result. Pass requires both AssertFixApplied AND the verify phase's own
                          "VERIFIED: PASS" verdict. Each phase capped at 5 minutes wall-clock;
                          not supported against .112 (too slow for a 3-call test).

    Each host gets its own --artifacts-path (RoslynSentinel\_scratchbuild_<host-suffix>) so
    that two hosts can be launched concurrently without racing on shared project references'
    obj/ output - see reference_model_eval_procedure memory / CS2012 "locked by VBCSCompiler"
    for the failure this avoids. Test/transcript output lands under
    _scratchbuild_<host-suffix>\bin\RoslynSentinel.Tests.ModelEval\debug\model-eval\...

.PARAMETER HostAddress
    LM Studio host to target. Known aliases: 112 (http://192.168.1.112:1234/v1, GTX 1080) and
    113 (http://192.168.1.113:1234/v1, RTX 4060). Any other value is used verbatim as the full
    base URL (e.g. http://localhost:1234/v1), and its --artifacts-path suffix is derived from
    a sanitized version of that URL.

.PARAMETER Test
    SizeThreshold | MinimalGuidance. Required.

.PARAMETER Size
    SizeThreshold only: single value for ROSLYNSENTINEL_MODELEVAL_SIZES (default: 60).
    Ignored for MinimalGuidance.

.PARAMETER Model
    ROSLYNSENTINEL_LLM_MODEL. Default: qwen3.5-9b-coder (confirmed present on both .112/.113
    as of 2026-08-30 - re-confirm if it's been a while, since /v1/models lists every
    downloaded model, not which one is actually loaded).

.PARAMETER Clean
    Archive this host's _scratchbuild_<suffix>\bin\...\model-eval directory (transcripts,
    agent.log, results.csv - everything) to ModelTestingResults\<host-suffix>\<timestamp>\
    under the repo root, then delete the original, before running - so stale run-history from
    a previous session isn't mixed in with this run's data, but nothing is lost. Skipped
    silently if there's no existing model-eval directory to archive.

.PARAMETER Repeats
    Run the test this many times in sequence (default: 1). Between iterations, waits for
    this scratch path's own testhost.exe to fully exit before starting the next build — a
    bare `for` loop calling `dotnet test --artifacts-path <dir>` repeatedly WILL race the
    next build's DLL copy against the previous run's still-exiting testhost.exe and fail
    with MSB3027 "locked by testhost" (seen in practice 2026-08-31). Use -Repeats instead of
    a caller-side loop so this wait always happens. After each individual run (pass or fail),
    its own run directory is copied to ModelTestingResults\<host-suffix>\<TestName>\ right
    away - this happens unconditionally, independent of -Clean, so results are available in
    the common folder as soon as each run finishes rather than only on the next -Clean.

.EXAMPLE
    .\roslynsentinel-modeleval.ps1 -HostAddress 112 -Test SizeThreshold -Size 60
    Sweep size=60 against the .112 GTX 1080 host.

.EXAMPLE
    .\roslynsentinel-modeleval.ps1 -HostAddress 113 -Test MinimalGuidance
    Run the symptom-only whole-file-rewrite fix prompt against the .113 RTX 4060 host.

.EXAMPLE
    .\roslynsentinel-modeleval.ps1 112 SizeThreshold 60
    .\roslynsentinel-modeleval.ps1 113 MinimalGuidance
    Same two runs, positional args - launch both in separate terminals/background jobs to run
    concurrently.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [string]$HostAddress,

    [Parameter(Position = 1, Mandatory)]
    [ValidateSet('SizeThreshold', 'MinimalGuidance', 'MinimalGuidanceDisambiguated', 'PlanOnly', 'PlanThenExecute', 'ScriptedPlan', 'PlanImplementVerify')]
    [string]$Test,

    [Parameter(Position = 2)]
    [int]$Size = 60,

    [string]$Model = 'qwen3.5-9b-coder',

    [switch]$Clean,

    [int]$Repeats = 1
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

$knownHosts = @{
    '112' = @{ BaseUrl = 'http://192.168.1.112:1234/v1'; Suffix = '112' }
    '113' = @{ BaseUrl = 'http://192.168.1.113:1234/v1'; Suffix = '113' }
}

if ($knownHosts.ContainsKey($HostAddress)) {
    $baseUrl = $knownHosts[$HostAddress].BaseUrl
    $suffix = $knownHosts[$HostAddress].Suffix
}
else {
    $baseUrl = $HostAddress
    $suffix = ($HostAddress -replace '[^a-zA-Z0-9]', '_').Trim('_')
    if (-not $suffix) {
        throw "Could not derive an --artifacts-path suffix from -HostAddress '$HostAddress'. Pass a known alias (112, 113) or a URL like http://host:1234/v1."
    }
    Write-Warning "'$HostAddress' is not a known host alias (112, 113) - using it verbatim as the base URL and deriving artifacts suffix '$suffix'."
}

$testNames = @{
    'SizeThreshold'                 = 'Model_SizeThresholdSweep'
    'MinimalGuidance'               = 'Model_FixesWholeFileRewriteBug_MinimalGuidance'
    'MinimalGuidanceDisambiguated'  = 'Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated'
    'PlanOnly'                      = 'Model_PlansWholeFileRewriteFix_PrefersCallingHelper'
    'PlanThenExecute'               = 'Model_FixesWholeFileRewriteBug_PlanThenExecute'
    'ScriptedPlan'                   = 'Model_FixesWholeFileRewriteBug_ScriptedPlan'
    'PlanImplementVerify'           = 'Model_FixesWholeFileRewriteBug_PlanImplementVerify'
}
$testName = $testNames[$Test]

$artifactsPath = Join-Path $repoRoot "_scratchbuild_$suffix"

if ($Clean) {
    $modelEvalDir = Join-Path $artifactsPath 'bin\RoslynSentinel.Tests.ModelEval\debug\model-eval'
    if (Test-Path $modelEvalDir) {
        $archiveStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $archiveDir = Join-Path $repoRoot "ModelTestingResults\$suffix\$archiveStamp"
        Write-Host "Archiving stale run history to $archiveDir before removing $modelEvalDir" -ForegroundColor Yellow
        New-Item -ItemType Directory -Force -Path (Split-Path $archiveDir -Parent) | Out-Null
        Move-Item -Path $modelEvalDir -Destination $archiveDir
    }
}

Write-Host ""
Write-Host "=== ModelEval: $testName against $baseUrl (model=$Model) ===" -ForegroundColor Cyan
if ($Test -eq 'SizeThreshold') {
    Write-Host "    ROSLYNSENTINEL_MODELEVAL_SIZES=$Size" -ForegroundColor Cyan
}
Write-Host "    --artifacts-path $artifactsPath" -ForegroundColor Cyan
Write-Host ""

$env:ROSLYNSENTINEL_LLM_BASE_URL = $baseUrl
$env:ROSLYNSENTINEL_LLM_MODEL = $Model
if ($Test -eq 'SizeThreshold') {
    $env:ROSLYNSENTINEL_MODELEVAL_SIZES = "$Size"
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_SIZES -ErrorAction SilentlyContinue
}

$csproj = Join-Path $repoRoot 'RoslynSentinel.Tests.ModelEval\RoslynSentinel.Tests.ModelEval.csproj'
$testhostPath = Join-Path $artifactsPath 'bin\RoslynSentinel.Tests.ModelEval\debug\testhost.exe'

function Wait-ForTesthostExit {
    # dotnet test's own process can return before the testhost.exe it spawned has fully
    # released its file locks on this scratch path's DLLs - the next iteration's build
    # then races that teardown and fails with MSB3027 "locked by testhost". Poll for any
    # testhost.exe whose path is under this run's own --artifacts-path (never touch a
    # testhost.exe belonging to a different host's scratch build or the plain bin/ output).
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $stillLocked = Get-CimInstance Win32_Process -Filter "Name='testhost.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -eq $testhostPath }
        if (-not $stillLocked) { return }
        Start-Sleep -Milliseconds 500
    }
    Write-Warning "testhost.exe under $artifactsPath did not exit within 60s of the test run finishing - the next iteration's build may hit MSB3027."
}

$testDir = Join-Path $artifactsPath "bin\RoslynSentinel.Tests.ModelEval\debug\model-eval\$testName"
$resultsDir = Join-Path $repoRoot "ModelTestingResults\$suffix\$testName"

function Get-RunLeafDirs {
    # Each individual run's own directory is a UTC-timestamp-named leaf (yyyyMMdd-HHmmss[-fff]).
    # MinimalGuidance lays these out flat (model-eval\<TestName>\<timestamp>\); SizeThreshold
    # nests them one level deeper (model-eval\SizeThreshold\n<size>\<timestamp>\) - matching by
    # name pattern rather than a fixed depth handles both without hardcoding either shape.
    if (-not (Test-Path $testDir)) { return @() }
    Get-ChildItem -Path $testDir -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}-\d{6}' } |
        Select-Object -ExpandProperty FullName
}

function Copy-NewRunDirectories {
    param([string[]]$Before)

    $after = Get-RunLeafDirs
    $new = $after | Where-Object { $_ -notin $Before }
    foreach ($runPath in $new) {
        $relative = $runPath.Substring($testDir.Length).TrimStart('\')
        $dest = Join-Path $resultsDir $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Write-Host "Archiving run to $dest" -ForegroundColor DarkGray
        Copy-Item -Path $runPath -Destination $dest -Recurse -Force
    }
}

$exitCode = 0
for ($i = 1; $i -le $Repeats; $i++) {
    if ($Repeats -gt 1) {
        Write-Host "--- Run $i of $Repeats ---" -ForegroundColor DarkCyan
    }

    $before = @(Get-RunLeafDirs)

    # A failing test makes `dotnet test` exit non-zero, which PowerShell 7 promotes to a
    # terminating NativeCommandError under $ErrorActionPreference = 'Stop' - that would jump
    # straight out of this loop, skipping Copy-NewRunDirectories below and every remaining
    # repeat, silently truncating the batch on the FIRST test failure (not just a crash).
    # Suppress that promotion for just this call so a real test failure is archived and
    # counted like any other run; $LASTEXITCODE still reflects the real per-run outcome.
    $ErrorActionPreference = 'Continue'
    & dotnet test $csproj -c Debug `
        --artifacts-path $artifactsPath `
        --filter "Name=$testName" `
        --logger "console;verbosity=detailed"
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($exitCode -ne 0) {
        Write-Warning "Run $i of $Repeats failed (dotnet test exit code $exitCode) - archiving it anyway and continuing."
    }

    Copy-NewRunDirectories -Before $before

    if ($i -lt $Repeats) {
        Wait-ForTesthostExit
    }
}

exit $exitCode

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
    a caller-side loop so this wait always happens.

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
    [ValidateSet('SizeThreshold', 'MinimalGuidance')]
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
    'SizeThreshold'   = 'Model_SizeThresholdSweep'
    'MinimalGuidance' = 'Model_FixesWholeFileRewriteBug_MinimalGuidance'
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

$exitCode = 0
for ($i = 1; $i -le $Repeats; $i++) {
    if ($Repeats -gt 1) {
        Write-Host "--- Run $i of $Repeats ---" -ForegroundColor DarkCyan
    }

    & dotnet test $csproj -c Debug `
        --artifacts-path $artifactsPath `
        --filter "Name=$testName" `
        --logger "console;verbosity=detailed"
    $exitCode = $LASTEXITCODE

    if ($i -lt $Repeats) {
        Wait-ForTesthostExit
    }
}

exit $exitCode

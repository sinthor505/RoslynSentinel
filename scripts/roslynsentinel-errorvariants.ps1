<#
.SYNOPSIS
    Build & run the RoslynSentinel.Tests.ErrorVariants replay test against a chosen LM Studio host: saved
    transcripts are cut at their first error, the error text is swapped for each variant, and the live model's
    next turn is classified into variants.csv.

.DESCRIPTION
    Front door for ErrorVariantReplayTests.ReplayVariants_SamplesAndClassifies ([Explicit]). It maps its parameters to the
    VARIANT_* and ROSLYNSENTINEL_LLM_* environment variables, builds and tests ONLY RoslynSentinel.Tests.ErrorVariants (no MCP
    server, no fixture), and archives the run folder to ModelTestingResults\<host-suffix>\ReplayVariants_SamplesAndClassifies\<timestamp>\
    (the test's own archiver does it; this script copies the run if that did not happen). The result is variants.csv in that folder,
    one row per sample, plus a transcript.json per sample.

    Like roslynsentinel-modeleval.ps1 it uses its own --artifacts-path (_scratchbuild_ev_<host-suffix>), so it can run beside a
    ModelEval job on the same host without racing on obj/.

    A source whose transcript header records a model key, temperature or top-p different from this run's is refused (a harness-error
    row, no model request); transcripts written before those fields existed are accepted and the row's SourceSettings says "unknown".
    So pass -Temperature/-TopP only when the sources were recorded with them. The model is checked once per run against the host's
    /api/v1/models; if it is not loaded, every row is a harness error and no model request is made.

.PARAMETER HostAddress
    LM Studio host (aliases -HostName, -Host). Known aliases: 112, 113 and 85 (as in roslynsentinel-modeleval.ps1). Any other value
    is used verbatim as the full base URL, e.g. http://localhost:1234/v1.

.PARAMETER Model
    ROSLYNSENTINEL_LLM_MODEL, the model key as /api/v1/models lists it. Mandatory, no default: a default would silently overwrite
    the caller's environment variable.

.PARAMETER Sources
    VARIANT_SOURCES. Run folders (their sample-NN\transcript.json children are all replayed), sample folders or transcript.json
    paths. Several allowed (array, or one string separated by ';'). Resolved to absolute paths before the test starts.

.PARAMETER Variants
    VARIANT_FILES. Variant ids as <errorCode>/<id> (for example FileNotFound/armE), <errorCode>/* for every variant of the folder,
    or a bare id that exists in exactly one errorCode folder. The variant files are RoslynSentinel.Tests.ErrorVariants\ErrorVariants\<errorCode>\<id>.md.

.PARAMETER Samples
    VARIANT_SAMPLES: samples per source x variant (default 3).

.PARAMETER ContinueTurns
    VARIANT_CONTINUE_TURNS: live turns after the cut (default 1, one step).

.PARAMETER CutTurn
    VARIANT_CUT_TURN: cut every source after this turn number. Default: each transcript's first turn with an erroring call.
    0 = REGENERATE (cut 0): no seed turns; the model generates every turn live from the source's system prompt, user prompt and
    tools, and the host scripts all recorded turns with the first error call carrying the variant text. A sample is a next-turn
    sample only when the model re-issues that error call in turns 1..E (E = the source's first error turn); the first live turn
    after it is classified. Otherwise DecisionClass is RegenMiss:<first off-script tool or none> and nothing is classified.
    turnCap = E + ContinueTurns. variants.csv columns: mode (seeded|regenerated) and regenTurn (turn the error call was served in).

.PARAMETER AbsentTargets
    VARIANT_ABSENT_TARGETS: names the task says are absent (for example CustomsDeclarationPrinter.cs); a reply that names one and
    says it does not exist classifies as StopProse.

.PARAMETER Temperature
    ROSLYNSENTINEL_LLM_TEMPERATURE. Omitted: the server default, and any value inherited from the shell is cleared.

.PARAMETER TopP
    ROSLYNSENTINEL_LLM_TOP_P. Omitted: the server default, and any value inherited from the shell is cleared.

.EXAMPLE
    .\roslynsentinel-errorvariants.ps1 -HostAddress 113 -Model qwen3.5-9b-mtp -Variants FileNotFound/armE -Samples 1 `
        -Sources ModelTestingResults\113\RunScenario_SamplesAndScores\20261113-050525-257\sample-01
    One sample: the first transcript of the arm-E run, cut at its first error, with arm E's own message put back.

.EXAMPLE
    .\roslynsentinel-errorvariants.ps1 113 -Model qwen3.5-9b-mtp -Variants 'FileNotFound/*' -Samples 3 -AbsentTargets CustomsDeclarationPrinter.cs `
        -Sources ModelTestingResults\113\RunScenario_SamplesAndScores\20261113-050525-257
    Every FileNotFound variant against all ten transcripts of the run, three samples each.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [Alias('HostName', 'Host')]
    [string]$HostAddress,

    [Parameter(Mandatory)]
    [string]$Model,

    [Parameter(Mandatory)]
    [string[]]$Sources,

    [Parameter(Mandatory)]
    [string[]]$Variants,

    [ValidateRange(1, 1000)]
    [int]$Samples = 3,

    [ValidateRange(1, 100)]
    [int]$ContinueTurns = 1,

    [ValidateRange(0, 1000)]
    [int]$CutTurn,

    [string[]]$AbsentTargets,

    [double]$Temperature,

    [double]$TopP
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

$knownHosts = @{
    '112' = @{ BaseUrl = 'http://192.168.1.112:1234/v1'; Suffix = '112' }
    '113' = @{ BaseUrl = 'http://192.168.1.113:1234/v1'; Suffix = '113' }
    '85'  = @{ BaseUrl = 'http://192.168.10.85:1234/v1'; Suffix = '85' }
}

if ($knownHosts.ContainsKey($HostAddress)) {
    $baseUrl = $knownHosts[$HostAddress].BaseUrl
    $suffix = $knownHosts[$HostAddress].Suffix
}
else {
    $baseUrl = $HostAddress
    $suffix = ($HostAddress -replace '[^a-zA-Z0-9]', '_').Trim('_')
    if (-not $suffix) {
        throw "Could not derive an --artifacts-path suffix from -HostAddress '$HostAddress'. Pass a known alias (112, 113, 85) or a URL like http://host:1234/v1."
    }
    Write-Warning "'$HostAddress' is not a known host alias (112, 113, 85) - using it verbatim as the base URL and deriving artifacts suffix '$suffix'."
}

# The testhost runs from the scratch build folder, so every source path must be absolute.
$sourcePaths = @($Sources | ForEach-Object { $_ -split ';' } | Where-Object { $_ } | ForEach-Object {
    if (-not (Test-Path -LiteralPath $_)) {
        throw "-Sources entry '$_' does not exist."
    }
    (Resolve-Path -LiteralPath $_).Path
})
$variantSpecs = @($Variants | ForEach-Object { $_ -split '[,;]' } | Where-Object { $_ })
if ($sourcePaths.Count -eq 0) { throw '-Sources is empty.' }
if ($variantSpecs.Count -eq 0) { throw '-Variants is empty.' }

$testName = 'ReplayVariants_SamplesAndClassifies'
$artifactsPath = Join-Path $repoRoot "_scratchbuild_ev_$suffix"
$csproj = Join-Path $repoRoot 'RoslynSentinel.Tests.ErrorVariants\RoslynSentinel.Tests.ErrorVariants.csproj'
$testDir = Join-Path $artifactsPath "bin\RoslynSentinel.Tests.ErrorVariants\debug\model-eval\$testName"
$resultsDir = Join-Path $repoRoot "ModelTestingResults\$suffix\$testName"

$env:ROSLYNSENTINEL_LLM_BASE_URL = $baseUrl
$env:ROSLYNSENTINEL_LLM_MODEL = $Model
# Always set or cleared, so a value left in the shell cannot skew a run; unset means the server default.
$env:ROSLYNSENTINEL_LLM_TEMPERATURE = if ($PSBoundParameters.ContainsKey('Temperature')) { "$Temperature" } else { $null }
$env:ROSLYNSENTINEL_LLM_TOP_P = if ($PSBoundParameters.ContainsKey('TopP')) { "$TopP" } else { $null }

$env:VARIANT_SOURCES = $sourcePaths -join ';'
$env:VARIANT_FILES = $variantSpecs -join ','
$env:VARIANT_SAMPLES = "$Samples"
$env:VARIANT_CONTINUE_TURNS = "$ContinueTurns"
$env:VARIANT_CUT_TURN = if ($PSBoundParameters.ContainsKey('CutTurn')) { "$CutTurn" } else { $null }
$env:VARIANT_ABSENT_TARGETS = if ($AbsentTargets) { ($AbsentTargets | ForEach-Object { $_ -split '[,;]' } | Where-Object { $_ }) -join ',' } else { $null }

Write-Host ""
Write-Host "=== ErrorVariants: $testName against $baseUrl (model=$Model) ===" -ForegroundColor Cyan
Write-Host "    sources: $($sourcePaths -join '; ')" -ForegroundColor Cyan
Write-Host "    variants: $($variantSpecs -join ', ')  samples=$Samples continueTurns=$ContinueTurns cutTurn=$(if ($env:VARIANT_CUT_TURN) { $env:VARIANT_CUT_TURN } else { '<first error>' })" -ForegroundColor Cyan
Write-Host "    temperature=$(if ($env:ROSLYNSENTINEL_LLM_TEMPERATURE) { $env:ROSLYNSENTINEL_LLM_TEMPERATURE } else { '<server default>' }) topP=$(if ($env:ROSLYNSENTINEL_LLM_TOP_P) { $env:ROSLYNSENTINEL_LLM_TOP_P } else { '<server default>' })" -ForegroundColor Cyan
Write-Host "    --artifacts-path $artifactsPath" -ForegroundColor Cyan
Write-Host ""

function Get-RunLeafDirs {
    # Each run's folder is a UTC-timestamp-named leaf (yyyyMMdd-HHmmss[-fff]) directly under the test folder.
    if (-not (Test-Path $testDir)) { return @() }
    Get-ChildItem -Path $testDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}-\d{6}' } |
        Select-Object -ExpandProperty FullName
}

$before = @(Get-RunLeafDirs)

# A failing test makes `dotnet test` exit non-zero, which PowerShell 7 promotes to a terminating error under
# $ErrorActionPreference = 'Stop'; suppress that for this call so the run is still archived. $LASTEXITCODE keeps the real outcome.
$ErrorActionPreference = 'Continue'
& dotnet test $csproj -c Debug `
    --artifacts-path $artifactsPath `
    --filter "Name=$testName" `
    --logger "console;verbosity=detailed"
$exitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($exitCode -ne 0) {
    Write-Warning "dotnet test exited with code $exitCode - archiving the run anyway."
}

foreach ($runPath in (Get-RunLeafDirs | Where-Object { $_ -notin $before })) {
    $dest = Join-Path $resultsDir (Split-Path $runPath -Leaf)
    if (-not (Test-Path -LiteralPath $dest)) {
        # The test's own archiver normally got here first; this covers a run that ended before it could.
        Write-Host "Archiving run to $dest" -ForegroundColor DarkGray
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Copy-Item -Path $runPath -Destination $dest -Recurse -Force
    }
    $csv = Join-Path $dest 'variants.csv'
    if (Test-Path -LiteralPath $csv) {
        Write-Host "variants.csv: $csv" -ForegroundColor Green
    }
    else {
        Write-Warning "No variants.csv in $dest - the run ended before the first row."
    }
}

exit $exitCode

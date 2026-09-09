<#
.SYNOPSIS
    Run RoslynSentinel.Tools.PlanStepRunner against a chosen LM Studio host, without needing to
    remember the --plan-dir/--repo/--llm-* incantation.

.DESCRIPTION
    Front door for driving plan-eval-defect-remediation-v2-steps-runner one step file at a time,
    each in its own git worktree, against a real model - the automated equivalent of starting a
    new LM Studio chat per step with "Load the solution. Review <step>.md. Implement the plan."
    (see RoslynSentinel.Tools.PlanStepRunner's own doc comments for the full per-step flow: fresh
    worktree off -Branch's tip, isolated `dotnet build` of that worktree's own Server.Advanced,
    stdio MCP connection, LoadSolution, ModelAgentRunner to convergence, build+test snapshot,
    then commit-and-advance or halt-and-leave-the-worktree).

    Uses plan-eval-defect-remediation-v2-steps-runner (not the non-runner
    plan-eval-defect-remediation-v2-steps directory) - that copy's prompts omit the load-solution
    instruction, since PlanStepRunner already loads the worktree's solution itself before the
    model gets a turn.

    A halted/failed step leaves its worktree in place under -WorktreeRoot for inspection - re-run
    with -StartStep/-EndStep set to just that step number after fixing/removing it, rather than
    restarting the whole range from scratch.

.PARAMETER HostAddress
    LM Studio host to target. Known aliases: 112 (http://192.168.1.112:1234/v1, GTX 1080) and 113
    (http://192.168.1.113:1234/v1, RTX 4060). Any other value is used verbatim as the full base
    URL (e.g. http://localhost:1234/v1) - matches roslynsentinel-modeleval.ps1's -HostAddress
    convention.

.PARAMETER StartStep
    First plan step number to run (inclusive), 0-11 (tab-completable). Default: 1
    (step 0/01-baseline.md). Mutually exclusive with -Step.

.PARAMETER EndStep
    Last plan step number to run (inclusive), 0-11 (tab-completable). Default: 11 (the
    final-verification step). Mutually exclusive with -Step.

.PARAMETER Step
    Run (or retry) exactly this one step number, 0-11 (tab-completable) — shorthand for passing
    the same value to both -StartStep and -EndStep. Mutually exclusive with -StartStep/-EndStep.

.PARAMETER Model
    ROSLYNSENTINEL_LLM_MODEL. Default: qwen/qwen3.6-35b-a3b (the model used for the manual
    steps 00-04 runs this tool automates the rest of).

.PARAMETER ImplRepo
    Path to the RoslynSentinel checkout PlanStepRunner should branch/worktree from. Default: the
    sibling RoslynSentinel-eval-defect-remediation-v2-impl checkout is deliberately NOT used here
    - PlanStepRunner creates its own branch (-Branch) off this repo's current HEAD and never
    touches -impl's own in-progress branch/worktree, so the two stay fully independent. Default:
    this script's own repo root (the RoslynSentinel checkout this script lives in).

.PARAMETER PlanDir
    Path to plan-eval-defect-remediation-v2-steps-runner. Default:
    docs\tests\plan-eval-defect-remediation-v2-steps-runner under this script's own repo root —
    the canonical copy, kept independent of the sibling -impl checkout's own in-progress work.

.PARAMETER Branch
    Git branch PlanStepRunner commits each successful step onto, created off -ImplRepo's current
    HEAD if it doesn't already exist. Default: eval-defect-remediation-v2-auto.

.PARAMETER WorktreeRoot
    Directory each step's fresh worktree is created under. Default: PlanStepRunner's own default
    (%TEMP%\RoslynSentinel-plan-step-worktrees).

.PARAMETER OutDir
    Directory transcripts/logs are written under. Default: PlanStepRunner's own default
    (PlanStepRunnerResults under -ImplRepo).

.PARAMETER TurnCap
    Max model turns per step before giving up without converging. Default: 40.

.PARAMETER WallClockCapMinutes
    Max wall-clock minutes per step before giving up without converging. Default: 30.

.PARAMETER Temperature
    Sampling temperature sent on every request (omitted entirely if not passed, letting LM Studio
    apply its own default) - same semantics as roslynsentinel-modeleval.ps1's -Temperature.

.PARAMETER TopP
    Nucleus sampling top-p, sent and verified the same way as -Temperature.

.EXAMPLE
    .\roslynsentinel-planstep.ps1 -HostAddress 113
    Run every step (1 through 11) against the .113 RTX 4060 host using the default model.

.EXAMPLE
    .\roslynsentinel-planstep.ps1 113 5 5
    Run (or retry) just step 5 against .113, positional -StartStep/-EndStep args.

.EXAMPLE
    .\roslynsentinel-planstep.ps1 -HostAddress 113 -Step 5
    Equivalent shorthand for the previous example, using the -Step parameter set.

.EXAMPLE
    .\roslynsentinel-planstep.ps1 -HostAddress 112 -StartStep 7 -EndStep 11 -Model qwen/qwen3.6-35b-a3b
    Run the Phase 3 steps through final verification against .112 with an explicit model.
#>
[CmdletBinding(DefaultParameterSetName = 'Range')]
param(
    [Parameter(Position = 0, Mandatory)]
    [string]$HostAddress,

    [Parameter(ParameterSetName = 'Range', Position = 1)]
    [ValidateSet(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)]
    [int]$StartStep = 1,

    [Parameter(ParameterSetName = 'Range', Position = 2)]
    [ValidateSet(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)]
    [int]$EndStep = 11,

    [Parameter(ParameterSetName = 'Single', Mandatory)]
    [ValidateSet(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)]
    [int]$Step,

    [string]$Model = 'qwen/qwen3.6-35b-a3b',

    [string]$ImplRepo = $PSScriptRoot,

    [string]$PlanDir,

    [string]$Branch = 'eval-defect-remediation-v2-auto',

    [string]$WorktreeRoot,

    [string]$OutDir,

    [int]$TurnCap = 40,

    [int]$WallClockCapMinutes = 30,

    [double]$Temperature,

    [double]$TopP
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

if ($PSCmdlet.ParameterSetName -eq 'Single') {
    $StartStep = $Step
    $EndStep = $Step
}

$knownHosts = @{
    '112' = 'http://192.168.1.112:1234/v1'
    '113' = 'http://192.168.1.113:1234/v1'
}

if ($knownHosts.ContainsKey($HostAddress)) {
    $baseUrl = $knownHosts[$HostAddress]
}
else {
    $baseUrl = $HostAddress
    Write-Warning "'$HostAddress' is not a known host alias (112, 113) - using it verbatim as the base URL."
}

if (-not $PlanDir) {
    $PlanDir = Join-Path $repoRoot 'docs\tests\plan-eval-defect-remediation-v2-steps-runner'
}
if (-not (Test-Path $PlanDir)) {
    throw "Plan directory not found: $PlanDir. Pass -PlanDir explicitly if plan-eval-defect-remediation-v2-steps-runner lives somewhere other than the default docs\tests location in this repo."
}

$runnerProject = Join-Path $repoRoot 'RoslynSentinel.Tools.PlanStepRunner\RoslynSentinel.Tools.PlanStepRunner.csproj'

Write-Host ""
Write-Host "=== PlanStepRunner: steps $StartStep-$EndStep against $baseUrl (model=$Model) ===" -ForegroundColor Cyan
Write-Host "    --plan-dir $PlanDir" -ForegroundColor Cyan
Write-Host "    --repo $ImplRepo" -ForegroundColor Cyan
Write-Host "    --branch $Branch" -ForegroundColor Cyan
if ($PSBoundParameters.ContainsKey('Temperature')) {
    Write-Host "    --llm-temperature $Temperature" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('TopP')) {
    Write-Host "    --llm-top-p $TopP" -ForegroundColor Cyan
}
Write-Host ""

$runnerArgs = @(
    'run', '--project', $runnerProject, '--'
    '--plan-dir', $PlanDir
    '--repo', $ImplRepo
    '--branch', $Branch
    '--start-step', $StartStep
    '--end-step', $EndStep
    '--turn-cap', $TurnCap
    '--wall-clock-cap-minutes', $WallClockCapMinutes
    '--llm-base-url', $baseUrl
    '--llm-model', $Model
)

if ($WorktreeRoot) {
    $runnerArgs += @('--worktree-root', $WorktreeRoot)
}
if ($OutDir) {
    $runnerArgs += @('--out-dir', $OutDir)
}
if ($PSBoundParameters.ContainsKey('Temperature')) {
    $runnerArgs += @('--llm-temperature', $Temperature)
}
if ($PSBoundParameters.ContainsKey('TopP')) {
    $runnerArgs += @('--llm-top-p', $TopP)
}

# A halted step (non-convergence, blocked model, or a red build outside the known-build-optional
# steps) makes the runner exit non-zero - surface that as this script's own exit code rather than
# letting PowerShell's $ErrorActionPreference = 'Stop' promote it into a terminating exception,
# same reasoning as roslynsentinel-modeleval.ps1's dotnet-test exit-code handling.
$ErrorActionPreference = 'Continue'
& dotnet @runnerArgs
$exitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'

if ($exitCode -ne 0) {
    Write-Warning "PlanStepRunner exited with code $exitCode - see the console output above for which step halted and why, and inspect its worktree before retrying."
}

exit $exitCode

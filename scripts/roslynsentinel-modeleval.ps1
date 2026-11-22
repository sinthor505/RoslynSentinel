<#
.SYNOPSIS
    Build & run a RoslynSentinel.Tests.ModelEval real-LM-Studio test against a chosen host,
    without needing to remember the env vars / filter syntax / --artifacts-path incantation.

.DESCRIPTION
    Front door for the two model-eval scenarios exercised most often:

      SizeThreshold    - Model_SizeThresholdSweep ([Explicit]). Sweeps unrelated-method
                          counts x repeats against SizeGraduatedReproducer fixture variants.
                          -Size sets ROSLYNSENTINEL_MODELEVAL_SIZES (single value, e.g. 60).
      LiteralSteps     - Model_FixesWholeFileRewriteBug_UsingExistingHelperPattern (runs by
                          default). Level-2 prompt: names the buggy method AND the sibling
                          fix pattern to reuse, plus scripted steps - the least ambiguous
                          prompt in the ladder. Use this (not MinimalGuidance) when the goal
                          is isolating tool-use/execution behavior from planning/bug-location,
                          since the model isn't asked to find or scope the fix itself.
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
      LargeResultPaging - Model_PagesLargeResultToFindNeedle. Read-only: ReadFile on a generated
                          300-method file (fixture adds ContosoOrders.Core/FixtureHelpers/TierRates.cs)
                          overflows the inline cap, so the server returns a largeResult pointer and the
                          model must page the rest with GetLargeResult to find one needle method. Asserts
                          the marshaller was exercised, the pointer was followed, the answer is right, and
                          no stack trace leaked; writes largeresult-summary.txt next to agent.log. Not
                          [Explicit] - runs by default in the ModelEval project.
      LargeResultPagingReadFileOnly - Model_PagesLargeResultToFindNeedle_ReadFileOnly. Same fixture and
                          15 KB cap as LargeResultPaging, but Search, GetFileOutline, GetMethodSource,
                          Member and ReadFile memberName lookups are blocked by a filter, so only whole-file
                          ReadFile and GetLargeResult paging can reach the needle. Asserts the pointer was
                          followed and a GetLargeResult page carried the needle, plus the same answer and
                          leak checks. [Explicit]: not run by default (the loop is deterministic with
                          every other tool hidden; Tests.Tools.Basic/ReadFilePagingLoopTests covers it).
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
                          "VERIFIED: PASS" verdict. Each phase capped at 5 minutes wall-clock.
                          Pass -MinimalTools to narrow every phase's advertised tool schema from
                          the full 48 tools down to the 11 actually observed in real transcripts
                          (see project_granite42_8b_tool_schema_size_isolated) - a diagnostic for
                          testing whether schema size itself is the latency driver on slow models
                          like granite-4.2-8b, not a change to what phases are allowed to do.
      OrderPricingRefactor - Model_AppliesThreeChainedRefactors. Different fixture from every
                          test above (OrderPricingRefactorReproducer, not
                          WholeFileRewriteReproducer): three independent, ordinary refactoring
                          steps chained on one small class (extract a duplicated expression,
                          rename a method with a real cross-file call site, change the new
                          method's accessibility) instead of one bug-fix scenario. Prompt gives
                          MinimalGuidance/Disambiguated-style flexibility - the model chooses
                          which tool(s) to use for each step (ExtractMethodSafe/RenameSymbol/
                          ChangeAccessibility/ModifyModifier or plain ApplyDiff).
      OrderPricingRefactorChain4/5/6/7/8/9/10 - escalating ladder built on top of
                          OrderPricingRefactor's 3 base steps, one added idiomatic refactor per
                          rung: Chain4 adds inlining a single-use local; Chain5 adds renaming a
                          parameter (rate -> discountRate); Chain6 adds a guard clause rejecting a
                          negative discount rate (the one rung introducing new behavior,
                          deliberately scoped to non-negative inputs to avoid repeating
                          OrderPricingRefactor's step-1 wording-ambiguity failure); Chain7 adds
                          extracting an IOrderPricingCalculator interface with OrderCheckout
                          depending on the interface type (requires Advanced-tier tools - see
                          below); Chain8 adds renaming the OrderPricingCalculator class itself to
                          StandardOrderPricingCalculator; Chain9 adds widening
                          IOrderPricingCalculator to also declare DescribeOrder; Chain10 adds
                          replacing SummarizeShipping's magic zone numbers with named constants.
                          Each rung is its own fixture/prompt/test method in
                          OrderPricingRefactorChainAgentTests.cs, not a parameterization of
                          OrderPricingRefactor. NOTE: as of 2026-09-08 this test class registers
                          the full Advanced tool catalog (not just Basic's Refactor/Workspace
                          modes) for every rung, since Chain7's interface-extraction step needs
                          ExtractInterface/CreateFile, which only exist in Advanced - see the
                          class doc comment in OrderPricingRefactorChainAgentTests.cs.

    -Test Scenario also prints a per-scenario summary (Get-ScenarioSummary.ps1) of each archived run's
    results.csv: Inconclusive samples are listed by reason and kept out of every rate. It is advisory and never fails a run.

    Each host gets its own --artifacts-path (<scratch>\build\<host-suffix>) so
    that two hosts can be launched concurrently without racing on shared project references'
    obj/ output - see reference_model_eval_procedure memory / CS2012 "locked by VBCSCompiler"
    for the failure this avoids. Test/transcript output lands under
    <scratch>\build\<host-suffix>\bin\RoslynSentinel.Tests.ModelEval\debug\model-eval\...
    <scratch> is ROSLYNSENTINEL_SCRATCH_ROOT when set, else <parent of the repo>\RoslynSentinel-ScratchDir,
    outside the repo. An eval checkout (marker files complete and lastused) builds into its own checkout-local folder instead.

.PARAMETER HostAddress
    an alias from scripts/eval-hosts.json (for example 85u or 85lms), a machine key (its default endpoint) or a base URL

.PARAMETER Test
    SizeThreshold | LiteralSteps | MinimalGuidance | MinimalGuidanceDisambiguated | PlanOnly | LargeResultPaging |
    LargeResultPagingReadFileOnly | PlanThenExecute | ScriptedPlan | PlanImplementVerify | OrderPricingRefactor |
    OrderPricingRefactorChain4 | OrderPricingRefactorChain5 | OrderPricingRefactorChain6 |
    OrderPricingRefactorChain7 | OrderPricingRefactorChain8 | OrderPricingRefactorChain9 |
    OrderPricingRefactorChain10 | ResumeTranscript (resume a saved ten-chain transcript and resample
    the next turns; see -ResumeTranscript) | ScoreTranscript (score a saved transcript without calling a
    model; see -ScoreTranscript) | ShippingKindResume (resume a saved ShippingKind transcript at turn
    -ResumeKeepTurns and resample the next turns; needs -ResumeTranscript and -ResumeStep) | Scenario (run a registered targeted scenario for -Samples samples; see -Scenario) |
    ShippingKindStep (Model_CompletesShippingKindStep: six independent steps of the stepped ShippingKind scenario,
    each from a golden commit; all six by default, or the ones named by -Step) | ShippingKindStepV2
    (Model_RunsShippingKindStepPromptV2: the same five steps with the V2 prompts for steps 2, 3 and 5; steps 1 and 4
    reuse the V1 prompts as a control; -Step works the same) | ShippingKindStepV3 / V4 / V5 / V6
    (Model_RunsShippingKindStepPromptV3..V6: further prompt variants; V3 targeted fixes, V4 Haiku-style brief for
    all five steps, V5 defined-behaviour wording, V6 = V5 plus a search-before-replying nudge) |
    ShippingKindStepV7 .. V11 (Model_RunsShippingKindStepPromptV7..V11: alternate STEP 5 wordings, steps 1-4 reuse V1;
    run with -Step 5), ShippingKindStepV12 / V13 (step 5 with a visible ShippingBehaviourTests file seeded in the
    step-0 golden: V12 = V7 plus a run-the-tests sentence, V13 = V7 control; run with -Step 5), ShippingKindStepV14 (V7 plus an explicit
    IsExpedited-edit-first and acceptance sentence; step 5, no visible tests; run with -Step 5), ShippingKindStepFinal
    (Model_RunsShippingKindStepPromptFinal: the chosen prompt set for all six steps, V1 for 1, 4, 6, V2 for 2, 3, V7
    for 5; the baseline for later changes) | ShippingKindStepUsingStatic
    (Model_RunsShippingKindStepPromptUsingStatic: the Final prompts, but the fixture already has
    `using static ContosoOrders.Core.ShippingKinds;` in the four target files and steps 4 and 5 ask for bare
    constant names such as `case Overnight:`; compare steps 4 and 5 against ShippingKindStepFinal) |
    ShippingKindStepPriorityOvernight (Model_RunsShippingKindStepPromptPriorityOvernight: the Final prompts with
    the constant Overnight renamed PriorityOvernight in goldens, prompts and check sources; its value stays
    "overnight"; compare against ShippingKindStepFinal) | ImplementationScenario (Model_CompletesImplementationScenario:
    the data-driven implementation scenarios in ImplementationScenarioRegistry, each a small ContosoOrders overlay
    with golden steps; -Scenario <Id> picks one (default all), -Step narrows to its steps). Required. Accepts multiple values (e.g.
    -Test OrderPricingRefactor,OrderPricingRefactorChain4,OrderPricingRefactorChain5) to run
    each one in turn, in the order given, all under the same -HostAddress/-Model/-Repeats -
    this is the supported way to queue a multi-test overnight sequence: it reuses this script's
    own env-var setup, --artifacts-path, and Wait-ForTesthostExit handling for every test
    instead of a caller-side loop re-deriving any of that (see
    feedback_check_for_existing_scripts_before_looping).

.PARAMETER Size
    SizeThreshold only: single value for ROSLYNSENTINEL_MODELEVAL_SIZES (default: 60).
    Ignored for MinimalGuidance.

.PARAMETER Model
    ROSLYNSENTINEL_LLM_MODEL. Default: qwen3.5-9b-coder (confirmed present on both .112/.113
    as of 2026-08-30 - re-confirm if it's been a while, since /v1/models lists every
    downloaded model, not which one is actually loaded).

.PARAMETER Clean
    Archive this host's <scratch>\build\<suffix>\bin\...\model-eval directory (transcripts,
    agent.log, results.csv - everything) to <scratch>\ModelTestingResults\<host-suffix>\<timestamp>\
    then delete the original, before running - so stale run-history from
    a previous session isn't mixed in with this run's data, but nothing is lost. Skipped
    silently if there's no existing model-eval directory to archive.

.PARAMETER Repeats
    Run the test this many times in sequence (default: 1). Between iterations, waits for
    this scratch path's own testhost.exe to fully exit before starting the next build - a
    bare `for` loop calling `dotnet test --artifacts-path <dir>` repeatedly WILL race the
    next build's DLL copy against the previous run's still-exiting testhost.exe and fail
    with MSB3027 "locked by testhost" (seen in practice 2026-08-31). Use -Repeats instead of
    a caller-side loop so this wait always happens. After each individual run (pass or fail),
    its own run directory is copied to <scratch>\ModelTestingResults\<host-suffix>\<TestName>\ right
    away - this happens unconditionally, independent of -Clean, so results are available in
    the common folder as soon as each run finishes rather than only on the next -Clean.

.PARAMETER MinimalTools
    PlanImplementVerify only: sets ROSLYNSENTINEL_LLM_MINIMAL_TOOLS=true, narrowing every
    phase's advertised tools/list schema to the 11 tools actually used in real transcripts
    instead of the full 48. Ignored (has no effect) for every other -Test value.

.PARAMETER Temperature
    Sampling temperature sent on every /v1/responses request (omitted entirely if not passed,
    letting LM Studio apply its own default). Confirmed real 2026-09-05: LM Studio echoes back
    whatever value it actually used, and LmStudioAgentClient logs a warning if that echoed value
    doesn't match what was requested (e.g. a preset silently overriding it - see LM Studio
    bug tracker #1389). IBM's own recommended llama.cpp params for Granite 4.x are
    -Temperature 0.0 -TopP 1.0 (greedy decoding) - see
    project_granite42_8b_tool_schema_size_isolated for why this is being tested as a second,
    independent lever alongside -MinimalTools for granite's chronic slowness.

.PARAMETER TopP
    Nucleus sampling top-p, sent and verified the same way as -Temperature.

.PARAMETER TopK
.PARAMETER RepeatPenalty
.PARAMETER MinP
    UI-ONLY as far as this harness can tell - passing any of these prints a warning instead of
    doing anything. Confirmed 2026-09-05: LM Studio's /v1/responses endpoint (what this harness
    uses) silently drops top_k/repeat_penalty/min_p - a same-prompt A/B test at repeat_penalty
    1.0 vs 1.8 produced byte-identical generations, even though top_k/repeat_penalty ARE real,
    documented fields on /v1/chat/completions (a different endpoint this harness doesn't use).
    Set these by hand in LM Studio's sampling panel for the loaded model instead.

.PARAMETER HostSampler
    Sampler values you set by hand in LM Studio that the harness cannot send or verify, as
    "topK=20;minP=0.1;repeatPenalty=1.0;presencePenalty=0" (keys: topK, minP, repeatPenalty,
    presencePenalty, frequencyPenalty). Exported as ROSLYNSENTINEL_LLM_HOST_SAMPLER and recorded
    in transcript.json Settings.Sampler as Source "declared", Verified false. It changes nothing
    about the request; it only documents what the host was configured with.

.PARAMETER StreamTimeout
	How many seconds to wait before assuming the model has failed to generating streaming tokens.

.PARAMETER ResumeTranscript
    ResumeTranscript only (required there): transcript.json, or the run folder holding it, of a
    Model_AppliesTenChainedRefactors run (ROSLYNSENTINEL_MODELEVAL_RESUME_TRANSCRIPT).

.PARAMETER ResumeKeepTurns
    ResumeTranscript only: keep turns 1..K of the transcript as history (default 3, so the model
    generates turn 4). ROSLYNSENTINEL_MODELEVAL_RESUME_KEEP_TURNS.

.PARAMETER ShippingKindMappingProbe
    Not a parameter: -Test ShippingKindMappingProbe sends one text turn (no tools, the three step 4 files in the prompt, "reply with the
    literal -> ShippingKinds.Name mapping") -Samples times and writes a results.csv of the names found and any that are not members.

.PARAMETER ResumeStep
    ShippingKindResume only (required there, 1-6): the ShippingKind step the transcript came from, which selects the golden
    commit each sample starts from (ROSLYNSENTINEL_MODELEVAL_RESUME_STEP). -Test ShippingKindResume also takes -ResumeTranscript,
    -ResumeKeepTurns, -Samples and -ContinueTurns; keep the turns up to the read of ShippingKinds.cs (2) and continue 1 turn to
    sample the first edit. Count typos in the saved samples with scripts/Get-ShippingKindTypoCounts.ps1.

.PARAMETER LiteralCase
    ShippingKindReplay only: Original (default) keeps the saved history as recorded; Capitalized rewrites the quoted values "standard",
    "express", "overnight", "pickup" in it to "Standard", ... so a value is spelled exactly like its constant name
    (ROSLYNSENTINEL_MODELEVAL_RESUME_LITERAL_CASE). -Test ShippingKindReplay seeds the saved turns 1..-ResumeKeepTurns (use 2: up to the
    read of ShippingKinds.cs), runs no fixture and no tools, and samples one live turn -Samples times; it takes -ResumeTranscript
    and -Samples. Count typos with scripts/Get-ShippingKindTypoCounts.ps1.

.PARAMETER Samples
    ResumeTranscript, ShippingKindResume, ShippingKindReplay and Scenario: how many samples (default 10). ROSLYNSENTINEL_MODELEVAL_RESUME_SAMPLES or ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES.

.PARAMETER ContinueTurns
    ResumeTranscript only: live turns each sample runs after the resume point (default 3).
    ROSLYNSENTINEL_MODELEVAL_RESUME_CONTINUE_TURNS.

.PARAMETER LoopAssist
    ResumeTranscript only: turn loop assist on for the samples (ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST=1).
    Without it the script sets the variable to 0, so a variable left in the shell cannot skew an A/B pair.

.PARAMETER LoopAssistRole
    ResumeTranscript only: 'user' (default) or 'assistant', the role of the injected message
    (ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST_ROLE). Only matters with -LoopAssist.

.PARAMETER ScoreTranscript
    ScoreTranscript only (required there): transcript.json, or the run folder holding it, to score.
    No model is called, so -HostAddress is still required by this script but any alias works. The
    scorer appends one line per transcript to the CSV (ROSLYNSENTINEL_MODELEVAL_SCORE_TRANSCRIPT).

.PARAMETER ScoreCsv
    ScoreTranscript only: the results CSV the scorer appends one line to; a header is written when the
    file is missing or empty. Default: score.csv in the folder holding the transcript
    (ROSLYNSENTINEL_MODELEVAL_SCORE_CSV).

.PARAMETER ScoreTool
    ScoreTranscript only: the tool whose first call and recovery are scored. Default: ReplaceSnippet
    (ROSLYNSENTINEL_MODELEVAL_SCORE_TOOL).

.PARAMETER Scenario
    Scenario only (required there): id of a scenario in ScenarioRegistry (RoslynSentinel.Tests.ModelEval). Runs it for -Samples samples, each on a fresh fixture, and writes one scored CSV line per sample to results.csv in the run folder (ROSLYNSENTINEL_MODELEVAL_SCENARIO).
    ImplementationScenario (optional there): id of a scenario in ImplementationScenarioRegistry; selects its ImplScenario_<Id>_Step<k> cases by test filter (no env var). Omit to run every scenario.

.PARAMETER TurnCap
    Scenario only: replaces the scenario definition's TurnCap (maximum live model turns) for this run. Default 0 = unset, the
    scenario's own cap applies; a positive value sets ROSLYNSENTINEL_MODELEVAL_TURN_CAP, and the script clears the variable
    otherwise so one left in the shell cannot skew a run.

.PARAMETER StepWallClockMinutes
    ShippingKindStep* only: wall-clock cap per step in minutes (default 0 = unset, the test's 15 minutes apply). A positive value
    sets ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES; the script clears the variable otherwise. For slow models (tiel) that
    sometimes exceed 15 minutes; the cap is printed in step-<k>.outcome.txt. Raise the evalqueue -TimeLimitMinutes to match
    (it covers every step of one run plus the build).

.PARAMETER ReasoningWindow
    Every test: which assistant turns replay their reasoning (ReasoningReplayPolicy, created by ModelAgentRunner).
    Default -1 = unset: the pressure policy - replay all reasoning until the projected prompt reaches 70% of the model's
    context, then drop the oldest half of the unpinned turns; the first 3 turns are always kept (if the context length
    is unknown, a 10-turn block window). 0 = replay every turn's reasoning, never drop. N > 0 = the legacy block window
    of N (the oldest turns lose their reasoning N at a time, no pinned turns; 10 was the default before the pressure
    policy). A value >= 0 sets ROSLYNSENTINEL_REASONING_WINDOW; the script clears the variable otherwise so one left in
    the shell cannot skew a run. The transcript records ReasoningReplayMode, ContextLength and ReasoningDropTurns.

.PARAMETER ReasoningPressure
    Every test: the context fraction at which the pressure policy drops reasoning (default 0.7). Use a lower value (for
    example 0.5) to force a drop in a live run. Default 0 = unset; a value in (0, 1] sets
    ROSLYNSENTINEL_REASONING_PRESSURE, and the script clears the variable otherwise. Ignored when -ReasoningWindow >= 0.

.PARAMETER Step
    ShippingKindStep only: step numbers 1-6 to run (for example -Step 4,5 runs steps 4 and 5). Default: all six
    for ShippingKindStep (ShippingKindStepV2..V13 have steps 1-5 only, so use -Step there when you want fewer).
    Each listed step is one Model_CompletesShippingKindStep test case. ImplementationScenario: the scenario's step numbers
    (most scenarios have one step); needs -Scenario.

.EXAMPLE
    .\roslynsentinel-modeleval.ps1 -HostAddress 112 -Test SizeThreshold -Size 60
    Sweep size=60 against the .112 GTX 1080 host.

.EXAMPLE
    .\roslynsentinel-modeleval.ps1 -HostAddress 113 -Test MinimalGuidance
    Run the symptom-only whole-file-rewrite fix prompt against the .113 RTX 4060 host.

.EXAMPLE
    .\roslynsentinel-modeleval.ps1 -HostAddress 113 -Test Scenario -Scenario CalcDiscRename -Samples 10
    Run the registered CalcDiscRename scenario for 10 samples against the .113 RTX 4060 host.

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
    [ValidateSet('SizeThreshold', 'LiteralSteps', 'MinimalGuidance', 'MinimalGuidanceDisambiguated', 'PlanOnly', 'LargeResultPaging', 'LargeResultPagingReadFileOnly', 'PlanThenExecute', 'ScriptedPlan', 'PlanImplementVerify', 'OrderPricingRefactor', 'OrderPricingRefactorChain4', 'OrderPricingRefactorChain5', 'OrderPricingRefactorChain6', 'OrderPricingRefactorChain7', 'OrderPricingRefactorChain8', 'OrderPricingRefactorChain9', 'OrderPricingRefactorChain10', 'ResumeTranscript', 'ScoreTranscript', 'Scenario', 'ShippingKindStep', 'ShippingKindStepV2', 'ShippingKindStepV3', 'ShippingKindStepV4', 'ShippingKindStepV5', 'ShippingKindStepV6', 'ShippingKindStepV7', 'ShippingKindStepV8', 'ShippingKindStepV9', 'ShippingKindStepV10', 'ShippingKindStepV11', 'ShippingKindStepV12', 'ShippingKindStepV13', 'ShippingKindStepV14', 'ShippingKindStepFinal', 'ShippingKindStepUsingStatic', 'ShippingKindStepPriorityOvernight', 'ShippingKindResume', 'ShippingKindMappingProbe', 'ShippingKindReplay', 'ImplementationScenario')]
    [string[]]$Test,

    [Parameter(Position = 2)]
    [int]$Size = 60,

    [string]$Model = 'qwen3.5-9b-coder',

    [switch]$Clean,

    [int]$Repeats = 1,

    [switch]$MinimalTools,

    [double]$Temperature,

    [double]$TopP,

    [double]$TopK,

    [double]$RepeatPenalty,

    [double]$MinP,

    [string]$HostSampler,

	[double]$StreamTimeout,

    [string]$ResumeTranscript,

    [int]$ResumeKeepTurns = 3,

    [int]$ResumeStep,

    [ValidateSet('Original', 'Capitalized', 'QualifiedResult', 'ExampleOvernight')]
    [string]$LiteralCase = 'Original',

    [int]$Samples = 10,

    [int]$ContinueTurns = 3,

    [switch]$LoopAssist,

    [ValidateSet('user', 'assistant')]
    [string]$LoopAssistRole = 'user',

    [string]$ScoreTranscript,

    [string]$ScoreCsv,

    [string]$ScoreTool = 'ReplaceSnippet',

    [string]$Scenario,

    [int]$TurnCap = 0,

    [ValidateRange(0, 600)]
    [double]$StepWallClockMinutes = 0,

    [ValidateRange(-1, 1000)]
    [int]$ReasoningWindow = -1,

    [ValidateRange(0.0, 1.0)]
    [double]$ReasoningPressure = 0,

    [ValidateRange(1, 6)]
    [int[]]$Step
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')
$scratchRoot = Get-RoslynSentinelScratchRoot -RepoRoot $repoRoot

. (Join-Path $PSScriptRoot 'roslynsentinel-evalhosts.ps1')
$evalTable = Read-EvalHostTable
$hostEntry = Resolve-EvalHostEntry -Address $HostAddress -Table $evalTable
$baseUrl = $hostEntry.BaseUrl
$suffix = $hostEntry.Suffix
if (-not $hostEntry.Known) {
    $knownAliases = (@($evalTable | ForEach-Object { $_.Alias })) -join ', '
    $knownMachines = (@($evalTable | ForEach-Object { $_.Machine } | Sort-Object -Unique)) -join ', '
    Write-Warning "'$HostAddress' is not an alias or machine key in scripts/eval-hosts.json - using it verbatim as the base URL and deriving artifacts suffix '$suffix'. Known aliases: $knownAliases. Known machine keys: $knownMachines."
}

$testNames = @{
    'SizeThreshold'                 = 'Model_SizeThresholdSweep'
    'LiteralSteps'                  = 'Model_FixesWholeFileRewriteBug_UsingExistingHelperPattern'
    'MinimalGuidance'               = 'Model_FixesWholeFileRewriteBug_MinimalGuidance'
    'MinimalGuidanceDisambiguated'  = 'Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated'
    'PlanOnly'                      = 'Model_PlansWholeFileRewriteFix_PrefersCallingHelper'
    'LargeResultPaging'             = 'Model_PagesLargeResultToFindNeedle'
    'LargeResultPagingReadFileOnly' = 'Model_PagesLargeResultToFindNeedle_ReadFileOnly'
    'PlanThenExecute'               = 'Model_FixesWholeFileRewriteBug_PlanThenExecute'
    'ScriptedPlan'                   = 'Model_FixesWholeFileRewriteBug_ScriptedPlan'
    'PlanImplementVerify'           = 'Model_FixesWholeFileRewriteBug_PlanImplementVerify'
    'OrderPricingRefactor'          = 'Model_AppliesThreeChainedRefactors'
    'OrderPricingRefactorChain4'    = 'Model_AppliesFourChainedRefactors'
    'OrderPricingRefactorChain5'    = 'Model_AppliesFiveChainedRefactors'
    'OrderPricingRefactorChain6'    = 'Model_AppliesSixChainedRefactors'
    'OrderPricingRefactorChain7'    = 'Model_AppliesSevenChainedRefactors'
    'OrderPricingRefactorChain8'    = 'Model_AppliesEightChainedRefactors'
    'OrderPricingRefactorChain9'    = 'Model_AppliesNineChainedRefactors'
    'OrderPricingRefactorChain10'   = 'Model_AppliesTenChainedRefactors'
    'ResumeTranscript'              = 'ResumeFromTranscript_SamplesTheNextTurns'
    'ScoreTranscript'               = 'ScoreSavedTranscript_AppendsOneLine'
    'Scenario'                      = 'RunScenario_SamplesAndScores'
    'ShippingKindStep'              = 'Model_CompletesShippingKindStep'
    # The V2 name must not contain 'Model_CompletesShippingKindStep': the V1 filter below is Name~ on that string.
    'ShippingKindStepV2'            = 'Model_RunsShippingKindStepPromptV2'
    'ShippingKindStepV3'            = 'Model_RunsShippingKindStepPromptV3'
    'ShippingKindStepV4'            = 'Model_RunsShippingKindStepPromptV4'
    'ShippingKindStepV5'            = 'Model_RunsShippingKindStepPromptV5'
    'ShippingKindStepV6'            = 'Model_RunsShippingKindStepPromptV6'
    'ShippingKindStepV7'            = 'Model_RunsShippingKindStepPromptV7'
    'ShippingKindStepV8'            = 'Model_RunsShippingKindStepPromptV8'
    'ShippingKindStepV9'            = 'Model_RunsShippingKindStepPromptV9'
    # V7-V11 override step 5 only; run them with -Step 5. (No test is named ...PromptV1, so Name~...PromptV10 / V11 select only their own.)
    'ShippingKindStepV10'          = 'Model_RunsShippingKindStepPromptV10'
    'ShippingKindStepV11'           = 'Model_RunsShippingKindStepPromptV11'
    # V12 (V7 + run-the-tests sentence) and V13 (V7 control) also seed a visible shipping test file; step 5 only, run with -Step 5.
    'ShippingKindStepV12'           = 'Model_RunsShippingKindStepPromptV12'
    'ShippingKindStepV13'           = 'Model_RunsShippingKindStepPromptV13'
    'ShippingKindStepV14'           = 'Model_RunsShippingKindStepPromptV14'
    # Final = the chosen prompt set for all six steps (V1 for 1, 4, 6; V2 for 2, 3; V7 for 5); the baseline for later changes.
    'ShippingKindStepFinal'         = 'Model_RunsShippingKindStepPromptFinal'
    # UsingStatic = Final, but the step 3 golden seeds `using static ContosoOrders.Core.ShippingKinds;` into the four target files and steps 4 and 5 ask for bare names; all six steps, run with -Step 4,5 to isolate the token effect.
    'ShippingKindStepUsingStatic'   = 'Model_RunsShippingKindStepPromptUsingStatic'
    # PriorityOvernight = Final with the constant Overnight named PriorityOvernight everywhere (goldens, prompts, check sources; value stays "overnight"); all six steps. Tests whether the ".O" typo moves or disappears.
    'ShippingKindStepPriorityOvernight' = 'Model_RunsShippingKindStepPromptPriorityOvernight'
    # Resume a saved ShippingKind transcript at turn -ResumeKeepTurns and resample the next turns; needs -ResumeTranscript and -ResumeStep.
    'ShippingKindResume'            = 'ResumeShippingKindStep_SamplesTheNextTurns'
    # One text turn with the step 4 files in the prompt and no tools: how often does the model misspell a constant it can see? Uses -Samples.
    'ShippingKindMappingProbe'      = 'ShippingKindMappingProbe_SamplesTheMapping'
    # Stateless replay of a saved ShippingKind transcript: no fixture or tool execution, one live turn per sample; needs -ResumeTranscript, optional -LiteralCase.
    'ShippingKindReplay'            = 'ShippingKindReplay_SamplesTheDecisionTurn'
    # Data-driven implementation scenarios (ImplementationScenarioRegistry); cases are named ImplScenario_<Id>_Step<k> via SetName.
    # -Scenario <Id> picks one scenario (default: all), -Step narrows to its steps.
    'ImplementationScenario'        = 'Model_CompletesImplementationScenario'
}

if ($Step -and -not ($Test | Where-Object { $_ -like 'ShippingKindStep*' -or $_ -eq 'ImplementationScenario' })) {
    throw "-Step applies only to -Test ShippingKindStep, ShippingKindStepV2..V6 or ImplementationScenario."
}

if ($Step -and $Test -contains 'ImplementationScenario' -and -not $Scenario) {
    throw "-Test ImplementationScenario -Step needs -Scenario <scenario id> (see ImplementationScenarioRegistry in RoslynSentinel.Tests.ModelEval)."
}

if ($Test -contains 'ShippingKindResume' -and ($ResumeStep -lt 1 -or $ResumeStep -gt 6)) {
    throw "-Test ShippingKindResume needs -ResumeStep <1-6>, the ShippingKind step the transcript came from."
}

if ($Test -contains 'ResumeTranscript' -or $Test -contains 'ShippingKindResume' -or $Test -contains 'ShippingKindReplay') {
    if (-not $ResumeTranscript) {
        throw "-Test ResumeTranscript/ShippingKindResume/ShippingKindReplay needs -ResumeTranscript <path to transcript.json or its run folder>."
    }
    if (-not (Test-Path -LiteralPath $ResumeTranscript)) {
        throw "-ResumeTranscript '$ResumeTranscript' does not exist."
    }
    # The testhost runs from the scratch build folder, so hand it an absolute path.
    $ResumeTranscript = (Resolve-Path -LiteralPath $ResumeTranscript).Path
}

if ($Test -contains 'ScoreTranscript') {
    if (-not $ScoreTranscript) {
        throw "-Test ScoreTranscript needs -ScoreTranscript <path to transcript.json or its run folder>."
    }
    if (-not (Test-Path -LiteralPath $ScoreTranscript)) {
        throw "-ScoreTranscript '$ScoreTranscript' does not exist."
    }
    # The testhost runs from the scratch build folder, so hand it an absolute path.
    $ScoreTranscript = (Resolve-Path -LiteralPath $ScoreTranscript).Path
    # -ScoreCsv may name a file that does not exist yet, so it cannot go through Resolve-Path.
    if ($ScoreCsv) {
        # GetFullPath resolves against the .NET process directory, not PowerShell's location, so anchor relative paths first.
        if (-not [System.IO.Path]::IsPathRooted($ScoreCsv)) { $ScoreCsv = Join-Path (Get-Location).Path $ScoreCsv }
        $ScoreCsv = [System.IO.Path]::GetFullPath($ScoreCsv)
    }
}

if ($Test -contains 'Scenario') {
    if (-not $Scenario) {
        throw "-Test Scenario needs -Scenario <scenario id> (see ScenarioRegistry in RoslynSentinel.Tests.ModelEval)."
    }
}

$artifactsPath = Get-RoslynSentinelBuildDir -RepoRoot $repoRoot -Suffix $suffix
# The testhost runs from <scratch>\build\..., outside the repo, so it resolves the repo and scratch root from these (RepoRootLocator and ScratchRoot read them).
$env:ROSLYNSENTINEL_REPO_ROOT = $repoRoot
$env:ROSLYNSENTINEL_SCRATCH_ROOT = $scratchRoot

if ($Test.Count -gt 1) {
    Write-Host ""
    Write-Host "=== Queuing $($Test.Count) tests in sequence against $baseUrl (model=$Model): $($Test -join ', ') ===" -ForegroundColor Magenta
}

# Central results base: <scratch>\ModelTestingResults, or ROSLYNSENTINEL_RESULTS_ROOT (absolute) when set; the queue sets it for pinned jobs.
# The helper throws on a relative ROSLYNSENTINEL_RESULTS_ROOT. Validated here because the test archivers catch and swallow their own exceptions.
$resultsBase = Get-RoslynSentinelResultsRoot -RepoRoot $repoRoot

$finalExitCode = 0
foreach ($currentTest in $Test) {
$testName = $testNames[$currentTest]

if ($Clean) {
    $modelEvalDir = Join-Path $artifactsPath 'bin\RoslynSentinel.Tests.ModelEval\debug\model-eval'
    if (Test-Path $modelEvalDir) {
        $archiveStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $archiveDir = Join-Path $resultsBase "$suffix\$archiveStamp"
        Write-Host "Archiving stale run history to $archiveDir before removing $modelEvalDir" -ForegroundColor Yellow
        New-Item -ItemType Directory -Force -Path (Split-Path $archiveDir -Parent) | Out-Null
        Move-Item -Path $modelEvalDir -Destination $archiveDir
    }
}

Write-Host ""
Write-Host "=== ModelEval: $testName against $baseUrl (model=$Model) ===" -ForegroundColor Cyan
if ($currentTest -eq 'SizeThreshold') {
    Write-Host "    ROSLYNSENTINEL_MODELEVAL_SIZES=$Size" -ForegroundColor Cyan
}
if ($MinimalTools) {
    Write-Host "    ROSLYNSENTINEL_LLM_MINIMAL_TOOLS=true (only applies to PlanImplementVerify)" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('Temperature')) {
    Write-Host "    ROSLYNSENTINEL_LLM_TEMPERATURE=$Temperature (sent on every request; LM Studio's own echoed value is checked against this - see AgentLoop warnings)" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('TopP')) {
    Write-Host "    ROSLYNSENTINEL_LLM_TOP_P=$TopP (sent on every request; verified the same way as Temperature)" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('TopK') -or $PSBoundParameters.ContainsKey('RepeatPenalty') -or $PSBoundParameters.ContainsKey('MinP')) {
    Write-Warning "TopK/RepeatPenalty/MinP CANNOT be sent or verified via this harness's /v1/responses endpoint - confirmed 2026-09-05 that LM Studio silently drops these three there (a repeat_penalty A/B test at 1.0 vs 1.8 produced byte-identical output), even though they ARE real, documented params on /v1/chat/completions. Set them by hand in LM Studio's sampling panel for the loaded model; this script cannot do it for you or confirm the UI value matches."
}
if ($HostSampler) {
    Write-Host "    ROSLYNSENTINEL_LLM_HOST_SAMPLER=$HostSampler (declared, not verifiable; recorded in transcript.json Settings)" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('StreamTimeout')) {
	Write-Host "    ROSLYNSENTINEL_LLM_STREAM_IDLE_TIMEOUT_SECONDS=$StreamTimeout" -ForegroundColor Cyan
}
Write-Host "    --artifacts-path $artifactsPath" -ForegroundColor Cyan
Write-Host ""

$env:ROSLYNSENTINEL_LLM_BASE_URL = $baseUrl
$env:ROSLYNSENTINEL_LLM_MODEL = $Model
# Child dotnet/MSBuild processes the test spawns (the fixture's dotnet restore) must not leave reusable
# MSBuild nodes behind: an orphaned node can hold a redirected pipe open and stall test SetUp for 15+ minutes.
$env:MSBUILDDISABLENODEREUSE = '1'
# Always set or cleared (never left from the shell) so a stale window cannot skew a run.
if ($ReasoningWindow -ge 0) {
    $env:ROSLYNSENTINEL_REASONING_WINDOW = "$ReasoningWindow"
    $windowShown = if ($ReasoningWindow -eq 0) { '0 (replay all reasoning)' } else { "$ReasoningWindow" }
    Write-Host "    ROSLYNSENTINEL_REASONING_WINDOW=$windowShown" -ForegroundColor Cyan
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_REASONING_WINDOW -ErrorAction SilentlyContinue
}
if ($ReasoningPressure -gt 0) {
    $env:ROSLYNSENTINEL_REASONING_PRESSURE = $ReasoningPressure.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    Write-Host "    ROSLYNSENTINEL_REASONING_PRESSURE=$($env:ROSLYNSENTINEL_REASONING_PRESSURE)" -ForegroundColor Cyan
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_REASONING_PRESSURE -ErrorAction SilentlyContinue
}
if ($PSBoundParameters.ContainsKey('Temperature')) {
    $env:ROSLYNSENTINEL_LLM_TEMPERATURE = $Temperature
}
else {
    $env:ROSLYNSENTINEL_LLM_TEMPERATURE = $null
}
if ($PSBoundParameters.ContainsKey('TopP')) {
    $env:ROSLYNSENTINEL_LLM_TOP_P = $TopP
}
else {
    $env:ROSLYNSENTINEL_LLM_TOP_P = $null
}
# Always set or cleared so a stale value from the shell cannot leak into the transcript's Settings block.
if ($HostSampler) {
    $env:ROSLYNSENTINEL_LLM_HOST_SAMPLER = $HostSampler
}
else {
    $env:ROSLYNSENTINEL_LLM_HOST_SAMPLER = $null
}
# Always set or cleared (never left from the shell) so a stale cap cannot skew a run.
if ($StepWallClockMinutes -gt 0) {
    $env:ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES = "$StepWallClockMinutes"
    Write-Host "    ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES=$StepWallClockMinutes" -ForegroundColor Cyan
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES -ErrorAction SilentlyContinue
}
if ($PSBoundParameters.ContainsKey('StreamTimeout')) {
    $env:ROSLYNSENTINEL_LLM_STREAM_IDLE_TIMEOUT_SECONDS = $StreamTimeout
}
else {
    $env:ROSLYNSENTINEL_LLM_STREAM_IDLE_TIMEOUT_SECONDS = $null
}
if ($currentTest -eq 'SizeThreshold') {
    $env:ROSLYNSENTINEL_MODELEVAL_SIZES = "$Size"
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_SIZES -ErrorAction SilentlyContinue
}
if ($currentTest -eq 'ShippingKindMappingProbe') {
    $env:ROSLYNSENTINEL_MODELEVAL_RESUME_SAMPLES = "$Samples"
    Write-Host "    ShippingKindMappingProbe samples=$Samples" -ForegroundColor Cyan
}
if ($currentTest -eq 'ShippingKindResume') {
    $env:ROSLYNSENTINEL_MODELEVAL_RESUME_STEP = "$ResumeStep"
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_RESUME_STEP -ErrorAction SilentlyContinue
}
if ($currentTest -eq 'ShippingKindReplay') {
    # Always set so an A/B pair is never skewed by a value left in the shell; the replay runs one live turn, so no -ContinueTurns.
    $env:ROSLYNSENTINEL_MODELEVAL_RESUME_LITERAL_CASE = $LiteralCase
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_RESUME_LITERAL_CASE -ErrorAction SilentlyContinue
}
if ($currentTest -in 'ResumeTranscript', 'ShippingKindResume', 'ShippingKindReplay') {
    $env:ROSLYNSENTINEL_MODELEVAL_RESUME_TRANSCRIPT = $ResumeTranscript
    $env:ROSLYNSENTINEL_MODELEVAL_RESUME_KEEP_TURNS = "$ResumeKeepTurns"
    $env:ROSLYNSENTINEL_MODELEVAL_RESUME_SAMPLES = "$Samples"
    $env:ROSLYNSENTINEL_MODELEVAL_RESUME_CONTINUE_TURNS = "$ContinueTurns"
    # Always set (not just when -LoopAssist is passed) so an A/B pair is never skewed by a variable left in the shell.
    $env:ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST = if ($LoopAssist) { '1' } else { '0' }
    $env:ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST_ROLE = $LoopAssistRole
    Write-Host "    ResumeTranscript=$ResumeTranscript keep=$ResumeKeepTurns samples=$Samples continue=$ContinueTurns loopAssist=$($LoopAssist.IsPresent) role=$LoopAssistRole" -ForegroundColor Cyan
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_RESUME_TRANSCRIPT, Env:\ROSLYNSENTINEL_MODELEVAL_RESUME_KEEP_TURNS, Env:\ROSLYNSENTINEL_MODELEVAL_RESUME_CONTINUE_TURNS -ErrorAction SilentlyContinue
    # The mapping probe reads its sample count from the same variable, set above.
    if ($currentTest -ne 'ShippingKindMappingProbe') { Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_RESUME_SAMPLES -ErrorAction SilentlyContinue }
}

if ($currentTest -eq 'ScoreTranscript') {
    $env:ROSLYNSENTINEL_MODELEVAL_SCORE_TRANSCRIPT = $ScoreTranscript
    if ($ScoreCsv) {
        $env:ROSLYNSENTINEL_MODELEVAL_SCORE_CSV = $ScoreCsv
    }
    else {
        Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_SCORE_CSV -ErrorAction SilentlyContinue
    }
    $env:ROSLYNSENTINEL_MODELEVAL_SCORE_TOOL = $ScoreTool
    $csvShown = if ($ScoreCsv) { $ScoreCsv } else { '<default: score.csv beside the transcript>' }
    Write-Host "    ScoreTranscript=$ScoreTranscript csv=$csvShown tool=$ScoreTool" -ForegroundColor Cyan
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_SCORE_TRANSCRIPT, Env:\ROSLYNSENTINEL_MODELEVAL_SCORE_CSV, Env:\ROSLYNSENTINEL_MODELEVAL_SCORE_TOOL -ErrorAction SilentlyContinue
}

if ($currentTest -eq 'Scenario') {
    $env:ROSLYNSENTINEL_MODELEVAL_SCENARIO = $Scenario
    $env:ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES = "$Samples"
    # Always set or cleared (never left from the shell) so a stale cap cannot skew a run.
    if ($TurnCap -gt 0) {
        $env:ROSLYNSENTINEL_MODELEVAL_TURN_CAP = "$TurnCap"
    }
    else {
        Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_TURN_CAP -ErrorAction SilentlyContinue
    }
    $capShown = if ($TurnCap -gt 0) { "$TurnCap" } else { '<scenario default>' }
    Write-Host "    Scenario=$Scenario samples=$Samples turnCap=$capShown" -ForegroundColor Cyan
}
else {
    Remove-Item Env:\ROSLYNSENTINEL_MODELEVAL_SCENARIO, Env:\ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES, Env:\ROSLYNSENTINEL_MODELEVAL_TURN_CAP -ErrorAction SilentlyContinue
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

$modelEvalRoot = Join-Path $artifactsPath "bin\RoslynSentinel.Tests.ModelEval\debug\model-eval"
$resultsRoot = Join-Path $resultsBase $suffix
$resultsDir = Join-Path $resultsRoot $testName

function Get-RunLeafDirs {
    # Each individual run's own directory is a UTC-timestamp-named leaf (yyyyMMdd-HHmmss[-fff]).
    # MinimalGuidance lays these out flat (model-eval\<TestName>\<timestamp>\); SizeThreshold
    # nests them one level deeper (model-eval\SizeThreshold\n<size>\<timestamp>\) - matching by
    # name pattern rather than a fixed depth handles both without hardcoding either shape.
    # The ShippingKindStep* tests are TestCase rows, so their folders are model-eval\<TestName>(<case>)\<timestamp>\:
    # scan every folder named <TestName> or <TestName>(...) (the "(" keeps ...V1 apart from ...V10).
    if (-not (Test-Path -LiteralPath $modelEvalRoot)) { return @() }
    $testRoots = @(Get-ChildItem -LiteralPath $modelEvalRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $testName -or $_.Name.StartsWith("$testName(", [System.StringComparison]::Ordinal) })
    foreach ($testRoot in $testRoots) {
        Get-ChildItem -LiteralPath $testRoot.FullName -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d{8}-\d{6}' } |
            Select-Object -ExpandProperty FullName
    }
}

function Copy-NewRunDirectories {
    param([string[]]$Before)

    $after = Get-RunLeafDirs
    $new = $after | Where-Object { $_ -notin $Before }
    foreach ($runPath in $new) {
        # Relative to model-eval\ (keeps the <TestName> or <TestName>(<case>) folder), so the layout under
        # <scratch>\ModelTestingResults\<suffix>\ matches what the test project's own ModelTestingResultsArchiver writes.
        $relative = $runPath.Substring($modelEvalRoot.Length).TrimStart('\')
        $dest = Join-Path $resultsRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Write-Host "Archiving run to $dest" -ForegroundColor DarkGray
        # Copy-Item of a folder onto an EXISTING folder nests it (<ts>\<ts>\...). $dest already exists
        # when something else archived this run first (the test project's own
        # ModelTestingResultsArchiver also writes <scratch>\ModelTestingResults\<suffix>\...), so copy the
        # contents in that case; a missing $dest is created by the plain folder copy.
        if (Test-Path -LiteralPath $dest) {
            Copy-Item -Path (Join-Path $runPath '*') -Destination $dest -Recurse -Force
        }
        else {
            Copy-Item -Path $runPath -Destination $dest -Recurse -Force
        }
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
    $testFilter = "Name=$testName"
    if ($currentTest -like 'ShippingKindStep*') {
        # Five TestCase rows share one method name: Name~ selects all five, -Step narrows to the listed cases.
        # Parentheses are filter syntax, so the case name's own ( and ) are escaped.
        $testFilter = if ($Step) {
            (($Step | Sort-Object -Unique | ForEach-Object { "Name=$testName\($_\)" }) -join '|')
        }
        else {
            "Name~$testName"
        }
    }
    elseif ($currentTest -eq 'ImplementationScenario') {
        # SetName replaces the method name in the VSTest name, so the filter matches ImplScenario_<Id>_Step<k>, not $testName.
        $testFilter = if ($Scenario -and $Step) {
            (($Step | Sort-Object -Unique | ForEach-Object { "Name~ImplScenario_$($Scenario)_Step$_" }) -join '|')
        }
        elseif ($Scenario) {
            "Name~ImplScenario_$($Scenario)_"
        }
        else {
            "Name~ImplScenario_"
        }
    }
    & dotnet test $csproj -c Debug `
        --artifacts-path $artifactsPath `
        --filter $testFilter `
        --logger "console;verbosity=detailed"
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($exitCode -ne 0) {
        Write-Warning "Run $i of $Repeats failed (dotnet test exit code $exitCode) - archiving it anyway and continuing."
    }

    Copy-NewRunDirectories -Before $before

    if ($currentTest -eq 'Scenario') {
        # Advisory only: the per-scenario summary must never fail a run, so any error is a warning.
        try {
            . (Join-Path $PSScriptRoot 'Get-ScenarioSummary.ps1')
            $newestCsv = Get-ChildItem -Path $resultsDir -Recurse -Filter results.csv -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($newestCsv) {
                Write-ScenarioSummary -CsvPath $newestCsv.FullName
            }
            else {
                Write-Warning "No results.csv under $resultsDir - scenario summary skipped."
            }
        }
        catch {
            Write-Warning "Scenario summary failed (run results are unaffected): $($_.Exception.Message)"
        }
    }

    if ($i -lt $Repeats) {
        Wait-ForTesthostExit
    }
}

if ($exitCode -ne 0) {
    $finalExitCode = $exitCode
}

if ($Test.Count -gt 1 -and $currentTest -ne $Test[-1]) {
    Wait-ForTesthostExit
}
}

if ($Test.Count -gt 1) {
    Write-Host ""
    Write-Host "=== All $($Test.Count) queued tests finished (final exit code $finalExitCode) ===" -ForegroundColor Magenta
}

exit $finalExitCode

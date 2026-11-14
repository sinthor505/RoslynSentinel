# Plan: Stepped ShippingKind ModelEval scenario on ContosoOrders with a golden commit per step

**Status:** IN PROGRESS 2026-11-13. Steps 1-7b done (f0f03fbf, 5977fe75, d9bf2b51, ec7881b6, f1d68375, 59a0332a, 97a5a63f); Steps 8-9 left.

## Problem

- The only multi-step model tests are the whole-plan chains in
  `RoslynSentinel.Tests.ModelEval/OrderPricingRefactorChainAgentTests.cs` (one prompt, ten steps, one
  conversation) and PlanStepRunner. Neither gives a cheap, repeatable per-step measurement.
- A chain run does not separate "cannot do step k" from "skipped step k"; a failed early step makes the
  later steps unmeasurable (`docs/current/ideas/idea_modeleval_stepped_prompting.md`, "The actual gap").
- PlanStepRunner is tied to this repo: a git worktree per step (`Program.cs:67,81`), the server built
  from the worktree (`Program.cs:180-181`) and `<worktree>/RoslynSentinel.slnx` (`Program.cs:186`).
  One step has hit the 60-turn cap at 24m18s
  (`docs/current/evals/closed/eval_run398_defect_remediation_a1_a5.md:11`). A weak model also has the
  whole repo to get lost in.
- `Samples/ContosoOrders` is small (5 files, 134 lines in `ContosoOrders.Core`) but has nothing to
  migrate: no shipping code and no logic on `OrderStatus`.

## Decision

Add one ModelEval scenario that copies the shape of the MissingTargetKinds change (`d80dfffc` added a
constants vocabulary in `RoslynSentinel.Common/MissingTargetKinds.cs`; `69c34693` and five sibling
commits moved callers from string literals to the constants) into a ContosoOrders fixture copy as a
"shipping kind" vocabulary.

- **Five small steps, one fresh conversation each.** Each step is one NUnit test case, so a step, a
  range ("part 2" = steps 4-5) or all five are selected with an ordinary test filter.
- **Golden commit per step.** At the start of a test the harness turns the fixture copy into a git
  repo and commits the expected state after each step, tagged `step-0` (seed) to `step-5`. Step k
  starts from tag `step-(k-1)`, never from the model's own earlier output.
- **A fresh in-process server per test case**, as the chain tests already do (a new `Host` per test in
  `[SetUp]`). The checkout happens before `LoadSolutionAsync`, so the server never sees a harness write.
- **Grading:** behaviour and text checks gate the outcome. The diff against the golden tag is saved as
  a report only; an exact-diff gate would fail correct answers that differ in layout.
- **A no-model self-test** proves each golden passes its own check and the previous golden does not.

### Decisions 2026-11-13 (implementation review)

- Script selection: `-Test ShippingKindStep` in `scripts/roslynsentinel-modeleval.ps1` plus a `-Step`
  list (new Step 7b). Owner choice.
- Self-test: one shared fixture for all nine cases; `CheckAsync` deletes the check-test file it wrote
  in a `finally`. Owner choice.
- Run mode: Batched, one orchestrator for Steps 1-7b; the main session runs Steps 8-9. Owner present.
- Blocked-stop detection in Step 6 uses `RoslynSentinel.Common.AgentLoop.StopMarker` (public);
  `ScenarioScorer.HasBlockedMarker` is private and stays so.
- The fixture repo ignores `.roslynsentinel/` (the server offloads large results to
  `.roslynsentinel/largeresults/` under the solution root, `LargeResultHelper.cs:80`).
- `DotnetTestRunner.RunAsync` throws `InvalidOperationException` when no test summary line appears
  (`DotnetTestRunner.cs:55-58`, build error or failed start); `CheckAsync` maps it to `BuildBroken`.
- 2026-11-13, after the Step 8 run: steps 4-5 text checks also accept a
  `using static ...ShippingKinds;` directive with bare names (and a bare `IsExpedited(` call), and the
  step 4-5 prompts ask for the qualified `ShippingKinds.<Name>` form. Owner chose both. The Step 8
  CheckFailed on step 4 was this check gap: the edit built green and left no literals.

### Seed (tag `step-0`)

New files under `ContosoOrders.Core/Shipping/`, in the namespace `ContosoOrders.Core/OrderStatus.cs`
declares. Kinds are the four lower-case strings below; every comparison is exact (`==` or a `case`
label).

| Kind | Fee for weight `w` kg | Days | Carrier | Valid when |
| --- | --- | --- | --- | --- |
| `"standard"` | `4.99m + 0.50m * w` | 5 | `"GroundPost"` | `w > 0` |
| `"express"` | `9.99m + 0.75m * w` | 2 | `"AirFast"` | `w > 0` |
| `"overnight"` | `19.99m + 1.00m * w` | 1 | `"AirFast"` | `w > 0` |
| `"pickup"` | `0m` | 0 | `"none"` | `w >= 0` |
| anything else | throws `ArgumentException` | throws `ArgumentException` | throws `ArgumentException` | `false` |

| File | Member | Shape (fixes the literal count) | Kind literals |
| --- | --- | --- | --- |
| `Shipment.cs` | `public sealed record Shipment(string OrderId, string Kind, decimal WeightKg);` | - | 0 |
| `ShippingFeeCalculator.cs` | `public static decimal Calculate(Shipment shipment)` | `switch (shipment.Kind)` with four `case` labels | 4 |
| `ShipmentValidator.cs` | `public static bool IsValid(Shipment shipment)` | a four-way `==` chain for "known kind", then `shipment.Kind == "pickup"` for the weight rule | 5 |
| `DeliveryEstimator.cs` | `public static int EstimateDays(string kind)` | `if (kind == "express" \|\| kind == "overnight") return kind == "overnight" ? 1 : 2;` then one `if` each for pickup and standard | 5 |
| `CarrierSelector.cs` | `public static string Select(Shipment shipment)` | one `if` for pickup, one `if (... == "express" \|\| ... == "overnight")`, one `if` for standard | 4 |

Total: 18 kind literals in 4 files. The carrier strings are deliberately not kinds.

### Steps the model is given

| Step | Prompt (final wording) | Golden change | Allowed paths |
| --- | --- | --- | --- |
| 1 | "The files under ContosoOrders.Core/Shipping compare shipping kinds as string literals (for example "express"). Find every such comparison or case label. Do not change any file. Reply with one line per occurrence: file name, method name, literal." | none (`step-1` tags the `step-0` commit) | none |
| 2 | "Create ContosoOrders.Core/Shipping/ShippingKinds.cs with a public static class ShippingKinds in namespace `<ns>`. It has four public const string fields: Standard = "standard", Express = "express", Overnight = "overnight", Pickup = "pickup", and a public static `IReadOnlySet<string>` property All holding the four values. Change no other file. Reply with what you created." | adds `ShippingKinds.cs` | `ShippingKinds.cs` |
| 3 | "Add three public static methods to ShippingKinds (ContosoOrders.Core/Shipping/ShippingKinds.cs). bool IsKnown(string? kind): true when kind is exactly one of the four constants. bool IsExpedited(string? kind): true for Express or Overnight. string Normalize(string kind): trims and lower-cases kind, returns it when IsKnown, otherwise throws ArgumentException. Change no other file. Reply with what you changed." | edits `ShippingKinds.cs` | `ShippingKinds.cs` |
| 4 | "In ShippingFeeCalculator.cs and ShipmentValidator.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant. Behaviour must not change. Change no other file. Reply with the number of literals replaced per file." | 9 literals replaced | those 2 files |
| 5 | "In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant, and replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. Behaviour must not change. Change no other file. Reply with what you changed." | 9 literals replaced, 2 `IsExpedited` calls | those 2 files |

`IsKnown` and `IsExpedited` are case-sensitive on purpose: the seed compares with `==`, so the
migration in steps 4-5 preserves behaviour for every input. `All` uses the default comparer.

### Check per step

| Step | State check |
| --- | --- |
| 1 | No path changed since `step-0`; the final message names all four seed files that hold literals |
| 2 | Check tests pass: the four constants have the table values; `All` has exactly those four |
| 3 | Check tests pass: `IsKnown("express")` true, `IsKnown("drone")` false, `IsKnown(null)` false, `IsExpedited("overnight")` true, `IsExpedited("standard")` false, `Normalize(" Express ")` is `"express"`, `Normalize("drone")` throws `ArgumentException` |
| 4 | Text: neither file contains any of the four kind literals in quotes; both contain `ShippingKinds.`. Check tests pass: the Fee and Valid columns of the seed table, including the unknown-kind row |
| 5 | Text: as step 4 for its two files, each contains `ShippingKinds.IsExpedited(`. Check tests pass: the Days and Carrier columns, including the unknown-kind row |

Check tests are xUnit sources the harness writes to
`ContosoOrders.Tests/ModelEvalGenerated/ShippingKindStep<k>Tests.cs` only after the model has stopped
(the folder convention is `ScenarioFixtureFiles.CheckoutTestsRelativePath`,
`RoslynSentinel.Tests.ModelEval/ScenarioFixtureFiles.cs:14`), then runs with
`DotnetTestRunner.RunAsync`. The model never sees them.

### Outcome classes (one per test case run)

| Class | Meaning |
| --- | --- |
| `Pass` | Converged, state check green, only allowed paths changed |
| `OutOfScopeEdit` | State check green, but a path outside the step's allowed list changed (step 1: any path) |
| `CheckFailed` | The check tests build, but a text or behaviour check is red |
| `BuildBroken` | The check test project does not build after the model stops |
| `Blocked` | Converged with a blocked-stop final message and the state check is not green |
| `NotConverged` | `AgentRunResult.Converged` is false; the detail carries `StopReason` and the state-check class the tree would have earned |

Turns, wall time and `StopReason` are recorded per step. No turn ideal is set yet (see Risks).

## Execution rules

- All new code is in `RoslynSentinel.Tests.ModelEval`. No server, engine, tool or sample-solution file
  changes; `Samples/ContosoOrders` on disk stays as it is (about 180 other fixture constructions copy it).
- `.cs` reads and writes through the MCP tools. One `Build`/`RunTest` at a time.
- Implementers never run tests; the dispatcher runs the named tests.
- Tier limits per `.claude/implementer-tiers.json` (depth tier 1: 3 files, 15 edits per slice).
- No new record/class that only carries data: the check returns a tuple, and git plumbing reuses
  `GitTestRepo` (reachable through the `RoslynSentinel.Tests` project reference;
  namespace `RoslynSentinel.Tests.Battery.Basic`).

## Steps

### Step 1 - Contract skeleton
- Done: f0f03fbf
- Files (all new, one `CreateFile` each, in this order):
  `RoslynSentinel.Tests.ModelEval/GoldenStepRepository.cs`,
  `RoslynSentinel.Tests.ModelEval/Fixtures/ShippingKindStepGoldens.cs`,
  `RoslynSentinel.Tests.ModelEval/ShippingKindStepChecks.cs`
- Depends on: none
- Effect: text-neutral (new members, no callers)
- Change: land these signatures with `throw new NotImplementedException(); // TODO implement (Step <n>)` bodies.
  - `internal sealed class GoldenStepRepository` (new):
    - `public static GoldenStepRepository Create(string repoDirectory, int lastStep, Func<int, IReadOnlyDictionary<string, string>> filesForStep)` (Step 3)
    - `public string RepoDirectory { get; }`
    - `public static string TagForStep(int step)` returning `"step-<n>"` (Step 3)
    - `public void CheckoutStep(int step, string branchName)` (Step 3)
    - `public IReadOnlyList<string> ChangedPathsSinceStep(int step)` (Step 3)
    - `public string CommitWorkingTree(string subject)` (Step 3)
    - `public string DiffAgainstStep(int step)` (Step 3)
  - `internal static class ShippingKindStepGoldens` (new):
    - `public const int LastStep = 5;`
    - `public static IReadOnlyDictionary<string, string> FilesForStep(int step)` - solution-relative path to full content, only the files that step adds or replaces; step 0 is the seed; step 1 is empty (Step 2)
    - `public static string PromptForStep(int step)` (Step 2)
    - `public static IReadOnlyList<string> AllowedPathsForStep(int step)` (Step 2)
  - `internal enum ShippingKindStepOutcome { Pass, OutOfScopeEdit, CheckFailed, BuildBroken, Blocked, NotConverged }` (new)
  - `internal static class ShippingKindStepChecks` (new):
    - `public static Task<(ShippingKindStepOutcome Outcome, string Detail)> CheckAsync(int step, string solutionDirectory, string? finalMessage, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)` (Step 4)
- Done when: `Build` of `RoslynSentinel.Tests.ModelEval` has 0 errors.
- Lane: `implementer`

### Step 2 - Seed, golden files and prompts
- Done: 5977fe75
- Files: `RoslynSentinel.Tests.ModelEval/Fixtures/ShippingKindStepGoldens.cs`
- Depends on: Step 1
- Effect: text-neutral
- Change: implement the three members from the "Seed", "Steps the model is given" and golden-change
  columns above. File content as raw string literals, following the string-constant convention of
  `Fixtures/OrderPricingRefactorReproducer.cs`. Read the namespace from
  `Samples/ContosoOrders/ContosoOrders.Core/OrderStatus.cs` and substitute it for `<ns>` in the step 2
  prompt. Step 5 golden for `DeliveryEstimator`: `if (ShippingKinds.IsExpedited(kind)) return kind == ShippingKinds.Overnight ? 1 : 2;`.
  CRLF line endings in every golden file.
- Done when: `Build` of `RoslynSentinel.Tests.ModelEval` has 0 errors; no `NotImplementedException` left in the file.
- Lane: `implementer` (pairs with Step 3; disjoint files)

### Step 3 - GoldenStepRepository
- Done: d9bf2b51
- Files: `RoslynSentinel.Tests.ModelEval/GoldenStepRepository.cs`
- Depends on: Step 1
- Effect: text-neutral
- Change: implement on top of `GitTestRepo` (`RoslynSentinel.Tests.Support.Basic/GitTestRepo.cs`):
  `public static void InitRepo(string directory)` (line 33),
  `public static string RunGit(string workingDirectory, params string[] args)` (line 41),
  `public static string CommitAll(string workingDirectory, string subject, string? trailers = null)` (line 61).
  - `Create`: `InitRepo`; set `core.autocrlf false`; write a `.gitignore` with `bin/`, `obj/`, `.vs/`,
    `.roslynsentinel/` (the fixture has already run a package restore, so `obj/` exists); commit the
    sample as it is; then for each step 0..`lastStep` write that step's files, `CommitAll`, and tag the
    commit `step-<n>` (lightweight tag). A step with no files tags the current commit without
    committing (`CommitAll` passes `--allow-empty`, so do not call it then).
  - `CheckoutStep`: `git checkout -B <branchName> step-<n>`.
  - `ChangedPathsSinceStep`: `git add -A`, then `git diff --cached --name-only step-<n>`, so new,
    deleted and model-committed changes all count.
  - `CommitWorkingTree`: `CommitAll`, returns what it returns; tolerates "nothing to commit".
  - `DiffAgainstStep`: `git diff step-<n> HEAD`.
- Done when: `Build` of `RoslynSentinel.Tests.ModelEval` has 0 errors; no `NotImplementedException` left in the file.
- Lane: `implementer` (pairs with Step 2)

### Step 4 - Step checks and check-test sources
- Done: ec7881b6
- Files: `RoslynSentinel.Tests.ModelEval/ShippingKindStepChecks.cs`
- Depends on: Step 1 (content follows the "Check per step" table)
- Effect: text-neutral
- Change: implement `CheckAsync`. It returns only `Pass`, `OutOfScopeEdit`, `CheckFailed` or
  `BuildBroken`; the caller maps `Blocked` and `NotConverged`.
  - Scope: any entry of `changedPaths` outside `ShippingKindStepGoldens.AllowedPathsForStep(step)`
    turns a green result into `OutOfScopeEdit`.
  - Step 1: no test run; the final-message and no-change checks only.
  - Steps 2-5: write the step's xUnit source (private raw string constants in this file) to
    `ContosoOrders.Tests/ModelEvalGenerated/ShippingKindStep<k>Tests.cs`, then call
    `public static async Task<DotnetTestResult> RunAsync(string testProjectPath, CancellationToken cancellationToken)`
    (`DotnetTestRunner.cs:36`) on `ContosoOrders.Tests/ContosoOrders.Tests.csproj`. Result type:
    `internal sealed record DotnetTestResult(int Passed, int Failed, int Skipped, int Total, int ExitCode, string RawOutput);`.
    `RunAsync` throws `InvalidOperationException` when the build fails (`DotnetTestRunner.cs:55-58`):
    catch it and return `BuildBroken` with the first line of the message. Delete the written test file
    in a `finally` so a shared fixture stays clean for the next check.
  - Text checks read the files from `solutionDirectory`.
  - `Detail` names the first failing check in one sentence.
- Done when: `Build` of `RoslynSentinel.Tests.ModelEval` has 0 errors; no `NotImplementedException` left in the file.
- Lane: `implementer`

### Step 5 - No-model golden self-test
- Done: f1d68375 (self-test 9/9 green)
- Files: `RoslynSentinel.Tests.ModelEval/ShippingKindStepGoldenTests.cs` (new)
- Depends on: Steps 2, 3, 4
- Effect: text-neutral
- Change: `[TestFixture]` class `ShippingKindStepGoldenTests` (new), marked like the existing no-model
  tests (`ResumeDriftProbeTests.cs:19`: `[Explicit]` with a "No model needed" reason; see Risks).
  One shared fixture: `[OneTimeSetUp]` builds `new TestSolutionFixture(restorePackages: true)` and
  `GoldenStepRepository.Create(fixture.SolutionDirectory, ShippingKindStepGoldens.LastStep, ShippingKindStepGoldens.FilesForStep)`,
  and calls `Assert.Ignore` when `GitTestRepo.IsGitAvailable()` returns false; `[OneTimeTearDown]`
  disposes the fixture.
  - `Golden_PassesItsOwnCheck(int step)`, cases 1-5: check out `step-<k>`, pass the paths that differ
    between `step-<k-1>` and `step-<k>` (and for step 1 a canned reply naming the four files); expect `Pass`.
  - `PreviousGolden_FailsTheCheck(int step)`, cases 2-5: check out `step-<k-1>`, no changed paths;
    expect anything but `Pass`.
- Done when: the dispatcher runs `ShippingKindStepGoldenTests` (both methods, all cases) green.
- Outcome classes: pass, or the failing case names the step and the `Detail` text.
- Lane: `test-writer` (pairs with Step 6; disjoint files)

### Step 6 - Model test class
- Done: 59a0332a
- Files: `RoslynSentinel.Tests.ModelEval/ShippingKindSteppedAgentTests.cs` (new)
- Depends on: Steps 2, 3, 4
- Effect: behaviour-changing for eval runs only: five new explicit test cases
- Change: `[TestFixture]` class `ShippingKindSteppedAgentTests` (new).
  - `[SetUp]`: same host construction as `OrderPricingRefactorChainAgentTests` SetUp (lines 85-228:
    `LlmOptions.Configure([])` with `Assert.Ignore` when no model, `new TestSolutionFixture(restorePackages: true)`,
    the in-memory pipe pair, the `FlushingFileLoggerProvider`, `AddLmStudioAgentClient`,
    `AddRoslynSentinelEnginesAdvanced()`,
    `AddRoslynSentinelToolsAdvanced(services, ChainEvalToolSurface.NewModes(), hideTools: ChainEvalToolSurface.NewHiddenTools())`,
    `_host.RunAsync()`, `McpClient.CreateAsync`; see lines 140-155), without the OrderPricing staging
    and without loading the solution.
    Leave `// TODO extract the shared ModelEval host setup (third copy)` at the top of the SetUp.
  - `[TearDown]`: copy `OrderPricingRefactorChainAgentTests` TearDown (lines 230-250): dispose client,
    host and fixture, and `ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory)`.
  - `[TestCase(1)]` ... `[TestCase(5)]`, `[Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]`
    `public async Task Model_CompletesShippingKindStep(int step)`:
    1. `GoldenStepRepository.Create(...)`, then `CheckoutStep(step - 1, "run/step-<k>")`.
    2. `workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, ...)` as the chain SetUp does.
    3. `new ModelAgentRunner(_agentClient, _mcpClient, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: 25, wallClockCap: TimeSpan.FromMinutes(15), logger: ...)`
       and `RunAsync(ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent), ShippingKindStepGoldens.PromptForStep(step), _runDirectory, cancellationToken)`.
    4. `ChangedPathsSinceStep(step - 1)`, then `CommitWorkingTree`, then write `DiffAgainstStep(step)`
       to `step-<k>.golden.diff` in the run directory. All three happen before the check writes its test file.
    5. `ShippingKindStepChecks.CheckAsync(...)` with `result.Transcript.FinalMessage()`.
    6. Map the outcome: not `result.Converged` -> `NotConverged` (detail: `StopReason` plus the
       state-check class); converged, check not green and a blocked-stop final message -> `Blocked`
       (`StopMarker.IsLeading` / `StopMarker.ContainsAnywhere`, public in
       `RoslynSentinel.Common/AgentLoop/StopMarker.cs`, the same test `ScenarioScorer` applies); otherwise
       the check's class.
    7. Record outcome class, `TurnCount`, wall time and `StopReason` (TestContext output and a line in
       the run directory); the TearDown archives the run. `Assert.That` the outcome is `Pass`, with the
       detail as the message.
- Done when: `Build` of `RoslynSentinel.Tests.ModelEval` has 0 errors.
- Lane: `implementer-senior` (about 140 copied SetUp lines plus a TearDown exceed the implementer budget)

### Step 7b - Script selection
- Done: 97a5a63f
- Files: `scripts/roslynsentinel-modeleval.ps1`
- Depends on: Step 6 (test name)
- Effect: behaviour-changing for the script only (new `-Test` value and parameter)
- Change: add `ShippingKindStep` to `$testNames` (around lines 331-353) mapped to
  `Model_CompletesShippingKindStep`; the filter for it is `Name~Model_CompletesShippingKindStep`
  instead of `Name=` (line 589). New optional `-Step` int array: when given with `ShippingKindStep`,
  the filter becomes an OR of `Name=Model_CompletesShippingKindStep(<k>)` per step. Other `-Test`
  values unchanged.
- Done when: the script parses (`[System.Management.Automation.Language.Parser]::ParseFile` reports no
  errors) and a dry print of the composed filter for `-Test ShippingKindStep -Step 4,5` shows the two
  cases (if the script has no dry-run switch, the orchestrator checks the composed string by reading).
- Lane: orchestrator direct edit (non-C# file)

### Step 7 - Stub sweep
- Done: no commit (0 NotImplementedException hits)
- Files: the five new files
- Depends on: Steps 2-6
- Effect: text-neutral
- Change: none expected.
- Done when: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Tests.ModelEval/**/*.cs")`
  returns no hit in the five new files.
- Lane: dispatcher (a plain call)

### Step 8 - LIVE first run
- Done: 2026-11-13, qwen3.5-9b-mtp on host 113, commit 0487eedb, default 18-tool surface
  (`ROSLYNSENTINEL_MODELEVAL_TOOLSET` unset), queue job 20261114-061415-001, 4m41s total.
  Self-test 9/9 green. Solution-relative prompt paths resolved in every tool; no load-ordering issue.

  | Step | Outcome | Turns | Wall | StopReason |
  | --- | --- | --- | --- | --- |
  | 1 | Pass | 7 | 32 s | ModelFinished |
  | 2 | Pass | 4 | 22 s | ModelFinished |
  | 3 | Pass | 9 | 36 s | ModelFinished |
  | 4 | CheckFailed (check gap: `using static` + bare names; fixed after the run) | 7 | 45 s | ModelFinished |
  | 5 | Pass | 8 | 48 s | ModelFinished |
- Files: none
- Depends on: Steps 5, 6, 7, 7b
- Effect: none on source
- Change: run `ShippingKindStepGoldenTests` (no model). Then one pass of the five
  `Model_CompletesShippingKindStep` cases with `scripts/roslynsentinel-modeleval.ps1 -Test ShippingKindStep`
  (`-Model` is required), submitted through `scripts/roslynsentinel-evalqueue.ps1` so the host shows busy;
  procedure in `docs/current/references/reference_model_eval_procedure.md`.
- Done when: the self-test is green and a five-row table is reported: step, outcome class, turns,
  wall time, `StopReason`.
- Outcome classes: the six in "Outcome classes"; a harness fault (setup throws, git missing) is
  reported separately as `HarnessError`, not as a model outcome.
- Lane: dispatcher, with `modeleval-runner` for the model pass

### Step 9 - Document the scenario
- Files: `docs/current/references/reference_model_eval_procedure.md`
- Depends on: Step 8
- Effect: text-neutral
- Change: add a short section: what the scenario measures, the filter for one step and for steps 4-5,
  where the per-step diff lands, and the self-test to run after changing a golden.
- Done when: the section exists and `scripts/Test-GuidanceHygiene.ps1` reports nothing new.
- Lane: dispatcher (direct edit)

## Out of scope

- Chained mode (step k starts from the model's own step k-1 result, reset to the golden only after a
  failure). The golden tags make it possible later; it needs a loop inside one test.
- One conversation across steps, as `idea_modeleval_stepped_prompting.md` describes.
  `ModelAgentRunner.RunAsync` builds a new message list per call and takes one user prompt.
- Grading the model's free-text counts (step 1 list, step 4 "number replaced"). They are in the
  transcript; only the file names in step 1 are checked.
- Any change to PlanStepRunner, to `TestSolutionFixture`, or to `Samples/ContosoOrders` on disk.
- Extracting the duplicated ModelEval host setup into a shared helper (Step 6 leaves a `// TODO`).
- An env-var turn-cap override. `ROSLYNSENTINEL_MODELEVAL_TURN_CAP` is read by
  `ScenarioDefinition.EffectiveTurnCap` (`ScenarioDefinition.cs:82`) for scenario tests only; the chain
  tests use a literal too.

## Risks and open decisions

Decisions 1-4 were settled by the owner on 2026-11-13: option A for 1 and 2, and 3 and 4 as recommended.

1. **Goldens as C# string constants or as files on disk.**
   - A (chosen): raw string constants in `Fixtures/ShippingKindStepGoldens.cs`. Matches the
     `Fixtures/*Reproducer.cs` convention, needs no csproj change and no runtime path lookup.
   - B: one folder per step with real `.cs` files. Easier to read and diff, but the files must be
     excluded from compilation and copied to the output directory (the csproj has only one
     `<None ... CopyToOutputDirectory>` item today), and they sit outside any loaded project.
2. **Self-test visibility.**
   - A (chosen): `[Explicit]` "No model needed", like `ResumeDriftProbeTests` and
     `SavedTranscriptScoringTests`; Step 8 and the reference doc say to run it after any golden change.
   - B: in the default run. It would catch drift without anyone remembering, but it needs a package
     restore and about eight `dotnet test` runs on the fixture copy. Run time is not measured.
3. **Independent steps only.** Every step starts from a golden, so a run never shows error
   accumulation across steps. The ten-chain still covers that. Recommended: accept for the first version.
4. **No turn ideal yet.** `reference_model_eval_procedure.md` ("Scoring design") asks for an ideal
   calibrated from data. The first runs record turns per step; set an ideal and add a "slow" class
   after about 10 samples per step.
5. **Unverified, to check during the named step** (2026-11-13 review answered the `DotnetTestRunner`,
   marker-test, archiver, `CommitAll`/`InitRepo` and Git-tool items: see "Decisions 2026-11-13";
   `InitRepo` sets no identity, `CommitAll` passes one per call; `Git` is hidden on both chain
   surfaces. Still open: the `LoadSolutionAsync` ordering, and whether tools resolve the prompts'
   solution-relative paths such as `ContosoOrders.Core/Shipping`; the chain prompts pass absolute
   paths. Step 8 shows both.)
   - The `LoadSolutionAsync` ordering is new: here the repo is created and checked out before the
     solution is loaded. The chain tests load first and stage after. Hypothesis: loading after the files
     exist is the simpler case. (Step 6)
   - How `DotnetTestRunner.RunAsync` reports a compile failure (`Total == 0` with a non-zero
     `ExitCode` is a guess). (Step 4)
   - The blocked-stop marker test in `ScenarioScorer.cs` and the archiver calls in the ten-chain test
     were not read. (Step 6)
   - `CommitAll` behaviour with nothing to commit, and whether `InitRepo` sets a user identity. (Step 3)
   - Whether the `Git` tool is on `ChainEvalToolSurface`. If it is, the model can commit in the
     fixture repo; `ChangedPathsSinceStep` compares against the tag, so that is harmless.
6. **Cross-plan dependency.** `docs/current/plans/plan_modeleval_lean_tool_surface.md` is in progress
   and changes ModelEval tool surfaces. This scenario has no baseline to break, but its first numbers
   are only comparable with runs on the same surface. Record the surface with the Step 8 results.
7. **Existing TODO.** The `TestSolutionFixture` constructor carries a TODO noting ModelEval fixtures
   are not git repos. This plan makes one fixture copy a repo from the test, not from the fixture;
   the TODO stays.
8. **Sample build.** `docs/current/evals/eval_scenario_eval_followups.md:238` reports the
   `ContosoOrders.Tests` project failing a solution-scope build (missing Xunit). The chain tests use
   `restorePackages: true` and a project-scoped `DotnetTestRunner.RunAsync`; this plan does the same.

# Plan: Standing workflow contract behind a switch, ScenarioTask record and renderer, contract stamp, validateOnApply hide switch (proposal Phasing step 1)

**Status:** DRAFT 2026-11-23 (restructured after the owner's decisions on the proposal's Scenario authoring rules and Graders). Phasing step 1 of [proposal_modeleval_standing_workflow_contract.md](../proposals/proposal_modeleval_standing_workflow_contract.md); nothing built.

## Problem

The eval harness gives a model only the system prompt (`AgentSystemPrompts.CodingAgent`, RoslynSentinel.AgentLoop/AgentSystemPrompts.cs:21,
composed by `ErrorPolicyWiring.ComposePrompt`) and the scenario's task prompt. When a scenario expects a process (stop, leave the tree
unchanged, report) that neither prompt states, models latch onto the edit itself (WideningRejectedByGate: gate rejection, resend with
`validateOnApply: false`, graded BuildBroken). Evidence and the full draft contract are in the proposal; its Decisions table is settled
and is not reopened here.

This plan builds only the machinery the later audit, rework and A/B need, with today's behaviour as the default: the new contract text
beside the old, a record that renders task prompts in a fixed shape, a contract hash stamped next to `ErrorPolicyHash`, a switch that hides
only `validateOnApply` on the eval tool surface (with a per-scenario opt-in), and the model-free tests for all of it. It changes no scenario
prompt and no grader. Auditing and reworking the first-slice scenarios, controls and the A/B are separate work (see "Out of scope / next").

## Decision

1. **Selection switch.** Two environment variables, read by a new harness class `EvalContractOptions` (RoslynSentinel.Tests.Harness,
   namespace `RoslynSentinel.Tests.ModelEval`, the same home as `EvalRunStamp`). This is the mechanism the harness already uses for
   run-level options (`ROSLYNSENTINEL_MODELEVAL_EDIT_ARM` in `EditArmToolSurface`, `ROSLYNSENTINEL_MODELEVAL_TOOLSET` in
   `ChainEvalToolSurface`; an evalqueue job records every `ROSLYNSENTINEL_*` variable of the submitting shell, reference_model_eval_procedure.md
   line 241):
   - `ROSLYNSENTINEL_MODELEVAL_CONTRACT`: unset, empty or `legacy` = `AgentSystemPrompts.CodingAgent` (default, unchanged); `standing` = the new
     constant; anything else throws `ArgumentException`.
   - `ROSLYNSENTINEL_MODELEVAL_CONTRACT_ENDINGS`: unset, empty or `system` = endings only in the system prompt (default); `user` = the
     contract's Endings section is also repeated at the end of every `ScenarioTask` rendered through `EvalContractOptions.Render`. `user` with
     `legacy` throws (the Endings describe `DONE:`/`BLOCKED:`, which only the new contract defines).
   The A/B this serves (proposal Decisions, "A/B arms"): old versus new contract, both on the same prompts at the same commit, no pinned
   pre-migration arm; comparison with earlier history is not a goal. The hash stamp labels which text ran.
2. **Where things live (layering `Common <- AgentLoop`, `Tests.Harness`/`Tests.ModelEval` above it; PlanStepRunner and
   Tools.Advanced already reference AgentLoop).** The contract constants, `ScenarioTask`, its renderer, and the pure parse/hash helpers go in
   RoslynSentinel.AgentLoop (namespace `RoslynSentinel.Common.AgentLoop`, beside `AgentSystemPrompts`), because the Endings text the renderer
   repeats belongs to the contract and PlanStepRunner and SubAgentEval will consume the same record later (proposal Phasing step 6). The
   environment read and the call-site seam stay in the harness (`EvalContractOptions`), so AgentLoop gains no environment variable.
3. **One seam for the system prompt.** The 13 ModelEval call sites (11 files) of `ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent)`
   become `EvalContractOptions.ComposeSystemPrompt()`. With the variable unset it returns exactly the old string, so that sweep is text-neutral.
   PlanStepRunner (Program.cs:239) and SubAgentEvalImpl.cs:56 are not touched.
4. **Stamp.** A new process stamp `ModelAgentRunner.ContractStamp` (hash and placement name), set once by `ModelEvalAssemblySetUp`, copied
   into new transcript fields `ContractHash` and `ContractPlacement`, and into two appended per-sample CSV columns `contractHash`,
   `contractPlacement`. The existing `ProcessStamp` 4-tuple keeps its shape (it has 6 consumers; changing its arity buys nothing). The hash is the
   first 12 lowercase hex characters of SHA-256 over the contract text with CRLF normalised to LF (same length and style as `ErrorPolicyHash`,
   via `ContentFingerprint.Compute`). The legacy contract is stamped too, so old-versus-new is visible in every row. runs.csv is unchanged
   (variants are separate commits or env values recorded by the evalqueue; `pinnedCommit` fixes the text).
   **Scope of the CSV stamp.** Only the `ScenarioSample` family (ScenarioAgentTests, which includes the StopAdherence scenarios) writes the
   per-sample CSV. `ImplementationScenarioAgentTests` writes only `transcript.json` and `step-<k>.outcome.txt`, so for ImplementationScenario
   the transcript fields are the stamp.
5. **ScenarioTask.** A sealed record `ScenarioTask(Goal, Files, IfFails, DoneWhen, Conditions = null, Report = null)` in AgentLoop with
   `Render(bool repeatEndings = false)`. Labels and order: `Goal:`, `Files:`, `Conditions:` (skipped when empty), `If a condition fails:`,
   `Done when:`, `Report:` (skipped when empty). Values keep their own line breaks (a brief may use a numbered list or name the acceptance
   tests on several lines), CRLF becomes LF, outer whitespace is trimmed, an empty required field throws `ArgumentException` naming the field.
   The renderer does not limit the length of a value: how long a Goal or brief is, is the rework's decision (proposal section 3). Call sites always
   use named arguments, because C# requires the two optional parameters last while the rendered order is the proposal's.
   `EvalContractOptions.Render(task)` = `task.Render(repeatEndings: placement is user)`. No scenario calls it in this plan.
6. **`validateOnApply` leaves the eval tool surface (owner decision, proposal Decisions table).** The contract text carries no rule about it, and the
   parameter is hidden from the schemas every eval world lists, with `validateOnApply:false` refused. The existing machinery already does both
   halves: `McpToolSchemaPatcher.ApplyLeanProfile` strips parameters from the emitted schema and registers them in `HiddenSchemaParams`, and
   `ToolArgumentValidator.Validate` refuses `validateOnApply:false` for a tool where `HiddenSchemaParams.IsHidden(tool, "validateOnApply")`.
   Only the trigger is too wide: the Lean profile also hides `autoStage`, `returnDiff`, `lineBefore` and `lineAfter`, which would confound the A/B.
   **Switch shape (recommended): a hidden-parameter list option on `SchemaOptions`**, `--hide-params=<name>[,<name>...]` /
   `ROSLYNSENTINEL_HIDE_PARAMS` (flag wins, like `--schema-profile`), exposed as `SchemaOptions.HiddenParameters`; the patcher hides the union of the
   Lean list (when the profile is Lean) and that list. It composes with `--schema-profile` and needs no code change to hide another name later, whereas a
   third `SchemaProfile` value (for example `NoValidateOnApply`) is not composable and adds an enum member per future experiment. The refusal needs no
   new trigger (it already keys on `IsHidden`); Step 11 only makes it robust and fixes its wording.
   **Hosting finding (verified in source):** the harness does not spawn a server. `ModelEvalMcpEnvironment.InitializeAsync` builds an in-process MCP server on two
   pipes (`StreamServerTransport`) through `AddRoslynSentinelToolsAdvanced`, which delegates to `AddRoslynSentinelToolsBasic`; that registers the
   call-tool filter list including `ArgumentValidation` (`ServiceRegistrationExtensionsBasic.cs` `BuildCallToolFilterRegistrations`, entry at ~248 ->
   `ToolArgumentValidator.Validate`), and tools are built by `McpToolSchemaPatcher.CreateSentinelTool`, which calls `ApplyLeanProfile` at registration. The
   agent loop lists and calls tools through the `McpClient` on the other end of the pipes, so its tools/list is patched and its calls are validated. The harness
   never calls `SchemaOptions.Configure`, so the process profile is Full today. Hiding at the harness's tool-schema layer is therefore not needed; the
   option is set per world, before the tools are registered (Step 12). Spawned servers (PlanStepRunner via `ErrorPolicyWiring.ChildServerArgs`) are not
   eval worlds and are untouched.
   **Harness wiring:** `ModelEvalMcpEnvironment.CreateAsync` gains an optional `fullToolSchema` argument (default false = `validateOnApply` hidden). The two
   scenario families expose the per-scenario switch as a trailing property `OfferFullToolSchema` (default false) on `ScenarioDefinition` and
   `ImplementationScenario`. No scenario sets it; the property is a mechanism only.

NEW-DTO: ScenarioTask (Request, AgentLoop)
Evaluated: ImplementationScenarioStep (internal, Tests.ModelEval) - holds a finished prompt string plus golden files and grader callbacks, not visible below the test layer; ScenarioDefinition (Tests.Harness) - `PromptTemplate` string plus scoring fields, same visibility problem; PlanStepFile (RoslynSentinel.Utilities.PlanStepRunner/PlanStepFile.cs:28) - parsed plan-step markdown in a project above AgentLoop, so AgentLoop cannot reference it (mapping plan steps onto the record is proposal Phasing step 6)
Wire impact: none (authoring input to a renderer, never serialized or returned by a tool; the name is fixed by the settled proposal, the `*Request` suffix rule targets tool and engine inputs)

## Execution rules

- Plan ID for commit trailers: `plan_modeleval_workflow_contract_phase1` (`Plan:` on each slice, `Closes:` on the last).
- One `Build`/`RunTest` at a time (subagents included). Slice Done-when uses `Build` quickBuild on the solution (0 errors); Step 21 uses the default
  fullBuildThrowaway. Implementers never run tests; the named tests are run by the dispatcher in Step 21.
- **No scenario prompt or grader changes in any step.** The scenario files under RoslynSentinel.Tests.ModelEval/ImplementationScenarios, the prompts and
  `ClassifyStopAdherence` in RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs and `ImplementationScenarioChecks` are not edited. The only edits near
  scenario definitions are the trailing `OfferFullToolSchema` property on the two record types (Step 12) and passing it at the two call sites (Step 13).
  Step 21 verifies this with `Git(operation: diff)`.
- Steps 1-11 and 14-20 are text-neutral for a run with the variables unset (the CSV gains two appended columns, Step 6; the refusal text changes by one
  clause, Step 11). Steps 12-13 are the only behaviour-changing steps: every eval world stops offering `validateOnApply`. They may be held until the
  tests of Steps 15-18 exist. Evalqueue jobs pin a commit, so a queued job is unaffected.
- Steps 10 and 11 change server source (Common, Server.Advanced): the Verification rule applies (Step 21).
- Model-free tests go in RoslynSentinel.Tests.Harness (namespace `RoslynSentinel.Tests.ModelEval`) or, for internal types, in
  RoslynSentinel.Tests.ModelEval in namespace `RoslynSentinel.Tests.ModelEval`. Never in `RoslynSentinel.Tests.ModelEval.LiveModel`: the
  `[SetUpFixture]` there (`ModelEvalAssemblySetUp`) calls `Assert.Ignore` when `ROSLYNSENTINEL_LLM_MODEL` is unset or the host is unreachable, which would silently skip the test.
- A hook may ask for `DTO-EXEMPT: <why>` on a new static class, enum or test fixture; give it (these are not DTOs). `ScenarioTask` carries the NEW-DTO block above in the `reason` of the call that creates it.
- No new MCP tool, so no registration step. New files in SDK-style projects need no csproj edit.
- Verified anchors (2026-11-23): AgentSystemPrompts.cs `CodingAgent` 21-151, `WithErrorHandling` 204; ErrorPolicyWiring.cs `ComposePrompt` 13/21;
  AgentTranscript.cs `ErrorPolicyHash` 76 (properties end at `FinalMessage` 197); ModelAgentRunner.cs `ProcessStamp` 93, assignment block ~268-273 inside `RunCoreAsync`;
  EvalRunStamp.cs `BuildProcessStamp` 14/33; ScenarioSample.cs record 33-52, `Header` 57-58, `Parse` ~121-170; ModelEvalAssemblySetUp.cs `ProcessStamp` line ~57;
  ImplementationScenarioAgentTests.cs `ComposePrompt` 127, `ModelEvalMcpEnvironment.CreateAsync` ~101; `ContentFingerprint.Compute` (Common/ContentFingerprint.cs);
  SchemaOptions.cs (`Configure`, `ParseProfile`, `SchemaProfile`), McpToolSchemaPatcher.cs `ApplyLeanProfile` ~625 (caller ~214), HiddenSchemaParams.cs,
  ToolArgumentValidator.cs `Validate` refusal block ~1287-1305.

## Steps

### Step 1 - Contract skeleton (AgentLoop)
- Files: RoslynSentinel.AgentLoop/ScenarioTask.cs (new), RoslynSentinel.AgentLoop/WorkflowContract.cs (new), RoslynSentinel.AgentLoop/AgentSystemPrompts.cs
- Depends on: none
- Effect: text-neutral (new members, no callers).
- Change: one write per file, definitions first. All in namespace `RoslynSentinel.Common.AgentLoop`. Every method body is `throw new NotImplementedException(); // TODO implement (Step 3)`.
  - `AgentSystemPrompts.cs`, one `batchEdits` call: add `public const string CodingAgentContract = "TODO contract text (Step 2)";` and `public const string CodingAgentContractEndings = "TODO contract endings (Step 2)";` after `CodingAgent`. `CodingAgent` stays byte-identical.
  - `WorkflowContract.cs` (new), `CreateFile`, with `reason` containing `DTO-EXEMPT: static helper and enums, not DTOs`:
    `public enum ContractVariant { Legacy, Standing }`, `public enum EndingsPlacement { System, SystemAndUser }`, and
    `public static class WorkflowContract` with `public const int HashLength = 12;`,
    `public static string TextFor(ContractVariant variant)`, `public static string Hash(string contractText)`,
    `public static string PlacementName(EndingsPlacement placement)`, `public static ContractVariant ParseVariant(string? value)`,
    `public static EndingsPlacement ParsePlacement(string? value)`.
  - `ScenarioTask.cs` (new), `CreateFile`, `reason` carrying the NEW-DTO block from the Decision section:
    `public sealed record ScenarioTask(string Goal, string Files, string IfFails, string DoneWhen, string? Conditions = null, string? Report = null)` with `public string Render(bool repeatEndings = false)`.
- Done when: `Build` quickBuild on the solution reports 0 errors.
- Lane: implementer (3 files, 3 edits, depth tier 1).

### Step 2 - Contract text constants
- Files: RoslynSentinel.AgentLoop/AgentSystemPrompts.cs
- Depends on: 1
- Effect: text-neutral (the constants have no caller yet).
- Change: replace the two placeholder constants. `CodingAgentContract` is a raw string literal, 8-space indented like `CodingAgent`, whose content is exactly the fenced block of the proposal's "1. Standing workflow contract" (the lines between the ``` fences under that heading in proposal_modeleval_standing_workflow_contract.md, currently lines 117-161); take it from the proposal as committed at implementation time. The "Working" list there is numbered 1-5 with no `validateOnApply` rule (step 5 is "Check each Done-when item once"); if a `validateOnApply` rule is still present, drop it, renumber the following step, and report that the proposal was not yet updated. Do not reword (wording is settled by the A/B). `CodingAgentContractEndings` is the same text as its final "## Endings" section (heading line through the end of the BLOCKED bullet). Doc comment on `CodingAgentContract` carries the survival-rule citations from the proposal (names you observed and listing: token-path slips and guessed-name Search loops; transcription: ReplaceSnippet transcription failures; comment removal: WholeFileRewrite MinimalGuidance; tool side effects: whitespace no-op loops) and says it is selected by `ROSLYNSENTINEL_MODELEVAL_CONTRACT=standing`; the prompt text itself carries no incident history.
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "TODO contract")` over AgentSystemPrompts.cs returns no match; `Search(mode: text, query: "validateOnApply", fileGlob: "RoslynSentinel.AgentLoop/**/*.cs")` returns no match.
- Lane: implementer (1 file, 2 edits). Pairs with: Step 3.

### Step 3 - Renderer and contract helpers
- Files: RoslynSentinel.AgentLoop/ScenarioTask.cs, RoslynSentinel.AgentLoop/WorkflowContract.cs
- Depends on: 1
- Effect: text-neutral (no caller yet).
- Change: replace the stub bodies.
  - `ScenarioTask.Render`: validate that Goal, Files, IfFails and DoneWhen are not blank (`ArgumentException` whose message names the field and says the field is required); normalise each value (CRLF to LF, trim); emit `Goal: ...`, `Files: ...`, `Conditions: ...` (only when non-blank), `If a condition fails: ...`, `Done when: ...`, `Report: ...` (only when non-blank), joined by `\n`, no trailing newline; when `repeatEndings`, append a blank line and `AgentSystemPrompts.CodingAgentContractEndings` with CRLF normalised to LF and trimmed. No escaping of braces and no length limit (a consumer that calls `string.Format` writes `{0}` itself).
  - `WorkflowContract.TextFor`: `Legacy` returns `AgentSystemPrompts.CodingAgent`, `Standing` returns `AgentSystemPrompts.CodingAgentContract`. `Hash`: `ContentFingerprint.Compute(text with CRLF replaced by LF)` lower-case, first `HashLength` characters. `PlacementName`: `"system"` or `"system+user"`. `ParseVariant`: null, empty, whitespace or `legacy` gives Legacy; `standing` gives Standing (trimmed, case-insensitive); anything else throws `ArgumentException` listing `legacy, standing`. `ParsePlacement`: null, empty, whitespace or `system` gives System; `user` gives SystemAndUser; anything else throws `ArgumentException` listing `system, user`.
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "TODO implement \\(Step 3\\)")` over the two files returns no match.
- Lane: implementer (2 files, ~6 edits). Pairs with: Step 2.

### Step 4 - Contract stamp plumbing (AgentLoop)
- Files: RoslynSentinel.AgentLoop/AgentTranscript.cs, RoslynSentinel.AgentLoop/ModelAgentRunner.cs
- Depends on: none (pairs after Step 1 for scheduling only)
- Effect: text-neutral; transcripts gain two optional JSON fields only when a stamp is set.
- Change: `AgentTranscript`: after `ErrorPolicyHash` (line 79) add `public string? ContractHash { get; set; }` and `public string? ContractPlacement { get; set; }`, each with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` and a doc comment in the style of `ErrorPolicyHash` ("Null means the run was not stamped, so the property is omitted from the JSON"). `ModelAgentRunner`: beside `ProcessStamp` (line 93) add `public static (string Hash, string Placement)? ContractStamp { get; set; }` with the same "Intentional deviation: a mutable static, one stamp per test process" comment; in `RunCoreAsync` after the `if (ProcessStamp is { } stamp) { ... }` block copy `ContractStamp` into `transcript.ContractHash` and `transcript.ContractPlacement` (independent of `ProcessStamp`, which is null when git is unreadable).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (2 files, 4 edits). Pairs with: Step 6.

### Step 5 - EvalContractOptions and assembly setup
- Files: RoslynSentinel.Tests.Harness/EvalContractOptions.cs (new), RoslynSentinel.Tests.ModelEval/ModelEvalAssemblySetUp.cs
- Depends on: 1, 4 (compile); behaviour needs 2 and 3
- Effect: text-neutral with both variables unset.
- Change: `EvalContractOptions` (namespace `RoslynSentinel.Tests.ModelEval`, `public static class`, `reason` with `DTO-EXEMPT: static options class`), one write:
  - `public const string ContractEnvVar = "ROSLYNSENTINEL_MODELEVAL_CONTRACT";` and `public const string EndingsEnvVar = "ROSLYNSENTINEL_MODELEVAL_CONTRACT_ENDINGS";`
  - `public static (ContractVariant Variant, EndingsPlacement Placement) Resolve(string? contractValue, string? endingsValue)`: `WorkflowContract.ParseVariant` and `ParsePlacement`; throws `ArgumentException` when the placement is `SystemAndUser` and the variant is `Legacy`.
  - `public static (ContractVariant Variant, EndingsPlacement Placement) Current()`: `Resolve` over the two environment variables, read on every call (no cached state; a bad value throws with the variable name each time).
  - `public static string ComposeSystemPrompt()` = `ComposeSystemPrompt(Current().Variant, ErrorPolicyOptions.Mode, ErrorPolicyOptions.PolicyFilePath)`; `public static string ComposeSystemPrompt(ContractVariant variant, ErrorPolicyMode mode, string? policyFilePath)` = `ErrorPolicyWiring.ComposePrompt(WorkflowContract.TextFor(variant), mode, policyFilePath)`.
  - `public static string Render(ScenarioTask task)` = `Render(task, Current().Placement)`; `public static string Render(ScenarioTask task, EndingsPlacement placement)` = `task.Render(repeatEndings: placement == EndingsPlacement.SystemAndUser)`.
  - `public static (string Hash, string Placement) BuildContractStamp()` = over `Current()`; `BuildContractStamp(ContractVariant variant, EndingsPlacement placement)` = `(WorkflowContract.Hash(WorkflowContract.TextFor(variant)), WorkflowContract.PlacementName(placement))`.
  `ModelEvalAssemblySetUp.OneTimeSetUp`: right after the `ModelAgentRunner.ProcessStamp = ...` line add `ModelAgentRunner.ContractStamp = EvalContractOptions.BuildContractStamp();` with a comment naming the two environment variables (before the `Assert.Ignore` model check, so a bad value fails loudly rather than being skipped).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (2 files, 3 edits). Pairs with: Step 7.

### Step 6 - Per-sample CSV columns
- Files: RoslynSentinel.Tests.Harness/ScenarioSample.cs
- Depends on: none
- Effect: behaviour-changing for results.csv only: two columns appended (`contractHash`, `contractPlacement`); old rows still parse. No prompt or grader changes.
- Change: append `string ContractHash = ""` and `string ContractPlacement = ""` to the `ScenarioSample` record after `TopP` (with `<param>` docs: stamped contract hash and placement name; "" when unstamped or for an old CSV row without the column). `Header`: append `,contractHash,contractPlacement`. Add `public const int PreContractColumnCount = 36;` with a doc comment in the style of the neighbours. `ToCsvLine`: append `Csv(sample.ContractHash), Csv(sample.ContractPlacement)`. `Parse`: accept `PreContractColumnCount` as a fifth legal count (update the condition, the exception text and the doc comment); the three conditions `f.Count == expected ? f[33]/f[34]/f[35]` for ModelKey, Temperature and TopP become `f.Count >= PreContractColumnCount`; add `f.Count == expected ? f[36] : string.Empty` and `f[37]` for the two new fields. Existing construction sites compile unchanged (defaulted parameters).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~8 edits). Pairs with: Step 4.

### Step 7 - Fill the contract columns where samples are built
- Files: RoslynSentinel.Tests.Harness/ScenarioSampleLoop.cs, RoslynSentinel.Tests.Harness/ScenarioRescoring.cs, RoslynSentinel.Tests.Harness/SavedTranscriptScoring.cs
- Depends on: 4, 6
- Effect: behaviour-changing for results.csv content only.
- Change: `ScenarioSampleLoop.RunSamplesAsync`: the three `new ScenarioSample(...)` constructions (scored sample ~line 130, the result-is-null branch ~97 that reads `stamp?`, and the harness-error branch ~157): add `ContractHash` and `ContractPlacement` named arguments, from `result.Transcript.ContractHash ?? string.Empty` / `ContractPlacement ?? string.Empty` where a transcript exists and from `ModelAgentRunner.ContractStamp?.Hash ?? string.Empty` / `?.Placement ?? string.Empty` where only the process stamp exists. `ScenarioRescoring.RescoreRunFolderAsync`: read `contractHash` and `contractPlacement` with `Field(columns, fields, ...)` like `errorPolicyHash`, pass them in both constructions (scored: fall back to the transcript values when the column is empty, same pattern as `ErrorPolicyHash` at line 146). `SavedTranscriptScoring.ScoreFile`: pass `transcript.ContractHash ?? ""` and `transcript.ContractPlacement ?? ""`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (3 files, ~7 edits). Pairs with: Step 5.

### Step 8 - Route the ModelEval call sites through the seam, part 1
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarioAgentTests.cs, ScenarioAgentTests.cs (2 sites), ShippingKindSteppedAgentTests.cs, LargeResultPagingAgentTests.cs, OrderPricingRefactorAgentTests.cs, OrderPricingRefactorChainAgentTests.cs
- Depends on: 5
- Effect: text-neutral (default `legacy` returns the identical string).
- Change: one operation, 6 files, 7 sites (all in project RoslynSentinel.Tests.ModelEval): replace `ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent)` with `EvalContractOptions.ComposeSystemPrompt()`. Tool: `ReplaceSnippet` with `batchEdits`, one batch per file, exact old snippet; in ScenarioAgentTests the two sites sit on different lines (137 `var result = await runner.RunAsync(...)`, 212 `systemPrompt = ...`), so include the unique line prefix in each snippet. The task prompts passed beside them are not touched.
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "ComposePrompt\\(AgentSystemPrompts\\.CodingAgent\\)", fileGlob: "RoslynSentinel.Tests.ModelEval/**/*.cs")` lists only the 5 files of Step 9.
- Lane: implementer-sweep (1 operation, 6 files, 7 call sites, 1 project; sweep tier 0). Pairs with: Step 9.

### Step 9 - Route the ModelEval call sites through the seam, part 2
- Files: RoslynSentinel.Tests.ModelEval/PlanImplementVerifyAgentTests.cs (2 sites: `plan` and `implement` phases; the `verify` phase keeps `AgentSystemPrompts.CodeReviewer`), PlanOnlyAgentTests.cs, PlanThenExecuteAgentTests.cs, SizeThresholdAgentTests.cs, WholeFileRewriteAgentTests.cs
- Depends on: 5
- Effect: text-neutral.
- Change: same operation and tool as Step 8 over 5 files, 6 sites.
- Done when: `Build` quickBuild 0 errors; the same `Search` returns no match in RoslynSentinel.Tests.ModelEval.
- Lane: implementer-sweep (1 operation, 5 files, 6 call sites, 1 project). Pairs with: Step 8.

### Step 10 - Hidden-parameter option on SchemaOptions and the patcher (server source)
- Files: RoslynSentinel.Common/SchemaOptions.cs, RoslynSentinel.Common/McpToolSchemaPatcher.cs, RoslynSentinel.Common/HiddenSchemaParams.cs
- Depends on: none
- Effect: text-neutral (the default list is empty; a Full or Lean server emits the same schemas as today).
- Change: definitions first, then the consumer.
  - `SchemaOptions`: add `public static IReadOnlyList<string> LeanHiddenParameters { get; } = ["autoStage", "returnDiff", "validateOnApply", "lineBefore", "lineAfter"];` (the single source of the Lean list, moved from the local array in `ApplyLeanProfile`); `public static IReadOnlySet<string> HiddenParameters { get; private set; }` (default empty, ordinal comparer); `public static IReadOnlySet<string> ParseHiddenParameters(string? raw)` (split on `,` and `;`, trim, drop empties, never throws, unknown names are kept and simply match nothing); `public static void ConfigureHiddenParameters(IEnumerable<string> names)` (replaces the set; the harness calls this directly so it does not touch `Profile` or `EmitDataTags`); `public static IReadOnlyList<string> EffectiveHiddenParameters()` (the Lean list when `Profile` is `Lean`, then the explicit names not already in it, in that order). `Configure(args)` also sets `HiddenParameters` from `--hide-params` falling back to `ROSLYNSENTINEL_HIDE_PARAMS` (the existing private `GetArgValue`), and its summary documents the flag and variable.
  - `McpToolSchemaPatcher.ApplyLeanProfile`: replace the `SchemaOptions.Profile != SchemaProfile.Lean` guard and the local `leanHiddenParameters` array with `var hidden = SchemaOptions.EffectiveHiddenParameters(); if (hidden.Count == 0) return;` and iterate `hidden`; rename the private method to `ApplyHiddenParameters` (`RenameSymbol`, one caller at line ~214) and update its doc comment (Lean profile and `--hide-params`). Behaviour for Lean is unchanged (same five names, same `required` cleanup, same `HiddenSchemaParams.Register`).
  - `HiddenSchemaParams`: update the summary (stripped by the Lean profile or `--hide-params`; cref `ApplyHiddenParameters`).
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "ApplyLeanProfile")` returns no match; the existing `SchemaLeanProfileTests` still pass (dispatcher runs them in Step 21, not here).
- Lane: implementer (3 files, ~7 edits, depth tier 1; the rename is one `RenameSymbol` call). Pairs with: Step 11.

### Step 11 - Refusal robust to a stale registry, facts-only wording (server source)
- Files: RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs
- Depends on: none
- Effect: behaviour-changing only for the refusal text (the Lean case): one clause is reworded and the refusal no longer fires when the schema declares the parameter.
- Change: in `Validate` (the `validateOnApply:false` block at ~1287-1305): add `&& !declared.Contains("validateOnApply")` to the condition (reason: `HiddenSchemaParams` and the validator's `SchemaCache` are process-wide and keyed by tool name, so a world built with the parameter declared after a world with it hidden would otherwise be refused although its schema offers the parameter; the emitted schema is the authority); change `on this server's current schema profile:` to `on this server's current schema configuration:` (the parameter can now be hidden by `--hide-params` as well as by the Lean profile). The rest of the text is unchanged: it states facts and options only (the gate always validates, nothing was executed, the gate's message names the failing symbols, options are to change the edit or put dependent changes in the same call), with no stop or must wording (CLAUDE.md: tool text carries facts and options, policy lives in prompts). A comment on the block records the process-static reason.
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "current schema profile", fileGlob: "RoslynSentinel.Server.Advanced/**/*.cs")` returns no match.
- Lane: implementer (1 file, 2 edits). Pairs with: Step 10.

### Step 12 - Harness: hide `validateOnApply` per world, opt-in property (test harness)
- Files: RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs, RoslynSentinel.Tests.Harness/ScenarioDefinition.cs, RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenario.cs
- Depends on: 10, 11
- Effect: behaviour-changing: from here every eval world built by `ModelEvalMcpEnvironment.CreateAsync` lists tools without `validateOnApply` and refuses `validateOnApply:false`. No scenario opts in; no prompt or grader changes.
- Change:
  - `ModelEvalMcpEnvironment.CreateAsync` (and the private `InitializeAsync`): new trailing optional parameter `bool fullToolSchema = false`. Immediately before the `configureToolSurface` / `AddRoslynSentinelToolsAdvanced` registration (tools are built there, and `SchemaOptions` is read at that moment) do, in this order: `HiddenSchemaParams.Clear()`, `ToolArgumentValidator.ClearSchemaCacheForTests()`, `SchemaOptions.ConfigureHiddenParameters(fullToolSchema ? [] : ["validateOnApply"])`. A comment says the three are process-static, that a world built later resets them, and that the ModelEval assembly runs its fixtures sequentially (no assembly-level `Parallelizable`, verified), which is why a per-world static is safe here (Risks R4). Do not restore afterwards: a toolset enabled later in the same world builds tools with the same setting. The method's summary documents the parameter and that `Profile` is deliberately untouched.
  - `ScenarioDefinition`: append `bool OfferFullToolSchema = false` after `ReplyMustName` with a `<param>` doc: mechanism only; true builds the sample's world with the full tool schema (validateOnApply offered); no scenario sets it.
  - `ImplementationScenario`: append `bool OfferFullToolSchema = false` after `ParkedReason`, same doc. `Placeholder` is unchanged.
- Done when: `Build` quickBuild 0 errors (all existing `CreateAsync` callers and positional record constructions compile unchanged because the new parameters are trailing and defaulted).
- Lane: implementer (3 files, ~5 edits). Pairs with: Step 19.

### Step 13 - Harness: pass the per-scenario switch at the two scenario call sites
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarioAgentTests.cs, RoslynSentinel.Tests.ModelEval/ScenarioAgentTests.cs
- Depends on: 12
- Effect: text-neutral (every property is false, so the default applies).
- Change: `ImplementationScenarioAgentTests.RunStepAsync`: add `fullToolSchema: scenario.OfferFullToolSchema` to the `ModelEvalMcpEnvironment.CreateAsync` call (~line 101, after `configureToolSurface:`). `ScenarioAgentTests.ScenarioEnvironment.CreateAsync` (~line 394): add `fullToolSchema: scenario.OfferFullToolSchema`. The other `CreateAsync` callers (stepped, chain, size-threshold, whole-file, plan and resume tests) take the default (hidden) with no edit; that is the intended "evals run with the parameter hidden by default".
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "OfferFullToolSchema")` lists the two record declarations, the two call sites and no scenario definition that sets it to true.
- Lane: implementer (2 files, 2 edits). Pairs with: Step 20.

### Step 14 - Document the options and the stamp
- Files: docs/current/references/reference_model_eval_procedure.md
- Depends on: 5, 6, 10, 12
- Effect: text-neutral (docs).
- Change: in the environment-variable table (after the `ROSLYNSENTINEL_MODELEVAL_EDIT_ARM` row, line 74) add one row for `ROSLYNSENTINEL_MODELEVAL_CONTRACT` (`legacy` default, `standing`; recorded with an evalqueue job; selects the coding-agent system prompt for every ModelEval call site; stamped as `ContractHash` in transcript.json and `contractHash` in the per-sample CSV) and one for `ROSLYNSENTINEL_MODELEVAL_CONTRACT_ENDINGS` (`system` default, `user`; needs `standing`; applies only to prompts rendered from `ScenarioTask` through `EvalContractOptions.Render`). Note beside them that ImplementationScenario runs carry the stamp in transcript.json only, and add one sentence that eval worlds hide `validateOnApply` by default (server flag `--hide-params` / `ROSLYNSENTINEL_HIDE_PARAMS` for a spawned server; `OfferFullToolSchema` on the scenario record for the full schema). Use Edit (Markdown), ASCII only.
- Done when: Grep on the file for `MODELEVAL_CONTRACT` shows both variable names, each as one table row, and for `OfferFullToolSchema` shows the sentence.
- Lane: implementer (1 non-C# file, 1 edit).

### Step 15 - Tests: contract constants, helpers and renderer
- Files: RoslynSentinel.Tests.Harness/WorkflowContractTests.cs (new), RoslynSentinel.Tests.Harness/ScenarioTaskRenderTests.cs (new)
- Depends on: 2, 3
- Effect: text-neutral (tests).
- Change: namespace `RoslynSentinel.Tests.ModelEval`, `[TestFixture]`, model-free.
  `WorkflowContractTests`: `TextFor_Legacy_IsCodingAgentUnchanged`; `TextFor_Standing_IsTheContractAndDiffersFromLegacy`; `Contract_IsAsciiAndHasFewerLinesThanLegacy`; `Contract_WorkingListIsNumberedOneToFive_AndNamesNoValidateOnApply` (no `validateOnApply` anywhere in the contract; numbered items 1. to 5. only, consecutive); `Contract_EndsWithEndingsSection` (LF-normalised `EndsWith` of `CodingAgentContractEndings`); `Endings_ContainNoBracesAndNameBothTokens` (no `{`/`}`; contains `DONE:` and `BLOCKED:`; the string.Format-safety guard); `Hash_IsTwelveLowercaseHex`; `Hash_IgnoresLineEndingStyle`; `Hash_DiffersBetweenVariants`; `ParseVariant_UnsetEmptyAndLegacy_ReturnLegacy`; `ParseVariant_StandingAnyCase_ReturnsStanding`; `ParseVariant_Unknown_ThrowsNamingTheValues`; `ParsePlacement_UnsetAndSystem_ReturnSystem`; `ParsePlacement_User_ReturnsSystemAndUser`; `ParsePlacement_Unknown_Throws`; `PlacementName_ReturnsSystemAndSystemPlusUser`.
  `ScenarioTaskRenderTests`: `Render_AllFields_OneLabelledLineEachInOrder`; `Render_EmptyOptionalFields_AreSkipped`; `Render_BlankRequiredField_ThrowsNamingTheField` (TestCase per Goal, Files, IfFails, DoneWhen); `Render_ValueWithLineBreaks_KeepsThemAsLf`; `Render_LongMultiSentenceGoal_IsNotTruncated`; `Render_TrimsOuterWhitespace`; `Render_RepeatEndingsOff_IsTheDefault`; `Render_RepeatEndingsOn_AppendsEndingsAfterBlankLine`; `Render_KeepsFormatPlaceholder_SoStringFormatFillsIt`.
- Done when: dispatcher runs `RunTest` filter `WorkflowContractTests|ScenarioTaskRenderTests`, all pass. Outcome classes: pass or named test failure; no model involved.
- Lane: test-writer (2 new files). Pairs with: Step 10.

### Step 16 - Tests: the option class and the stamp helper
- Files: RoslynSentinel.Tests.Harness/EvalContractOptionsTests.cs (new)
- Depends on: 5 (and 2, 3)
- Effect: text-neutral (tests).
- Change: `[TestFixture]`, `[NonParallelizable]` (one test sets environment variables; restore them in `finally`; comment why). Class `EvalContractOptionsTests`, methods: `Resolve_Unset_IsLegacyAndSystem`; `Resolve_StandingUser_ReturnsStandingSystemAndUser`; `Resolve_UserEndingsWithLegacy_Throws`; `Resolve_UnknownContract_ThrowsNamingTheVariable`; `ComposeSystemPrompt_LegacyOffNoFile_EqualsCodingAgent`; `ComposeSystemPrompt_StandingOffNoFile_EqualsContract`; `ComposeSystemPrompt_StandingWithPolicyFile_StartsWithContractAndAddsErrorHandlingBlock` (temp policy file, deleted in `finally`, explicit-argument overload so `ErrorPolicyOptions` static state is not touched); `Render_ExplicitPlacement_RepeatsEndingsOnlyForSystemAndUser`; `BuildContractStamp_Legacy_HashesCodingAgentAndNamesSystem`; `BuildContractStamp_StandingUser_HashesContractAndNamesSystemPlusUser`; `Current_ReadsTheEnvironmentOnEveryCall`.
- Done when: dispatcher runs `RunTest` filter `EvalContractOptionsTests`, all pass.
- Lane: test-writer (1 new file). Pairs with: Step 12.

### Step 17 - Tests: shared pinned prompt assertions gain a case for the new constant
- Files: RoslynSentinel.Tests.ErrorVariants/ErrorPolicyWiringTests.cs, RoslynSentinel.Tests.Harness/ErrorPolicyEvalPromptTests.cs, RoslynSentinel.Tests.Harness/PlanStepRunnerSurfaceTests.cs
- Depends on: 2
- Effect: text-neutral (tests). No existing assertion pins `CodingAgent` wording (they compare against the constant itself), so none breaks; these add the contract constant beside it.
- Change: `ErrorPolicyWiringTests`: `ComposePrompt_ContractOffNoFile_ReturnsContractUnchanged`; `ComposePrompt_ContractFileSetAnyMode_AppendsErrorHandlingBlock` (TestCase `Off`, `Pointer`, `Inline`, `StartWith(CodingAgentContract.TrimEnd())`). `ErrorPolicyEvalPromptTests`: `ComposePrompt_ContractModeOffNoFile_YieldsTheContract`; `ComposePrompt_ContractPointerWithPolicyFile_StartsWithContractAndAddsBlock`. `PlanStepRunnerSurfaceTests.Prompts_NameNoToolOutsideTheAllowList`: add the pair `("ErrorPolicyWiring.ComposePrompt(CodingAgentContract)", ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgentContract))` to `prompts`. If that test then names a tool outside the allow list, report the tool name and stop: contract wording is the owner's (settled by the A/B), not a test fix.
- Done when: dispatcher runs `RunTest` filter `ErrorPolicyWiringTests|ErrorPolicyEvalPromptTests|Prompts_NameNoToolOutsideTheAllowList`, all pass.
- Lane: test-writer (3 files, one slice because the pinned assertions are shared). Pairs with: Step 13.

### Step 18 - Tests: stamp and CSV
- Files: RoslynSentinel.Tests.Harness/ScenarioSampleCsvTests.cs, RoslynSentinel.Tests.Harness/ContextExhaustedRunnerTests.cs, RoslynSentinel.Tests.Harness/ScenarioRescoringTests.cs
- Depends on: 4, 6, 7
- Effect: text-neutral (tests).
- Change: `ScenarioSampleCsvTests`: update the pinned counts (`Header_EndsWithSamplingColumnsAndHasThirtySixColumns` becomes `...ThirtyEightColumns` and also asserts `PreContractColumnCount` 36 and columns 36 and 37; every `Has.Count.EqualTo(36)` becomes 38); add `ContractColumns_RoundTripThroughCsvLine`, `PreContractThirtySixColumnRow_ParsesWithEmptyContractColumnsAndReserializesWithThirtyEight`. `ContextExhaustedRunnerTests`: add `RunWithContractStamp_TranscriptCarriesHashAndPlacement` and `RunWithoutContractStamp_TranscriptOmitsContractFields` (set and restore `ModelAgentRunner.ContractStamp` in `finally`, as the existing `ProcessStamp` tests do); extend `Transcript_WithoutStampProperties_Deserializes` with `ContractHash` and `ContractPlacement` null. `ScenarioRescoringTests`: add one case that a rescored row keeps its `contractHash` and `contractPlacement` columns (and that an old row without them rescores to empty values); fix the 30-column `LegacyHeader` comment if the header text changed.
- Done when: dispatcher runs `RunTest` filter `ScenarioSampleCsvTests|ContextExhaustedRunnerTests|ScenarioRescoringTests|ScenarioSampleLoopTests`, all pass.
- Lane: test-writer (3 files). Pairs with: Step 14.

### Step 19 - Tests: the hide-params option on the server
- Files: RoslynSentinel.Tests.Server/SchemaHiddenParametersTests.cs (new)
- Depends on: 10, 11
- Effect: text-neutral (tests).
- Change: model on `SchemaLeanProfileTests` (RoslynSentinel.Tests.Server, namespace `RoslynSentinel.Tests.Server`): `[TestFixture]`, `[NonParallelizable]` (comment: mutates the process-wide `SchemaOptions`, `HiddenSchemaParams` and the validator schema cache), the same in-process `WithClientAsync` over `AddRoslynSentinelToolsBasic` with modes Workspace and Refactor (copy the helper; the originals are private), `ResetStatics` in SetUp and TearDown extended with `SchemaOptions.ConfigureHiddenParameters([])`. Class `SchemaHiddenParametersTests`, methods:
  - `HideParams_ValidateOnApply_TopLevelSchemaLacksExactlyThatName` (TestCase `ReplaceSnippet`, `Member`; the hidden schema's property set equals the Full schema's minus `validateOnApply`; so `autoStage`, `returnDiff`, `lineBefore`, `lineAfter` stay wherever Full has them);
  - `HideParams_ValidateOnApply_RequiredNoLongerListsIt`;
  - `HideParams_DoesNotTouchNestedBatchEditItemSchemas`;
  - `HideParams_RegistersExactlyValidateOnApply` (`HiddenSchemaParams.IsHidden("ReplaceSnippet", "validateOnApply")` true; `returnDiff` false);
  - `HideParams_CallPassingValidateOnApplyFalse_IsRefused` (text contains `validateOnApply` and `Nothing was executed`, contains neither `MUST` nor `do not`);
  - `HideParams_CallPassingValidateOnApplyTrue_IsNotRefused`;
  - `HideParams_CallPassingStillVisibleReturnDiff_IsAccepted` (not rejected as unknown);
  - `NoHideParams_ValidateOnApplyFalse_IsNotRefused` (Full, empty set);
  - `Refusal_WhenSchemaDeclaresTheParameter_IgnoresAStaleHiddenRegistration` (register the name in `HiddenSchemaParams` by hand, then a Full world: not refused);
  - `LeanProfileWithHideParams_HidesTheUnion_WithoutDuplicates`;
  - `ParseHiddenParameters_NullEmptyCommaSemicolonWhitespaceAndDuplicates` (TestCases);
  - `Configure_HideParamsFlag_SetsTheSet`, `Configure_Absent_LeavesNoneHidden`, `Configure_EnvironmentVariable_IsUsedWhenNoFlag_AndFlagWins` (save and restore `ROSLYNSENTINEL_HIDE_PARAMS` in `finally`).
- Done when: dispatcher runs `RunTest` filter `SchemaHiddenParametersTests`, all pass. Outcome classes: pass or named test failure; no model involved.
- Lane: test-writer (1 new file). Pairs with: Step 12.

### Step 20 - Tests: the harness default is hidden, the opt-in is full
- Files: RoslynSentinel.Tests.ModelEval/EvalToolSchemaVisibilityTests.cs (new)
- Depends on: 12, 13
- Effect: text-neutral (tests).
- Change: model on `ModelEvalToolSurfacesTests` (namespace `RoslynSentinel.Tests.ModelEval`, not `.LiveModel`; builds worlds with `ModelEvalMcpEnvironment.CreateAsync`, no LM Studio needed, no live-model attributes), `[TestFixture]`, `[NonParallelizable]` with the same process-static comment. Class `EvalToolSchemaVisibilityTests`, methods:
  - `Default_ValidateOnApplyIsHiddenFromTheListedSchemas` (ReplaceSnippet and Member via `environment.Client.ListToolsAsync`);
  - `Default_OtherBoilerplateParametersStayListed` (`returnDiff`, `lineBefore`, `lineAfter` present where the full world has them);
  - `Default_ValidateOnApplyFalse_IsRefusedThroughTheClient` (a `ReplaceSnippet` call with `action: validate` and `validateOnApply: false`: text contains `Nothing was executed`);
  - `OptIn_FullToolSchema_ListsValidateOnApply_AndAcceptsFalse`;
  - `OptIn_AfterADefaultWorld_IsNotRefused` and `Default_AfterAnOptInWorld_HidesAgain` (two worlds in one process, both orders, which is what the stale-registry guard of Step 11 and the reset of Step 12 exist for);
  - `NoRegisteredScenarioOptsIn` (every `ImplementationScenarioRegistry.All` entry and every `ScenarioRegistry.All` entry has `OfferFullToolSchema == false`; the opt-in is a mechanism only).
- Done when: dispatcher runs `RunTest` filter `EvalToolSchemaVisibilityTests`, all pass.
- Lane: test-writer (1 new file). Pairs with: Step 19.

### Step 21 - Leftover-stub search and verification
- Files: none edited (search and run)
- Depends on: 1-20
- Effect: text-neutral.
- Change / checks, in order:
  1. `Search(mode: text, query: "NotImplementedException|TODO implement \\(Step|TODO contract")` over RoslynSentinel.AgentLoop, RoslynSentinel.Tests.Harness and RoslynSentinel.Tests.ModelEval: no match introduced by this plan (fail if any remains in ScenarioTask.cs, WorkflowContract.cs, AgentSystemPrompts.cs or EvalContractOptions.cs).
  2. `Search(mode: text, query: "ComposePrompt\\(AgentSystemPrompts\\.CodingAgent\\)", fileGlob: "RoslynSentinel.Tests.ModelEval/**/*.cs")`: no match.
  3. `Build` (default fullBuildThrowaway, solution): 0 errors; compare warnings with docs/known-build-warnings.Solution.txt and report only new ones.
  4. `RunTest` (one at a time): the classes named in Steps 15-20, plus the existing `SchemaLeanProfileTests` and `SchemaDataTagOptionTests` (the Lean profile and DataTag options must behave as before Step 10), plus `StopAdherenceFixtureTests`, `StopAdherenceScenariosTests`, `ImplementationScenarioGoldenTests` methods `Budget_FitsContextWindow` (TestCase per scenario id) and `Registry_IdsUniqueAndMatchClassNames` (the scenario prompts are unchanged, so these pass exactly as before); compare failures with docs/known-failing-tests.Solution.txt (baseline is 0 failures; any failure is new).
  5. Dry check of the switch without a model: run `EvalContractOptionsTests` once with `ROSLYNSENTINEL_MODELEVAL_CONTRACT=standing` set in the shell and confirm `ComposeSystemPrompt_StandingOffNoFile_EqualsContract` still passes (it uses explicit arguments, so it must not depend on the variable).
  6. No scenario prompt or grader changed: `Git(operation: diff)` over the RoslynSentinel.Tests.ModelEval/ImplementationScenarios folder, `ImplementationScenarioChecks.cs` (locate it with `Search(mode: symbol)`) and RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs shows no hunk except the single `OfferFullToolSchema` property added in `ImplementationScenario.cs` (Step 12). `Search(mode: text, query: "OfferFullToolSchema = true|OfferFullToolSchema: true")` over the ModelEval and Harness projects: no match (no scenario opts in).
  7. Verification rule (Steps 10 and 11 changed server source, so the running server is stale until restarted): after items 1-6 are green, commit (explicit file list, `Plan:` trailer, `Closes:` on this plan's last commit), then restart this session's own server (`McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`, then `LoadSolution`) only when no subagent is mid-operation, and confirm `McpServerStatus` `lastLaunch.servedSha` is the commit that holds Steps 10-11. The in-process eval worlds use the freshly built test assemblies, so Steps 12-20 do not need the restart; a spawned `--hide-params` server does.
- Done when: items 1-7 hold. Outcome classes for the dispatcher's report: stub-free / stub-left, build-clean / new-warning / error, tests green / new failure, scenarios untouched / scenario diff found, served-sha current / stale.
- Lane: dispatcher (plain `Search`, `Build`, `RunTest`, `Git` and `McpServerControl` calls; no supervisor agent).

## Out of scope / next

Out of scope for this plan: every scenario prompt and grader (nothing here edits one), the schema variant in the stamp, a global environment override of the hidden default, a usage rule for the `OfferFullToolSchema` opt-in (the property is a mechanism only), the Lean profile's own list and the `claude-lean` surface, and the PlanStepRunner and SubAgentEval prompt builders and servers (they keep `CodingAgent` and the full tool schema; a spawned server would need `--hide-params` in `ErrorPolicyWiring.ChildServerArgs`, deferred).

Next, in this order, outside this plan:

1. **First-slice audit** (proposal Phasing step 2): cold read of each ImplementationScenario and StopAdherence prompt, acceptable-outcome list, every grader check traced to a prompt sentence, the behavioural tests each scenario lacks. Output is a findings doc (being written separately).
2. **A separate rework plan**, written from the audit's findings (proposal Phasing step 3, Scenario authoring rules in section 3, Graders in section 5): each scenario becomes a realistic brief in `ScenarioTask` form with behavioural acceptance tests in the fixture's test project. It then covers controls, grader outcome classes, the A/B and the default switch (Phasing steps 4-7).

Notes for the rework plan (facts verified while planning this one; none is a step here):

- **WidenToReadOnlyDictionary as the WideningRejectedByGate twin: usable, with two caveats.** The condition holds (every fixture caller passes a `Dictionary` to what becomes an `IReadOnlyDictionary` parameter; in WideningRejectedByGate the callers use target-typed `new()` and a `[]` argument and a helper still takes a Dictionary, which is why the widening fails there). A completed run grades Pass: the hidden xUnit test requires exactly 10 public static methods with an `IReadOnlyDictionary<FilePath, string>` first parameter and runs a read-only wrapper through them, `CheckWidenedFiles` requires the four caller files byte-unchanged, no `new Dictionary<` in the two target files and no `cref` still spelling `Dictionary`, and the scope check limits changes to the two files. A stop with an unchanged tree fails the hidden test and `ImplementationScenarioAgentTests.RunStepAsync` already relabels it `Blocked` (non-Pass plus `StopMarker`), so a false BLOCKED is already a distinct non-Pass outcome; the rework only has to name it. Caveat 1: it is a different fixture folder with 10 parameters across two files plus `<see cref>` edits against 3 signatures, so a failure may come from cref or edit trouble rather than condition handling, and its stop clause ("a method body mutates the map") never fires. Caveat 2: it is longer, so a cap hit is not evidence of over-blocking. Recommendation: use it as is; write a literal twin (the WideningRejectedByGate seed with callers that do compile) only if the A/B shows false-BLOCKED that this fixture cannot separate from cref difficulty.
- **WideningRejectedByGate grader (owner decision: unchanged in this plan).** `CheckOutcome` accepts outcome (a) nothing changed plus a report and outcome (b) an `IReadOnlyDictionary` overload beside the old signature with the callers unchanged. The rework records (b) as its own outcome class.
- **Graders that read the final message** (so a `Report` field is load-bearing for them): WideningRejectedByGate (`CheckReportedOutcome`), PlanStalenessReview (`CheckReview`), SweepConstructionSites (`CheckSweep`); StopAdherence scores a `BLOCKED:` marker plus an absence statement (`ClassifyStopAdherence` 129-150, `ScenarioScorer` StopAndReport).
- **Seam to use for a reworked prompt.** `private const string StepPrompt` in each scenario file becomes an expression-bodied property `private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(...))`, not a `static readonly` field: WidenToReadOnlyDictionaryScenario declares `StepPrompt` after `Definition`, so a field would still be null when `Definition` is built. The StopAdherence prompts (`MissingFilePrompt`, `MissingMethodPrompt`) are `public const string` used as `PromptTemplate` with one `{0}` placeholder; as `public static readonly string` initialised with the same call (declared before the `ScenarioDefinition` fields) they keep their names, which `StopAdherenceScenariosTests` and `StopAdherenceFixtureTests` reference. A prompt that goes through `string.Format` must contain no other brace. `ImplementationScenarioGoldenTests.Budget_FitsContextWindow` (24000 characters of seed plus longest prompt) is the prompt-growth guard.
- **Label reads oddly when `Conditions` is empty.** `If a condition fails:` then follows no `Conditions:` line for plain change tasks. The label is kept because the contract text cites it; the A/B measures it. Alternative if it confuses models: relabel to `If you stop:` (one constant in `ScenarioTask.Render` plus the contract wording).
- **The environment is read when a prompt is built.** `EvalContractOptions.Render` reads `ROSLYNSENTINEL_MODELEVAL_CONTRACT_ENDINGS` while the scenario registry initialises (test-case discovery, before `ModelEvalAssemblySetUp`), so a bad value surfaces as a `TypeInitializationException` whose inner `ArgumentException` names the variable. Acceptable (a typo fails loudly, like `EditArmToolSurface.Parse`).

## Risks and open decisions

No owner decision is open for this plan. Decided by the owner and recorded in the Decision section and the notes above: the A/B arms are old versus new contract on the same prompts at the same commit with no pinned pre-migration arm and no comparability goal; the WideningRejectedByGate grader stays unchanged here; scenario migration is replaced by audit and rework.

**R1 - ImplementationScenario has no CSV.** Only `transcript.json` carries `ContractHash` for these runs (Decision 4, scope of the CSV stamp); the proposal's "per-sample CSV" stamp serves the StopAdherence family. Cross-checking an ImplementationScenario A/B therefore reads transcripts (or the evalqueue job's recorded variables), not results.csv.

**R2 - Positional CSV readers.** The two columns are appended, `Parse` accepts the 36-column rows, and rescoring reads by name. I found no script that reads the per-sample CSV by position (scripts mention `errorPolicyHash` only in unrelated contexts); if one turns up it must tolerate two trailing columns.

**R3 - Hiding is applied at the server's schema layer, not the harness's (hosting verified).** The harness hosts the server in process, and its tool listing and calls go through `McpToolSchemaPatcher.CreateSentinelTool` and the `ArgumentValidation` call-tool filter, so the existing machinery applies and the harness-layer fallback is not planned. If a future eval world is built without `AddRoslynSentinelToolsBasic`/`Advanced` (none today: every `ModelEvalMcpEnvironment.CreateAsync` caller goes through it), the hiding would not apply there; `NoRegisteredScenarioOptsIn` and the `Default_*` tests would not catch that, so keep new worlds on `ModelEvalMcpEnvironment.CreateAsync`.

**R4 - Process-static state.** `SchemaOptions`, `HiddenSchemaParams` and the validator's `SchemaCache` are process-wide and keyed by tool name. Step 12 resets all three per world and Step 11 makes the refusal ignore a stale hidden registration, which is what lets a default world and an opt-in world coexist in turn. They would not coexist concurrently: the ModelEval and Harness assemblies have no assembly-level `Parallelizable` (only the Server, Basic, Advanced, Tools and Battery test assemblies do; verified by search), so fixtures run sequentially; if parallelism is ever added there, mixed hidden/opt-in worlds would race and the opt-in scenario's fixture would need `[NonParallelizable]`. Accepted, not mitigated: a Tests.ModelEval test that lists tools without `CreateAsync` (for example `ChainEvalToolSurfaceLeanTests`, which builds its own server) sees whatever the last world left in the statics; those tests assert on tool names, not parameters.

**R5 - The schema variant is not stamped.** Hiding `validateOnApply` changes what models are offered but is not recorded in transcripts or the CSV; the commit that holds Step 12 identifies it (evalqueue jobs pin commits). Stamping it is out of scope; add it only if a runtime switch is ever introduced.

**R6 - Refusal text names "schema configuration".** Step 11 rewords the one clause because the hide can now come from `--hide-params`; no test pins the old words (searched). The text stays facts and options only.

**R7 - Contract wording is first draft.** All contract text is the proposal's draft; per the proposal the exact wording is settled by the A/B, not in review. A tool name flagged by `Prompts_NameNoToolOutsideTheAllowList` (Step 17) is reported, not edited around.

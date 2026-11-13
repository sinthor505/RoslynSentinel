# Give ModelEval a from-scratch 15-tool surface instead of the 55-tool Refactor+Workspace+WholeFileWrite set

**Status:** PROPOSED 2026-11-13. The owner asked for the ModelEval tool list to be rebuilt from scratch. This doc proposes the list from a prompt, assertion and usage review; nothing is built.

## Motivation

- **Size.** ModelEval offers the model 55 tools: modes `Refactor`, `Workspace` and `WholeFileWrite`, minus an 11-name hide list (`RoslynSentinel.Tests.ModelEval/ChainEvalToolSurface.cs:22-32`). Their schemas and descriptions come to about 114k chars, roughly 28.5k tokens. That is most of the ~23k-token turn-2 prompt measured in `eval_error_variant_replay_validation.md`, which shows tool tokens dominating the prompt. Every turn pays it, and runs end `BudgetOut` (3 of 10 in the evening live arm F re-run).
- **Unused tools.** The evals are simple navigation and edit tasks. Across the 1,702 samples that ran on the current surface (2026-11-10 to 11-13), 25 of the 55 tools were never called: ConstructorParameter, ConvertAnonymousToNamed, ExtractLocalVariable, ExtractMembers, ExtractMethod, GetMethodSource, GetTypeInfo, Inline, InlineClass, InspectSymbol, Introduce, IntroduceParameterObject, InvertAssignments, ListProjectFrameworkTargets, ModifyAttribute, ModifyEnum, MoveAllTypesToFiles, MoveMember, MoveType, QuerySymbolRelationships, SyncInterface, SyncTypeAndFilename, TypedMember and WrapRange. `RunTest` was called only in the ten-chain.
- **Choice noise.** Near-duplicate tools split the model's choices. The first call after a file miss splits between `ListAll`, `ListSolutionItems` and `Search` (replay cells in the eval above). Calls such as `GetWorkspaceHealth` (72), `ProjectDoc` (42), `LocateSymbol` (27) and `FindReferences` (3) look like noise.

Evidence: a review of every ModelEval scenario and PlanStepRunner prompt, their assertions and verifiers, and a tally of 6,494 transcripts (2026-08-31 to 11-13). The tally scripts are in `c:\tmp\tally\`.

## Proposal

### Core list (12) and hedges (3)

| Tool | Capability | Evidence |
| --- | --- | --- |
| ReadFile | read a file, or one member via `memberName` | used in 100% of samples |
| GetFileOutline | outline a file | not-found hints; 15-30% of stop-family samples |
| Search | text, symbol, references, callers, declaration lists | 2,077 calls |
| ListSolutionItems | list files and folders (the only file lister) | 841 calls; the not-found orientation path relies on it |
| GetLargeResult | page large results | the LargeResult family; paging of Build and RunTest output |
| ReplaceSnippet | small edit | tool under test in 11 scenarios; 1,047 calls |
| Member | add, remove, replace or view a member | 44% of ten-chain samples |
| RenameSymbol | rename across the solution | rename scenarios, `ExpectedRecoveryTool` |
| CreateFile | new file | ten-chain step 7 (91%) |
| DeleteFile | remove a file | EmptyInterfaceRemoval (60%); the surface test requires it |
| Build | compile | 372 calls |
| RunTest | run tests | ten-chain (78%), PlanStepRunner |
| *GetMethodSource* (hedge) | read a method | never called, but the system prompt names it (`AgentSystemPrompts.cs:60`) |
| *UndoLastApply* (hedge) | revert an apply | `DeleteFile`'s description names it |
| *ChangeSignature* (hedge) | signature change | ten-chain step 5; rarely called |

The 15 tools cost about 8.2k tokens of schema, against 28.5k for the 55.

### Per-family extras (decided per family, not in the core)

- **Resume family.** Its seeded transcript (`stale-caller-edit-after-rename.json`) calls `ChangeAccessibility` and `ModifyModifier`. Either keep both for this family, or re-record the transcript on the lean surface. `ToolSchemaComparer` may flag them as `Missing`.
- **LargeResult family.** Keeps its existing two-tool allow-list (`LargeResultPagingAgentTests.cs:79`).
- **Legacy whole-file fixtures** (WholeFileRewrite, SizeThreshold, PlanOnly, PlanThenExecute, PlanImplementVerify). They test `ApplyDiff`/`WriteFile`, so they stay on the full surface.
- **PlanStepRunner.** Add `GetDiagnostics` and `UsingDirective`. The runner itself calls `LoadSolution`, `Build` and `RunTest` (`Program.cs:212,266,277`), so `LoadSolution` stays registered but should be hidden from the model. This needs a model-side filter or a new server argument. PlanStepRunner has `--hide-tools` (`ServerStartupHelpers.cs:113`) but no allow-list.

### Overlaps resolved

- **Symbol listing:** Search declaration modes replace `ListAll` (both cover the same 12 kinds).
- **Lookup:** Search `mode: symbol|references` replaces `LocateSymbol`, `FindReferences` and `PreviewRenameImpact`.
- **Diagnostics:** Build `quickBuild` replaces `GetDiagnostics`, except in PlanStepRunner.
- **Modifiers and declarations:** `ReplaceSnippet` or `Member` replace `ChangeAccessibility`, `ModifyModifier`, `ModifyBaseType` and `UsingDirective`.

### Text audit: model-facing text must only name tools on the list (CLAUDE.md)

| Where | Fix |
| --- | --- |
| `Common/AgentLoop/AgentSystemPrompts.cs:46,51,140` | names `ListAll`: change it to `ListSolutionItems`/`Search` |
| Scenario prompts ("go straight to ReadFile/Search/ListAll"): `FaultScenarios.cs:30,76,113,146`, `GuardScenarios.cs:36,189`, `InterfaceScenarios.cs:38`, `StopAdherenceScenarios.cs`, `RenameScenarios.cs`, `TwoRenameScenarios.cs`, `OrderPricingRefactorChainAgentTests.cs:258,360,496,678,869,1083,1447` | the same rewording |
| `Common/SymbolWireResolver.cs:147,174` | passes `isCallable: null`, so `SymbolNotFoundDetail.cs:176` names `ListAll` regardless of surface; pass the real `isCallable` |
| `Server.Advanced/ToolArgumentValidator.cs:473`, `Tools.Basic/WorkspaceReadNavigationImpl.cs:649,1268`, `Engines.Basic/ProjectStructureEngine.cs:80` | they name `ListAll` (and `ProjectDoc`); make the hint surface-aware through `ActiveToolSurface.IsCallable` |
| Descriptions: `Member` (names TypedMember), `RenameSymbol` (UsingDirective), `ListSolutionItems` schema (ProjectDoc) | drop the cross-reference or make it conditional |
| `PlanStepRunner/Program.cs:242` | names `LoadSolution`, `ListWorkspaceSolutions`; `RunnerOptions.cs:99` has stale class names |

`FileNotFoundMessage.cs:86-245`, `SymbolNotFoundDetail.cs:163-176` and `OrientationCircuitBreaker.cs:76-94` already drop a hint when the tool is not callable.

Error and hint text changes affect every caller, not only ModelEval. The surface-aware route (`ActiveToolSurface.IsCallable`) keeps the full-surface text unchanged.

### Mechanism and rollout

1. **Switch.** In `ChainEvalToolSurface.NewHiddenTools()`, when `ROSLYNSENTINEL_MODELEVAL_TOOLSET=lean`, return every discovered tool (`McpToolSchemaPatcher.DiscoverToolMethods`, `Common/McpToolSchemaPatcher.cs:222`) minus the allow-list. Outside claude-lean, the hide list already acts as "all minus hidden" (`ServiceRegistrationExtensionsBasic.cs:205-210`), and the hint text reads the same surface.
   - The four call sites take this list already: `ScenarioAgentTests.cs:425-427`, `TranscriptResumeAgentTests.cs:207-209`, `OrderPricingRefactorChainAgentTests.cs:119-126` and the surface test.
   - The chain test's `EditSnippetsExperiment` arm (`:121-122`) needs its own handling.
   - The pattern follows `ROSLYNSENTINEL_MODELEVAL_EDIT_ARM`.
2. **Text audit fixes** (table above), surface-aware.
3. **A/B, lean against full:** Control, MissingFile, MissingMethod, NoSuchMethod, Typo and one ten-chain, 30 samples each. Report pass rate, turns, `BudgetOut`, first-call split and wall time.
4. **Default.** Switch the default to lean, and re-baseline the families in use. Results before the switch are 55-tool results and are not comparable.
5. **PlanStepRunner.** Follows separately, with a `--allow-tools` argument or a computed `--hide-tools` list.

## Alternatives considered, not pursued

- **Base the surface on claude-lean.** It omits `ListSolutionItems`, which the file-not-found orientation path needs. It also adds admin and session tools (`LoadSolution`, `Git`, `WriteFile`, `McpServerControl`, ...) that make no sense on a temp fixture.
- **Trim the current hide list.** It starts from 55 and removes tools one by one, so it never asks what the tasks need. The owner asked for a from-scratch list.
- **Gate tools through toolsets (`McpToolsetControl`).** A weak model would have to manage the gating, which adds a failure mode the evals do not test.

## Open questions

- **Hedges:** keep all three (GetMethodSource, UndoLastApply, ChangeSignature), or start at 12 and add on evidence?
- **Resume family:** keep `ChangeAccessibility`/`ModifyModifier`, or re-record the resume capture on the lean surface?
- **ListAll's row cap** compared with Search's 30-row declaration cap was not checked. Search replacing ListAll may page more.
- **Scoring:** whether a scenario's scoring or `ToolSchemaComparer` changes when tools are hidden was not checked.
- **Replay** (`RoslynSentinel.Tests.ErrorVariants`) sends each source transcript's own tool list. Lean screening needs lean source runs.

## Cost / risk

- **Touches:** `ChainEvalToolSurface` (the switch), prompt strings in `AgentSystemPrompts.cs` and the scenario files, the not-found hint call sites in `Common`/`Tools.Basic`/`Engines.Basic`/`Server.Advanced` (surface-aware, so no change for the full surface), and three tool descriptions. The description edits change every caller's schema, so check `ClaudeLeanSchemaBudgetTests`.
- **Baseline break:** every ModelEval result to date is a 55-tool result.
- **Harder, not impossible:** ten-chain step 3 (accessibility) falls back to `ReplaceSnippet`/`Member`; live ten-chain used `ChangeAccessibility` in 22% of samples. The stop family loses its `ListAll` route, which probably helps stop adherence.
- **Out of scope:** the legacy whole-file fixtures, which stay on the full surface.

## Related

- `docs/current/evals/eval_error_variant_replay_validation.md` (prompt-size and first-call split evidence).
- `docs/current/plans/plan_error_variant_replay.md`.
- `docs/current/plans/closed/plan_error_policy_pointer.md` (the not-found wording arms, all on 55 tools).
- `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs` (claude-lean allow-list, used for comparison).

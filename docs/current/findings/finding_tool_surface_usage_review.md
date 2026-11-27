# Finding: 37 of 121 tools carry all measured use; 7 are duplicates and 77 can leave the main solution

**Status:** DECISION 2026-11-21. The four open decisions and the tier-2 layout are settled (see Decisions); the owner amends the 37/7/77 lists before step 1. Earlier: OPEN 2026-11-15.

## Context

The owner asked for a per-tool judgement of how likely each MCP tool is to be used, and which tools to
move out of `RoslynSentinel.slnx` into a rarely maintained "tier 2" solution. It follows
`docs/current/proposals/proposal_usage_based_core_split.md`, whose per-tool counts dated from 2026-10-19.

Owner positions stated for this review (2026-11-15):

- Weak local models struggle with basic tasks on the available hardware, so "a weak model might need
  it" is not a reason to keep a tool.
- Tier 2 is for tools not in active use but not yet archived. It references the main projects and is
  maintained less often.
- Removing a single unused tool is worth doing even without its engine, because engine return-type
  changes fan out to every tool wrapper.
- Claude Code defers MCP tool schemas (names only at startup), so the claude-lean token budget no longer
  justifies gating tools for Claude sessions.

Sources, all gathered 2026-11-15:

1. Journal recount: every `.claude/journal/*.calls.jsonl` file (231 files, 2026-10-01 to 2026-11-15),
   rows with `kind` = `mcp`, grouped by `tool`.
2. Tool list: `docs/generated/architecture_tools.md` (51 Tools.Advanced, 66 Tools.Basic and
   4 Tools.Experimental rows = 121 tools).
3. Three read-only audit agents: Advanced and Experimental dependencies; low-use Basic tool
   dependencies; guidance references and model-eval usage.

The dependency facts and `file:line` references below are as reported by those audit agents. They were
not re-checked line by line, line numbers and line counts are approximate, and anything the agents could
not confirm is marked UNVERIFIED.

## What is broken

Nothing is defective. The finding is that most of the maintained tool surface has no measured use.

### Claude sessions (journal)

80,195 MCP calls hit 54 distinct tool names. "Last 30" counts calls on or after 2026-10-16.

| Tool | Calls | Failed | Last 30 | Days used | Last used |
| --- | --- | --- | --- | --- | --- |
| ReadFile | 22518 | 377 | 17152 | 39 | 2026-11-14 |
| Search | 19400 | 1004 | 15493 | 39 | 2026-11-14 |
| ReplaceSnippet | 6781 | 862 | 5168 | 39 | 2026-11-14 |
| Git | 5990 | 96 | 4659 | 40 | 2026-11-15 |
| GetMethodSource | 5660 | 254 | 4707 | 39 | 2026-11-14 |
| RunTest | 4885 | 99 | 3596 | 39 | 2026-11-14 |
| Member | 4130 | 495 | 3541 | 35 | 2026-11-14 |
| GetFileOutline | 2912 | 95 | 2389 | 39 | 2026-11-14 |
| Build | 2805 | 102 | 2137 | 39 | 2026-11-14 |
| GetLargeResult | 1262 | 52 | 855 | 38 | 2026-11-14 |
| CreateFile | 815 | 84 | 710 | 34 | 2026-11-14 |
| McpServerStatus | 442 | 10 | 318 | 38 | 2026-11-15 |
| LoadSolution | 394 | 27 | 208 | 38 | 2026-11-14 |
| WriteFile | 384 | 55 | 150 | 23 | 2026-11-14 |
| SemanticFindReplace | 332 | 8 | 332 | 3 | 2026-10-28 |
| ExternalFileDrift | 302 | 19 | 252 | 24 | 2026-11-09 |
| McpServerControl | 213 | 16 | 164 | 25 | 2026-11-14 |
| FindReferences | 188 | 50 | 0 | 10 | 2026-10-13 |
| DeleteFile | 123 | 2 | 102 | 27 | 2026-11-14 |
| McpToolsetControl | 114 | 2 | 99 | 26 | 2026-11-13 |
| InspectSymbol | 80 | 22 | 23 | 15 | 2026-11-14 |
| GetDiagnostics | 57 | 6 | 43 | 14 | 2026-11-14 |
| LocateSymbol | 55 | 1 | 0 | 5 | 2026-10-13 |
| GitRemote | 52 | 10 | 52 | 2 | 2026-11-13 |
| RenameSymbol | 47 | 4 | 36 | 16 | 2026-11-14 |
| MoveMember | 47 | 27 | 32 | 5 | 2026-11-10 |
| UsingDirective | 34 | 4 | 8 | 4 | 2026-11-13 |
| TagTestCategories | 21 | 2 | 0 | 1 | 2026-10-05 |
| ListExternalDiskChanges | 20 | 3 | 0 | 5 | 2026-10-08 |
| SubAgent | 18 | 7 | 1 | 4 | 2026-11-06 |
| AcknowledgeExternalFileChanges | 17 | 0 | 0 | 5 | 2026-10-08 |
| UndoChange | 13 | 3 | 7 | 9 | 2026-11-14 |
| ChangeSignature | 10 | 5 | 10 | 3 | 2026-11-04 |
| MoveType | 10 | 3 | 6 | 3 | 2026-11-10 |
| ListSolutionItems | 9 | 2 | 2 | 3 | 2026-10-28 |
| ExtractMembers | 9 | 2 | 9 | 1 | 2026-11-04 |
| ApplyDiff | 9 | 1 | 8 | 3 | 2026-11-04 |
| ApplyUnifiedDiff | 5 | 1 | 0 | 2 | 2026-10-12 |
| ChangeAccessibility | 5 | 1 | 0 | 2 | 2026-10-03 |
| ModifyModifier | 4 | 4 | 0 | 1 | 2026-10-02 |
| ProjectDoc | 3 | 0 | 3 | 2 | 2026-11-13 |
| GetWorkspaceHealth | 2 | 0 | 2 | 1 | 2026-10-28 |
| CreateProject | 2 | 0 | 2 | 2 | 2026-11-04 |
| QuerySymbolRelationships | 2 | 0 | 2 | 2 | 2026-11-10 |
| ListAll | 2 | 0 | 0 | 1 | 2026-10-01 |
| MoveAllTypesToFiles | 2 | 0 | 2 | 1 | 2026-11-04 |
| MethodSignature | 2 | 0 | 0 | 1 | 2026-10-02 |
| ModifyAttribute | 2 | 1 | 0 | 1 | 2026-10-01 |
| IsSessionHalted | 1 | 1 | 0 | 1 | 2026-10-02 |
| PreviewRenameImpact | 1 | 0 | 0 | 1 | 2026-10-13 |
| SafeDeleteUnusedSymbol | 1 | 0 | 0 | 1 | 2026-10-13 |
| ParameterEdit | 1 | 0 | 0 | 1 | 2026-10-13 |
| Inline | 1 | 1 | 1 | 1 | 2026-10-20 |
| ListWorkspaceSolutions | 1 | 0 | 0 | 1 | 2026-10-01 |

Notes:

- `ListExternalDiskChanges`, `AcknowledgeExternalFileChanges`, `IsSessionHalted` and
  `SafeDeleteUnusedSymbol` are not in today's tool list. Hypothesis: they are earlier names of
  `ExternalFileDrift` operations and `DeleteUnusedSymbol`.
- Every tool in the list that is absent from this table has zero Claude calls. That includes all of
  AsyncifyTools, IntelligenceTools, ScanTools, QualityTools, GenerationTools, CommentingTools,
  ModernizationTools, SubAgentEvalTools and the Experimental `CodeTransformTools`.
- The journal records Claude sessions in this repo only.

### Models under test (model-eval and PlanStepRunner)

- Tool surface: `RoslynSentinel.Common\AgentLoop\AgentToolAllowLists.cs:8-14` defines `Lean` (18 tools):
  ReadFile, GetFileOutline, Search, ListSolutionItems, GetLargeResult, ReplaceSnippet, Member,
  RenameSymbol, CreateFile, DeleteFile, Build, RunTest, GetMethodSource, UndoChange, ChangeSignature,
  ChangeAccessibility, ModifyModifier, UsingDirective. `PlanStepRunnerSurface` (line 19) adds
  GetDiagnostics. Model-eval defaults to the lean list since 2026-11-14
  (`RoslynSentinel.Tests.ModelEval\ChainEvalToolSurface.cs`).
- Calls across 2,945 non-archived model-eval runs, matched on `Turn N: calling <Name> with args` in
  `agent.log` (39 names): ReadFile 10797, ReplaceSnippet 4944, Search 4081, Build 1554,
  ListSolutionItems 1126, RenameSymbol 1122, Member 477, ListAll 463, CreateFile 431, RunTest 332,
  GetLargeResult 323, GetFileOutline 170, ChangeAccessibility 110, GetWorkspaceHealth 90, LocateSymbol 74,
  ProjectDoc 73, UsingDirective 59, EditSnippets 52, Git 42, ModifyModifier 36, ModifyAttribute 27,
  ModifyBaseType 22, DeleteFile 21, UndoChange 19, PreviewRenameImpact 11, ChangeSignature 10,
  MethodSignature 10, RetryFailedChanges 9, DeleteUnusedSymbol 8, SummaryComment 8, GetDiagnostics 6,
  FindReferences 3, GetBestInsertionPoint 2, GetMethodSource 2, McpServerStatus 2, SyncTypeAndFilename 2,
  TypedMember 2, GetOperationDetail 1, GetTypeInfo 1.
- 13 PlanStepRunner logs (23 names): ReadFile 182, ReplaceSnippet 66, SearchSolutionText 65 (a stale
  name, not in today's list), Member 35, RunTest 17, Build 15, LocateSymbol 13, ChangeAccessibility 8,
  CreateFile 6, UndoChange 6, UsingDirective 6, GetDiagnostics 4, ListAll 2, ListSolutionItems 2, and
  one call each of GetBestInsertionPoint, GetLargeResult, GetMethodSource, GetOperationDetail,
  GetWorkspaceHealth, InspectSymbol, ModifyAttribute, ModifyEnum, SummaryComment.
- No Advanced or Experimental tool was called by a model under test. Neither were ExtractMethod or
  ExtractLocalVariable.
- UNVERIFIED: the run counts were not reconciled (2,941 against 2,945 between two passes), and calls
  logged without arguments would be missed.

## Root cause

Why so much unused surface is still maintained, and why it is hard to drop:

1. **Tools were sorted by sophistication, not use.** This is the proposal's original observation and the
   recount confirms it: only 4 Advanced tools were ever called (SubAgent 18, MoveType 10,
   ExtractMembers 9, Inline 1).
2. **Several tools are second front ends over the same code.**
   - `Declaration` calls the same implementation as ModifyModifier, ChangeAccessibility, ModifyAttribute
     and ModifyBaseType (`RoslynSentinel.Tools.Basic/DeclarationTools.cs:86,104,132,165`).
   - `ParameterEdit` delegates to the MethodSignature and ConstructorParameter implementations
     (`ParameterEditTools.cs:85,107`).
   - `Search` delegates to the implementations behind LocateSymbol, FindReferences, ListAll and
     QuerySymbolRelationships(attribute) (`WorkspaceReadNavigationTools.cs:172-195`).
   - `EditSnippets` calls ReplaceSnippet with the batch list (`EditSnippetsTools.cs:32`).
   - `ApplyDiff` handles both the full-files and unified-diff formats; `ApplyUnifiedDiff` is a separate
     tool method over the same `DiffEngine`.
3. **Most low-use Basic tools are thin wrappers** (7 to 40 lines) over shared implementation classes, so
   removing a wrapper alone saves little engine code. The exceptions are listed under "Engine code that
   can leave" below.
4. **The main solution references Advanced by type, not only by name.**
   - `Server.Advanced` calls `AddRoslynSentinelEnginesAdvanced` (`ServerStdio.cs:114`, `ServerHttp.cs:85`),
     registers all 11 Advanced and Experimental tool classes
     (`ServiceRegistrationExtensionsAdvanced.cs:69,88-142`) and uses
     `TaskEnabledToolsHelper.SelectExecutionMode` (`ServerStdio.cs:132`, `ServerHttp.cs:93`).
     `TaskEnabledToolsHelper` lives in Tools.Advanced although it is the server's task selector.
   - Tests.Server has 14 files with `using` of Tools.Advanced or Tools.Experimental, and Tests.ModelEval
     has 21. Both build the full Advanced DI graph. Tests.SubAgent has 3.
5. **Tools are also coupled by name in `Common` and in tests.**
   - `RoslynSentinel.Common/ErrorPolicyIntentTable.cs:40-155` has one row per tool, about 24 of them for
     Advanced and Experimental tools. Tests.Server enforces it both ways
     (`EveryDeclaredTool_HasATableEntry`, `TableNamesNoToolThatIsNotDeclared`) and asserts exactly three
     scanned tool assemblies.
   - `RoslynSentinel.Common/ToolsetCatalog.cs:45-47` lists 10 AdvancedRefactoringTools names inside the
     `moveExtract` toolset; line 65 names SubAgent.
   - Other by-name references: `ToolArgumentValidator`, `AgentIdentityCapture`, `WriteToolAdviceHelper`
     (Server.Advanced); `EditImpactAnalyzer.NamedTools` (Engines.Basic, names MethodSignature and
     ChangeAccessibility); `McpToolSchemaPatcher.cs:477` (EditSnippets); `ServerStdio.cs:33`
     (`typeof(DocumentationTools)`).
   - Orientation hints name ListSolutionItems and ListAll
     (`ServiceRegistrationExtensionsBasic.cs:884-952`), guarded by `Callable()`.
6. **Guidance names tools that are not reachable.**
   - The CLAUDE.md dog-fooding table and `.claude/agents/implementer.md` name MethodSignature and
     ModifyModifier, which are not in claude-lean.
   - The `explore-tier1` and `explore-tier2` agent tool lists name FindReferences, LocateSymbol, ListAll
     and QuerySymbolRelationships.
   - `.claude/settings.json:235-277` pre-approves about 30 tool names, several of them low-use.
   - `scripts/roslynsentinel-modeleval.ps1:26,80` names ApplyDiff, ChangeAccessibility and ModifyModifier.

## Why it matters

- A change to an engine return type or a shared helper has to keep 121 tools and their tests compiling,
  while 27 tools carry more than 99% of the calls.
- Engines.Advanced is about 29,000 lines in 34 files. The Advanced test projects are about 59,000 lines
  (Tests.Advanced about 30K, Tests.Tools.Advanced 13.5K, Tests.Battery.Advanced 10K, Tests.Asyncify 5K).
- Duplicate front ends cost description and schema upkeep (`MergedToolDescriptionSyncTests` keeps the
  Declaration and ParameterEdit descriptions in step with six older tools) and give a model two ways to
  do the same thing.

## Recommendation

Nothing here is decided. Likelihood is the reviewer's judgement of future use by Claude sessions.

### Keep in the main solution (37)

| Tools | Evidence | Likelihood |
| --- | --- | --- |
| ReadFile, Search, ReplaceSnippet, Git, GetMethodSource, RunTest, Member, GetFileOutline, Build, GetLargeResult, CreateFile, McpServerStatus, LoadSolution, WriteFile | 384 to 22,518 calls each | Certain |
| SemanticFindReplace, ExternalFileDrift, McpServerControl, DeleteFile, McpToolsetControl, InspectSymbol, GetDiagnostics, GitRemote, RenameSymbol, MoveMember, UsingDirective, UndoChange, ChangeSignature | 10 to 332 calls each, all used in the last 30 days | High |
| MoveType, ListSolutionItems | MoveType 10 calls, 6 recent. ListSolutionItems 9 Claude calls, 1,126 eval calls, named in orientation hints | High |
| ExtractMembers, MoveAllTypesToFiles, ApplyDiff, CreateProject, ProjectDoc | 2 to 9 calls each, all recent; ProjectDoc first used 2026-11-10 | Medium |
| ParameterEdit | 1 call; the only tool that fixes call sites for constructor parameters | Medium |
| ChangeAccessibility, ModifyModifier | No Claude calls in 30 days; 110 and 36 eval calls; both in `AgentToolAllowLists.Lean` | Medium, eval only |

MoveType and ExtractMembers are in Tools.Advanced. Promoting them needs
`StructuralRefactoringEngine.MoveTypeToOuterScopeAsync` and `ExtractMembersToPartialWithSourceAsync`
moved into Engines.Basic; their other engine calls (`BasicRefactoringEngine.MoveTypeToFileAsync`,
`ExtractInterfaceAsync`) are already there.

### Delete outright (7)

No capability is lost; a kept tool runs the same code.

| Tool | Covered by |
| --- | --- |
| LocateSymbol, FindReferences, ListAll | Search (same implementation). No Claude calls to LocateSymbol or FindReferences since 2026-10-13 |
| EditSnippets | ReplaceSnippet batch form; exists only behind the `EditSnippetsExperiment` mode |
| ApplyUnifiedDiff | ApplyDiff with the unified-diff format |
| MethodSignature, ConstructorParameter | ParameterEdit (same implementation) |

Tests that name these: `RoslynSentinel.Tests.ModelEval\LoopAssistRunnerTests.cs:108,222,320-321,358`
(LocateSymbol) and `LargeResultPagingAgentTests.cs:57,73-74` (blocked-tool lists).

### Move to tier 2 (77)

Advanced and Experimental (53):

| Cluster | Tools | Likelihood |
| --- | --- | --- |
| AsyncifyTools (15) | AddCancellationToken, Asyncify, AsyncifyLoop, BridgeAsyncMethods, ClearAsyncMigrationCandidateFlags, EventHandlersToAsync, ExtractEventHandlers, GetAsyncMigrationProgress, GetAsyncMigrationState, GetMutationBreakerStatus, PropagateCancellationToken, ResetAsyncMigrationState, ResetMutationBreaker, ScanAsyncMigrationCandidates, UpliftCallers | Low |
| ScanTools (6) | AnalyzeMethod, DescribeScanDetectors, GetPublicApiSurface, RunScanDetector, ScanBreakingChanges, ScanDuplicateBlocksInClass | Low |
| IntelligenceTools (7) | GetCallGraph, GetCodeInventory, GetComprehensiveHealthReport, GetDiRegistrations, GetSolutionMetrics, PreviewMoveFileToNamespaceFolder, TraceVariableLifetime | Low; GetCallGraph is the likeliest to come back |
| QualityTools (5) | AnalyzeForeachForLinqConversion, AnalyzeSwitchForPatternConversion, DescribeAdvancedToolOptions, GetMethodComplexity, GetTestCoverageMap | Low |
| GenerationTools (4), CommentingTools (1), ModernizationTools (1) | GenerateClassesFromJson, GenerateDefaultConfigJson, GenerateHttpClient, InterpolateString, BulkComment, InvertBooleanLogic | Low |
| CodeTransformTools, Experimental (4) | ApplyClassCodeTransform, ApplyFileCodeTransform, ApplyMethodCodeTransform, Generate | Very low |
| Rest of AdvancedRefactoringTools (8) | Inline, InlineClass, Introduce, IntroduceParameterObject, InvertAssignments, ConvertAnonymousToNamed, SyncInterface, WrapRange | Low; Inline medium-low (1 call, failed) |
| SubAgentTools, SubAgentEvalTools (2) | SubAgent, SubAgentEval | Low to medium; 18 calls, 1 in the last 30 days (open decision 1) |

Basic (24):

| Group | Tools | Likelihood |
| --- | --- | --- |
| Whole classes with exclusive engines | NamedArguments, QualifyCrefs, TagTestCategories | Low; one-off jobs |
| Declaration family | Declaration, ModifyAttribute, ModifyBaseType | Low |
| Done by Member, ReplaceSnippet or RenameSymbol in practice | ModifyEnum, TypedMember, SyncTypeAndFilename, SummaryComment, ExtractLocalVariable, GetBestInsertionPoint | Low |
| Hard to do correctly by text edit | ExtractMethod, DeleteUnusedSymbol, QuerySymbolRelationships | Medium-low; 3 calls in total |
| Admin and listing | GetTypeInfo, PreviewRenameImpact, GetOperationDetail, GetWorkspaceHealth, ListProjectFrameworkTargets, ListWorkspaceSolutions, RetryFailedChanges, SplitProjectByFolder, Features | Low |

If the owner wants more than 37 tools kept, ExtractMethod, DeleteUnusedSymbol, QuerySymbolRelationships
and Inline are the first to keep.

`InvertBooleanLogic` may duplicate the `invertBoolean` mode of SemanticFindReplace. This is inferred
from the two tool descriptions only (hypothesis), so it is listed for tier 2, not for deletion.

### Engine code that can leave

- All of Engines.Advanced except the two methods promoted with MoveType and ExtractMembers.
  Unreached by any tool today: `InstrumentationEngine.cs` (153 lines) and `SecurityAndSafetyEngine.cs`
  (479 lines).
- Engines.Basic files whose only tool-facing callers are tier-2 tools: `NamedArgumentsEngine.cs` (510),
  the four `TestCategory*` files (about 1,400), `AttributeTextEditBuilder.cs` (220),
  `BaseTypeTextEditBuilder.cs` (81), `InventoryEngine` (89), `SemanticRefactoringEngine` (348),
  `SyntaxUpgradeEngine` (877), `ThreadSafetyEngine` (725).
- Stays because a kept tool needs it: `DocTrailerEngine.cs` (ProjectDoc), `ConstructorCallSiteFinder.cs`
  (ParameterEdit), `CrefQualifier` (MoveMember), `StructuralRefinementEngine` (Member remove),
  `MsToolAugmentEngine` (shared).
- `Common` types with no non-Advanced production caller: `ToolGraph`, `ToolDescriptor`, `FailureRouter`,
  `MigrationLedger`.
- Dead already: `MemberRefactoringEngine.ExtractMethodAsync` (220 lines) and
  `ConvertMethodToPropertyAsync` are called only by tests; `MakeMethodStaticAsync` only by
  `CodeTransformTools`.

### Order of work, by leverage

1. Delete the 7 duplicate wrappers and fix the guidance that names them (root cause 6). Small, needs no
   tier 2.
2. Promote MoveType and ExtractMembers into Tools.Basic with their two engine methods; move
   `TaskEnabledToolsHelper` out of Tools.Advanced; move the Basic-tool tests still in
   Tests.Tools.Advanced (the `MoveMember*` files) to Tests.Tools.Basic.
3. Rewire Tests.Server and Tests.ModelEval onto the Basic registration, and make the by-name tables and
   the three-assembly assertion tolerate tools that live outside the main solution.
4. Move Tools.Advanced, Tools.Experimental, Engines.Advanced and their test projects to tier 2, with a
   thin tier-2 host project so the main server never references tier 2.
5. Move the 24 Basic tools in batches, whole classes first (NamedArgumentsTools, QualifyCrefsTools,
   TestCategoryTaggingTools, TypedMemberTools, DeclarationTools).
6. Expose every kept tool in the Claude mode without toolset gating, then re-run the journal recount
   after about four weeks.

Tier-2 layout, recommended: a second solution in its own folder of this repo, excluded from searches the
way `*/Worktree/` is. It isolates the same way a separate repo would, without cross-repo project
references, and can be split out later. The owner raised a separate repo; this is open.

### Open decisions (owner)

1. **SubAgent.** Listed for tier 2, but this session's server runs with `claude-lean,subagenttools`. Keep
   it in the main solution if delegation to local models continues. Both classes depend only on `Common`
   (about 1,100 lines in 7 files).
2. **ChangeAccessibility and ModifyModifier.** They stay only because `AgentToolAllowLists.Lean` lists
   them. If the eval list drops them, they can leave with the Declaration family.
3. **Features.** Once it leaves, feature flags change only through configuration. The
   `DeleteUnusedSymbol` flag also gates Member(remove) on types.
4. **Gating.** With 37 tools exposed in the Claude mode, McpToolsetControl and most of
   `ToolsetCatalog` become removable.

## Out of scope

- Tests.Integration: not in `RoslynSentinel.slnx`, not searched.
- Plain-prose mentions of tool names in `.claude/agents` and `.claude/skills`: only backticked,
  tools-list and `mcp__` forms were searched.
- Per-engine test file mapping, and which private helpers inside `GitImpl` and
  `RefactoringStructuralImpl` are shared.
- "Injected but never called" constructor parameters (ModernizationTools takes 10 unused services,
  QualityTools 5, IntelligenceTools 3) were judged from `_field.Method(` calls only.
- Rewording the proposal's slices against this finding; that follows the owner's decisions.

## Decisions (todo review 2026-11-21)

- **SubAgent:** stays in main. Why: the delegation path to local models; depends only on Common.
- **Eval lean list ChangeAccessibility / ModifyModifier:** both stay. Why: 110 and 36 eval calls; dropping them breaks baseline comparability.
- **Features:** stays in main (owner; differs from the review's tier-2 recommendation).
- **Claude-mode gating:** kept. Why: `McpToolsetControl` is heavily used (114 calls) and the tab-scoped persistence plan is in flight; revisit when the core reaches its post-move size.
- **Tier-2 layout:** a second solution in this repo, excluded from default searches and builds.
- **The 37 / 7 / 77 split:** not accepted yet; the owner amends the lists here first. Then step 1 (delete the 7 duplicates; update guidance and the not-found text that offers ListAll, `SymbolNotFoundDetail.RenderNoCandidates`, with its golden tests) becomes READY. Acceptance for step 1: the 7 are gone from `docs/generated/architecture_tools.md` and the Tests.Server by-name tables pass.
- Note since the review: the claude-lean schema budget was raised to 46,500 chars (d2dcdc33), and ReplaceRange (proposals/closed/proposal_replace_range_tool.md) and MoveFile (proposals/closed/proposal_move_file_tool.md) are decided as new core tools.

## Decisions (todo review 2026-11-22)

- **ListAll:** stays in the delete set but leaves in its own step 1b, after the other six. Step 1b
  retargets every reference to `Search(mode: ...)` (Search runs the same implementation; hint pairs
  `('kind', X)` become `('mode', X)`): about 105 code matches in about 40 files - hint builders
  (`SymbolNotFoundDetail.RenderNoCandidates`, `OrientationCircuitBreaker`, `BuildEngine.ListingCall`,
  `ToolArgumentValidator.BuildParameterHints`, `ProjectStructureEngine` messages), name-keyed server logic
  (`IsEmptyListAllResult`, the orientation-breaker allowlist, `ErrorPolicyIntentTable`), about 60 test pins,
  and eval surfaces (`AgentToolAllowLists.Lean`, ModelEval and PlanStepRunner tool lists, agent-definition
  tool lists); lean-eval baselines restart after it (463 eval calls). Medium, roughly 4-6 slices.
  Why: the owner wants the duplicate gone, but its eval and hint reach is far larger than the other six.
  Acceptance: `Search(text, "\"ListAll\"")` finds nothing outside history docs; Tests.Server by-name tables pass.
- **The lists:** the owner amends the keep / delete / tier-2 lists (including the 2026-11-21 moves of
  SubAgent and Features to main) personally; step 1 stays gated on that.

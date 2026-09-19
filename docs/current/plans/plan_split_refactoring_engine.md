# Split RefactoringEngine.cs and retire the Standard/Advanced/Granular engine naming split

## Context

`RoslynSentinel.Basic/RefactoringEngine.cs` is 5928 lines, ~90 methods — the largest engine in the
solution by a wide margin, confirmed via `GetFileOutline`. It grew by accretion: nearly every new
refactoring capability over this project's history was added as another method on this one class,
rather than to a class grouped by what the method actually does.

Separately, three other engines exist that look like a deliberate capability tier
(`StandardRefactoringEngine`, `AdvancedRefactoringEngine`, `GranularRefactoringEngine`,
`SemanticRefactoringLibrary`) but are not one: each is a small (150-400 line) grab-bag of a handful
of unrelated transforms, split from `RefactoringEngine` at different points in time with no shared
interface or inheritance relationship between them. `StandardRefactoringEngine` was confirmed (via
the companion Server.Basic tool-class audit, see `plan_split_workspace_refactoring_tools_for_di.md`)
to have exactly one live consumer solution-wide: `SentinelCodemodTools.cs` in
`RoslynSentinel.Server.Advanced`. Its injection into `SentinelRefactoringTools`
(`RoslynSentinel.Server.Basic`) was dead code, already removed as part of that class's cleanup.

This plan is the engine-side counterpart to that tool-class plan: it does not touch any
`[McpServerTool]` method or any `Server.Basic`/`Server.Advanced` file. It only reorganizes
`RoslynSentinel.Basic`'s engine classes so each one has a coherent, nameable responsibility and a
bounded size, and retires the misleading Standard/Advanced/Granular naming.

**Sequencing relative to the tool-class plan**: do the tool-class-side split
(`plan_split_workspace_refactoring_tools_for_di.md`) first. Its `*Impl` classes, once extracted,
will show — cluster by cluster — exactly which `RefactoringEngine` methods each Server.Basic tool
actually calls, which cross-checks the groupings below before they're locked in. This plan can be
scoped and drafted in parallel but should not start file-splitting until that one's Decision 7
step 3 (SentinelRefactoringTools split) has landed and built green.

## Facts confirmed by reading the actual file (via GetFileOutline, not assumption)

`RefactoringEngine.cs`, `RoslynSentinel.Basic` namespace, 5928 lines, single class `RefactoringEngine`
(lines 34-5913) plus a handful of small result records at file scope. Full method inventory grouped
by what each method actually does (line numbers from the outline read on 2026-09-17):

- **Formatting/analysis, stays put**: `FormatDocumentAsync` (68-89), `FormatDocumentPreviewAsync`
  (5830-5853), `ComputeFormatHunks` (5861-5912), `AnalyzeControlFlowAsync` (1990-2032),
  `AnalyzeDataFlowAsync` (2034-2085), `WrapInTryCatchAsync` x2 overloads (4094-4181, 4187-4303),
  `WrapInRegionAsync` x2 overloads (4978-5016, 5022-5078).
- **Signature/parameters**: `ChangeSignatureAsync` (91-323), `AddRemoveParamsAsync` (1046-1093),
  `AddConstructorParameterAsync` (4305-4424), `RemoveConstructorParameterAsync` (4432-4564),
  `GetConstructorParametersAsync` (4572-4625), `AddMethodParameterAsync` (4684-4774),
  `RemoveMethodParameterAsync` (4787-4976), `GetMethodParametersAsync` (4634-4667).
- **Extraction**: `ExtractMethodAsync` (325-543), `ExtractInterfaceAsync` (767-834),
  `ExtractConstantAsync` (1635-1732), `ExtractLocalVariableAsync` (1734-1928), `HasSideEffects`
  (1930-1958), `InferVariableName` (1960-1988).
- **Type/file organization**: `MoveTypeToFileAsync` (545-604), `MoveAllTypesToFilesForDocumentAsync`
  (606-657), `BuildSplitFileRoot` (663-680), `RemoveOrphanedRegionDirectives` (683-707),
  `MoveAllTypesToFilesAsync`/`InProjectAsync`/`InSolutionAsync` (709-765),
  `ConvertToPrimaryConstructorAsync` (1441-1499), `ConvertExpressionBodyAsync` (1501-1633),
  `ConvertIndexerToMethodAsync` (980-1044).
- **Member CRUD**: `AddMemberAsync` (1166-1256), `AddTopLevelTypeAsync` (1267-1358),
  `RemoveMemberAsync` (1360-1439), `ReplaceMemberAsync` (1095-1164), `AddPropertyAsync`
  (4013-4018), `AddFieldAsync` (4020-4042), `SortMembersAsync` (4044-4092),
  `InsertMemberAfterAsync`/`BeforeAsync` (2526-2685), `GetContainerMembersAsync` (5313-5378).
- **Attribute/base-type/modifier batch + single-item**: `ApplyModifierBatchAsync` (3122-3225),
  `ApplyAttributeBatchAsync` (3235-3374), `ApplyBaseTypeBatchAsync` (3384-3480), `AddAttributeAsync`
  (2687-2790), `ReplaceAttributeAsync` (2865-2957), `RemoveAttributeAsync` (2969-3035),
  `GetAttributeName` (2959-2967), `AddBaseTypeAsync` (2792-2863), `RemoveBaseTypeAsync`
  (3037-3106), `ChangeAccessibilityAsync` (3483-3561), `AddModifierAsync` (3563-3639),
  `RemoveModifierAsync` (3641-3716).
- **Doc comments**: `AddSummaryCommentAsync` (3718-3733) + `AddSummaryCommentCoreAsync`
  (3742-3821), `RemoveSummaryCommentAsync` (3901-3970), `GetSummaryCommentAsync` (3972-4011),
  `BuildDocCommentText` (3830-3869), `NormalizeSummaryText` (3875-3899),
  `UpdateXmlDocsFromSignatureAsync` (5698-5823).
- **Enum**: `ModifyEnumAsync` (2234-2414), `AddEnumMemberAsync` (2426-2473),
  `RemoveEnumMemberAsync` (2480-2500), `ReplaceEnumMemberAsync` (2507-2524).
- **Using directives**: `AddUsingDirectiveAsync` (2087-2155), `RemoveUsingDirectiveAsync`
  (2157-2201), `GetUsingDirectivesAsync` (2203-2219).
- **Rename/interface sync**: `RenameSymbolAsync` (836-876), `FindResidualMentionsAsync` (885-918),
  `TryResolveUpdatedHandleAsync` (920-978), `SyncInterfaceToImplementationAsync` (5531-5696).
- **Shared name/type/member resolution helpers** (called from nearly every cluster above):
  `GetMemberName` (5080-5102), `ResolveMemberByNameOrSnippet` (5118-5169),
  `ResolveMemberOrEnumMemberByNameOrSnippet` (5182-5232), `TryGetEnumMemberContainerNameAsync`
  (5243-5268), `IsEnumContainerAsync` (5278-5302), `NormalizeTypeName` (5393-5408),
  `ResolveTypeByNameOrSnippet` (5420-5450), `BuildMemberHint` (5452-5473),
  `BuildContainerNotFoundMessage` (5487-5506), `BuildTypeHint` (5508-5529),
  `DisplayTypeForExtractedSignature` (62-66).

Peer engines confirmed by file existence and prior audit, not yet individually outlined in this
pass — outline each before moving its methods, per the task list below:
- `StandardRefactoringEngine.cs` (~160 lines) — 1 live consumer (`SentinelCodemodTools.cs`,
  Server.Advanced).
- `AdvancedRefactoringEngine.cs` (~359 lines).
- `GranularRefactoringEngine.cs` — contains `RunMicroRefactoringAsync`, already flagged in
  `docs/current/TODO.md`'s NormalizeWhitespace entry as having 5 dispatched helpers returning bare
  `SyntaxNode?` with no way to identify which sub-node changed — read that TODO entry before moving
  this engine's content, the formatting-scope issue travels with the code.
- `SemanticRefactoringLibrary.cs`.

## Decision 1 — New engine groupings

Ten new focused engines replace `RefactoringEngine.cs`'s single 5928-line class, plus one static
helper class. Every engine below lives in `RoslynSentinel.Basic/`, same namespace, same
constructor shape as today (`ILogger`, `IWorkspaceManager`, `SentinelConfiguration` where used).

| New engine | Methods (from RefactoringEngine.cs unless noted) | Approx. lines |
|---|---|---|
| `MemberEditEngine` | AddMemberAsync, AddTopLevelTypeAsync, RemoveMemberAsync, ReplaceMemberAsync, AddPropertyAsync, AddFieldAsync, SortMembersAsync, InsertMemberAfterAsync, InsertMemberBeforeAsync, GetContainerMembersAsync | ~700 |
| `SignatureRefactoringEngine` | ChangeSignatureAsync, AddRemoveParamsAsync, AddConstructorParameterAsync, RemoveConstructorParameterAsync, GetConstructorParametersAsync, AddMethodParameterAsync, RemoveMethodParameterAsync, GetMethodParametersAsync | ~1000 |
| `ExtractionEngine` | ExtractMethodAsync, ExtractInterfaceAsync, ExtractConstantAsync, ExtractLocalVariableAsync, HasSideEffects, InferVariableName | ~700 |
| `TypeOrganizationEngine` | MoveTypeToFileAsync, MoveAllTypesToFilesForDocumentAsync, BuildSplitFileRoot, RemoveOrphanedRegionDirectives, MoveAllTypesToFilesAsync, MoveAllTypesToFilesInProjectAsync, MoveAllTypesToFilesInSolutionAsync, ConvertToPrimaryConstructorAsync, ConvertExpressionBodyAsync, ConvertIndexerToMethodAsync | ~700 |
| `AttributeModifierBatchEngine` | ApplyModifierBatchAsync, ApplyAttributeBatchAsync, ApplyBaseTypeBatchAsync, AddAttributeAsync, ReplaceAttributeAsync, RemoveAttributeAsync, GetAttributeName, AddBaseTypeAsync, RemoveBaseTypeAsync, AddModifierAsync, RemoveModifierAsync, ChangeAccessibilityAsync | ~1400 |
| `DocCommentEngine` | AddSummaryCommentAsync, AddSummaryCommentCoreAsync, RemoveSummaryCommentAsync, GetSummaryCommentAsync, BuildDocCommentText, NormalizeSummaryText, UpdateXmlDocsFromSignatureAsync | ~500 |
| `EnumEditEngine` | ModifyEnumAsync, AddEnumMemberAsync, RemoveEnumMemberAsync, ReplaceEnumMemberAsync | ~350 |
| `UsingDirectiveEngine` | AddUsingDirectiveAsync, RemoveUsingDirectiveAsync, GetUsingDirectivesAsync | ~150 |
| `RenameEngine` | RenameSymbolAsync, FindResidualMentionsAsync, TryResolveUpdatedHandleAsync, SyncInterfaceToImplementationAsync | ~400 |
| `FormattingAnalysisEngine` | FormatDocumentAsync, FormatDocumentPreviewAsync, ComputeFormatHunks, AnalyzeControlFlowAsync, AnalyzeDataFlowAsync, WrapInTryCatchAsync (both overloads), WrapInRegionAsync (both overloads) | ~500 |
| `RefactoringNameResolutionHelpers` (static, not an engine) | GetMemberName, ResolveMemberByNameOrSnippet, ResolveMemberOrEnumMemberByNameOrSnippet, TryGetEnumMemberContainerNameAsync, IsEnumContainerAsync, NormalizeTypeName, ResolveTypeByNameOrSnippet, BuildMemberHint, BuildContainerNotFoundMessage, BuildTypeHint, DisplayTypeForExtractedSignature | ~500 |

Result records currently at file scope in `RefactoringEngine.cs` (`ExtractMethodResult`,
`UsingDirectiveInfo`, `ResidualMention`, `SkippedCallSite`, `ChangeSignatureResult`,
`RenameSymbolResult`, `ControlFlowSummary`, `DataFlowSummary`, `FormatHunk`,
`FormatPreviewResult`, `ConstructorParameterInfo`, `MethodParameterInfo`, `ContainerMemberInfo`,
`SignatureParameterSpec`, `ExistingParameterSpec`, `NewParameterSpec`) move with their primary
consumer engine, same convention already used in the tool-class plan for `OutlineItem` etc.

### Why `RefactoringNameResolutionHelpers` is a static class, not an eleventh engine

Every one of the 10 engines above calls into name/type/member resolution — this is the load-bearing
shared logic, not a domain of its own. Making it a static helper class (matching the existing
`ValidateAndApplyHelper` convention already used in `Server.Basic`) means none of the 10 new engines
need to inject `RefactoringEngine` itself (or each other) just to reach these methods, which would
otherwise recreate exactly the "everything injects everything" coupling problem this whole
reorganization exists to fix.

## Decision 2 — Retiring the Standard/Advanced/Granular naming split

`StandardRefactoringEngine`, `AdvancedRefactoringEngine`, `GranularRefactoringEngine`, and
`SemanticRefactoringLibrary` are not a real tier — they're four small classes with no shared base
type or interface, split off `RefactoringEngine` at different points in the project's history with
no consistent rule for what belongs where (confirmed: no inheritance relationship exists between
any of these four and `RefactoringEngine`).

**Action**: read each of the four via `GetFileOutline` (not yet done in this pass — first task
below), classify every one of their methods into one of the 10 new engines above by what the method
actually does (an inverse-operation pair like `ConvertMethodToPropertyAsync`/
`ConvertPropertyToMethodsAsync`, if split across two of these files today, belongs together in
whichever single new engine matches — most likely `TypeOrganizationEngine` or a new
`ConversionEngine` if the classify pass finds enough conversion-shaped methods to warrant one; do
not force them into existing categories if they don't fit; a new cluster is fine if the audit
finds a real one).

Once every method is moved, delete the four old classes entirely — do not leave them as
facades. Unlike the Server.Basic tool classes (which need facades because 18 test call sites
construct them via positional-argument constructors), these are engines with far fewer, more
easily located construction/injection sites — confirm the actual count via `FindReferences` on
each class's constructor before assuming a facade is unnecessary, but the working expectation
is that a facade is not warranted here.

**`StandardRefactoringEngine`'s one live consumer** (`SentinelCodemodTools.cs`,
`RoslynSentinel.Server.Advanced`) needs to be repointed to whichever new engine its methods land
in, as part of this same pass — this is the one cross-project (Basic → Advanced-consumed) edge in
this plan and should get its own build checkpoint.

## Decision 3 — Constructor dependencies

All 10 new engines take the same baseline the original `RefactoringEngine` took:
`ILogger<TEngine>`, `IWorkspaceManager`, `SentinelConfiguration` — confirmed these three are used
throughout the file, not specific to any one cluster (the `_logger`/`_workspaceManager`/`_config`
fields at lines 36-38 are referenced broadly across the method groups above). No engine in this
split gains a cross-engine dependency on another new engine from this same split — if the
per-method audit below finds one is needed, prefer moving the shared logic into
`RefactoringNameResolutionHelpers` (or a second static helper class if it isn't name/type/member
resolution) over injecting one new engine into another, matching Decision 1's reasoning above.

## Decision 4 — Ordered execution steps with build checkpoints

Each step is its own commit boundary; build to 0 errors before proceeding (build clean, then
commit immediately). Do not start until `plan_split_workspace_refactoring_tools_for_di.md`'s
Decision 7 step 3 has landed (see Context above).

1. **Outline the four peer engines** (`StandardRefactoringEngine`, `AdvancedRefactoringEngine`,
   `GranularRefactoringEngine`, `SemanticRefactoringLibrary`) via `GetFileOutline`, and confirm via
   `FindReferences` how many real construction/injection sites each one has, before finalizing
   Decision 2's "no facade needed" assumption. If any turns out to have a materially larger
   call-site footprint than expected, stop and reconsider the facade-free approach for that one
   engine specifically rather than proceeding on the stale assumption.

2. **Extract `RefactoringNameResolutionHelpers`** as a static class first (no behavior change,
   unblocks every other step) — move the 11 shared helper methods verbatim, update
   `RefactoringEngine.cs`'s internal call sites to call the new static. **Build checkpoint.**

3. **Extract the 9 remaining new engines one at a time**, smallest first
   (`UsingDirectiveEngine` → `EnumEditEngine` → `RenameEngine` → `DocCommentEngine` →
   `FormattingAnalysisEngine` → `ExtractionEngine` → `TypeOrganizationEngine` →
   `MemberEditEngine` → `AttributeModifierBatchEngine`), each as its own commit:
   - Move the method bodies verbatim into the new engine class.
   - Update every call site across `RoslynSentinel.Server.Basic`/`RoslynSentinel.Server.Advanced`
     that referenced the corresponding `RefactoringEngine` method to reference the new engine
     instead (this is the step that touches DI registrations and constructor parameter lists in
     the Server.Basic/Server.Advanced tool classes — coordinate against whichever state
     `plan_split_workspace_refactoring_tools_for_di.md` has left those classes in at the time this
     step runs).
   - **Build checkpoint after each engine**, not batched — this plan's biggest risk is a silent
     dropped call site, and one-engine-at-a-time keeps any break isolated to a single, small diff.

4. **Classify and migrate the four peer engines' methods** into the 10 new engines per Decision 2,
   one peer engine at a time, smallest first (`StandardRefactoringEngine` →
   `GranularRefactoringEngine`/`SemanticRefactoringLibrary`/`AdvancedRefactoringEngine` in
   whatever order the outline pass finds least entangled). Repoint
   `SentinelCodemodTools.cs`'s `StandardRefactoringEngine` dependency as part of that specific
   step. **Build checkpoint after each peer engine.**

5. **Delete the now-empty `RefactoringEngine.cs`** and the four peer-engine files. **Build
   checkpoint**, then run the full test suite (`RoslynSentinel.Tests`, `.Basic`, `.Battery`,
   `.Advanced`, `.Asyncify`) to confirm behavior, not just compilation, survived the moves.

## Files to create

- `RoslynSentinel.Basic/RefactoringNameResolutionHelpers.cs`
- `RoslynSentinel.Basic/MemberEditEngine.cs`, `SignatureRefactoringEngine.cs`,
  `ExtractionEngine.cs`, `TypeOrganizationEngine.cs`, `AttributeModifierBatchEngine.cs`,
  `DocCommentEngine.cs`, `EnumEditEngine.cs`, `UsingDirectiveEngine.cs`, `RenameEngine.cs`,
  `FormattingAnalysisEngine.cs`
- Possibly a new `ConversionEngine.cs` if Decision 2's classify pass over the four peer engines
  finds enough conversion-shaped methods to warrant it — not decided until that pass runs.

## Files to modify

- Every `Server.Basic`/`Server.Advanced` class that today injects `RefactoringEngine` or one of
  the four peer engines — exact list is only known once
  `plan_split_workspace_refactoring_tools_for_di.md` has landed, since that plan is already moving
  these same call sites. Re-grep at the start of Decision 4 step 3 rather than trusting a list
  compiled before that plan runs.

## Files to delete (Decision 4 step 5)

- `RoslynSentinel.Basic/RefactoringEngine.cs`
- `RoslynSentinel.Basic/StandardRefactoringEngine.cs`
- `RoslynSentinel.Basic/AdvancedRefactoringEngine.cs`
- `RoslynSentinel.Basic/GranularRefactoringEngine.cs`
- `RoslynSentinel.Basic/SemanticRefactoringLibrary.cs`

## Open questions (not resolved in this pass)

- Whether `GranularRefactoringEngine.RunMicroRefactoringAsync`'s pre-existing
  `SyntaxNode?`-return formatting-scope issue (`docs/current/TODO.md`, NormalizeWhitespace entry)
  should be fixed as part of this move or carried forward unchanged into whichever new engine
  inherits it — default to carrying it forward unchanged and re-flagging it in the new engine's
  file, since fixing it is a separate, already-scoped piece of work with its own three-way decision
  needed (per that TODO entry), not a mechanical move.
- Exact destination for any conversion-shaped methods found in the four peer engines
  (`ConversionEngine` vs. folding into `TypeOrganizationEngine`) — deferred to Decision 4 step 1's
  outline pass, not guessed here.

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors after every step above.
- Full test suite (`RoslynSentinel.Tests`, `.Basic`, `.Battery`, `.Advanced`, `.Asyncify`) after
  Decision 4 step 5 — behavioral confirmation, not just compilation.
- Build to 0 errors, then commit immediately — one commit per numbered step/sub-step above, not
  batched.

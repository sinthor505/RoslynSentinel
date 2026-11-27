# Engines report typed facts; the tool layer names tools and writes the text

**Status:** DRAFT 2026-11-27. plan-design phase 4 (12 owner decisions settled; next: Q85, Q86 and the roadmap changes, then groups 2 to 5): 20 of
43 questions open. Phase 3 of
[plan_roadmap_layering_and_project_splits.md](../plans/plan_roadmap_layering_and_project_splits.md); design only, no implementation steps.

## Motivation

The core of the problem: engines, and the engine-like code in `Common`, name MCP tools in the text they write. Callers of the naming machinery
therefore sit below the tool layer, so the machinery itself (`ToolCallHint`, `ToolSignatureCatalog`, the tool-surface types, the not-found text
builders, `ToolErrorMapper`) has to sit below them, in `Common`. That is the inversion of the roadmap's Problem 2. The callable check is a
consequence, not the cause: a lower layer that names tools correctly has to ask what the caller can call.

Size, measured 2026-11-27 (text searches over non-test code; method and anchors in the Decision entries Q77 and Q80 and in the grounding reports
named under Related):

- About 138 engine lines name a tool, in about 80,000 lines of engine code: 81 `ToolCallHint` call lines (Engines.Basic 75, Engines.Advanced 4,
  Engines.Core 2) and 57 bare tool names in string literals (Engines.Basic 56, Engines.Core 1). They are concentrated: `EditImpactAnalyzer`
  (7 hint calls, 13 bare names), `MemberRefactoringEngine` (11, 9), `SymbolNavigationEngine` (11, 4), `BasicRefactoringEngine` (9 bare names),
  `TestInstanceLauncher` (5 hint calls). Engines.Advanced is nearly clean.
- A few shared builders carry most of the volume: `DocumentLookup.RequireDocument` (200 call sites; its text comes from `FileNotFoundMessage`),
  `DeclarationNotFoundDetail.Build` (71 sites, one embedded `GetFileOutline` call), `SnippetErrorFormatter`.
- The callable check has real use in six engine files (`SymbolNavigationEngine`, `EditImpactAnalyzer`, `BuildEngine`, `EditImpactGate`,
  `CompilerErrorLookupHelper`, `MemberRefactoringEngine`); the 99 matches in 11 files include declarations and comments. `Common`: 77 matches in
  11 files.
- `ToolException` family: 22 classes, 632 constructions (`throw new`: Engines.Basic 336, Engines.Advanced 238, Common 21, Tools.Basic 8,
  Tools.Advanced 1; Engines.Core builds 10 and returns them). At most about a third carry tool text, directly or through a shared builder.
- `Common` holds 37 `ToolCallHint` call lines and 84 bare-name lines in 18 files (31 are the keys of `ErrorPolicyIntentTable`).
- Not counted separately: how many of the 229 `new ResultError(` / `ToolErrorCode.` matches in engines carry tool text (the 57 bare-name lines
  should include them).

What it costs today:

1. `Common` cannot shed its tool-layer code (about 6,600 lines; roadmap, Appendix B) while lower code calls it. The engine edits are the gate;
   the mass is the Phase 6 moves.
2. Hints name tools the caller may not have. Most engine hints carry no check: `RequireDocument` passes none, `SnippetErrorFormatter` and
   `DeclarationNotFoundDetail` name tools unconditionally, and `ToolCallHint` renders from the reflection catalog of all loaded tools. The fix
   inside the engines is to push the check into every site that names a tool; that spread had begun (about ten engine files, 2026-11-12 to
   2026-11-14) and deepens the inversion.
3. Tool-layer helpers sit in Engines.Basic because the tool projects cannot see each other: `RefactoringToolHelpers` (271 lines, 6 consumer
   files in 3 tool projects), `RejectedContentClassifier` (55 lines, one consumer), `ViewResults`.
4. The one function every error passes through renders nothing and sits in `Common`: `ToolErrorMapper.ToResultError` (147 non-test references in
   28 files: the tool-level catches and `CallToolFailureHandler`).

What it does not cost: nothing fails to compile, and an engine area can be split into its own project with the inversion still in it. The
roadmap's figures (323 and 234 throws, about 260 builder call sites) count throws and call sites, not lines that name tools; they overstate the
engine side.

Target (owner, 2026-11-26): engines report what went wrong and which kind of next step resolves it, as typed facts; only the tool layer knows the
caller's tool surface, names tools and writes the text. Engines never take a callable check and never call `ToolCallHint`.

One built instance of the target exists: the Git split (2026-11-24). `Git/GitHintTypes.cs` holds `GitHintKey`, `GitHintSlot`, `GitHintFacts` and
`GitHint`; `Tools.Basic/GitHintText.cs` renders them; `GitHintApplyingOperations` decorates `IGitOperations`. It covers results only, not
exceptions, and its keys are call-shaped (`GitStatusCall`), not kinds of next step.

This proposal settles the design that Phases 4.1, 4.2, 5 and 6 of the roadmap build on. It worked when the Phase 4.1 plan (`DocTrailerEngine`
pilot) can be written from it with no new design question.

## Decision

Dated entries; later entries do not override earlier ones unless they say so. Q-numbers continue the roadmap (last used there: Q47).

- **2026-11-27, deliverable (owner):** one proposal, not a plan. Rollout steps belong to the plans of Phases 4.1, 4.2, 5 and 6. This doc carries a
  rollout outline and the pilot's test approach only.
- **2026-11-27, inputs (owner):** `plan_error_codes_follow_cause.md` and `proposal_throw_typed_exceptions_not_failure_results.md` are read as inputs
  now (the Phase 0.3 fold of the first into the second is not awaited).
- **2026-11-27, constraints carried from earlier owner decisions (not re-asked):**
  - C1 target layering, Common rule, mechanical splits (roadmap, 2026-11-26).
  - C2 no change to wire text, DTO properties or tool schemas (roadmap, Out of scope). Consequence: a fact never appears on the wire; fact
    properties are `[JsonIgnore]` or internal, as in the Git split.
  - C3 typed payload properties per exception subtype, not a common details object; contracts `IOperationResult`/`IOperationError`; member name
    `ErrorCode` (typed-exceptions proposal, Q2 and Q6b).
  - C4 area exceptions move with their area; the exception base stays in `Common` while `Common` throws it (Build/RunTest draft, Q31 and Q36).
  - C5 no renderer delegate is injected into an engine, and no engine holds a format string (Git split, D-c).
  - C6 tool text carries facts and options, never policy; a tool is named only when callable; no mode or flag changes tool text for evals only
    (facts/options reference).
  - C7 ratchets are shrink-only: growth fails, a decrease passes with a message (house rule D10).
  - C8 `Engines.Core` admission rule (Q46) and the CLAUDE.md wording (Q47).
- **2026-11-27, Q72 (investigation, done):** the three callable rules are INBOX lines of 2026-11-24. R8: `ActiveToolSurface.IsCallable` does not
  count runtime-toolset tools (held, options in `plans/closed/plan_tool_surface_controller_stage_e.md`). R10: `WriteToolAdviceHelper.IsExposed` is
  a second startup-only rule. R11: `CallToolFailureHandler.UnknownToolError` names two tools with no surface check.
- **2026-11-27, Q74 (investigation, done):** the S1-S24 site table is not in the on-hold Build/RunTest draft; only the Q34 channel summary is
  (`plan_isolate_build_test_area_into_own_project.md:169-176`). The Phase 4.2 plan re-derives the sites.
- **2026-11-27, Q70 (investigation, done):** where typed information is still reachable.
  - Thrown exception: caught by `CallToolFailureHandler` (`CallToolFailureHandler.cs:28-43`), registered as the `WorkspaceNotLoaded` filter, 10th
    of 16 (`ServiceRegistrationExtensionsBasic.cs:244-262`, `:543-544`). It holds the live exception; the filter lambda can reach the tool name,
    the arguments and DI (`ActiveToolSurface` included). Today `ToolErrorMapper.ToResultError` (`ToolException.cs:335-351`) keeps only the code,
    the message, a detail and `StructuredDetail`. A thrown error skips the marshaller and the offload and path-relativize filters.
  - Returned result: the typed DTO exists only inside the tool method and in `ToolResultMarshaller.MarshalAsync` (`ToolResultMarshaller.cs:86-148`),
    which already reads `[JsonIgnore]` properties (`SentinelCallToolResult.cs:353,361,372`). The marshaller has no `ActiveToolSurface`, skips tools
    not built by the factory and does nothing in mode `passthrough` or `off`. Every filter sees serialized text only.
  - Git: `GitHintApplyingOperations` is wrapped by hand in the tool constructors (`GitTools.cs:22`, `GitRemoteTools.cs:25`), renders inside the
    tool method and uses no callable check.
  - Deleting a tool-level catch is not text-neutral: the context prefix falls back to the tool name, `JsonException` and cancellation map to
    other codes, the marshaller is skipped and partial results are lost (`WholeFileWriteTools.cs:488-501`, `AsyncifyTools.cs:3962-3965`).
  - No tool returns a bare string. `ToolCallHint.Current` is assigned only by tests.
- **2026-11-27, Q77 (investigation, done):** sizes (text searches; per-line regex, comments and `ToolCallHint.`/`nameof(` lines dropped).
  - Bare tool names in string literals: Engines.Basic 56 lines in 15 files (`EditImpactAnalyzer` 13, `BasicRefactoringEngine` 9,
    `MemberRefactoringEngine` 9), Engines.Advanced 0, Engines.Core 1 (`SnippetErrorFormatter.cs:949`), Git 3 (`GitEngine.cs:1986, 1987, 2002`),
    Common 84 lines in 18 files (31 are the keys of `ErrorPolicyIntentTable`). With the 81 hint call lines and the 99 callable-check matches,
    about 240 engine lines depend on the tool surface, against 632 exception constructions and 229 engine `ResultError` matches in total.
  - `ToolHintRatchetTests` already scans every non-test project for hand-written call text (a tool name followed by `(` or after a trigger
    word); its baseline `docs/known-handwritten-tool-hints.txt` has one entry. `MessageTextRatchetTests` scans tests only (149 long text pins in
    66 test files, `docs/known-long-message-pins.txt`).
  - Wording passed down into engines: about 42 engine methods (Engines.Basic about 36, Engines.Advanced 5, Engines.Core 1) take `paramName`,
    `hint` or a message-building `Func` (hand-classified, about +-10). `Func` builders: `SyntaxTargetResolver.cs:699, 718`,
    `ConstructionReplacer.cs:517`. `MemberRefactoringNotFound.cs:87-229` holds 17 factories that take `paramName`.
  - Parameter names in `ToolInvalidArgumentException` messages (sample of 40 in 12 files): 0 quote a parameter name, 13 mention one unquoted
    (`callSiteFixups`, `contextSnippet`, `projectName`), 13 quote a value.
  - Wire fields: `ImpactVerdict.SuggestedTool` (`EditImpactResult.cs:106`; the grounding's `EditImpactVerdict` was the wrong name) is a tool-name
    literal set in the engine (`EditImpactAnalyzer.cs:634, 642, 659-669`) and travels in `errorData.structuredDetail`.
    `AppliedChangeSummary.Note` (`AppliedChangeSummary.cs:115-151`, Common) is a computed, serialized property that renders an `UndoChange` call and
    names `dryRun` and `returnDiff`. `ItemFailure.SuggestedTool` is a structured `ToolHint` set only in `Tools.Advanced/AsyncifyTools.cs:1927, 1966`.
    `GuidanceHint.SuggestedToolNames` is `[JsonIgnore]` and has no production setter.
- **2026-11-27, Q80 (investigation, done):** the `*Impl` layer and the Advanced projects (text searches; tool counts from
  `docs/generated/architecture_tools.md`).
  - `*Impl` classes: 12 in `Tools.Basic` (DI singletons, `ToolServiceCollectionExtensionsBasic.cs:35-46`), 2 in `Tools.Advanced`. No MCP
    attributes; they build `SentinelCallToolResult<>` and `ResultError`, and hold 67 `ToolCallHint` lines and 76 callable-check lines. 51 of the 69
    Basic tools forward to an Impl; 2 of the 51 Advanced tools and none of the 4 Experimental tools do. `ToResultError` calls: Basic 56 in Impl and
    15 in tool classes; Advanced 61 and Experimental 10, all in tool classes. Three Impl classes exceed 1,500 lines (`WorkspaceFileEditImpl` 2,707,
    `WorkspaceReadNavigationImpl` 2,266, `RefactoringStructuralImpl` 1,519). Origin: `plans/closed/plan_split_workspace_refactoring_tools_for_di.md`,
    Decision 1 amendment.
  - Every tool the dog-food rule names is in `Tools.Basic`, except `MoveType` (`Tools.Advanced`).
  - No mechanism builds the server without the Advanced projects: `Server.Advanced.csproj:26-31` references both, `ToolClassRegistry.cs:107-110`
    lists 11 of their classes (plus mode rows and a validation), `scripts/build.ps1:62` offers `Advanced` and `Solution` only. Five test projects
    reference `Tools.Advanced` directly; four more reach it through the server.
  - What the Advanced tool projects take from members this refactor changes: `new SymbolNavigationEngine(..., isCallable)` at 2 sites
    (`AdvancedRefactoringTools.cs:35`, `GenerationTools.cs:34`), `EditImpactGate.Describer` at 2, `DocumentLookup.Describe(IsCallable)` at 3,
    `RefactoringToolHelpers` at 34 (31 are `GuardEngineResult` in `CodeTransformTools`), `CompilerErrorLookupHelper` 1, `ProjectNotFoundDetail.Build` 1.
  - What `Engines.Advanced` takes: `DocumentLookup.RequireDocument` 99 sites (throws; its signature need not change),
    `DeclarationNotFoundDetail.Build` 58 (returns a string that is concatenated into a `ToolNotFoundException` message and embeds a `GetFileOutline`
    call, `AdvancedStructuralEngine.cs:58`), snippet lookup 10, `ProjectNotFoundDetail` 1. Unreconciled: 17 `ToolCallHint.` text lines there
    against 4 call lines in the first grounding.
- **2026-11-27, Q84 (investigation, done in part):** feasibility of an `[Obsolete]` ledger.
  - Build: no `TreatWarningsAsErrors`, `WarningsAsErrors` or `NoWarn` in the props, targets, project files or `.editorconfig`; every project
    targets `net10.0`. No `[Obsolete]` uses `DiagnosticId` today (25 declarations, 3 in engines). No banned-symbol analyzer is referenced.
  - Baseline: `scripts/build.ps1` writes `docs/known-build-warnings.*.txt` and is the only comparer (`:182-231`). Its key is
    `SEVERITY ID relpath:line` (`:196`), so an edit that shifts lines shows one new and one fixed warning. The Solution baseline has 15 lines, 10 of
    them CS0618 (3 of those no longer point at a call).
  - Hidden warnings: several test projects set `WarningLevel` 0 or 1 (`Tests.Battery.Basic`, `Tests.Git`, `Tests.Tools.Basic` at 0;
    `Tests.Basic`, `Tests` at 1), which hides obsolete warnings there.
  - Run time: nothing reads `[Obsolete]` in schema, description or doc generation. Tool results serialize through reflection
    (`SharedJsonOptions.cs:13`), which does not skip obsolete members; `[JsonIgnore]` is the only per-member wire control.
  - Callable-check members: 67 non-test members in 23 files take a `Func<string, bool>` check (Common, Engines.Basic, Server.Advanced,
    Tools.Basic). Engines.Basic: `EditImpactAnalyzer.cs:79, 334, 566, 607` and static `:659, :750, :840`; `CompilerErrorLookupHelper.cs:204, 277,
    1026`; `BuildEngine.cs:46, 187, 202, 238`; `SymbolNavigationEngine.cs:163` (constructor) and static `:2832, :2869, :2947`;
    `MemberRefactoringEngine.cs:2999` (instance). `ActiveToolSurface.IsCallable` has 39 non-test reference sites (Tools.Basic 27, Tools.Advanced 5,
    Server.Advanced 3, Common 2, Engines.Basic 2).
  - Not done: the inventory of engine result members that carry hint text (only `AppliedChangeSummary.Note` was traced: mixed, serialized
    as-is, 33 test readers, no production reader found), and instance-or-static for most of the 67 members. Each area plan inventories its own.
- **2026-11-27, Q71 (investigation, done):** `EngineError` is live but narrow. One producer, `SymbolWireResolver` (`:52-55`, `:83-86`, `:94-97`),
  with two of the five codes (`SymbolNotResolved`, `MissingParameterListOverloads`); it travels on `SymbolResolution.Error`
  (`SymbolResolution.cs:9`). Three readers: `SymbolWireResolver.ToResultError` (`:117-124`, called by `RefactoringSignatureImpl.cs:117` and
  `WorkspaceProjectManagementImpl.cs:1047`) and `DiscoveryEngine.cs:546-547`, which turns it into `ToolNotFoundException` /
  `ToolInvalidArgumentException`. `General`, `StaleSession` and `BuildNotRun` have no production use; the three appear only as `[TestCase]`
  inputs at `ErrorPolicySymbolWireResolverRouteTests.cs:37-39`. `ToToolResponse()` has no production caller found.
- **2026-11-27, Q73 (investigation, done):** no test pins the three `DocTrailerEngine` hint texts through the tool. Every pin calls the engine
  directly and checks substrings: `DocTrailerEngineTests.cs:292-303` (`:312` text: "docs/", "without its extension", "case-sensitive",
  "ProjectDoc"), `:305-313`, `:329-337` (`:810-812` text), `DocCloseTests.cs:377-379` (`:406` text: "ProjectDoc"), `:387-389`. The tool-level
  tests (`DocumentationToolsTests.cs:367-382`) assert `Exists` and `IsError` only. Consequence for the pilot: full-sentence pins through
  `DocumentationTools` are written first, against today's text, before the text moves (golden files outside `.cs` were not searched).

- **2026-11-27, Q75 = A (owner). D-Q75: the engine keeps the diagnosis sentence.** An engine writes what went wrong in code terms; its text
  holds no tool name, toolset, mode or call syntax (parameter labels: Q76). Only the next-step part becomes a typed fact that the tool layer
  renders. Why: a diagnosis sentence does not depend on the tool surface, and about 138 engine lines change instead of 632 constructions and 229
  error results. Moving an area's whole text later stays possible.
- **2026-11-27, Q48 = A (owner). D-Q48: one exception family.** The existing `ToolException` hierarchy is the family; no second hierarchy. The
  base and the general subclasses stay in `Common` as kernel error types; area exceptions live with their area (C4); the base gains the
  next-step list (shape: Q37). Owner follow-up, exceptions into sub-namespaces: answered under Q82.
- **2026-11-27, Q54 = A (owner). D-Q54: the tool layer fills wire fields that name a tool.** The fields stay on the wire (C2).
  `ImpactVerdict.SuggestedTool`: the engine reports a typed remedy and the tool layer fills the field, in the edit-impact area plan.
  `AppliedChangeSummary.Note`: computed by the tool layer, moves with result delivery (Phase 6). `ItemFailure.SuggestedTool`: already set
  tool-side, no change. `GuidanceHint.SuggestedToolNames`: no production setter, recorded as a dead-code finding.

- **2026-11-27, Q76 = A (owner). D-Q76: labels stay, wording goes.** An engine may name its own parameters, and may echo a parameter label the
  caller passes (the calling tool's parameter name, where it differs from the engine's). Hint strings and message-building functions passed into
  engines are removed (`SyntaxTargetResolver.cs:699, 718`, `ConstructionReplacer.cs:517`, the `hint` parameters); the engine reports a fact and
  the tool layer adds the wording. A label that names a parameter the caller does not have is a finding when met. Not measured: how many call
  sites pass a label from a tool.

- **2026-11-27, Q37 = B (owner). D-Q37: per-area fact records on one kernel slot.** The exception base gains a slot whose type is a kernel
  interface (owner direction; name: Q86); results carry the same slot type behind one interface, `[JsonIgnore]`. Each area declares its own
  records that implement it, with a key enum named for the situation, never for a tool. The tool layer owns the whole hint sentence. Why: the
  Git split already works this way and rejected engine-held hint wording (C5); a missing tool then changes the sentence in one layer.
  Implementer default: the slot is a read-only list, empty by default (some sites carry several steps: Git's `Hints`, the `BuildEngine`
  ladder). Cost accepted: a new hint is an edit in two layers.
- **2026-11-27, Q65 = A (owner). D-Q65: fact types are public and live beside their emitter.** The carrier (interface, slot on the exception
  base, result interface) is in `Common`; each area's records sit beside the code that emits them. One test checks that no fact type is
  reachable from a wire schema. Owner follow-up (do namespaces keep the wire-level tool classes from seeing engine facts?): a namespace makes a
  use visible, it does not prevent it; a `using` of an engine-facts namespace in a tool class is the detectable violation. The compiler enforces
  it only when a tool project stops referencing the engine projects and turns off transitive project references, which needs every tool of that
  project to go through the layer and no tool method signature to name an engine type (see Q85).
- **2026-11-27, Q78 = A (owner). D-Q78: the Advanced projects are converted last, on master.** The Basic side is converted first and is the
  blueprint; `Tools.Advanced`, `Tools.Experimental` and `Engines.Advanced` keep compiling against the old signatures (Q81) until their own pass.
  No branch.
- **2026-11-27, Q79 = B (owner). D-Q79: the layer project is the `*Impl` layer.** It holds the render function, the hint builders and the
  shared tool-side helpers, and the 12 Basic `*Impl` classes move into it as their own mechanical split after the pilot. Nothing waits for that
  split. Limit: the layer cannot take text out of an engine unless that engine site changes to report a fact. Renderers are per-area classes,
  not additions to the three `*Impl` classes over 1,500 lines. Owner follow-up (extract the remaining 18 Basic tools): Q85.

- **2026-11-27, Q85 probe (done):** can a tool turn a tool method into an `*Impl` body plus a thin wrapper?
  - Request: two `MoveMember` dry runs on `Tools.Basic/NamedArgumentsTools.cs` (one tool method, four private fields, one private helper, one
    private constant), `targetClassName: NamedArgumentsImpl` (does not exist), `oldMember: keepDelegator`, `dryRun: true`.
  - Run 1, members `NamedArgumentsTool` and `InvalidArgument`: refused, `TargetIneligible`: "oldMember 'keepDelegator' cannot forward
    'InvalidArgument': it is Private, so the forwarder in 'NamedArgumentsTools' could not call it on 'NamedArgumentsImpl' (CS0122)".
  - Run 2, `NamedArgumentsTool` alone: refused, `InvalidArgument`: "their bodies reference private/internal member(s) [DefaultMinParameters,
    InvalidArgument, _engine, _logger, _validationEngine, _workspaceManager] of 'NamedArgumentsTools' that are not included in this move".
  - Reading: the tool has the pieces (`keepDelegator`, `fieldMap`, `forwarderReceiver`, creation of a new target class) but two gaps block a
    one-call extraction: the keep mode applies to every listed member, so private helpers cannot travel with the method; and `fieldMap` maps
    only to fields that already exist on the target, so the `*Impl` class, its fields and constructor and the source's `_impl` field have to
    exist first. Untested (no dry run got that far): whether MCP attributes and parameter defaults stay on the forwarder. Recorded in INBOX.
  - `ExtractMembers` (`Tools.Advanced/AdvancedRefactoringTools.cs:416`) extracts into an interface or a partial class only; its class mode was
    removed in favour of `MoveMember`.
  - Run 3, the two-step route (owner idea): `ExtractMethod` dry run on the whole body of `NamedArgumentsTool` (121 lines pasted as
    `exactSourceBlock`), `newMethodName: NamedArgumentsCore`. Refused by the compile gate, `ValidationFailed`: "CS4032 (x2): The 'await' operator
    can only be used within an async method ... at NamedArgumentsTools.cs:97, :149". The extracted method is not made `async Task<T>`, so
    no async body can be extracted today. Recorded in INBOX.
  - Two routes once the tools are fixed. Route A: `MoveMember` with `keepDelegator` (needs a keep mode per member or private companions that
    travel without a forwarder, attribute placement on the forwarder, and creation of the target's fields and constructor). Route B:
    `ExtractMethod` on the whole body, which leaves the attributed tool method as a one-line call by construction, then `MoveMember` with
    `oldMember: remove` for the extracted method and its private companions (needs the `ExtractMethod` async fix, in practice a whole-body
    selector, and the same creation of target fields and constructor, or a hand-written `*Impl` skeleton per class). Not yet shown for route B:
    which parameters the extracted method keeps (an unused `reason` would be dropped) and how the wrapper's call is rewritten to go through
    `_impl`.
  - Update, from `proposals/closed/proposal_movemember_keep_delegator_and_same_class_calls.md` (IMPLEMENTED 2026-10-18) and
    `proposals/proposal_parallel_change_deprecated_wrappers.md` (owner pointed to both):
    - Route A points the wrong way for this job, by decided design. In keep modes the member's attributes travel with the moved member and
      the forwarder left behind has none (section 1, "Forwarder shape": `[McpServerTool]`, `[Description]`, `[Produces]` stay on the moved
      member). It was built to move a tool out of the `WorkspaceTools` facade into another tool class. A keep-mode move into a new class is
      limited to stateless methods (forwarder `new global::Ns.NewClass().M(args)`), which matches run 2.
    - Route B's second step is already built. `MoveMember` with `oldMember: remove` rewrites a bare same-class call to a moved member into
      `this.<receiver>.M(...)` and reports it (slice 7); `forwarderReceiver` is honoured in `remove` mode for that. The intended way to give
      the target its dependencies is the constructor-parameter tool plus `fieldMap` (that proposal, "Moved from TODO.md": "`ConstructorParameter`
      plus `fieldMap` covers it"); `ConstructorParameter` (`RefactoringSignatureTools.cs:81`) and `ParameterEdit` (`ParameterEditTools.cs:25`)
      are declared today.
    - So route B needs one fix, the `ExtractMethod` async defect of run 3, and in practice a whole-body selector. Sequence per tool class:
      create the `*Impl` class, add its constructor parameters and the tool class's `_impl` field, `ExtractMethod` each tool body, then one
      `MoveMember` (`remove`, `fieldMap`, `forwarderReceiver: _impl`) for the extracted methods and their private companions.
  - Prior art: `plans/closed/plan_split_workspace_refactoring_tools_for_di.md`, Decision 1 amendment: bodies verbatim into `*Impl`, one-line
    delegates, every MCP attribute on the wrapper only, `reason` on both layers, one `*Impl` per tool class. It names no tool for the move. Its
    Decision 3 kept tool-class constructor signatures stable because tests construct tool classes directly (18 call sites then). Its deferred
    idea (the wrapper catches and builds the envelope, `*Impl` returns plain types) is not what D-Q79 does: there the `*Impl` layer builds the
    envelope and renders.

- **2026-11-27, Q49 = `RoslynSentinel.Impl.Core` (owner). D-Q49: the layer is `Impl`; its shared project is `RoslynSentinel.Impl.Core`.** It
  holds the render function, the hint machinery and the shared tool-side helpers, for both flavors and the server. Refines D-Q79 in one
  detail: the 12 Basic `*Impl` classes move to `RoslynSentinel.Impl.Basic`, not into the shared project (roadmap Q40: the flavors do not depend
  on each other, shared code gets a home of its own). A later `RoslynSentinel.Impl.Advanced` can hold renderers for Advanced-engine facts,
  where both `Tools.Advanced` and `Tools.Experimental` can see them. Layering becomes `Common` <- `Git` <- `Engines.*` <- `Impl.*` <- `Tools.*`
  <- `Server.*`; the CLAUDE.md wording changes in the plan that creates the project. What each `Impl` project holds and references: Q87.
- **2026-11-27, Q82 = A (owner). D-Q82: namespace-first, pilot on the hint machinery.** Tool-layer types that sit in `Common` and Engines.Basic
  get their `RoslynSentinel.Impl.Core.<Area>` namespace before any file leaves its project; a `using` of it below the `Impl` layer is a
  violation to sweep, and a later project move rewrites no references. Pilot: `ToolCallHint` and the signature catalog; the other roles of
  `Common` are decided after the pilot. Rules: no global `<Using>` for these namespaces; a file holding several concepts is split first; each
  rename checks for full type names used as strings (reflection lookups, log categories, test filters, persisted JSON). Limits: a `using` is
  per file, not per use; string literals and `Func<string, bool>` parameters need none and stay invisible. `ToolException.cs` splits three
  ways: the kernel family gets no namespace of its own (every layer uses it legitimately), area exceptions take their area's namespace,
  `ToolErrorMapper` takes the layer's.
- **2026-11-27, Q81 = A (owner). D-Q81: old signatures live on as shims in the `Impl` layer.** When a callable-check parameter leaves an engine
  member, the engine's own overload is deleted in the same slice and the old signature is kept in the layer: an extension method for an
  instance member, a plain static class for a static helper, a wrapper class that holds the engine and the surface for
  `SymbolNavigationEngine` (check stored by the constructor, `:163`). Interfaces stay where they exist (Git); there is no interface-per-engine
  rule. Why: a wrapper can only reference downward (`proposal_parallel_change_deprecated_wrappers.md`), so an old signature can only survive
  above the engine; a caller that cannot see the layer fails to compile, which lists what is left. Known: at least six of the Engines.Basic
  members are static (`EditImpactAnalyzer.cs:659, :750, :840`; `SymbolNavigationEngine.cs:2832, :2869, :2947`). `ChangeSignature` already has
  `oldSignature: remove | keepAsDeprecatedWrapper | auto`; the migrate step `InlineDeprecatedCalls` is not declared.
- **2026-11-27, Q83 = A (owner). D-Q83: marked members are the ledger.** Members that carry tool-naming text and are wrong for every caller
  (hint-only result properties, callable-check overloads, string-returning hint builders, `SuggestedTool` on the exception) are marked
  `[Obsolete]` area by area, as each area plan starts. The ledger is the count of references to each marked member (symbol search); build
  warnings are a secondary signal (`proposal_parallel_change_deprecated_wrappers.md`, "The source is the ledger"). Forwarding wrappers use the
  existing `RSDEPRECATED` ID; other marked members get a second ID (implementer default: `RSTOOLTEXT`), so a later `InlineDeprecatedCalls` does
  not try to inline them. Not marked: mixed fields (`Error`, `Message`) and exception messages. A result that a tool returns as-is is invisible
  to this ledger (the serializer reads the property); each area's inventory finds those. Replaces the default Q68.

## Current design

- **Built:**
  - Engines keep their diagnosis sentences and stop naming tools (D-Q75); parameter labels stay, wording passed into engines goes (D-Q76).
  - Next steps travel as per-area fact records on one kernel slot, on the one existing exception family and on results (D-Q37, D-Q48); the
    types are public, the carrier is in `Common`, area records sit beside their emitter (D-Q65).
  - Wire fields that name a tool are filled by the tool layer from a fact (D-Q54).
  - A new `Impl` layer sits between the engines and the MCP tool classes. `RoslynSentinel.Impl.Core` holds the render function, the hint
    machinery and the shared helpers; the Basic `*Impl` classes later move to `RoslynSentinel.Impl.Basic` (D-Q79, D-Q49).
  - Tool-layer types get their `Impl.Core` namespace before they move, starting with a pilot on the hint machinery (D-Q82).
  - When a callable check leaves an engine, the old signature lives on as a shim in the layer (D-Q81).
  - Remaining violations are tracked by marking members `[Obsolete]` and counting references (D-Q83).
  - The Basic side is converted first; the Advanced projects last, on master (D-Q78).
- **Left out:** moving every sentence to the tool layer; a second exception hierarchy; a branch that severs the Advanced projects; `*Impl`
  extraction as a prerequisite; an interface per engine; a text-search ratchet as the main ledger.
- **Exceptions to the design checklist:** none so far.
- **Still open:** the carrier's name (Q86), vocabulary (Q52, Q63, Q64), the render function and the `Impl` projects (Q38, Q51, Q60, Q61, Q62,
  Q66, Q87), engine projects (Q43, Q50), rollout and conventions (Q53, Q55, Q56, Q57, Q59, Q67, Q85).

## Proposal

To be written as the questions close. Topics (roadmap, Phase 3):

- (a) How facts travel: the engine-layer exception family, fact records on exceptions and results, the rename of the `ToolException` family.
- (b) The Core projects: the shared tool-layer project, the reach of `Engines.Core`, the `Engines.Advanced` -> `Engines.Basic` reference.
- (c) The one render point and its relation to `FromException` in `plan_shared_tool_execution_pipeline.md`.
- (d) The vocabulary for "kind of next step".
- (e) The namespace convention.
- Rollout outline and the pilot's test approach (golden pins through the tool stay unchanged).
- The reshape list: what changes in each doc marked "reshape" in the roadmap's Appendix C.

### Roadmap changes implied (for owner review; not applied to the roadmap)

Follows from the measured size (Motivation). Items marked "pending" hang on an open question.

1. **Problem 2 restated:** the core is "engines name tools, which keeps the hint machinery in `Common`"; the callable check is a consequence.
   The counts are replaced by the measured ones.
2. **Appendix A re-ranked** by lines that name tools, not by throws. By that measure the work sits in the shared builders and about five
   files; the tiers built on throw counts (member edit 137, structural refactoring 77, modernization 78) overstate their areas.
3. **Phase 5, Change:** "tier 4 files are broken up before their text moves" is dropped; converting about 20 lines in a 6,000-line file does
   not need the file split first. Order: shared builders (`FileNotFoundMessage`/`RequireDocument`, `DeclarationNotFoundDetail`,
   `SnippetErrorFormatter`), then the concentrated files.
4. **Phase 5, Effort:** from "L (several plans)" to a few M plans.
5. **Phase 5, Done when:** loses "the `ToolException` family is renamed" (pending Q59; planner recommendation: defer indefinitely or drop).
6. **Phase 4.1:** the "shared tool-layer home" it creates is `RoslynSentinel.Impl.Core` (D-Q79, D-Q49); its first step writes full-sentence pins
   through `DocumentationTools` against today's text (Q73). Anchors corrected to `:312`, `:406`, `:812`.
7. **Phase 4.2:** the S1-S24 table is re-derived (Q74); the "text-neutral" label and the three defect fixes are reconciled (pending Q55).
8. **Appendix D:** the `EngineError` row reads "live, one producer, two of five codes used" (Q71); the `SuggestedTool` anchor is `:533`.
9. **Advanced projects:** converted last, on master (D-Q78). New roadmap items: the move of the 12 Basic `*Impl` classes into
   `RoslynSentinel.Impl.Basic` (D-Q79, D-Q49), the extraction of the remaining 18 Basic tools (pending Q85), and the tool it depends on
   (`proposal_extract_class_with_forwarding_members_tool.md`).
10. **Namespace-first step** before any project move, starting with a pilot on the hint machinery (D-Q82). The layering line in the roadmap
    and in CLAUDE.md gains the `Impl` layer (D-Q49).
11. **Phase 0.3:** `TODO.md:10` still says Phase 2 is running.

## Alternatives considered, not pursued

To be filled per question.

## Open questions

Numbers are fixed for the session. Rounds settle one group at a time, in the order below.

### Owner decisions

Group 1, facts:
- **Q86:** the name of the carrier interface of D-Q37. Owner's working name: `IEngineExceptionHint`. Planner note: the same slot type is needed
  on results (D-Q54, the result channel), so a name without "Exception" serves both (`IEngineHint`, in line with Git's `GitHint`).

Group 2, vocabulary (depends on Q37):
- **Q52:** the vocabulary for "kind of next step" (tool roles, per-area keys, or keys mapped to roles). Four hand-kept tool classifications
  already exist: `FailureRouter`/`ItemFailure.SuggestedTool`, `GuidanceHint.SuggestedToolNames`, `ToolsetCatalog.ToolsBySet`,
  `WriteToolAdviceHelper`.
- **Q63:** what the tool layer writes when no callable tool serves a step kind (omit, generic wording, or a substitution ladder kept in the tool
  layer). Engines hold such ladders today (`BuildEngine`: `ListAll` -> `ListSolutionItems` -> dropped).
- **Q64:** which callable rule the mapper uses: `ActiveToolSurface.IsCallable` as it is, or one that first settles R8.

Group 3, render point and tool-side project (depends on Q37, Q65; facts in Q70):
- **Q38 (carried):** the one render point for exception facts. Owner lean: a tool-layer builder. Owner direction 2026-11-27: look at a helper
  that tool-level catches keep calling, so the wire output stays the same, instead of one catch site in the server. Candidate: the successor of
  `ToolErrorMapper.ToResultError`, which the tool-level catches and `CallToolFailureHandler` already share (147 non-test references in 28 files).
- **Q60:** the render hook for result facts (explicit call in the tool method, a decorator as in Git, the marshaller, or a filter). The Build and
  Test engines are concrete classes with no interface to decorate.
- **Q51:** how the render point relates to `FromException` in the shared-pipeline plan (one mechanism or two), and whether the 134 tool-level
  catches go.
- **Q87:** what `Impl.Core` holds and references, against `Impl.Basic` (the name is settled, D-Q49). The hard case: a renderer for a
  Basic-engine fact needs that engine's fact types, and both tool flavors and the server need the renderer. Either `Impl.Core` references
  `Engines.Basic` (a shared project that depends on one flavor), or those renderers sit in `Impl.Basic` and `Tools.Advanced` references it (as
  it references `Engines.Basic` today). Fact: `Tools.Advanced` sees `Engines.Basic` and `Engines.Advanced` but
  neither `Tools.Basic` nor `Engines.Core`; `Server.Advanced` sees every tool project but not `Engines.Core` or `Engines.Advanced`.
- **Q61:** how a renderer is found for a fact across tool projects (pattern match in one shared class, renderers registered per area, or a
  member on the fact type).
- **Q62:** render context (tool name, surface, retry call): an explicit argument or ambient state. `ToolCallHint.Current` is static and never
  assigned in production.
- **Q66:** when `Impl.Core` is created and its first resident. Narrowed by D-Q82: `ToolCallHint` takes the `Impl.Core` namespace in the pilot
  and stays in `Common` until nothing below the layer uses it.

Group 4, engine projects:
- **Q50:** what else is admitted to `Engines.Core` (confirm or adjust C8).
- **Q43 (carried):** does `Engines.Advanced` stop referencing `Engines.Basic`? It uses 11 Basic types (IL scan); 58 of the references are
  `DeclarationNotFoundDetail`; most of the 11 fail C8 until their text moves.

Group 5, rollout and conventions:
- **Q59:** the rename of the `ToolException` family: name, timing and mechanics (22 classes, 1,747 name matches, 623 exact-type test asserts).
  Depends on Q48. Planner recommendation 2026-11-27: defer indefinitely or drop; it fixes a name, not a coupling.
- **Q55:** known text defects found on the way (roadmap, Appendix D): fixed in the same slice as the move, or in a separate slice so the move
  stays provably text-neutral.
- **Q56:** `StructuredDetail` (wire JSON produced inside the exception, 84 matches in 20 files): stays, or becomes a render product.
- **Q57:** `EngineError`/`EngineErrorCode`: fold into the new family, keep as the result-side record, or leave (facts in Q71).
- **Q85:** extract an `*Impl` for the 18 Basic tools that have none (owner idea 2026-11-27), so every Basic tool class is a thin wrapper:
  `WholeFileWriteTools` 5, `CoreRefactoringTools` 4, `AdminTools` 2, and one each in `DocumentationTools`, `GitTools`, `GitRemoteTools`,
  `NamedArgumentsTools`, `SemanticFindReplaceTools`, `ServerStatusTools`, `ToolsetControlTools`. Planner reading: yes, as its own roadmap item,
  into `Impl.Basic`; `DocumentationTools` first, with the Phase 4.1 pilot; the four admin and status tools decided after a look (they act on
  the server and the tool surface, not on engines). It depends on a tool: the one-call extraction sketched in
  [proposal_extract_class_with_forwarding_members_tool.md](proposal_extract_class_with_forwarding_members_tool.md) (PROPOSED; the owner designs
  it in a separate session), or until then the multi-call route after the `ExtractMethod` async fix (Decision, "Q85 probe"). Not by manual
  copy (tool-gap rule). Not measured: class sizes and tool methods that take MCP SDK parameters.
- **Q67:** the pilot proves only the result-field channel (`DocTrailerEngine` throws nothing). Add a small exception-channel pilot before
  Phase 4.2, or let 4.2 prove it.
- **Q53:** the namespace convention. Today every project uses its own name; `Engines.Core.SnippetMatching` and `Git.Worktrees` add one
  sub-namespace per area. Narrowed by D-Q82 (tool-layer types take `RoslynSentinel.Impl.Core.<Area>` ahead of their move). Left to confirm:
  the general rule "project name as the root plus one sub-namespace per area", and whether the other roles of `Common` follow after the pilot.

### Investigations

- Q70, Q71, Q72, Q73, Q74, Q77, Q80, Q84: done, see Decision (Q84 in part).

### Implementer defaults (stated here, no longer open unless reclassified)

- **Q58:** an exception that wraps another carries the inner facts forward (today `BuildEngine.cs:333-336` and `TestRunEngine.cs:223-226` drop them).
- **Q68:** replaced by D-Q83 and D-Q82: the ledger is the reference count to marked members, and a `using` of an `Impl` namespace below the
  layer is the file-level check. The existing `ToolHintRatchetTests` keeps covering call text written into string literals.
- **Q69:** engine tests assert facts (typed fields); full-sentence text pins live at the tool level only. An engine-direct test that pins hint
  text today moves or changes its assert in the slice that moves the text.

### Findings to place (not questions)

- `SuggestedTool` on `ToolTargetIneligibleException` (`ToolException.cs:224`) is set and never read; `TestInstanceLauncher.cs:533` sets it to
  rendered call text.
- `ErrorPolicyClassifier.cs:20-23` hand-mirrors two Git error codes.
- "Fix the argument and call again." in `CallToolFailureHandler` may be policy text under C6 (hypothesis).
- `AgentLoop` declares `RoslynSentinel.Common.AgentLoop` and `RoslynSentinel.Common` namespaces, none of its own.
- Three tool constructors build `SymbolNavigationEngine` themselves with the callable check, beside the DI path.
- Remaining items of the typed-exceptions proposal (S4 error-field retirements, S5, Q4b, Q6d, Q9).
- `proposal_parallel_change_deprecated_wrappers.md` (PROPOSED 2026-10-09) lists the `ChangeSignature` wrapper mode as proposed; the code has it
  (`ChangeSignatureOldSignature` in `ToolEnums.cs`, `ChangeSignatureWrapper.cs`), under the parameter name `oldSignature`.
- Ratchets and scripts that read moved text or project lists: `ToolHintRatchetTests`, `MessageTextRatchetTests`, `ErrorPolicyKindRatchetTests`,
  `FirstMatchRatchetTests`, `ToolReturnShapeRatchetTests`, the Git goldens, `Get-SlipCounts.ps1`, `AgentToolErrorAssertions.cs:115-139`.

## Cost / risk

To be written. Known so far: the rollout is several plans over months (roadmap, risk 8); every item is text-neutral on the wire.

## Related

- [plan_roadmap_layering_and_project_splits.md](../plans/plan_roadmap_layering_and_project_splits.md) (Appendix C lists the docs this proposal reshapes).
- [plan_isolate_build_test_area_into_own_project.md](../plans/plan_isolate_build_test_area_into_own_project.md) (on hold; Q33-Q38).
- [reference_tool_error_text_facts_options_policy.md](../references/reference_tool_error_text_facts_options_policy.md).
- [reference_design_review_checklist.md](../references/reference_design_review_checklist.md).
- Grounding reports of 2026-11-27: scratch root, `plan_design_layering_proposal\grounding_code_2026-11-27.md` and `grounding_docs_2026-11-27.md`.

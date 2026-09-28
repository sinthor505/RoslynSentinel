# `MoveMember` forces a shared-name dependency field into the move, then rejects the result for colliding with the target's own copy of that same field

**Status:** OPEN, confirmed. No disk corruption - every reproduction below was either `dryRun: true`
or rejected pre-write by `ValidationFailed` (confirmed nothing changed on disk).

## What was being attempted

Engine-reorg group 4 ("Structural refactoring merge") of `.claude/plans/enumerated-jumping-babbage.md`,
in a session continuing prior work (groups 1/2/3/9 already committed). Group 4 merges
`GranularRefactoringEngine` (`RoslynSentinel.Advanced/GranularRefactoringEngine.cs`) into the class
recently renamed to `StructuralRefactoringEngine` (`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`,
renamed this session from `AdvancedStructuralEngine` via `RenameSymbol`, commit not yet made and
unaffected by this defect). This requires moving all of `GranularRefactoringEngine`'s members into
`StructuralRefactoringEngine` via `MoveMember`.

## Reproduction

Both classes independently declare an identically-named, identically-typed field, assigned the same
way from an identically-named/typed constructor parameter:

- `GranularRefactoringEngine(IWorkspaceManager workspaceManager)` -> `_workspaceManager =
  workspaceManager;` (field: `private readonly IWorkspaceManager _workspaceManager` - confirmed via
  `GetMethodSource` on the constructor, `RoslynSentinel.Advanced/GranularRefactoringEngine.cs`
  lines 13-16).
- `StructuralRefactoringEngine(IWorkspaceManager workspaceManager, ValidationEngine?
  validationEngine = null)` -> `_workspaceManager = workspaceManager;` (same field name and type -
  confirmed via `GetMethodSource` on the constructor,
  `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs` lines 28-32).

1. Attempt 1: `MoveMember(filepath: "RoslynSentinel.Advanced/GranularRefactoringEngine.cs",
   className: "GranularRefactoringEngine", memberNames: [18 real methods/nested type, NOT including
   "_workspaceManager"], targetClassName: "StructuralRefactoringEngine", targetFilepath:
   "RoslynSentinel.Advanced/AdvancedStructuralEngine.cs", dryRun: true)` - rejected immediately with
   `errorCode: "InvalidArgument"`:

   > Cannot move the requested member(s) out of 'GranularRefactoringEngine': their bodies reference
   > private/internal member(s) [_workspaceManager] of 'GranularRefactoringEngine' that are not
   > included in this move and would no longer be reachable from 'StructuralRefactoringEngine'. Add
   > the missing name(s) to memberNames so they move together, or leave the referencing member(s)
   > behind.

2. Attempt 2: same call but with `"_workspaceManager"` added to `memberNames` (forced, per the
   tool's own suggested fix), plus per-file `callSiteFixups` keyed `"FilePath:*"` (not the bare
   wildcard `"*"` form - confirmed distinct from the wildcard-corruption defect documented separately,
   see "Related" below) supplying the pre-existing receiver expression (e.g.
   `_granularRefactoringEngine`) at each of the 15 implicated test files. Result: `errorCode:
   "ValidationFailed"`, "the change was valid and matched its target(s), but introduces new compiler
   errors":

   - `CS0102 at RoslynSentinel.Advanced/AdvancedStructuralEngine.cs:1331: The type
     'StructuralRefactoringEngine' already contains a definition for '_workspaceManager'`
   - `CS0229` (x18): `Ambiguity between 'StructuralRefactoringEngine._workspaceManager' and
     'StructuralRefactoringEngine._workspaceManager'` at 18 locations throughout
     `AdvancedStructuralEngine.cs` (every pre-existing use of the class's own original
     `_workspaceManager` field became ambiguous once a second field of the same name was added by
     the move).
   - Plus a large cascade of `CS1061`/`CS0103` errors, because the compiler snapshot used for
     validation got confused about which class's members were "moved" vs "not moved" once the
     duplicate field made the target class itself fail to compile. This cascade is a downstream
     symptom of the `CS0102`/`CS0229` root cause, not a separate defect - noted here only so a future
     session does not re-diagnose it as one.

## Where it happened

- Tool: `MoveMember`, both calls `dryRun: true`.
- Source: `RoslynSentinel.Advanced/GranularRefactoringEngine.cs`, class
  `GranularRefactoringEngine`, constructor at lines 13-16.
- Target: `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, class `StructuralRefactoringEngine`,
  constructor at lines 28-32, post-move `CS0102` at line 1331 and 18x `CS0229` throughout the file.

## Root cause

`RequireNoUnmovedDependencies` (the same guardrail documented in
`docs/obsolete/blockers/blocking_error_movemember_nested_class_dependency_not_moved.md`) treats "is
this field referenced by a moving method" and "is this field name/type already satisfied on the
target class" as unrelated questions - it only asks the former. There is no parameter in
`MoveMember`'s schema (confirmed by reading the tool's full JSON schema: required `reason`,
`filepath`, `className`, `memberNames`, `targetClassName`; optional `targetFilepath`, `dryRun`,
`callSiteFixups`, `autoResolveCallSites`, `autoStage`, `returnDiff`) that lets the caller declare
"this dependency is already satisfied by an existing target member, do not move or duplicate it."
`callSiteFixups` cannot substitute for this either - it only supplies alternate receiver expressions
for unresolved call sites in **calling** code, not a resolution for a field-vs-field collision
**inside** the target class produced by the move itself.

The two error messages the tool produces across attempts 1 and 2 directly contradict each other in
this exact scenario: the first insists `_workspaceManager` must move with the selected members ("Add
the missing name(s) to memberNames so they move together"); the second fails specifically because
moving it collides with a same-named field that already exists on the target. Following the first
message's own suggested fix walks straight into the second message's rejection.

## Ruled out

- Not a `callSiteFixups` problem. `callSiteFixups` in attempt 2 used safe, verified per-site
  `"FilePath:*"` keys (not the bare wildcard `"*"` form implicated in
  `blocking_error_movemember_callsitefixups_wildcard_corrupts_source.md`), and the same `CS0102`/
  `CS0229` collision would occur even with zero `callSiteFixups` supplied - it originates entirely
  from the forced inclusion of `_workspaceManager` in `memberNames`, not from any fixup value.
- Not a name or accessibility mismatch. Both fields are `private readonly IWorkspaceManager
  _workspaceManager`, confirmed via `GetMethodSource` on both constructors.
- Not disk corruption. Every reproduction was `dryRun: true` or was rejected pre-write by
  `ValidationFailed`; confirmed nothing changed on disk.

## Why this blocks the whole group, not just this one move

The same pattern (a `_workspaceManager` field, assigned from a same-named/typed constructor
parameter) is confirmed present in every other source class group 4 needs to fold into
`StructuralRefactoringEngine`: `AdvancedRefactoringEngine.cs`, `RefinementEngine.cs`,
`AdvancedTypeEngine.cs` (outlines read earlier this session; each declares its own
`_workspaceManager` field). Every one of those merges will hit this exact same collision. This is
load-bearing for group 4's entire structural-merge mechanism as currently achievable through
`MoveMember`, not a narrow or isolated case.

## What unblocks it

- `MoveMember` should detect when a "must move together" dependency (a field) already has a
  same-name, same-type, compatibly-initialized counterpart on the target class, and in that case
  treat the dependency as already satisfied - rewriting the moved method bodies' references to point
  at the target's existing field instead of also moving or duplicating the source field.
- Failing that, an explicit opt-in parameter (e.g. `treatAsSatisfied: ["_workspaceManager"]` or
  similar) that lets the caller assert "this field is already present with compatible shape on the
  target, do not include it in the move," which the tool could still sanity-check (same name,
  compatible type) before trusting.
- At minimum, the `ValidationFailed` error message for this specific collision (`CS0102` "already
  contains a definition for X" where X is a member the caller was told by the tool itself to add to
  `memberNames`) should name this exact scenario and suggest removing the field from `memberNames`
  and instead leaving the source class's references to it to be resolved against the target's
  existing field of the same name - rather than leaving the two error messages in direct
  contradiction with each other.
- Until fixed, group 4's `GranularRefactoringEngine` -> `StructuralRefactoringEngine` merge (and, by
  the same pattern, the planned `AdvancedRefactoringEngine`/`RefinementEngine`/`AdvancedTypeEngine`
  folds) cannot proceed via `MoveMember` as the tool currently behaves. Do not attempt a
  `Read`/`Edit`/`Write` bypass on the `.cs` files - per CLAUDE.md, a tool failure is a blocking
  finding, not license to route around the tool.

## Status of group 4's other work

Group 4's earlier `RenameSymbol` work (`AdvancedStructuralEngine` -> `StructuralRefactoringEngine`)
is unaffected and already complete and valid; only the `GranularRefactoringEngine` merge (and the
subsequent planned folds sharing this same field-collision shape) is blocked by this defect.

## Second confirmed instance: group 5 (logic simplification merge)

Reproduced again, independently, in engine-reorg group 5 ("Logic simplification merge" -
`LogicOptimizationEngine` + `AdvancedLogicEngine` + `CodeFlowEngine` -> `LogicSimplificationEngine`).
All three source/target classes independently declare `private readonly IWorkspaceManager
_workspaceManager` (`LogicOptimizationEngine`, `AdvancedLogicEngine`) or `private readonly
IWorkspaceReader _workspaceManager` (`CodeFlowEngine` - same field *name*, narrower interface type),
each assigned from a same-named constructor parameter - confirmed via `ConstructorParameter(view)` on
all three before attempting any move.

- `MoveMember(filepath: AdvancedLogicEngine.cs, className: "AdvancedLogicEngine", memberNames:
  ["InvertBooleanLogicAsync"], targetClassName: "LogicOptimizationEngine", dryRun: true)` (field NOT
  included) -> immediately rejected, `errorCode: "InvalidArgument"`, identical wording to attempt 1
  above (substituting `AdvancedLogicEngine`/`LogicOptimizationEngine`/`_workspaceManager`).
- Same call with `_workspaceManager` added to `memberNames` and explicit per-line `callSiteFixups`
  (not wildcard) supplying `"new LogicOptimizationEngine(_workspaceManager)"` /
  `"new LogicOptimizationEngine(_mgr)"` at the two affected test call sites
  (`RoslynSentinel.Tests.Integration/IntegrationTwentyNineTests.cs:213`,
  `RoslynSentinel.Tests.Battery/BatteryFourteenTests.cs:34`) -> `errorCode: "ValidationFailed"`:
  `CS0102` at `LogicOptimizationEngine.cs:651` ("already contains a definition for
  '_workspaceManager'"), `CS0103` x15, `CS0229` x7 ambiguity - same shape as attempt 2 above.
- Repeated a third time moving `CodeFlowEngine.ReduceBlockDepthAsync` (+`_workspaceManager`) into the
  same target, with correct per-line fixups for its own 3 call sites in
  `RoslynSentinel.Tests.Battery/BatteryFifteenTests.cs` (lines 82/101/119, `"new
  LogicOptimizationEngine(_mgr)"`) -> identical `CS0102`/`CS0229` collision, confirming the defect is
  not specific to identical field *types* - `CodeFlowEngine`'s field is typed `IWorkspaceReader`
  (narrower than the other two's `IWorkspaceManager`) and still collides purely on the shared field
  *name*.
- Every method body checked in both `AdvancedLogicEngine` (`InvertBooleanLogicAsync`,
  `ExtensionToStaticAsync`, `ConvertForEachToForAsync`) and `CodeFlowEngine`
  (`ReduceBlockDepthAsync`) reads `_workspaceManager` directly, so this blocks moving **every**
  member out of both classes into `LogicOptimizationEngine` - not a narrow case. Group 5's entire
  member-folding merge is blocked by this defect; only the pure rename
  `LogicOptimizationEngine` -> `LogicSimplificationEngine` (no field move involved) could proceed.

This second, independent reproduction (different classes, different field type in one case)
confirms the defect is general to `MoveMember`'s `RequireNoUnmovedDependencies` guardrail whenever
source and target already separately declare a same-named dependency field, not an artifact specific
to `_workspaceManager`/`IWorkspaceManager` or to the group-4 classes.

## Related

- `docs/current/blockers/blocking_error_movemember_callsitefixups_wildcard_corrupts_source.md` - the
  distinct, previously-known wildcard (`"*"`) `callSiteFixups` defect; this new defect is unrelated
  (uses safe `"FilePath:*"` keys, not the bare wildcard) and would occur even with zero
  `callSiteFixups` supplied, since it originates from the forced inclusion of `_workspaceManager` in
  `memberNames`, not from any fixup value.
- `docs/obsolete/blockers/blocking_error_movemember_nested_class_dependency_not_moved.md` - the
  `RequireNoUnmovedDependencies` guardrail implicated here is the same one documented (and partially
  fixed) there; that doc's fix made the guardrail correctly detect unmoved field dependencies in the
  first place, which is what now surfaces this new, previously-unreachable collision case once the
  caller follows its suggestion.
- `docs/obsolete/blockers/blocking_error_group4_refactoringengine_absorption_false_duplicates.md` -
  the other group-4 blocker found earlier this session; unrelated (that one is about a false premise
  in the plan text regarding `RefactoringEngine` absorbing `InstrumentationEngine`/
  `MsToolAugmentEngine` methods, not a `MoveMember` tool defect).
- `project_engine_reorg_group5_logic_simplification_blocked.md` (memory) - the group-5 session that
  reproduced this defect a second time, independently, against a different class family.
- `.claude/plans/enumerated-jumping-babbage.md` - group 4 ("Structural refactoring merge") and group
  5 ("Logic simplification merge") plan text.

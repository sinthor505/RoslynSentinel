# `MoveMember` forces a shared-name dependency field into the move, then rejects the result for colliding with the target's own copy of that same field

**Status:** FIXED 2026-09-28. No disk corruption occurred at any point while this was open - every
reproduction below was either `dryRun: true` or rejected pre-write by `ValidationFailed` (confirmed
nothing changed on disk). See "Fix implemented" section near the end for the file:line, what
changed, and an important scope caveat for group 5's `CodeFlowEngine` case.

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

## Root-cause investigation and fix design (2026-09-28, not implemented)

A dedicated read-only investigation pinned the root cause to exact file:line and drafted a minimal
fix, but stopped short of implementing it (multi-method control-flow change judged too large to
guess at in a single pass). Full design in memory
`project_movemember_duplicate_field_root_cause_and_fix_design.md`. Summary:

- **Forcing branch**: `RequireNoUnmovedDependencies`,
  `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs:344-391`. Builds its sibling-dependency set
  from `classSymbol.GetMembers()` (line 352) - the **source** class only. `targetClassName` is a
  parameter but is used only for string interpolation in the exception message (lines 386-389); it is
  never resolved to a symbol and never consulted before forcing a field into the required move set.
- **Rejection branch**: not inside `RequireNoUnmovedDependencies` itself - it's downstream, in
  `MoveInstanceMembersAsync` (lines 1204-1396), at two `AddMembers(membersToMove.ToArray())` call
  sites (~line 1289 same-file branch, ~line 1294 cross-file branch) that blindly append every moved
  member, including the forced-in field, onto the target class with no same-name check. The resulting
  duplicate is what the write-path's own compile-validation gate correctly (if unhelpfully) flags as
  CS0102/CS0229 two steps later.
- Confirmed by search: no existing helper in this file resolves the target's symbol or checks
  type-compatibility for a candidate field - this logic is entirely missing, not misapplied.
- **Fix shape**: resolve the target class symbol before `RequireNoUnmovedDependencies` runs; for each
  forced dependency that is a field, check whether the target already declares a same-name field with
  an identical or implicitly-convertible type - if so, treat it as already-satisfied (exclude it from
  `membersToMove` before both `AddMembers` calls, no body-rewrite needed since the bare-name reference
  already resolves against the target's own field). If a same-name field exists but is
  type-incompatible, keep failing, but fix the message to name the real problem instead of the
  current self-contradictory advice. No new tool schema parameter required.
- This would unblock both group 4 and group 5 once implemented and verified (group 5's
  `CodeFlowEngine._workspaceManager: IWorkspaceReader` case depends on `IWorkspaceManager` being
  assignable to `IWorkspaceReader` - not verified, worth confirming before assuming full coverage).
- Not implemented this session; a regression test would belong in
  `RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs` alongside the existing
  `MoveMemberAsync_*` family.

## Fix implemented (2026-09-28)

Implemented per the design above, with one refinement confirmed against actual source rather than
assumed. Both changes landed atomically in a single `ApplyUnifiedDiff` call (caller and callee
signatures had to change in lockstep - see below for why they could not be split).

- `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, `MoveMemberAsync`: now resolves
  `targetClassSymbol` (an `INamedTypeSymbol?`) before calling `RequireNoUnmovedDependencies`, and
  filters any field names the guardrail reports as already-satisfied out of `membersToMove` before
  the `AddMembers` call sites, so the pre-existing target field is never duplicated.
- `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs:384-474`, `RequireNoUnmovedDependencies`:
  signature changed to accept `INamedTypeSymbol? targetClassSymbol` and now returns
  `HashSet<string> alreadySatisfiedFields` instead of `void`. For each forced field dependency, if the
  target class already declares a same-name field, checks `SymbolEqualityComparer.Default.Equals`
  (exact type match) or `Compilation.ClassifyConversion(sourceField.Type, targetField.Type).IsImplicit`
  (implicit conversion, source type -> target type). Either match: treat as already-satisfied, exclude
  from the move, no body rewrite needed (bare-name reference already resolves against the target's own
  field post-move). Neither match: throw `ToolInvalidArgumentException` with a new message that names
  the real problem (`"... already declares its own field named 'X' of an incompatible type ('Y')...
  Rename one of the two fields first so they no longer collide, then retry."`) instead of the old
  message's self-contradictory "add it to memberNames" advice.
- Regression tests added in `RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs`,
  alongside the `MoveMemberAsync_*` family:
  - `MoveMemberAsync_FieldDependencyAlreadySatisfiedOnTarget_MovesWithoutDuplicatingFieldAsync` -
    moves a method depending on a same-name, same-type field that already exists on the target;
    asserts the move succeeds and the target's field is not duplicated.
  - `MoveMemberAsync_FieldDependencyIncompatibleTypeOnTarget_FailsWithCollisionMessageAsync` - same
    scenario but the target's same-name field has an incompatible type (`int` vs `string`); asserts
    `ToolInvalidArgumentException` with the new message naming the field and "incompatible type".
  - Both pass. `Build`: 0 errors, 20 warnings (all pre-existing, same set characterized in every prior
    engine-reorg group's memory). `RunTest` scoped to
    `RoslynSentinel.Tests.Battery` filtered to `PreviewInstanceMoveCallSitesTests`: 14/14 passed,
    0 failed, 0 skipped - no regressions in this test class.

### Group 4: fix covers it, safe to retry

`GranularRefactoringEngine`/`AdvancedRefactoringEngine`/`RefinementEngine`/`AdvancedTypeEngine` all
declare `_workspaceManager` typed **exactly** `IWorkspaceManager`, identical to
`StructuralRefactoringEngine`'s own field. `sameType` (`SymbolEqualityComparer.Default.Equals`) will
match directly - no conversion-classification nuance involved. The fix should let all four folds
proceed via `MoveMember` without forcing the field or hitting the CS0102/CS0229 collision. Not
retried in this session (out of scope, reserved for the coordinator/follow-up).

### Group 5: fix covers `AdvancedLogicEngine`, does NOT cover `CodeFlowEngine` - verified, not assumed

`AdvancedLogicEngine`'s `_workspaceManager` is typed exactly `IWorkspaceManager`, matching
`LogicSimplificationEngine`'s own field exactly (`sameType` match) - this fold should proceed cleanly,
same as group 4.

`CodeFlowEngine`'s `_workspaceManager` is typed `IWorkspaceReader`, and this pairing was checked
against the actual interface declaration rather than assumed: `IWorkspaceManager.cs:14-16` confirms
`public interface IWorkspaceManager : ..., IWorkspaceHealthReporter, IWorkspaceMutator, IRateLimiter,
ISymbolResolver, IWorkspaceReader` - i.e. `IWorkspaceManager` **extends** `IWorkspaceReader`
(derived-to-base direction). The fix's check is `ClassifyConversion(sourceField.Type,
targetField.Type).IsImplicit` where, for this move, `sourceField` is `CodeFlowEngine`'s field
(`IWorkspaceReader`) and `targetField` is `LogicSimplificationEngine`'s pre-existing field
(`IWorkspaceManager`). That is a **base-to-derived** conversion (`IWorkspaceReader ->
IWorkspaceManager`), which C# does not treat as an implicit reference conversion - only the opposite
direction (derived-to-base, `IWorkspaceManager -> IWorkspaceReader`) is implicit. So
`implicitlyConvertible` evaluates `false` for this pairing, `sameType` is also `false`, and
`CodeFlowEngine.ReduceBlockDepthAsync`'s move will land in the **incompatible-type rejection branch**,
not the already-satisfied branch.

This is not a silent failure, though: it now throws the new, correctly-worded
`ToolInvalidArgumentException` naming `_workspaceManager`, both concrete types
(`IWorkspaceReader`/`IWorkspaceManager`), and suggesting a rename - a real improvement over the old
self-contradictory message, and the caller gets an accurate diagnosis instead of a CS0102 cascade.
But it means group 5's `CodeFlowEngine` fold is **not unblocked by this fix as implemented** and
still needs a manual resolution before `MoveMember` will move `ReduceBlockDepthAsync` automatically -
options include widening `CodeFlowEngine`'s field to `IWorkspaceManager` first (a separate small
edit), or renaming one of the two fields per the tool's own new suggestion. `AdvancedLogicEngine`'s
two other methods are unaffected and can proceed.

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

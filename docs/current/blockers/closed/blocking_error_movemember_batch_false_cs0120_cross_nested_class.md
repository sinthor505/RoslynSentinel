# `MoveMember`'s call-site auto-resolver picks an unreachable field from an enclosing/nested class

**Status: FIXED.** Root cause traced and fixed in
`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, `PreviewInstanceMoveCallSitesAsync`, in the
`candidates` computation (originally around line 1248, now ~1245-1291 after the fix). The
`LookupSymbols(...).Where(...)` filter accepted any lexically-visible field/property/parameter/local
of the destination type, including instance members of an outer enclosing class that are
name-visible to a nested class but not actually accessible unqualified (C# nested classes hold no
implicit outer-instance reference). Fixed by adding an `IsReachableUnqualified(ISymbol s)` local
function - a static candidate is always fine; an instance candidate is only usable unqualified if
declared on the call site's own enclosing type or one of that type's base types - and gating the
existing `candidates` filter with it (`&& IsReachableUnqualified(s)`). Commit 695bbeb0. Regression
test added: `OuterClassFieldOfDestinationType_NotTreatedAsUnqualifiedCandidateForNestedCallSiteAsync`
in `RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs` (after
`NoInScopeCandidate_ClassifiesAsNoCandidateIntroducibleAsync`). Build: 0 errors, 20 pre-existing
warnings, none new. Tests: 15/15 in `PreviewInstanceMoveCallSitesTests.cs` (including the new one),
16/16 in a broader `MoveMember`/`InstanceMove`/`StructuralRefactoring` sweep across
`RoslynSentinel.Tests.Battery` - zero regressions. **Two corrections to the reproduction evidence
below, found while re-verifying before the fix** (kept as-is otherwise, for history):
1. The title's and body's "sibling nested class" framing for the Group 4 case is imprecise:
   `Bug77IntroduceParameterRegressionTests` is genuinely nested INSIDE `Bug70_74RegressionTests`
   (parent-child), not a sibling of it. This doesn't change the defect classification (the access
   is still invalid either way - nested classes don't get an implicit outer-instance reference
   regardless of whether the other class is a sibling or the direct parent) but the "sibling" wording
   throughout this doc is inaccurate and should be read as "an enclosing type not reachable via the
   call site's own type or its base types."
2. The "batch size >=2 is the trigger" theory (reproduction step 3, "Why this blocks Group 4") is
   disproven: reproduction step 3's single-member move of `IntroduceParameterAsync` alone never
   reached the compile-validation stage in the first place, because it supplied no fixup for the
   real unresolved site at `NewImplementationsTests.cs:984` and was rejected earlier
   (`UnresolvedCallSites`) - it never actually got far enough to observe whether CS0120 would occur.
   The true trigger, per the fix above, is purely "does an out-of-reach same-named/same-typed field
   exist anywhere lexically visible from the call site" - independent of batch size. A corrected
   single-member repro (the one needed fixup supplied) reproduces the identical CS0120 x2, confirming
   batch size was never the actual gating condition.

Distinct from both other `MoveMember` defects already on file: the duplicate-field-on-target
collision (fixed, commit 301c9518, see
`docs/current/blockers/blocking_error_movemember_duplicate_field_on_target_collision.md`) and the
bare `"*"` `callSiteFixups` wildcard corruption
(`docs/current/blockers/blocking_error_movemember_callsitefixups_wildcard_corrupts_source.md`). Do
not conflate this with either. No disk corruption occurred - every reproduction below was
`dryRun: true` or was rejected pre-write by `ValidationFailed`; confirmed nothing changed on disk.

**Groups 4 and 5 should now be retried** - both reproduction directions (Group 4's nested class
wrongly reaching for an outer field; Group 5's outer field wrongly reached into a nested class) are
fixed and covered by the regression test.

## What was being attempted

Engine-reorg group 4 ("Structural refactoring merge") of `.claude/plans/enumerated-jumping-babbage.md`,
retried this session past the now-fixed duplicate-field defect. Folding
`GranularRefactoringEngine` (`RoslynSentinel.Advanced/GranularRefactoringEngine.cs`) into
`StructuralRefactoringEngine` (`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`) via
`MoveMember`, moving all 18 planned members (`RemoveNodeFormattedAsync`, `RunMicroRefactoringAsync`,
`ApplyTypeToVar`, `ApplyRemoveLocalAtLine`, `ApplyAddBraces`, `ApplyRemoveBraces`,
`ApplyExtractConstant`, `InlineFieldAsync`, `InlineParameterAsync`, `ConvertMethodToIndexerAsync`,
`IntroduceFieldAsync`, `IsExpressionClassScopeSafe`, `IntroduceParameterAsync`,
`IntroduceVariableAsync`, `MoveTypeToOuterScopeAsync`, `ExtractMembersToPartialAsync`,
`IntroduceParameterObjectAsync`, `ParamToPropertyRewriter`) together, with a complete
`callSiteFixups` map covering every one of the 39 genuinely-unresolved call sites across 10 test
files.

## Reproduction

1. Full 18-member batch, `targetClassName: "StructuralRefactoringEngine"`, `targetFilepath:
   RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, `dryRun: true`, `callSiteFixups` covering
   all 39 real unresolved sites (`StubImplementationTests.cs`, `IntegrationTwentyNineTests.cs`,
   `BatteryTwentyFiveTests.cs`, `BugFixTests.cs` lines 1409/1427/1595/1615/1628 specifically -
   **not** 2206/2230, which were never in the unresolved list, `RefactoringTests.cs`,
   `BatterySixteenTests.cs`, `NewImplementationsTests.cs`, `IntegrationThirtyFourTests.cs`,
   `FinalRegressionTests.cs`, `AdvancedToolsTests.cs`) -> result: `errorCode: "ValidationFailed"`,
   two new compiler errors:

   > CS0120: An object reference is required for the non-static field, method, or property
   > 'BugFixTests.Bug11RegressionTests.Bug70_74RegressionTests._advancedStructuralEngine'

   at `RoslynSentinel.Tests.Advanced/BugFixTests.cs:2206` and again at `:2230`, columns 40-65 in each
   case (verbatim, both occurrences identical text).

2. Reduced to a 2-member batch (`IntroduceParameterAsync` + `IntroduceVariableAsync`), correct
   fixups scoped to only their own genuinely-unresolved call sites (none targeting 2206/2230) ->
   identical CS0120 x2 at the same two lines. Repeated again with a different second member
   (`IntroduceParameterAsync` + `ApplyTypeToVar`) -> same result. Confirms the defect is not specific
   to which second member is included - any 2-member batch reproduces it.

3. Moved `IntroduceParameterAsync` alone (single member, no other members in the batch) -> the
   dry run's only failure is an unrelated, correctly-reported unresolved call site at
   `NewImplementationsTests.cs:984`. Zero error at `BugFixTests.cs:2206` or `:2230`. Proves the
   defect is purely a function of batch size (>=2 members moved together in one `MoveMember` call),
   not of which member(s) are in the batch.

## Where it happened

- Tool: `MoveMember`, all calls `dryRun: true`.
- Source: `RoslynSentinel.Advanced/GranularRefactoringEngine.cs`, class `GranularRefactoringEngine`.
- Target: `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, class `StructuralRefactoringEngine`.
- Surfaced at: `RoslynSentinel.Tests.Advanced/BugFixTests.cs:2206` and `:2230`, inside nested class
  `Bug77IntroduceParameterRegressionTests` (declared at `BugFixTests.cs:2168-2235`), methods
  `BUG_77_IntroduceParameter_SimpleExpression_NoServerCrash` (line 2188, call at 2206) and
  `BUG_77_IntroduceParameter_VariableExpression_NoServerCrash` (line 2212, call at 2230).

## Root cause, confirmed via direct reproduction

`RoslynSentinel.Tests.Advanced/BugFixTests.cs` has deeply nested test-fixture classes:
`BugFixTests` -> `Bug11RegressionTests` -> `Bug70_74RegressionTests` (declares its own instance
field `private StructuralRefactoringEngine _advancedStructuralEngine;` at line 1782, unrelated to
this move) -> a sibling nested class, `Bug77IntroduceParameterRegressionTests` (declares its own,
separate fields `_workspaceManager` and `_granularEngine` at lines 2171-2172). The two call sites at
2206 and 2230 read `_granularEngine.IntroduceParameterAsync(...)` - confirmed via `GetMethodSource`
to be genuine, correct, already-compiling code with zero relation to `_advancedStructuralEngine`.

When `MoveMember` moves `GranularRefactoringEngine` members into `StructuralRefactoringEngine` and
needs to rewrite call sites in `BugFixTests.cs`, its auto-resolver (`autoResolveCallSites`, default
`true`) searches the **whole file** for any in-scope field/variable of the target type
(`StructuralRefactoringEngine`) to use as a substitute receiver wherever the original receiver's
declared type is being merged away. It finds `_advancedStructuralEngine` - declared on the outer
class `Bug70_74RegressionTests` - and incorrectly treats it as a valid rewrite candidate for the
already-correctly-typed, never-unresolved call sites inside the sibling nested class
`Bug77IntroduceParameterRegressionTests`, even though:

1. Those two call sites were never unresolved in the first place. They auto-resolve on their own
   with zero errors, confirmed by reproduction step 3 above (single-member move of
   `IntroduceParameterAsync` produces no error or rewrite at either line).
2. `_advancedStructuralEngine` is not accessible from `Bug77IntroduceParameterRegressionTests`'s
   instance methods even if it were the intended receiver. It is an instance field of a different
   sibling nested class (`Bug70_74RegressionTests`), not of `Bug77IntroduceParameterRegressionTests`
   and not of any base class. C# requires a captured outer-instance reference (`Outer.this`) to
   access it, which these nested test-fixture classes do not hold. Using it unqualified is invalid
   regardless of rewrite intent - which is exactly the CS0120 produced.

This points to a scoping bug in `MoveMember`'s candidate-search for auto-resolved call-site
receivers: it searches "any field of the target type visible by name at the call site's position"
(via `SemanticModel.LookupSymbols`) rather than scoping the search to types actually reachable from
the call site (the call site's own containing class, its base classes, and outer classes only if an
outer-instance reference is actually available).

**Confirmed file:line (fixed):** `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`,
`PreviewInstanceMoveCallSitesAsync`, the `candidates` computation (originally line ~1248). The
`.Where(s => (s is IFieldSymbol || s is IPropertySymbol || s is IParameterSymbol || s is
ILocalSymbol) && IsDestinationType(s))` filter had no reachability check at all - `LookupSymbols`
returns a nested class's enclosing-type instance members by name because C# lexically allows
referencing an enclosing type's member names from a nested class for lookup/overload-resolution
purposes, but that is not the same as the reference being valid unqualified (nested classes hold no
implicit outer-instance reference, unlike VB.NET/Java). Fixed by adding `IsReachableUnqualified` and
gating the filter with it - see the FIXED status note at the top of this doc for the exact fix and
commit.

## Why this blocks Group 4

The 18 `GranularRefactoringEngine` members have real intra-class dependencies -
`RunMicroRefactoringAsync` is a dispatcher that calls the private helpers `ApplyTypeToVar`/
`ApplyRemoveLocalAtLine`/`ApplyAddBraces`/`ApplyRemoveBraces`/`ApplyExtractConstant` directly by
unqualified name. Moving `ApplyTypeToVar` alone (tried as a workaround, to move everything one
member at a time) fails with CS0122 ("is inaccessible due to its protection level") because
`RunMicroRefactoringAsync` in the old class still calls it unqualified and the method is private -
confirmed via a real dry-run reproduction, not assumed. So members cannot all be moved one at a
time either; some must move together as a cluster, which is exactly the condition (batch size >=2)
that triggers this defect. There is no batch size or membership that both (a) satisfies the real
same-class dependencies and (b) avoids tripping the false CS0120, short of fixing the tool or first
changing `GranularRefactoringEngine`'s private helpers to a broader accessibility - an unrelated
correctness/design change to the source that would exist purely to route around a tool defect, and
is out of scope as a silent workaround.

## What was NOT done as a workaround (and why)

Did not widen `ApplyTypeToVar`/etc. accessibility, did not rename `BugFixTests.cs`'s
`_advancedStructuralEngine` field, did not use a bare `"*"` `callSiteFixups` wildcard (the known,
separate corruption defect), and did not force `autoResolveCallSites: false` (tested - it simply
rejects the whole call immediately, requiring an explicit fixup for every one of the 39 sites with
no partial/auto fallback, so it does not change the false-candidate behavior at all; confirmed via a
direct test call, this option was ruled out rather than merely assumed to fail).

## What unblocks it

- [DONE] Trace the auto-resolver's candidate-search implementation to its actual file:line - see
  "Root cause, confirmed via direct reproduction" above.
- [DONE] Fix: scope the candidate search to types actually reachable from the call site under normal
  C# rules - the call site's own containing class and its base classes always; an outer class's
  instance members only when actually reachable (in practice, `IsReachableUnqualified` as
  implemented does not special-case a true nested-type outer-instance reference either, since C#
  nested classes never get one implicitly - only static members or same-type-or-base-type instance
  members are accepted).
- Not implemented (left as a secondary hardening idea, not needed once the above fix landed): the
  auto-resolver could additionally avoid touching a call site that was never on the
  `UnresolvedCallSites` list in the first place. Since the candidate-search fix already prevents the
  unreachable field from being offered as a candidate at all, this is no longer load-bearing, but
  would be a reasonable defense-in-depth addition if a similar defect class recurs elsewhere.
- Not implemented: the `ValidationFailed` message for a CS0120 produced by an auto-resolve rewrite
  does not (yet) name that the offending identifier came from `MoveMember`'s own rewrite rather than
  from `callSiteFixups` or pre-existing source. Since this specific defect is fixed, this is now a
  general error-message-quality improvement, not a blocking gap.
- Groups 4 and 5 are unblocked - retry both via `MoveMember` as originally planned.

## Status of Group 4's other planned folds

`AdvancedRefactoringEngine`, `RefinementEngine`, and `AdvancedTypeEngine` (the other three source
engines Group 4 plans to fold into `StructuralRefactoringEngine`) have not yet been attempted this
session. Whether they hit this same defect is untested - noted here as an open question for whoever
picks this up next, not a confirmed status either way.

The git working tree is clean of any partial edits from this investigation. Every reproduction above
was `dryRun: true` or was rejected pre-write by `ValidationFailed`/`UnresolvedCallSites`; confirmed
via `Git(status)` showing only the pre-existing `AdvancedStructuralEngine.cs` using-directive change
(unrelated, already-legitimate, predating this investigation) as modified.

## Second confirmed instance: Group 5 (logic simplification merge)

Reproduced the identical defect class in a completely unrelated fold, ruling out any dependency on
`GranularRefactoringEngine`'s or `BugFixTests.cs`'s specific nested-class shape from the Group 4
reproduction above.

**What was being attempted:** Engine-reorg group 5 ("Logic simplification merge") of
`.claude/plans/enumerated-jumping-babbage.md`. Folding `AdvancedLogicEngine`
(`RoslynSentinel.Advanced/AdvancedLogicEngine.cs`) into `LogicSimplificationEngine`
(`RoslynSentinel.Advanced/LogicOptimizationEngine.cs`, filename unchanged, class renamed from
`LogicOptimizationEngine` in commit 34d1eb1), moving all 12 planned members
(`InvertBooleanLogicAsync`, `ConvertIfToSwitchExpressionAsync`, `ConvertIfToSwitchStatementAsync`,
`ExtensionToStaticAsync`, `ConvertStaticToExtensionAsync`, `ConvertForEachToForAsync`,
`ConvertForToForEachAsync`, `ConvertWhileToForAsync`, `IfBranch`, `TryExtractIfChainBranches`,
`GetSingleReturnExpression`, `IndexedAccessRewriter`) together via `MoveMember`, `dryRun: true`,
with a `callSiteFixups` map covering 5 files' genuinely-unresolved call sites
(`BatteryFourteenTests.cs`, `IntegrationTwentyNineTests.cs`, `BatteryTwentyFiveTests.cs`,
`RefactoringTests.cs`, `BatteryTwentyEightTests.cs`).

Before this reproduction, a genuine, unrelated pre-existing gap was found and fixed: the target
class (`LogicOptimizationEngine.cs`) was missing `using Microsoft.CodeAnalysis.FindSymbols;`, needed
by `SymbolFinder` used inside one of the moved-in `AdvancedLogicEngine` methods. Fixed via
`UsingDirective(operation: "add", namespaceName: "Microsoft.CodeAnalysis.FindSymbols")` - this landed
cleanly and is a real, valid fix, unrelated to the defect documented in this file.

**Reproduction:** retrying the same 12-member dry run after the using-directive fix, the CS0103
("SymbolFinder does not exist") is gone (confirming that fix worked), but a new-shaped, identically
patterned error persists:

> CS0120: An object reference is required for the non-static field, method, or property
> 'BugFixTests._logicOptimizationEngine'

at `RoslynSentinel.Tests.Advanced/BugFixTests.cs:2487`.

**Confirmed via direct source inspection** (`Search(mode: text)`, not assumed):
- `_logicOptimizationEngine` is declared at `BugFixTests.cs:26` as a field of the **outer, top-level**
  `BugFixTests` class (`private LogicSimplificationEngine _logicOptimizationEngine;`), assigned in
  `BugFixTests.Setup` at line 42. It is referenced unqualified only twice elsewhere in the entire
  file: the constructor assignment (line 42) and inside `BUG_54_AddGuardClauses_IncludesStringParameters`
  (line 331) - both directly inside `BugFixTests` itself, nowhere near line 2487.
- Line 2487 is inside `BUG_56_ConvertStaticToExtension_EnsuresClassIsStatic`, a method on a
  different, separately-declared nested test-fixture class. That class declares its **own**,
  unrelated field `_advancedLogicEngine` at line 2417 (`private AdvancedLogicEngine
  _advancedLogicEngine;`), assigned in its own `Setup` at line 2428. The real, correct,
  already-compiling source at line 2487 is:
  `var result = await _advancedLogicEngine.ConvertStaticToExtensionAsync("StringExtensions.cs", "IsValidEmail");`
  - zero relation to `_logicOptimizationEngine`.

This is the same defect shape as the Group 4 reproduction, with the outer/nested relationship
inverted: there, a sibling nested class's field was wrongly reached into; here, the auto-resolver
reached instead into the distant **outer** class's field to substitute into an **inner** nested
class's already-correct call site. Both are invalid under normal C# scoping (an outer class's
private instance field is not usable unqualified from a nested class's instance method without an
available outer-instance reference - and even where the language does allow implicit outer-instance
access from a true nested type, `BUG_56_ConvertStaticToExtension_EnsuresClassIsStatic`'s own class
already declares its own same-purpose field, so the outer field was never the correct receiver to
begin with). Confirms this is a general property of the auto-resolver's candidate search - not
specific to `GranularRefactoringEngine`, `StructuralRefactoringEngine`, or `BugFixTests.cs`'s
particular Group-4 nesting depth - and reproduces across two structurally different fold pairs and
two different injected fields (`_advancedStructuralEngine` in Group 4; `_logicOptimizationEngine`
here).

**Status of Group 5:** the `AdvancedLogicEngine` fold is blocked by this defect on the same terms as
Group 4's `GranularRefactoringEngine` fold - not yet proven to require a multi-member batch the way
Group 4's intra-class dependency chain was proven (via the `ApplyTypeToVar` CS0122 reproduction), but
the defect triggers on any 2+-member batch per the Group 4 reproduction's step 2, and `AdvancedLogicEngine`
has 12 members planned to move together, well above that threshold. Not re-tested at single-member
granularity for this specific fold (out of scope for this confirmation; the defect's batch-size
trigger condition was already established generally in the Group 4 reproduction above and is not
believed to be pairing-specific). `AdvancedLogicEngine.cs` is left completely untouched - not folded,
not partially edited, not deleted; only the target file's using-directive gap was fixed, which is
unrelated to and does not touch the source engine at all.

## Related

- `.claude/plans/enumerated-jumping-babbage.md` - Group 4 ("Structural refactoring merge") plan
  text.
- `docs/current/blockers/blocking_error_movemember_duplicate_field_on_target_collision.md` - the
  now-fixed sibling defect (commit 301c9518) this reproduction effort was originally retrying past;
  unrelated root cause (target-side duplicate-field detection vs. this doc's call-site
  auto-resolver reachability gap).
- `docs/current/blockers/blocking_error_movemember_callsitefixups_wildcard_corrupts_source.md` - the
  other known, distinct `MoveMember` defect (bare `"*"` wildcard); ruled out as the cause here since
  no wildcard fixup was used in any reproduction.
- `RoslynSentinel.Advanced/GranularRefactoringEngine.cs` - source class, 18 members plus 1 nested
  type planned to move.
- `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs` - target class `StructuralRefactoringEngine`.
- `RoslynSentinel.Tests.Advanced/BugFixTests.cs:1774-2236` - `Bug70_74RegressionTests` and its
  nested `Bug77IntroduceParameterRegressionTests` at 2168-2235, the fixture exposing the defect.
- `.claude/plans/enumerated-jumping-babbage.md` - Group 5 ("Logic simplification merge") plan text,
  source of the second confirmed instance.
- `RoslynSentinel.Advanced/AdvancedLogicEngine.cs` - Group 5 source class, 12 members plus 1 record
  plus 1 nested type planned to move; left untouched.
- `RoslynSentinel.Advanced/LogicOptimizationEngine.cs` - Group 5 target class
  `LogicSimplificationEngine`; only its unrelated missing `using
  Microsoft.CodeAnalysis.FindSymbols;` directive was fixed this session, not the fold itself.
- `RoslynSentinel.Tests.Advanced/BugFixTests.cs:26` (`_logicOptimizationEngine` field) and `:2417-2487`
  (`BUG_56_ConvertStaticToExtension_EnsuresClassIsStatic` and its enclosing fixture's own
  `_advancedLogicEngine` field) - the Group 5 reproduction site.
- [[project_engine_reorg_group4_structural_merge_blocked]],
  [[project_engine_reorg_group5_logic_simplification_blocked]] - memory files for both blocked
  groups; both superseded in part by this doc (the old duplicate-field defect they describe is fixed;
  this new batch-CS0120 defect is what now blocks both).

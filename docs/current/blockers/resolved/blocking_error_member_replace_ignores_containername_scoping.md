# `Member(operation: "replace")` ignores `containerName` and edits the wrong same-named method

**Status:** RESOLVED 2026-09-27.

Root cause: `RefactoringEngine.ReplaceMemberAsync` and `RefactoringEngine.RemoveMemberAsync`
(RoslynSentinel.Basic/RefactoringEngine.cs, formerly lines 1297 and 1613) never accepted a
`containerName` parameter at all - `RefactoringStructuralImpl.Member` (RoslynSentinel.Basic/
RefactoringStructuralImpl.cs) called both with only `memberName` + snippet-disambiguation args.
Internally both resolved candidates via `SymbolNavigationEngine.ResolveCandidates(root, sourceText,
memberName, ...)` - a bare by-name scan across the whole file with no container narrowing - then
disambiguated only by `contextSnippet`/`lineBefore`/`lineAfter` via `ResolveBySnippetOrThrow`. With
no snippet supplied and 2+ same-named methods across different classes, `ResolveBySnippetOrThrow`
picked the first candidate in file order, which is always the first textually-occurring same-named
method - exactly the observed symptom. `Member(operation: "view")`'s path
(`SymbolNavigationEngine.GetContainerMembersAsync`) was unaffected because it resolves the
*container* node first via `ResolveCandidates(..., containerName, ...)` and only lists that
container's direct `Members`, so it never depended on `memberName` uniqueness.

The fix: added an optional `containerName` parameter to both `ReplaceMemberAsync` and
`RemoveMemberAsync`, and inserted a call to the already-existing (but previously unused by these two
methods) `SymbolNavigationEngine.FilterByContainingType(candidates, containerName)` helper right
after building the member-candidate list and before `ResolveBySnippetOrThrow` - the same helper
`AddSummaryCommentCoreAsync`/`RemoveSummaryCommentAsync`/`GetSummaryCommentAsync` already used for
the identical disambiguation need. `RefactoringStructuralImpl.Member` was updated to thread its own
`containerName` parameter through to both calls. `FilterByContainingType` is a safe no-op when
`containerName` is null and falls back to the unfiltered set if narrowing would zero out all
candidates, so no existing caller (engine-level tests calling `ReplaceMemberAsync`/`RemoveMemberAsync`
without a container argument) was affected.

Other `Member` operations checked and confirmed NOT affected: `addMember`, `addTypedMember`,
`addTopLevelType` all resolve their target container explicitly via `containerName` as a required/
optional parameter on `AddMemberAsync`/`AddPropertyAsync`/`AddFieldAsync`/etc. *before* doing any
name-based member work, so they never shared this bug (also matches the blocker doc's own
black-box observation that `addTypedMember` correctly targeted all 4 classes).

Regression tests added: `ReplaceMember_WithContainerName_ScopesToRequestedContainerOnly` and
`RemoveMember_WithContainerName_ScopesToRequestedContainerOnly` in
`RoslynSentinel.Tests.Basic/CodeEditingTests.cs`, reproducing the two-classes-same-method-name
scenario directly against `RefactoringEngine`.

Commit: 76f6aed40a2324d9e3987be1174461b15b632792.

## What was being attempted

Group 3 of the engine-reorganization plan (dissolving `AnalysisEngine` into `ResourceSafetyEngine`/
`DeadCodeEngine`/`ArchitecturalEngine`/`AntiPatternEngine`, see
`C:\Users\Administrator\.claude\plans\enumerated-jumping-babbage.md`). A bulk `MoveMember` of 30
members into `AntiPatternEngine` was rejected with 91 unresolved call sites (no in-scope
`AntiPatternEngine` reference at each site). The chosen fix, consistent with this session's
established pattern, was to add an `_antiPatternEngine` field to every affected class so the
post-move call sites have something to redirect to.

`RoslynSentinel.Tests.Advanced\SentinelAccuracyTests.cs` is a single file containing ~20 separate
`[TestFixture]` classes, several of which independently declare a field named `_engine` and a
method named `Setup` with near-identical bodies (differing only in which engine type `_engine` is
constructed as). Four of these classes - `SemaphoreAccuracyTests`, `MismatchedAwaitAccuracyTests`,
`MissingGenericConstraintTests`, `AnalysisEngineExtended2Tests` - needed `_antiPatternEngine`
initialized inside their own `Setup()`. `Member(operation: "addTypedMember", containerName: ...)`
had already correctly added the field to each of the 4 classes (confirmed via
`Member(operation: "view", containerName: ..., memberName: "_antiPatternEngine")` returning the
right class each time). The next step was to initialize that field inside each class's own `Setup`.

## The exact symptom

`Member(operation: "replace", containerName: "MismatchedAwaitAccuracyTests", memberName: "Setup",
newMemberSource: "...")` (and the same call shape for the other 3 target classes) failed with:

```
CS0029 at SentinelAccuracyTests.cs:26: Cannot implicitly convert type
'RoslynSentinel.Advanced.AnalysisEngine' to 'RoslynSentinel.Advanced.AsyncSafetyEngine'
CS0103 at SentinelAccuracyTests.cs:27: The name '_antiPatternEngine' does not exist in the
current context
```

Line 26-27 is inside `FireAndForgetAccuracyTests.Setup()` (the *first* `[TestFixture]` class in the
file, whose own `_engine` field is typed `AsyncSafetyEngine`) - not
`MismatchedAwaitAccuracyTests.Setup()` at lines 468-473, which is what `containerName` explicitly
asked for. The tool tried to splice `_engine = new AnalysisEngine(...)` /
`_antiPatternEngine = new AntiPatternEngine(...)` into the wrong class entirely.

Confirmed this is a real scoping miss, not user error, by calling `Member(operation: "view",
containerName: "MismatchedAwaitAccuracyTests", memberName: "Setup")` immediately before and after:
`view` correctly returned lines 468-473 both times (the right class, right `_engine` type,
`AnalysisEngine`). Only `replace` picked the wrong same-named method. All 4 `replace` calls in the
batch (`SemaphoreAccuracyTests`, `MismatchedAwaitAccuracyTests`, `MissingGenericConstraintTests`,
`AnalysisEngineExtended2Tests`) failed with the identical shape of error, always pointing at
`FireAndForgetAccuracyTests.Setup()` (the first `Setup` method textually in the file) regardless of
which `containerName` was requested.

## Why no corruption happened

`Member`'s replace-then-validate-before-write gate did its job here: all 4 calls report the
introduced-compiler-error and explicitly state "change not applied." Re-checked
`FireAndForgetAccuracyTests.Setup()` via `Member(view)` after the failed attempts - its `_engine`
field is still `AsyncSafetyEngine`, method body unchanged, matching the pre-edit expectation
exactly. `Git(status)` also shows `SentinelAccuracyTests.cs` as modified only from the earlier
successful `addTypedMember` calls (which correctly targeted the right containers), not from the
failed `replace` calls. So the failure mode here is "wrong target, caught before write," not
silent data corruption - but it is still a correctness bug in the tool's own claimed contract:
`containerName` is documented/behaves as an exact-match scope for `view` and (per the plan's own
expectation and this session's established use for `addTypedMember`) should scope `replace`
identically.

## Where it happened

- Tool: `Member` (`mcp__root_roslyn_sentinel_advanced_stdio__Member`), `operation: "replace"`,
  `containerName` + `memberName` combination, when multiple classes in the same file declare a
  method with the same name (`Setup`).
- File: `RoslynSentinel.Tests.Advanced\SentinelAccuracyTests.cs` (4086 lines, ~20 `[TestFixture]`
  classes, at least 5 of them named `Setup` with an `_engine` field of varying engine type).
- Contrast: `Member(operation: "view", containerName: ..., memberName: "Setup")` against the exact
  same file/containerName/memberName triple correctly disambiguates every time.

## Root cause - NOT traced to source

Not yet read the `Member` tool's implementation for the `replace` code path. Hypothesis, not
confirmed: `replace`'s member-lookup likely resolves `memberName` via a first-match-by-name scan
across the whole file/syntax tree (picking up the first `Setup` method encountered, textually),
while `view`'s lookup path correctly filters by `containerName` first. This would explain why the
wrong target is always the *first* `Setup` in the file (`FireAndForgetAccuracyTests`, the first
`[TestFixture]` class) regardless of which real `containerName` was requested.

## Why this blocks (per CLAUDE.md failure doctrine)

`containerName` exists specifically to disambiguate identically-named members across multiple
types in one file - the exact scenario this file was built to test/exercise. If `replace` silently
resolves to the wrong container, any caller relying on `containerName` for a `replace` in a
multi-class file cannot trust the edit lands on the intended target without independently
re-verifying via `view` afterward every time - which defeats the purpose of the scoping parameter
and turns every `replace` in such a file into a two-call, manually-verified operation. The
validate-before-write gate prevented actual corruption this time only because the wrong-target
edit happened to introduce a compiler error; a case where the wrong same-named method has a
type-compatible body (plausible for near-duplicate test fixtures like this file's `Setup` methods)
would silently succeed against the wrong class with zero error signal.

## What unblocks it

- Read `Member`'s implementation for `operation: "replace"`'s member-resolution path and compare it
  directly against `operation: "view"`'s path to find where the `containerName` filter is applied
  in one but not the other (or applied in both but incorrectly in `replace`).
- Fix `replace` to filter candidates by `containerName` before matching `memberName`, identically to
  `view`.
- Add a regression test with two classes in one file sharing a same-named method, asserting
  `Member(replace, containerName: A, memberName: X)` edits only class A's method and leaves class
  B's untouched.
- Until fixed: treat any `Member(replace, containerName: ..., memberName: ...)` call as unsafe
  whenever the target file contains another member of the same name in a different container -
  verify the actual edited location via `view` immediately after, or avoid `replace` for such cases
  and use a more specific mechanism (e.g. `ReplaceSnippet` with enough unique surrounding context,
  or per-member `MoveMember`/dedicated field-init tooling) instead.

## Related

- This session's earlier `MoveMember` batch (30 members -> `AntiPatternEngine`) is what created the
  need to initialize `_antiPatternEngine` in these 4 `SentinelAccuracyTests.cs` classes in the first
  place; that move is still pending, blocked on this.
- `docs/current/blockers/resolved/blocking_error_constructorparameter_add_no_default_value.md` (see
  its resolution note) - unrelated tool, but the same engine-reorg task; noting for continuity since
  both blockers occurred in the same work session on the same plan group.

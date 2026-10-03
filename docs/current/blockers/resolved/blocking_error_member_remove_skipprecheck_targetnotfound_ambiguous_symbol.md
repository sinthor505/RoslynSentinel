# Member(remove, skipPrecheck:true) fails TargetNotFound when the member name is ambiguous solution-wide, even though the identical call without skipPrecheck resolves the same target correctly

Status: FIXED (2026-09-29, commit 5a55e39). Root cause was NOT the ambiguous-solution-wide-name
hypothesis this doc originally proposed (see "Root cause" section below, left in place with a
correction). The real defect: `RemoveMemberAsync`'s candidate resolution deliberately excludes
type-level declarations (`Class`/`Interface`/`Struct`/`Record`/`Enum`/`EnumMember`) from its member
pool by design (`Member(remove)` only operates on class-level members). When that exclusion was
the actual reason nothing resolved, the failure reported a generic `// Member not found.`
(`TargetNotFound`) instead of naming the real cause. Both the precheck path (`FindCallersAsync`,
which has no such exclusion and resolves type declarations fine) and the `skipPrecheck:true` removal
path call the *same* `RemoveMemberAsync` internally - so the doc's original theory that they use
different/buggier resolution logic does not hold. What actually differs is that the precheck catches
a real caller first (reporting a caller list) while `skipPrecheck:true` bypasses that catch and falls
through to the always-doomed type-kind exclusion, surfacing the misleading generic message instead.

Fix: `RoslynSentinel.Basic/RefactoringEngine.cs`, `RemoveMemberAsync` (~line 1706). Capture the
unfiltered candidate list before applying the type-kind exclusion; when resolution comes up empty,
check whether any unfiltered candidate was one of the excluded kinds, and if so return a specific
`TargetNotFound` message naming the kind (e.g. "is a type-level declaration (Record), not a class
member... skipPrecheck does not change this... use ReplaceSnippet or ApplyDiff instead"). Regression
test: `RemoveMember_OnNamespaceLevelRecord_SkipPrecheckTrue_ReturnsActionableTypeKindMessage` in
`RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`, reproducing the original two-step
precheck-then-skipPrecheck symptom with a namespace-level record with a real caller.

`ReplaceMemberAsync` has the identical exclusion and the same underlying gap but was left unfixed -
out of this fix's scope since it was not the symptom this doc reported. Flagged as a related,
still-open sibling gap for whoever next touches `Member(replace)`.

This fix does not add a "remove a type-level declaration" capability - it only makes the existing,
by-design restriction fail with an actionable message instead of a misleading generic one. Removing
a type declaration that still has callers still requires `ReplaceSnippet`/`ApplyDiff` directly.

---

Original status before fix (2026-09-29 investigation): OPEN (new, found 2026-09-29 during a
coordinator-directed follow-up on
`blocking_error_movetype_cannot_relocate_namespace_type_across_projects.md`, engine reorg group 6).

## Symptom

While relocating the namespace-level record `CircularDependencyChain` from
`RoslynSentinel.Advanced/ArchitecturalEngine.cs:7` to a new file in `RoslynSentinel.Common` (to
unblock the `ArchitecturalEngine` fold into `SolutionStructureEngine` - see the cross-project-type
blocker above), the two-step relocation is:

1. Write the new `RoslynSentinel.Common/CircularDependencyChain.cs` copy (via
   `WriteFile(operation:CreateFile, validateOnApply:false)`, since a transient CS0104 ambiguity
   between the old and new copy is expected and self-resolves once step 2 runs). Succeeded cleanly.
2. Remove the old copy from `ArchitecturalEngine.cs` via `Member(remove)`.

Step 2 fails. Before the Common copy existed, `Member(remove, memberName:"CircularDependencyChain")`
(no `skipPrecheck`) correctly found the target and precheck-blocked with the expected 4-caller list
(reproduced again below, byte-for-byte identical, after the Common copy was created - the precheck
path is unaffected by the new ambiguity). But `Member(remove, memberName:"CircularDependencyChain",
skipPrecheck:true)` - the exact next step the precheck's own error message tells the caller to take
- fails instead with:

```
Member: no change produced for 'C:\...\ArchitecturalEngine.cs' (TargetNotFound). // Member not found.
```

Reproduced 3 times: once bare (`skipPrecheck:true` only), once with `contextSnippet:"public record
CircularDependencyChain"` added to disambiguate (no change in outcome), and confirmed the *without*
`skipPrecheck` call still works correctly a third time (returns the identical caller-list error as
before the Common file was created) - isolating the defect specifically to the `skipPrecheck:true`
code path's target-resolution logic, not to `Member(remove)`'s resolution in general and not to
`contextSnippet` handling.

`GetFileOutline` re-confirmed the record is still present, unchanged, at
`ArchitecturalEngine.cs:7` throughout all 3 attempts - this is not a case of the target having
already been removed by an earlier partial write.

## Root cause (not yet traced to source - out of scope per standing order)

Not investigated further, per this session's standing order to stop at the first new tool error and
document rather than root-cause or fix it myself. Hypothesis only, clearly labeled as such: the
precheck path (which lists callers) and the `skipPrecheck:true` path (which skips that check and
goes straight to locating-and-removing the target) appear to use different symbol-resolution logic
internally - the precheck path successfully resolves the file-scoped `ArchitecturalEngine.cs`
declaration by name even with a duplicate now existing elsewhere in the solution, but the
`skipPrecheck:true` path's resolution (possibly a solution-wide `Search(mode:symbol)`-style lookup
that requires a unique name match, unlike the precheck path's presumably file-scoped syntax lookup)
fails closed as soon as the name stops being solution-wide-unique, even when a `filepath` is already
given and a `contextSnippet` further disambiguates. This is a hypothesis - the actual branch has not
been read.

**CORRECTION (2026-09-29, traced to source):** this hypothesis was wrong. Both the precheck and
`skipPrecheck:true` paths call the same `RemoveMemberAsync`, which resolves via the same
`SymbolNavigationEngine.ResolveCandidates` regardless of solution-wide name uniqueness - the ambiguity
between the old `Advanced`-namespace record and the new `Common`-namespace record was never actually
the cause. The real cause is `RemoveMemberAsync`'s type-kind exclusion filter (see the FIXED status
banner at the top of this doc for the full explanation and the fix). The symptom reproduced
identically with or without a duplicate name present, which this investigation did not test at the
time - the duplicate-name theory was a plausible-looking coincidence, not the actual mechanism.

## What this blocks

The `Common`-relocation approach the coordinator suggested for unblocking `ArchitecturalEngine`'s
fold (see `blocking_error_movetype_cannot_relocate_namespace_type_across_projects.md`) is itself
now blocked at its final step: the new Common copy exists and compiles (confirmed via the earlier
`CreateFile` attempt's own CS0104 detection, which correctly identified both copies as valid,
compilable declarations), but the old Advanced copy cannot be removed via `Member(remove)` with
`skipPrecheck:true` once such a duplicate exists - which is unavoidable, since removing the old copy
*before* creating the new one is impossible (4 real callers still need it) and creating the new one
first is the only correct order (confirmed necessary and sufficient by `CreateFile`'s own error
message suggesting exactly this two-step sequence).

Current repo state: `RoslynSentinel.Common/CircularDependencyChain.cs` exists (new, untracked,
compiles standalone). `RoslynSentinel.Advanced/ArchitecturalEngine.cs` is UNCHANGED - the old
namespace-level record is still present at line 7, not removed, not modified. This means the
solution right now has two `CircularDependencyChain` records in different namespaces, engineered so
that every existing call site would resolve to the OLD (`Advanced`) one via normal C# unqualified
name binding (nearest/only fully-in-scope-and-unambiguous rule does not apply here since both are
brought into scope by global usings - this needs verification via `Build`/`GetDiagnostics` before
any further action, not yet done this session since this doc is filed the moment the removal itself
failed, per the standing order to stop immediately). This has NOT been built/tested since it is a
genuinely incomplete, in-progress relocation; do not treat the working tree as either "reverted" or
"complete" until whoever picks this back up finishes the removal by some other means.

## What would fix this (environment-side, not attempted - out of scope this session)

- `Member(remove, skipPrecheck:true)` should resolve the target using the same file-scoped lookup
  the precheck path already uses successfully, rather than (hypothesized) a solution-wide
  unique-name lookup that breaks under a legitimate, expected transient duplicate.
- Failing that, the error message should name the ambiguity explicitly (e.g. "found 2 declarations
  named 'CircularDependencyChain' across the solution; pass a fully-qualified name or narrow via X")
  rather than reporting `TargetNotFound`, which reads as "no such member exists in this file at
  all" - actively misleading here, since the member visibly exists in the same file per
  `GetFileOutline` run immediately after the failure.
- A supported single-call "replace this type's namespace" or "move type to a different project"
  operation (the same gap already flagged in the sibling blocker doc) would avoid this whole
  two-step create-then-remove dance and its transient-duplicate window entirely.

## Self-inflicted bypass disclosed (not a blocker, per CLAUDE.md's recovery clause)

Used the raw `Grep` tool (not the MCP `Search` tool) once, on `IntegrationTwentyNineTests.cs`,
checking its leading `using`/`namespace` lines while investigating whether the type could resolve
across namespaces cleanly. Caught immediately; switched to `GetFileOutline` (which does not surface
using-directives) and then `Search(mode:text)` for the same fact, which worked correctly. No
residual tool-side anomaly resulted - all subsequent MCP calls behaved normally given their own
inputs. Reported here per the recovery clause; not repeated; not treated as this blocker's cause
(this doc is for the `Member(remove, skipPrecheck:true)` defect above, which reproduced identically
regardless of that unrelated, already-corrected bypass).

## Related

- `docs/current/blockers/blocking_error_movetype_cannot_relocate_namespace_type_across_projects.md` -
  the original blocker this session's fix attempt was trying to resolve; still open, now compounded
  by this new defect in the manual-relocation workaround the coordinator suggested.
- `RoslynSentinel.Common/CircularDependencyChain.cs` - new file, created this session, left in place
  (compiles standalone; do not delete without understanding the current dual-declaration state
  first).
- `RoslynSentinel.Advanced/ArchitecturalEngine.cs:7` - old declaration, NOT removed, left in place.
- `RoslynSentinel.Tests.Integration/IntegrationTwentyNineTests.cs:612` - one of the 4 callers,
  confirms global-usings-mediated unqualified resolution (`global using RoslynSentinel.Advanced;`
  and `global using RoslynSentinel.Common;` both present per that project's generated
  `GlobalUsings.g.cs`).

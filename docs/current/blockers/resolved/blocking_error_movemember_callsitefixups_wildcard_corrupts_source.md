# RESOLVED: `MoveMember`'s wildcard `callSiteFixups` ("*") rewrites unrelated occurrences inside the source class itself

**Status:** RESOLVED (uncommitted)

**Resolution:** Fixed in `MemberRefactoringEngine.MoveInstanceMembersAsync` (lines 3907-3909): wildcard `callSiteFixups` no longer overwrite valid receivers already assigned to a line. When multiple moved-member calls appear on the same line with mixed Valid/Unresolved statuses, the Valid receiver is preserved and not replaced by the wildcard.

---

# `MoveMember`'s wildcard `callSiteFixups` ("*") rewrites unrelated occurrences inside the source class itself

**Status:** OPEN, confirmed. Distinct from, and not fixed by, the now-resolved nested-class/nested-
record dependency lookup defect (`docs/obsolete/blockers/blocking_error_movemember_nested_class_dependency_not_moved.md`).
No disk corruption resulted - every reproduction below was caught at the dry-run/validation stage
and nothing was written to disk (confirmed via `Git status` showing zero diff to
`AsyncOptimizationEngine.cs` after every attempt).

## What was being attempted

Engine-reorg group 1 (async family), step 2: carving migration-candidate-scoring and
CT-propagation methods out of `RoslynSentinel.Advanced/AsyncOptimizationEngine.cs` into a new
`AsyncMigrationEngine`, per `.claude/plans/enumerated-jumping-babbage.md`'s group 1 item 2. This
required moving 23 members (migration-scoring bucket + CT-propagation bucket) plus 2 shared helper
methods (`IsEventHandlerSignature`, `ReplaceOrAddAttribute`) and the `_workspaceManager` field, all
identified via the tool's own proactive dependency guardrail (see below) as required for the move to
be self-consistent.

## Reproduction

1. Full-batch dry run of all 23+2 members plus `_workspaceManager`, target class
   `AsyncMigrationEngine` (does not exist yet), no `callSiteFixups`: failed with
   `errorCode:"UnresolvedCallSites"`, listing 58 call sites across `ScanTools.cs`,
   `AsyncBatchEngine.cs`, `AsyncifyTools.cs`, and `RoslynSentinel.Tests.Asyncify/FlagMigrationCandidateTests.cs`
   that call the moving methods on fields typed as `AsyncOptimizationEngine` (`_asyncOptimizationEngine`,
   `_engine`, `_asyncEngine`) - expected, since a brand-new target type can't be inferred
   automatically.
2. Retried with `callSiteFixups: {"*": "new AsyncMigrationEngine(_workspaceManager)"}` to blanket-fix
   every unresolved call site by constructing a fresh `AsyncMigrationEngine` inline at each site.
   Result: `ValidationFailed` with 55 cascading compiler errors (`CS0103`, `CS1503`, `CS1729`,
   `CS8130`, `CS1061`). The tool's own error message explicitly flagged the cause: **"55 error(s)
   are on lines rewritten by your callSiteFixups value(s) 'new AsyncMigrationEngine(_workspaceManager)'
   - the fixup expression itself is likely the cause."**

## Root cause

The wildcard key `"*"` in `callSiteFixups` is documented/expected to apply its replacement only to
genuine unresolved call sites of the *moved* members (i.e., external callers that need to be
repointed at the new target class). Instead, the fixup was applied as a blind textual/structural
substitution wherever the matched pattern occurs, **including inside the source class
(`AsyncOptimizationEngine.cs`) being edited, on code that has nothing to do with any moved member**.
Specifically, plain `_workspaceManager.SomeMethodAsync(...)` calls inside "stays" methods (mutation
methods explicitly not part of this move, e.g. methods in the ValueTask/awaits/ConfigureAwait/
async-bridge bucket that the plan says remain in `AsyncOptimizationEngine`) got rewritten to `new
AsyncMigrationEngine(_workspaceManager).SomeMethodAsync(...)` even though those methods never called
any of the 23 members being moved. This produced the cascade: `_workspaceManager` no longer resolved
in its original context (`CS0103`), the synthesized constructor call didn't match
`AsyncMigrationEngine`'s actual (nonexistent at time of validation) shape (`CS1729`), and downstream
type-inference/deconstruction assumptions broke (`CS8130`, `CS1061`, `CS1503`).

**Root Cause Analysis:**
The issue occurred when a line had multiple calls to moved members with mixed Valid/Unresolved statuses:
1. Line 100 contains: `a.MovedMember1();` (Valid) and `this.MovedMember2();` (Unresolved)
2. When processing rows, the Valid row adds `resolvedReceivers[(filePath, 100)] = "validFix"`
3. The Unresolved row then overwrites it: `resolvedReceivers[(filePath, 100)] = "wildcard_fixup"`
4. Both calls on line 100 then use the wildcard fixup, not their correct individual fixes
5. If the valid call uses a receiver like `_workspaceManager`, it gets replaced incorrectly with the wildcard

This means `callSiteFixups`'s wildcard scope is broader than "unresolved call sites of the members
being moved" - it appears to match and rewrite by pattern across the entire file(s) touched by the
operation, not scoped to actual call-site references identified by the `UnresolvedCallSites`
analysis pass that produced the original error list.

## Why this blocks

This is a **new, distinct tool defect**, separate from the nested-type lookup issue. Even with that
issue fixed (confirmed working this session - see below), this wildcard-fixup behavior independently
blocks any multi-member move to a brand-new target class where a shared field/dependency needs a
wildcard-style fixup, because there is no way to construct a fixup value that resolves the target
class's constructor without also being blindly applied to unrelated occurrences of the same pattern
inside the source file.

## Confirmation the nested-type lookup fix is separate and does work

Per a mid-task coordinator update, the MCP server was restarted and the solution reloaded to pick up
a fix for `docs/obsolete/blockers/blocking_error_movemember_nested_class_dependency_not_moved.md`
(nested `class`/`record` declarations previously returned immediate `NotFound`). Retried in
isolation: a dry-run move of `FlagMigrationCandidateEngineResult` (a nested record inside
`AsyncOptimizationEngine`) was correctly **found** this time and subjected to normal dependency
validation (failed only because of genuine remaining references from not-yet-moved methods and
`FlagMigrationCandidateTests.cs` - a correct rejection, not a `NotFound`). This confirms that fix
landed and works. It is what allowed progress far enough to discover the wildcard-fixup defect above
as the next blocking issue.

## What unblocks it

- Scope `callSiteFixups`'s wildcard (`"*"`) matching to only the specific call sites enumerated by
  the tool's own `UnresolvedCallSites` analysis (the same list it reports in the
  `errorCode:"UnresolvedCallSites"` payload), not a broader textual/structural pattern match across
  the whole file.
- Alternatively/additionally: before applying any `callSiteFixups` value, re-run the same dependency
  scan used for `RequireNoUnmovedDependencies` against the *rewritten* result, so a fixup that
  accidentally touches unrelated code is caught before proposing the change, not after producing a
  cascade of unrelated compiler errors.
- Until fixed, avoid wildcard `callSiteFixups` when the fixup expression contains an identifier
  (like `_workspaceManager` here) that also appears legitimately elsewhere in the source class being
  edited; prefer per-`"FilePath:Line"`-keyed fixups instead, even though that requires knowing the
  exact call sites up front (defeating some of the wildcard's convenience).

## Deferred as a result

The `AsyncMigrationEngine` carve-out (group 1, item 2 of the plan) remains deferred this session.
Two compounding, now fully identified reasons:
1. The wildcard-fixup defect above.
2. Four members (`IsEventHandlerSignature`, `ReplaceOrAddAttribute`,
   `MigrationCandidateShortName`, `MigrationCandidateFullName`) are needed by both the "stays"
   method `ConvertToAsyncBridgeAsync` (explicitly required by the plan to remain in
   `AsyncOptimizationEngine` - confirmed via `Search(mode:"symbol")` it is still at
   `AsyncOptimizationEngine.cs:389`, never moved) and the "moves" bucket, and `MoveMember` has no
   mechanism to duplicate a shared private helper across two classes - only to move-together or
   leave-behind, and leaving behind breaks `RequireNoUnmovedDependencies` once the mover set
   includes the members that need them.

## Related

- `docs/obsolete/blockers/blocking_error_movemember_nested_class_dependency_not_moved.md` - the
  separate, now-fixed defect this doc's reproduction steps had to work around first.
- `project_engine_reorg_group1_async_family.md` (memory) - full session summary for this task.
- `.claude/plans/enumerated-jumping-babbage.md` - group 1 ("Async family") plan text.

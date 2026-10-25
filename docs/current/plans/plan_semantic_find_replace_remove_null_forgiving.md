# Plan: SemanticFindReplace operation removeNullForgiving, then strip the 235 redundant UpdatedText! in tests

**Status:** IN PROGRESS 2026-10-25. Steps 1-5 done in `1934c0f3`; step 6 applied in `600251b0` to 46 files (205 sites); the 36 sites in `MemberRefactoringTests.cs` and step 7 remain (TODO.md).

## Problem
`DocumentEditResult.UpdatedText` is now a non-nullable `string` (`RoslynSentinel.Common/DocumentEditResult.cs:21`), so the
235 `UpdatedText!` uses in 47 test files are redundant (`Search` text mode: 235 matches across 47 files; largest
`BatteryThirtyTests.cs` 42, `MemberRefactoringTests.cs` 36, `CodeGenerationTests.cs` 20). No tool removes a
null-forgiving `!`: `SemanticFindReplace` has only `invertBoolean` and `replaceConstruction`, and a hand or regex edit
across 47 files is the brute force CLAUDE.md forbids.

## Decision
Add a third `SemanticFindReplace` operation, `removeNullForgiving`: given a property or field (docCommentId), delete the
postfix `!` from every expression whose operand binds to that member, as one atomic multi-file change through the
existing compile gate (`ValidateAndApplyHelper`). Then use it to do the 235 removals.

Design points (all from reading `SemanticReplaceEngine.cs`, `ConstructionReplacer.cs`, `SemanticFindReplaceTools.cs`):
- New static class `NullForgivingRemover` in `RoslynSentinel.Engines.Basic`, modelled on `ConstructionReplacer.PlanAsync`
  (walks `project.Documents`, semantic model per document, returns `SemanticReplaceOutcome`). Not on the
  `FindReferences` path of invertBoolean, which is solution-wide only.
- The edit deletes only the `!` operator token span. This keeps nested forms (`x.P!.Y!`, `a!!`) free of overlapping
  edits, and preserves surrounding trivia and line endings (`EolChangeGuard`).
- Site match: a `PostfixUnaryExpression` of kind `SuppressNullableWarningExpression` whose operand, after stripping
  parentheses and nested `!`, binds to the target (identifier, `x.Prop`, `this.Prop`, `x?.Prop` via the binding name).
- Refuse when the target's type is nullable-annotated (`NullableAnnotation.Annotated`): there `!` is not redundant.
  Refuse a target that is not a property or field.
- Scope: `projectFilter` (exists, currently replaceConstruction-only; re-word) plus a new optional `fileFilter` glob on
  the solution-relative path. `fileFilter` is how the run avoids another session's dirty file.
- Result: reuse `SemanticReplaceSite` with a new `SemanticReplaceRole.NullForgiving`; the tool's apply path follows the
  replaceConstruction shape, including the zero-site early return (otherwise no `changeId`).
- The tool's `isConstruction` boolean (`SemanticFindReplaceTools.cs:74`) becomes a switch over the three operations.
- `SemanticFindReplace` is on-demand (`moveExtract` toolset), so the claude-lean schema budget test is unaffected.

## Execution rules
- Implementation goes to subagents, sequentially, one `Build`/`RunTest` at a time. `SemanticFindReplaceTools.cs` has
  mixed line endings: edit it with tools that preserve them, and let `EolChangeGuard` be the check.
- Other sessions' dirty files must not be edited or committed: `RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs`
  (36 of the 235 sites), `LargeResultHints.cs`, `ToolCallHint.cs`, `LargeResultHintsTests.cs`,
  `ListAllIncludeTestsTests.cs`, `ToolCallHintTests.cs`, and two docs. Re-check `Git status` before each step.
- The running server is stale until `Build(level: fullBuild)` + restart; the new operation is not callable before step 5.

## Steps

### Step 1 - Engine
- Files: `RoslynSentinel.Common/ToolEnums.cs` (add `removeNullForgiving` to `SemanticReplaceOperation`),
  `RoslynSentinel.Engines.Basic/SemanticReplaceTypes.cs` (add `SemanticReplaceRole.NullForgiving`),
  new `RoslynSentinel.Engines.Basic/NullForgivingRemover.cs`, `RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs`
  (one-line delegating `RemoveNullForgivingAsync`, like `ReplaceConstructionAsync`).
- Done when: Build 0 errors.

### Step 2 - Tool wiring
- Files: `RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs`.
- Change: switch dispatch; validation (symbol required; `newName` and `factory` not allowed for the new operation);
  `fileFilter` parameter; zero-site apply early return; update the "two operations" description and the "Ignored by"
  parameter wording.
- Done when: Build 0 errors.

### Step 3 - Tests
- Files: new `RoslynSentinel.Tests.Basic/NullForgivingRemoverTests.cs` (engine, `AdhocWorkspace` style of
  `SemanticReplaceEngineTests`), new `RoslynSentinel.Tests.Tools.Basic/SemanticFindReplaceNullForgivingTests.cs`
  (tool, style of `SemanticFindReplaceConstructionTests`).
- Cases: `x.P!` and bare `P!`, `this.P!`, `x?.P!`, nested `x.P!.Y!`, parenthesised, other member with the same name left
  alone, nullable-annotated target refused, non-member target refused, `projectFilter`/`fileFilter` scoping, multi-file
  atomic apply with a `changeId`, preview writes nothing, zero sites on apply.
- Done when: both fixtures pass (check `totalCount`; a zero-match filter reports success).

### Step 4 - Generated docs and freshness
- Files: `docs/generated/architecture_tools.md` (regenerate; the tool description is clipped to 120 chars at
  `ArchitectureDocGenerator.cs:182`, and `ArchitectureDocFreshnessTests` compares it).
- Done when: `ArchitectureDocFreshness` passes.

### Step 5 - Make the operation callable
- `Build(level: fullBuild)`, commit steps 1-4, restart this session's server, `LoadSolution`,
  `McpToolsetControl(toolSet: moveExtract, enabled: true)`. Smoke check: `preview` on
  `P:RoslynSentinel.Common.DocumentEditResult.UpdatedText` returns about 235 sites.

### Step 6 - Apply to the test projects
- Per test project, sequentially: `SemanticFindReplace(operation: removeNullForgiving, symbol:
  "P:RoslynSentinel.Common.DocumentEditResult.UpdatedText", mode: preview)` then `apply` with `projectFilter`; for
  `RoslynSentinel.Tests.Basic` use `fileFilter` per file so `MemberRefactoringTests.cs` is skipped. Build after each project.
- Done when: `Search` text `UpdatedText!` shows only the 36 sites in `MemberRefactoringTests.cs`; full Build 0 errors;
  the affected test projects pass with the same counts as before.

### Step 7 - Bookkeeping
- Move the TODO.md TOOL-GAP entry to CLOSED.md; set this plan's Status; move it to `plans/closed/`; trailers
  `Plan: plan_semantic_find_replace_remove_null_forgiving`, final commit `Closes:`.

## Out of scope
- The 36 sites in `MemberRefactoringTests.cs` (another session's dirty file): re-run step 6 for that file once it is
  committed.
- The meaningless `Is.Not.Null` asserts on `UpdatedText`: a different edit shape; count them after step 6 and decide.
- A general `removeNullForgiving` for arbitrary expressions or null-forgiving on method returns.

## Risks and open decisions
- Operand binding for `x?.P!` (conditional access wraps a member-binding) is untested; step 3 pins it.
- `ReferenceRoleClassifier` handling of a postfix `!` parent was not read; the new operation does not use it, so this
  should not matter (hypothesis).
- Removing `!` is only a no-op when the target type is non-nullable; the refusal rule covers the target, not the
  surrounding flow analysis. The compile gate catches new errors but not new warnings unless warnings are errors.
- Any mismatch between the claimed 235 sites and the preview count blocks step 6 until explained.

# Structural refactoring engine defects left open (MoveTypeToOuterScope, IntroduceParameterObject, MoveType) - OPEN

**Status:** OPEN 2026-10-17. Four confirmed defects in the structural engine and MoveType tool; none fixed (re-verified against source 2026-10-17).

## Summary
Entries below were moved verbatim from the restructured TODO.md. Source re-check on 2026-10-17: `AdvancedStructuralEngine.MoveTypeToOuterScopeAsync` (:961) still has no accessibility widening and the `_ => currentContainer` arm; `IntroduceParameterObjectAsync` (:1120-1304) still has `todoComment` (:1209), `EmptyStatement` (:1222) and the "// Local variable introduced." message (:1301); `MoveType` (`AdvancedRefactoringTools.cs:1558`) still has no target-folder parameter.

## Moved from TODO.md (2026-10-17)

### `MoveTypeToOuterScopeAsync` does not adjust accessibility (2026-10-17) - not started

A `private`/`protected` nested type moved to namespace scope becomes invalid C# (CS1527); the engine does not widen accessibility (tests use `public`). Decide: widen to `internal`, or reject with an actionable message. Also unreachable `_ => currentContainer` / `TargetNotFound` arm (~:1034-1048) and test gaps (nested-in-nested, attributes/docs/#region, duplicate names).

### `IntroduceParameterObjectAsync` drops its TODO comment, hides the class path, wrong success message (2026-10-17) - not started

Found in the C5b-2a review (all pre-existing; the formatter routing itself is done in 1069b161). (1) The "TODO: Update call sites" comment is attached to an `EmptyStatement` built with `WithSemicolonToken(MissingToken)`, which drops the leading trivia, so it never reaches the output (`todoComment` is dead code); `IntroduceParameterObject_IncludesTodoComment` never asserts the comment. (2) The method is found by name with `FirstOrDefault`, so an interface declared before its implementing class is picked and the class implementation is left untouched; the warning branch is then unreachable. (3) The main-path success message reads "// Local variable introduced." Fix all three with byte-exact tests.

### `MoveType` cannot lift a nested type to top level (2026-10-14) - not started

`MoveType(typeName: SnippetSpan, destination: ownFile)` on a nested type (`ContextHelper.SnippetSpan`) moves the declaration but leaves the qualified `ContextHelper.SnippetSpan` references, so the compile gate rejects it with 13 `CS0426`. Suggested change: when the moved type was nested, rewrite `Outer.Nested` references solution-wide to the new top-level name in the same atomic change. Worked around in slice 5f-D4 with migration-first: add the top-level type beside the nested one, retarget references, then remove the nested one.

### `MoveType(destination: ownFile)` has no target-folder parameter (2026-10-14) - not started

Found moving `SnippetMatchOutcome` out of `ContextErrorBuilder.cs` (commit d9a2ff90). `MoveType` can only create the new file beside the source file, so a type cannot be moved into a subfolder such as `SnippetMatching/`. It also adds an unused `using Microsoft.CodeAnalysis.Text;` to the new file and drops the type-level doc summary. Suggested change: add an optional `targetFolder` (or `targetPath`) parameter, copy the source file's usings only where the moved type needs them, and carry the type's doc comment across. Workaround used: accept the source folder and fix the using and summary with one `ReplaceSnippet`.

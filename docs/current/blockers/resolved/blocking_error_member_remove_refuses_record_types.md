# `Member(operation: remove)` refuses record (and other type-level) declarations

**Status:** RESOLVED 2026-10-14. See "Resolution" at the end. Original text: root cause traced to source: type-level kinds are excluded from the remove path by design, and the refusal message does not point at the tool that can remove an unreferenced type.

## What was being attempted
Slice 5f-D4b (2026-10-13): delete the now-unreferenced nested record `ContextHelper.SnippetSpan`, using `Member(operation: remove, memberName: SnippetSpan)`.

## The exact symptom
`Member(remove)` on a record returned `TargetNotFound` with the engine message below, even though `SnippetSpan` exists and had zero callers after the migration:

```
// 'SnippetSpan' is a type-level declaration (Record), not a class member. Member(remove) only removes class-level members (methods, properties, fields, constructors, events, indexers) -- it does not support removing type declarations (classes, interfaces, structs, records, enums, or enum members), and skipPrecheck does not change this. There is currently no tool for removing a type declaration that still has callers; use ReplaceSnippet or ApplyDiff to edit the file's raw text instead.
```

(Message text copied from the source below; the 2026-10-13 session recorded the outcome as `TargetNotFound` on a record.)

## Source trace
- `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` `RemoveMemberAsync` (~lines 3164-3227): `memberCandidates` filters out `CandidateKind.Class/Interface/Struct/Record/Enum/EnumMember` ("by design", comment at ~3178). When nothing resolves, it re-reads the excluded kinds and returns the `TargetNotFound` message above.
- The wording is stale: it says there is "no tool" for removing a type declaration. `SafeDeleteUnusedSymbol` does remove an unreferenced type (used successfully for D4b). The message only mentions the caller-bearing case and never names it.
- Related, already resolved: `blockers/resolved/blocking_error_member_remove_skipprecheck_targetnotfound_ambiguous_symbol.md` added the explicit message; it did not add a type-removal capability.

## What is and is not confirmed
- Confirmed (source): the exclusion and the message text.
- Confirmed (session 2026-10-13): `SafeDeleteUnusedSymbol` removed the record; `Member(remove)` did not.
- Not re-run today: no live repro was made, to avoid editing source.
- Related gotcha (confirmed 2026-10-13): the `Member(remove)` precheck counts doc-comment `cref`s as callers, so even for members the removal can be refused until crefs are rewritten.

## Why this matters
A weak model told "no tool exists" will fall back to raw-text edits (`ReplaceSnippet`/`ApplyDiff`), which is the mission's named failure mode. The environment knows a better route and does not say so.

## Suggested direction (not implemented)
1. Cheapest: change the refusal message to name `SafeDeleteUnusedSymbol` for unreferenced type declarations and `MoveType`/`ReplaceSnippet` only for the referenced case.
2. Or route type-level kinds from `Member(remove)` to the `SafeDeleteUnusedSymbol` engine and return its result.
3. Add a regression test asserting the message names the right tool.

## Resolution (2026-10-14)
- **Root cause:** `MemberRefactoringEngine.RemoveMemberAsync` excluded Class/Interface/Struct/Record/Enum from `memberCandidates` by design and its refusal message claimed no tool exists for type removal.
- **Fix, step 1 (commit a06ccf16):** the refusal message names `SafeDeleteUnusedSymbol`.
- **Fix, step 2 (routing, the commit that moves this doc):** when the name resolves only to type-level kinds, `RemoveMemberAsync` resolves the declaration and returns `StructuralRefinementEngine.SafeDeleteSymbolAsync(filePath, symbol, ct)` unchanged. An unreferenced type is removed; a referenced type gets the safe-delete refusal ("Symbol 'X' has N usages and cannot be safely deleted."). EnumMember-only names keep the old "type-level declaration" TargetNotFound message.
- **Tests (`BatteryTwentyFourTests`):** `RemoveMember_OnUnreferencedRecord_RemovesRecord`, `RemoveMember_OnUnreferencedInterface_RemovesInterface`, `RemoveMember_OnNamespaceLevelRecord_SkipPrecheckTrue_ReferencedRecord_ReturnsSafeDeleteRefusal`. Tools.Advanced Member/BatteryTwentyFour filter: 114/114 pass. Tests.Basic MemberRefactoring/RemoveMember/SafeDelete filter: 132/132 pass.
- **Known limits:** a class with an explicit constructor resolves to the constructor in `Member(remove)` and never reaches the type path; safe delete also refuses on reflection-risk string literals and when its feature flag is off; it edits one file only (no file deletion, no using/namespace cleanup).

# `Member(operation: remove)` refuses record (and other type-level) declarations

**Status:** OPEN 2026-10-14. Root cause traced to source: type-level kinds are excluded from the remove path by design, and the refusal message does not point at the tool that can remove an unreferenced type.

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

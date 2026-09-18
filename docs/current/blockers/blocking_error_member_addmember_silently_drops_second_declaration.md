# `Member(addMember)` silently drops the second of two top-level declarations in `newMemberSource`, then blames a misleading CS0103 on the retained one

**Status:** OPEN. Root cause traced to source: same `ParseMemberDeclaration` single-declaration
truncation already documented for other `Member` call paths, but this is a distinct, previously
undocumented symptom of it - a misleading compiler error rather than a silent false-success. See
"Relationship to existing docs" below before conflating the two.

## What was being attempted

While implementing Decision 3 of `docs/current/plans/plan_scoped_operation_ledger.md`, a single
`Member(operation: addMember, containerName: "AdvancedStructuralEngine", ...)` call was made against
`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, with a `newMemberSource` string containing two
top-level declarations back-to-back, intended to land as two independent sibling members:

1. A public async method `PreviewInstanceMoveCallSitesAsync(...)` (~150 lines) whose body calls a
   private static helper, `GetSymbolType(ISymbol symbol)`.
2. `GetSymbolType` itself, immediately following the method in the same `newMemberSource` string.

## The exact result reported

```
Member: the change was valid and matched its target(s), but introduces new compiler errors - change not applied. Fix the issue(s) below and retry:
CS0103 at ...AdvancedStructuralEngine.cs:972: The name 'GetSymbolType' does not exist in the current context
  No symbol named 'GetSymbolType' was found anywhere in the solution. This is likely a typo, or the member genuinely doesn't exist yet and needs to be added.
```

`GetSymbolType` was present, verbatim, later in the exact same `newMemberSource` string submitted in
that same call. The identical two-declaration payload was retried byte-for-byte a second time to rule
out a one-off transcription slip, and produced the exact same CS0103 at the exact same line - ruling
out a fluke.

## Where it happened

- Tool: `Member`, operation `addMember`.
- Target: `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, container `AdvancedStructuralEngine`.
- Reported error location: `AdvancedStructuralEngine.cs:972`, inside the body of
  `PreviewInstanceMoveCallSitesAsync` (confirmed present on disk at that line post-split, in the `if
  (!accessible)` accessibility-check block that calls `GetSymbolType`).

## How the root cause was isolated

The single two-declaration call was split into two sequential `Member(addMember)` calls:

- Call A: `newMemberSource` containing only the `GetSymbolType` helper. Succeeded immediately
  (changeId `315ac0a8`), written to disk, confirmed via the tool's own `changedContent` echo.
- Call B (submitted after A succeeded): `newMemberSource` containing only
  `PreviewInstanceMoveCallSitesAsync` (now able to resolve `GetSymbolType`, already on disk from call
  A). Also succeeded (changeId `54365e89`).

Both pieces are now correctly on disk as two separate members. The two-declaration single-call
version never worked across two identical attempts; the same content split into two separate calls
worked on the first try each time. This rules out the content itself being invalid - the failure is
specific to submitting both declarations in one `newMemberSource`.

## Root cause - traced to source

`operation: addMember` dispatches through
`RoslynSentinel.Server.Basic/RefactoringStructuralImpl.cs:633`:

```csharp
updated = await _refactoringEngine.AddMemberAsync(filePathResolved, containerName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
```

into `RoslynSentinel.Basic/RefactoringEngine.cs:1237`:

```csharp
var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
```

`SyntaxFactory.ParseMemberDeclaration` is a single-declaration Roslyn API: given a string containing
more than one member declaration, it parses only the *first* one and silently discards everything
after, with no "unconsumed trailing text" diagnostic. The `null` check immediately after
(`RefactoringEngine.cs:1238`) only guards a totally unparseable string; it cannot detect "parsed
successfully but only consumed part of the input," which is exactly this case. This is the same
underlying API and the same missing-guard pattern already traced to source in
`docs/current/blockers/blocking_error_member_add_multi_type_silent_drop.md` for the sibling `add`
call paths (`AddTopLevelTypeAsync`, and this same `AddMemberAsync`, at what that doc's older line
numbers call `RefactoringEngine.cs:1237`/`:1292` - unchanged in this session's read).

What makes tonight's repro a distinct symptom, not just a repeat: only `PreviewInstanceMoveCallSitesAsync`
survived the parse (it was first in the string); `GetSymbolType` (second) was discarded before
`RefactoringStructuralImpl.cs` ever reached its compile-check step
(`ValidateAndApplyAsync`, called at `RefactoringStructuralImpl.cs:665`). Because the surviving method's
body still references the now-absent `GetSymbolType`, the compile-check correctly finds a real
compile error in the truncated document - but its CS0103 message ("The name 'GetSymbolType' does not
exist ... the member genuinely doesn't exist yet and needs to be added") is actively misleading: the
member did exist, in the very payload that produced this error, and was silently dropped one parse
step earlier for reasons invisible to the error message. Unlike the existing docs' repros (which
reported `success: true` with a `changedContent` echo of the full un-applied input, because
`RefactoringStructuralImpl.cs:617`'s `addedMemberSource = newMemberSource` sources `ChangedContent`
from the raw request parameter, not the parsed/applied result), this call correctly failed and nothing
was written to disk - the compile-check step did its job here. The defect is entirely in the
misleading *reason* given for the failure, not in a false success report.

## Relationship to existing docs - do not conflate

- `docs/current/blockers/blocking_error_member_add_multi_type_silent_drop.md` and
  `docs/current/blockers/blocking_error_member_addmember_silent_partial_write.md` both document the
  same `ParseMemberDeclaration` truncation reporting **false success**: the tool returns
  `success: true`, echoes the full multi-declaration input as `changedContent`, and only the first
  declaration is actually on disk - the drop is invisible until an unrelated downstream call fails.
- **This doc is a different symptom of the same underlying truncation**, surfaced when the *first*
  declaration's body depends on the *second* (dropped) one: the compile-check step correctly catches
  the resulting break and reports failure rather than false success, but attributes it to a "missing
  symbol" (CS0103) rather than to the truncation that actually caused it. No silent partial write
  reached disk in this repro; the payload was rejected, correctly, but for a misleading stated reason.

## What unblocks it

Same structural fix already proposed in `blocking_error_member_add_multi_type_silent_drop.md`,
which would also close this symptom: at `RefactoringEngine.cs:1237` (and the other
`ParseMemberDeclaration` call sites enumerated in that doc), after `ParseMemberDeclaration` returns a
non-null node, verify the parsed node's span reaches the end of `newMemberSource` (modulo trailing
whitespace). If it doesn't, reject up front with an explicit error - e.g. "newMemberSource contains 2
top-level declarations; addMember accepts exactly one per call - split into 2 separate calls" -
instead of silently truncating and letting a downstream compile-check produce a CS0103 that names the
wrong problem. This would have surfaced the real issue (multi-declaration input) on the first attempt
instead of requiring a second identical retry and a manual split-and-isolate to find the cause.

## Evidence

- CS0103 reported at `AdvancedStructuralEngine.cs:972` on both identical attempts of the
  two-declaration `newMemberSource`.
- changeId `315ac0a8` - `GetSymbolType` alone, applied successfully.
- changeId `54365e89` - `PreviewInstanceMoveCallSitesAsync` alone (submitted after `315ac0a8`, so
  `GetSymbolType` already resolvable), applied successfully.
- `RefactoringStructuralImpl.cs:633` -> `RefactoringEngine.cs:1237` (`ParseMemberDeclaration`
  call) -> `RefactoringStructuralImpl.cs:665` (`ValidateAndApplyAsync` compile-check) is the traced
  call chain for `operation: addMember`.

## Related

- `docs/current/blockers/blocking_error_member_add_multi_type_silent_drop.md` - same root API
  (`ParseMemberDeclaration`), same missing span-check fix; documents the false-success/silent-drop
  symptom rather than this doc's misleading-CS0103 symptom.
- `docs/current/blockers/blocking_error_member_addmember_silent_partial_write.md` - a third repro of
  the same false-success symptom, at higher multiplicity (4 declarations, 3 dropped); not this bug
  either.

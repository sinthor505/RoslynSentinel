# `Member(remove)` on `ServerBinaryPath` reported "not found" for a member that has real cref usages, blocked by an outcome-swallowing bug distinct from the prior field precheck fix

**Status:** FIXED 2026-09-20, `RoslynSentinel.Basic/RefactoringStructuralImpl.cs`'s `Member` remove
branch now routes through `RefactoringToolHelpers.RequireUpdatedText` instead of a blanket
"not found" string. Not yet committed - see `## Fix applied` for the exact diff and follow-up
required before this is safe to close in git history.

## What was being attempted

`Member(operation: "remove", filepath: "RoslynSentinel.Common/SentinelCallToolResult.cs",
memberName: "ServerBinaryPath", containerName: "SentinelCallToolResult", skipPrecheck: true)`, as
part of the envelope field-promotion plan. Two prior edits to the same file in the same session
(an `addTopLevelType` add and a `replace`) had already succeeded.

## The exact result reported

```json
{"success":false,"error":{"errorCode":"Exception","message":"Member: member 'ServerBinaryPath' not found in '...\\SentinelCallToolResult.cs'."}}
```

Both `Member(operation: "view", ...)` and `GetFileOutline` confirmed the property existed at line
99 with the exact filepath/memberName/containerName passed to the failing `remove` call, in the
same call cycle - ruling out staleness or drift. Two identical `remove` attempts (with and without
`skipPrecheck`) produced the same "not found" text for different underlying reasons (see below).

## Root cause - traced to source, not a hypothesis

**Confirmed via `failure-root-cause-analyst` reading `RefactoringEngine.cs` and
`RefactoringStructuralImpl.cs` directly, not by taking the error text at face value.**

`ServerBinaryPath` (`SentinelCallToolResult.cs:99`) is referenced by two
`<see cref="ServerBinaryPath"/>` doc-comment tags on the sibling `ServerPid` property
(`SentinelCallToolResult.cs:104,106`). `RemoveMemberAsync`
(`RoslynSentinel.Basic/RefactoringEngine.cs:1534-1613`) runs its own internal usage check via
`SymbolFinder.FindReferencesAsync`, which counts `<see cref>` locations as reference locations by
default (standard Roslyn `SymbolFinder` behavior). With `usageCount > 0` from the two crefs, it
correctly returns `EditOutcome.CannotRemove` with a message naming the usages - **the member was
found the entire time; it was blocked for an unrelated, legitimate reason.**

The bug is one level up: `RefactoringStructuralImpl.cs`'s `Member` method, in the `remove` branch
(originally around line 493-495), never inspected `result.Outcome` or `result.Message` - it checked
only `string.IsNullOrEmpty(result.UpdatedText)` and, if empty, unconditionally returned the generic
`"Member: member '{memberName}' not found in '{filePathResolved}'."` regardless of which of
`EditOutcome`'s eight non-`Modified` values actually caused the empty result. The `replace` branch
immediately above it (lines 428-436, still present) already did this correctly, switching on
`result.Outcome` via `RefactoringToolHelpers.ErrorCodeFor`. The `remove` branch was simply never
brought in line with that pattern - `CannotRemove` (cref usages) was relabeled as "not found," which
is false: the member both exists and was located.

This explains the exact repro shape: the *first* `remove` attempt (no `skipPrecheck`) correctly
reported "2 caller(s)" via `FindCallersAsync`/`FindImplementationsForMemberAsync` (which count
crefs too) - the precheck was doing its job. `skipPrecheck: true` only skips that precheck's
reporting block; it does not skip `RemoveMemberAsync`'s own separate, internal `CannotRemove` check,
which then got silently downgraded to "not found" by the bug above.

## Is this the same defect as the prior fix (`e120b68`)? No - confirmed distinct

`e120b68` (see
`blocking_error_member_remove_field_precheck_false_negative_unrecoverablebreakerlock.md`) fixed a
declaration-kind dispatch gap in `SymbolNavigationEngine.cs`'s `FindImplementationsForMemberAsync`,
where `FieldDeclarationSyntax` fell through a `_ => false` default that only `Method`/
`PropertyDeclarationSyntax` were matched against. That fix is scoped entirely to that one method;
it does not touch `RemoveMemberAsync` or `RefactoringStructuralImpl.cs` at all. `ServerBinaryPath`
is a property, and every declaration-kind dispatch table checked during this investigation
(`RefactoringEngine.cs:5297`, `:5573`, `SymbolNavigationEngine.cs:1638`) already handles properties
correctly - `GetDeclaredSymbol` needs no field-style special-casing for a `PropertyDeclarationSyntax`
node. This is a third, independent bug in the same tool family that happened to collapse to the
identical "not found" error text, not a recurrence or a wider blast radius of `e120b68`'s fix.

**Ruled out during investigation:**
- Session/document staleness (the doc's own originally-flagged hypothesis): `RemoveMemberAsync`
  fetches `solution` fresh via `_workspaceManager.GetCurrentSolutionAsync` on every call
  (`RefactoringEngine.cs:1536`) - no snapshot is held across calls.
- `skipPrecheck` routing to a different *existence*-lookup path: it only skips
  `FindCallersAsync`/`FindImplementationsForMemberAsync` (`RefactoringStructuralImpl.cs:458-477`);
  the member-existence lookup inside `RemoveMemberAsync` itself runs identically either way, and it
  succeeded (the member was found, then blocked by `CannotRemove`, not `TargetNotFound`).

## Fix applied

`RoslynSentinel.Basic/RefactoringStructuralImpl.cs`, `Member` method, `remove` branch:

```csharp
// before
var result = await _refactoringEngine.RemoveMemberAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter);
if (string.IsNullOrEmpty(result.UpdatedText))
    return new SentinelCallToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: member '{memberName}' not found in '{filePathResolved}'.") };

// after
var result = await _refactoringEngine.RemoveMemberAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter);
var removeError = RefactoringToolHelpers.RequireUpdatedText(result, "Member", filePathResolved);
if (removeError is not null)
    return removeError;
```

Reused the existing `RefactoringToolHelpers.RequireUpdatedText` helper (already built for exactly
this purpose, in `RefactoringToolHelpers.cs:52-68`, but previously called by nothing - `replace`
independently hand-rolled its own equivalent inline switch instead of calling it) rather than
adding a third bespoke outcome switch. This means `CannotRemove` now surfaces via
`RefactoringToolHelpers.ErrorCodeFor` (falls into the `_ => ToolErrorCode.Exception` default, same
as before, but the message now includes `result.Message` - the real "has N usages" explanation -
instead of a false "not found").

Applied via `ReplaceSnippet`, validated by its delta-compile gate, confirmed by a solution-wide
`Build(level: fullBuild)`: 0 errors, 0 new warnings. **Not runtime-verified against a live
`Member(remove)` call in this session** - the running MCP server process does not rebind to a
freshly-built DLL mid-session (`buildTimeUtc`/`pid` in the tool envelope stayed unchanged after the
rebuild), a known limitation (see memory `feedback_stale_server_before_rebuild`,
`feedback_new_tool_needs_fresh_session`). Confirmed instead that the target file was untouched by
the failed verification attempt (`GetFileOutline` still shows `ServerBinaryPath` at line 99) - no
corruption, just an unverified-this-session fix. **Whoever resumes the envelope field-promotion
plan should re-run the exact repro call after a fresh session/server restart to confirm the new
error text names `CannotRemove` and the cref usages, before trusting this as fully closed.**

## Runtime verification (2026-09-20, follow-up session)

Server restarted via `McpServerControl(op: "stop")` + respawn (new PID, fresh `buildTimeUtc`) +
`LoadSolution`. Retried the exact original repro call:

`Member(remove, filepath: "RoslynSentinel.Common/SentinelCallToolResult.cs", memberName:
"ServerBinaryPath", containerName: "SentinelCallToolResult", skipPrecheck: true)`

Result: the false "not found" is gone. The tool now returns the honest reason predicted above -
`"Member: no change produced ... (CannotRemove). // ERROR: Cannot remove member 'ServerBinaryPath' -
it has 2 usages in the solution."` - exactly the `CannotRemove`/real-usages message the fix was
supposed to surface, confirming `RequireUpdatedText` is correctly wired into the `remove` branch and
the fix reached the running binary. Removed `ServerPid` (the source of the 2 cref usages, itself
already queued for deletion in the plan) first, then retried `ServerBinaryPath` removal with no
`skipPrecheck` override - succeeded cleanly (`changeId: "0946e69c"`). Fix confirmed fully closed.

## Why this was worth a writeup even though the fix is small

Per `CLAUDE.md`'s failure doctrine and root-cause discipline: the surface-level restatement ("remove
said not found, but view found it, so remove must be broken") would have been the same restatement
that already happened twice before, both against DIFFERENT bugs. This is the third recurrence of an
identical *symptom string* produced by three different bugs across two different files - the pattern
itself was the signal that this class of failure needed a shared-cause investigation rather than a
fourth isolated patch. That larger question - whether `Member`'s several member-lookup/outcome
paths should be unified rather than patched one at a time - is deliberately not resolved by this
fix; see `docs/current/proposal_unify_member_lookup_paths.md`.

## Related

- `blocking_error_member_remove_field_precheck_false_negative_unrecoverablebreakerlock.md` -
  same tool, same "remove says not found, view disagrees" symptom shape, confirmed different root
  cause (field-declaration-kind dispatch gap in a different method/file).
- `docs/current/proposal_unify_member_lookup_paths.md` - the unification proposal raised by this
  being the third recurrence of the same symptom family from three distinct causes.
- `feedback_verify_before_theorizing_on_tool_errors` (memory) - the practice this investigation
  followed: traced the literal `CannotRemove`/`FindReferencesAsync` mechanism rather than accepting
  "not found" or the doc's own originally-flagged staleness hypothesis at face value.
- Resume point: envelope field-promotion plan
  (`C:\Users\Administrator\.claude\plans\inherited-orbiting-toucan.md`) - remove `ServerBinaryPath`
  and `ServerPid` (updating/removing the crefs on `ServerPid` at the same time, since they're the
  actual blocking usage), then continue with the `Success`/`Error`/`Data`/`Warning` renames and
  `DirectiveKind` deletion. Also still open: the cosmetic `-&gt;` HTML-entity-escaping defect noted
  in the original blocker (in the `ServerInfo` doc comment) - queue for the same pass.

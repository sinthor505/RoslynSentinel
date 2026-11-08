# `Member(remove)` falsely reported `_unrecoverableBreakerLock` as "not found" via a field-blind `FindImplementations` precheck

**Status:** FIXED 2026-09-19, commit `e120b68` ("Fix FindImplementationsForMemberAsync field-blind
decl lookup"). Documented here as the concrete repro that drove the fix, per this repo's
dog-fooding/failure-doctrine policy that a tool defect is a blocking finding worth a writeup even
once resolved.

## What was being attempted

`Member(operation: "remove", memberName: "_unrecoverableBreakerLock", containerName: "UnrecoverableCircuitBreaker", ...)`,
removing a private field that had just been added successfully in a prior call in the same session
(confirmed by that prior call's own success response).

## The exact result reported

Rejected with an error attributing the failure to a `FindImplementations` lookup - i.e. the tool's
own stated reasoning was that it went looking for implementations/overrides of the named symbol (a
check that makes sense for a method or property, not a plain private field) and, on that lookup
failing to resolve the symbol at all, reported the field itself as "not found declared" rather than
reporting the real problem (the lookup path couldn't handle a field).

## Verification performed before concluding this was a real defect

`GetFileOutline` on the target file was called immediately after the rejection and confirmed the
field genuinely exists, declared at line 6:

```json
{"kind":"field","name":"_unrecoverableBreakerLock","container":"UnrecoverableCircuitBreaker","startLine":6,"endLine":6}
```

This ruled out the field simply not existing (e.g. the earlier "add" call having silently failed) -
the file on disk and the tool's own outline agreed the field was there; only `Member(remove)`'s own
precheck disagreed.

## Root cause - traced to source, not a hypothesis

Confirmed in `RoslynSentinel.Basic/SymbolNavigationEngine.cs`, `FindImplementationsForMemberAsync`.
`Member(remove)`'s precheck calls this method to check for implementations before allowing a
removal. Its filePath-scoped, no-`contextSnippet` declaration-lookup branch only matched
`MethodDeclarationSyntax`/`PropertyDeclarationSyntax` in its dispatch switch - any other declaration
kind, including `FieldDeclarationSyntax`, fell through to a default `_ => false` case. This meant
the method could never even resolve a field's declared symbol in the first place, regardless of the
field's name, modifiers, or whether it was `const` - the switch itself was blind to fields as a
category. The thrown `InvalidOperationException` ("FindImplementations: symbolName 'X' was not
found declared in ...") then propagated up through `Member(remove)`'s precheck as a misleading
"member not found" result, even though the field was correctly declared and correctly visible to
every other tool (`GetFileOutline`, `ReadFile`) that looked at the same file.

This confirmed the defect was **broader than previously known**: the pre-existing memory
(`project_member_remove_precheck_false_negative_on_const_fields.md`) had only been reproduced
against `const` fields (`LineCount`, `MatchToken`, `RawWindowOverheadBytes`, found 2026-09-12).
`_unrecoverableBreakerLock` is a plain private instance field, not `const` - proving the switch
statement's blindness applied to the whole `FieldDeclarationSyntax` category, not specifically to
`const` fields as the earlier, narrower repro had suggested.

`FindCallersAsync` in the same file already had the correct handling for this case (match
`FieldDeclarationSyntax` on `.Declaration.Variables`, then fall back to the child
`VariableDeclaratorSyntax` for `GetDeclaredSymbol`, since Roslyn's `GetDeclaredSymbol` returns
`null` directly on the field-declaration node itself) - the two lookups had simply drifted apart
over time, one fixed for fields and the other not.

## Fix applied

Ported `FindCallersAsync`'s existing field-handling pattern into `FindImplementationsForMemberAsync`,
commit `e120b68`. `Member(remove)`'s `skipPrecheck: true` workaround (documented in the earlier,
narrower memory) is no longer needed for this failure mode - kept documented in case of regression,
per the "verify before theorizing, but don't assume a fix holds forever" practice this repo
generally follows for tool-source-traced fixes.

## Why this was worth a writeup even though already fixed

Per `CLAUDE.md`'s failure doctrine, the interesting part is not "the field wasn't found" (a novice
reading would stop there) but that the tool's *own error text* named the wrong mechanism
(`FindImplementations`) for a symbol kind (a field) that mechanism was never built to handle, and
that a `GetFileOutline` cross-check was needed to even establish the tool's stated cause was false.
The fix (`e120b68`) is a good example of the failure doctrine working as intended: rather than
accepting the model-facing error at face value or reaching for `skipPrecheck: true` as a permanent
answer, the actual switch-statement branch that threw was located and the general defect (any
field, not just const) was confirmed before the fix, avoiding a narrower patch that would have left
this exact instance (`_unrecoverableBreakerLock`) unfixed.

## Related

- Memory: `project_member_remove_precheck_false_negative_on_const_fields.md` - the original,
  narrower repro (const fields only) that this instance broadened and closed out; describes the
  same fix and commit.
- `feedback_verify_before_theorizing_on_tool_errors` (memory) - the general practice this fix
  followed: read the actual branch that threw rather than trusting the error's stated cause.
- `RoslynSentinel.Basic/SymbolNavigationEngine.cs` - `FindImplementationsForMemberAsync` (fixed),
  `FindCallersAsync` (the sibling method whose already-correct field-handling pattern was ported
  over).

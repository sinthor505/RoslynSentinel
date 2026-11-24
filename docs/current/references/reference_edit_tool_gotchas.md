# Reference: ReplaceSnippet, Member and related edit-tool gotchas

**Status:** CURRENT 2026-11-03. Moved out of CLAUDE.md "Dog-fooding"; the `filePath` + `batchEdits` rule re-verified against the tool source 2026-11-13, the rest not re-verified.

## Purpose
Consult before the first `ReplaceSnippet`, `Member`, `ModifyEnum` or `MethodSignature` call in a session, or
when one of them rejects an edit.

## How it works
- `ReplaceSnippet` and `Member` run a compile gate: an edit that leaves the solution non-compiling is rejected.
- `ReplaceSnippet` matches the old snippet exactly, whitespace and line endings included.

## Gotchas
- `ReplaceSnippet`: a top-level `filePath` beside `batchEdits` is the default for batch items without their own `filePath`; a top-level `oldContent`/`newContent` (with any selectors) beside `batchEdits` is appended as one more item. Only a top-level edit that cannot form an item (`oldContent` without `newContent`, the reverse, or a selector with neither) is rejected as InvalidArgument (`WorkspaceFileEditImpl.cs` `ReplaceSnippet`). The `Modify*` batch tools still reject any singular parameter beside their batch.
- Order batch edits definitions-first (the compile gate rejects non-compiling intermediate states);
  rename a field and all its usages in one atomic batch.
- Snippets must match exactly, whitespace and line endings included; copy context from a
  `ReadFile`/`GetMethodSource` result, never invent it.
- `Member(addMember)` takes one or more declarations per call (atomic, one changeId); `Member(replace)` takes exactly one; use `addTopLevelType` for a new top-level type.
  `Member` cannot edit an existing top-level record - use `ReplaceSnippet`.
- `ModifyEnum`'s `values` is the complete list; omitting a member deletes it.
- `CancellationToken` is always the last parameter; new parameters go before it.

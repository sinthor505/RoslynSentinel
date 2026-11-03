# Reference: ReplaceSnippet, Member and related edit-tool gotchas

**Status:** CURRENT 2026-11-03. Moved out of CLAUDE.md "Dog-fooding"; not re-verified against the tool source.

## Purpose
Consult before the first `ReplaceSnippet`, `Member`, `ModifyEnum` or `MethodSignature` call in a session, or
when one of them rejects an edit.

## How it works
- `ReplaceSnippet` and `Member` run a compile gate: an edit that leaves the solution non-compiling is rejected.
- `ReplaceSnippet` matches the old snippet exactly, whitespace and line endings included.

## Gotchas
- Never pass `filePath` with `batchEdits`; each batch edit carries its own path.
- Order batch edits definitions-first (the compile gate rejects non-compiling intermediate states);
  rename a field and all its usages in one atomic batch.
- Snippets must match exactly, whitespace and line endings included; copy context from a
  `ReadFile`/`GetMethodSource` result, never invent it.
- `Member(addMember)` takes one declaration per call; use `addTopLevelType` for a new top-level type.
  `Member` cannot edit an existing top-level record - use `ReplaceSnippet`.
- `ModifyEnum`'s `values` is the complete list; omitting a member deletes it.
- `CancellationToken` is always the last parameter; new parameters go before it.

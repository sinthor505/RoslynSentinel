# Idea: `Member(addMember)` support for multiple sibling declarations in one call

**Status:** deferred, not designed. Raised 2026-09-18 while closing out the 4 `Member`/`ChangeSignature`
blocker docs.

## Context

`AddMemberAsync`/`InsertMemberAfterAsync`/`InsertMemberBeforeAsync`/`AddTopLevelTypeAsync`/
`ReplaceMemberAsync` now reject a `newMemberSource` containing more than one top-level declaration
outright (see `DetectTrailingUnparsedDeclaration` in `RoslynSentinel.Basic/RefactoringEngine.cs`),
rather than silently truncating to the first declaration and dropping the rest. This closed 3 of the
4 blocker docs about silent partial writes, but means a caller who legitimately wants to add several
sibling members (e.g. 4 fields, or a method plus its private helper) must make one `Member(addMember)`
call per declaration.

## Question raised

Should `Member(addMember)` instead genuinely support N declarations per call - parsing and adding
all of them, with a per-item confirmation (e.g. "Added field '_a', field '_b', field '_c'") instead
of the current single-item `DescribeMemberOutcome`/`DescribeParsedMember` confirmation - rather than
rejecting and asking the caller to split?

## Lead to check before designing from scratch

`Member(operation: "move")` already has batch semantics, added recently. `ReplaceSnippet` also
accepts a batch `edits` array (applied atomically against each file's original content). Before
inventing new batch plumbing for `addMember`, check whether either existing mechanism's shape
(request array, atomic-apply-or-reject-all semantics, per-item result reporting) can be reused or
extended, rather than designing a third, different batch pattern for this one operation.

## Not yet decided

- Atomic (all N or none) vs. partial-success (some land, some don't) semantics.
- Whether a later declaration in the batch can reference an earlier one added in the same call (the
  original bug repro in `blocking_error_member_addmember_silently_drops_second_declaration.md` was
  exactly this: a method referencing a helper meant to land in the same call).
- Response shape for the per-item confirmation list.

## Related

- `docs/current/blockers/blocking_error_member_add_multi_type_silent_drop.md`
- `docs/current/blockers/blocking_error_member_addmember_silent_partial_write.md`
- `docs/current/blockers/blocking_error_member_addmember_silently_drops_second_declaration.md`

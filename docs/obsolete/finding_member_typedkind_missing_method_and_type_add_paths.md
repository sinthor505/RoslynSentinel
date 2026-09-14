# Finding: `Member(add)`'s `typedKind` has no path for adding a whole method, type, or record — crashes instead of rejecting cleanly

**Status:** confirmed tool/schema defect, not yet fixed. Found during self-run
`manual-selfrun-20260914-004605`, steps 06 and 10 (two independent reproductions, same root
cause).

## Context

**Reproduction 1 (step 06):** `Member(operation: "add", typedKind: "method", ...)` — a natural
guess for adding a whole method — throws a raw, unhandled `JsonException`. `TypedMemberKind`
(`RoslynSentinel.Common/ToolEnums.cs:179-183`) only defines `{ property, field }`; `method` was
never a valid value, so the JSON deserializer rejects it before the tool's own logic ever runs.

**Reproduction 2 (step 10):** adding a new top-level `record` via `Member(operation: "add", ...)`
— both `typedKind: "record"` and `typedKind: "class"` fail the same way: raw
`JsonException` ("The JSON value could not be converted to
System.Nullable`1[RoslynSentinel.Common.TypedMemberKind]"). Same enum gap, different guessed
value.

## Root cause

`TypedMemberKind` was apparently only ever meant to disambiguate *between property and field*
for a specific narrower path — adding a whole method, type, class, or record via `typedKind` was
never a supported path at all. But nothing about the parameter's shape or description says so:
the model has no way to know `typedKind` is scoped to two member kinds only, and a bad enum
value crashes as a raw `JsonException` instead of the "Unknown parameter, accepts only [...]"
pattern this run found reliable elsewhere (including `ReplaceSnippet`'s own oversized-content
error).

## Workaround (confirmed working, both reproductions)

Omit `typedKind`/`typedName` entirely. Pass `memberName` + the full `newMemberSource` text +
`containerName: ""` (empty string, for top-level) + `position: "after:<TypeName>"`. This
successfully adds a new member/type without going through the broken enum path at all.

## Recommendation

- Either extend `TypedMemberKind` to actually support `method`/`class`/`record` (if that's the
  intended long-term shape of `typedKind`), or make the parameter reject cleanly with its real
  valid values instead of leaking a raw `JsonException`.
- At minimum, document in `typedKind`'s own parameter description that it's scoped to
  property/field only, and that omitting `typedKind`/`typedName` (using `memberName` +
  `newMemberSource` + `position`) is the correct path for adding a whole new method, type, class,
  or record via `Member(add)`.

## Reference

- Session log: `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md`,
  Step 06 (~line 534) and Step 10 (~line 1152).

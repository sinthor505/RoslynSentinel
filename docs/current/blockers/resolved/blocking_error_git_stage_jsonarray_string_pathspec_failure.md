# `Git(stage)` fails on a JSON-array-shaped string passed to `paths`/`files` - open question: real bug or tool-guidance gap

**Status:** OPEN, needs deeper investigation - found (again) 2026-09-19, while executing
`docs/current/plans/plan_extract_symbol_resolver.md`. Not a hard stop: the failure is loud and
recoverable (no data written to the wrong place), the workaround (one path per call, or a
comma-separated string) is known and used successfully in the same session.

## What was being attempted

Staging multiple files in one `Git(operation: "stage")` call, passing `paths` as a JSON array.

## Call 1 - array-shaped string, no `scope`

```
Git(operation: "stage",
  paths: "[\"RoslynSentinel.Common/PersistentWorkspaceManager.cs\", \"RoslynSentinel.Common/MutationCircuitBreaker.cs\", \"docs/current/plans/plan_extract_manual_circuit_breaker.md\"]")
```

Result (exact): *"You named files to stage but passed scope=\"tracked\", which ignores them. Pass
scope=\"listed\" to stage exactly the files you named (untracked ones included), or drop the file
list to stage by scope=\"tracked\". Nothing was staged."*

This much is working as documented - `scope` defaults to `"tracked"`, and the tool correctly
refuses to silently ignore a named file list under that default, per the guarantee recorded in
`docs/current/blockers/resolved/blocking_error_git_stage_listed_scope_over_stages_unrequested_file.md`.

## Call 2 - same array-shaped string, with `scope: "listed"` added

```
Git(operation: "stage", scope: "listed",
  paths: "[\"RoslynSentinel.Common/PersistentWorkspaceManager.cs\", \"RoslynSentinel.Common/MutationCircuitBreaker.cs\", \"docs/current/plans/plan_extract_manual_circuit_breaker.md\"]")
```

Result (exact): *"git add failed: fatal: pathspec '[\"RoslynSentinel.Common/PersistentWorkspaceManager.cs\"'
did not match any files"*

## What this actually is - precise characterization

**Important distinction, confirmed by re-reading this session's transcript directly:** the failing
value was not a true JSON array passed as the parameter's type. `paths` is documented (per
`project_git_tool_defects_2026_09_12.md`, verified against source 2026-09-16) as a plain
`string?` accepting one comma-separated list, split with `Split(',', ...)`. What was actually sent
here was **a JSON-array literal serialized as that one string** - i.e. the literal text
`["a", "b", "c"]`, including the brackets and quotes, submitted as the entire (unsplit) value of a
parameter that expects `a,b,c`. `Split(',', ...)` on that input produces exactly one token per
whatever the tool's implementation trims to - and the git error confirms the *first* element still
carries its literal leading `["` and trailing `"` - meaning the array-as-string was passed through
essentially unparsed into `git add --`, rather than being rejected as malformed input up front, or
transparently accepted as an array (which the schema does not support).

So this reproduces cleanly as: **a plausible-looking but wrong input shape (JSON array textified
into a single string) produces a raw, uninterpreted `git add` pathspec failure**, not a clean
parameter-validation error naming the expected shape (`"comma-separated string, not a JSON array"`).

## Why this needs investigation, not just a memory note

This exact mistake has now recurred **at least three times across sessions** despite being
"fixed" at the design level on 2026-09-16 (schema changed from array-typed to a single
comma-separated `string?`):

1. Documented in `project_git_tool_defects_2026_09_12.md`'s 2026-09-15 entry (original discovery).
2. Documented in the same memory's "Re-encountered 2026-09-18" note - explicitly diagnosed at the
   time as "not a new tool defect - a reminder to actually read this memory's content... rather than
   assuming array-shaped params from other tools apply here too."
3. This session (2026-09-19), reproduced again with the same shape.

A mistake this consistent, recurring across independent sessions/models that have no memory of each
other, despite the parameter already being schema-typed as a plain string, is a signal that the
*schema alone* is not sufficient guidance - something about the tool surface invites this specific
malformed shape. Open questions this doc exists to have investigated, not to answer unilaterally:

1. **Is there a genuine parsing bug**, e.g. should the tool detect a leading `[` and either reject
   with a clear "this looks like a JSON array; pass a comma-separated string instead" error, or
   (more permissively) actually parse and accept a JSON array as a convenience? Right now it does
   neither - it silently mis-splits and hands the mangled result straight to `git`.
2. **Is the `[Description]` on `paths`/`files` sufficiently explicit** that this is a single
   delimited string and not a JSON array? Every other array-shaped concept in this MCP surface
   (e.g. `ChangeSignature`'s `parameters`) genuinely *is* a JSON array - a caller conditioned by the
   rest of the tool surface has a reasonable prior that a multi-item parameter accepts array syntax,
   and nothing in the failure path corrects that prior; it just fails downstream at the `git`
   invocation layer with a raw pathspec error.
3. **Should the error message itself name the fix?** Compare the `scope` mismatch error above (Call
   1), which names the exact required parameter and value ("Pass scope=\"listed\"") - genuinely
   agent-friendly per this repo's own stated principle. The pathspec failure (Call 2) does not; it
   surfaces a raw `git` error with no indication that the fix is "use a comma-separated string, not
   a JSON array." This is the same "error message enables recovery" test `CLAUDE.md`'s root-cause
   discipline asks of every failure - it currently fails that test.

## Root cause - not traced to source this session

`SentinelGitTools.cs`'s `StageAsync` (per the sibling memory, `Split(',', ...)`-based) was not
re-read this session. Whether a leading-`[`/JSON-array detection would be cheap to add, whether
`git add --` is called with the raw split tokens with no per-token validation, and whether other
`Git` operations sharing the same comma-separated-string convention (`diff`'s `paths`, `log`'s
`paths`) have the identical exposure, are all open and unconfirmed.

## What unblocks it

A maintainer should read `StageAsync` (and ideally every other `Git` operation accepting a
comma-separated path list) to decide, deliberately, one of:

1. Reject early with a clear message when the input looks like a JSON array (starts with `[`,
   ends with `]`) rather than passing it through to `git`.
2. Accept a JSON array transparently as an alternative input shape, if that's judged more useful
   than forcing callers to remember this tool's parameter is the odd one out.
3. At minimum, strengthen `paths`/`files`'s `[Description]` to state explicitly "comma-separated
   string, NOT a JSON array" given how consistently this has been mis-guessed, and confirm whether
   doing so alone (without a code change) is enough to stop the recurrence - this is the cheapest
   lever and should be tried/measured before assuming a code fix is required.

This doc exists specifically so a maintainer decides the fix direction deliberately, rather than
this being patched over again with only a memory note (which has already been tried once, 2026-09-18,
and did not prevent the third recurrence documented here).

## Related

- `project_git_tool_defects_2026_09_12.md` (memory) - full history of this exact failure across
  three occurrences; being updated alongside this doc's creation to point here.
- `docs/current/blockers/resolved/blocking_error_git_stage_listed_scope_over_stages_unrequested_file.md` -
  a different, already-filed `Git(stage)` defect (over-staging via index-additivity), same tool,
  unrelated failure mode.
- `docs/current/plans/plan_extract_symbol_resolver.md` - the task this recurrence was found during.

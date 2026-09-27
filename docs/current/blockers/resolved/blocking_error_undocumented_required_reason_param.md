# `reason` is a required parameter on `ReplaceSnippet` and `Git` with an empty schema `description` - undiscoverable until rejected

**Status:** WORKED AROUND this session (task completed once `reason` was supplied on each call);
not a live blocker. Writeup filed because the schema defect that caused the rejections is still on
disk and will keep producing the identical surprise for the next caller of either tool.

## What was being attempted

A DI-refactor task in `RoslynSentinel.Server.Basic`: registering `*Impl` classes as DI singletons,
converting `*Tools` constructors to take the corresponding `*Impl` directly, and deleting the
now-unused legacy convenience constructors. Two of the routine per-CLAUDE.md dog-fooding calls this
required - a code edit via `ReplaceSnippet` and a working-tree stage via `Git` - were each rejected
on the first attempt for a missing required parameter with no prior indication in the schema that it
existed or was mandatory.

## Call 1 - `ReplaceSnippet`, missing `reason`

Called with `filePath`, `action`, `oldContent`, `newContent` - no `reason`. This came immediately
after already resolving a separate missing-`action` rejection on the same tool earlier in the same
session (that one was self-explanatory: `ToolSearch` showed `action` as a required enum parameter
with a real description). The `reason` rejection on the very next call was a second, distinct
surprise of the same "undocumented required parameter" shape:

```
Missing required parameter 'reason' for tool 'ReplaceSnippet'. Pass reason: a short phrase (at
least 10 characters, containing a space) saying why you are calling this tool right now, e.g.
"checking the working tree before staging". Nothing was executed.
```

Nothing was written; the edit was retried with `reason` supplied and succeeded.

## Call 2 - `Git`, missing `reason`

Called with `operation: "add"` and an explicit `paths` list, no `reason`. Rejected with the same
class of message (tool name `Git` in place of `ReplaceSnippet`, otherwise the same shape and wording
pattern). Retried with `reason` supplied.

## Call 3 - `Git`, `reason` supplied, different (and well-formed) rejection

Same call, now with `reason` present, `operation: "add"`, and explicit `paths`, relying on the
default `scope`. Rejected again, but for an unrelated, already-well-documented reason:

```
You named files to stage but passed scope="tracked", which ignores them. Pass scope="listed" to
stage exactly the files you named (untracked ones included), or drop the file list to stage by
scope="tracked".
```

This third rejection is included here deliberately as a **contrast case, not part of the defect**:
it names the exact parameter (`scope`), explains the conflict against the parameter actually
supplied (`paths`), and gives two concrete alternative fixes. It is exactly what CLAUDE.md's
root-cause discipline (section 3, "did the error message enable recovery?") asks for, and it is the
standard the `reason` rejections in Calls 1-2 should be brought up to.

## Where this happened

- Tool `ReplaceSnippet`, `reason` parameter - rejection quoted above under Call 1.
- Tool `Git`, `reason` parameter - rejection quoted above under Call 2.
- Tool `Git`, `scope`/`paths` interaction - rejection quoted above under Call 3 (contrast case only).

## Root cause - confirmed live against the actual emitted schema, not the error text or memory

Per CLAUDE.md's root-cause discipline, the emitted JSON schema was pulled directly this session via
`ToolSearch(select: ReplaceSnippet, Git)` rather than trusting the rejection message's own account of
itself. Confirmed on both tools:

```json
"reason": { "description": "", "type": "string" }
```

- On `ReplaceSnippet`: `"required": ["reason", "action"]`.
- On `Git`: `"required": ["reason", "operation"]`.

`reason` is genuinely required on both tools (present in each schema's `required` array) and the
`description` field for it is a **literal empty string** on both - not a vague description, not a
truncated one, an empty one. A model reading the schema has no way to discover that `reason` exists,
that it is mandatory, or what makes a value valid. The constraint that does exist - "at least 10
characters, containing a space" - and the example - `"checking the working tree before staging"` -
appear only inside the rejection error text, never in the schema the model sees before calling the
tool. This is confirmed by direct inspection of the live schema, not inferred from the rejection
message or from memory.

This is the CLAUDE.md failure-doctrine pattern named explicitly in the "environment fixes look
like" list: "a required parameter instead of an optional one that relocates the failure" - the
failure is relocated from call time (where a good description would have prevented it) to
rejection time (where the model must be told after the fact, and only in the error string, what the
schema itself should have said).

By contrast, `action` on `ReplaceSnippet` and `scope` on `Git` are both required-or-defaulted
parameters on the same two tools that already carry real, non-empty schema descriptions - `reason`
is the outlier on both tools, not a pattern the tools lack elsewhere.

## What unblocks it

Populate the `description` field for `reason` in both tools' schema-emission source with the same
content currently living only in the rejection message: the length/space constraint and the
worked example ("at least 10 characters, containing a space, e.g. \"checking the working tree
before staging\""). This is a schema/description change only - `reason`'s validation behavior
itself (the constraint, the rejection wording) does not need to change, since Call 3 above shows
this repo's error-message quality bar is already met elsewhere; `reason` just needs its schema entry
brought up to that same bar so the rejection in Calls 1-2 stops being the first place a caller can
learn the parameter exists at all.

## Related

- `docs/current/blockers/blocking_error_membershaped_success_types_not_unified.md` - the other
  currently-filed blocker doc in this directory; unrelated tool surface, same directory/taxonomy.
- CLAUDE.md, "Failure doctrine: the environment is responsible" - the "required parameter instead
  of an optional one that relocates the failure" pattern this doc is a direct instance of.
- CLAUDE.md, "Root-cause discipline", item 3 - "did the error message enable recovery?" - the
  standard Call 3's `scope` message already meets and the `reason` schema description does not.

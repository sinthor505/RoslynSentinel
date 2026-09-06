# Proposed tool: SplitFacadeMember

A special-purpose refactoring tool for the *Tools*/*Impl* god-class-facade split pattern (Decision
1-Amendment in `plan_split_workspace_refactoring_tools_for_di.md`), designed after hand-performing
this exact operation 6 times via `ApplyDiff` in the `WorkspaceReadNavigationTools`/`Impl` trial
slice. Related to, but distinct from, `MoveMember` — see "Relationship to MoveMember" below.

## The pattern this targets

Every method migrated in the trial slice went through the same four mechanical steps:

1. **Move** the verbatim method body (plus any private helpers used only by it) from the god-class
   (`SentinelWorkspaceTools`) to a new plain DI class (`WorkspaceReadNavigationImpl`), stripping MCP
   attributes there.
2. **Stub the god-class**: replace the original method body with a one-line delegate to a new field
   of a second new class (`WorkspaceReadNavigationTools`), keeping the original method's name,
   accessibility, and *all* its `[McpServerTool]`/`[Description]`/`[Produces]`/parameter attributes
   completely unchanged — the public MCP tool surface must not move.
3. **Create the middle hop**: a thin `[McpServerToolType]` class duplicating that same attributed
   stub shape, delegating onward to the `*Impl` class — the two-hop shim required by the
   *Tools*/*Impl* amendment.
4. **Redirect same-class internal callers**: any other method in the god-class that called the
   moved method by its old same-class name (e.g. `ReadFile` calling `GetFileOutline`) gets
   redirected to call through the new field instead.

This is a different shape from a general "move and rewrite all callers" operation: the method's
public name, signature, and attributes are pinned at the outermost layer by design, and the
*internal* structure underneath is what's changing. `MoveMember` doesn't model this — it moves a
member to one destination and rewrites callers to the new location directly, not through a
deliberate double-facade.

## Proposed signature

```
SplitFacadeMember(
    reason: string,
    oldLocation: string,           // filepath of the god-class
    oldClassName: string,
    memberNames: string[],         // batch — helpers travel with their owning method(s)
    facadeLocation: string,        // filepath of the *Tools middle-hop class (created if absent)
    facadeClassName: string,
    implLocation: string,          // filepath of the *Impl class (created if absent)
    implClassName: string,
    implFieldName: string? = null, // defaults to a camelCase derivation of implClassName, e.g. "_readNav"
    facadeName: string? = null,    // defaults to the original member's name; set to rename at the facade hop
    implName: string? = null,      // defaults to the original member's name; set to rename at the impl hop
    autoStage: bool = true,
    dryRun: bool = false,
    returnDiff: bool = false)
```

`oldName` is just `memberNames`'s entries themselves — no separate parameter needed unless a
per-member rename is wanted, in which case `facadeName`/`implName` would need to become
per-member maps rather than single strings. For the common case observed in this slice (all 6
methods kept an identical name at all three locations), scalar defaults matching the original name
are sufficient.

## What it would do, atomically, per batch

1. **Move bodies + dependency helpers.** For each name in `memberNames`, move the verbatim body to
   `implLocation`/`implClassName`, stripping MCP attributes. Scan the god-class for private helper
   methods used *exclusively* by members in this batch and move those too. A helper used by a
   member **not** in this batch stays behind in the god-class — flagged in the report, not silently
   skipped — since removing it would break the still-resident caller (this is exactly the CS0103
   failure hit mid-session when `GetOperationDetail`'s helpers were removed before `GetMethodSource`,
   which also used them, had been converted).
2. **Generate the facade stub** at `facadeLocation`/`facadeClassName`: same signature and attributes
   as the original, body `=> _<implFieldName>.MethodName(args...)`.
3. **Replace the god-class body** at `oldLocation` with the equivalent stub shape, delegating to a
   field of `facadeClassName`'s type — producing the two-hop chain (god-class → *Tools* → *Impl*).
4. **Wire constructor fields/parameters** at both `oldLocation` and `facadeLocation` as needed for
   the new field dependencies introduced in steps 2–3. This is the step that, done by hand, produced
   the bulk of this session's mechanical follow-on work: every one of the 14 test files that directly
   constructed `SentinelWorkspaceTools(...)` needed updating once its constructor signature grew a
   parameter. A tool-driven version of this step doesn't remove that follow-on work (test call sites
   still need the new argument), but it does guarantee the production-code side of the wiring is
   internally consistent in one pass rather than assembled by hand across several `ApplyDiff` calls.
5. **Redirect same-class internal call sites.** Scan `oldClassName`'s remaining methods (the ones
   *not* in this batch) for calls to the moved member(s) by their old same-class name, and rewrite
   those to go through the new field. Scoped strictly to same-class calls — there is no external
   call-site rewriting, by design, since the public tool name never moves.
6. **One combined write** through `ValidateAndApplyAsync` across all three files (old, facade, impl)
   plus any files containing redirected internal call sites — one compiler check, one atomic
   outcome, same principle `MoveMember` already applies.

## Report contents

Mirroring the dry-run-report design proposed for `MoveMember`
(`proposal_movemember_instance_callsite_resolution.md`), the result/dry-run report should include:

- Which helper methods were auto-moved (used only by this batch) vs. left behind (still used
  elsewhere in the god-class), and which specific still-resident member(s) keep each left-behind
  helper alive.
- Which same-class internal call sites were found and rewritten (file, line, old call, new call).
- The final constructor signatures at `oldLocation` and `facadeLocation`, so the caller can
  immediately see what changed without a separate `GetMethodSource` round-trip (this session read
  each affected constructor at least once specifically to answer "what got added").

## Why this sidesteps MoveMember's instance-callsite problem entirely

`MoveMember`'s hard case is: after a move, what does an *external* caller's now-invalid receiver
expression get rewritten to, when multiple candidate instances of the destination type might exist?
`SplitFacadeMember` never has this problem, by construction — the public name and call surface at
the outermost (god-class) layer are pinned and unchanged; only the implementation underneath moves.
The only rewriting this tool ever performs is same-class-internal and mechanical (a same-class
method call becomes a same-class field-dot-method call), which has no receiver-identity ambiguity to
resolve — there is exactly one correct new receiver (the new field), always.

## Scope and relationship to MoveMember

This is narrower than `MoveMember` by design — it encodes one specific architectural pattern (thin
facade → thin facade → plain impl, with the outer name/attributes pinned) rather than a general
move primitive. That's an acceptable trade here because the DI-split plan
(`plan_split_workspace_refactoring_tools_for_di.md`) repeats this exact pattern across many more
classes ahead of `WorkspaceReadNavigationTools`, so a purpose-built tool amortizes across the whole
plan rather than a single slice.

The two tools are complementary, not overlapping: `MoveMember` for general member relocation with
caller-facing renames (its instance-callsite refinement is tracked separately), and
`SplitFacadeMember` for this specific god-class-to-DI-facade decomposition where the public surface
must not move at all.

## Status

Design proposal only — not yet implemented or scheduled. Validated against one real completed
migration (`WorkspaceReadNavigationTools`/`Impl`, 6 methods, 8 test files, all done by hand via
`ApplyDiff` across this session) as the source of the mechanical steps it's meant to automate.

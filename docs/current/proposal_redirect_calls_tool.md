## Motivation

`AddMemberAsync`/`InsertMemberAfterAsync`/`InsertMemberBeforeAsync`
(`RoslynSentinel.Basic/RefactoringEngine.cs`) previously called
`.ReplaceNodeFormattedAsync(document, root, container, newContainer, ...)` with `newContainer` built
by adding a member to the *whole class/struct/interface/record* node, then formatting that whole
container. `Formatter.FormatAsync` over a whole-container span reformats every sibling member's
whitespace as a side effect of formatting the one member that actually changed — not just the new
member. This was found and fixed this session (uncommitted at time of writing; see
`git diff RoslynSentinel.Basic/RefactoringEngine.cs` and the new
`FormattingHelper.InsertMemberFormattedAsync` at `RoslynSentinel.Common/FormattingHelper.cs:180-`,
which scopes the formatting annotation to just the inserted member instead). The bug is written up at
`docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md`.

Separately from that fix, the same repo has roughly 40 other call sites — scattered across
`RoslynSentinel.Basic` and `RoslynSentinel.Advanced` engine classes — that call Roslyn's
`.NormalizeWhitespace()` extension method directly, each formatting a node the same uncontrolled way,
with no shared chokepoint to know which of them share this container-wide-reformat risk shape. A
follow-up task, in progress separately from this proposal, is centralizing those ~40 sites behind a
new wrapper — `FormattingHelper.NormalizeWholeSubtreeWhitespace(node, indentation, eol,
elasticTrivia)` (already added as a pure pass-through, per the same uncommitted diff, at
`RoslynSentinel.Common/FormattingHelper.cs`'s new method following `InsertMemberFormattedAsync`) —
so that later, if the container-wide-reformat risk needs auditing or fixing at any of those 40 sites,
there's one method to grep for and reason about instead of 40 independent extension-method calls.

Getting there today means an agent (or a human) locating each of the ~40 call sites individually and
manually reshaping each one — from an extension-method call, `node.NormalizeWhitespace(indentation,
eol, elasticTrivia)`, into a static-wrapper call, `FormattingHelper.NormalizeWholeSubtreeWhitespace(
node, indentation, eol, elasticTrivia)` — via `ReplaceSnippet`/`ApplyDiff`, one call site at a time,
plus adding a `using RoslynSentinel.Common;` where the file doesn't already have one. That reshape —
"every call to method X, solution-wide, should now call method Y instead, with the original
receiver/arguments remapped into Y's parameter list" — has no dedicated tool today. This proposal is
for that tool, motivated by this recurring shape, not a request to redo or duplicate the in-flight
40-site centralization itself.

## Why the existing tools don't cover this

**`RenameSymbol`** (`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs:165-`, backed by
`RefactoringEngine.RenameSymbolAsync` at `RoslynSentinel.Basic/RefactoringEngine.cs:836-876`, itself
a thin wrapper over `Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync`) changes an
identifier's *name* everywhere it's referenced, but preserves the call expression's shape exactly —
argument count, order, and receiver-vs-static form are untouched. `node.NormalizeWhitespace(args)` →
`FormattingHelper.NormalizeWholeSubtreeWhitespace(args)` is not a name change: the receiver (`node`)
has to move from "the expression the method is called on" to "the first argument in a static call,"
which `Renamer` has no concept of.

**`FindReferences`** (`RoslynSentinel.Server.Basic/SentinelSymbolTools.cs:400-`) finds call sites —
`FindReferencesAsync`/`FindCallersAsync`, backed by `SymbolFinder.FindReferencesAsync`
(`RoslynSentinel.Basic/RefactoringEngine.cs:161,1418,4141,4497` are existing call sites of that same
Roslyn API elsewhere in this codebase) — but only reports them; it does not rewrite anything.

**`ChangeSymbolType`** (proposed, not yet implemented — see
`docs/current/proposal_changesymboltype_tool.md`) is the closest existing design precedent for "find
every reference semantically, then apply a shape-changing rewrite per reference, then submit the
whole multi-file result as one atomic `ValidateAndApplyAsync` call" — but it rewrites references to a
*symbol's declaration* (a field/property changing type), not *call expressions* to a *method*. The
mechanics this proposal needs — reference search via `SymbolFinder`, per-site rewrite, one atomic
multi-file apply — are the same pattern; the reshape itself (call-expression restructuring rather
than declaration-type change) is different enough that it doesn't fit as a mode of that tool.

No existing tool does "find every call site of method X and rewrite the call expression itself into
a different shape, calling method Y."

## Proposed tool

Working name: `RedirectCalls` (alternatives considered below).

### What it does

1. Resolve a target method the same way `PreviewRenameImpact`/`RenameSymbol` already do — prefer
   `docCommentId` + `projectName` (unambiguous, as returned by `LocateSymbol`), falling back to
   `filepath`/`symbolName`/`contextSnippet`/`lineBefore`/`lineAfter` when `docCommentId` isn't known
   (`SentinelSymbolTools.cs:363-376`'s parameter set is the template to match, not `FindReferences`'s
   — `FindReferences` at `SentinelSymbolTools.cs:403-414` never gained the `docCommentId` fallback
   and is `symbolName`/`filepath`-only; this proposal should follow the newer, preferred convention
   rather than propagate the older one).
2. Find every call site of that method solution-wide, reusing `SymbolFinder.FindReferencesAsync` —
   the same Roslyn API `FindReferences`, `RenameSymbolAsync`, and the field-usage checks in
   `Member(remove)`/`ConstructorParameter(remove)` already call
   (`RoslynSentinel.Basic/RefactoringEngine.cs:161,1418,4141,4497`) — rather than a new reference-
   finding path.
3. Take a description of the new call shape and rewrite each found call-expression node accordingly.
4. Add the needed `using` directive per file that doesn't already have one for the new method's
   containing namespace (mirroring `UsingDirective(add)`'s existing add-if-absent semantics,
   `SentinelRefactoringTools.cs:607-621`, though this tool would do it as an internal step of one
   larger edit rather than as its own top-level call).
5. Assemble every file's fully-rewritten content into one `Dictionary<FilePathWrapper, string>` and
   submit it in a single `ValidateAndApplyHelper.ValidateAndApplyAsync` call
   (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:18-118`) — one delta-compile validation pass, one
   atomic write, one `changeId`, rollback on partial failure — exactly the same shape `RenameSymbol`
   already gets from `Renamer.RenameSymbolAsync` producing a multi-file dictionary in one pass
   (`RefactoringEngine.cs:856-866`).
6. Support `dryRun`/`returnDiff` on the same terms `ValidateAndApplyAsync` already gives every other
   mutating tool that calls it (`ValidateAndApplyHelper.cs:24-25,59-63`) — a full diff of every
   proposed call-site rewrite, reviewable before committing.

### v1 scope: extension-call → static-wrapper-call only

Scope v1 to exactly the motivating shape:

```
node.NormalizeWhitespace(indentation, eol, elasticTrivia)
  → FormattingHelper.NormalizeWholeSubtreeWhitespace(node, indentation, eol, elasticTrivia)
```

i.e.: an instance/extension-method call `receiver.OldMethod(arg1, ..., argK)` becomes a static call
`NewType.NewMethod(receiver, arg1, ..., argK)` — the receiver becomes the new call's first argument,
and the original arguments shift right by one position, in original order, unchanged. No other
receiver position, no argument reordering, no instance-to-instance or static-to-extension direction.
This is the only proven need so far (the ~40-site centralization); broader remapping (arbitrary
argument-position permutation, receiver-to-non-first-argument, static-to-instance) is speculative
until a second real case shows up. Per this repo's general anti-speculative-generality convention
(CLAUDE.md: don't design for hypothetical future requirements), v1 should reject any request that
isn't this one shape rather than accept a general remapping DSL nobody has asked for yet.

Concretely, the tool's shape-description input needs only:
- the target method to redirect from (existing symbol addressing, above)
- the new method's `docCommentId` (or filepath/name) to redirect to
- which argument position the receiver lands at in the new call (fixed at position 0 for v1, but
  naming it explicitly in the parameter schema — rather than hardcoding it silently — costs nothing
  and pre-empts the first likely v2 ask)

### Where it fits in the tool family

`RenameSymbol` and `Member` both live in `SentinelRefactoringTools.cs` (Basic), backed by
`RefactoringEngine`. This tool is closest in kind to `RenameSymbol` — solution-wide, symbol-driven,
one atomic multi-file apply — so the natural home is alongside it: a new method on
`RefactoringEngine` (`RoslynSentinel.Basic/RefactoringEngine.cs`) called from a new
`[McpServerTool]` in `SentinelRefactoringTools.cs`, Basic tier. Nothing about the v1 scope (a single
fixed reshape) needs anything `RoslynSentinel.Advanced` provides beyond what `RenameSymbol` already
uses, so there's no basis for putting it in Advanced instead
(`docs/current/project_advanced_extends_basic.md`-equivalent reasoning: Advanced project-references
Basic, not the other way, so a Basic-tier tool stays usable by both).

## Open questions

- **Naming.** `RedirectCalls` reads as "point calls elsewhere" and doesn't imply an argument
  reshape, which is arguably the more interesting/riskier part of what this tool does.
  `RewriteCallSites` is more literal about doing a rewrite, but doesn't name what the input/output
  relationship is. `RemapMethodCalls` foregrounds the argument remapping but is a mouthful. Given
  this repo's existing tool names are short, imperative, and don't try to fully describe mechanism
  (`RenameSymbol`, `MoveMember`, `ChangeSignature`), `RedirectCalls` is the tentative recommendation,
  but this is genuinely open — pick based on how it reads next to `RenameSymbol`/`MoveMember` in an
  agent's tool list, not in isolation.
- **v1 scope confirmation.** This doc recommends narrowing to extension-call → static-wrapper-call
  only (see above) rather than a general reshape DSL. Confirm before implementation — the broader
  shape (arbitrary argument remapping, other directions) is a natural v2 if a second real need shows
  up, but nothing here should be built speculatively ahead of that.
- **Partial-mapping failures.** What happens when a call site's arguments don't cleanly map — named
  arguments, an omitted optional argument, a `params` array spread across a variable number of
  arguments at the call site? Two options:
  - **Fail-closed per site, continue the rest**: report the specific unmappable site (file, line,
    reason) in the result and skip only that call, redirecting every other clean site — mirrors
    `ChangeSymbolType`'s proposed `Unclassified`/`ConstructorPositionalArg` handling in one sense
    (name the uncovered shape) but diverges in the other (that proposal rejects the *whole batch* on
    any uncovered site, per its "Fail-closed coverage requirement",
    `docs/current/proposal_changesymboltype_tool.md:120-125`).
  - **Fail-closed for the whole batch**: any single unmappable site blocks the entire redirect,
    consistent with `ChangeSymbolType`'s stricter policy and with this tool's own point 5 above
    (one atomic apply, no partial landing).
  This doc does not resolve which; both are defensible, and the answer likely depends on whether the
  common case is "occasionally one odd call site among forty, worth skipping and reporting" or
  "any miss means the redirect is unsafe to do at all." Recommend deciding by looking at how many of
  the actual ~40 in-flight `NormalizeWhitespace` sites (once that centralization finishes or is
  inspected) use named/optional/params-spread arguments — if zero do, the question is moot for the
  motivating case and can be decided by whichever policy is simpler to implement correctly.
- **`PreviewRedirectImpact` vs. dry-run only.** `RenameSymbol` has a companion read-only
  `PreviewRenameImpact` (`SentinelSymbolTools.cs:360-`) that reports affected-file/location counts
  without needing the caller to construct the full rewrite first. `dryRun` on `RedirectCalls` itself
  would need the full shape description supplied anyway (it's not optional — the tool cannot rewrite
  anything without knowing the target shape), so a `dryRun=true` call already gets a caller the full
  proposed diff via `returnDiff`, at the cost of specifying the shape up front. A separate
  `PreviewRedirectImpact` would only add value if it can answer "how many call sites exist, and do
  any of them look unmappable" *before* the caller has fully specified the new shape — useful for
  scoping ("is this even worth doing to 40 sites") but redundant with `dryRun` once the shape is
  known. Recommend: skip a separate preview tool for v1; `dryRun` + `returnDiff` covers the same
  need once the (fixed, narrow) v1 shape is specified, and `FindReferences` already answers "how many
  call sites and where" for the scoping question without a new tool.

## Cost / risk

- **New engine method** on `RefactoringEngine.cs` (already the largest file in the solution per the
  ~27-site `NormalizeWhitespace` inventory alone, `docs/obsolete/plan-normalize-whitespace-full-
  sweep-v1.md`'s per-file counts) — adds to an already-large surface rather than a new file, unless
  this is treated as the next candidate for the `Tools/Impl` split work
  (`docs/current/project_di_tool_split_plan_2026_09_05` per memory, or whichever doc that's been
  promoted to) already in progress for other reasons.
- **Argument-shape rewriting is a new kind of mutation** for this codebase: every existing mutating
  tool either (a) swaps an identifier (`RenameSymbol`), (b) replaces/inserts/removes a whole member
  or declaration (`Member`, `ModifyEnum`, `ChangeAccessibility`), or (c) takes fully-specified
  replacement source from the caller (`ReplaceSnippet`, `ApplyDiff`). This tool synthesizes a new
  call-expression's argument list programmatically from an old one — closer in kind to the
  proposed-but-unimplemented `ChangeSymbolType`'s per-shape templates
  (`docs/current/proposal_changesymboltype_tool.md`) than to anything currently shipped. Expect the
  same category of edge case that document flags (named args, positional-only sites, unusual
  argument shapes inside lambdas/expression trees) to apply here too.
- **What it doesn't touch**: no changes to `ValidateAndApplyHelper`, `PersistentWorkspaceManager`, or
  any other tool's write path — this is additive, one new tool riding the existing chokepoint.
- **What it makes harder**: nothing structural, but a `RedirectCalls`-shaped tool sitting next to
  `RenameSymbol` invites scope creep toward "a general call-rewriting DSL" over time; the v1-narrow
  recommendation above is meant to hold that line explicitly rather than leave it implicit.

## Status

Design proposal only — not yet implemented, not yet scheduled. Motivated by this session's
`Member(add)`/`InsertMemberAfter`/`InsertMemberBefore` whole-container-reformat fix and the
separately in-flight (not yet complete) `NormalizeWhitespace` → `FormattingHelper
.NormalizeWholeSubtreeWhitespace` call-site centralization, which is the first concrete case this
tool would have applied to had it existed. Author: Claude (Sonnet 5), for Andrew Almond
(andrew.almond@clearbridge.ca).

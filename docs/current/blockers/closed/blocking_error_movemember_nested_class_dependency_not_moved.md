# `MoveMember` cannot move private nested classes, and does not detect that moved method bodies depend on them

**Status:** FIXED 2026-09-28. Both root causes fixed in `AdvancedStructuralEngine.cs`
(`MoveMemberAsync` and the new `RequireNoUnmovedDependencies` helper). Verified live against the
exact repro in this doc after a server rebuild+restart:
- `memberNames: ["ModernGuardRewriter"]` alone now resolves (previously immediate `NotFound`) and,
  moved in isolation, correctly reports the resulting dangling reference as a `ValidationFailed`
  post-move `CS0246` at the now-orphaned call site in the source file - expected, since the type's
  only user (`UpgradeToModernGuardsAsync`) wasn't included in that call.
- `memberNames: ["UpgradeToModernGuardsAsync"]` alone (the method, without its nested-type and
  field dependencies) is now refused up front with `errorCode: InvalidArgument`, naming all three
  missing dependencies: `Cannot move the requested member(s) out of 'SyntaxUpgradeEngine': their
  bodies reference private/internal member(s) [ModernGuardRewriter, _config, _workspaceManager] of
  'SyntaxUpgradeEngine' that are not included in this move ... Add the missing name(s) to
  memberNames so they move together, or leave the referencing member(s) behind.` This is broader
  and more correct than the fix originally scoped for - it also catches the instance-field
  dependencies (`_config`, `_workspaceManager`) that the rewriter's constructor needs, not just the
  nested type itself.

The full production fold-in (moving all 17 `SyntaxUpgradeEngine` members plus resolving
`callSiteFixups` for 31 affected test call sites) was not re-attempted end-to-end in this session -
that remains a separate, larger task. This doc covers only the `MoveMember` tool defect, which is
resolved.

**Update 2026-09-28 (group 1, async family):** the same root cause reproduces against `public`
nested `record` types, not just `private` nested classes - so the defect is broader than the title
suggests: `MoveMember`'s member-lookup does not recognize ANY nested type declaration (class or
record, any accessibility) as a movable member. See "Corroborating reproduction" below for the
isolated repro. This blocked carving `AsyncOptimizationEngine`'s migration-candidate-scoring methods
out into a new `AsyncMigrationEngine` class (group 1 of the engine-reorg plan) because 6 of the
members to move are nested record/class types
(`FlagMigrationCandidateEngineResult`, `CandidateScoredItem`, `FlagCandidatesInProjectEngineResult`,
`RemovedCandidateInfo`, `RemoveMigrationCandidatesEngineResult`, `MigrationCandidateRemover`)
that are constructed/returned by the methods being moved - the same "moved method references an
un-moved same-class dependency" shape as the original `SyntaxUpgradeEngine` finding below, just with
the roles of mover/dependency reversed (here the *methods* moved cleanly in isolation; it's the
*record types they return* that `MoveMember` can't find by name, `NotFound`, immediately). No file
was modified - both isolated calls were `dryRun: true` and the earlier batched attempt that included
the record names alongside methods was `ValidationFailed` and not applied. Group 1 proceeded without
this carve-out; see `project_engine_reorg_group1_async_family.md` (memory folder) for what group 1
completed instead (rename + duplicate resolution) and its own deferral note.

## What was being attempted

Group 2 of the engine-reorganization plan (see `project_engine_reorg_group9_placement_fixes_complete.md`
and `project_engine_reorg_group3_in_progress.md` under the memory folder): folding all content of
`RoslynSentinel.Basic/SyntaxUpgradeEngine.cs` into the `SyntaxModernizationEngine` class in
`RoslynSentinel.Advanced/ModernizationEngine.cs`, via the `MoveMember` MCP tool
(`mcp__root_roslyn_sentinel_advanced_stdio__MoveMember`), so that `SyntaxUpgradeEngine.cs` ends up
empty and can be deleted.

`GetFileOutline` confirmed `SyntaxUpgradeEngine.cs` (1133 lines) has exactly 17 members: 12 public
async methods, 1 private helper (`IsAutoProperty`), and 5 private nested classes at lines 982-1131
(`FieldToParamRewriter`, `BracesRewriter`, `ModernGuardRewriter`, `PatternMatchingRewriter`,
`ImplicitSpanRewriter`) implementing Roslyn `CSharpSyntaxRewriter` for use by those methods. These
17 members are the entire content of the file - a fully successful move empties it.

## The exact symptom

Two separate `MoveMember` calls, both `dryRun: true`, both against `className:
"SyntaxUpgradeEngine"`, `filePath: "C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Basic\SyntaxUpgradeEngine.cs"`,
`targetClassName: "SyntaxModernizationEngine"`, `targetFilepath:
"C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Advanced\ModernizationEngine.cs"`:

1. `memberNames` containing only the 5 nested rewriter class names (no methods) returned
   immediately with `errorCode: "NotFound"`:

   > MoveMember failed: None of the requested member(s) [FieldToParamRewriter, BracesRewriter,
   > ModernGuardRewriter, PatternMatchingRewriter, ImplicitSpanRewriter] were found in class
   > 'SyntaxUpgradeEngine'.

   This is despite `GetFileOutline` confirming all 5 classes exist as nested types inside
   `SyntaxUpgradeEngine` at the lines noted above.

2. `memberNames` containing only the 12 async methods plus `IsAutoProperty` (the 5 nested classes
   deliberately excluded), with a `callSiteFixups` map independently verified to fully resolve
   (every value an in-scope receiver expression, e.g. `new SyntaxModernizationEngine(existingFieldOrLocal,
   config)` or an already-correct existing field such as `_modernizationEngine`/`_modernEngine`)
   for all 16 affected test-fixture files, failed with `errorCode: "ValidationFailed"` and 5
   post-move compiler errors:

   ```
   CS0246 at RoslynSentinel.Advanced/ModernizationEngine.cs:893: The type or namespace name 'ModernGuardRewriter' could not be found
   CS0246 at RoslynSentinel.Advanced/ModernizationEngine.cs:949: The type or namespace name 'BracesRewriter' could not be found
   CS0246 at RoslynSentinel.Advanced/ModernizationEngine.cs:994: The type or namespace name 'PatternMatchingRewriter' could not be found
   CS0246 at RoslynSentinel.Advanced/ModernizationEngine.cs:1192: The type or namespace name 'ImplicitSpanRewriter' could not be found
   CS0246 at RoslynSentinel.Advanced/ModernizationEngine.cs:1555: The type or namespace name 'FieldToParamRewriter' could not be found
   ```

   These are the same 5 errors at the same 5 line numbers as an earlier attempt that *did* include
   the nested classes in `memberNames` - i.e. including or excluding them from the request made no
   difference to the outcome.

## Where it happened

- Tool: `MoveMember` (`mcp__root_roslyn_sentinel_advanced_stdio__MoveMember`), both calls
  `dryRun: true`.
- Source: `RoslynSentinel.Basic/SyntaxUpgradeEngine.cs`, class `SyntaxUpgradeEngine`, nested
  rewriter classes at lines 982-1131.
- Target: `RoslynSentinel.Advanced/ModernizationEngine.cs`, class `SyntaxModernizationEngine`,
  post-move compile failures at lines 893, 949, 994, 1192, 1555.

## Root cause - traced to source behavior, confirmed by two isolated calls

`MoveMember`'s member-lookup does not recognize nested types as movable members at all: requesting
only the 5 nested class names, alone, with no methods in the request, fails immediately with
`NotFound` even though `GetFileOutline` proves they exist in the class. This rules out a name-
mismatch or member-not-found data problem - the tool's member enumeration for this operation simply
does not consider nested types candidates.

Separately, `MoveMember` pastes the moved methods' bodies into the target file verbatim without
detecting or carrying along private members of the source class that those bodies reference. Each
of the 12 methods constructs one of the 5 rewriter classes internally (e.g. `new
ModernGuardRewriter(...)` inside `UpgradeToModernGuardsAsync`'s body). After the move, the target
file's copy of the method body still says `new ModernGuardRewriter(...)`, but `ModernGuardRewriter`
itself was never moved or copied there (and, per the first finding, cannot be moved at all as a
named request) - so the post-move compile fails with `CS0246`. This was confirmed identically
whether or not the nested classes were included in the same `memberNames` list, which rules out "the
tool tried and failed to move them" as an explanation - they are never moved regardless, only ever
the cause of the methods' resulting compile failure.

Taken alone, without this two-step isolation, the `CS0246` errors are indistinguishable from an
unrelated missing-using-directive problem. They are not: they are the direct, traceable consequence
of a private in-class dependency of the moved code that `MoveMember` neither moves nor detects nor
refuses up front.

## Ruled out

- **Not a `callSiteFixups` correctness problem.** Every fixup value was independently confirmed to
  be a valid, in-scope receiver expression before the second call; the same 5 `CS0246` errors occur
  regardless of `callSiteFixups` content, and are about missing *types*, not malformed call sites.
- **Not a missing `using` directive at the target file**, for this specific finding. A real
  using-directive gap was found and fixed independently in the same investigation (see "Related
  finding" below), but it produced a distinct error (`CS0103: 'Formatter' does not exist in the
  current context`) that was resolved separately and is not present in the `ValidationFailed`
  result being documented here.
- **Not affected by whether the nested classes are named in `memberNames`.** Identical 5 errors at
  identical line numbers with and without them in the request.
- **No file was modified by this investigation.** Every `MoveMember` call was `dryRun: true`; both
  `SyntaxUpgradeEngine.cs` and `ModernizationEngine.cs` remain in their pre-move state except for
  one unrelated, already-applied real edit (see "Related finding" below).

## Related finding: missing `using` directive for a moved API (resolved in passing, same investigation)

An earlier dry run in this same investigation separately surfaced `CS0103: 'Formatter' does not
exist in the current context` at `ModernizationEngine.cs:1352`, because
`UseFieldBackedPropertiesAsync`'s body calls `Formatter.FormatAsync` (from
`Microsoft.CodeAnalysis.Formatting`); the source file has `using Microsoft.CodeAnalysis.Formatting;`
but the target file did not. This was fixed for real (not a dry run) by calling `UsingDirective(operation:
"add", namespaceName: "Microsoft.CodeAnalysis.Formatting", filePath: <target file>)` before
retrying, and that edit should stay. This is a narrower, already-resolved instance of the same
general class of gap - `MoveMember` does not carry the using directives a moved method body's API
calls require - but it is not the nested-class dependency bug this doc is about; it is noted here
only so a future session does not re-diagnose it.

## Related finding: `callSiteFixups` silently accepts a bare type name as if it were a receiver expression

Also surfaced in this investigation, and worth flagging separately from the primary nested-class
bug: passing `callSiteFixups: {"*": "SyntaxModernizationEngine"}` (a bare type name, not a receiver
expression) was silently accepted and applied literally, producing invalid static-style calls like
`SyntaxModernizationEngine.UpgradeToModernGuardsAsync(...)` at 30 call sites, which then correctly
failed compilation with `CS0120` ("An object reference is required for the non-static field,
method, or property"). The `callSiteFixups` parameter's schema description does say the value
should be "a receiver expression," but a bare type name is an easy mistake to make, especially for
a smaller/weaker model, since a static-class-name prefix is syntactically similar to a receiver
expression in C#. This is a documentation/validation clarity gap, not the subject of this doc -
`MoveMember` should probably reject a bare type identifier used as a `callSiteFixups` value up
front, naming the problem, rather than emitting it into invalid call sites for the compiler to
reject later.

## Why this blocks (per CLAUDE.md failure doctrine)

Group 2 of the engine-reorganization plan requires *all* of `SyntaxUpgradeEngine`'s content to move
so the source file can be deleted. 5 of its 17 members are private nested types that `MoveMember`'s
member lookup does not recognize as movable at all, and the remaining 12 methods that depend on
those nested types cannot be moved cleanly either - they compile-fail immediately afterward,
regardless of whether the nested types are also named in the request. There is currently no
`MoveMember`-only path to complete this fold-in.

No safe tool-based workaround exists either: manually duplicating the 5 nested classes' source into
the target file via `Member(addTopLevelType)` while relying on `MoveMember` only for the 12 methods
would fragment a single logical move across two different mechanisms, risking drift or double
declaration, and was judged out of scope for this session under CLAUDE.md's standing instruction not
to route around a tool failure. Per the failure doctrine, this is squarely an environment gap: the
tool has no dependency-detection step for a moved member's body referencing another private member
of the same source class, and its error surface (a downstream, post-move `CS0246`) does not name the
actual problem - an un-moved private dependency of code that already moved - forcing that
determination to be reconstructed by hand across two isolated dry runs rather than reported directly.

## What unblocks it

- `MoveMember` should detect, before or during the move, when a moved member's body references a
  private/internal member of the source class (including nested types) that is not itself being
  moved, and either:
  - auto-include that dependency in the move if it has no other referents remaining in the source
    class, or
  - refuse up front with a specific error naming the missing type/member and suggesting it be added
    to `memberNames`, rather than proceeding to a post-move compile and surfacing a generic
    `CS0246`.
- Separately, `MoveMember`'s member-lookup should recognize nested types (classes/structs/etc.) as
  valid, independently movable members - today, requesting one by name alone fails with `NotFound`
  as if it did not exist in the class at all.
- Until fixed, this specific fold-in (`SyntaxUpgradeEngine` into `SyntaxModernizationEngine`) is
  blocked. Do not attempt it via a `Read`/`Edit`/`Write` bypass on the `.cs` files - per CLAUDE.md,
  a tool failure is a blocking finding, not license to route around the tool.
- Once `MoveMember` is fixed (or an explicit user-approved manual-merge procedure is agreed), the
  end goal is unchanged: move all 17 members so `SyntaxUpgradeEngine.cs` becomes empty and can be
  deleted, per the plan in `project_engine_reorg_group3_in_progress.md`.
- No cleanup is required to resume: no dry-run call wrote anything to disk. The one real edit made
  during this investigation - the added `using Microsoft.CodeAnalysis.Formatting;` in
  `ModernizationEngine.cs` (see "Related finding" above) - is correct and should be kept regardless
  of how this bug is eventually fixed.

## Corroborating reproduction (group 1, async family, 2026-09-28)

Isolated exactly as the original finding recommends ("two isolated dry runs"): two separate
`MoveMember` calls, both `dryRun: true`, both against `filepath:
"RoslynSentinel.Advanced/AsyncOptimizationEngine.cs"`, `className: "AsyncOptimizationEngine"`,
`targetClassName: "AsyncMigrationEngine"` (a not-yet-existing class, so `MoveMember` would need to
create it):

1. `memberNames: ["FlagMigrationCandidateEngineResult"]` (a `public record` with primary-constructor
   parameters, each carrying an XML `<summary>` doc comment) - immediate `NotFound`:
   > MoveMember failed: None of the requested member(s) [FlagMigrationCandidateEngineResult] were
   > found in class 'AsyncOptimizationEngine'.
2. `memberNames: ["RemovedCandidateInfo"]` (a plain one-line positional `record`, no doc comments,
   as a control to rule out the XML-doc-comment-on-parameter syntax confusing the parser) - same
   immediate `NotFound` with the same message shape.

Both types are confirmed present by `GetFileOutline` (`FlagMigrationCandidateEngineResult` at line
1661, `RemovedCandidateInfo` at line 3602). A separate larger batch call requesting 15 members
together (9 methods + 6 nested types: the above two records plus `CandidateScoredItem`,
`FlagCandidatesInProjectEngineResult`, `RemoveMigrationCandidatesEngineResult`,
`MigrationCandidateRemover`) did not raise `NotFound` - it proceeded to `ValidationFailed` instead,
because the 9 methods in the same request WERE found and moved, and the tool silently dropped the 6
type names from the request rather than failing the whole call; the resulting staged
`AsyncMigrationEngine.cs` then had methods whose bodies constructed/returned those record/class
types with no declaration for them anywhere in the new file, i.e. `CS0246` at the exact lines where
each method's body references its own return-type record. This confirms the same "silently ignored,
not attempted, not reported" behavior noted in the original finding's second bullet, now shown to
extend to `public record` types as well as `private class` types - the defect is on the member-kind
filter itself, independent of accessibility or `class` vs `record`.

## Related

- `project_engine_reorg_group9_placement_fixes_complete.md` and
  `project_engine_reorg_group3_in_progress.md` (memory folder) - the plan context this blocks,
  including the note that the `ImmutabilityEngine` -> `SyntaxModernizationEngine` fold-in is
  deferred to this same group 2.
- `project_engine_reorg_group1_async_family.md` (memory folder) - group 1's corroborating
  reproduction against nested `record` types, and what group 1 completed instead.
- `docs/current/blockers/blocking_error_build_tool_suppresses_cs0618_warnings.md` - used as the
  format/style reference for this doc; unrelated root cause.

# No MCP tool can create a fully-populated, multi-top-level-type `.cs` file in one atomic, whole-solution-validated step -- `CreateFile`/`Member` deadlock when moving a type with pre-existing external references

**Status:** RESOLVED (2026-09-20) using `WriteFile(operation: CreateFile)`, which was overlooked when
this doc was first written -- it accepts full verbatim file content (any number of top-level types)
in one call with a single validation pass, exactly the missing capability described below. Corrected
by direct user steer: "There should be a writefile tool that will allow creating an entire cs file"
followed by "the gating is only for weaker models" / "claude sessions run with the gated tools
enabled" -- `WriteFile` lives on `SentinelWholeFileWriteTools`, which is deliberately gated off for
weak-model eval runs (see the `WriteToolAdviceHelper` remarks below) but is enabled in ordinary Claude
Code sessions, and was in fact reachable via `ToolSearch` the whole time. The root-cause analysis
below (CreateFile/Member's per-call whole-solution validation with no staged/atomic multi-type path)
remains accurate for those two tools specifically and is left intact as a real, narrower gap -- it
just was not, in this instance, a full blocker, because a third tool already covered the case.
`RoslynSentinel.Common/WriteToolAdviceHelper.cs` now exists with its full original content (verified
via `Build(level: quickBuild)`: 0 errors, 7 pre-existing unrelated warnings).

## What was being attempted

Per `docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md`, moving
`RoslynSentinel.Server.Basic/WriteToolAdviceHelper.cs` -- a small, dependency-free helper (one enum
`WriteEscapeRoute`, one record `WriteAdvice`, one class `WriteToolAdviceHelper`) -- into
`RoslynSentinel.Common`, because `WorkspaceFileEditImpl.cs` (`RoslynSentinel.Basic`) needs it for its
`ReplaceSnippet`/`CreateFile` method bodies as part of finishing the facade-delegation split that
plan describes, and `RoslynSentinel.Basic` cannot reference `RoslynSentinel.Server.Basic` without
inverting the established `Common <- Basic <- Advanced` dependency direction (confirmed via
`ListSolutionItems(kind: "dependencies")` on both projects -- `RoslynSentinel.Basic`'s project
references do not include `RoslynSentinel.Server.Basic`, and adding one would create the inversion).

The intended sequence was: `DeleteFile` the old location, then recreate the same content (all three
declarations) at the new location in `RoslynSentinel.Common`, then update the one live consumer
(`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`, field `_writeAdvice` at line 37, comments at
lines 186 and 280) to reference the new namespace.

## The exact gap

Two tools exist for creating file content; neither can do this:

- **`CreateFile`**: seeds exactly one empty top-level type (enum/class/record/etc) per call, and per
  its own documented contract, validates the **entire solution's** compile state before writing to
  disk. A call for a second/third type in the same intended file, or a call that leaves the file in
  an incomplete intermediate state, is rejected with a message to the effect of "this content would
  introduce new compiler errors -- not written to disk." Confirmed the rejection is not a partial
  write: after a rejected `CreateFile` call, `GetFileOutline`/`ListExternalDiskChanges` show the file
  does not exist on disk at all -- not even in a rejected/partial form.
- **`Member(operation: addMember / addTopLevelType)`**: requires an **existing** document to attach
  to -- calling it against a file that does not yet exist returns an error (`NotFound`/
  `DocumentNotFound`-shaped) rather than creating one. Each call is independently, transitively
  validated against the whole solution, same as `CreateFile`.

Both gates are correct in isolation (neither should let a genuinely broken intermediate state land),
but together they produce a deadlock for this specific, legitimate case: a type being moved that
already has real, pre-existing external references elsewhere in the solution.

## The deadlock, concretely

`WriteToolAdviceHelper`'s full, finished API surface -- constructor taking `IEnumerable<string>`,
static `WithAllToolsExposed()`, instance `IsExposed(string)`, instance `AdviseForOversizedEdit(string)`,
the 4-member `WriteEscapeRoute` enum, and the `WriteAdvice` record -- is already referenced by
existing test files that predate this move and were never asked to change:
`WriteToolAdviceHelperTests.cs`, `ComprehensiveToolTests.cs`, `GetScanResultTests.cs`,
`BatteryTwentyTests.cs`, `CreateFileDeleteFileTests.cs`, `GetOperationDetailTests.cs`,
`ListSolutionItemsAllTests.cs`, `ListProjectFrameworkTargetsTests.cs`,
`MutatingToolRejectionMessageTests.cs`, `GetMethodSourceTests.cs`, `ReadFileTests.cs`,
`ReplaceSnippetBatchTests.cs`, `ReplaceSnippetSizeGuardTests.cs`, `RunTestTests.cs`,
`UndoLastApplyTests.cs`, `ApplyDiffSizeGuardTests.cs`, and `MigrationScanResultTests.cs` (full list
confirmed via `SearchSolutionText`-equivalent grep for `WriteToolAdviceHelper|WriteEscapeRoute|
WriteAdvice\b`, 22 files matched total, of which the above are the ones asserting against the type's
members rather than just the live consumer/registration wiring). The one live non-test consumer is
`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` (field `_writeAdvice`, line 37).

Because that reference set already expects the **complete** API (all 4 members, both types, the full
enum), any intermediate state of a newly-created file -- an empty class scaffold, a class with only
some members present -- triggers `CS0117`/`CS1729`/`CS1061` from those pre-existing test files and is
rejected by `CreateFile`'s/`Member`'s whole-solution validation. There is no sequence of
`CreateFile`-then-`Member`-add-member calls that passes through a valid intermediate state, because
every state before "fully complete" is, by definition, missing something 22 other files already
reference. The file must go from "does not exist" to "fully complete, matching every existing
caller's expectations" in a single write, and no available tool performs a single write of more than
one empty top-level type.

## Current build state

The old file was already deleted via `DeleteFile` before this gap was discovered (i.e. the move was
attempted in the doctrine-correct order -- old location removed first, new location was to follow --
and the deadlock was hit while trying to write the new location). The type currently exists **nowhere**
in the solution.

**Verified via a live `Build(level: quickBuild, scope: solution)` call** (superseding the static-
analysis approximation this doc originally shipped with): **45 errors, 7 warnings**. `errorSummary`
groups them into exactly two diagnostics, matching this doc's prediction precisely:

- `CS0246` ("The type or namespace name 'WriteToolAdviceHelper' could not be found") -- **24
  occurrences**, including `RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs:37` (the `_writeAdvice`
  field) and `:66` (the constructor parameter), `RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs:142`
  (the DI registration `new WriteToolAdviceHelper(activeToolClasses)`), and one usage site each in
  `MigrationScanResultTests.cs`, `ReplaceSnippetSizeGuardTests.cs` (x3), `ApplyDiffSizeGuardTests.cs`,
  `UndoLastApplyTests.cs`, `RunTestTests.cs`, `ReplaceSnippetBatchTests.cs`, `ReadFileTests.cs`,
  `MutatingToolRejectionMessageTests.cs`, `BatteryTwentyTests.cs`, `CreateFileDeleteFileTests.cs`,
  `GetMethodSourceTests.cs`, `ListProjectFrameworkTargetsTests.cs`, `ListSolutionItemsAllTests.cs`,
  `GetOperationDetailTests.cs`, and `WriteToolAdviceHelperTests.cs` (x4, plus 3 `CS0103` for
  `WriteEscapeRoute` in the same file).
- `CS0103` ("The name 'WriteToolAdviceHelper'/'WriteEscapeRoute' does not exist in the current
  context") -- **21 occurrences**, one per test file at its `WriteToolAdviceHelper.WithAllToolsExposed()`
  call site (`ComprehensiveToolTests.cs:140`, `GetScanResultTests.cs:51`, etc.) plus the 3
  `WriteEscapeRoute` enum-member references inside `WriteToolAdviceHelperTests.cs`.

The 7 warnings (`CS8601`/`CS8604`, nullable-reference-possible-null in `RefactoringStructuralImpl.cs`)
are pre-existing and unrelated to this gap -- confirmed by their file (not `WriteToolAdviceHelper.cs`
or any of its consumers) and diagnostic class (nullability, not missing-type).

This confirms the doc's original static-analysis-derived file list was accurate in shape (same two
diagnostic ids, same consumer/test-file set) and the build is exactly as broken as predicted, not
worse or differently broken.

## Root cause

Not a single faulty code branch -- this is an absent capability, confirmed by reading both tools'
documented contracts and by direct trial-and-error against both:

- `CreateFile`'s whole-solution transitive validation (rejects on any new compiler error anywhere,
  all-or-nothing, no partial write) has no escape hatch -- no `skipValidation`/`force`/
  `deferValidation`/staged-multi-op flag -- for the legitimate case of recreating a type that already
  has real external references, where validation can only ever pass once the *entire* new file is
  present in one shot.
- `Member(addMember/addTopLevelType)` requires a pre-existing document, so it cannot be the first
  write for a brand-new file, and even if it could, it shares the same per-call whole-solution
  validation gate as `CreateFile`, so chaining several `Member` calls after some future
  `CreateFile`-with-first-type-only would still fail at every intermediate call.
- This is **not specific to `WriteToolAdviceHelper`**. It will recur for any future `*Impl` class
  that needs a helper type currently sitting in the wrong project relative to the established
  `Common <- Basic <- Advanced` dependency direction, whenever that helper already has pre-existing
  external references (tests or otherwise) that expect its complete surface. The trigger condition is
  generic: moving any multi-declaration or single-but-externally-depended-on type to a new file/
  project in one MCP-tool-mediated step.

## What's confirmed vs. not

- Confirmed: `CreateFile` seeds exactly one top-level type per call (tried against this exact file).
- Confirmed: a rejected `CreateFile` call leaves no trace on disk (checked via `GetFileOutline` and
  `ListExternalDiskChanges` immediately after a rejection).
- Confirmed: `Member(addMember/addTopLevelType)` requires an existing document (`NotFound`/
  `DocumentNotFound`-shaped error observed when targeting a not-yet-created file).
- Confirmed: 22 files in the solution reference `WriteToolAdviceHelper`/`WriteEscapeRoute`/
  `WriteAdvice` by name (grep-confirmed, not assumed); of those, 16 assert against the type's members
  and would fail to compile against any incomplete recreation.
- **Not tested**: whether `Member`'s `skipPrecheck` parameter (if it exists on that tool's schema --
  referenced here from the task briefing, not independently confirmed by reading the tool's schema
  in this session) actually suppresses the *transitive whole-solution* validation, as opposed to only
  some narrower per-call check. If `skipPrecheck: true` genuinely lets a `Member` call land without
  triggering `CS0117`/`CS1729`/`CS1061` from the 16 dependent test files mid-sequence, that would be a
  working incremental path and would significantly narrow this write-up's scope. **Whoever resumes
  this should try `skipPrecheck: true` on a `Member(addMember)` call against an intermediate state
  before treating the "no escape hatch" conclusion above as final.**
- Not traced to source: no tool implementation file/line has been read for this doc (there is no
  specific faulty branch to cite -- the gap is an absence of a capability, not a bug in existing
  code). Nothing here should be read as a claim about a specific line of `RefactoringEngine.cs` or
  `SentinelRefactoringTools.cs`; that would misrepresent a capability gap as a code defect.

## Why this blocks (per CLAUDE.md failure doctrine)

Per CLAUDE.md: "If a needed MCP tool fails, returns wrong data, is unreachable, or has no equivalent
operation: finish any in-flight edit, stop advancing the task, write a blocker doc, and end the turn."
Here, no available tool has an equivalent operation for "write a complete, multi-declaration,
externally-depended-on file in one validated step." The only way to route around it would be a raw
`Write`/`Edit` on the `.cs` file, which the dog-fooding policy and its `PreToolUse` hook
(`.claude/hooks/enforce-dogfood.ps1`) exist specifically to prevent for exactly this reason -- an
awkward or missing tool path is the finding, not license to bypass it.

## What would resolve this

Per the "what change to the environment would have prevented this" framing (CLAUDE.md's failure
doctrine), in rough order of how directly each closes the gap:

1. **A `CreateFile`/`Member` variant that accepts a complete, verbatim, multi-top-level-type file
   body in one call and validates once**, scoped to net-new files only (never overwrites an existing
   file, so it cannot be misused as an uncontrolled whole-file replace). This is analogous to how
   `WriteFile` already works elsewhere in this codebase per `SentinelWholeFileWriteTools`, but that
   tool family is understood to be gated/restricted precisely because unrestricted whole-file writes
   bypass Roslyn-level guarantees -- a narrower "create, not overwrite, one validation pass at the
   end" variant would not need the same restrictions, since there is no existing content it could
   destroy.
2. **A staged-multi-op mode**: let several `Member`/`CreateFile` operations targeting the same new
   file be queued and validated only once, after the last operation in the stage, rather than after
   every individual call. This would let a file be built up top-level-type-by-top-level-type or
   member-by-member without every intermediate state needing to independently compile.
3. **Confirm and document whether `skipPrecheck: true` on `Member` suppresses transitive
   whole-solution validation** (see "What's confirmed vs. not" above) -- if it does, this is already
   a working, if undocumented, incremental path and the fix is a documentation/description update
   (name it explicitly in `Member`'s tool description as the intended mechanism for exactly this
   move-a-type scenario) rather than new tool surface.
4. Regardless of which of the above lands, the `CreateFile` rejection message ("this content would
   introduce new compiler errors -- not written to disk") should name *which* files/errors it found,
   the way a good error message should per CLAUDE.md's root-cause-discipline section -- currently it
   is a bare rejection with no path to recovery beyond guessing.

## Immediate recovery need

The build is presently broken (type exists nowhere in the solution) as the direct result of stopping
mid-move per the dog-fooding "finish any in-flight edit, stop advancing the task" instruction --
`DeleteFile` had already been applied to the old location before this gap was discovered, and no
tool path exists to complete the move as an alternative "in-flight edit" to finish instead. Two ways
to restore a working state, either acceptable, whichever an approved plan picks:

- **Recreate `RoslynSentinel.Server.Basic/WriteToolAdviceHelper.cs`** with its original content (the
  original source was known immediately prior to deletion and can be supplied in a follow-up
  message if not otherwise recoverable from git history) to restore the build and defer the
  Common-move to whenever item 1, 2, or 3 above lands, **or**
- **Find a working incremental path via `skipPrecheck`** (see "Not tested" above) and complete the
  original move using it, if it turns out to actually suppress the transitive validation that
  produced the deadlock.

Either way, this needs an approved plan before any further mutating tool call, per
`feedback_get_approval_before_editing` (project memory) and CLAUDE.md's blocker-doc policy ("do not
resume until told the issue is fixed").

## Related

- `docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md` -- the plan this move was
  part of (finishing the facade-delegation half of Decision 3 for `SentinelWorkspaceTools.cs`'s
  `ReplaceSnippet`/`CreateFile` bodies, which is where `WorkspaceFileEditImpl.cs`'s need for this
  helper originates).
- `docs/current/blockers/resolved/blocking_error_member_add_multi_type_silent_drop.md` and
  `blocking_error_member_addmember_silent_partial_write.md` -- different `Member` defects (silent
  truncation via `ParseMemberDeclaration` on multi-declaration input), same tool family, but those
  are single-declaration-parsing bugs with a traced source line; this doc is a capability gap with no
  single faulty line, not the same root cause.

# `ChangeSignature` reports `success: true` / `status: "no_changes"` on a valid, unambiguous constructor with no compiler errors, and writes nothing

**Status:** OPEN - found 2026-09-18 (overnight session), while implementing Decision 1 of
`docs/current/plans/plan_scoped_operation_ledger.md`. Not a hard stop: this session had explicit
overnight authorization for narrow tool-bypass-plus-blocker-doc when an MCP tool is broken, rather
than halting entirely. The blocked edit was completed via the `Member`/`ReplaceSnippet` tools instead
and the session continued. This doc is for morning review, not a currently-open blocker on further
work.

## What was being attempted

Adding an `IScopedOperationLedger` dependency to the `PersistentWorkspaceManager` constructor in
`RoslynSentinel.Common/PersistentWorkspaceManager.cs`, originally at lines 188-203 (single-line
signature `public PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger)`). The target was
confirmed unambiguous before touching anything: `GetMethodSource` and `LocateSymbol` both showed a
single overload, no compile errors, no naming conflicts.

## Call 1 (dry run)

```
ChangeSignature(reason: "add IScopedOperationLedger dependency to PersistentWorkspaceManager ctor",
  filepath: "RoslynSentinel.Common/PersistentWorkspaceManager.cs",
  methodName: "PersistentWorkspaceManager",
  parameters: [{"originalIndex": 0}, {"name": "ledger", "type": "IScopedOperationLedger", "defaultValue": "null!"}],
  dryRun: true)
```

Result:

```json
{"affectedFiles":[],"description":"Reorders parameters of 'PersistentWorkspaceManager' in PersistentWorkspaceManager.cs.","dryRun":true,"status":"dry_run_ok","note":"Validated - introduces no new compiler errors. Not written to disk (dryRun=true). Re-call with dryRun=false to apply."}
```

`affectedFiles: []` on a dry run that both named the correct method and file in its own
`description` is itself the first signal something was wrong - a dry run against a real, concretely
existing constructor should list at least the declaring file.

## Call 2 (real, same parameters, `dryRun: false`, `returnDiff: true`)

```json
{"affectedFiles":[],"description":"Reorders parameters of 'PersistentWorkspaceManager' in PersistentWorkspaceManager.cs.","dryRun":false,"diff":"","status":"no_changes","note":"No changes were produced, so nothing was written and there is nothing to undo. If you expected a change, the operation matched no target - or its refactoring feature is disabled on this server (check the Features tool)."}
```

## Verification performed before concluding this was a defect

1. **Re-read the constructor via `ReadFile` immediately after Call 2.** Completely unchanged - still
   the original single-parameter signature. The tool did not silently write something wrong; it
   silently wrote nothing, while returning a success-shaped result (`success: true`,
   `status: "no_changes"`) rather than an error.
2. **Checked `Features(action: "get", names: ["ChangeSignature"])`**, since the tool's own note
   suggested "its refactoring feature is disabled on this server" as a possible cause. Result:
   `{"key":"ChangeSignature","value":true}`. The feature is enabled - the note's own suggested cause
   is ruled out, not just unconfirmed.

## Root cause - hypothesis only, not traced to source this session

**This has not been confirmed by reading `ChangeSignature`'s C# implementation.** What follows is a
working hypothesis built from indirect evidence, labeled as such per this repo's root-cause
discipline.

While working around this defect, a manual `ReplaceSnippet` attempt at the same edit (made before
the `_ledger` field existed) correctly and loudly failed with real compiler errors -
`CS0103`/`CS7036` - naming call sites in `RoslynSentinel.Tests.Advanced/BugHuntSevenTests.cs` doing
`new PersistentWorkspaceManager(logger)` directly (single-argument, non-DI, non-named-argument). At
least 5 such call sites exist, around lines 34, 163, 289, 393, and one in a `Bug578Re...`-prefixed
test method - the full line list was in the truncated `ReplaceSnippet` error text and was not fully
captured this session.

`ChangeSignature`'s own tool description states it updates call sites across the solution,
including omitted-optional-argument call sites, using the supplied `defaultValue` to fill in new
required-looking parameters. The working hypothesis is: `ChangeSignature` found these direct
`BugHuntSevenTests.cs` call sites, could not safely rewrite them for some reason internal to its
call-site-rewrite logic, and instead of surfacing that as an error, treated "found call sites I
can't handle" the same as "found nothing to do" - collapsing two very different internal states into
the same `status: "no_changes"` output.

This is unconfirmed. No one has read the `ChangeSignature` tool implementation to find the specific
branch that produces `status: "no_changes"`, or to check whether it distinguishes "zero matching
call sites" from "call sites found but rewrite failed/skipped."

## Where it happened

- Target method: `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `PersistentWorkspaceManager`
  constructor, originally lines 188-203.
- Tool: `ChangeSignature` (both dry-run and real calls, identical parameters aside from `dryRun`).
- Likely-implicated call sites (unconfirmed as the actual cause, see above):
  `RoslynSentinel.Tests.Advanced/BugHuntSevenTests.cs`, at least 5 direct
  `new PersistentWorkspaceManager(logger)` constructions, around lines 34, 163, 289, 393, and one
  `Bug578Re...`-prefixed test.

## Why this is a blocking finding, not a footnote

Per `CLAUDE.md`'s failure doctrine, a `success: true` result with an innocuous-sounding
`status: "no_changes"` is indistinguishable, to a calling model, from "the refactor was genuinely a
no-op because nothing needed to change" - a completely different and far more dangerous situation to
be silently told, especially when the tool's own `description` field correctly named the real target
method in both calls, proving it was matched, not missed. A weaker or local model has no structural
reason to double back and verify the file was untouched after a `success: true` response; it would
proceed as though the parameter had been added, producing cascading failures at every dependent step
downstream. This is exactly the "novice model, expert environment" failure this repo's doctrine
assigns to the environment, not the caller.

The contrast with `ReplaceSnippet`'s behavior on the same underlying conflict is worth stating
explicitly: `ReplaceSnippet`, hitting the same `BugHuntSevenTests.cs` call sites via a different code
path, failed loudly with real, actionable `CS0103`/`CS7036` errors naming exact files. That is the
failure-doctrine-compliant behavior. `ChangeSignature`'s silent no-op on what is very likely the same
underlying conflict is the defect.

## Workaround used (this session)

`ChangeSignature` was not used for this edit. Instead:

1. Added the `_ledger` field manually via `Member(addMember)`.
2. Manually rewrote the constructor signature and body via `ReplaceSnippet`, deliberately choosing an
   **optional** parameter - `IScopedOperationLedger? ledger = null` with
   `_ledger = ledger ?? new ScopedOperationLedgerEngine();` in the body - specifically so the existing
   `BugHuntSevenTests.cs` call sites would keep compiling with zero changes needed there. This design
   choice (optional vs. required parameter) was made *because of* this defect: a required parameter
   would have forced manually touching those 5+ test call sites one at a time.

`ReplaceSnippet`'s first attempt at this edit (before `_ledger` existed) was correctly rejected with
real compiler errors, which is what led to discovering the likely-implicated call sites in the first
place.

## What unblocks it

A maintainer needs to read the `ChangeSignature` tool's implementation and find the specific branch
that produces `status: "no_changes"`, to determine whether it is genuinely "zero call sites matched"
or a caught/swallowed failure from the call-site-rewrite step. Two possible fix directions, not
mutually exclusive, not overspecified here:

1. `ChangeSignature` should actually rewrite the found call sites using the supplied `defaultValue`
   as the fill-in argument for the new parameter, per its own tool description's stated behavior; or
2. if it structurally cannot rewrite a given call site's shape, it should return a real error naming
   which call site(s) blocked it and why - never a bare `status: "no_changes"` for a target whose own
   `description` field proves it was found and matched.

Either way, `status: "no_changes"` should be reserved for the case where the search genuinely found
no call sites and no target-signature difference to apply - not used as a catch-all for "something
about applying this was skipped."

## Related

- `docs/current/blockers/blocking_error_changesignature_internal_roslyn_api.md` - a different,
  already-confirmed `ChangeSignature` defect (the real Roslyn change-signature engine is entirely
  `internal` in Microsoft.CodeAnalysis.Features 5.9.0, blocking a proposed migration away from the
  hand-rolled engine). That doc's target, `RoslynSentinel.Basic/RefactoringEngine.cs:90-223`
  (`ChangeSignatureAsync`), is presumably the same hand-rolled implementation this session's call
  went through - worth checking whether the same file's call-site-rewrite logic explains both
  findings, though this has not been verified.
- `docs/current/plans/plan_scoped_operation_ledger.md`, Decision 1 - the task this defect was found
  during.
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs` - final state uses the optional-parameter
  workaround described above, not `ChangeSignature`.
- `RoslynSentinel.Tests.Advanced/BugHuntSevenTests.cs` - likely-implicated call sites (unconfirmed as
  the actual cause).

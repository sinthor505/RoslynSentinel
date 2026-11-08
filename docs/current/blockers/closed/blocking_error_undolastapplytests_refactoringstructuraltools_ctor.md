# CS1729 on `RefactoringStructuralTools` ctor arity at `UndoLastApplyTests.cs:283` does not match
any source in the tree - line points at an `Assert.That`, not a constructor call

**Status:** RESOLVED (stale build artifact, confirmed 2026-09-25). After a VS Code restart spawned a
fresh MCP server process (new binary path `bin-vscode\d87a019a-fdaec881\...`, new PID) and the
solution was reloaded from scratch, a full `Build` succeeded with 0 errors / 0 warnings across all
14 projects including `RoslynSentinel.Tests.Battery`. No source file referenced in this doc was
edited between the failing build and this one. This confirms hypothesis 1 in "Open question" below:
the prior server process was running against a stale compiled assembly, not current source. No code
fix was needed.

**Status (original, kept for history):** OPEN. Root cause NOT yet traced to a source-level defect -
every construction site for `RefactoringStructuralTools` in the current working tree is correctly
shaped. The reported file:line does not contain the offending code. This looks like a
stale-build/stale-line-number symptom (see "What's been ruled out" and "Open question" below), but
that has not been confirmed against the actual build log or a clean rebuild, so it is labeled a
hypothesis, not a finding.

## What was being attempted

A full-solution `Build` (per CLAUDE.md's dogfooding chokepoint - `Bash(dotnet build/test)` ->
`Build`), unrelated to any edit made in the current session. The build surfaced:

```
CS1729: 'RefactoringStructuralTools' does not contain a constructor that takes 6 arguments
File: c:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Battery\UndoLastApplyTests.cs:283
```

This doc is a report-only trace of that error per CLAUDE.md's "any unhandled CS#### surfacing during
automated work gets a writeup immediately" rule and its root-cause discipline (never restate the
surface error as the cause). No fix was applied; no test file was edited.

## What is actually at the cited location

`RoslynSentinel.Tests.Battery/UndoLastApplyTests.cs:283`, read directly:

```csharp
281:        var undoResult = await workspaceTools.UndoLastApply(reason: "test message", changeId: changeId!);
282:
283:        Assert.That(undoResult.IsSuccess, Is.True, $"Expected undo to succeed; error: {undoResult.ErrorData?.Message}");
```

Line 283 is an NUnit assertion. It contains no `new RefactoringStructuralTools(...)` call and no
reference to `RefactoringStructuralTools` at all. The only construction of that type in this test
method is nine lines earlier, at line 269:

```csharp
269:        var structuralTools = new RefactoringStructuralTools(new RefactoringStructuralImpl(refactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralImpl>.Instance));
```

This is a **1-argument** call to `RefactoringStructuralTools` (the single `RefactoringStructuralImpl`
instance produced by the inner `new RefactoringStructuralImpl(...)`, which is itself the one that
takes 6 arguments). This is exactly the correctly-split Tools/Impl shape the CS1729 claims is
missing - just on the wrong type in the error text, if the line number is to be believed.

## Constructor arity actually on disk

`RoslynSentinel.Server.Basic/RefactoringStructuralTools.cs:101-104`:

```csharp
public RefactoringStructuralTools(RefactoringStructuralImpl impl)
{
    _impl = impl;
}
```

One constructor, one parameter. There is only one `RefactoringStructuralTools` class in the solution
(confirmed via a solution-wide grep for `class RefactoringStructuralTools` - single hit, this file).

`RoslynSentinel.Basic/RefactoringStructuralImpl.cs:29-35` - the type that genuinely has 6 ctor
parameters:

```csharp
public RefactoringStructuralImpl(
    RefactoringEngine refactoringEngine,
    StructuralRefinementEngine structuralRefinementEngine,
    SymbolNavigationEngine symbolNavigationEngine,
    IWorkspaceManager workspaceManager,
    ValidationEngine validationEngine,
    ILogger logger)
```

Six parameters - this is almost certainly the "6 arguments" the compiler is complaining about, but
attached to the wrong type name if the message is read literally. That mismatch itself needs
explaining, not just noting.

## Every construction site checked, all correctly shaped

Grepped the whole solution for `new RefactoringStructuralTools\(` (7 hits, one per test fixture) and
read each call site in full:

- `RoslynSentinel.Tests.Battery/UndoLastApplyTests.cs:269`
- `RoslynSentinel.Tests.Advanced/MassiveRefactoringTests.cs:37-43`
- `RoslynSentinel.Tests.Battery/ModifyModifierBatchTests.cs:32-38`
- `RoslynSentinel.Tests.Battery/ModifyBaseTypeBatchTests.cs:63-69`
- `RoslynSentinel.Tests.Battery/ModifyAttributeBatchTests.cs:44-50`
- `RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs:426-432`
- `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs:131-137`

Every single one nests the 6-argument `RefactoringStructuralImpl` call inside a 1-argument
`RefactoringStructuralTools(...)` call, in the same order as the current constructor signatures. None
of them pass 6 arguments directly to `RefactoringStructuralTools`. There is no call site in this
working tree that would produce this CS1729 as literally worded.

## What's been ruled out

- **Not a bad edit to `UndoLastApplyTests.cs` itself in this session** - `git status` (via the
  harness-provided snapshot at conversation start) does not list this file as modified; the content
  read above is what's on disk right now, matching the described "pre-existing" framing.
- **Not a DI-wiring change from the two files that ARE modified-unstaged**
  (`RoslynSentinel.Common/IWorkspaceReader.cs`, `RoslynSentinel.Common/PersistentWorkspaceManager.cs`).
  `IWorkspaceReader.cs` only adds a new interface (`IWorkspaceReader`, `ReadSource` enum) - it does
  not touch `RefactoringStructuralTools`, `RefactoringStructuralImpl`, or any type in their
  constructor parameter lists. `PersistentWorkspaceManager`'s own constructor
  (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:152`,
  `public PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger, IScopedOperationLedger? ledger = null)`)
  is unchanged in arity and matches every `new PersistentWorkspaceManager(...)` call site seen in
  these tests.
- **Not a second/duplicate `RefactoringStructuralTools` type in another namespace** - solution-wide
  grep for the class declaration returns exactly one match
  (`RoslynSentinel.Server.Basic/RefactoringStructuralTools.cs:97`).
- **Not `WorkspaceTools`'s constructor being confused for this one** - `UndoLastApplyTests.cs`'s
  local `BuildTools` helper (line 53) builds a `WorkspaceTools`, not a `RefactoringStructuralTools`,
  and its 15-argument call (lines 63-71) matches `WorkspaceTools`'s current constructor
  (`RoslynSentinel.Server.Basic/WorkspaceTools.cs:50`) parameter-for-parameter, in order. Not the
  source of a `RefactoringStructuralTools`-branded error.

## Open question - not yet resolved

Given the above, the most likely remaining explanations, in order of plausibility, are:

1. **Stale build artifact.** The repo currently has a large number of parallel build outputs under
   `bin-vscode/`, per-project `bin/<Config>/net10.0/`, and several `_scratchbuild*` directories with
   `RoslynSentinel.Server.Basic.dll` copies dated across a week-plus range (oldest seen: Aug 29;
   newest: Sep 25 13:46, same day as this build). A `RefactoringStructuralTools.dll` compiled before
   the Tools/Impl split (i.e. from before the constructor was reduced to take `RefactoringStructuralImpl`
   directly) would plausibly still expose a legacy multi-parameter constructor under the old name,
   and a build that resolves a stale reference assembly instead of recompiling from current source
   could report the arity mismatch against whichever source file the compiler associates with that
   compilation unit's cached line-number table - which would explain both symptoms at once (wrong
   line content, arity number matching `RefactoringStructuralImpl` instead). This matches a
   previously-logged repo pattern (memory: stale server DLL predating current source, checked via
   bin mtime vs. git log) but has **not been confirmed here** - no build log with a timestamp/hash
   for this specific CS1729 was available to this writeup, and no clean rebuild was performed to see
   if the error reproduces.
2. **The reported file:line is simply inaccurate** (e.g. the harness or build-output parser that
   produced the "File: ...:283" attribution for this task mis-mapped the diagnostic's span, possibly
   picking up a stale post-edit position after some earlier compile pass shifted lines in this file).
   Also unconfirmed - no raw MSBuild/csc diagnostic line (as opposed to the summarized `CS1729: ...
   File: ...` text) was available to check the original span the compiler itself emitted.

Both remain hypotheses. Neither has been traced to source, and per CLAUDE.md's root-cause discipline
this is stated explicitly rather than presented as a confirmed cause.

## What unblocks it

Whichever of these is fastest to check first:

1. Re-run `Build` from a guaranteed-clean state (delete/rename the target project's `obj`/`bin` for
   `RoslynSentinel.Tests.Battery` and its referenced `RoslynSentinel.Server.Basic` /
   `RoslynSentinel.Basic`, or use a fresh `_scratchbuild*`-style isolated copy) and see whether the
   CS1729 reproduces at all. If it does not reproduce, this was staleness and the fix is ensuring
   the build path used for this task is not picking up a cached assembly - worth a follow-up
   proposal (there is prior related memory: `feedback_stale_server_before_rebuild`).
2. If it does reproduce cleanly, capture the **raw** compiler diagnostic (not a summarized
   "CS1729: ... File: ..." string) including its exact reported span/column, and re-open this doc
   with that span - at that point the discrepancy between the claimed line 283 and the actual
   constructor call at line 269 becomes the thing to explain, rather than a hypothesis.
3. Either way, confirm which specific build output path (`bin-vscode/...`, a per-project
   `bin/<Config>/net10.0/`, or a `_scratchbuild*` folder) the build that produced this error actually
   ran from, since several exist side by side with `RoslynSentinel.Server.Basic.dll` timestamps
   spanning Aug 29 through Sep 25 13:46 in this tree.

## Related

- `docs/current/blockers/blocking_error_membershaped_success_types_not_unified.md` and other files in
  this folder - format/tone reference used for this writeup.
- Memory: stale server DLL predating current source is a recurring, previously-diagnosed pattern in
  this repo; not re-derived here, only flagged as the leading hypothesis.
- `RoslynSentinel.Server.Basic/RefactoringStructuralTools.cs:97-104` and
  `RoslynSentinel.Basic/RefactoringStructuralImpl.cs:15-43` - the two types and constructors read in
  full to establish current on-disk arity.
- `RoslynSentinel.Tests.Battery/UndoLastApplyTests.cs:253-286` - the test method containing both the
  cited line 283 and the actual (correctly-shaped) construction at line 269.

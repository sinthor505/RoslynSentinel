# `ChangeSignature` does not cascade a parameter change across an interface/implementation boundary

**Status:** OPEN - found 2026-09-19, while executing
`docs/current/plans/plan_remove_dead_symbol_session_check.md` (removing the dead `sessionId`
parameter from `ISymbolResolver.ResolveFromWireAsync` and its two implementers,
`PersistentWorkspaceManager` and `FakeWorkspaceManager`). Not a hard stop: the edit was completed
via `ApplyUnifiedDiff`/`ReplaceSnippet` with `validateOnApply: false` per file, converged with a
single terminal `Build`, and the session continued per this repo's tool-failure doctrine (finish
in-flight edit, document, do not silently route around it without recording the gap).

## What was being attempted

Removing the unused `sessionId` parameter from `Task<SymbolResolution> ResolveFromWireAsync(string
sessionId, string projectName, string docCommentId, CancellationToken cancellationToken)`, declared
on the `ISymbolResolver` interface (`RoslynSentinel.Common/ISymbolResolver.cs`) and implemented by
both `PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:27`) and
`FakeWorkspaceManager` (`RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs:15`).

## Call 1 - targeting the interface declaration

```
ChangeSignature(filepath: "RoslynSentinel.Common/ISymbolResolver.cs",
  methodName: "ResolveFromWireAsync",
  parameters: [{"originalIndex":1}, {"originalIndex":2}, {"originalIndex":3}],
  dryRun: true, returnDiff: true)
```

Result: rejected, with real compiler errors reported back in the dry run itself:

```
CS0535 at C:\...\PersistentWorkspaceManager.cs:27: 'PersistentWorkspaceManager' does not
  implement interface member 'ISymbolResolver.ResolveFromWireAsync(string, string, CancellationToken)'
CS0535 at C:\...\FakeWorkspaceManager.cs:15: 'FakeWorkspaceManager' does not implement
  interface member 'ISymbolResolver.ResolveFromWireAsync(string, string, CancellationToken)'
```

Retried identically later in the same session (after other edits) - same two `CS0535` errors,
same two files.

## Call 2 - targeting the concrete implementation instead

Same parameter-drop, but `filepath: "RoslynSentinel.Common/PersistentWorkspaceManager.cs"` (the
concrete class, not the interface). Result: rejected again -

```
CS0535 at PersistentWorkspaceManager.cs:27: 'PersistentWorkspaceManager' does not implement
  interface member 'ISymbolResolver.ResolveFromWireAsync(string, string, string, CancellationToken)'
CS7036 (x3) - call sites missing the required cancellationToken argument
```

Note the interface's own signature is unchanged in this call's error (still 4-arg) - `PersistentWorkspaceManager`'s
signature had trimmed to 3-arg, but the interface it implements had not moved, so now the concrete
class no longer satisfies its own contract, and 3 call sites that had been relying on
`ChangeSignature`'s call-site rewrite came back wrong (missing `cancellationToken`) because the
rewrite was computed against the wrong originating signature shape.

## Root cause - confirmed by direct observation, not yet traced to source

**Confirmed behaviorally, not yet read against `RefactoringEngine.ChangeSignatureAsync`'s
implementation this session.** `ChangeSignature` only rewrites call sites and validates against the
**single symbol declaration it was invoked on** - it does not walk from an interface method to its
implementers, or from an implementing method back up to the interface(s) it satisfies. Concretely:

- Invoked on the interface: correctly rewrites every real external call site that resolves against
  the interface method, but leaves both implementing classes' method signatures untouched -> both
  now fail to satisfy the interface (`CS0535`).
- Invoked on the concrete class: rewrites that one class and its directly-resolved call sites, but
  leaves the interface declaration (and the sibling implementer) untouched -> the edited class no
  longer satisfies the interface it implements, and callers resolved against the *interface* type
  get non-matching argument counts.

Either direction leaves the tree broken in a way only a matching edit on the sibling
declaration(s) can fix - `ChangeSignature` cannot complete this refactor in one call no matter
which side of the interface boundary it targets, for any interface with more than one implementer
or any implementer accessed through its interface type anywhere in the solution.

## Why this is a blocking finding, not a footnote

Per `CLAUDE.md`'s failure doctrine, `ChangeSignature`'s own tool description advertises solution-wide
call-site rewriting - a model has no way to know from the description alone that "solution-wide"
silently excludes the interface-to-implementer and implementer-to-interface edges. The dry run
*does* surface real compiler errors rather than silently no-op-ing (contrast
`blockers/resolved/blocking_error_changesignature_silent_noop_on_valid_constructor.md`), which is
good - a model attempting this will find out before anything is written. But there is no way to
complete a routine "drop this unused interface-method parameter" refactor using `ChangeSignature`
alone when the interface has more than one implementer; the model is forced to discover the
per-file `validateOnApply: false` + terminal `Build` workaround itself, which is a materially
harder and less-discoverable pattern than the tool's happy path implies.

## Workaround used (this session)

`ChangeSignature` was abandoned for this edit. Instead: `ApplyUnifiedDiff`/`ReplaceSnippet` with
`validateOnApply: false` applied independently to `ISymbolResolver.cs`, `PersistentWorkspaceManager.cs`,
and `FakeWorkspaceManager.cs` (plus all real call sites), followed by one terminal
`Build(level: fullBuild)` to converge on the true remaining error set. 0 errors after convergence.

## What unblocks it

A maintainer needs to read `RefactoringEngine.ChangeSignatureAsync` (or wherever the current
hand-rolled signature-change engine lives - see `docs/current/blockers/resolved/blocking_error_changesignature_silent_noop_on_valid_constructor.md`'s
"Related" section for the suspected shared implementation) to confirm whether call-site discovery is
scoped to the literal invoked symbol only, or attempts a semantic walk at all. Two possible fix
directions, not mutually exclusive:

1. When invoked on an interface method, walk `ISymbolResolver`-style implementer resolution (`FindImplementationsForMemberAsync`,
   already present elsewhere in this codebase per commit `e120b68`) and apply the same signature
   edit to every implementing method found.
2. When invoked on a concrete method that implements an interface member, detect that and either
   refuse with an explicit error naming the interface and telling the caller to target it instead,
   or cascade upward the same way.

Failing either, the tool's `[Description]` should say explicitly that interface/implementation
cascades are not supported, so a model reaches for the multi-call `validateOnApply: false` +
`Build` pattern immediately instead of discovering the gap by trial and error.

## Related

- `docs/current/blockers/resolved/blocking_error_changesignature_silent_noop_on_valid_constructor.md` -
  a different, already-documented `ChangeSignature` defect (silent no-op instead of a loud error) on
  the same underlying hand-rolled engine; not the same failure mode, but worth checking whether one
  source read explains both.
- `docs/current/blockers/blocking_error_changesignature_dryrun_diff_corruption_multiparam_reorder.md` -
  a third, distinct `ChangeSignature` defect found in the same session (corrupted diff rendering on
  an 8-of-9-parameter removal), filed separately since it is a rendering bug, not a cascade bug.
- `docs/current/plans/plan_remove_dead_symbol_session_check.md` - the task this defect was found
  during.
- `RoslynSentinel.Common/ISymbolResolver.cs`, `RoslynSentinel.Common/PersistentWorkspaceManager.cs`,
  `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs` - final state uses the manual
  `validateOnApply: false` workaround described above, not `ChangeSignature`.

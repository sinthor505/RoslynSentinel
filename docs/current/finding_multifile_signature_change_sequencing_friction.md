# Finding: multi-file signature changes require a `validateOnApply: false` + terminal `Build` pattern that isn't discoverable from any tool's description

**Status:** confirmed friction pattern, not a single tool defect - a gap in how the mutating-tool
surface as a whole handles a routine multi-file refactor. Found 2026-09-19 across two plan
executions in the same session: `plan_extract_symbol_resolver.md` and
`plan_remove_dead_symbol_session_check.md`.

## The problem

Every mutating tool in this surface (`ReplaceSnippet`, `Member`, `ApplyUnifiedDiff`, `ChangeSignature`)
validates the **entire solution's build** after each call by default, and rejects any individual
edit that introduces a new compiler error - including a purely transient one that an already-planned,
already-decided subsequent call in the same logical change would fix. This makes the most natural
way to perform a multi-file signature change - edit the interface, then edit each implementer, in
separate tool calls - structurally impossible without an escape hatch, because every call except the
very last one necessarily leaves the tree in a temporarily-inconsistent state.

## Concrete repro from this session

Removing the unused `sessionId` parameter from `ISymbolResolver.ResolveFromWireAsync` required
touching, in some order, the interface declaration, two implementers
(`PersistentWorkspaceManager`, `FakeWorkspaceManager`), and every real call site. Editing the
interface alone via `ReplaceSnippet`:

```
oldContent: "Task<SymbolResolution> ResolveFromWireAsync(string sessionId, string projectName, string docCommentId, CancellationToken cancellationToken);"
newContent: "Task<SymbolResolution> ResolveFromWireAsync(string projectName, string docCommentId, CancellationToken cancellationToken);"
```

was rejected outright:

> ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler
> errors - change not applied. Fix the issue(s) below and retry:
> CS0535 at PersistentWorkspaceManager.cs:27: 'PersistentWorkspaceManager' does not implement
> interface member 'ISymbolResolver.ResolveFromWireAsync(string, string, CancellationToken)'
> CS0535 at FakeWorkspaceManager.cs:15: 'FakeWorkspaceManager' does not implement interface member
> 'ISymbolResolver.ResolveFromWireAsync(string, string, CancellationToken)'
> CS1501 at RefactoringSignatureImpl.cs:72 / WorkspaceProjectManagementImpl.cs:488 /
> DiscoveryEngine.cs:682: No overload for method 'ResolveFromWireAsync' takes 4 arguments

Every one of these errors was already known and already going to be fixed by the next several
calls in the same plan - the tool has no way to be told "I know, I'm getting there." The identical
shape recurred when the concrete class was edited instead: trimming
`PersistentWorkspaceManager.ResolveFromWireAsync` to its final 3-arg form before the interface had
moved failed for the mirror-image reason (`CS0535` the other way). This is a genuine circular
dependency between the interface and its implementers that no ordering of single-file, fully-validated
edits can resolve - the interface can't move first (breaks both implementers), an implementer can't
move first (breaks its own contract), and nothing in between is buildable.

## The workaround, and how it was found

`ReplaceSnippet`/`ApplyUnifiedDiff` both accept `validateOnApply: false`, which skips the
whole-solution build check for that one call. The pattern that worked, used across roughly a dozen
individual edits in this session without ever leaving a genuinely broken *committed* state:

1. Apply every file in the multi-file change with `validateOnApply: false` - interface, each
   implementer, each MCP tool wrapper, any legacy facade, test fakes.
2. Once every file believed to need editing has been touched, run one `Build(level: fullBuild)`.
3. Fix whatever the full build actually reports (genuine remaining errors, not transient ones) using
   normal, validated calls.
4. Only stage/commit once the build is clean.

This is a reasonable and safe pattern once known. **It is not discoverable from any tool's
`[Description]` or from the error message the default-validated call returns.** The rejection
message above is accurate and well-formed (it names the real files and real errors), but it gives
no hint that the fix is "turn off per-call validation and batch these," nor does it point at
`validateOnApply` by name, nor does `Build`'s own description mention that it's the intended
convergence point for a deliberately-staged multi-file change. A model encountering this for the
first time has to independently arrive at "disable the safety check that's blocking me, on purpose,
across several calls, then check everything at the end" - which is a meaningfully different and
riskier-sounding action than what actually happens (nothing is left inconsistent on disk any longer
than the time between the first `validateOnApply: false` call and the terminal `Build`).

## Why this is worth fixing at the environment level

Per `CLAUDE.md`'s failure doctrine, the question is not "should the model have known to use
`validateOnApply: false`" - it's what environment change would have made the correct path obvious
without trial and error. Candidate fixes, roughly cheapest first:

1. **Cross-reference `validateOnApply` from the rejection error itself.** When a validated call is
   rejected specifically because of new errors (as opposed to a malformed edit), the error text
   could say: "If this is one step of a multi-file change, retry with `validateOnApply: false` and
   converge with a single `Build(level: fullBuild)` once every file is edited." This alone would
   likely have cut the discovery cost in this session to near zero.
2. **State the pattern in `Build`'s own `[Description]`** - it currently reads as a plain
   build-and-report tool with no indication that it doubles as the intended checkpoint after a
   deliberately-staged sequence of unvalidated edits.
3. **A dedicated batch/transaction affordance** (already flagged separately - see
   `docs/current/proposal_batch_replacesnippet.md`) that lets a caller submit several file edits as
   one logical unit, validated only once at the end, would remove the need for
   `validateOnApply: false` to be a manually-discovered escape hatch at all. This is a larger,
   already-proposed change, not something to build as a side effect of this finding.

## Distinction from the `ChangeSignature`-specific defects found in the same session

This finding is about the **general validate-per-call design** shared by every mutating tool, not
about `ChangeSignature` specifically. `ChangeSignature` has its own, separate defects filed
individually because they are wrong regardless of sequencing:

- `docs/current/blockers/blocking_error_changesignature_interface_implementation_no_cascade.md` -
  `ChangeSignature` cannot complete an interface/implementer signature change in a single call *at
  all*, cascade or no cascade, because it only ever rewrites call sites resolved against the one
  symbol it was invoked on.
- `docs/current/blockers/blocking_error_changesignature_dryrun_diff_corruption_multiparam_reorder.md` -
  a corrupted dry-run diff on a multi-line parameter list, unrelated to sequencing.

Both of those defects were themselves *worked around* using this finding's `validateOnApply: false`
+ `Build` pattern (manual `ReplaceSnippet`/`ApplyUnifiedDiff` per file instead of `ChangeSignature`),
which is why all three were found back-to-back in the same plan execution.

## Related

- `docs/current/proposal_batch_replacesnippet.md` - the most direct existing proposal for removing
  the need for this workaround entirely.
- `docs/current/proposal_nonblocking_validation_mode.md` - likely overlaps; worth checking whether
  it already covers `validateOnApply`'s discoverability specifically or only its existence.
- `docs/current/plans/plan_extract_symbol_resolver.md`,
  `docs/current/plans/plan_remove_dead_symbol_session_check.md` - the two plan executions this
  session where the pattern was needed and used.

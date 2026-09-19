# Remove dead sessionId/IsCurrentSession from symbol resolution

## Context

Sibling plan `plan_extract_symbol_resolver.md` moves `ResolveSymbolAsync`/
`ResolveByDocCommentIdAsync` out of `PersistentWorkspaceManager` as a pure, behavior-preserving
move. That plan explicitly excludes `ResolveFromWireAsync`'s `sessionId` parameter and
`IsCurrentSession`/`IWorkspaceManager.SessionId`, because removing them is a real API-surface
change (3 MCP tool signatures, a struct field, 3 tests), not a same-behavior relocation. This plan
covers that removal on its own.

**Trigger for this plan:** while scoping the symbol-resolver extraction, `sessionId` was suspected
dead (`IsCurrentSession(sessionId)` returns `true` whenever `sessionId` is empty, and all 3 known
callers default it to `""`). A background investigation confirmed this goes further than
suspected - the interface itself already marks `IsCurrentSession` `[Obsolete("No production
caller. Reserved for external consumers; do not add new usages.")]`
(`RoslynSentinel.Common/IWorkspaceManager.cs:22`), i.e. this was already flagged as dead by a prior
author, just never removed.

## Facts confirmed by reading the actual file and a full-solution trace

**`IWorkspaceManager.SessionId` / `IsCurrentSession(string)`** (the workspace-level session guard,
NOT `SymbolHandle.SessionId` - two different types with the same property name, do not conflate
them during execution):
- `IsCurrentSession` body: `string.IsNullOrEmpty(sessionId) || sessionId == this.SessionId.ToString()`
  (`PersistentWorkspaceManager.cs:1961`ish, re-verify at execution time).
- All 3 call sites of `ResolveFromWireAsync` default `sessionId` to `""`:
  `RefactoringSignatureImpl.RenameSymbol`, `DiscoveryEngine.PreviewRenameImpactAsync`,
  `WorkspaceProjectManagementImpl.SafeDeleteUnusedSymbol` (the last passes `string.Empty`
  explicitly rather than relying on a default).
- However, `RoslynSentinel.Tests.Advanced/MassiveRefactoringTests.cs:117` and
  `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs:257,268` DO pass a real, currently-valid
  `_workspaceManager.SessionId.ToString()` into `RenameSymbol`'s `sessionId` parameter. These tests
  will need their call sites updated to drop the argument (not just left passing a value into a
  removed parameter).
- `BatteryTwentyTests.cs:581-584` already asserts, for the sibling tool `SafeDeleteUnusedSymbol`,
  that "sessionId is no longer an exposed parameter... never surfaces a sessionId for a caller to
  pass back in" - i.e. this exact removal has already been done once for one tool and is an
  established, tested precedent, not a novel API change.

**`SymbolHandle.SessionId`** (struct field, `RoslynSentinel.Common/SymbolHandle.cs:5`) - traced via
background investigation, full accounting of every read/write in the solution:
- Written only via the constructor, called from `PersistentWorkspaceManager.ResolveFromWireAsync`
  (passes through the same `sessionId` parameter being removed) and
  `RefactoringEngine.TryResolveUpdatedHandleAsync:1064` (copies `handle.SessionId` from an existing
  handle into a newly reconstructed one after a rename - never a fresh value).
  read only at `RefactoringSignatureImpl.cs:131`, where `h.SessionId` is placed into an anonymous
  `updatedHandle` object and serialized into `RenameSymbol`'s JSON tool response - pure
  display-through, never compared, validated, or branched on anywhere.
- No code anywhere re-validates a handle's `SessionId` against a live session on a later call - the
  investigation found no "stale handle from a prior workspace load" check exists in practice; that
  concern is undesigned, not a hidden dependency this removal would break.
- Conclusion: once `IWorkspaceManager.SessionId`/`IsCurrentSession`/`ResolveFromWireAsync`'s
  `sessionId` parameter are gone, `SymbolHandle.SessionId` has zero writers producing a meaningful
  value and zero readers depending on its content - safe to delete from the struct entirely rather
  than leave as an always-empty vestige.

**MCP tool surface touched** (re-verified 2026-09-19 via solution-wide `SearchSolutionText` for
`SessionId|IsCurrentSession` - the original 3-signature count was incomplete; there are 2 more
pass-through layers plus a test fake not caught by the initial scoping):
- `RefactoringSignatureTools.cs:33` (`RenameSymbol`) -> impl `RefactoringSignatureImpl.cs:63`
  (reads `h.SessionId` at line 131 into the `updatedHandle` response object).
- `SentinelRefactoringTools.cs:58` - a second, legacy-facade `RenameSymbol` declaration
  (`[McpServerToolType]`, doc comment "LEGACY FACADE") that delegates straight through to
  `RefactoringSignatureTools`'s `_signature.RenameSymbol(...)` at line 63 - must drop `sessionId` in
  lockstep with the primary declaration, not treated as a separate concern.
- `SentinelSymbolTools.cs:123` (`PreviewRenameImpact`) -> delegates to
  `SymbolRelationshipTools`'s composed `_relationship.PreviewRenameImpact(...)` at line 125.
- `SymbolRelationshipTools.cs:65` (`PreviewRenameImpact`) -> impl `SymbolRelationshipImpl.cs:196`
  (delegate call at line 204).
- **Resolved:** `SentinelSymbolTools` and `SymbolRelationshipTools` are NOT duplicate/dead
  registrations - confirmed via `ToolClassRegistry.cs:15,41,53`: `SentinelSymbolTools` is
  registered under the `"Workspace"` mode group (both Basic and Advanced), `SymbolRelationshipTools`
  separately under `"SymbolRelationship"` - both are genuinely live tool-name registrations of
  `PreviewRenameImpact` and both signatures must be edited.
- `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs` implements `IWorkspaceManager` directly:
  `SessionId` (line 31), `IsCurrentSession` (line 71, currently `throw new NotImplementedException()`),
  `ResolveFromWireAsync` (line 99) all need their signatures/bodies updated to match the trimmed
  interface, or the build will fail with a CS0535 the moment the interface changes.
- Not touched by this plan (confirmed a different property, do not conflate): `SentinelRefactoringTools.cs:313`'s
  `[property: Produces(DataTag.SessionId)] string SessionId` on an unrelated response record.

## Decision 1 - What gets removed

- `IWorkspaceManager.SessionId` property (already unused outside `IsCurrentSession` itself, per the
  `[Obsolete]` tag - confirm zero other readers via `SearchSolutionText` at execution time before
  deleting, since `SessionId` is a common enough name that a naive search will need to filter for
  the `IWorkspaceManager`/`PersistentWorkspaceManager` receiver specifically, not
  `SymbolHandle.SessionId` or any other unrelated `SessionId`).
- `IWorkspaceManager.IsCurrentSession(string)` method (already `[Obsolete]`).
- `ISymbolResolver.ResolveFromWireAsync`'s `sessionId` parameter - becomes
  `ResolveFromWireAsync(string projectName, string docCommentId, CancellationToken cancellationToken)`.
- The `StaleSession` branch inside `ResolveFromWireAsync`'s body (the `if (!this.IsCurrentSession(sessionId))`
  check and its `EngineErrorCode.StaleSession` result).
- `SymbolHandle.SessionId` field and the corresponding constructor parameter - becomes
  `SymbolHandle(string projectName, string docCommentId)`.
- The `sessionId` parameter on the 3 MCP tool methods listed above, and their `[Description(ToolParams.SessionId)]`
  attributes.
- The `h.SessionId` entry inside `RefactoringSignatureImpl.cs`'s `updatedHandle` anonymous object
  (becomes `new { h.ProjectName, h.DocCommentId }`).
- `RefactoringEngine.TryResolveUpdatedHandleAsync:1064`'s `handle.SessionId` argument to the
  reconstructed `SymbolHandle` (constructor call loses that argument entirely, matching the new
  2-arg constructor).

## Decision 2 - What must be updated, not just deleted

- `RoslynSentinel.Tests.Advanced/MassiveRefactoringTests.cs:117` and
  `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs:257,268` - drop the
  `sessionId: _workspaceManager.SessionId.ToString()` argument from their `RenameSymbol` calls
  entirely (not replace with a different value - the parameter is gone).
- Any other `SymbolHandle` construction site found by a fresh `SearchSolutionText` for
  `new SymbolHandle\(` at execution time (2 known: `PersistentWorkspaceManager.cs` and
  `RefactoringEngine.cs:1064` - re-confirm no others exist before editing).
- `[Produces(DataTag.SessionId)]` attributes on `LocateSymbol` (`SentinelSymbolTools.cs:48`,
  `SymbolNavigationTools.cs:25`) - **resolved 2026-09-19**: read `LocateSymbolResult`
  (`SymbolNavigationTools.cs:106-110`) and its element type `LocatedSymbolInfo`
  (`SymbolNavigationTools.cs:81-92`) in full. Neither has a `SessionId` property - the attribute is
  stale metadata left over from when `SymbolHandle.SessionId` was a real field, not a distinct
  concept. `LocateSymbol`'s own `[Description]` already says it "Returns SymbolHandles containing
  projectName, docCommentId, and filePath" with no mention of session. Remove both
  `[Produces(DataTag.SessionId)]` attributes as part of this cleanup rather than leaving them as a
  dangling reference to a field that no longer exists.

## Decision 3 - Ordered execution steps with build checkpoints

1. Re-run `SearchSolutionText` for `\.SessionId\b` and `IsCurrentSession` solution-wide immediately
   before starting, to catch any new call site added since this plan was drafted, and to
   re-partition hits between `IWorkspaceManager.SessionId` and `SymbolHandle.SessionId` (both named
   identically - do not conflate during execution).
2. Investigate `LocateSymbol`'s actual response shape (read `SentinelSymbolTools.cs`'s
   `LocateSymbol` method and whatever result type it constructs) to resolve Decision 2's open
   question before deciding whether it needs any change.
3. Remove `sessionId` from `ISymbolResolver.ResolveFromWireAsync`'s signature and delete the
   `StaleSession` branch from its body.
4. Remove `IWorkspaceManager.SessionId` and `IsCurrentSession` from the interface, then from
   `PersistentWorkspaceManager`'s implementation.
5. Remove the `SessionId` field/constructor parameter from `SymbolHandle`, then fix the 2
   construction call sites (`PersistentWorkspaceManager.cs`'s `ResolveFromWireAsync`,
   `RefactoringEngine.cs:1064`).
6. Remove `sessionId` from the 3 MCP tool method signatures and their `[Description(ToolParams.SessionId)]`
   attributes; fix each tool's call into `ResolveFromWireAsync` to match the new 3-arg signature.
7. Fix `RefactoringSignatureImpl.cs`'s `updatedHandle` anonymous object to drop `h.SessionId`.
8. Update the 3 test call sites (`MassiveRefactoringTests.cs:117`, `BatteryTwentyFourTests.cs:257,268`)
   to drop the now-removed argument.
9. **Build checkpoint** (`Build(level: fullBuild)`, 0 errors) - expect and confirm CS-level errors
   guide every remaining call site if step 1's search missed anything; this is exactly the kind of
   change where the compiler enumerates the full blast radius for you.
10. Full regression suite run, comparing against the known baseline failure set; specifically
    re-run `MassiveRefactoringTests` and `BatteryTwentyFourTests`/`BatteryTwentyTests` to confirm
    the edited tests still pass with the argument removed rather than merely compiling.
11. Git stage + commit, separate from `plan_extract_symbol_resolver.md`'s commit, with a message
    that describes this as removing confirmed-dead API surface (cite the `[Obsolete]` tag and the
    `BatteryTwentyTests.cs:581-584` precedent as justification).

## Files to modify

- `RoslynSentinel.Common/ISymbolResolver.cs`
- `RoslynSentinel.Common/IWorkspaceManager.cs`
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`
- `RoslynSentinel.Common/SymbolHandle.cs`
- `RoslynSentinel.Basic/RefactoringEngine.cs`
- `RoslynSentinel.Basic/DiscoveryEngine.cs`
- `RoslynSentinel.Server.Basic/RefactoringSignatureImpl.cs`
- `RoslynSentinel.Server.Basic/RefactoringSignatureTools.cs`
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs` (legacy facade pass-through, added
  2026-09-19 re-verification)
- `RoslynSentinel.Server.Basic/SentinelSymbolTools.cs`
- `RoslynSentinel.Server.Basic/SymbolRelationshipTools.cs`
- `RoslynSentinel.Server.Basic/SymbolRelationshipImpl.cs` (added 2026-09-19 re-verification)
- `RoslynSentinel.Server.Basic/WorkspaceProjectManagementImpl.cs` (confirm whether it needs a change
  at all - it already passes `string.Empty` explicitly, so the only edit may be dropping that now-
  nonexistent argument)
- `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs` (added 2026-09-19 re-verification - implements
  `IWorkspaceManager` directly, will not compile once the interface trims)
- `RoslynSentinel.Tests.Advanced/MassiveRefactoringTests.cs`
- `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`

## Open questions

- Whether `SentinelSymbolTools.cs:123` and `SymbolRelationshipTools.cs:65`'s two
  `PreviewRenameImpact` declarations are duplicate registrations of the same logical tool (e.g. one
  per DI mode) or genuinely different tools that happen to share a name - resolve before editing
  both, since the fix differs (edit once vs. edit two independent implementations).
- Whether `LocateSymbol`'s `[Produces(DataTag.SessionId)]` reflects a real returned field or is
  stale metadata - resolve via Decision 3 step 2 before deciding if `LocateSymbol` needs any edit.
- Sequencing relative to `plan_extract_symbol_resolver.md`: recommend landing the pure extraction
  first (lower risk, no public API change), then this cleanup second, so a build break during the
  cleanup can't be confused with a regression from the extraction. Not a hard dependency either
  direction, but doing them in the same order they were discovered keeps the git history easier to
  bisect if something goes wrong.

## Verification

- `Build(level: fullBuild)` -> 0 errors, with special attention to any CS0117/CS1061 the compiler
  surfaces from a missed call site (expected and useful here, not a defect).
- Full test suite pass, specifically `MassiveRefactoringTests` and
  `BatteryTwentyFourTests`/`BatteryTwentyTests` passing with their call sites updated, not just
  compiling.
- Build to 0 errors, then commit immediately.

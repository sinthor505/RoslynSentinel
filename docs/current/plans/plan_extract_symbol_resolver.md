# Extract symbol resolution out of PersistentWorkspaceManager

## Context

`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`, 2037 lines as
of 2026-09-19) implements 11 interfaces. The three `ICircuitBreaker`-derived interfaces
(`UnrecoverableCircuitBreaker`, `MutationCircuitBreaker`, `OrientationCircuitBreaker`) and
`IScopedOperationLedger` (`ScopedOperationLedgerEngine`) are already extracted into composed
classes. `IRateLimiter` has a plan drafted (`plan_extract_rate_limiter.md`, not yet executed).

`ISymbolResolver` was raised as the next candidate on the reasoning that symbol resolution is
domain logic (resolving a doc-comment ID to a Roslyn symbol within a project's compilation), not
workspace lifecycle (loading, watching, or mutating the solution on disk) - it doesn't belong next
to `OnFileSystemChanged`/`SetupWatcher` conceptually even though it currently lives in the same
file.

**This plan covers only the pure, behavior-preserving move.** A related but separate concern -
whether `ResolveFromWireAsync`'s `sessionId` parameter and `IsCurrentSession`/`SessionId` should be
removed as dead-in-practice code - is intentionally NOT part of this plan. That is scoped as its
own follow-up (`plan_remove_dead_symbol_session_check.md`) because it changes 3 MCP tool public
signatures (`RenameSymbol`, `PreviewRenameImpact` x2) and touches `SymbolHandle.SessionId`, which
required its own investigation into whether that struct field is fully inert or carries meaning
across separate calls (e.g. detecting a handle from a prior workspace load). Keeping the two plans
separate means the low-risk pure move can land independently of the higher-risk API surface change.

## Facts confirmed by reading the actual file

Interface contract (`RoslynSentinel.Common/ISymbolResolver.cs`):

```csharp
public interface ISymbolResolver
{
    Task<SymbolResolution> ResolveFromWireAsync(string sessionId, string projectName, string docCommentId, CancellationToken cancellationToken);
}
```

Only `ResolveFromWireAsync` is on the interface. But `PersistentWorkspaceManager` also has two
`[Obsolete]` methods declared on the broader `IWorkspaceManager` umbrella interface
(`RoslynSentinel.Common/IWorkspaceManager.cs`) that are part of the same symbol-resolution method
group:
- `ResolveSymbolAsync(SymbolHandle handle, CancellationToken)` - `[Obsolete("Superseded by ResolveFromWireAsync...")]`
- `ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken)` - `[Obsolete("Superseded by ResolveFromWireAsync...")]`
- `TrackSymbol(string agentHandle, SymbolHandle handle)` - `[Obsolete("No production caller...")]`, writes `_trackedSymbols[agentHandle] = handle` and nothing ever reads `_trackedSymbols` (confirmed via solution-wide `SearchSolutionText` for `_trackedSymbols` - the only 2 hits are the field declaration and this one write).

Method bodies read in full (`PersistentWorkspaceManager.cs`, current line numbers - re-verify at
execution time since this file has shifted repeatedly this session):

- `ResolveSymbolAsync` (~1936-1945): `GetCurrentSolutionAsync` -> find `Project` by name -> `GetCompilationAsync` -> `DocumentationCommentId.GetFirstSymbolForDeclarationId`. Depends only on solution/compilation access - **this is exactly what `ISolutionProvider.GetCurrentSolutionAsync` already provides**, confirmed by checking `GetCurrentSolutionAsync`'s own declaring interface.
- `ResolveByDocCommentIdAsync` (~1946-1954): identical shape to `ResolveSymbolAsync`, just takes `(symbolId, projectName)` directly instead of a `SymbolHandle`. Same dependency: `GetCurrentSolutionAsync` only.
- `ResolveFromWireAsync` (~1965-1998): calls `this.IsCurrentSession(sessionId)` (needs `SessionId` - an `IWorkspaceManager`-level `Guid` property, NOT `SymbolHandle.SessionId`, a different `string` field on an unrelated type with the same name) and `this.ResolveSymbolAsync(...)`. **This method is the one that must stay on `PersistentWorkspaceManager` for this plan** - or become a thin orchestrator that calls the new class - because it depends on `IsCurrentSession`/`SessionId`, which this plan does not touch.
- `IsCurrentSession` (~1956-1962): `string.IsNullOrEmpty(sessionId) || sessionId == this.SessionId.ToString()`. Not moved by this plan.
- `TrackSymbol` (~1931-1934): one-liner writing dead state (`_trackedSymbols`). Candidate for outright deletion rather than extraction - confirm zero readers again at execution time, then either move it verbatim (lowest-risk) or delete it and `_trackedSymbols` together as a small separate cleanup step within this same plan (deleting genuinely dead code is not a behavior change). Decide at execution time; either is acceptable, but do not silently drop it without a build-checkpoint confirming no reader exists.

Solution-wide caller check for the one live public entry point, `ResolveFromWireAsync` (via
`SearchSolutionText` for `\.ResolveFromWireAsync\(`): 3 callers, all through
`_workspaceManager` typed as `IWorkspaceManager` -
`RefactoringSignatureImpl.RenameSymbol:72`, `DiscoveryEngine.PreviewRenameImpactAsync:682`,
`WorkspaceProjectManagementImpl.SafeDeleteUnusedSymbol:488`. None of these call
`ResolveSymbolAsync`/`ResolveByDocCommentIdAsync` directly - confirmed no external caller reaches
past the `IWorkspaceManager` facade for these two methods, so moving their implementation is safe
without touching any of the 3 callers.

## Decision 1 - New class shape

Create `RoslynSentinel.Common/SymbolResolver.cs`:

```csharp
public class SymbolResolver
{
    private readonly ISolutionProvider _solutionProvider;

    public SymbolResolver(ISolutionProvider solutionProvider)
    {
        _solutionProvider = solutionProvider;
    }

    // ResolveSymbolAsync, ResolveByDocCommentIdAsync moved verbatim, with
    // "await GetCurrentSolutionAsync(...)" rewritten to "await _solutionProvider.GetCurrentSolutionAsync(...)".
    // No other changes to method bodies.
}
```

This class does **not** implement `ISymbolResolver` (that interface's only method,
`ResolveFromWireAsync`, stays on `PersistentWorkspaceManager` per the Context section - it has a
dependency, `IsCurrentSession`, that this plan does not extract). `SymbolResolver` is a plain
helper class, analogous in spirit to `MutationCircuitBreaker` etc. but not tied 1:1 to an existing
interface, since the interface boundary here (`ISymbolResolver`) does not line up with the
state-isolated method group (`ResolveSymbolAsync`/`ResolveByDocCommentIdAsync`).

`ISolutionProvider` already exists (confirmed via `GetTypeInfo` on `PersistentWorkspaceManager` -
it is one of the 11 implemented interfaces) and already declares `GetCurrentSolutionAsync` -
confirm the exact signature via `ReadFile` on `RoslynSentinel.Common/ISolutionProvider.cs` at
execution time before writing the constructor parameter type.

## Decision 2 - PersistentWorkspaceManager becomes a composing facade for these two methods

```csharp
private readonly SymbolResolver _symbolResolver;
```

initialized in the constructor as `_symbolResolver = new SymbolResolver(this);` - `this` satisfies
`ISolutionProvider` since `PersistentWorkspaceManager` implements it directly. This is a narrower,
safer version of "pass the whole manager back in" (the anti-pattern flagged during this plan's
scoping discussion): the new class's constructor parameter is typed as the narrow
`ISolutionProvider` interface, not `PersistentWorkspaceManager` or `IWorkspaceManager`, so it
cannot reach breaker state, rate limiting, or file-watching even though the concrete instance
passed in happens to implement all of those too. A future test double only needs to implement
`ISolutionProvider`, not the full `IWorkspaceManager` surface, to exercise `SymbolResolver` in
isolation.

`ResolveSymbolAsync` and `ResolveByDocCommentIdAsync` become one-line delegations:

```csharp
public Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken) => _symbolResolver.ResolveSymbolAsync(handle, cancellationToken);
public Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default) => _symbolResolver.ResolveByDocCommentIdAsync(symbolId, projectName, cancellationToken);
```

`ResolveFromWireAsync` keeps its current body unchanged (it already calls
`this.ResolveSymbolAsync(...)`, which now resolves to the delegation above - no edit needed to
`ResolveFromWireAsync` itself beyond what naturally follows from `ResolveSymbolAsync`'s body
changing).

## Decision 3 - Ordered execution steps with build checkpoints

1. Re-run `GetFileOutline` and `GetMethodSource` on all 5 methods in this group
   (`ResolveSymbolAsync`, `ResolveByDocCommentIdAsync`, `ResolveFromWireAsync`, `IsCurrentSession`,
   `TrackSymbol`) to confirm current line numbers and exact bodies - this file has shifted after
   every prior extraction this session.
2. Read `RoslynSentinel.Common/ISolutionProvider.cs` in full to get `GetCurrentSolutionAsync`'s
   exact signature (return type, parameter defaults) for the new class's method signatures.
3. Re-run the solution-wide `SearchSolutionText` for `_trackedSymbols|\.TrackSymbol\(|\.ResolveSymbolAsync\(|\.ResolveByDocCommentIdAsync\(`
   immediately before moving anything, to catch any call site added since this plan was drafted.
4. Create `RoslynSentinel.Common/SymbolResolver.cs` per Decision 1 - move `ResolveSymbolAsync` and
   `ResolveByDocCommentIdAsync` verbatim except for the `GetCurrentSolutionAsync` call rewritten to
   go through the injected `ISolutionProvider`.
5. Add the `_symbolResolver` field and constructor wiring to `PersistentWorkspaceManager` per
   Decision 2.
6. Replace `ResolveSymbolAsync` and `ResolveByDocCommentIdAsync`'s bodies on
   `PersistentWorkspaceManager` with one-line delegations.
7. Decide and execute the `TrackSymbol`/`_trackedSymbols` disposition (move verbatim, or delete as
   confirmed-dead code - see Decision 1's note). If deleting, remove both the method and the field.
8. **Build checkpoint** (`Build(level: fullBuild)`, 0 errors).
9. Run a full regression suite. Compare against the known baseline (19 pre-existing failures as of
   the automatic-breaker extraction - see `plan_extract_automatic_circuit_breaker.md`'s execution
   history for the exact signatures) rather than assuming any new failure is unrelated; verify
   independently before dismissing, per CLAUDE.md's failure doctrine.
10. Git stage + commit, following the same message style as the prior extraction commits.

## Files to create

- `RoslynSentinel.Common/SymbolResolver.cs`

## Files to modify

- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`

## Open questions

- Final disposition of `TrackSymbol`/`_trackedSymbols` (move vs. delete) - lean toward delete given
  zero confirmed readers, but re-verify at execution time rather than trusting this document's
  earlier search.
- Whether `SymbolResolver` should eventually also own `ResolveFromWireAsync` and the session check,
  once/if `plan_remove_dead_symbol_session_check.md` lands and removes the `IsCurrentSession`
  dependency - not in scope now, but worth revisiting after that sibling plan resolves, since it
  would let `SymbolResolver` implement `ISymbolResolver` directly instead of being a bare helper
  class with no interface.

## Verification

- `Build(level: fullBuild)` -> 0 errors.
- Full test suite pass, or the same known-unrelated failures and no new ones (verify any new
  failure independently before dismissing it).
- Build to 0 errors, then commit immediately.

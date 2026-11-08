# Extract IRateLimiter out of PersistentWorkspaceManager

## Context

`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`, 2037 lines as
of 2026-09-19) implements 11 interfaces in one `partial class` (no sibling file uses the second
part). The three `ICircuitBreaker`-derived interfaces have already been extracted into composed
classes: `UnrecoverableCircuitBreaker`, `MutationCircuitBreaker`, `OrientationCircuitBreaker` (see
the three sibling `plan_extract_*_circuit_breaker.md` docs, all landed). `IScopedOperationLedger`
was extracted earlier still, via constructor-injectable `ScopedOperationLedgerEngine`
(`plan_scoped_operation_ledger.md`) - `PersistentWorkspaceManager` now holds only a
`private readonly IScopedOperationLedger _ledger` field with six one-line pass-through methods
(`PersistentWorkspaceManager.cs:2014-2023`). That extraction is the template this plan follows.

`IRateLimiter` is the next-cleanest candidate: a single-method interface backed by state that has
zero references anywhere outside its own two methods (confirmed by a solution-wide
`SearchSolutionText` for `_rateLimitWindows|DefaultRateLimits|LoadRateLimits` - the only 5 hits are
the two field declarations, the two lines inside `CheckRateLimit` that read them, and
`LoadRateLimits`'s own declaration). It does not touch `_logger`, does not participate in any lock
shared with another subsystem, and is not read or written by the file-watcher, breaker, or
solution-loading code. This makes it a lower-risk extraction than any of the three breakers were
(they at least logged through `_logger`; this needs no constructor dependency at all).

## Facts confirmed by reading the actual file

Interface contract (`RoslynSentinel.Common/IRateLimiter.cs`):

```csharp
public interface IRateLimiter
{
    string? CheckRateLimit(string toolName, int defaultLimit);
}
```

One method only. No `Reset`, no companion "get current state" accessor - contrast with the breaker
interfaces, which all needed multiple methods.

Private state in `PersistentWorkspaceManager.cs`:
- `_rateLimitWindows` (line 144) - `ConcurrentDictionary<string, ConcurrentQueue<long>>`, per-tool
  sliding-window call timestamps. Instance field.
- `DefaultRateLimits` (line 145) - `static readonly Dictionary<string, int>`, initialized by
  calling `LoadRateLimits()` inline. **Static, not instance state** - this is the one detail that
  makes this extraction shape slightly different from the three breakers (which had no static
  fields at all). The new class must preserve this as a `static readonly` field with the same
  eager initializer, not convert it to instance state - doing so would be a behavior change (right
  now every `PersistentWorkspaceManager` instance in a process shares one loaded rate-limit config;
  multiple test fixtures constructing separate instances still only pay the config-file read once).

Method bodies (`PersistentWorkspaceManager.cs:987-1061`):
- `CheckRateLimit(string toolName, int defaultLimit)` (987-1017) - public, plain method (not
  explicit interface impl - `IRateLimiter` is not `ICircuitBreaker`-derived, so there is no
  redeclaration collision to disambiguate). Reads `DefaultRateLimits` for a per-tool override,
  falls back to `defaultLimit`; drains expired entries from `_rateLimitWindows[toolName]`; returns
  a diagnostic string if the sliding-window count is at/over the limit, otherwise records the call
  and returns `null`.
- `LoadRateLimits()` (1019-1061) - `private static Dictionary<string, int>`. Reads a config file
  (confirm exact path/format via `GetMethodSource` at execution time - not re-derived here since
  the body wasn't fully read in this pass) and falls back to built-in defaults on any failure. Pure
  function: no instance state, no `_logger` calls confirmed in the signature area read so far -
  verify during execution whether it logs anything, since if it does, the new class needs an
  `ILogger` the same way the three breaker classes did.

No constructor initialization beyond the static field's own eager initializer - confirmed
`_rateLimitWindows` uses `= new()` inline and has no constructor-assigned value.

## Decision 1 - New class shape

Create `RoslynSentinel.Common/ToolCallRateLimiter.cs`:

```csharp
public class ToolCallRateLimiter : IRateLimiter
{
    // _rateLimitWindows moved verbatim as an instance field.
    // DefaultRateLimits and LoadRateLimits moved verbatim as static members - preserve "static"
    // exactly; do not convert to instance state (see Context section above for why this matters).

    // CheckRateLimit moved verbatim as a plain public method (no explicit-interface-impl syntax
    // needed - this class implements only one interface, and IRateLimiter was never explicit-impl
    // on PersistentWorkspaceManager either).
}
```

If `LoadRateLimits`'s full body (to be read at execution time) turns out to call `_logger`, add an
`ILogger` field/constructor parameter following the exact pattern used in `MutationCircuitBreaker`
and `OrientationCircuitBreaker`. If it does not log anything, this class needs **no constructor
parameters at all** - simpler than any of the three breaker extractions, matching
`ScopedOperationLedgerEngine`'s shape more than the breakers' shape.

## Decision 2 - PersistentWorkspaceManager becomes a composing facade for this interface

Same pattern as `_ledger`: `PersistentWorkspaceManager` keeps `IRateLimiter` in its interface list
(inherited via `IWorkspaceManager`, confirmed via `GetTypeInfo`), gains a field:

```csharp
private readonly ToolCallRateLimiter _rateLimiter = new();
```

(or constructor-assigned if `ILogger` turns out to be needed - decide during Decision 1's execution
based on what `LoadRateLimits` actually does). `CheckRateLimit` becomes a one-line delegation:

```csharp
public string? CheckRateLimit(string toolName, int defaultLimit) => _rateLimiter.CheckRateLimit(toolName, defaultLimit);
```

No explicit-interface-implementation syntax needed (`IRateLimiter` was never explicit-impl here,
unlike the three `ICircuitBreaker`-derived interfaces).

## Decision 3 - Ordered execution steps with build checkpoints

1. Re-run `GetFileOutline` and `GetMethodSource` on `CheckRateLimit` and `LoadRateLimits` to
   confirm exact current line numbers (this file has shifted twice since the numbers above were
   captured) and to read `LoadRateLimits`'s full body, resolving whether it logs anything.
2. Re-run the solution-wide `SearchSolutionText` for `_rateLimitWindows|DefaultRateLimits|LoadRateLimits`
   immediately before moving anything, to catch any call site that may have been added since this
   plan was drafted - this is the lesson carried forward from the unrecoverable-breaker extraction,
   where a missed direct-field-access call site in `ApplyProposedChangesAsync` was only caught by a
   failed `Member(remove)` precheck.
3. Create `RoslynSentinel.Common/ToolCallRateLimiter.cs` per Decision 1 - move the two fields and
   two methods verbatim (no logic changes), preserving the `static` modifier on `DefaultRateLimits`
   and `LoadRateLimits` exactly.
4. Add the `_rateLimiter` field to `PersistentWorkspaceManager` per Decision 2.
5. Replace `CheckRateLimit`'s body with the one-line delegation.
6. Remove the now-duplicated `_rateLimitWindows`, `DefaultRateLimits`, and `LoadRateLimits` from
   `PersistentWorkspaceManager.cs` via `Member(remove, skipPrecheck: true)` - `skipPrecheck: true`
   is applied preemptively based on this session's confirmed pattern that `Member(remove)`'s
   precheck produces false negatives on plain (non-const) field declarations; constants and methods
   have sometimes succeeded without it, but there is no cost to using it uniformly here.
7. **Build checkpoint** (`Build(level: fullBuild)`, 0 errors).
8. Run any existing rate-limiter-specific tests (locate via `SearchSolutionText` for
   `CheckRateLimit` in `*Tests*` paths - not yet identified in this pass) plus a full regression
   suite run. Compare the failure count/signatures against the known pre-extraction baseline (19
   pre-existing failures as of the automatic-breaker extraction: 15 LM-Studio-unavailable, 2
   enum-member-insert, 2 GUID-equality-in-task-polling - see
   `plan_extract_automatic_circuit_breaker.md`'s execution history) rather than assuming any new
   failure is unrelated; if a new failure appears, dispatch the same kind of independent
   verification used for `T5_GetLargeResult_ReadsFile_PagingWorks_TotalRecordsMatchesT4` before
   concluding it's a pre-existing flake.
9. Git stage + commit, following the same message style as the three breaker commits
   (`0ade2df2`, `13296b4e`, `fe31269c`).

## Files to create

- `RoslynSentinel.Common/ToolCallRateLimiter.cs`

## Files to modify

- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`

## Open questions

- Whether `LoadRateLimits` performs any logging or has any other dependency beyond pure
  config-file I/O - not resolved until step 1's full `GetMethodSource` read. This determines
  whether the new class needs a constructor parameter at all.
- Exact on-disk config file path/format `LoadRateLimits` reads - not re-derived in this pass:
  confirm during execution rather than assuming.
- Whether any test fixture relies on `DefaultRateLimits` being process-wide static state (e.g. a
  test that mutates rate-limit config and expects it to be visible across multiple
  `PersistentWorkspaceManager` instances in the same test run) - if such a test exists, moving the
  static field to a separate class changes nothing observable (it's still one static field, just
  declared on a different type), but worth confirming no test reaches into
  `PersistentWorkspaceManager`'s private state via reflection or an internal-visible-to hack.

## Verification

- `Build(level: fullBuild)` -> 0 errors.
- Full test suite pass, or the same 19 known-unrelated failures and no new ones (verify any new
  failure independently before dismissing it, per the failure doctrine in CLAUDE.md).
- Build to 0 errors, then commit immediately.

# Extract IManualCircuitBreaker out of PersistentWorkspaceManager

## Context

`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`, 2273 lines)
implements 8 interfaces plus `IDisposable` in one `partial class` (the `partial` keyword is present
but there is no sibling file using the second part — confirmed via `Glob` on
`**/PersistentWorkspaceManager*.cs`, only one file matches). This is the first of three sibling
plans extracting each circuit-breaker interface into its own class; see
`plan_extract_automatic_circuit_breaker.md` and `plan_extract_unrecoverable_circuit_breaker.md` for
the other two. All three breakers were confirmed (via `Grep` across the file for their private
fields) to already be state-isolated from each other and from the rest of the class, since each
uses explicit interface implementation forced by `ICircuitBreaker`'s `new bool IsTripped()` /
`new string? StateMessage()` redeclarations on every derived breaker interface
(`RoslynSentinel.Common/ICircuitBreaker.cs:20-21,49-50,90-91`) — this is the part of the god class
that was already designed for decomposition, just never materialized into separate classes.

`IManualCircuitBreaker` is the mutation-failure breaker: it tracks batch-operation outcomes
(succeeded/failed/rolledBack/skipped) and halts mutating tools after a failure-rate or
rollback-score threshold is crossed. It is manual-reset only by design (see the interface's XML
doc, `ICircuitBreaker.cs:18`) — no auto-recovery path exists for this one.

## Facts confirmed by reading the actual file

Interface contract (`RoslynSentinel.Common/ICircuitBreaker.cs:18-40`):
`IManualCircuitBreaker : ICircuitBreaker` declares `new bool IsTripped()`, `new string?
StateMessage()`, `void Reset()`, `BatchResultSummary? CheckBreaker()`, `[Obsolete] string
GetBreakerDirective()`, `[Obsolete] string GetBreakerSeverity()`, `BreakerStatusReport
GetBreakerStatus()`, `void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int
skipped)`.

Private state in `PersistentWorkspaceManager.cs` (from `GetFileOutline`, line numbers as of
2026-09-16):
- `_breakerLock` (158)
- `_breakerOpen` (159)
- `_consecutiveFailureStreak` (160)
- `_totalAttempts` (161)
- `_totalFailures` (162)
- `_weightedRollbackScore` (163)
- Threshold constants: `BreakerStreakThreshold` (149), `BreakerRateMinAttempts` (150),
  `BreakerRateThreshold` (151), `BreakerRollbackScoreThreshold` (152), `CautionStreakThreshold`
  (153), `CautionRateMinAttempts` (154), `CautionRateThreshold` (155),
  `CautionRollbackScoreThreshold` (156)

Method bodies (explicit-interface-implemented, from `GetFileOutline`):
- `RecordBatchOutcome` (1851-1886)
- `CheckBreaker` (1892-1916)
- `Reset` (1922-1934) — **note**: the outline shows four separate `Reset`/`IsTripped`/
  `StateMessage` method groups across lines 1922-2129 because all three breaker interfaces'
  explicit implementations live interleaved in the same file region. Re-run `GetFileOutline` at
  the start of this plan's execution and use `InspectSymbol` with a unique `contextSnippet` (e.g.
  the line immediately after each method's opening brace) to positively identify which
  `IsTripped`/`StateMessage`/`Reset` bodies belong to `IManualCircuitBreaker` specifically, rather
  than trusting line-number ranges copied into this doc — the interleaving is exactly the kind of
  thing that shifts if either sibling plan lands first.
- `AmbiguousBreakerView` (1941-1942) — helper, confirm which interface's explicit impl this
  actually serves before assuming it's exclusive to this breaker.
- `IsTripped` (1944), `StateMessage` (1945) — one of the four interleaved groups; disambiguate per
  above.
- `GetBreakerSeverity` (2019-2025), `GetBreakerDirective` (2028-2035), `GetBreakerStatus`
  (2038-2062) — these three are named uniquely (no interleaving collision) and read/write only the
  fields listed above, per a full-file `Grep` for each field name showing zero references outside
  this breaker's method cluster and the constructor's implicit zero-initialization.

Constructor (`PersistentWorkspaceManager.cs:188-203`) does not initialize any of this breaker's
fields explicitly — they rely on C#'s default zero-initialization (`false`/`0`), confirmed by
reading the constructor body in full via `GetMethodSource`. The new extracted class's constructor
must replicate this (implicit defaults are fine; do not add explicit initializers that weren't
there before).

## Decision 1 — New class shape

Create `RoslynSentinel.Common/MutationCircuitBreaker.cs`:

```csharp
public class MutationCircuitBreaker : IManualCircuitBreaker
{
    // BreakerStreakThreshold, BreakerRateMinAttempts, BreakerRateThreshold,
    // BreakerRollbackScoreThreshold, CautionStreakThreshold, CautionRateMinAttempts,
    // CautionRateThreshold, CautionRollbackScoreThreshold, _breakerLock, _breakerOpen,
    // _consecutiveFailureStreak, _totalAttempts, _totalFailures, _weightedRollbackScore
    // moved verbatim from PersistentWorkspaceManager.

    // RecordBatchOutcome, CheckBreaker, Reset, IsTripped, StateMessage, GetBreakerSeverity,
    // GetBreakerDirective, GetBreakerStatus moved verbatim, dropping "explicit interface impl"
    // syntax in favor of plain public methods since this class implements only one breaker
    // interface (no sibling ICircuitBreaker-derived interface to disambiguate against).
}
```

No constructor parameters needed — confirmed above that none of this breaker's fields are
externally initialized. If the disambiguation pass in Decision 2 step 1 finds a dependency this
plan didn't anticipate (e.g. a shared helper method also used by another breaker), stop and
re-scope rather than duplicating that helper into two classes.

## Decision 2 — PersistentWorkspaceManager becomes a composing facade for this interface

`PersistentWorkspaceManager` keeps `IManualCircuitBreaker` in its interface list (no caller-facing
change — confirmed no external caller was named in this plan's scope, since the class is still the
constructed type registered in DI). It gains a field:

```csharp
private readonly MutationCircuitBreaker _mutationBreaker = new();
```

Every `IManualCircuitBreaker` member on `PersistentWorkspaceManager` becomes a one-line delegation,
e.g.:

```csharp
BatchResultSummary? IManualCircuitBreaker.CheckBreaker() => _mutationBreaker.CheckBreaker();
void IManualCircuitBreaker.RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped)
    => _mutationBreaker.RecordBatchOutcome(succeeded, failed, rolledBack, skipped);
```

Keep the explicit-interface-implementation syntax on `PersistentWorkspaceManager`'s delegating
members (it still implements 3 `ICircuitBreaker`-derived interfaces after this plan and its two
siblings land, so the disambiguation requirement described in `ICircuitBreaker.cs`'s comments still
applies at this layer).

## Decision 3 — Ordered execution steps with build checkpoints

1. Re-run `GetFileOutline` on `PersistentWorkspaceManager.cs` and use `InspectSymbol` to positively
   identify the four interleaved `IsTripped`/`StateMessage`/`Reset` method bodies (lines
   ~1941-2129 per the stale outline above), confirming which belong to `IManualCircuitBreaker`
   specifically before moving anything.
2. Create `RoslynSentinel.Common/MutationCircuitBreaker.cs` per Decision 1, via `CreateFile` then
   `Member(add...)` — move the confirmed fields and methods verbatim (no logic changes).
3. Add the `_mutationBreaker` field and delegating members to `PersistentWorkspaceManager` per
   Decision 2, via `Member`.
4. Remove the now-duplicated fields and method bodies from `PersistentWorkspaceManager.cs` via
   `Member(remove...)`.
5. **Build checkpoint** (`Build`, 0 errors) before proceeding to either sibling plan.
6. Run the full test suite (at minimum `RoslynSentinel.Tests`) to confirm behavior — the breaker's
   threshold logic is a plausible target for an off-by-one introduced during the verbatim move,
   and CS-level compilation success does not prove the thresholds still trip at the same counts.

## Files to create

- `RoslynSentinel.Common/MutationCircuitBreaker.cs`

## Files to modify

- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`

## Open questions

- Whether `AmbiguousBreakerView` (line 1941 in the stale outline) belongs to this breaker or is
  shared infrastructure used by more than one breaker interface's explicit implementation — not
  resolved until step 1's disambiguation pass runs. If shared, it must stay on
  `PersistentWorkspaceManager` (or move to a small shared static helper) rather than being
  duplicated into `MutationCircuitBreaker`.

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors.
- Full test suite pass, not just compilation — confirm breaker threshold behavior is unchanged.
- Build to 0 errors, then commit immediately.

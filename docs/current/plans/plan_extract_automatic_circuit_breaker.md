# Extract IAutomaticCircuitBreaker out of PersistentWorkspaceManager

## Context

Second of three sibling plans extracting each circuit-breaker interface out of
`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`, 2273 lines,
8 interfaces + `IDisposable` in one file). See `plan_extract_manual_circuit_breaker.md` for the
shared background on why these three breakers are the lowest-risk, highest-value part of that god
class to decompose first (each already uses explicit interface implementation with disjoint
private state, forced by `ICircuitBreaker`'s `new bool IsTripped()` / `new string? StateMessage()`
redeclarations — `RoslynSentinel.Common/ICircuitBreaker.cs:20-21,49-50,90-91`).

`IAutomaticCircuitBreaker` is the "orientation breaker": it trips after repeated zero-match
`SearchSolutionText` calls, restricting the agent to a small orienting-tool allowlist, and
auto-resets on the next successful allowlisted call — no manual reset tool exists for it (per the
interface's XML doc, `ICircuitBreaker.cs:43`). This is a materially different reset model from
`IManualCircuitBreaker` (manual-only) and `IUnrecoverableBreaker` (no reset at all), which is worth
keeping in mind since it's the detail most likely to get blurred if these three extractions are
done carelessly from a shared template.

## Facts confirmed by reading the actual file

Interface contract (`RoslynSentinel.Common/ICircuitBreaker.cs:43-56`):
`IAutomaticCircuitBreaker : ICircuitBreaker` declares `new bool IsTripped()`, `new string?
StateMessage()`, `void Reset()`, `bool RecordSearchOutcome(int matchCount)`. Note
`RecordSearchOutcome`'s documented contract: it returns `true` only on the exact call that flips
the breaker from untripped to tripped, so a caller can attach a one-time Finding to that call's own
result rather than relying on a separate pre-check on the next call — this return-value semantic
must be preserved exactly in the moved implementation, not just the trip/reset state.

Private state in `PersistentWorkspaceManager.cs` (from `GetFileOutline`, line numbers as of
2026-09-16):
- `OrientationBreakerTripThreshold` (169)
- `_orientationBreakerLock` (170)
- `_orientationBreakerOpen` (171)
- `_consecutiveZeroMatchSearches` (172)

Method bodies (from `GetFileOutline`):
- `RecordSearchOutcome` (2070-2092)
- `IsTripped` (2095-2101)
- `StateMessage` (2104-2119)
- `Reset` (2122-2129)
- `ComputeSeverityUnlocked` (2131-2144) and `ComputeDirectiveUnlocked` (2146-2161) — named
  uniquely, but confirm via `FindReferences` that these serve only this breaker before moving:
  their names are generic enough that they could plausibly be shared helpers. A `Grep` for
  `ComputeSeverityUnlocked` and `ComputeDirectiveUnlocked` across the full file should show a
  reference count matching exactly this breaker's methods; if it shows references from
  `IManualCircuitBreaker`'s `GetBreakerSeverity`/`GetBreakerDirective` cluster too, they are
  actually shared and must stay on `PersistentWorkspaceManager` or move to a small shared static
  helper instead of into `AutomaticCircuitBreaker`.

As with the manual breaker plan, the outline shows overlapping `IsTripped`/`StateMessage`/`Reset`
line ranges across all three breakers' interleaved explicit implementations
(roughly lines 1941-2129). Re-run `GetFileOutline` and use `InspectSymbol` with a unique
`contextSnippet` to positively identify which bodies belong to `IAutomaticCircuitBreaker`
specifically — do not trust the line numbers in this doc as-is, especially if
`plan_extract_manual_circuit_breaker.md` has already landed and shifted them.

Constructor (`PersistentWorkspaceManager.cs:188-203`) does not explicitly initialize any of this
breaker's fields — confirmed via `GetMethodSource`, same as the manual breaker. Defaults
(`false`/`0`) apply.

## Decision 1 — New class shape

Create `RoslynSentinel.Common/OrientationCircuitBreaker.cs`:

```csharp
public class OrientationCircuitBreaker : IAutomaticCircuitBreaker
{
    // OrientationBreakerTripThreshold, _orientationBreakerLock, _orientationBreakerOpen,
    // _consecutiveZeroMatchSearches moved verbatim.

    // RecordSearchOutcome, IsTripped, StateMessage, Reset moved verbatim as plain public methods
    // (dropping explicit-interface-impl syntax - this class implements only one breaker
    // interface, no disambiguation needed).

    // ComputeSeverityUnlocked / ComputeDirectiveUnlocked moved here ONLY if the FindReferences
    // check above confirms they are exclusive to this breaker. If shared, leave them on
    // PersistentWorkspaceManager (or a shared static helper) and have this class's StateMessage
    // call back into whatever the confirmed shared location turns out to be.
}
```

## Decision 2 — PersistentWorkspaceManager becomes a composing facade for this interface

Same pattern as the manual-breaker plan: `PersistentWorkspaceManager` keeps
`IAutomaticCircuitBreaker` in its interface list, gains

```csharp
private readonly OrientationCircuitBreaker _orientationBreaker = new();
```

and every `IAutomaticCircuitBreaker` member becomes a one-line delegation using explicit-interface-
implementation syntax (still required afterward, since `PersistentWorkspaceManager` implements 3
`ICircuitBreaker`-derived interfaces once all three sibling plans land).

## Decision 3 — Ordered execution steps with build checkpoints

1. Re-run `GetFileOutline` and use `InspectSymbol` to positively identify this breaker's
   `IsTripped`/`StateMessage`/`Reset` bodies among the interleaved group, and confirm via
   `FindReferences` whether `ComputeSeverityUnlocked`/`ComputeDirectiveUnlocked` are exclusive to
   this breaker or shared with `IManualCircuitBreaker`'s severity/directive methods.
2. Create `RoslynSentinel.Common/OrientationCircuitBreaker.cs` per Decision 1 — move confirmed
   fields and methods verbatim.
3. Add the `_orientationBreaker` field and delegating members to `PersistentWorkspaceManager` per
   Decision 2.
4. Remove the now-duplicated fields and method bodies from `PersistentWorkspaceManager.cs`.
5. **Build checkpoint** (`Build`, 0 errors).
6. Run the full test suite, paying particular attention to any test exercising the
   zero-match-search allowlist restriction and the auto-reset-on-success behavior — this breaker's
   auto-reset path is easy to silently break during a verbatim move if the reset call site (inside
   whatever handles a successful allowlisted call) isn't also confirmed and re-pointed at the new
   class.

## Files to create

- `RoslynSentinel.Common/OrientationCircuitBreaker.cs`

## Files to modify

- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`
- Whichever call site invokes the auto-reset path on a successful allowlisted call — not yet
  identified in this pass; locate via `FindReferences` on `IAutomaticCircuitBreaker.Reset` before
  starting step 3, since this is the one behavior in this plan that isn't purely internal to the
  breaker's own methods.

## Open questions

- Exclusivity of `ComputeSeverityUnlocked`/`ComputeDirectiveUnlocked` — not resolved until step 1's
  `FindReferences` check runs.
- Whether this plan should land before or after `plan_extract_manual_circuit_breaker.md` — no hard
  ordering dependency was found (the two breakers' state is disjoint), but doing them one at a time
  with a build checkpoint between, in either order, is safer than parallel edits to the same file.

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors.
- Full test suite pass, specifically covering the zero-match-search trip and successful-call
  auto-reset behaviors, not just compilation.
- Build to 0 errors, then commit immediately.

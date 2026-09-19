# Extract IUnrecoverableBreaker out of PersistentWorkspaceManager

## Context

Third of three sibling plans extracting each circuit-breaker interface out of
`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`, 2273 lines,
8 interfaces + `IDisposable` in one file). See `plan_extract_manual_circuit_breaker.md` for the
shared background on why these three breakers are the lowest-risk part of that god class to
decompose first.

`IUnrecoverableBreaker` is the most structurally distinct of the three: it guards server-integrity
faults (currently: a failed operation-blob write, meaning a change landed on disk with no undo
record). Per its XML doc (`RoslynSentinel.Common/ICircuitBreaker.cs:58-77`), it is **deliberately
non-resettable** — no `Reset()` method exists on the interface, and it cannot inherit one now that
`ICircuitBreaker` itself declares none, making unresettability a compile-time guarantee rather than
a convention. The doc explicitly records that a prior design (reusing `IManualCircuitBreaker`) was
rejected because it would hand the agent a reset button for a server defect. The doc also cites
run `20260910-013550-398` as the reason a warning-only approach was judged insufficient (agent read
warnings and continued for 60 turns). **This history must not be lost during extraction** — carry
the full XML doc comment verbatim onto the new class, do not summarize or shorten it.

No MCP tool reads or clears this breaker (per the same doc) — it exists purely so the call-tool
filter can consult it through the shared `ICircuitBreaker` type and halt all further mutating tools
for the life of the process once tripped.

## Facts confirmed by reading the actual file

Interface contract (`RoslynSentinel.Common/ICircuitBreaker.cs:58-99`):
`IUnrecoverableBreaker : ICircuitBreaker` declares `new bool IsTripped()`, `new string?
StateMessage()`, `void Trip(string toolName, string changeId, string diagnostic)`. `Trip` is
documented as idempotent: the first trip's detail is kept, since it is closest to the root cause —
this idempotency must be preserved exactly (a second `Trip` call must not overwrite
`_unrecoverableHaltMessage`).

Private state in `PersistentWorkspaceManager.cs` (from `GetFileOutline`, line numbers as of
2026-09-16):
- `_unrecoverableBreakerLock` (180)
- `_unrecoverableHaltMessage` (181)

Method bodies: this breaker's `Trip`/`IsTripped`/`StateMessage` are among the interleaved group
spanning roughly lines 1941-2129 in the stale outline, alongside the other two breakers' explicit
implementations. As with both sibling plans: re-run `GetFileOutline` and use `InspectSymbol` with a
unique `contextSnippet` to positively identify this breaker's three method bodies before moving
anything — do not trust line numbers carried over from the initial review, especially since this
plan is likely to be executed after one or both siblings have already changed the file's line
layout.

Given only 2 private fields (the smallest footprint of the three breakers), this is plausibly the
simplest of the three extractions once the disambiguation pass identifies the right method bodies.

Constructor (`PersistentWorkspaceManager.cs:188-203`) does not explicitly initialize either field —
confirmed via `GetMethodSource`. Defaults (`false` for the implicit trip-state, `null` for the
message) apply.

## Decision 1 — New class shape

Create `RoslynSentinel.Common/UnrecoverableCircuitBreaker.cs`:

```csharp
/// <summary>
/// [Carry the full XML doc from ICircuitBreaker.cs's IUnrecoverableBreaker declaration verbatim,
/// including the <remarks> history about run 20260910-013550-398 and the rejected
/// IManualCircuitBreaker-reuse design - this context is load-bearing for future maintainers and
/// must not be lost in the move.]
/// </summary>
public class UnrecoverableCircuitBreaker : IUnrecoverableBreaker
{
    // _unrecoverableBreakerLock, _unrecoverableHaltMessage moved verbatim.

    // Trip, IsTripped, StateMessage moved verbatim as plain public methods (dropping
    // explicit-interface-impl syntax - only one breaker interface implemented here).
    // Trip's idempotency (first-trip-wins) must be preserved exactly.
}
```

No `Reset()` method — confirm the new class does not accidentally pick one up by implementing a
broader interface than `IUnrecoverableBreaker` itself.

## Decision 2 — PersistentWorkspaceManager becomes a composing facade for this interface

Same pattern as both sibling plans: `PersistentWorkspaceManager` keeps `IUnrecoverableBreaker` in
its interface list, gains

```csharp
private readonly UnrecoverableCircuitBreaker _unrecoverableBreaker = new();
```

and every `IUnrecoverableBreaker` member becomes a one-line delegation using explicit-interface-
implementation syntax (still required once all three sibling plans land, since
`PersistentWorkspaceManager` implements 3 `ICircuitBreaker`-derived interfaces).

## Decision 3 — Ordered execution steps with build checkpoints

1. Re-run `GetFileOutline` and use `InspectSymbol` to positively identify this breaker's `Trip`/
   `IsTripped`/`StateMessage` bodies among the interleaved group.
2. Create `RoslynSentinel.Common/UnrecoverableCircuitBreaker.cs` per Decision 1 — move the 2 fields
   and 3 methods verbatim, including the full XML doc comment from the interface declaration.
3. Add the `_unrecoverableBreaker` field and delegating members to `PersistentWorkspaceManager` per
   Decision 2.
4. Remove the now-duplicated fields and method bodies from `PersistentWorkspaceManager.cs`.
5. **Build checkpoint** (`Build`, 0 errors).
6. Locate every call site that invokes `Trip` (via `FindReferences` on
   `IUnrecoverableBreaker.Trip`) — expected to be inside `ApplyProposedChangesAsync`
   (`PersistentWorkspaceManager.cs:1227-1602`) where a failed operation-blob write is detected —
   and confirm those call sites still compile and reach the new class's `Trip` correctly through
   the facade delegation, not a stale direct field reference.
7. Run the full test suite, specifically any test exercising the operation-blob-write-failure /
   session-halt path, plus a manual idempotency check (call `Trip` twice with different diagnostics
   and confirm the first message wins) since this is the one behavior most likely to silently
   regress during a verbatim move and has no compiler-visible signal if it does.

## Files to create

- `RoslynSentinel.Common/UnrecoverableCircuitBreaker.cs`

## Files to modify

- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`

## Open questions

- Whether this breaker's `Trip` call site inside `ApplyProposedChangesAsync` needs to change at all
  beyond the implicit facade delegation, or whether it already calls through the
  `IUnrecoverableBreaker` interface member (in which case no change is needed there) — not
  confirmed until step 6's `FindReferences` pass runs.
- Execution order relative to the other two sibling plans — no hard dependency found; this one's
  smaller field footprint (2 fields vs. 6 and 4) makes it a reasonable candidate to do first if a
  low-risk warm-up is wanted, but nothing in this plan requires that ordering.

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors.
- Full test suite pass, specifically the operation-blob-write-failure / session-halt path.
- Manual idempotency check: two `Trip` calls with different diagnostics, confirm the first message
  is retained.
- Build to 0 errors, then commit immediately.

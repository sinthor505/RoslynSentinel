# ScopedOperationLedgerEngine: tracking blast-radius operations to completion without bypassing validation

## Motivation

`proposal_movemember_instance_callsite_resolution.md` and `proposal_nonblocking_validation_mode.md`
both hit the same underlying wall from different directions: an operation with a legitimately large
blast radius (an instance member move, a coordinated multi-file type change) cannot be both (a) safe
- never leaving the solution in a state the caller loses track of - and (b) achievable without either
rejecting outright (today's `MoveMember` instance-move behavior) or asking `ValidateAndApplyAsync` to
accept broken intermediate state (the non-blocking-validation-mode proposal's opt-in bypass).

This doc proposes a third path for this specific shape of problem: apply the causing change
atomically, surface every resulting compile break as a tracked, individually-resolvable worklist
item, and mechanically prevent unrelated work from proceeding until every item is resolved - without
ever turning off the compiler-error gate itself. Validation stays strict throughout; what changes is
the *scope* of what a rejection blocks.

## Core idea

For an operation like an instance `MoveMember` move:

1. **Discover call sites before applying anything**, via the same `dryRun: true` scan proposed in
   `proposal_movemember_instance_callsite_resolution.md` (its `PreviewCallSite` report, one row per
   call site with a `Status` of `Valid`/`Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/
   `MoveOrderDependent`). This is strictly better than discovering breakage after the fact from
   post-apply compiler diagnostics: it costs nothing extra (the detection scan already has to visit
   every call site to know what to report), it never leaves the solution in a broken state even
   transiently, and it distinguishes cases - like `NoCandidateBlocked`'s real architectural
   inaccessibility - that a bare CS1061 diagnostic alone can't tell apart from an ordinary ambiguous
   case. For a tool with no equivalent pre-apply scan, post-apply `ValidationEngine.ValidateChangesAsync`
   diagnostics remain an acceptable fallback seeding source (file, line, and diagnostic are still real,
   compiler-sourced data) - but pre-apply discovery is the preferred path wherever the tool supports it.
2. Apply the move as a single atomic write: every `Valid` row's call site is rewritten as part of the
   same write as the member relocation itself. Nothing left in a `Valid` state before apply requires
   any tracking after apply - it's just done.
3. **Open a ledger** seeded with one entry per non-`Valid` row: `Ambiguous`, `NoCandidateIntroducible`,
   `NoCandidateBlocked`, or `MoveOrderDependent`. Report it to the caller as the affected-call-site list
   - carrying forward the same `BlockReason`/`SuggestedFix`/`Candidates` data the dry-run preview
   already computed, so nothing needs to be re-derived between preview and real run.
4. Trip a **scoped** circuit breaker: while the ledger has unresolved entries, `ValidateAndApplyAsync`
   only accepts mutating calls that touch a file appearing in the ledger. Anything else is rejected,
   with a message naming the open ledger and pointing at its remaining entries.
5. Each subsequent call that resolves a call site - a delegating-stub fix, a throwing placeholder, a
   manual `ReplaceSnippet`, an auto-resolved field reference, whatever the reviewer chooses per site -
   validates normally against that file's own remaining errors. The resolution strategy is
   deliberately not the ledger's concern; see "Relationship to MoveMember's resolution strategies"
   below.
6. The breaker releases automatically once every entry is resolved. No manual reset step, unlike
   `IManualCircuitBreaker`.

The caller (human or agent) decides *how* to resolve each site. The ledger's only job is making sure
none get silently forgotten and nothing unrelated proceeds while they're open.

A real, non-dry-run `MoveMember` call and its own `dryRun: true` preview should produce the *same*
`PreviewCallSite` rows for the non-`Valid` cases - the real call just additionally applies the `Valid`
rows and opens a ledger from the rest, rather than only reporting them. This symmetry is what makes the
dry-run genuinely predictive: a caller who reviews the preview and proceeds should never be surprised
by what the ledger contains afterward.

## Ledger entries: base type + per-operation extension

Different blast-radius operations produce structurally different "what's broken" data - an instance
move's call-site entries need a broken expression, its static type, and in-scope candidates; a future
`SplitFacadeMember`-style helper-left-behind tracker (`project_caller_fixup_tool_audit_idea.md`) would
need a helper name and which still-resident members keep it alive; something else again may need a
third shape entirely.

Rather than one flat DTO accumulating optional fields per new operation type - the same unbounded-
width growth already visible in `PersistentWorkspaceManager` itself - the ledger uses a common base
record with sealed per-operation extensions, following the existing `EngineResultBase` convention
already used across `RoslynSentinel.Common` (`BatchResultSummary`, `BestInsertionResult`,
`BridgeBatchResult`, etc. all extend it; `MigrationEnvelope<T>` shows this codebase is already
comfortable with a generic envelope over a variable payload for the same class of problem).

```csharp
public abstract record LedgerEntryBase
{
    public required string EntryId { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public bool IsFixed { get; init; }
    public string? ChangeId { get; init; }
}

public sealed record CallSiteLedgerEntry : LedgerEntryBase
{
    public required string BrokenExpression { get; init; }
    public required string OldStaticType { get; init; }
    public required CallSiteStatus Status { get; init; }   // Ambiguous | NoCandidateIntroducible | NoCandidateBlocked | MoveOrderDependent - never Valid, those never become entries
    public string? BlockReason { get; init; }
    public string? SuggestedFix { get; init; }
    public IReadOnlyList<string> CandidatesInScope { get; init; } = [];
}
```

`Status`/`BlockReason`/`SuggestedFix` are carried straight over from the `PreviewCallSite` row that
seeded this entry (`proposal_movemember_instance_callsite_resolution.md` #5-6) - the ledger doesn't
recompute anything the dry-run scan already worked out, it just adds the fix-tracking fields on top.

`IsFixed`/`ChangeId` live on the base since every entry, regardless of operation type, needs the same
fix-tracking and undo-reconciliation; only the description of *what's broken* varies per operation.

### Entries are a ledger, not a queue

Resolved entries are never removed - `IsFixed` flips `true` and `ChangeId` records which applied
change resolved it, but the entry stays. This makes the collection append-only and self-auditing:

- **Breaker release** becomes "fold over all entries, all `IsFixed`" rather than "collection is
  empty" - same effect, no separate history needed to reconcile against.
- **`UndoLastApply` needs no special case.** It looks up the `changeId` being undone, finds every
  ledger entry carrying that `ChangeId` (one fix call can resolve more than one call site at once,
  e.g. a `ReplaceSnippet` batch touching two call expressions in one method - `ChangeId` is
  many-to-one, not 1:1), and flips `IsFixed` back to `false`. This is the same mechanism whether the
  undone change was the original move or a later fix to entry #3.
- **Re-tripping.** If an undo flips an entry back to unfixed after the breaker had already released,
  the breaker re-trips. This is a real state transition to build for, not an edge case - it's the
  same "a run that ends green must be distinguishable from one that never broke" concern
  `proposal_nonblocking_validation_mode.md`'s audit-trail section already raises.
- **Abandoning a move.** Undoing the *original* move's `ChangeId` should cascade-invalidate every
  entry that depends on it (their `ChangeId`s only make sense relative to a move that still exists).
  This falls out of ledger semantics for free rather than needing a bolt-on "abandon" operation.

## Single ledger at a time

The ledger rejects opening a second concurrent ledger outright while one is open. Allowing two
in-flight blast-radius operations simultaneously just enables causing two breakages and losing track
of - or getting distracted out of finishing - the first one while working the second. A second
`MoveMember`-style call that would open a new ledger is rejected with a pointer at the ledger already
open, the same rejection shape used for an unrelated file blocked by the open ledger.

## Owning engine, not a tenth interface on `PersistentWorkspaceManager`

`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`) is a single
2,200+ line `partial` class (partial in name only - one file) already implementing nine interfaces:
`IDisposable`, `IWorkspaceManager`, `ISolutionProvider`, `IManualCircuitBreaker`,
`IAutomaticCircuitBreaker`, `IUnrecoverableBreaker`, `IWorkspaceHealthReporter`, `IWorkspaceMutator`,
`IRateLimiter`, `ISymbolResolver`. It already carries file-watching, changeset caching, three flavors
of breaker state, symbol resolution, and batch-outcome tracking as fields/methods on one object. This
is exactly the god-class shape the Decision 7 `*Tools`/`*Impl` DI-split plan
(`project_di_tool_split_plan_2026_09_05`) is actively fixing elsewhere in the codebase - adding the
ledger as a tenth interface here would be new coupling in the wrong direction while every sibling
class is being decoupled.

The ledger's actual dependency surface is thin: read "is this file part of an open ledger's entries,"
write "record a fix / record an undo." It doesn't need file-watching, symbol resolution, or changeset
caching - none of `PersistentWorkspaceManager`'s other concerns. That makes it a genuine candidate for
extraction rather than the kind of superficial, naming-driven grouping flagged as a risk in
`project_di_engine_audit_open_question` (memory) - the ledger's cohesion is by shared usage, not just
by name.

Proposed: a new `ScopedOperationLedgerEngine`, singleton-registered the same way every other `*Engine`
in this codebase is (`services.AddSingleton<ScopedOperationLedgerEngine>()`, alongside `MetricsEngine`,
`SecurityEngine`, etc. in `ServiceRegistrationExtensionsAdvanced.cs`), implementing a new
`IScopedOperationLedger` interface. `PersistentWorkspaceManager` holds a reference to the engine and
exposes a thin pass-through cast, matching the existing call pattern at the one confirmed precedent
site: `ValidateAndApplyHelper.cs:93` already does
`((IUnrecoverableBreaker)workspaceManager).Trip(operationName, changeId, reason)` - a cast on the
`workspaceManager` parameter `ValidateAndApplyAsync` already receives, not a new parameter. Keeping
`((IScopedOperationLedger)workspaceManager)...` as the call shape means `ValidateAndApplyAsync`
doesn't fork into two different conventions for reaching cross-cutting state - one cast-based, one
parameter-based - while the actual ledger state and logic live in the new engine, not on
`PersistentWorkspaceManager`. Net effect on the god-class: zero new state, zero new fields - pure
delegation, and a first concrete step toward narrowing it rather than the more common direction of
growth.

`ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs`) gets one more check
alongside its existing `_sessionHalted`/breaker checks, before apply: ask the ledger whether the
target file is blocked by an open ledger's unresolved entries; if so, reject with the ledger's own
report instead of a generic validation error.

## Relationship to MoveMember's resolution strategies

This ledger design is orthogonal to *how* an individual call site gets fixed - it's the tracking and
gating layer underneath whichever resolution approach `proposal_movemember_instance_callsite_resolution.md`
settles on (auto-resolution of the unambiguous case, `callSiteFixups` map entries, a delegating-stub
field, a throwing placeholder, or plain manual edits). Each of those is just "a call that resolves one
or more ledger entries." The ledger doesn't pick a strategy; it makes sure none of them can be applied
and then forgotten.

## Relationship to the existing Asyncify bridge/ledger machinery

`SentinelAsyncifyTools.cs` and `MigrationLedger.cs` (`RoslynSentinel.Common/MigrationLedger.cs`) are
the closest existing precedent for a large-scale batch refactor that avoids breakage, and are worth
being precise about rather than assuming they already solve this proposal's problem.

**What Asyncify already validates:** `BridgeAsyncMethods` converts `Foo()` into an `[Obsolete(...
"Asyncify-bridge" ...)]` sync wrapper plus a new `FooAsync()` body - the old member never disappears,
it becomes a thin, marked-obsolete delegator. `UpliftCallers` then rewrites every caller of the
obsolete wrapper to call the async method directly, *in the same tool call*, with its own per-call-site
success/fail/skip accounting (`OperationItemRecord`/`FailureDetail`). This is idea 2 from the
`MoveMember` discussion (delegating stub instead of a throwing placeholder), already running at scale
in production in this repo - real evidence that a delegating-stub default is the right resolution
strategy to prefer over a throwing placeholder for `MoveMember`'s instance case too.

**Why it doesn't need a scoped ledger:** `UpliftCallers` never leaves an incomplete migration staged
across turns - bridge and uplift happen in the same call, eagerly, with immediate accounting, so there
is no window in which a caller could walk away from a half-fixed operation. A caller Asyncify can't
safely rewrite mechanically (e.g. a sync-only call context) is simply left on the obsolete wrapper and
surfaces as `PendingObsoleteCallers` in `GetAsyncMigrationProgress` - a read-only progress signal, not
a gate. Nothing about Asyncify's design blocks unrelated mutating calls while pending callers exist.
This works specifically *because* Asyncify's rewrite is mechanical and unambiguous (every caller of the
obsolete wrapper gets the same rewrite); it sidesteps `MoveMember`'s hard case rather than solving it.
This proposal's scoped ledger exists for exactly the case Asyncify doesn't have: call sites where the
correct rewrite is not mechanically determined and genuinely needs a per-site human/agent judgment call
(`proposal_movemember_instance_callsite_resolution.md`'s ambiguous-receiver problem).

**What `MigrationLedger` already is, precisely:** cross-run, cross-session observability - has this
method already been touched, by which phase, how many times (`HitCount > 1` flags re-entry/thrashing).
It never blocks a subsequent call and every entry it holds already represents a completed action
(success or a deliberate idempotency skip), never an open item awaiting resolution. It is not a
worklist and doesn't need to become one - but its cross-run persistence-to-disk design
(`.roslynsentinel/migration-ledger.json`, survives server restarts) is worth reusing as a pattern if
`ScopedOperationLedgerEngine`'s "does ledger state need to survive a restart" open item (below) is ever
answered yes.

## Interaction with non-blocking validation mode

Complementary, not overlapping, with `proposal_nonblocking_validation_mode.md`: that proposal lets a
*known-coordinated* multi-file change land incrementally without per-call rejection, and needs its own
stall backstop because it removes the per-call compile gate entirely for its scope. This proposal
never removes the gate - every call is still validated strictly - it only narrows *what's allowed to
be attempted* while a ledger is open. The two could compose (a ledger-tracked operation resolved via a
non-blocking-mode multi-file fix), but neither depends on the other landing first.

## Open items

- Exact trip/query API shape for `IScopedOperationLedger` (`TryOpen`, `IsBlocked(filePath)`,
  `RecordFix(entryIds, changeId)`, `RecordUndo(changeId)`, `TryRelease`) - sketched above, not
  finalized against real call-site signatures.
- Whether ledger state needs to persist across server restarts, or is acceptably session-scoped like
  `_sessionHalted` - leaning session-scoped, since a restart mid-ledger is already an unusual enough
  event that requiring the caller to re-survey with a fresh `MoveMember`/validation pass seems
  acceptable, but not decided.
- Whether other tools beyond `MoveMember` (`SplitFacadeMember`'s helper-left-behind tracking; see
  `project_caller_fixup_tool_audit_idea` memory) should feed the same ledger via their own
  `LedgerEntryBase` subtype, or get their own ledger instance - leaning same ledger given the
  single-open-ledger-at-a-time rule already assumes one global scope to protect.

## Status

Design proposal only - not yet implemented or scheduled. Grew out of a design discussion following
`proposal_movemember_instance_callsite_resolution.md`; cross-referenced from that doc and from
`proposal_nonblocking_validation_mode.md`. Also referenced from the `project_di_engine_audit_open_question`
memory as a candidate first step in narrowing `PersistentWorkspaceManager`.

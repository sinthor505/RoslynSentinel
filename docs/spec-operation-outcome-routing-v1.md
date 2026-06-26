<!-- spec-operation-outcome-routing-v1.md -->
# Spec: Operation Outcome Classification + ToolGraph-Routed Failure Hints
**Version:** v1
**Target implementer:** Coding agent (Sonnet in VS Code) constrained to the RoslynSentinel MCP tool surface, or direct edits in the repo.
**Status:** Ready to implement.

---

## 0. Context (read first, do not skip)

A trial async-migration run on a weak local model (gemma-4-e4b) exposed a defect class: an operation reported `success: true` / `severity: caution` / `breakerOpen: false` while its actual outcome was mostly no-ops, and the model confabulated a "all phases complete" summary over it. The 17/27 cases that were idempotent no-ops ("method already async") were reported in the same `failures` bucket as real failures, with no structural distinction and no next-step routing.

**Root cause being addressed here:** outcome classification and next-step routing are currently the *model's* job (it must interpret severity + breaker + scan a prose `failures` array). They must become the *substrate's* job.

This spec does **not** cover the separate `UpliftCallers` bug (it was applying the bridge to target methods instead of their callers). That fix is tracked elsewhere. Do not attempt it here.

### Architectural constraints (non-negotiable — violating any of these fails the spec)
1. **Substrate owns classification.** The model must not infer outcome from severity/breaker/array scanning. The substrate emits a named verdict.
2. **Idempotent no-ops are structurally distinct from failures.** "Already in target state" must never appear in the actionable list and must never count toward the failure rate that drives `severity` or the circuit breaker.
3. **Routing comes from `ToolGraph`, never hand-authored per-reason strings.** A `FailureReason` with no producer route returns a null hint *loudly* — that null is a diagnostic signal (runtime manifestation of `[RequiresExternalInput]` debt), not a swallowed gap.
4. **Routes cannot drift from the live tool surface.** Hints resolve through `ToolGraph.ProducersOf(...)` so a renamed/removed tool never leaves a dangling hint.
5. **Hints pre-fill everything the substrate already knows** (file path, method name, changeId). The model supplies only what it must genuinely decide.

### C# style (enforced)
- Explicit types only. No `var`.
- Always braces, even single-statement.
- No expression-bodied members.

---

## 1. Scope

### In scope
- New `ItemOutcome` and `OperationOutcome` enums.
- New `FailureReason` enum (machine-routable).
- New `ItemFailure`, `ToolHint`, `OperationSummary` shapes in `RoslynSentinel.Common`.
- `FailureRouter` keyed on `ToolGraph`.
- **Pilot integration into `UpliftCallers` only.** Do not roll out to other tools in this spec.

### Explicitly out of scope (do NOT do)
- Do not modify the `UpliftCallers` bridge-vs-caller targeting bug.
- Do not migrate `Asyncify`, `ScanAsyncMigrationCandidates`, or any other tool to the new shape. Pilot first; rollout is a later spec.
- Do not change the MCP wire envelope (`success`/`data`/`hasMore`). The new summary rides inside `data`.
- Do not add `UseStructuredContent = true` to any tool (known SDK #930 coupling problem).
- Do not introduce nested-object *input* parameters. This spec changes *output* shapes only.

---

## 2. Sequencing (execute in this order)

1. Add enums and DTOs to `RoslynSentinel.Common` (§3). Build.
2. Add `FailureRouter` + `ToolGraph` producer-preference weighting (§4). Build.
3. Integrate into `UpliftCallers` result assembly (§5). Build.
4. Add tests (§6). Run.
5. Manual verification against the live solution (§7).

Do not proceed to a step until the prior step builds clean.

---

## 3. DTOs (RoslynSentinel.Common)

All in namespace `RoslynSentinel.Common` (or the established Common namespace — match the existing files, do not invent a new one).

### 3.1 ItemOutcome
```csharp
// v1
public enum ItemOutcome
{
    Succeeded,
    AlreadySatisfied,
    Skipped,
    Failed,
    Blocked
}
```
Semantics:
- `Succeeded` — change written to disk.
- `AlreadySatisfied` — idempotent no-op; already in desired state. **Never actionable, never counts toward failure rate.**
- `Skipped` — intentionally not attempted (below threshold / filtered).
- `Failed` — attempted, could not complete. May be terminal.
- `Blocked` — could not attempt due to a missing precondition the model may be able to resolve via a routed tool.

### 3.2 OperationOutcome
```csharp
// v1
public enum OperationOutcome
{
    CompletedFully,
    CompletedWithNoOps,
    PartialProgress,
    NoProgress,
    NothingToDo
}
```
Derivation rule (substrate computes — see §3.6):
- `NothingToDo` — `Attempted == 0` OR every item is `Skipped`/`AlreadySatisfied` with zero `Succeeded`.
- `CompletedFully` — `Succeeded > 0`, and zero `Failed`/`Blocked`, and zero `AlreadySatisfied`.
- `CompletedWithNoOps` — `Succeeded >= 0`, `AlreadySatisfied > 0`, zero `Failed`/`Blocked` (i.e. nothing left to do, some of it was already done).
- `PartialProgress` — `Succeeded > 0` AND (`Failed > 0` OR `Blocked > 0`).
- `NoProgress` — `Succeeded == 0` AND (`Failed > 0` OR `Blocked > 0`).

### 3.3 FailureReason
```csharp
// v1
public enum FailureReason
{
    OverloadAlreadyExists,
    AlreadyAsync,
    NoAsyncEquivalent,
    CompilerErrorAfterTransform,
    SymbolNotResolved,
    PreconditionMissing,
    Unknown
}
```
Note: `AlreadyAsync` should normally be reclassified to `ItemOutcome.AlreadySatisfied` at assembly time, not surfaced as a failure. It exists in the enum so the mapping is explicit rather than implicit.

### 3.4 ToolHint
```csharp
// v1
public sealed class ToolHint
{
    public string ToolName { get; init; }
    public IReadOnlyDictionary<string, string> PrefilledArgs { get; init; }
    public IReadOnlyList<string> RequiresFromModel { get; init; }
    public string Rationale { get; init; }
}
```

### 3.5 ItemFailure
```csharp
// v1
public sealed class ItemFailure
{
    public string FilePath { get; init; }
    public string MethodName { get; init; }
    public ItemOutcome Outcome { get; init; }   // Failed or Blocked only
    public FailureReason Reason { get; init; }
    public string Detail { get; init; }
    public ToolHint? SuggestedTool { get; init; }
}
```
Constraint: an `ItemFailure` carrying `Outcome` other than `Failed` or `Blocked` is a contract violation. Assert in debug; in release, do not emit it to the actionable list.

### 3.6 OperationSummary
```csharp
// v1
public sealed class OperationSummary
{
    public string BlobName { get; init; }
    public string ChangeId { get; init; }

    public int Succeeded { get; init; }
    public int AlreadySatisfied { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Blocked { get; init; }
    public int Attempted { get; init; }

    public OperationOutcome Outcome { get; init; }
    public string Directive { get; init; }

    public IReadOnlyList<ItemFailure> Actionable { get; init; }
    public bool ActionableTruncated { get; init; }

    public bool BreakerOpen { get; init; }
}
```
Rules:
- `Actionable` contains only `Failed` and `Blocked` items. `AlreadySatisfied`/`Skipped`/`Succeeded` never appear here.
- `Outcome` is computed via the §3.2 derivation, in one place (a static factory on `OperationSummary`, e.g. `OperationSummary.FromCounts(...)`). Do not compute it at call sites.
- `Directive` is a short, substrate-authored sentence stating the verdict and the single most useful next move. It must not contradict `Outcome`.
- Failure rate for `severity`/breaker is computed as `Failed / Attempted`. `AlreadySatisfied` and `Skipped` are excluded from both numerator and denominator-of-concern. (Confirm against the existing breaker formula before wiring — if the current formula uses a different denominator, match its intent, not its literal field.)

---

## 4. FailureRouter

New file: `FailureRouter.cs` (engine/server layer, not Common — it depends on `ToolGraph`).

```csharp
// v1
public sealed class FailureRouter
{
    private readonly ToolGraph _graph;

    public FailureRouter(ToolGraph graph)
    {
        _graph = graph;
    }

    public ToolHint? Route(FailureReason reason, ItemContext ctx)
    {
        DataTag? needed = MapToUnsatisfiedTag(reason);
        if (needed is null)
        {
            return null;
        }

        IReadOnlyList<ToolDescriptor> producers = _graph.ProducersOf(needed.Value);
        if (producers.Count == 0)
        {
            return null;
        }

        ToolDescriptor target = SelectPreferred(producers);
        Dictionary<string, string> prefilled = BuildPrefilled(target, ctx);
        List<string> requires = ResidualParams(target, prefilled);

        return new ToolHint
        {
            ToolName = target.Name,
            PrefilledArgs = prefilled,
            RequiresFromModel = requires,
            Rationale = DescribeReason(reason)
        };
    }

    private static DataTag? MapToUnsatisfiedTag(FailureReason reason)
    {
        switch (reason)
        {
            case FailureReason.OverloadAlreadyExists:
            {
                return DataTag.CancellationTokenSlot;
            }
            case FailureReason.SymbolNotResolved:
            {
                return DataTag.SymbolHandle;
            }
            case FailureReason.NoAsyncEquivalent:
            {
                return null;
            }
            case FailureReason.CompilerErrorAfterTransform:
            {
                return null;
            }
            case FailureReason.PreconditionMissing:
            {
                return null;
            }
            default:
            {
                return null;
            }
        }
    }

    // ... SelectPreferred, BuildPrefilled, ResidualParams, DescribeReason
}
```

### 4.1 ToolGraph producer-preference weighting (required — prevents nondeterministic hints)
`ProducersOf` may return >1 tool. Reflection order is not stable across restarts; nondeterministic hints will corrupt the empirical test runs that consume this. Add a per-`DataTag` producer-preference weight to `ToolGraph` and select deterministically.

- Extend the producer index so each producer carries an `int PreferenceWeight` (default 0).
- `SelectPreferred` returns the highest-weight producer; ties broken by ordinal tool-name sort (deterministic).
- Source of the weight: an attribute argument on `[Produces]` (e.g. `[Produces(DataTag.CancellationTokenSlot, Preference = 100)]`) OR a small static map in `ToolGraph` if attribute change is too invasive for the pilot. Prefer the attribute; if you choose the map, leave a `// TODO` noting the attribute is the intended home.

### 4.2 ItemContext
Minimal context the router needs to pre-fill args. Define alongside `FailureRouter`:
```csharp
// v1
public sealed class ItemContext
{
    public string FilePath { get; init; }
    public string MethodName { get; init; }
    public string ProjectName { get; init; }
    public string ChangeId { get; init; }
}
```
`BuildPrefilled` populates only the args the target tool's schema actually declares. Do not pass args the target does not accept. `ResidualParams` returns the target's required params minus the keys present in `PrefilledArgs`.

---

## 5. Pilot integration: UpliftCallers

Locate the `UpliftCallers` result-assembly site (where `succeeded`/`skipped`/`failed`/`failures` are currently populated).

1. Classify each processed item into `ItemOutcome` per §3.1. Specifically:
   - "already async" / "overload already exists with CancellationToken" → `AlreadySatisfied`.
   - "overload named XAsync already exists" (resolvable by adding CT) → `Blocked`, reason `OverloadAlreadyExists`.
   - symbol/path not resolved → `Failed`, reason `SymbolNotResolved`.
   - transform produced compiler errors → `Failed`, reason `CompilerErrorAfterTransform`.
   - no async equivalent → `Failed`, reason `NoAsyncEquivalent`.
2. For each `Failed`/`Blocked` item, build an `ItemContext` and call `FailureRouter.Route(reason, ctx)`. Attach the returned `ToolHint?` to the `ItemFailure`.
3. Build counts, call `OperationSummary.FromCounts(...)` to derive `Outcome`, author `Directive` from the verdict.
4. Serialize `OperationSummary` into the existing `data` field. Keep `hasMore` behavior unchanged.

**Do-not constraints for this section:**
- Do not put `AlreadySatisfied` items into `Actionable`.
- Do not let `AlreadySatisfied` raise `severity` or move the breaker.
- Do not author a `Directive` that claims completion when `Outcome` is `PartialProgress`/`NoProgress`.

---

## 6. Tests

Add to the existing test project. Minimum cases:

1. `FromCounts` derivation — one test per `OperationOutcome` value (5 tests), asserting the exact count combinations from §3.2.
2. `AlreadySatisfied` exclusion — given a mix including `AlreadySatisfied`, assert it is absent from `Actionable` and excluded from the failure-rate input.
3. Router hit — `OverloadAlreadyExists` with a `ToolGraph` containing a `CancellationTokenSlot` producer returns a hint naming that tool, with `FilePath`/`MethodName` pre-filled.
4. Router miss (loud null) — `NoAsyncEquivalent` returns `null` hint; assert the item still appears in `Actionable` with `SuggestedTool == null`.
5. Router empty-producers — `OverloadAlreadyExists` with a `ToolGraph` that has *no* producer for the tag returns `null` (the `[RequiresExternalInput]` debt signal); assert null, not throw.
6. Determinism — two producers with equal weight resolve to the same tool across repeated calls (ordinal tie-break).
7. Directive/Outcome consistency — assert `Directive` does not contain a completion claim when `Outcome != CompletedFully && Outcome != CompletedWithNoOps`. (Simple substring guard is acceptable.)

---

## 7. Manual verification

Against the live solution (`AvaalExpress.sln`), after the separate UpliftCallers caller-targeting bug is fixed:

1. Re-run the bridge on two methods, then `UpliftCallers` on their callers.
2. Confirm the returned `OperationSummary`:
   - idempotent callers land in `AlreadySatisfied`, not `Actionable`;
   - any `OverloadAlreadyExists` block carries a `SuggestedTool` naming `add_cancellation_token_to_method` with path + method pre-filled;
   - `Outcome` matches reality (`PartialProgress` if mixed);
   - `Directive` makes no completion claim when partial.
3. Hand the result to the weak model and confirm it self-routes to the suggested tool instead of dead-ending with "I cannot manually delete code."

---

## 8. Acceptance criteria

- [ ] Builds clean, all §6 tests pass.
- [ ] `AlreadySatisfied` never appears in `Actionable` and never affects severity/breaker.
- [ ] `Outcome` is substrate-derived in one place; no call site computes it.
- [ ] Every `Blocked` item with a known route carries a `ToolHint` with pre-filled path + method.
- [ ] An unroutable `FailureReason` yields `SuggestedTool == null` (loud), never a fabricated or stale tool name.
- [ ] Hints resolve through `ToolGraph.ProducersOf`; no hand-authored tool-name strings in failure handling.
- [ ] Producer selection is deterministic across restarts.
- [ ] Pilot is limited to `UpliftCallers`; no other tool's output shape changed.

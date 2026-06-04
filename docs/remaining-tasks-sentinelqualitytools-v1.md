# RoslynSentinel — Remaining Implementation Tasks
<!-- v1 -->

## Context

Three files have already been updated and are ready to merge:

| File | Changes |
|---|---|
| `ValidationEngine.cs` | `ValidateChangesAsync` now performs delta validation (introduced errors only); files not in solution pass through instead of blocking |
| `PersistentWorkspaceManager.cs` | `ApplyProposedChangesAsync` now captures pre-images before writing; `ApplyChangesResult` gains `PreImages` field |
| `SentinelWorkspaceTools.cs` | `ProposedChange` and `StagedChange` both gain `validateOnApply=true` gate; both write forensic blobs after apply via `WriteBlobForApplyAsync`; `UndoLastApply` description updated |

The one file **not yet updated** is `SentinelQualityTools.cs`. It requires two changes described below.

---

## Task 1 — Populate `BeforeSource` on `OperationItemRecord` at all batch-tool succeeded sites

### Why

`UndoLastApply` requires `Outcome == "succeeded" && BeforeSource != null` to revert a file.
`ApplyProposedChangesAsync` now returns `PreImages` (a `IReadOnlyDictionary<string, string?>`) on
its result. Every call site that builds a succeeded `OperationItemRecord` must read from `PreImages`
and populate `BeforeSource`. Without this, blobs are written but contain no reversible content.

### What to change

Search `SentinelQualityTools.cs` for every block matching this pattern:

```csharp
items.Add(new OperationItemRecord
{
    FilePath   = <someFilePath>,
    MethodName = <someMethodName>,   // may or may not be present
    Outcome    = "succeeded",
    // BeforeSource is absent — THIS is what needs fixing
});
```

At each such site, the preceding code calls `ApplyProposedChangesAsync` and stores the result.
Capture `BeforeSource` from that result's `PreImages` and set it on the record.

**Pattern to apply at every succeeded record site:**

```csharp
// Before (example — exact variable names vary by site):
var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
    new Dictionary<string, string> { { target.FilePath, updatedSource } });

items.Add(new OperationItemRecord
{
    FilePath   = target.FilePath,
    MethodName = methodName,
    Outcome    = "succeeded",
});

// After:
var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
    new Dictionary<string, string> { { target.FilePath, updatedSource } });

string? beforeSource = null;
applyResult.PreImages?.TryGetValue(target.FilePath, out beforeSource);

items.Add(new OperationItemRecord
{
    FilePath     = target.FilePath,
    MethodName   = methodName,
    Outcome      = "succeeded",
    BeforeSource = beforeSource,
});
```

### Scope

Use `search_solution_text` with pattern `Outcome    = "succeeded"` (or `Outcome = "succeeded"`) to
find all sites in `SentinelQualityTools.cs`. Apply the `BeforeSource` capture at each one.

Some sites apply changes to a **batch** of files in a loop, calling `ApplyProposedChangesAsync`
once per file per iteration. Each iteration has its own `applyResult` — use
`applyResult.PreImages?.TryGetValue(target.FilePath, out beforeSource)` scoped to that iteration.

Do **not** modify `failed`, `skipped`, or `rolledback` records — only `succeeded` records need
`BeforeSource`.

### Verification

After all sites are updated, confirm with `get_diagnostics(scope=solution, summarize=true)` that
zero new errors were introduced. Then confirm at least one `OperationItemRecord` construction sets
`BeforeSource` by reading back a representative record.

---

## Task 2 — Update `UndoLastApply` description in tool attribute (already done in SentinelWorkspaceTools.cs — verify not duplicated in SentinelQualityTools.cs)

The `UndoLastApply` method lives in `SentinelWorkspaceTools.cs` and its description was updated
there. Verify `SentinelQualityTools.cs` does not contain a duplicate `UndoLastApply` definition or
description attribute. If it does, remove the duplicate.

---

## Non-goals for this task

The following are **out of scope** for this agent session and should not be touched:

- `OperationBlobWriter.cs` — no changes needed
- `BatchTypes.cs` — no changes needed
- `ValidationEngine.cs`, `PersistentWorkspaceManager.cs`, `SentinelWorkspaceTools.cs` — already updated; do not re-edit
- The `modify_attribute` tool extension (`action=replace`) and dedicated `[Description]` attribute
  editing tool — separate future task, not part of this session
- `AfterSource` population — optional, deferred; do not add unless explicitly asked

---

## Important constraints

- **Use `staged_change(action=validate)` before every `staged_change(action=apply)`.**
  `validateOnApply=true` is now the default on the tool but confirming via explicit validate first
  is good practice during this session.
- **Use `get_diagnostics(scope=solution, summarize=true)` after the final apply** to confirm zero
  new errors before declaring the task complete.
- Do not declare success without a clean diagnostic check. A passing build is the success criterion.
- If `undo_last_apply` is needed at any point, use the `changeId` returned from the apply result.

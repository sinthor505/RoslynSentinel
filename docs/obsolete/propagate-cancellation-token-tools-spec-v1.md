# RoslynSentinel — `propagate_cancellation_token_*` Tools Specification
<!-- v1 -->

**Date:** 2026-05-28
**Purpose:** Add three new MCP tools to RoslynSentinel for propagating `CancellationToken` parameters to inner async call sites. Closes the gap identified during async migration where methods already have a CT parameter but don't forward it to their async callees.
**Target server:** RoslynSentinel (current paid extension version, source available locally)
**Mode:** Implementation spec — to be handed to an agent with workspace access to the RoslynSentinel source

---

## Context

During the Avaal Express async migration, the agent encountered ~150 call sites across ~30 files where async methods already had a `CancellationToken cancellationToken` parameter but were calling other async methods without forwarding the token. Example:

```csharp
public async Task<DataTable> GetTripsAsync(LoginUser user, CancellationToken cancellationToken = default)
{
    return await CommonSearch.searchAsync(sql.Format(user.CompanyId));  // ← missing cancellationToken
}
```

Should become:

```csharp
public async Task<DataTable> GetTripsAsync(LoginUser user, CancellationToken cancellationToken = default)
{
    return await CommonSearch.searchAsync(sql.Format(user.CompanyId), cancellationToken);  // ← forwarded
}
```

**Existing tools that do NOT cover this case:**

- `apply_cancellation_token_to_file` — explicitly skips methods that already have a CT parameter
- `add_cancellation_token_to_method` — adds CT and propagates in the same pass, but only targets methods *without* CT
- `find_cancellation_token_not_forwarded` — finds the problem but doesn't fix it

**The gap:** No tool handles "method already has CT, propagate it to all eligible callees."

---

## Tools to implement

Three tools in the established method/file/batch granularity pattern:

| Tool | Scope | Purpose |
|---|---|---|
| `propagate_cancellation_token_in_method` | Single method | Surgical propagation in one method |
| `propagate_cancellation_token_in_file` | Single file | All eligible methods in one file |
| `propagate_cancellation_token_batch` | List of targets | Batch operation across solution |

Additionally, **enhance `run_bridge_batch` and `run_uplift_batch`** with an optional `propagateCancellationTokens` flag (defaults to `true`) to compose propagation into the existing batch workflows.

---

## Tool 1 — `propagate_cancellation_token_in_method`

### Signature

```csharp
[McpServerTool]
[Description(
    "Propagates an existing CancellationToken parameter from a method to all eligible " +
    "async callees within its body. Locates the method by name in the file. For each call " +
    "to an async method that has a CancellationToken overload but isn't receiving the token, " +
    "appends ', cancellationToken' (or the actual parameter name) to the call site. " +
    "Preconditions: target method must already have a CancellationToken parameter. " +
    "Returns a change-set via autoStage; caller must use apply_staged_changes to commit. " +
    "Does not modify call sites that already forward a CT or call methods without CT overloads.")]
public async Task<PropagateCtResult> PropagateCancellationTokenInMethod(
    string filePath,
    string methodName,
    bool   autoStage = true)
```

### Return type

```csharp
public class PropagateCtResult
{
    public string?       ChangeId           { get; set; }   // null if autoStage=false or no changes
    public string        MethodName         { get; set; }
    public string        TokenParameterName { get; set; }   // usually "cancellationToken"
    public List<CallSiteForward> Forwarded  { get; set; }   // call sites that were rewritten
    public List<CallSiteSkip>    Skipped    { get; set; }   // call sites that were skipped, with reason
    public int           ForwardedCount     { get; set; }
    public int           SkippedCount       { get; set; }
    public bool          MethodFound        { get; set; }
    public string?       Error              { get; set; }   // non-null only on hard failure
}

public class CallSiteForward
{
    public string CalleeMethod     { get; set; }   // e.g. "searchAsync"
    public string CalleeType       { get; set; }   // e.g. "CommonSearch"
    public int    Line             { get; set; }
    public string BeforeSnippet    { get; set; }
    public string AfterSnippet     { get; set; }
}

public class CallSiteSkip
{
    public string CalleeMethod     { get; set; }
    public int    Line             { get; set; }
    public string Reason           { get; set; }   // see "Skip reasons" below
}
```

### Skip reasons (must be one of these exact strings)

- `"AlreadyForwarded"` — call site already passes a CancellationToken argument
- `"NoCancellationTokenOverload"` — callee method has no overload accepting CancellationToken
- `"AmbiguousOverload"` — multiple matching overloads exist, can't determine which to use
- `"NamedArgumentCollision"` — call uses named arguments and the position would conflict
- `"CalleeNotAsync"` — call site is to a sync method (shouldn't forward CT to sync calls)

### Implementation notes

- Locate the method via `MethodDeclarationSyntax.Identifier.Text == methodName` (case-sensitive).
- Extract the CT parameter name from the method signature (don't hardcode `cancellationToken` — the codebase might use `ct` or `token`).
- Use Roslyn's `SemanticModel` to resolve each call site's callee symbol. Check whether the callee has any overload with a `CancellationToken` parameter.
- For positional argument calls: append `, <tokenParameterName>` to the argument list.
- For calls already using named arguments: append `, cancellationToken: <tokenParameterName>`.
- For trailing parameter calls (last arg is multi-line / expression): preserve formatting via Roslyn's syntax trivia handling.
- Skip await-wrapped calls and non-await async calls uniformly (both can forward CT).
- Apply transformations in **reverse line order** within the file to avoid position offset shifts as the document is rewritten.

### Files to modify

- `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — add `PropagateCancellationTokenInMethodAsync(Document doc, string methodName)` returning `(updatedSource, List<CallSiteForward>, List<CallSiteSkip>)`
- `RoslynSentinel.Server/SentinelQualityTools.cs` — add `[McpServerTool]` registration that calls the engine method and wraps the result in `PropagateCtResult`

### Tests required

1. **Happy path** — method with CT, calls one async method with CT overload → call site rewritten
2. **No matching overload** — callee has no CT overload → skipped with `NoCancellationTokenOverload`
3. **Already forwarded** — call site already passes CT → skipped with `AlreadyForwarded`
4. **Multiple call sites in one method** — all eligible sites rewritten in single pass
5. **Method not found** — `MethodFound = false`, no changes
6. **Method has no CT parameter** — `Error = "Method does not have a CancellationToken parameter"`, no changes
7. **Named arguments** — call uses `await Foo(x: 1)` → rewritten to `await Foo(x: 1, cancellationToken: cancellationToken)`
8. **Custom CT parameter name** — method uses `CancellationToken ct = default` → forwards `ct` not `cancellationToken`
9. **Idempotency** — running twice produces no additional changes the second time
10. **autoStage = false** — returns the updated source as string, no ChangeId

---

## Tool 2 — `propagate_cancellation_token_in_file`

### Signature

```csharp
[McpServerTool]
[Description(
    "Propagates CancellationToken parameters to all eligible call sites across all methods " +
    "in a file. For each method that has a CancellationToken parameter, applies the same " +
    "logic as propagate_cancellation_token_in_method. Methods without a CT parameter are " +
    "skipped silently. Returns aggregated per-method results. Use this after bulk CT-adding " +
    "operations (e.g. apply_cancellation_token_to_file) to complete the forwarding step. " +
    "Returns a change-set via autoStage.")]
public async Task<PropagateCtFileResult> PropagateCancellationTokenInFile(
    string filePath,
    string[]? methodNames = null,   // optional: restrict to specific methods; null = all eligible
    bool   autoStage   = true)
```

### Return type

```csharp
public class PropagateCtFileResult
{
    public string?                   ChangeId       { get; set; }
    public string                    FilePath       { get; set; }
    public List<PropagateCtResult>   PerMethod      { get; set; }   // one entry per method processed
    public int                       TotalForwarded { get; set; }
    public int                       TotalSkipped   { get; set; }
    public int                       MethodsProcessed { get; set; }
    public int                       MethodsSkipped { get; set; }   // methods without CT param
}
```

### Implementation notes

- Reuse `PropagateCancellationTokenInMethodAsync` from Tool 1.
- Enumerate all `MethodDeclarationSyntax` in the file.
- For each method: check if it has a CT parameter. If yes, process it. If no, skip silently (increment `MethodsSkipped`).
- Apply `methodNames` filter when provided — restricts processing to the named methods (case-sensitive match).
- Apply all transformations on a single `SyntaxTree` re-parse cycle, not per-method (avoid position drift across methods).
- One single staged change containing all file-level edits, not one per method.

### Tests required

1. **Multiple methods with CT, multiple call sites each** — all rewritten in one pass
2. **File with mix of CT and non-CT methods** — only CT methods processed, others skipped silently
3. **`methodNames` filter** — only specified methods processed even if others are eligible
4. **File with no eligible methods** — returns `MethodsProcessed = 0`, no error
5. **File with one method, no eligible call sites** — `TotalForwarded = 0`, no error

---

## Tool 3 — `propagate_cancellation_token_batch`

### Signature

```csharp
[McpServerTool]
[Description(
    "Batch propagation of CancellationToken across multiple files. Accepts a list of " +
    "file paths (with optional per-file method filters). Processes each file independently, " +
    "validating each in-memory before writing. Failed files do not block successful ones. " +
    "On failure, flags the offending methods with [MigrationCandidate(\"CancellationTokenForwardCandidate\", " +
    "Reason=\"<specific reason>\")] for later manual review. Returns aggregated results with " +
    "per-file status. Use this for solution-wide CT propagation sweeps.")]
public async Task<PropagateCtBatchResult> PropagateCancellationTokenBatch(
    PropagateCtBatchInput input)
```

### Input/return types

```csharp
public class PropagateCtBatchInput
{
    public List<PropagateCtFileTarget> Targets         { get; set; }   // files to process
    public bool                        DryRun          { get; set; } = false;
    public int                         MaxFiles        { get; set; } = 100;
    public bool                        FlagFailures    { get; set; } = true;   // add MigrationCandidate on failure
}

public class PropagateCtFileTarget
{
    public string    FilePath    { get; set; }
    public string[]? MethodNames { get; set; }   // optional filter
}

public class PropagateCtBatchResult
{
    public List<PropagateCtFileResult> Applied         { get; set; }
    public List<PropagateCtFileFailure> Failed         { get; set; }
    public int                         TotalForwarded  { get; set; }
    public int                         TotalSkipped    { get; set; }
    public int                         RemainingFiles  { get; set; }   // if MaxFiles cap hit
    public string                      StopReason      { get; set; }   // "batch_complete", "budget_exhausted", "validation_failed"
}

public class PropagateCtFileFailure
{
    public string FilePath        { get; set; }
    public string Reason          { get; set; }   // "ValidationFailed", "MethodNotFound", "ParseError"
    public List<DiagnosticInfo> Diagnostics { get; set; }
    public List<string>         FlaggedMethods { get; set; }   // methods that were flagged with MigrationCandidate
}
```

### Implementation notes

- Reuse `PropagateCancellationTokenInFileAsync` from Tool 2.
- For each file: process → validate via `ValidationEngine.ValidateChangesAsync` (in-memory fork) → write or flag.
- On validation failure with `FlagFailures = true`: add `[MigrationCandidate("CancellationTokenForwardCandidate", Score = 90, Reason = "<specific reason>", FlaggedDate = "<today>")]` to each method that failed propagation. The attribute injection uses the existing `flag_migration_candidate` engine method.
- Respect `MaxFiles` cap; remaining files contribute to `RemainingFiles`.
- Do not abort on first failure — process all targets and report aggregated results.

### Tests required

1. **All files succeed** — `Applied` populated, `Failed` empty
2. **Mixed success/failure** — both lists populated, no cross-contamination
3. **Validation failure with `FlagFailures = true`** — file appears in `Failed`, methods get `MigrationCandidate` attribute
4. **Validation failure with `FlagFailures = false`** — file appears in `Failed`, no attribute injection
5. **MaxFiles cap** — stops at cap, `RemainingFiles` reflects unprocessed count, `StopReason = "budget_exhausted"`
6. **DryRun** — no files written, no attributes added, results show what *would* happen

---

## Enhancement — `propagateCancellationTokens` flag on existing batch tools

### `run_bridge_batch`

Add parameter:

```csharp
public async Task<BridgeBatchResult> RunBridgeBatch(
    int    maxBridges                 = 10,
    int    scoreThreshold             = 100,
    bool   dryRun                     = false,
    bool   propagateCancellationTokens = true)    // NEW
```

**Behavior when `propagateCancellationTokens = true`:**

For each successfully bridged method, after the bridge transformation is applied:
1. Call `PropagateCancellationTokenInFileAsync` on the file containing the new async overload, restricted via `methodNames` to just that overload.
2. Validate the composite (bridge + propagation) via `ValidationEngine.ValidateChangesAsync`.
3. On composite success: commit both changes together.
4. On composite failure:
   - Revert the in-memory fork (atomic rollback per candidate).
   - Flag the method with `[MigrationCandidate("CancellationTokenForwardCandidate", Reason = "<specific>")]`.
   - The bridge itself is NOT applied — the whole candidate is treated as failed.
   - Continue to next candidate.

**Why all-or-nothing per candidate:** prevents the "bridge succeeded but propagation didn't" intermediate state that requires manual cleanup.

### `run_uplift_batch`

Same flag with analogous behavior:

```csharp
public async Task<UpliftBatchResult> RunUpliftBatch(
    string bridgedMethodName,
    int    maxCallers                  = 10,
    bool   dryRun                      = false,
    bool   propagateCancellationTokens = true)    // NEW
```

For each successfully uplifted caller:
1. After the uplift rewrite, propagate CT in the modified caller method (it now has CT and may have other callees needing forwarding).
2. Validate composite, commit or flag as above.

---

## Architectural decisions documented

### Why three granularity tiers (method/file/batch)

Mirrors the existing pattern (`add_cancellation_token_to_method` / `apply_cancellation_token_to_file` / `run_bridge_batch`). The agent learns the granularity pattern once and applies it consistently. Method-level for surgical fixes, file-level for cleanup, batch-level for sweeps.

### Why the propagation flag is opt-out, not always-on

Failure isolation. If propagation is unconditionally embedded, you can't diagnose "bridge worked but propagation didn't" vs. "bridge failed." Keeping it as a flag (default true) preserves the diagnostic ability while making the common case automatic.

### Why failures flag with `MigrationCandidate` rather than logging

The attribute IS the work queue for the next session. Logs disappear; attributes persist in source and are queryable via `find_migration_candidates`. This is the same pattern that makes the existing migration state robust across sessions.

### Why all-or-nothing per candidate in the composite case

Avoids intermediate states. A bridge without forwarded CT in its overload is a half-applied migration that's harder to recover from than a candidate that simply hasn't been processed yet. Atomicity at the candidate level keeps the migration's invariant: every method is either fully migrated or fully pending.

### Why skip reasons are enumerated strings, not free text

Enables `find_migration_candidates` filtering by reason. An agent or human reviewing failures can query "show me everything that failed with `AwaitInLockStatement`" and get a clean work queue. Free-text reasons would require parsing.

---

## Implementation order

1. **Tool 1 — `propagate_cancellation_token_in_method`** (foundational)
2. **Tool 2 — `propagate_cancellation_token_in_file`** (orchestrates Tool 1)
3. **Tool 3 — `propagate_cancellation_token_batch`** (orchestrates Tool 2)
4. **Enhancement — flags on `run_bridge_batch` and `run_uplift_batch`** (composes Tool 2 into existing batch tools)

Each step depends on the previous. Get Tool 1's logic and tests solid before building on top.

---

## Build and deployment notes

The RoslynSentinel MCP server process holds a file lock on `RoslynSentinel.Server.exe` while VS Code is running. `dotnet build` will compile cleanly but fail on the final exe-copy step with MSB3027. **Restart VS Code (or stop the MCP server process)** before the final build to release the lock and deploy the new binary.

---

## Key files in RoslynSentinel

- `RoslynSentinel.Server/SentinelQualityTools.cs` — all `[McpServerTool]` registrations
- `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — async transformation logic; existing `AddCancellationTokenToMethodAsync` shows the pattern for parameter detection and call-site rewriting
- `RoslynSentinel.Server/AntiPatternEngine.cs` — `FindCancellationTokenNotForwardedAsync` shows the detection logic; can be reused/refactored as the basis for the rewrite
- `RoslynSentinel.Server/ValidationEngine.cs` — `ValidateChangesAsync` for in-memory composite validation
- `RoslynSentinel.Server/MigrationCandidateEngine.cs` (or wherever `flag_migration_candidate` lives) — for failure flagging integration

---

## Acceptance criteria

The implementation is complete when:

1. All three tools are registered and callable via MCP.
2. All test cases listed for each tool pass.
3. `run_bridge_batch` and `run_uplift_batch` accept the `propagateCancellationTokens` flag and behave as specified.
4. Running `propagate_cancellation_token_batch` on the Avaal3 solution successfully forwards CT in the ~30 files / ~150 call sites identified during the manual migration session.
5. Methods that fail propagation are flagged with `[MigrationCandidate("CancellationTokenForwardCandidate", ...)]` and discoverable via `find_migration_candidates(pattern: "CancellationTokenForwardCandidate")`.
6. Build is green after each tool's batch operation completes.
7. No regressions in existing tools (`add_cancellation_token_to_method`, `apply_cancellation_token_to_file`, `run_bridge_batch`, `run_uplift_batch` continue to behave as before when `propagateCancellationTokens` is not specified — defaults preserve existing behavior).

<!-- v1 -->

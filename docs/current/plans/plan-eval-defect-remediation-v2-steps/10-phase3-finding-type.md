# Step 3.2 — `Finding` type, `ToolResult<T>`/`EngineResultWrapper<T>` additions, bridge sweep

## Prior state

Step 3.1 already landed: `DirectiveKind { Proceed, ReviewRequired }` exists and is wired into
`OperationSummary`, `BatchResultSummary`, and `BreakerStatusReport`. Solution builds clean.

## Context

**Corrected vs. an earlier draft of this work:** `EngineResultWrapper<T>` is **not** the MCP wire
type — `ToolResult<T>` is. Every tool method manually bridges the two by hand today (see
`GetDiagnostics` in `SentinelWorkspaceTools.cs` or similar as the representative pattern) — there
is no automatic adapter.

**Files this step touches:**
- New `RoslynSentinel.Common/Finding.cs`
- `RoslynSentinel.Common/EngineResultWrapper.cs`
- `RoslynSentinel.Common/ToolResult.cs`
- `RoslynSentinel.Common/DataTag.cs`
- Every file with a manual `EngineResultWrapper<T>` → `ToolResult<T>` bridge (found via sweep
  below)

## Task

### A. `Finding` type

```csharp
public sealed record Finding(string Source, string Message, FindingSeverity Severity = FindingSeverity.Info);
public enum FindingSeverity { Info, Caution, Warning }
```

Name it `Finding`/`Findings`, not `OperationFinding` — the orientation breaker and
`DiffHunkAnalyzer` findings (wired in steps 3.3/3.4) aren't coupled to `OperationSummary`'s
Asyncify-specific outcome machinery, so borrowing that name would imply a relationship that
doesn't exist.

**This file needs two top-level types, which takes one `CreateFile` call plus one `Member(add)`
call, not a single write:**
1. `CreateFile(filepath: ".../Finding.cs", namespaceName: "RoslynSentinel.Common", typeKind: record, typeName: "Finding")`.
   `CreateFile` stubs an empty record — `public record Finding\n{\n}\n` — not the positional
   constructor form shown above. Follow with `Member(add, containerName: "Finding")` to give it
   the positional parameter list and the `Severity` default, or replace the stub's declaration
   line directly via `Member(replace)` once the file exists — either way, don't leave it as the
   empty stub.
2. `Member(add, containerName: null, newMemberSource: "public enum FindingSeverity { Info, Caution, Warning }")`
   — this is `CreateFile`'s documented path for a file's **second** top-level type; `CreateFile`
   itself only ever stubs one type per call.

### B. Add `Findings` to both wrapper types

Add `IReadOnlyList<Finding> Findings { get; init; } = []` to both `EngineResultWrapper<T>` and
`ToolResult<T>`. Purely additive — no existing constructor call site should break, since this is
an `init` property with a default.

Also add `DirectiveKind DirectiveKind { get; init; } = DirectiveKind.Proceed` to `ToolResult<T>`
itself (parallel to `Findings`) — step 3.4 (`ApplyUnifiedDiff`) needs a place to put it, and
`ToolResult<T>` doesn't carry any directive-like field today.

### C. Bridge-site sweep — do this before steps 3.3/3.4

Search the solution (`SearchSolutionText` or similar) for the pattern of a manual
`EngineResultWrapper<T>` → `ToolResult<T>` bridge — look for `TryGetData` followed by
`new ToolResult<`. At every bridge site found, copy `Findings` across from the wrapper to the
result. Do this sweep now, so the two concrete producers added in steps 3.3 and 3.4 have
somewhere to land — if you defer this sweep, those steps' `Findings` will be silently dropped at
the bridge.

### D. `DataTag.SearchPattern`

Add `SearchPattern` to the `DataTag` enum (`RoslynSentinel.Common/DataTag.cs`). If, once step 3.3
is implemented, nothing actually ends up consuming it, drop it rather than leaving dead enum
surface — don't add anything beyond what step 3.3 needs, but don't skip adding it now either,
since step 3.3 depends on it existing.

## Gate

Build the solution clean (`Build` MCP tool). No functional test changes expected yet — this step
is purely additive plumbing. If the build reveals any bridge site you missed in the sweep (C),
fix it now rather than deferring. Proceed to
[11-phase3-orientation-breaker.md](11-phase3-orientation-breaker.md).

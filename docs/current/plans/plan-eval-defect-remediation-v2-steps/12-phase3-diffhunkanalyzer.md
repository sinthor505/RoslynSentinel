# Step 3.4 — Wire `DiffHunkAnalyzer` out of `DiffEngine.ApplyDiff`

## Prior state

Step 3.3 already landed: the orientation breaker's trip-check now fires from inside
`WorkspaceReadNavigationImpl`'s search path itself, carrying a `Finding` on the triggering call's
own result. `ListAll` is now mentioned in the base `NoSearchMatchesException` hint unconditionally.
Solution builds clean, `RoslynSentinel.Tests.Basic` green.

## Context

**Files this step touches:**
- `RoslynSentinel.Common/DiffEngine.cs` (`ApplyDiff`, ~lines 53-70)
- `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` (`ApplyUnifiedDiff`, ~lines 542-660)
- New `RoslynSentinel.Tests.Basic/DiffEngineTests.cs`

**Scope note — narrow, explicit exception to the engine-signature freeze:** `ApplyUnifiedDiff`
never touches `EngineResultWrapper<T>` at all today — `DiffHunkAnalyzer`'s report is fully
swallowed inside `DiffEngine.ApplyDiff`, which returns a bare `SourceText`. Surfacing it requires
changing that method's return type. This has already been decided as an explicit, narrow
exception to the general rule against touching engine signatures, scoped **only** to adding the
diff report to `ApplyDiff`'s return value — do not use this as license to change other engine
signatures in this pass.

## Task

### A. Change `DiffEngine.ApplyDiff`'s return type

```csharp
public sealed record DiffApplyResult(SourceText Text, DiffReport Report);
```

The method's internals are otherwise unchanged — it already computes
`DiffHunkAnalyzer.Analyze(unifiedDiff)` and logs when `HasFindings`; now it also returns the
report instead of discarding it.

Changing this return type breaks every existing caller that used the old `SourceText` return
value directly — that's expected. Before searching for callers, run a build (quickBuild scope)
and use its errors as the authoritative list of what needs fixing in Part B, rather than relying
solely on a text search — a caller a search missed will show up here.

### B. Update callers

Search for all callers of `ApplyDiff` and update them to unwrap `.Text` where they currently use
the return value directly. `ApplyUnifiedDiff` (`SentinelWorkspaceTools.cs:588`) is the
Phase-3-relevant one, but fix every caller the search finds, not just that one. Cross-check
against the build errors from Part A: every caller listed there must be fixed here.

### C. Wire the report into `ApplyUnifiedDiff`'s result

When constructing the final `ToolResult<object>` (~line 612), populate `Findings` from the report
when `HasFindings` (`Source: "DiffHunkAnalyzer"`), and set `DirectiveKind = ReviewRequired` — the
diff still applies (`Success = true`), but the model is told to re-read rather than trust,
matching the corrected spec's own recommendation.

### D. New tests

Create `RoslynSentinel.Tests.Basic/DiffEngineTests.cs` — keep this **separate** from
`DiffHunkAnalyzerTests.cs`, which tests the analyzer in isolation; this new file tests the
plumbing that surfaces its output. Two tests:
1. Clean diff → `Findings` empty, `DirectiveKind == Proceed`.
2. Malformed-header diff (reuse the existing analyzer test fixture) run through the full
   `ApplyUnifiedDiff` path → `Findings` non-empty, `Success == true`, `DirectiveKind == ReviewRequired`.

## Gate — Phase 3 gate

Run `RoslynSentinel.Tests.Basic`, `RoslynSentinel.Tests.Battery`, and
`RoslynSentinel.Tests.Asyncify` via `RunTest` (the `DirectiveKind` migration in step 3.1 touches
`OperationSummary`, which `OperationOutcomeRoutingTests.cs` builds against — Asyncify must be
included). All three green. Full solution build clean. Zero regressions versus Phase 2's gate.

Once this gate passes, Phase 3 is complete. Proceed to
[13-final-verification.md](13-final-verification.md).

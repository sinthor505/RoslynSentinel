# Step 3.3 — Wire the orientation breaker into the triggering call itself

## Prior state

Step 3.2 already landed: `Finding`/`FindingSeverity` exist, `Findings` is on both
`EngineResultWrapper<T>` and `ToolResult<T>`, `DirectiveKind` is on `ToolResult<T>`, the
bridge-site sweep copied `Findings` across at every manual bridge, and `DataTag.SearchPattern`
exists. Solution builds clean.

## Context

**Files this step touches:**
- `RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs` (~line 510, search
  implementation and `NoSearchMatchesException` catch path)
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (orientation breaker,
  `RecordSearchOutcome`, ~line 1972)
- `RoslynSentinel.Tests.Basic/OrientationBreakerFilterTests.cs` (extend, don't duplicate)

**The actual bug being fixed:** the breaker's trip counter
(`PersistentWorkspaceManager.RecordSearchOutcome`) is driven today by an MCP request filter that
parses `SearchSolutionText`'s *already-serialized* JSON response for a `totalRecords` field — it
is not called from inside `SearchSolutionText`/`WorkspaceReadNavigationImpl` at all. That's why
the eval corpus showed byte-identical `NoMatches` prose on the exact call that tripped the
breaker: the trip message only appears as a pre-check short-circuit on the *next* call, after the
damage (three burned turns in the corpus) is already done.

## Task

### A. Move the trip-check inline

Call the breaker's trip-check from inside `WorkspaceReadNavigationImpl`'s search implementation
itself, immediately after the match count is known — not only from the external MCP request
filter. This way the **triggering call's own result** — including its `NoSearchMatchesException`
catch path (~lines 510-536) — carries a `Finding` (`Source: "OrientationBreaker"`) with the
`ListAll`-mentioning hint text, not just a bare `NoMatches` message.

Consider having `RecordSearchOutcome` return a "just tripped" signal so the filter and this new
in-line check consume one shared source of truth instead of two independently-derived trip
detections — avoiding exactly the kind of divergence this whole remediation effort is about. Do
not implement two separate, potentially-diverging trip-detection mechanisms.

### B. Broaden the base hint text unconditionally

Add `ListAll` to the base `NoSearchMatchesException` hint text unconditionally (today it mentions
`LocateSymbol`/`GetFileOutline`/`ProjectDoc` but not `ListAll`, even though `ListAll` is what
actually worked in the eval corpus) — this applies independent of whether the breaker has
tripped, i.e. even on the first zero-match call.

### C. Test

Extend the existing `OrientationBreakerFilterTests.cs` (it already covers filter-level end-to-end
behavior — don't duplicate that coverage) with a new test driving 3 consecutive zero-match
`SearchSolutionText` calls and asserting the **3rd call's own result** carries the finding, not a
4th call.

## Gate

Build the solution clean. Run `RoslynSentinel.Tests.Basic` via `RunTest` and confirm the new test
plus all existing `OrientationBreakerFilterTests.cs` tests pass. Proceed to
[12-phase3-diffhunkanalyzer.md](12-phase3-diffhunkanalyzer.md).

# Orientation breaker for SearchSolutionText thrashing

## Context

Raw LM Studio logs from the `.112`/`.113` model-eval runs (e.g.
`lmstudio_logs/192.168.1.113/2026-08-30.3.log`) show the model calling
`SearchSolutionText` 4-5 times in a row with slightly reworded guessed patterns after
each call returns zero matches, instead of switching to `ListAll`/`GetFileOutline`. This
happens despite two independent, already-present steering signals the model reads every
time: the system prompt says verbatim *"Repeatedly retrying SearchSolutionText with
slightly different guessed patterns after it returns no matches is a sign you should
switch to listing instead of searching"*, and `SearchSolutionText`'s own zero-match
response already appends *"Use ProjectDoc... or use GetFileOutline..."* as a warning.
`grep -c SearchSolutionText` across the log directory returns 5907 occurrences — this is
a large, recurring pattern, not a one-off.

Since the model demonstrably ignores prose guidance it already has (twice per turn), a
third restatement (e.g. an LLM-mediated `GetToolGuidance` tool, considered and rejected
for this failure mode) is unlikely to help and would add a real per-call cost: another
round-trip through the same weak local model that produced the thrashing. The fix instead
is mechanical enforcement, modeled on the existing mutating-tools circuit breaker
(`ICircuitBreaker` / `PersistentWorkspaceManager.cs` lines ~1600-1768): after too many
consecutive zero-match `SearchSolutionText` calls, force the agent onto a small allowlist
of orienting tools until it makes one successful call outside that streak.

Unlike the existing breaker, this one must auto-reset (the existing one is explicitly
"Manual only — never auto-reset by design", which is wrong for a low-stakes orientation
nudge) and is enforced globally via the MCP request-filter chokepoint rather than
per-call-site `CheckBreaker()` checks scattered across every mutating tool.

Rather than a wholly separate, unrelated interface, the two breakers share a minimal base
shape — factored out by splitting today's `ICircuitBreaker` (`RoslynSentinel.Common\ICircuitBreaker.cs`)
into a base interface plus two specializations:

```csharp
/// Minimal shared shape: something a caller can check "is this blocking me" against,
/// generically, without knowing which concrete breaker it's talking to.
public interface ICircuitBreaker
{
    bool IsTripped();
    /// Null/empty when not tripped; human-readable detail (counters, directive, etc.) when tripped.
    string? StateMessage();
    void Reset();
}

/// Today's mutating-tools breaker, renamed. Manual-only reset (never auto — see ResetBreaker's
/// existing "Manual only — never auto-reset by design" contract, preserved as-is).
public interface IManualCircuitBreaker : ICircuitBreaker
{
    BatchResultSummary? CheckBreaker();
    void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped);
    BreakerStatusReport GetBreakerStatus();
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    string GetBreakerDirective();
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    string GetBreakerSeverity();
}

/// New: the orientation breaker. Resets itself (via the request filter), no manual reset tool.
public interface IAutomaticCircuitBreaker : ICircuitBreaker
{
    /// Records a SearchSolutionText outcome. matchCount is the tool's totalRecords.
    void RecordSearchOutcome(int matchCount);
}
```

`IManualCircuitBreaker.Reset()`/`ICircuitBreaker.Reset()` maps to today's `ResetBreaker()` —
rename `ResetBreaker()` → `Reset()` and `CheckBreaker()` stays as the richer,
mutating-tools-specific query (`IsTripped()` becomes a thin `_breakerOpen` read, and
`StateMessage()` returns the same directive text `CheckBreaker()`/`GetBreakerStatus()`
already build). Existing callers of `ICircuitBreaker` (the 9 `CheckBreaker()` call sites in
`SentinelCommentingTools.cs`/`SentinelAsyncifyTools.cs`, plus the `ResetBreaker`/
`GetBreakerStatus` MCP tools in `SentinelWorkspaceTools.cs`) switch their declared
dependency type from `ICircuitBreaker` to `IManualCircuitBreaker` — mechanical rename, no
behavior change to the existing breaker.

`PersistentWorkspaceManager` implements all three interfaces (`ICircuitBreaker` is implied
by implementing both `IManualCircuitBreaker` and `IAutomaticCircuitBreaker`, but C# still
requires listing it or accepting the diamond is resolved implicitly — confirm during
implementation whether an explicit `ICircuitBreaker` listing is needed alongside the two
derived interfaces in the class declaration at line 27, since the class exposes two
*different* breakers' worth of state through the same base method names `IsTripped()`/
`StateMessage()`/`Reset()`, which is not representable by a single implementation — see
open question below).

New state for the automatic breaker, added as a new partial-class region on
`PersistentWorkspaceManager` (mirrors the existing "── Circuit breaker public API ──"
region at line 1601):
- `private int _consecutiveZeroMatchSearches;`
- `private bool _orientationBreakerOpen;`
- `private readonly object _orientationBreakerLock = new();`
- `private const int OrientationBreakerTripThreshold = 3;` (matches the user's suggested N=3)

`RecordSearchOutcome(matchCount)`: under lock, if `matchCount > 0` reset the streak to 0;
else increment, and trip (`_orientationBreakerOpen = true`) once the streak reaches the
threshold.

The automatic breaker's `Reset()` clears `_orientationBreakerOpen` and the streak — called
by the filter, not exposed as an MCP tool (no `Get`/`Reset` tool needed for it; auto-reset
makes a manual reset tool unnecessary).

Add a throwing stub to `RoslynSentinel.Tests\Fakes\FakeWorkspaceManager.cs` for whichever
new members `IAutomaticCircuitBreaker` introduces, matching the existing
`CheckBreaker() => throw new NotImplementedException();` pattern (line 35) — confirm
during implementation whether `FakeWorkspaceManager` needs to implement
`IAutomaticCircuitBreaker` at all (depends whether it declares the full
`PersistentWorkspaceManager` interface surface or just what its test callers use).

**Open question to resolve during implementation:** `PersistentWorkspaceManager` implementing
both `IManualCircuitBreaker` and `IAutomaticCircuitBreaker` — each of which extends
`ICircuitBreaker` with the *same* method names (`IsTripped`/`StateMessage`/`Reset`) but
*different* underlying state — cannot be done with one implicit implementation of each
method, since `IsTripped()` can't mean both "mutating breaker tripped" and "orientation
breaker tripped" simultaneously. Use explicit interface implementation
(`bool IManualCircuitBreaker.IsTripped() => ...` / `bool IAutomaticCircuitBreaker.IsTripped() => ...`)
so each interface gets its own method body on the same class — this is the standard C#
pattern for exactly this situation and should be called out to whoever implements this so
it isn't a surprise mid-task.

### 2. New request filter in `ServiceRegistrationExtensionsBasic.cs`

Add one more `filters.AddCallToolFilter(...)` to the existing chain inside
`AddRoslynSentinelToolsBasic` (after the existing 4 filters, same
`WithRequestFilters(filters => { ... })` block, `RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs`
lines 156-315). Two responsibilities in one filter, ordered to match the existing style
(each filter calls `next(...)` and does its own work before/after):

**Before `next(...)`:** if `context.Server.Services?.GetService<PersistentWorkspaceManager>()`,
accessed as `IAutomaticCircuitBreaker`, reports `IsTripped() == true` and
`context.Params?.Name` is not one of the allowlisted tool names (`ListAll`,
`ListSolutionItems`, `GetFileOutline`, `ReadFile`), short-circuit immediately without
calling `next`, returning a `CallToolResult` with `IsError = true` and
`StateMessage()`'s text (e.g. *"Orientation breaker tripped: N consecutive
SearchSolutionText calls returned no matches. Only ListAll, ListSolutionItems,
GetFileOutline, and ReadFile are available until one of them succeeds. Call ListAll
(kind=all) or ListSolutionItems (kind=all) to find what you're looking for by browsing
instead of guessing."*). Mirrors the `SolutionNotLoadedException` short-circuit shape
already in filter #1 (lines 161-188).

**After `next(...)`:**
- If the call was `SearchSolutionText`: parse the response body's `totalRecords` (same
  `JsonDocument.Parse` + `TryGetProperty` pattern as filter #2, lines 197-237) and call
  `RecordSearchOutcome(totalRecords)`.
- Else if the breaker was tripped (`IsTripped()`) and the call succeeded
  (`result.IsError != true`): call `Reset()` (via `IAutomaticCircuitBreaker`). (Equivalent
  to "any successful allowlisted call" in practice, since only allowlisted tools can run at
  all while tripped — confirmed with the user, no extra allowlist re-check needed here.)

Wrap in try/catch + `Debug.WriteLine` on failure, matching every other filter in this file
— a filter bug must never take down tool calls.

### 3. `ListSolutionItems` gets a `kind=all` option

Per the user's suggested approach ("`ListAll` internally runs over each project... stores
results in a Dictionary keyed by file... returns `.Distinct()`"), apply the same
aggregation idea to `ListSolutionItems` (`RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs`
lines 119-243):

- Add `all` to `SolutionItemsKind` (`RoslynSentinel.Common\ToolEnums.cs` line 28-31,
  currently `projects, files, dependencies, solutionItems`).
- In `ListSolutionItems`, when `kind == SolutionItemsKind.all`: iterate every project in
  the solution (`solution.Projects`), collect each project's files (reusing the existing
  `kind=files` document-enumeration logic at lines 164-196, minus the `projectName`
  requirement) and dependencies (reusing `_dependencyEngine.GetProjectDependenciesAsync`
  per project, lines 209-226) plus `projects` and `solutionItems` (both already
  solution-wide, lines 129-162) — key entries by file path in a `Dictionary<FilePath, ...>`
  to naturally dedupe multi-targeted/shared files, then return `.Values` (or
  `.Distinct()` if using a flat list) as the combined payload. `projectName` is ignored
  when `kind=all` (or rejected as invalid-with-all — decide during implementation based on
  what's least surprising).
- `kind` remains `[ExternalInputRequired]` (still no default — `all` is an explicit choice,
  not silently implied) — the point is giving a tripped/uncertain model a real "show me
  everything" option, not changing the default for normal calls.
- Because this aggregates across every project (files + dependencies loop), it's real
  implementation work, not a one-line default change — size accordingly.

`ListAll` already defaults `kind = ListAllKind.all` (line 1754) — no change needed there.
`GetFileOutline` has no kind/filter parameter at all (always returns every member in the
file) — no change needed there either. So only `ListSolutionItems` needs the new `all`
option; the other two allowlisted tools already behave as "show everything" by default or
by construction.

### 4. Tests

- `RoslynSentinel.Tests.Battery` (or a new file, following the existing
  `ApplyDiffSizeGuardTests.cs`/`FilePathLock`-style focused-fixture pattern): unit tests
  directly against `PersistentWorkspaceManager` as `IAutomaticCircuitBreaker` — trip after
  N consecutive zero-match `RecordSearchOutcome` calls, confirm a mid-streak non-zero match
  resets the streak, confirm `Reset()` clears `IsTripped()`. Also a quick regression test
  that `PersistentWorkspaceManager` as `IManualCircuitBreaker` is unaffected by the rename
  (existing `CheckBreaker`/`RecordBatchOutcome`/`ResetBreaker`-via-`Reset` behavior
  unchanged) and that the two breakers' state is genuinely independent (tripping one
  doesn't affect `IsTripped()`/`StateMessage()` on the other, per the explicit-interface-
  implementation design).
- A filter-level test (find and follow the existing pattern for testing the other 4
  `AddCallToolFilter` filters — likely in `RoslynSentinel.Tests.Advanced` or wherever
  `ServiceRegistrationExtensionsBasic`'s filter chain is currently covered, if at all;
  confirm during implementation) exercising: N zero-match `SearchSolutionText` calls trips
  the breaker; a subsequent `ApplyDiff` call (non-allowlisted) is short-circuited with
  `IsError=true` and never reaches the real tool; a subsequent `ListAll` call is allowed
  through and resets the breaker; a normal (non-tripped) session is unaffected.
- `ListSolutionItems(kind: all)` tests: returns projects + solutionItems + every project's
  files + dependencies, deduped, without requiring `projectName`.

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors.
- Run the new unit tests + filter tests + `ListSolutionItems` tests; confirm pass.
- Run the full `RoslynSentinel.Tests.Battery` suite; confirm no new regressions beyond the
  known pre-existing `ReadFile_LargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync`
  failure (see `reference_known_failing_tests` memory).
- Real-model spot check: rerun a `SizeThresholdAgentTests`-style scenario (or replay one of
  the transcripts that showed the thrashing pattern, e.g. via `TranscriptReplayTests`-style
  harness if applicable) against `.112`/`.113` and confirm the model gets redirected to
  `ListAll`/`GetFileOutline` after 3 zero-match searches instead of continuing to guess
  patterns indefinitely.
- Build to 0 errors, then commit per [[feedback_build_before_commit]].

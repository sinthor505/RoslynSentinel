# Blocking error: ReadFile throws ArgumentNullException('scanId') for files in the 8KB-30KB range

**RESOLVED** — fixed in commit `962394c` ("Fix tool response size offload threshold mismatch. Added
plan docs."). `ScanResultHelper` was later renamed to `LargeResultHelper`; the offload threshold is
now a single shared constant, `LargeResultHelper.OffloadThresholdBytes = 30 * 1024`
([LargeResultHelper.cs:19](../../../../RoslynSentinel.Common/LargeResultHelper.cs#L19)), with no
orphaned 8KB constant anywhere in `RoslynSentinel.Common`. Verified against current code
2026-08-29. Moved here for history; no further action needed.

**Original status:** confirmed real bug, reproduced and root-caused. Blocking per the dog-fooding
policy — stopping here, no fix attempted in this session. Discovered via a 9B-model dog-food test
run, not through this session's own direct usage.

## Symptom

`ReadFile(filepath: ...\RoslynSentinel.Advanced\AdvancedStructuralEngine.cs)` (no `startLine`/
`endLine`) fails every time with:

```
success: false
errorCode: "Exception"
message: "ReadFile failed unexpectedly (ArgumentNullException). Details: Value cannot be null. (Parameter 'scanId')"
```

Reproduced identically across 6 consecutive retries in
`RoslynSentinel-AgentTesting/RoslynSentinel NormalizeWhitespace Test - 9B - run 2 - 2026-08-27 23.43.md`
(qwen3.5-9b-coder via LM Studio, level-2 test plan, step 2). The 7th attempt aborted with a
transport-level timeout ("This operation was aborted") rather than the same exception — likely
just a slow/stuck call, not a separate bug; not investigated further here.

## Root cause — confirmed by reading the source, not just inferred

Two independent size thresholds disagree about when a `ReadFile` result should be offloaded to a
scan file, and the disagreement zone crashes:

1. **[SentinelWorkspaceTools.cs:1200](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1200)**
   — `ReadFile`'s own gate: `const int thresholdBytes = 8 * 1024`. Any whole-file read over **8KB**
   decides to offload and calls `ScanResultHelper.StoreScanResultAsync`.
2. **[ScanResultHelper.cs:19](../../../../RoslynSentinel.Common/ScanResultHelper.cs#L19)** —
   `ScanResultHelper.ThresholdBytes = 30 * 1024`. `StoreScanResultAsync` only actually writes a
   scan file (and returns a non-null `scanId`) if the serialized payload exceeds **30KB**;
   otherwise it returns `(offloaded: false, filePath: default, scanId: null, jsonBytes)` —
   `scanId` is `null` by design for anything under 30KB.
3. Back in `ReadFile`
   ([SentinelWorkspaceTools.cs:1209](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1209)),
   the result is used unconditionally: `scanId: stored.scanId!` — a null-forgiving `!` on a value
   that state (2) can genuinely leave null.
4. `LargeResultInfo`'s constructor
   ([ToolResult.cs:219](../../../../RoslynSentinel.Common/ToolResult.cs#L219)) enforces non-null:
   `this.ScanId = scanId ?? throw new ArgumentNullException(nameof(scanId));` — this is the exact
   throw site matching the transcript's error text.

**Any file between 8KB and 30KB hits this every time, deterministically** — not flaky, not
environment-specific. `AdvancedStructuralEngine.cs` is ~24-25KB, squarely in the gap.

## Why this wasn't caught by existing tests/prior sweeps

[[project_offload_helper_partial_wiring]] (memory) already flagged that `LargeResultInfo`/offload
wiring is inconsistent across tools — this is a concrete instance of that inconsistency, not a new
category of problem. `ReadFile`'s own doc comment
([SentinelWorkspaceTools.cs:1146](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1146))
says "Whole-file reads past the size threshold are written to .roslynsentinel/scans and returned as
a scanId" — implying one threshold, when there are actually two disagreeing ones.

## Suggested fix direction (not implemented — for the follow-up session)

Two independent thresholds for the same offload decision is the actual defect; whichever value is
"correct" for UX purposes, `ReadFile` must not decide to offload using a threshold that
`ScanResultHelper` might then refuse. Options, roughly in order of how surgical they are:
- Make `ReadFile` use `ScanResultHelper.ThresholdBytes` (30KB) directly instead of its own local
  `8 * 1024` constant, so the two decisions can never disagree.
- Or, have `StoreScanResultAsync` always honor the caller's decision to offload (accept an
  optional "force" instead of re-checking its own threshold), since `ReadFile` already decided
  based on its own gate before calling in.
- Either way, add a regression test with a file sized in the 8-30KB gap specifically (the existing
  test suite apparently has no fixture in this range, or this would have been caught already).

## Reproduction

1. Load `RoslynSentinel-AgentTesting\RoslynSentinel.slnx` (or any solution).
2. Call `ReadFile(filepath: <any .cs file between ~8KB and ~30KB>)` with no `startLine`/`endLine`.
3. Observe `ArgumentNullException: Parameter 'scanId'`.

`RoslynSentinel.Advanced\AdvancedStructuralEngine.cs` in either `RoslynSentinel` or
`RoslynSentinel-AgentTesting` reproduces it directly, no setup needed beyond loading the solution.

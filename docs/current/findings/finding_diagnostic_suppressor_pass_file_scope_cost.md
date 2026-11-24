# Finding: DiagnosticSuppressor pass makes file-scope GetDiagnostics on test projects about 2x slower; borderline, no cache yet

**Status:** OPEN 2026-11-23. Measured borderline (about 2x, 1.5-2 s, confounded by load and project size); decision: do not build a per-project `CompilationWithAnalyzers` cache now, revisit only if agents report file-scope latency on test projects.

## Context
Commit 3daad2ab (2026-11-23) made `DiagnosticEngine.GetCompilerDiagnosticsAsync`
(`RoslynSentinel.Engines.Basic/DiagnosticEngine.cs:78`) route compiler diagnostics through the analyzer
driver whenever the project references a `DiagnosticSuppressor` (`:83`, `OfType<DiagnosticSuppressor>()`).
All 13 test projects qualify via NUnit.Analyzers, so suppressed warnings such as NUnit3001 no longer
appear. Projects with no suppressor take `RawCompilerDiagnostics` and are unchanged.

The driver is built fresh on every call: `compilation.WithAnalyzers(...)` at `:93`, then
`GetAllDiagnosticsAsync` over the whole project at `:99`, then a filter by file and `IsSuppressed` at
`:100-101`. Nothing is cached, so a file-scope call on a test project computes the whole project. The
punchlist #2 question: is file scope slow enough to justify a per-project `CompilationWithAnalyzers`
cache?

No call-transcript file was written by the live host, so the numbers come from a scripted client:
`c:\tmp\diag-timing\Measure-DiagTiming.ps1` (stdio MCP client; raw output `result.csv`, `result2.csv`
and server logs beside it, outside the repo). It launched a throwaway build of the server
(`.roslynsentinel\build\d87a019a-92e41d45\...\RoslynSentinel.Server.Advanced.exe`, built 2026-11-23
11:00 PM, includes 3daad2ab), loaded `RoslynSentinel.slnx` (about 14 s), and timed
`tools/call GetDiagnostics` (`summarize=true`) with a Stopwatch around each call.

Targets:
- File scope: `RoslynSentinel.Tests.Tools.Basic/UndoChangeTests.cs` (test project, suppressor) vs
  `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs` (non-test, no suppressor).
- Project scope: `RoslynSentinel.Tests.Tools.Basic` vs `RoslynSentinel.Tools.Basic`.
- One solution-scope call.

## What is broken
Nothing is broken; this is a cost measurement. Raw milliseconds, in call order:

| Call | Run 1 | Run 2 |
| --- | --- | --- |
| test-project file scope | 12218 (cold), 4552, 5650, 3287, 3274 | 12567 (cold), 5346, 5825, 3645, 3736 |
| non-test file scope | 1797, 1587, 1420, 1429, 1473 | 2480, 2040, 1596, 2166, 3433 |
| test-project project scope | 3153, 2450, 2749 | 5049, 4557, 4051 |
| non-test project scope | 1325, 1395, 1395 | 2221, 1976, 2509 |
| solution scope | 17808 | 23617 |

Observations:
- Warm file scope on the test project: about 3.3-5.8 s. On the non-test project: about 1.4-3.4 s. Roughly
  2x, about 1.5-2 s more.
- File scope on the test project costs about the same as project scope on it, and the non-test file scope
  costs about its own project scope. This matches the code reading: the file scope still computes the
  whole project (`:99`) and filters afterwards (`:100-101`).
- The first call is about 12 s (cold compilation plus workspace warm-up; not attributable to the
  suppressor pass from these data).
- Solution scope is 18-24 s.

Caveats (these limit what the numbers support):
- Both runs were on a loaded machine. Run 1 had two detached worktree test runs executing; run 2 had
  other sessions' dotnet processes present. Absolute numbers are inflated and noisy; run 2 is slower
  than run 1 across the board.
- The two projects differ in size, so test-vs-non-test is not a controlled comparison. The 2x gap mixes
  suppressor cost with project size. No run measured the same project with and without the suppressor.

## Root cause
Traced for the shape, not for the magnitude. Shape: the analyzer driver is constructed per call
(`DiagnosticEngine.cs:93`) and asked for all diagnostics of the project (`:99`), so file scope buys no
saving over project scope. Magnitude of the suppressor share: not isolated (see caveats).

## Why it matters
The ratio sits at the 2x / 1 s threshold the todo review set for justifying a cache, so the honest
verdict is "borderline, not clearly slow". The cost lands on the tool surface agents use after every
edit to a test file (3-6 s warm per call, 12 s cold); it is not a correctness issue. If it becomes
a real complaint the failure is an environment one (the tool recomputes work it just did), not an
agent behavior problem.

## Recommendation
Decided:
- Do not build the cache now.

Open, in order of leverage if latency becomes a problem:
1. Per-project `CompilationWithAnalyzers` cache keyed by workspace version (or compilation identity),
   so repeated file-scope calls on an unchanged test project reuse the driver. Cost: lifetime and
   invalidation handling in `DiagnosticEngine`, memory held per project, and a new staleness hazard
   after edits (the engine reads `ReadSource.Committed`, `:60`). Build only if file-scope latency on test
   projects (3-6 s warm) proves a real problem in agent sessions.
2. Skip the suppressor pass for file scope when the file has no suppressible diagnostics (for example,
   when the raw per-file diagnostics contain no warning a loaded suppressor declares in
   `SupportedSuppressions`). Unmeasured idea; it would keep the cost only where it can change the result.
3. A cleaner measurement first: an idle machine and equal-size projects, ideally the same project with
   the suppressor reference present vs removed. `Measure-DiagTiming.ps1` is reusable for this; copy it
   out of `c:\tmp` into `scripts/` if the measurement is repeated.

## Out of scope
- Cold-start cost (about 12 s first call) and solution-scope cost (18-24 s): not driven by the
  suppressor pass as far as these data show, and not part of punchlist #2.
- Behavior of the fallback path (`:105-113`, raw diagnostics on analyzer failure): not exercised in
  these runs.

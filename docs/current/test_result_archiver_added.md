# ModelTestingResultsArchiver: archive every run, plus test-result.txt

**Status:** IMPLEMENTED. Archiver 72326aa (2026-09-02); test-result.txt capture d0ab31c (2026-09-07).

## 1. Archive every run regardless of launch method (72326aa)
The `ModelTestingResults\113\...` archive the user pointed a fresh pattern-analysis agent at was
only ever populated by `roslynsentinel-modeleval.ps1`'s own post-run copy step
(`Copy-NewRunDirectories`, diffing timestamp-named leaf dirs under the scratch build's
`--artifacts-path` before/after each `dotnet test` call). A run launched directly via
`dotnet test`/VS Code's test explorer never went through the script, so its transcript stayed
stranded under `_scratchbuild_<suffix>\bin\...\model-eval\` and was invisible to the archive.

**What was built**: `RoslynSentinel.Tests.ModelEval/ModelTestingResultsArchiver.cs` —
`ArchiveRunDirectory(runDirectory)` mirrors the script's own logic: derives `<host-suffix>` from
`LlmOptions.BaseUrl` (112/113 known aliases, else sanitize the URL — same rule as the script's
`$knownHosts` fallback), walks up from `AppContext.BaseDirectory` to find `RoslynSentinel.slnx`
(same pattern as `TestSolutionFixture.FindRepoRoot`, confirmed to still resolve correctly under a
non-default `--artifacts-path` since `_scratchbuild_<suffix>` sits directly under the repo root),
finds the `model-eval\` marker in `runDirectory` and keeps everything after it (preserves
SizeThreshold's extra `n<size>` nesting level automatically, no shape-specific logic needed), and
recursively copies to `ModelTestingResults\<suffix>\<that-relative-path>\`. No-ops quietly (logs
via `TestContext.Out`, never throws) if the source directory doesn't exist or the destination
already does — an archive failure must never mask or replace the test's own pass/fail result.

Wired into `TearDown` (after existing cleanup, guarded by `_runDirectory is not null` since
`SetUp`'s `Assert.Ignore` can fire before it's assigned) in all 5 real-model fixtures:
`WholeFileRewriteAgentTests`, `PlanOnlyAgentTests`, `PlanThenExecuteAgentTests`,
`PlanImplementVerifyAgentTests` (archives the whole 3-phase `_runDirectory`, sweeping up
`plan/`, `implement/`, `verify/` subdirectories in one copy), `SizeThresholdAgentTests`.
`TranscriptReplayTests.cs` deliberately excluded — it replays existing transcripts, no
`_runDirectory`/`LlmOptions` pattern to hook.

Verified the algorithm (repo-root walk, suffix derivation, recursive copy, both flat and
3-phase-nested layouts) via a standalone throwaway console app against a hand-built fake
`_scratchbuild_113\bin\...\model-eval\...` tree — confirmed output paths exactly match the
script's existing `ModelTestingResults\113\...` layout. Build to 0 errors, committed `72326aa`.

**How to apply**: from here on, every model-eval run is archived automatically regardless of
launch method — the script's own copy step becomes redundant (harmless, since `ArchiveRunDirectory`
no-ops if the destination already exists) rather than load-bearing. If a future fixture is added
to `RoslynSentinel.Tests.ModelEval` with its own `_runDirectory`, wire the same 3-line call into
its `TearDown` for the archive to stay complete.

## 2. test-result.txt capture (d0ab31c)
Added `test-result.txt` capture to `ModelTestingResultsArchiver.ArchiveRunDirectory`
(`RoslynSentinel.Tests.ModelEval/ModelTestingResultsArchiver.cs`, commit d0ab31c, 2026-09-07).
Previously the archived run directory only ever had the agent's own transcript/log — never the
test framework's verdict — so a failing run's archive gave no way to see which assertion failed
or why. `TestContext.CurrentContext.Result` is already populated by the time `TearDown` runs
(NUnit fills in `Outcome`/`Message`/`StackTrace` before `TearDown`, even on failure); the new
`WriteTestResult` helper writes those three fields to `test-result.txt` in `runDirectory` before
the existing recursive copy runs — no call-site changes needed across all 7 fixtures since they
all already call `ArchiveRunDirectory(_runDirectory)` from their own `TearDown`.

**Verified live 2026-09-07**: relaunched `Model_AppliesSevenChainedRefactors` (`-Test
OrderPricingRefactorChain7`) against .112 (qwen/qwen3.5-9b) via
`roslynsentinel-modeleval.ps1`, using `run_in_background: true` per the concurrent-sessions
Start-Job lesson. Run failed for real, and
`ModelTestingResults\112\Model_AppliesSevenChainedRefactors\20260907-221833-481\test-result.txt`
came back with the exact assertion: `UnrelatedCodeEquivalenceAssert` caught the model corrupting
`DescribeOrder`'s string interpolation while reformatting a method the task said not to touch —
`$"Order {id}: {label}"` became `$"Order {{id}}: {{label}}"` (doubled braces turn a real
interpolation into literal `{id}` text in the output, a genuine functional regression). This is a
distinct, newly-confirmed model failure mode not previously documented, and directly the class of
bug this fix was built to make visible — before this, only the agent's own (self-reported-correct)
transcript was recoverable, with no way to see the real failing assertion.

**How to apply**: any future ModelEval investigation should check `test-result.txt` first, before
falling back to transcript-only reasoning or the interrogate-script replay technique — it's
authoritative on *whether and why* NUnit failed, where the transcript alone can only show what the
agent believed it did.

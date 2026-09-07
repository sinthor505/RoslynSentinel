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

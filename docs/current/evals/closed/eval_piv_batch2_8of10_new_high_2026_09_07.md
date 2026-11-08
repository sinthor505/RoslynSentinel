
Second overnight `.113` PlanImplementVerify batch (`qwen/qwen3.6-35b-a3b`, 2026-09-06/07, 10 runs,
`Model_FixesWholeFileRewriteBug_PlanImplementVerify`) scored 5 pass / 5 fail raw from NUnit. Full
per-run categorization (see [[project_member_replace_drops_leading_blank_line_and_verify_gap]] for
the run-by-run table and evidence) found **zero genuine model reasoning/coding mistakes** among
the 5 raw fails: 3 were a `Member` tool formatting bug (dropped blank lines / re-spaced method
signatures — logically correct fixes, false-failed on cosmetic grounds) and 2 were LM Studio
streaming aborts (infra, model never got to attempt/finish the task).

**Corrected scoring**: excluding the 2 infra-void runs (nothing to credit or fault — the model
never completed them) as neither pass nor fail, the model went **8 for 8** on every run it
actually got to finish (5 clean NUnit passes + 3 tool-bug-corrupted-but-logically-correct). Even
scoring the infra runs as failures (the more conservative reading), that's still **8/10 (80%)**.

**This is a new high for PlanImplementVerify**: baseline in
[[project_model_eval_baseline_corrected_2026_09_02]] was 47%; the first .113 PIV batch the same
night (2026-09-06) scored 7/10 (70%) raw. 8/10 (or 8/8 excl. void runs) beats both.

**How to apply**: this result should NOT be read at face value from the raw ModelTestingResults
pass/fail CSV/archive without the tool-bug correction applied — a naive "PlanImplementVerify: 50%"
readout from this batch would be actively misleading. If/when
[[project_modeleval_fixture_test_suite_redesign]] ships (behavior-based test-suite assertions
replacing today's brittle text-matching), re-run this same fixture and confirm the raw NUnit tally
converges on ~8-10/10 without needing manual per-run correction — that's the validation signal the
redesign worked.

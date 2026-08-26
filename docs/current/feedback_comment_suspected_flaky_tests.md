---
name: feedback-comment-suspected-flaky-tests
description: "When a test fails on a full/parallel run but passes in isolation and on rerun, add a code comment at the test noting the observed flakiness instead of only reporting it in chat"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 01276dd3-b7c1-44d8-8782-0558fcb37e86
  modified: 2026-08-26T04:07:00.987Z
---

When investigation shows a test failure is order-dependent/parallel-execution flakiness — not caused
by the change under review — add a short comment directly above the `[Test]` method recording that,
rather than letting the finding live only in a chat transcript or a one-off investigation.

**Why:** This occurs frequently in this repo (NUnit + parallel test execution). Each time it
recurs, without a marker at the test itself, whoever hits the failure next (human or agent) has to
redo the same stash/rerun investigation from scratch to rule out a real regression. A comment at the
site is cheap and permanent, unlike a chat message. This mirrors [[feedback_comment_pattern_deviations]]
— an expensive finding (here, "this failure is flaky, not a regression") needs to live at the code site,
not just in a doc or transcript.

**How to apply:** When a test fails under a full-suite or parallel run but passes in isolation and on
a clean rerun of the same code (confirmed via e.g. `git stash`/rerun or repeated suite runs), add a
1-2 line comment above the test method noting: it was observed to fail intermittently under
full-suite/parallel runs, the date, and that it passed in isolation/on rerun — so a future investigator
can skip re-deriving this and check known flakiness first. Do not weaken or delete the test, and don't
skip/ignore it — the comment is a note for triage speed, not a suppression mechanism. If a
`docs/known-failing-tests.*.txt` baseline exists in the repo, that's a separate mechanism for
*consistently* failing tests; flaky (intermittently failing) tests are noted inline instead since they
don't reproduce reliably enough for a baseline diff to catch them.

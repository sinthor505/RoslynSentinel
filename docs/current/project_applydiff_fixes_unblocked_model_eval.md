---
name: project_applydiff_fixes_unblocked_model_eval
description: ApplyDiff/DiffEngine fixes this session measurably unblocked small local models on the plan-9b-step2 whole-file-rewrite task
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-30T08:41:22.646Z
---

Before this session's `ApplyDiff`/`DiffEngine` fixes (searchMode literal override,
IsError signaling, and especially the [[project_diffengine_trailing_blank_anchor_fix]]
phantom trailing-blank-line bug), qwen3-coder-9b was struggling to complete
plan-9b-model-test-step2.md's whole-file-rewrite task at all — tool-level `ApplyDiff`
failures were sabotaging otherwise-reasonable model attempts.

After the fixes: qwen3-coder-9b, given only a symptom-only "minimal guidance" prompt
(no method/file names, no fix mechanism named — see
`Model_FixesWholeFileRewriteBug_MinimalGuidance` in
`RoslynSentinel.Tests.ModelEval/WholeFileRewriteAgentTests.cs`), independently located
the bug, discovered the sibling file's existing fix pattern unprompted, copied the
helper in, rewired the buggy method, and converged in 9 turns (~3 min) with only 2
self-corrected tool errors — a real qualitative jump from "couldn't complete the
scripted, heavily-hinted version" to "solves the unscripted version mostly unaided."

**Why:** User confirmed this delta directly: "these results are a positive sign - when
we first started these tests..., the 9b model was struggling to complete the step2
test. The tool improvements have helped significantly, particularly the numerous
issues with ApplyDiff that was sabotaging the model." This is validated-approach
confirmation, not a correction — the harness and fix work were on the right track.

**How to apply:** When evaluating whether a future `ApplyDiff`/`DiffEngine` change is
worth the effort, remember tool-level reliability directly gates what small local
models can demonstrate — a model can reason correctly and still fail purely because
the tool layer rejects a valid edit. Prefer fixing tool-layer correctness issues over
adding more prompt guidance/scaffolding to model-eval tests; the trimmed Basic-flavor
tool set (fewer, correct tools) is the same philosophy applied to tool *selection*
rather than tool *correctness* — see [[project_test_asyncify]] and
[[project_searchmode_literal_override_bug]] for the underlying tool-layer work.

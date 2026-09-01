---
name: project_applydiff_capable_agent_feedback
description: "Positive dogfooding feedback from a capable Claude session implementing the external-drift-hard-blocker doc — ApplyDiff's validateOnApply and hunk re-anchoring beat plain Read/Edit/Bash"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T07:32:59.959Z
---

While implementing `docs/current/ideas/external-drift-hard-blocker.md` in a fresh session, a
capable (non-model-eval-weak-model) Claude session gave unprompted positive feedback comparing
ApplyDiff against the plain Read/Edit/Bash toolset it would otherwise have used:

- `validateOnApply` caught two real mistakes before they hit disk: a forward reference to a
  not-yet-written method, and stale test call sites left behind after removing methods from
  `SentinelWorkspaceTools`. With plain Edit these would have been silent build breaks discovered
  later, disconnected from the edit that caused them — getting the diagnostic back inline,
  atomically, while still holding the reasoning context for that specific change, was called a
  genuine improvement over edit-then-separately-build.
- Automatic hunk re-anchoring landed most edits even when line numbers were slightly stale from an
  earlier edit shifting the file.
- One ApplyDiff call bundled apply + delta-compile + rollback-on-failure, versus three separate
  steps (and more room for drift between them) via Read/Edit/Bash.

**Why this matters:** this is the first positive signal recorded from a *capable* agent's
perspective, distinct from the weaker-9B-model dogfooding results tracked in
[[project_applydiff_fixes_unblocked_model_eval]]. It suggests ApplyDiff's value proposition
(atomic validate+apply+rollback, re-anchoring) holds across the capability spectrum, not just as
scaffolding for weak models.

**How to apply:** cite as evidence when deciding whether to keep investing in ApplyDiff's
validation/re-anchoring machinery versus simplifying it — this confirms it's pulling weight for
strong agents too, not just compensating for weak-model unreliability.

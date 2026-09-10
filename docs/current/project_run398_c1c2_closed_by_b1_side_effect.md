---
name: run398-c1c2-closed-by-b1-side-effect
description: "C1/C2 (duplicate plan trees, unenforced isolation) were closed as a side-effect of harness fix B1, not by their own remediation — plan drafted against a stale snapshot had to be rewritten"
metadata: 
  node_type: memory
  type: project
  originSessionId: f8088bc0-92a1-45f8-84dc-2c36d4806676
  modified: 2026-09-10T08:04:07.307Z
---

Finding docs A, B, and C (PlanStepRunner run 20260910-013550-398) are all resolved and moved
to `docs/current/blockers/resolved/` as of commit `c14a470` (2026-09-10). C1 (duplicate plan
trees with contradictory gates) and C2 (isolation asserted in prose, not enforced by layout)
were closed as a side-effect of B1 — commit `50ce5b6`, landed in a separate session — which
inlines each plan step's body by value into the model's prompt instead of resolving it through
`ProjectDoc`. Once nothing looks up step content by name anymore, a duplicate/misnumbered file
can't be substituted in, regardless of tree layout.

The tree consolidation (retiring the old 13-step split to `docs/obsolete/`, replaced by a stub
at `docs/current/plans/plan-eval-defect-remediation-v2-steps.md` pointing to the
`docs/testing/plan-eval-defect-remediation-v2-steps-runner/` tree as sole source of truth) was
completed as cleanup on top of that, not as an urgent fix.

**Why this is worth keeping:** my first full plan draft (before user pushback) assumed C1/C2
were still a live, urgent hazard and included a now-redundant step to fix a stale path in
`roslynsentinel-planstep.ps1` that another session had already fixed. The user caught this with
"many changes have been made in separate sessions. Review the plan against the current
codebase" — re-checking `git log` against the plan's assumptions found three separate fixes
(B1, the `.ps1` path, `readOnly`/`buildOptional` enforcement) that had already landed.

**How to apply:** in a repo with multiple concurrent sessions (see
[[project_concurrent_sessions]]), always re-verify a plan's premises against current `git log`
immediately before presenting it for approval — not just when first drafting it — especially
after any gap where other sessions could have landed work. A finding doc or plan is a snapshot;
treat it as such rather than as current truth.

Also: `ProjectDoc`'s basename-fallback warning fires on *any* non-path-qualified name match,
even when the match is unique — a bare `01-baseline.md` read still returns a `warning` field
even with only one file of that name inside the resolution scope. Don't expect `Warning: null`
from a bare-name read; only a path-qualified read skips it.

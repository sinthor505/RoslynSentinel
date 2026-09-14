# Finding: `Build(quickBuild)` intermittently reports a massive phantom error cascade on a clean worktree

**Status:** confirmed recurring defect, not yet fixed, root cause not traced. Found during
self-run `manual-selfrun-20260914-004605` — first seen step 08 (dismissed as a possible one-off),
2nd reproduction step 10 (escalated to a recurring defect worth flagging).

## Context

`Build(quickBuild)` against a fresh, independent worktree returned a ~26,700-error cascade of
`CS0234`/`CS0246` errors across `RoslynSentinel.Advanced` (e.g. "The type or namespace name
'CodeAnalysis' does not exist in the namespace 'Microsoft'"). `Build(fullBuild)` run immediately
after, against the same unchanged worktree, reported 0 errors — confirming the cascade was never
real.

Same failure shape occurred on two separate, independent worktrees (steps 08 and 10), which
upgrades it from "non-reproducible curiosity" to a recurring defect.

## Why this matters

Step 10's own plan text explicitly instructed relying on `quickBuild`'s error list as "the
authoritative list" of broken callers when widening a return type. Trusting that instruction
here would have meant chasing ~26,700 phantom errors instead of the 2 real caller fixes that
`fullBuild` correctly identified. This silently defeats the one scenario (fast-path error
triage) `quickBuild` exists for.

## Root cause (not traced this run — candidate hypothesis only)

Candidate: `quickBuild`'s scoped/incremental compile picks up `RoslynSentinel.Advanced` before
its `RoslynSentinel.Basic`/`.Common` dependencies are ready in the same pass, so it briefly sees
an incomplete/stale reference graph and reports cascading missing-namespace errors that a full,
correctly-ordered build never hits.

## Recommendation

- Investigate `quickBuild`'s project build ordering — confirm whether it always waits for
  dependency projects to finish before compiling a dependent project, or whether it can start a
  dependent's compile against a partially-built dependency.
- Until root-caused, treat a `quickBuild` error list with sudden high volume (thousands of
  errors, especially `CS0234`/`CS0246` on the tool's own base namespaces) as a signal to
  cross-check with `fullBuild` before acting on it, rather than trusting it as authoritative.
- Consider updating any plan/prompt text that tells a model to treat `quickBuild`'s error list
  as authoritative, since this run showed that advice would have been actively unsafe.

## Reference

- Session log: `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md` —
  step 08 (first occurrence), step 10 (~line 1177, 2nd reproduction).

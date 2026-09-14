# Eval Defect Remediation v2 — Step Split (pointer)

This plan's step-by-step split covers three defect groups: `Build`'s `quickBuild` fabricating
a success verdict on zero compiled projects, `ReplaceNodeFormattedAsync` silently losing XML
doc comments and injecting foreign line endings, and the orientation breaker/`DiffHunkAnalyzer`
firing correctly but never reaching the MCP wire.

**The executable steps live in
[`docs/testing/plan-eval-defect-remediation-v2-steps-runner/`](../../testing/plan-eval-defect-remediation-v2-steps-runner/00-index.md).**
That directory is the single source of truth — read its own `00-index.md` first. It lives
under `docs/testing/` rather than here so `RoslynSentinel.Tools.PlanStepRunner`'s `--testing`
flag can scope `ProjectDoc` to it without colliding with a same-named production doc; see that
index for why, and for why the placement is now defense-in-depth rather than load-bearing (the
runner inlines step content by value into the model's prompt and no longer looks it up via
`ProjectDoc` at all).

The original single-file design rationale is
[`plan-eval-defect-remediation-v2.md`](plan-eval-defect-remediation-v2.md) in this same
directory — not an executable plan, but the source the step split was derived from.

A superseded 13-step split once lived at this path. It diverged from the runner copy in both
directions (missing later content updates; carrying build-checkpoint text the runner copy's
merges made partly obsolete) before being retired. It is archived at
`docs/obsolete/plan-eval-defect-remediation-v2-steps/` for historical reference only — do not
execute it; its numbering and some gate instructions no longer match the live plan.

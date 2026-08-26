---
name: feedback-comment-pattern-deviations
description: "When a consistency audit confirms a spot that deliberately deviates from an established codebase pattern, add a code comment there, not just an audit-doc note"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 01276dd3-b7c1-44d8-8782-0558fcb37e86
  modified: 2026-08-26T03:53:30.427Z
---

When fixing findings from a codebase consistency/duplication audit, any catch block, branch, or
call site confirmed as an **intentional** deviation from the dominant pattern (not a bug to fix)
must get a short code comment explaining why it's different, at the deviation site itself.

**Why:** Audit docs (e.g. `docs/current/codebase-consistency-audit-v1.md`) are expensive to produce
— they require multi-pass research to distinguish "this looks inconsistent but is actually correct"
from "this is a real bug." That reasoning is wasted if it only lives in a dated audit doc: the next
audit pass (or a future editor) has no signal at the code site itself and re-derives or re-flags the
same "inconsistency" from scratch. A comment at the site is cheap and permanent; the audit doc is a
point-in-time snapshot that isn't re-read before every edit.

**How to apply:** When a fix pass (mine or a future one) confirms a catch block/branch is
*correctly* hand-rolled instead of using the shared helper — e.g. `SentinelWorkspaceTools.LoadSolution`
not calling `ToolErrorMapper` (would produce a circular "call LoadSolution first" message) or
`SentinelRefactoringTools.SyncTypeAndFilename`'s old-file-delete catch (partial-success message with
remediation advice the generic mapper would drop) — add a 1-3 line comment at that exact catch/branch
stating *why* it diverges, not just that it does. Do this inline as part of applying audit fixes, not
as a separate pass. See [[project_codebase_consistency_audit_v1]] for the audit that surfaced this.

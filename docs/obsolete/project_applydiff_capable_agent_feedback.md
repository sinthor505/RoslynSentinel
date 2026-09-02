---
name: project_applydiff_capable_agent_feedback
description: "Positive dogfooding feedback from two capable Claude sessions (external-drift-hard-blocker doc, then the CS0122 lookup fix) — ApplyDiff's validateOnApply, hunk re-anchoring, and structured symbol/build tools consistently beat plain Read/Edit/Bash"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T08:55:01.905Z
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

**Second confirmation (2026-09-01), a different capable Claude session implementing the CS0122
lookup-helper fix** ([[project_cs0122_lookup_helper_proposal]]) independently raised the same
theme plus two new specifics:
- `validateOnApply` again caught a mistake pre-disk — this time a malformed string interpolation in
  a diff — returning a validation error with no file written, instead of a syntax error only
  discoverable later via a separate build step.
- Hunk re-anchoring again absorbed drifted line numbers from an earlier edit shifting the file.
- New: `GetFileOutline`/`LocateSymbol`/`SearchSolutionText` gave structured, line-ranged answers
  (symbol kind, containing type, accessibility, signature) in one call each — finding
  `SymbolLocation.Accessibility` and the right `LocateSymbolAsync` overload took two tool calls,
  not a manual read-and-parse of a 2000-line file.
- New: `Build` returned a clean structured error/warning list instead of the agent having to scrape
  raw `dotnet build` text output.

**Why this matters:** two independent capable-agent sessions, on unrelated tasks, both spontaneously
praised the same core mechanism (validate-before-write, hunk re-anchoring) and the second added
concrete evidence that the structured-data tools (`GetFileOutline`, `LocateSymbol`,
`SearchSolutionText`, `Build`) also save real turns versus raw file/shell tools — not just for the
weaker-9B-model dogfooding results tracked in [[project_applydiff_fixes_unblocked_model_eval]], but
across the capability spectrum.

**The user's framing of this (2026-09-01), the sharper point:** the interesting part isn't just
"the tools save turns" — it's that even a frontier-class model *makes real mistakes* (a forward
reference to a not-yet-written method, stale test call sites, a malformed string interpolation),
and RS's validate-before-write step caught each one before it hit disk, when it would otherwise
have surfaced later as a build failure disconnected from the edit that caused it and been harder to
diagnose. This is evidence for the tools' value independent of model capability — not merely
compensating for weak-model unreliability, but catching the kind of mistake any model makes given
enough edits, regardless of tier.

**How to apply:** cite as evidence when deciding whether to keep investing in ApplyDiff's
validation/re-anchoring machinery and the structured symbol/build tools versus simplifying them —
two independent sessions now confirm this is pulling weight for strong agents too, and the value
is specifically in catching real mistakes pre-disk (not just convenience), which frontier models
are not immune to making.

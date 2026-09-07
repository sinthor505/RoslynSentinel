---
name: project-doccommentid-description-gap
description: "Audit every docCommentId-taking tool: description must say to use LocateSymbol, and flag tools that pair docCommentId with other mandatory params like filePath. Tracked in docs/current/TODO.md"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-07T09:34:42.241Z
---

`RenameSymbol`'s tool description doesn't say how to obtain its `docCommentId` parameter (via
`LocateSymbol` or equivalent). Confirmed as the direct cause of a new failure signature in
[[project_sequential_edit_habit_vs_compiler_checks_theory]]'s 2026-09-06 batch: 2 of 3 runs called
`RenameSymbol` with a fabricated placeholder (`"docCommentId_for_CalcDisc"`), got rejected, then
self-corrected via `LocateSymbol` — recovery was fast (1-2 turns) but the failure is fully
avoidable at the description level.

**Why:** the model has no way to guess the real ID format without documentation nudging it toward
`LocateSymbol` first; it improvises a plausible-looking string instead, same failure shape as
`WriteFile`'s pre-3a4c521 raw-JSON rejection gap (undocumented behavior → model guesses → clean but
avoidable rejection).

**How to apply:** the user plans to fix this in a separate session — audit every tool parameter
named `docCommentId` (not just `RenameSymbol`) across the codebase and add "obtain this via
`LocateSymbol`" (or equivalent) to each description. Check this memory before that session starts
to confirm it's still unaddressed (`grep docCommentId` across tool description strings).

**2026-09-07 addendum — scope expanded, tracked as a TODO:** logged as a formal entry in
`docs/current/TODO.md` ("`docCommentId` parameter audit across all tools"). Added a second part to
the audit: while going tool-by-tool, also identify every tool that takes `docCommentId` *and* has
other mandatory params (e.g. `filePath`) — flag those separately, since requiring both a resolved
symbol ID and a hand-supplied file path is a second spot a model can supply mismatched/fabricated
values, and the description should clarify which one is authoritative. Not started yet.

---
name: dispatch-implementer
description: Brief template, lanes, tier limits and failure thresholds for dispatching the Haiku-tier implementer and implementer-sweep subagents. Load before the first implementer dispatch in a session.
---

# Dispatching implementer: the slice contract

Both agents are Haiku 5.5. They finish exactly-specified slices and stall on anything with a wide or
unmeasured blast radius. Cutting the work down is the **dispatcher's** job. A `PreToolUse` hook
(`enforce-dogfood.ps1`) refuses a dispatch that breaks the formal parts below.

**Limits live in `.claude/implementer-tiers.json`** (one tier ladder per lane; `tier` is the highest
tier currently allowed). Read it before briefing and copy the chosen tier's numbers into the brief's
`Limits:` line. Do not remember numbers from here or from an earlier session.

## Pick a lane

- `implementer` (depth, tiers `D0`..): several different edits in a few `.cs` files (the tier's file limit).
- `implementer-sweep` (breadth, tiers `S0`..): the same mechanical transformation across many files,
  done by a named call-site-updating tool (`RenameSymbol`, `ChangeSignature`, `MethodSignature`) or one
  repeated `ReplaceSnippet` batch pattern. If you cannot name that operation, it is depth work: split it.
- Straight to `implementer-senior`: new algorithms, public API shape changes, cross-project type
  relocation, or work where you cannot name the files up front.

Default to the lowest tier that fits. Going below the lane's current tier is always allowed; going
above is refused by the hook.

## Brief template

Every field on its own line, label first:

```
Tier: D0 | S0 ...          (letter = lane, number = tier)
Limits: files<=N, edits<=N, acceptance<=N, toolcalls<=N        (depth; values copied from the tier file)
        operations<=N, files<=N, callsites<=N, projects<=N, toolcalls<=N   (sweep)
Files: full paths including the project (distinct .cs files must not exceed the tier's files limit;
  for a sweep list EVERY file touched, call-site files included - the hook counts only this list)
Symbols: exact types/members to change
Call sites: pre-measured list (file:line), or "none"
Operation: (sweep only) the exact tool call or repeated edit pattern
Acceptance: a clean Build, or one named test/diagnostic (a second check only where the tier allows it)
Out of scope: what not to touch (e.g. "do not commit", "items 5-7") - always include "edit nothing
  outside the named symbols/branches; if a test seems to need another change, reply RESCOPE:"
  and "no Write/Edit/shell writes on .cs files"
```

Dispatch with `model: "haiku"` pinned explicitly.

**Measure first, then slice.** Get the call-site list from `InspectSymbol(aspect: blastRadius)` (or
`Search(mode: references)`) and paste it into the brief. `blastRadius` takes one symbol per call (resolve it with
`Search(mode: symbol)`, then pass `filePath` + a verbatim `contextSnippet`) and returns `totalCallSites`,
`affectedProjectsCount` and the reference list; count distinct files yourself. Treat a non-empty
`error` field as a failed measurement even if `isError` is false - on a stale server binary it
otherwise reads as "zero blast radius".

## Slicing rules

- Interdependent parts land in one edit: interface plus implementations, or signature plus callers, via
  one `ReplaceSnippet` with `batchEdits` (definitions first) or a call-site-updating tool. Name the tool
  call in the brief.
- **Migration-first.** Big or breaking changes are staged so every step compiles: add the new
  overload, delegate or property beside the old member, mark the old one `[Obsolete("Use X")]`, move
  callers in slices (a sweep slice per project or folder), remove the old member in a final slice once
  nothing references it. Prefer this over any "everything at once" change; it is what makes each slice
  fit a tier. See `docs/current/proposals/proposal_parallel_change_deprecated_wrappers.md`.
- Over the lane's limits: split again, or use the other lane, or go to `implementer-senior`.

The agent re-measures before editing and replies `RESCOPE:` (with its numbers) if the real blast radius
exceeds the brief's; the dispatcher then re-slices.

## Replies and what to do with them

| Reply | Meaning | Dispatcher action |
| --- | --- | --- |
| clean report | slice done, build green | review, append ledger line |
| `RESCOPE:` | real numbers exceed the brief | re-measure, re-slice; ledger counts it as a failure only if the numbers were within the tier |
| `NEED-ADVICE:` | blocked | send to `implementer-advisor`, resume the same agent with the answer |
| `TOOL-GAP:` | no tool does this as one semantic step | record it (see below), then dispatch whatever staged slice is possible |
| `ESCALATE:` | design decision or tool defect | `implementer-senior` (tool defect: blocker workflow) |

**`TOOL-GAP:` handling.** Do not let the agent do it by hand and do not do it by hand yourself. Record
the gap as a one-line `docs/current/TODO.md` item, or a proposal doc if the fix needs design, quoting
the agent's measured numbers and suggested tool change. If a staged (migration-first) route exists,
dispatch that now. If none exists the task is blocked: follow the CLAUDE.md blocker workflow.

## Tier evaluation: ledger, promotion, demotion

Ledger: local-only `.claude/journal/implementer-ledger.md`. When reviewing each report, append
`- YYYY-MM-DD <lane> T<n> <clean|fail:reason> nearest-limit=<which> <task>`.

A **failure** at the dispatched tier: an `ESCALATE:`; both `NEED-ADVICE` rounds used; a `RESCOPE:`
where the real numbers were within the tier's limits; a build not clean at hand-back; edits outside the
named symbols; a half-applied change; a `.cs` write that bypassed the MCP tools. **Not a failure:** a
`RESCOPE:` caused by the dispatcher's measurement error, an `ESCALATE:` for a genuine tool defect
(blocker workflow), one `NEED-ADVICE` the advisor resolved. A `TOOL-GAP:` is neutral: not a failure, and
it does not count toward the clean streak.

The hook enforces the `Tier:` ceiling and the `Files:` limit only. The other `Limits:` values
(including `acceptance`) are checked by the agent's preflight and by your review of its report.

- **Promote** one tier (edit `tier` in the tier file) after 5 consecutive clean dispatches at the
  tier. Clean = no failure, and the slice used at least 70% of at least one limit.
- **Demote** one tier after 2 failures in the last 5 dispatches at the tier.
- **Demote immediately** after an out-of-scope edit, a half-applied change or an MCP bypass.
- Tier 0 is the floor and is always available.
- Every failure gets a `failure-analysis` note naming the environment change (tool, description,
  brief wording, hook) that would have prevented it, before blaming the model.

# Plan: Split implementer into depth/sweep lanes with evaluated tiers, and add tool-gap reporting and migration-first guidance

**Status:** IN PROGRESS 2026-10-14. Steps 1-5 built and reviewed; commit pending. `.claude/agents/` is gitignored (local-only), so agent-file changes are not in the commit.

## Problem
Haiku 5.5 is a more capable model than the Haiku 4.5 the slice contract was tuned for, but the
contract (3 files, about 10 edits, 1 acceptance check, 25-tool-call tripwire) is hard-coded in four
places: `.claude/agents/implementer.md` (preflight and tripwires), the `dispatch-implementer` skill,
`.claude/hooks/enforce-dogfood.ps1:347-348` (file limit) and CLAUDE.md. Raising it means raising
every dimension together, so a failure cannot be attributed to a single limit. The vendor
benchmark figures for Haiku 5.5 are unverified and say nothing about C# refactoring through these
tools, so limits must rise on our own eval evidence, with a fallback to the existing contract.

Separately, nothing tells an agent what to do when a task is blocked or oversized because no tool
can do it in one semantic operation. The default failure mode is brute force (many manual edits,
shell loops), which is exactly the gap CLAUDE.md's mission says must be fixed in the tool surface.
Related: [proposal_parallel_change_deprecated_wrappers.md](../proposals/proposal_parallel_change_deprecated_wrappers.md)
(expand / migrate / contract), which this plan adopts as standing guidance but does not build tooling for.

## Decision
1. Two Haiku lanes, each with its own tier ladder:
   - `implementer` (depth): files stay at 3; edits, acceptance checks and tool-call tripwire rise.
   - `implementer-sweep` (breadth): one or two repeated mechanical operations across many files,
     driven by a named call-site-updating tool.
2. Limits live in one shared file, `.claude/implementer-tiers.json`, read by the hook. The brief
   carries `Tier:` and `Limits:` lines so agents read their limits from the brief, not from their
   own prompt; changing a tier never edits an agent file.
3. Promotion and demotion follow fixed thresholds recorded in a local ledger (below).
4. Tool-gap reporting (`TOOL-GAP:`) and migration-first workflow become standing rules in CLAUDE.md,
   mirrored in the agent prompts.

### Tier table (all lanes start at tier 0)

| Tier | Depth (`implementer`): files / edits / acceptance / tool calls | Sweep (`implementer-sweep`): operations / files / call sites / projects / tool calls |
| --- | --- | --- |
| 0 | 3 / ~10 / 1 / 25 (current contract) | 1 / 8 / 25 / 2 / 30 |
| 1 | 3 / ~15 / 1 / 35 | 2 / 15 / 50 / 2 / 45 |
| 2 | 3 / ~20 / 2 (Build + one named test) / 45 | 2 / 25 / 100 / 3 / 60 |

Always senior, both lanes: new algorithms, public API shape changes, cross-project type relocation,
work where the files cannot be named up front. A sweep must name the call-site-updating tool call
(`RenameSymbol`, `ChangeSignature`, `MethodSignature`) or one repeated `ReplaceSnippet` batch
pattern in an `Operation:` field; if it cannot, it is depth work and gets split.

### Failure thresholds
Counts as a failure at the dispatched tier: an `ESCALATE:` reply; both `NEED-ADVICE` rounds used; a
`RESCOPE:` where the real numbers were within the tier's limits; a build that is not clean at
hand-back; edits outside the named symbols; a half-applied change; a `.cs` write that bypassed the
MCP tools.

Does not count: a `RESCOPE:` caused by the dispatcher's measurement error; a tool defect (goes to
the blocker workflow); one `NEED-ADVICE` the advisor resolved.

- Promote one tier after 5 consecutive clean dispatches at the tier. Clean = no failure and the
  slice used at least 70% of at least one limit (trivial slices do not count).
- Demote one tier after 2 failures in the last 5 dispatches at the tier.
- Demote immediately after an out-of-scope edit, a half-applied change or an MCP bypass.
- Tier 0 is the floor and is always available; the dispatcher may pick a lower tier on doubt.
- Every failure gets a `failure-analysis` note naming the environment change that would have
  prevented it (failure doctrine: the tooling and brief are suspect before the model).

### Ledger
Local-only `.claude/journal/implementer-ledger.md` (same folder as the tool-experience journal).
One line per dispatch: `- YYYY-MM-DD <lane> T<n> <clean|fail:reason> nearest-limit=<which> <task>`.
The main session appends it when reviewing the implementer's report, and applies promotion or
demotion by editing `tier` in the tier file.

### Tool gaps and large blast radius
When a task is blocked, or its blast radius is large, because no tool can do it as one semantic
operation (or no such tool exists), the agent stops and replies `TOOL-GAP:` instead of doing it by
hand. Fields: task, tool tried and its limit (or "no tool"), measured numbers (call sites, files,
projects), suggested tool change (extend X with parameter Y, or new tool Z with inputs and outputs).
The dispatcher records it: a one-line `docs/current/TODO.md` item or a proposal doc, and continues
with whatever staged slice is possible. If no staged route exists the task is blocked and the
existing blocker workflow applies.

### Migration-first workflow
Large or breaking changes are staged so every step compiles: add the new overload, delegate or
property beside the old member; mark the old member `[Obsolete("Use X")]` (a warning only - no
`TreatWarningsAsErrors` exists in the repo); move callers in slices; remove the old member in a
final slice once reference count is zero. Preferred over any "everything at once" change.

## Execution rules
- C# is not touched by this plan. Markdown and JSON edits are made in the main session; the
  `.ps1` hook and its tests are dispatched to a subagent (`implementer-senior`; `test-writer` for
  hook tests if separable). No parallel dispatches.
- Hook tests (`.claude/hooks/enforce-dogfood.Tests.ps1`) and `.claude/hooks/agent-mcp-tools.Tests.ps1`
  must pass before commit. Existing `implementer` hook cases must keep passing unchanged for a tier-0
  depth brief that carries the new fields.
- Commit only this session's files, with the `Git` tool, scope `listed`.
- ASCII-only punctuation in everything written.

## Steps

### Step 1 - Tier file
- Files: `.claude/implementer-tiers.json`
- Change: one object per lane (`depth`, `sweep`) with `agent` (subagent_type), `tier` (current, 0) and a
  `tiers` array holding the limits from the table (`maxFiles`, `maxEdits`, `maxAcceptance`,
  `maxToolCalls` for depth; `maxOperations`, `maxFiles`, `maxCallSites`, `maxProjects`,
  `maxToolCalls` for sweep).
- Done when: valid JSON; limits match the table exactly.

### Step 2 - Hook
- Files: `.claude/hooks/enforce-dogfood.ps1`, `.claude/hooks/enforce-dogfood.Tests.ps1`
- Change: generalize the dispatch check at `enforce-dogfood.ps1:315` to both `implementer` and
  `implementer-sweep`, loading limits from the tier file. Required labels gain `Tier:` and `Limits:`
  (sweep also `Operation:`). Deny when: the tier is above the lane's current `tier`; `Files:` lists
  more distinct `.cs` files than the tier's `maxFiles` (replaces the hard-coded 3); the tier file is
  missing or unreadable (fail closed with an actionable message). Keep the model pin, the
  `RESCOPE` requirement and the Write/Edit/shell warning. Update the denial text to name the lane's
  current limits.
- Done when: all existing `implementer` tests pass with briefs updated to carry `Tier: D0` and a
  `Limits:` line; new tests cover sweep acceptance, over-tier denial, over-file-limit denial per
  tier, missing `Operation:` on sweep, and missing tier file.

### Step 3 - Agent files
- Files: `.claude/agents/implementer.md`, `.claude/agents/implementer-sweep.md` (new),
  `.claude/agents/implementer-senior.md`, `.claude/agents/implementer-advisor.md`
- Change: `implementer.md` preflight and tripwires read the limits from the brief's `Limits:` line
  (fallback tier 0 values if absent); description notes Haiku 5.5. `implementer-sweep.md`: same
  protocol (NEED-ADVICE, ESCALATE, RESCOPE, dog-fooding rules) with sweep tripwires: files outside the
  brief's list, tool calls above `Limits:`, a result larger than the brief's own call-site list
  offloaded to `GetLargeResult`; it must perform the named `Operation:` and never hand-edit call
  sites. All four files gain the `TOOL-GAP:` reply format and the migration-first rule. Senior and
  advisor mention the sweep lane as a possible escalation source.
- Done when: `agent-mcp-tools.Tests.ps1` passes; each agent prompt documents `TOOL-GAP:`.

### Step 4 - Dispatch skill
- Files: `.claude/skills/dispatch-implementer/SKILL.md`
- Change: lane selection guide; brief template with `Tier:`, `Limits:`, `Operation:`; pointer to the
  tier file as the single source of limits (remove the duplicated numbers); failure thresholds and
  ledger procedure; `TOOL-GAP:` handling; migration-first staging.
- Done when: no numeric slice limits remain in the skill outside a reference to the tier file.

### Step 5 - CLAUDE.md
- Files: `CLAUDE.md`
- Change: update "Dispatching `implementer`" for lanes, tier file and Haiku 5.5; add a section "Tool
  gaps and large changes: report, do not brute-force" (rule plus one-line why) and the
  migration-first rule, cross-referencing the parallel-change proposal.
- Done when: CLAUDE.md states each rule once and names no numeric limits.

### Step 6 - Review and commit
- Change: `explore-sonnet` reviews the diff for cross-file consistency (tier file vs hook vs skill vs
  agents). Fix findings, then commit this session's files.
- Done when: hook test suites green; review findings resolved or recorded.

## Out of scope
- Building tooling for expand / migrate / contract (stays in the proposal).
- Changing `test-writer` or the explore agents' tiers.
- Auto-promotion or auto-demotion logic; the main session edits `tier` by hand.
- Rewording historical docs and large-result dumps that mention Haiku.

## Risks and open decisions
- The hook cannot tell the main session from a subagent, so a subagent could edit the tier file
  (self-promotion). Hypothesis: low risk because agent prompts forbid it; revisit if the ledger
  shows it.
- The hook does not validate `Call sites:` counts against the tier's `maxCallSites`; the dispatcher
  measures and the agent re-measures in preflight. Unverified whether that is enough for sweeps.
- Review follow-ups (2026-10-14): tier-entry validation is DONE (the dispatching lane's `tier` and every
  required limit must be a whole number in range, else the dispatch is denied; the dispatch block also
  denies on any unexpected exception; a live-file smoke test checks both lanes). Still open:
  `maxAcceptance` and the other non-file `Limits:` values are unenforced by the hook;
  `journal-improvement-planner.md` still says "3 files or fewer" for plan slicing; a depth brief with an
  `Operation:` line after `Files:` would have its `.cs` paths counted as Files entries; the denial text
  for an out-of-int-range limit does not mention the upper bound.
- Sweep starting numbers (S0) are a proposal, not measured. Treat the first 5 dispatches as the
  calibration.
- Tokenizer reportedly counts about 30% more tokens; `scripts/Measure-OrientationCost.ps1` figures
  need a recheck after the model switch (unverified).
- Haiku 5.5 vendor figures are unverified and deliberately absent from CLAUDE.md and the agents.

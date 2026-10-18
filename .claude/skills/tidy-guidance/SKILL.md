---
name: tidy-guidance
description: Audit and clean up the agent-guidance files - CLAUDE.md, the memory directory (MEMORY.md, MEMORY_CLOSED.md, memory files) and docs/current/TODO.md (plus CLOSED.md). Runs the mechanical checker, dispatches read-only audits, proposes a plan, stops for approval, then applies it. Use when the user asks to clean up, trim, optimize or de-stale CLAUDE.md, MEMORY.md or TODO.md, or periodically when they have grown.
---

# Tidy guidance

Goal: keep the three always-loaded guidance surfaces small, true and correctly placed.
Follow CLAUDE.md: PowerShell for shell, ASCII-only punctuation, `Git` tool for commits (scope
`listed`), no implementation edits to `.cs` in the main session. These files are Markdown/JSON/ps1,
so editing them in the main session is allowed.

Placement rules (from CLAUDE.md "Where guidance belongs"):

| Surface | Holds | Does not hold |
| --- | --- | --- |
| CLAUDE.md | Standing rules an agent must know on turn one, where a wrong first move is costly and repeatable | Procedures (-> skill), reference tables (-> `docs/current/references/`), history |
| Memory | Reactive facts: feedback not yet promoted, in-progress project notes, pointers | Anything already in CLAUDE.md or `docs/current/`, resolved incidents (-> `MEMORY_CLOSED.md`) |
| TODO.md | Open items only, 1-3 lines each, linked to a proposal/plan/idea/design doc for detail | Done items (-> CLOSED.md), design prose |

## 1. Measure (mechanical)

```powershell
.\scripts\Test-GuidanceHygiene.ps1
```

Read-only report: sizes, dead CLAUDE.md paths, memory index/orphan/wikilink/frontmatter problems,
memory files duplicated under `docs/current/`, dead TODO links, oversized TODO entries. It cannot
judge staleness - that is step 2. Keep the output; the end report compares before/after.

## 2. Audit (dispatch, read-only, two agents in parallel is fine)

Main session must not read these files broadly. Dispatch `explore-sonnet` twice (breadth: very
thorough, "do NOT edit"):

- **A - CLAUDE.md + memory.** Per CLAUDE.md section: redundancy, content better placed in a
  skill/reference doc (name the target), stale or wrong pointers (verify every referenced
  file/script/tool/flag exists), contradictions with memory; est. bytes saved. Per memory file:
  stale/resolved (check git log, CLOSED.md, source), standing rules to promote, duplicates in
  CLAUDE.md or `docs/current/`, near-duplicate clusters, weak `MEMORY.md` lines. Feed it the checker
  output so it does not redo mechanical work.
- **B - TODO.md + CLOSED.md.** Per entry (heading + line range): DONE / DUPLICATE / SUPERSEDED /
  SHRINK / KEEP / UNSURE with one-line evidence (commit hash or file:line). Verify "done" against
  `Git` log, CLOSED.md, `blockers/resolved/`, source. Dead links, items superseded by a plan or
  proposal, estimated post-cleanup size, whether CLOSED.md needs a monthly archive split.

Exclude docs/current/upstream/ (reference copies from the upstream repo, e.g. UNFINISHED*.md) and docs/obsolete/ from every audit: they are not active documentation, so do not treat them as overlapping backlogs or dedupe targets.

Both report in tables, <= ~150 lines, no file dumps. Treat UNSURE as "ask the user", never guess.

## 3. Plan and approve (hard stop)

Present one combined plan: per surface, what is removed, moved, merged, promoted, and the
before/after size. Ask the user (AskUserQuestion) for the genuine choices only: how aggressive the
memory cleanup is (close+fix only vs merge clusters), TODO restructure vs prune, CLOSED.md archive
split. Do not edit before approval (memory: get approval before editing).

## 4. Apply (order matters)

1. **Memory first** (it feeds CLAUDE.md):
   - Move resolved entries: add a `> SUPERSEDED/CLOSED` banner *below* the frontmatter (never above
     it - frontmatter must be line 1), move the index line from `MEMORY.md` to `MEMORY_CLOSED.md`.
   - Merge near-duplicate clusters into the surviving file; delete the merged files with `Remove-Item`
     (memory dir is outside the repo, so the `DeleteFile` rule does not apply); repoint wikilinks.
   - Where a memory file is duplicated under `docs/current/`, keep the docs copy (newer, tracked),
     delete the memory copy, and point the index line at the docs path or drop it.
   - Fix broken `[[wikilinks]]` (repoint or remove), missing frontmatter (`name`, `description`,
     `metadata.type`), descriptions over 300 chars, and index the orphans (or close them).
   - `MEMORY.md` lines: one line each, < ~150 chars, hook says *when it matters*.
2. **CLAUDE.md**: promote the standing rules found (distil to rule + one-line why, mark the source
   memory superseded, move its index line to `MEMORY_CLOSED.md`); move procedures/reference to a skill
   or `docs/current/references/` and leave a 1-2 line pointer; fix stale pointers; trim redundancy.
   Never drop a rule silently - every cut either moves or is demonstrably duplicated elsewhere. Also
   fix stale CLAUDE.md heading names quoted by hooks/agents (grep `.claude/` for the old heading).
3. **TODO.md / CLOSED.md** (per `docs/current/templates/README.md`): move DONE entries to CLOSED.md
   with a resolution line; replace SHRINK entries with `- [STATE] Title (YYYY-MM-DD) - one sentence -
   [doc](relative/link.md)`, moving the prose into the matching proposal/idea/design doc first; group
   under P1 Now / P2 Next / P3 Later; leave UNSURE entries untouched and list them for the user. If
   CLOSED.md is split, archive by month under `docs/current/archive/` and keep the current month live.

## 5. Verify and report

- Re-run `.\scripts\Test-GuidanceHygiene.ps1`; remaining findings must be explained (intentional) or fixed.
- Confirm no CLAUDE.md rule was lost: diff the headings/rules list before/after.
- Report sizes before -> after for CLAUDE.md, MEMORY.md, TODO.md, CLOSED.md; list UNSURE items and
  anything deliberately skipped.
- Commit only repo files changed (CLAUDE.md, docs, scripts, hooks/agents touched) with `Git` scope
  `listed`. Memory files live outside the repo and are not committed.

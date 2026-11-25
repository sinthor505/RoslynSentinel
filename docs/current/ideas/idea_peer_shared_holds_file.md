# Idea: Peer coordination through a shared holds file

**Status:** NOT DESIGNED 2026-11-09. Raised with the stale-read guard follow-ups; `HOLD`/`RELEASE` between peer sessions exist only in chat text.

## What happened

- The peer protocol (CLAUDE.md "Peer sessions", `peer-coordination` skill) sends `HOLD <files>` / `RELEASE <files>` as `SendMessage` text.
- Recorded as an open TODO.md item next to [plan_stale_read_guard_followups.md](../plans/closed/plan_stale_read_guard_followups.md).

## The actual gap

- A hold lives only in conversation text, so it survives compaction only if the session copies it into its own tracker.
- A third session cannot see a hold between two others.

## Possible fixes (not decided)

1. **Git-ignored `.claude/holds.json`**, each entry = session name, files, reason, since, expected release. `HOLD`/`RELEASE` write it; `OWNS`/`WHOAREYOU` and the dispatch briefs read it; stale entries (session no longer in `ListAgents`) are ignored.
   - **Trade-off:** concurrent writes from several sessions to one file need locking.
2. **One file per session under `.claude/holds/`.** Same entries; avoids the concurrent-write problem, readers merge the files.
3. **Hook warning (optional add-on):** `enforce-dogfood.ps1` warns on an edit to a file another session holds. Open whether it should.

Either variant needs the `peer-coordination` skill and CLAUDE.md "Peer sessions" updated.

## Related

- [plan_stale_read_guard_followups.md](../plans/closed/plan_stale_read_guard_followups.md)
- `.claude/skills/peer-coordination/`, CLAUDE.md "Peer sessions".
- TODO.md P3 "Peer coordination: shared holds file".

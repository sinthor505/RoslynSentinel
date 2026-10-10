---
name: wrapup
description: Wind the session down because the usage limit is near. Finish or safely park in-flight work, build, commit only this session's files, and write a handoff log to .claude/handoff/ so a fresh session can resume without re-reading this chat. Use when the user says "wrapup" or "wrap up", or says they are nearing the session limit.
---

# Wrapup

Goal: leave the repo green and committed (or in a precisely described state), and leave a handoff
log so the next session starts from the log instead of this transcript. Be fast and frugal: this
runs when budget is nearly gone. No new investigation, no new features, no subagents.

Follow CLAUDE.md throughout (dog-fooding MCP tools for C# and git, PowerShell for shell, ASCII-only
punctuation, `Git` tool with scope `listed`, `CommitHashLength` off the result).

## 1. Settle in-flight work (stop adding scope)

- Finish an edit that is one or two calls from compiling. Anything larger: do not start it - record
  it as NEXT in the log instead.
- If a half-applied multi-step change cannot be completed, prefer `UndoLastApply` or reverting only
  *your* session's files to a compiling state over leaving the tree broken. Never revert files you
  did not touch this session.
- A tool failure that stopped work still gets its `docs/current/blockers/blocking_error_<slug>.md`
  (CLAUDE.md blocker rule) - write it now if it is not written yet, then list it in the log.

## 2. Verify

- `Build` (backgrounded is fine). Force a full rebuild if the counts look suspicious.
- Run only the tests relevant to this session's changes via `RunTest`, not the full suite, unless the
  user asks. Compare failures with `reference_known_failing_tests`; report only *new* ones.
- Record the exact outcome (errors/warnings count, tests run, pass/fail). Do not claim green
  without having run it.

## 3. Commit (only if the gate passes)

- Gate: build has 0 errors and no *new* test failures. If the gate fails, do **not** commit; the
  log says why and lists the uncommitted files instead.
- `Git(status)` first. Stage with scope `listed`, naming only files changed in *this session*;
  leave other dirty or untracked files alone (other sessions may share the worktree). Include
  docs/blocker files you wrote. Commit message follows the repo's style and ends with the
  attribution trailer.
- If the work is partial but compiles and passes, commit it with `WIP:` in the subject rather than
  leaving it uncommitted. Never commit a non-compiling tree.

## 4. Write the handoff log

Path: `.claude/handoff/<yyyy-MM-dd>_<sid8>.md` (`<sid8>` = the 8-char id from this session's
journal filename; create the folder if missing). Local-only, like the journal. Use the Write tool
(non-C# file). Also overwrite `.claude/handoff/LATEST.md` with a single line: the filename of this
log, so a new session can find it without listing the folder.

Everything the next session needs must be *in the file* - it must not require this chat. Use
absolute dates, `file:line` or symbol names for code, quoted text for errors. Template:

```markdown
# Handoff <yyyy-MM-dd HH:mm> (session <sid8>)

**Branch:** <branch> @ <short hash> (<N> commits this session: <hash subject>, ...)
**Build/tests:** <Build result; tests run and outcome; known-baseline failures ignored>
**Working tree:** <clean | list of uncommitted files, each with why>

## Goal
<1-3 sentences: what the user asked for this session and why>

## Done
- <outcome> (<commit hash> / <files or symbols>)

## In flight / partial
- <what is half-done, exact state, and what is already applied vs not>

## NEXT (do these first, in order)
1. <concrete step with the exact tool + target symbol/file, e.g. "Member(replace) on X.Y in Foo.cs: ...">
2. ...

## Decisions and constraints
- <choice made, the user's approval or instruction that governs it, and why - so it is not re-litigated>

## Gotchas / things learned
- <non-obvious facts that cost time: tool quirks, stale-server state, false-positive errors>

## Open blockers / issues
- <doc path or one-line description; "none">

## Resume checklist
- [ ] `LoadSolution`; if source of the server itself was edited, `McpServerControl(stop)` then reload
- [ ] `Git(status)` matches "Working tree" above
- [ ] Journal for this session: <path>
```

Rules for the log:
- Short beats complete: one line per item, no narrative of the session, no restating the diff
  (the commit has it). Record only what the commits and code do *not* already say.
- NEXT must be actionable by a model that has seen nothing: name the symbols and the tool.
- Do not paste large tool output; cite where to regenerate it.

## 5. Housekeeping (cheap ones only)

- Append any unjournaled notable tool experience to the session journal (one line each).
- If a memory file is now stale because of this session's work, correct it; do not create new memory
  just to duplicate the handoff log.
- Open items that outlive the session and are one-liners go in `docs/current/TODO.md`; larger ones in
  the doc genre per `docs/current/templates/README.md`. Skip if they are already in the log's NEXT
  and the user did not ask for docs.

## 6. Final message

Reply with 3-6 lines: commit hash(es) (or "not committed: <reason>"), build/test status, the
handoff log path, and the single first NEXT item. Tell the user to start the next session with:
"Resume from .claude/handoff/LATEST.md".

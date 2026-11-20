# Reference: when a bug in a script, harness, fixture or test may be fixed without asking

**Status:** CURRENT 2026-11-19. Owner-approved standing rule; CLAUDE.md "Agent conduct" points here.

## Purpose

Bugs in support code (scripts under `scripts/`, the ModelEval and PlanStepRunner harnesses, test
fixtures, test suites) are fixed promptly instead of piling up, without letting a "fix" hide a product
defect or corrupt an eval run. Consult this before editing support code you were not asked to change.

## How it works

1. **Record.** Append the issue to `docs/current/INBOX.md` at once (CLAUDE.md, Deferred issues).
2. **Investigate.** If the cause is not already evident, dispatch `explore-tier2` (read-only) with the
   symptom and evidence. If the bug does not block current work, a code `// TODO` plus the INBOX line is
   enough; stop there.
3. **Auto-fix** only when every criterion below holds. Otherwise go to step 4.
   - The root cause is confirmed with evidence (`file:line`, quoted error, run output), not a hypothesis.
   - The defect is in support code, not in product behaviour (server, engines, tools).
   - The fix stays within the small-edit cap (about 3 edits in 2 files).
   - It does not weaken, delete, skip (`[Ignore]`, `[Explicit]`, filter) or re-baseline an assertion,
     expected value, known-failing list or golden file.
   - No eval run or evalqueue job is queued or running against the files, and no peer `HOLD` covers them.
   - It needs no design decision (no new parameter, behaviour choice or output-shape change).
4. **Ask.** Present the cause, the evidence and one recommended fix for approval. Always ask when it is
   unclear whether the test or the product is wrong.
5. **Report an auto-fix** in one line: cause, fix, `file:line`, build/test result. Commit it on its own
   (session files only, per CLAUDE.md "Commits") so it can be reverted alone, and close its INBOX line.

## Gotchas

- A red test is often a real product bug. Making the test pass by editing the test is never an
  auto-fix; changing what a test expects needs approval.
- Editing a harness, fixture or `scripts/roslynsentinel-modeleval.ps1` while a queued eval will build
  from the working tree changes what that run measures.

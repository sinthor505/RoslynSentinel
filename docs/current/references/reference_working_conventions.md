# Reference: working conventions (build, docs, text, scripts)

**Status:** CURRENT 2026-11-03. Moved out of CLAUDE.md "Working conventions"; the short rules stay there.

## Purpose
The lower-frequency conventions. Consult when touching tests, docs, scripts or text encoding.

## How it works
- **Tests:** NUnit 5.0, not 4.6. `await` `Assert.ThrowsAsync`/`CatchAsync`/`DoesNotThrowAsync` (an
  un-awaited assert asserts nothing); `TestDelegate`/`AsyncTestDelegate` are gone. Full conventions:
  [reference_nunit5_test_conventions.md](reference_nunit5_test_conventions.md).
- **Not-found errors:** rule out scope, name-shape, container and kind mistakes before claiming absence,
  state what was searched and name the next call:
  [reference_not_found_recovery_ladder.md](reference_not_found_recovery_ladder.md).
- **Doc lifecycle:** `docs/current/TODO.md` is open-items-only; resolved entries go to `CLOSED.md`. A finished
  plan, proposal or finding gets its Status line updated and moves to its folder's `closed/`.
  `docs/current/upstream/` and `docs/obsolete/` are not active documentation.
- **Worktrees:** exclude `*/Worktree/` folders (PlanStepRunner harness clones) from diffs, searches and
  reviews; their git output is not authoritative.
- **Text:** ASCII-only punctuation in comments, docs, commit messages and prompts (`-`/`--`, straight
  quotes, `<=`/`>=`); non-ASCII becomes mojibake across the file -> server -> MCP -> harness chain.
- **Scripts:** before hand-rolling a shell loop for a repeated task, check `scripts/` for a front-door
  `.ps1` (`roslynsentinel-modeleval.ps1`, `roslynsentinel-planstep.ps1`, `build.ps1`, ...).
- **Solution:** `RoslynSentinel.slnx` (there is no root `.sln`).

## Gotchas
- Builds and tests share `bin/`/`obj/` and block on file locks, so run one at a time. For several
  projects use one solution-scope run or sequential project runs.

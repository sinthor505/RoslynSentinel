# Finding: `Git(diff)`'s rendered output has an encoding/mojibake defect on non-ASCII content — the real file is fine

**Status:** confirmed tool defect, not yet fixed, non-blocking. Found during self-run
`manual-selfrun-20260914-004605`, step 06.

## Context

While reviewing `Git(diff)` output for `AgentSystemPrompts.cs` (a file containing a real Unicode
em-dash, U+2014, inserted deliberately via a `[char]0x2014` PowerShell technique to avoid
corruption), the diff text showed the em-dash rendered as `ΓÇö` (mojibake) instead of `—`.

A direct `ReadFile` on the same lines of the same file confirmed the actual on-disk content has
the correct `—` in both places — the real file bytes are correct and uncorrupted. The same
`Git(diff)` output also showed a pre-existing (not introduced this step) comment-divider line in
`CodeEditingTests.cs`, made of a repeated non-ASCII character, rendered as `ΓòÉ` repeated ~50
times — same mojibake pattern, confirming this is a **`Git(diff)`-rendering-only defect**, not
file corruption.

## Root cause

Not traced to source this run, but the pattern (`—` → `ΓÇö`, a classic UTF-8-bytes-decoded-as-
Windows-1252 mis-decode) strongly implies the `Git` tool is reading the underlying `git diff`
subprocess's UTF-8 stdout through the wrong codepage (e.g. system default/Windows-1252) before
returning it as JSON.

## Why this matters

Non-blocking for this run — ground truth was always recoverable via `ReadFile` — but a real
defect: any file with real non-ASCII content will show a corrupted-looking diff to a model
relying on `Git(diff)` alone, which could cause the model to wrongly conclude its own edit
corrupted the file when it didn't.

## Recommendation

Force UTF-8 decoding of the git subprocess's stdout in the `Git` tool's `diff` implementation
(and likely `show`/`log` message text, if/when those are fixed per the sibling array-param/
invalid-operation finding).

## Reference

- Session log: `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md`,
  Step 06 (~line 646).
- Related: `docs/current/finding_git_tool_array_param_and_invalid_operation_crash.md`.

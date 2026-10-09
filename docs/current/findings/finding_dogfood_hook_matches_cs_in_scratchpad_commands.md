# Finding: The dog-fooding hook blocks scratchpad PowerShell that merely mentions ".cs", pushing agents to reword commands

**Status:** PARTIALLY ADDRESSED 2026-10-09. The here-string / heredoc case (prose written into a `.md` via `@'...'@` or `<<'EOF'`) is fixed by `Remove-HereStrings` in the hook (plan_agent_tooling_hooks_and_small_ergonomics.md, Step 1; commit 347e0da). The residual limitation remains for a quoted single-line literal that mentions `.cs` (for example `Set-Content notes.md 'Foo.cs'`, now also denied by the shell-write rule; use the bypass keyword) and for recommendations 1-3 below, which are not implemented.

## Context
During the 2026-10-02 type-inventory and anonymous-shape audits (session cc715aa0), Sonnet agents wrote
analysis to markdown/CSV files in the scratchpad using PowerShell. Two agents reported being blocked by
`.claude/hooks/enforce-dogfood.ps1` and got past it by rewording or splitting the command (the type
inventory agent hit it three times). I did not capture the exact blocked commands, so the trigger
below is inferred from the hook source, not reproduced.

## What is broken
The hook denies a PowerShell/Bash command when the whole command line matches `\.cs\b` and any reader verb
(`grep|...|Select-String|sls|cat|type|Get-Content|gc|head|tail|...`), regardless of where the `.cs` text is
(`enforce-dogfood.ps1:246-247`). A command that only writes a scratchpad `.md`/`.csv` and has `.cs` in a
string, a path fragment being parsed, or a regex is blocked. The header comment already records this:
"Same whole-command regex limitation as git detection above" (`:22`, `:30-31`).

## Root cause
Whole-command regex with no notion of which file the reader verb operates on (hypothesis for the exact
blocked commands; the regex is verified in source).

## Why it matters
CLAUDE.md says "do not reword the command to slip past it". A false positive leaves an agent only two
choices: stop, or reword. Two agents reworded. Each false block also costs turns and teaches agents that
the hook is arbitrary. This is an environment defect, not agent misbehaviour.

## Recommendation
1. Require the `.cs` token to be an argument of the reader verb (same pipeline segment, as a path or glob),
   not anywhere in the command. Decided: no. Open.
2. Exempt commands whose only file targets are under the session scratchpad.
3. Give the block message an escape hint for scratchpad output ("write analysis to the scratchpad with the Write tool"), so rewording is not the only way out.

## Out of scope
- Whether a subagent should be allowed to run PowerShell at all (the hook is a backstop, not the policy).

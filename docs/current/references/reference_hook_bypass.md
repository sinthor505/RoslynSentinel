# Deliberate hook bypass

**Status:** CURRENT 2026-10-17. Detail moved out of CLAUDE.md "Hook bypass"; the rule stays there.
The authoritative behaviour is the header of `.claude/hooks/enforce-dogfood.ps1`.

A deliberate bypass is legitimate for a `.cs` file outside this repo, or a manual repo edit/read/git
call you have decided is right (e.g. raw bytes, or a halted `Git` tool you already reported). Never for
a failed or missing MCP tool - that is a blocker.

## Routes

- **Outside this repo:** Edit/Write/Grep/Read on a `.cs` path outside the repo root is exempt automatically.
- **Bash/PowerShell:** put `DeliberateHookBypass: <reason>` in the command (as a comment) or the
  `description`. A bare keyword is still blocked.
- **Edit/Write/Grep/Read on repo files:** write `.claude/bypass.local.json` (valid 10 minutes):
  `{"reason": "...", "tools": ["Edit"], "paths": ["Foo.cs"]}` (`tools` may be `["Read"]`) - `reason`
  required, `tools`/`paths` optional narrowing; use the narrowest `paths`.

Accepted bypasses are logged to `.claude/journal/hook-bypass.jsonl` and are journal-worthy. The
`ReplaceSnippet` parameter check and the commit checks cannot be bypassed.

## Known false positives

The hook matches literal text: a PowerShell/Bash command that merely mentions `.cs` in a search pattern
or `git log` in an echoed string can be blocked. Reword the command (use `Search`/`Git` where they fit);
use the bypass only when the intent is genuinely a manual C#/git operation.

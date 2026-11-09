# Deliberate hook bypass

**Status:** CURRENT 2026-11-09. The rule is in CLAUDE.md "Tool failures and hooks"; the block
messages point here. The authoritative behaviour is the header of `.claude/hooks/enforce-dogfood.ps1`.

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

The shell `.cs` rules judge what a command reads or writes (parsed targets), so a `.cs` path used only
as data in a docs edit passes. They fall back to literal text when a target cannot be resolved (an
unknown variable, a statement the parser cannot read), and the git rule always matches literal text, so
a benign command can still be blocked. Never reword the shell command. Instead:

- Reading or editing a non-C# file: use Read/Edit/Write/Grep on it (the hook ignores those for non-`.cs`).
- A C# or git operation an MCP tool covers: use `Search`/`ReadFile`/`Git`.
- A genuinely manual C#/git operation (see above): use a bypass route.
- Otherwise: report the false positive and stop.

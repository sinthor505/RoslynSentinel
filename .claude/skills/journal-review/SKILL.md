---
name: journal-review
description: Summarize the tool-experience journal. No argument - append a short summary of THIS session's journal and call log to the session's journal. Argument "all" - roll up every session journal since the last rollup into .claude/journal/ROLLUP.md. Use when the user asks to review, summarize or roll up the tool journal, or at the end of a long session.
---

# Journal review

Journals are local-only files in `.claude/journal/` (plan:
`docs/current/plans/plan_mcp_tool_experience_journal.md`). Per session:

- `<date>_<sid8>.md` - model-written lines: `- HH:mm [+|-|~] ToolName: note`
- `<date>_<sid8>.calls.jsonl` - hook-written, one JSON object per call:
  `ts, agent (main | type:id), kind (mcp | fallback), tool, operation?, ok, errorCode?, durationMs, responseChars?, detail?`

Read only files under `.claude/journal/`. Do not open source files or transcripts for this.
Use ASCII-only punctuation in everything written.

## Default: this session

1. The journal path was printed at session start (and again after compaction). If it is no
   longer in context, use the most recently written `*.md` in `.claude/journal/` other than
   `ROLLUP.md`, and say which file you picked.
2. Read the journal. Get call-log stats with PowerShell (replace the base path):

   ```powershell
   $base = '.claude/journal/2026-10-01_aaaabbbb'
   $c = Get-Content "$base.calls.jsonl" | ConvertFrom-Json
   $c | Group-Object kind, tool | ForEach-Object { [pscustomobject]@{ kind_tool = $_.Name; calls = $_.Count; failed = @($_.Group | Where-Object { -not $_.ok }).Count; medianMs = ($_.Group.durationMs | Sort-Object)[[int]($_.Count / 2)] } } | Sort-Object calls -Descending | Format-Table -AutoSize
   $c | Where-Object { -not $_.ok } | Group-Object tool, errorCode | Select-Object Count, Name
   $c | Where-Object kind -eq 'fallback' | Select-Object ts, agent, detail
   ```
3. Append to the journal (do not rewrite earlier lines):

   ```
   ## Session summary (HH:mm)
   - Calls: <N> MCP (<top 3 tools with counts>), <F> failed (<tool:errorCode ...>), <B> C# fallbacks
   - Best: <the most useful tool experience, from the journal lines>
   - Worst: <the most painful one>
   - Fallbacks: <why each happened, or "none">
   - Suggested environment fix: <one concrete change, framed per CLAUDE.md's failure doctrine - or "none">
   ```

   Keep it to 3-6 lines. If the journal has no entries, say so in the summary. Do not make
   up impressions the journal lacks; the call stats are enough on their own.
4. Show the summary to the user.

## Argument "all": cross-session rollup

1. Read `.claude/journal/ROLLUP.md` if it exists. Its last `## Rollup <date>` heading lists
   which sessions it covered (`Sessions: <sid8>, ...`).
2. Gather every `*.md` journal not yet covered, plus its call log. Prefer each journal's own
   `## Session summary` lines; if a session has none, use its raw lines and call stats.
3. Append to `ROLLUP.md` (create it with `# Tool-experience rollup` if missing):

   ```
   ## Rollup <yyyy-MM-dd>
   Sessions: <sid8>, <sid8>, ...  (<count>, <first date> to <last date>)
   - Most used: <tool counts across sessions>
   - Recurring positives: <themes with 2+ mentions, tool names>
   - Recurring pain points: <themes with 2+ mentions, tool names, error codes>
   - C# fallbacks: <count and the recurring reasons>
   - Candidate environment fixes: <1-3 concrete changes, most-mentioned first>
   ```
4. Show the rollup to the user. Recurring pain points are candidates for `docs/current/TODO.md`
   or a finding doc, but only on the user's say-so. This skill never writes outside
   `.claude/journal/`.

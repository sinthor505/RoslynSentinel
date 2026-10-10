---
name: journal-improve
description: Turn new tool-experience journal entries into planned MCP server improvements. Runs scripts/Get-JournalDigest.ps1, dispatches journal-improvement-planner on the digest, shows the plan(s) and stops for approval. Optional argument "all" digests every session, ignoring the watermark. Use when the user asks to review journals and improve the tools, or periodically after a batch of sessions.
---

# Journal improve

Pipeline: digest script (no LLM) -> `journal-improvement-planner` -> **user approval** ->
`orchestrator`. Keep your own context small: you only run the script, relay the planner's
report, and hand off. Do not read journals, source or plans yourself.

Use ASCII-only punctuation in anything you write.

## 1. Digest

Run with the PowerShell tool (this is Windows; not Bash). With argument `all`, add `-All`:

```powershell
.\scripts\Get-JournalDigest.ps1
```

- Output `No new journal activity since the last digest.`: tell the user and stop.
- Otherwise it prints the digest path and one summary line (sessions, bad/mixed and good entry
  counts, failed MCP calls, fallbacks). If there are 0 bad/mixed entries and 0 failed calls, say
  so and stop; there is nothing to plan.

The script advances the watermark when it writes the digest, so the digest file is the record. If
planning fails, re-plan from the same digest path rather than re-running the script.

## 2. Plan

Dispatch `journal-improvement-planner` with `model: "sonnet"` and a self-contained brief:

> Digest: `<absolute digest path>`. Triage the clusters and write DRAFT plan doc(s) per your
> instructions. Report plan paths, the dispositions table, and decisions needed from the user.

Run it in the foreground; nothing else is waiting on you. It plans only, so there is no
conflict with the shared MCP server process.

## 3. Show and stop

Relay the planner's report: plan path(s), one-line goal and step count per plan, the
dispositions table, and the decisions it flagged. Add nothing else. Then ask the user which
plans to approve (all, some, or none), and whether to adjust scope or priority first.

**Never start implementation without that answer.** Plans stay DRAFT until the user approves.

## 4. Hand off (only after approval)

For each approved plan: set its Status line to `APPROVED <yyyy-MM-dd>`, then dispatch
`orchestrator` (`model: "sonnet"`) with the plan path and: "Drive this plan step by step.
Plans in this run are sequential, never parallel, because subagents share one MCP server
process." Dispatch approved plans one after another, not concurrently.

When the orchestrator finishes, relay its summary. The orchestrator owns verification and
commits; do not repeat them.

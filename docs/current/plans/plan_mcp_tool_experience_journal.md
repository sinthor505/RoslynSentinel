# Plan: Hook-assisted per-session journal of MCP tool experience

**Status:** IN PROGRESS 2026-10-01. Steps 0-7 built and wired; uncommitted. Step 8 (live shakedown) pending.

## Implementation notes (2026-10-01)

- Files:
  - `.claude/hooks/journal-common.ps1` (shared helpers)
  - `journal-session-start.ps1`, `journal-log-call.ps1`, `journal-stop-nudge.ps1`
  - `journal-hooks.Tests.ps1`: 33/33 pass
  - `.claude/skills/journal-review/SKILL.md` (local-only)
  - CLAUDE.md sections "Tool-experience journal" and "Compact instructions"
- **Latency resolved by `async: true`.** Run synchronously, the logger takes a median 475 ms
  per call (73 real Step 0 payloads replayed), almost all of it `powershell.exe` startup. The
  Claude Code 2.1.286 schema supports `"async": true` ("runs in background without blocking"),
  so the PostToolUse/PostToolUseFailure entries use it. The cost: a call made just before a
  stop may land in the log after the Stop hook has read it. That call is then counted at the
  next stop. This is accepted.
- **Sessions already running** when the hooks were installed never get a SessionStart. The
  Stop hook creates their journal the first time it nudges.
- **Nudge de-duplication.** A `.nudge` marker file records the time of the last nudge, and
  triggers count only activity after max(journal write, last nudge). A nudge the model
  ignores therefore does not repeat on every turn.
- **Subagent calls are logged but never trigger a nudge**, since the main agent did not see
  them first-hand.
- **Calls denied by `enforce-dogfood.ps1` are not logged**, because a denied call never
  reaches PostToolUse.

## Problem

We want continuous, low-effort feedback from Claude sessions on what it is like to use the
RoslynSentinel tools: short notes like "MoveMember moved 10 members and fixed 80 call sites in
one call" or "FindReferences beat Grep here". These are not findings or blockers. A summary
written at the end of a session is unreliable for three reasons:

- Mid-session compaction removes the detail before the end arrives.
- The model cannot tell how close it is to compaction, so it cannot write the summary early.
- Sessions have no clear end. `SessionEnd` hooks run without the model, so nothing there can
  reflect on the session.

A rule that lives only in CLAUDE.md also decays over hundreds of calls. That is the same
reason `enforce-dogfood.ps1` exists.

## Decision

Two streams per session, with hooks so neither depends on the model remembering:

1. **Objective call log (no model effort).** A `PostToolUse` hook appends one JSONL line per
   RoslynSentinel MCP call. It also logs Read/Grep/Glob/Bash calls that target C#, which are
   fallbacks. Compaction cannot affect this file.
2. **Subjective journal (model-written, one line per note).** CLAUDE.md asks for a note right
   after anything notable. A `Stop` hook acts as a backstop: when enough unjournaled activity
   has built up, it blocks once and asks for 1-3 lines. A `SessionStart` hook gives the model
   the journal path at startup and again after every compaction.

A `journal-review` skill turns both files into a short summary whenever the user asks for one.

Files per session, under `.claude/journal/`:

- `<yyyy-MM-dd>_<sid8>.md` - the journal. Entry format: `- HH:mm [+|-|~] ToolName: note`.
- `<yyyy-MM-dd>_<sid8>.calls.jsonl` - the call log.

`<sid8>` is the first 8 characters of the hook payload's `session_id`. Using one file per
session means concurrent sessions never write to the same file.

## Execution rules

- Every hook **fails open**: any internal error means exit 0 with no block, following the
  `check-build-staleness.ps1` pattern. A journaling failure must never interrupt real work.
- Hooks run under `powershell.exe -NoProfile -ExecutionPolicy Bypass -File`, like the
  existing ones.
- ASCII-only text in hook output, journal templates and CLAUDE.md additions.
- The call log never records tool *content* (code, diffs, file bodies). It records only the
  tool name, operation, outcome, error code and response size.

## Steps

### Step 0 - Confirm the hook payload contracts
- Files: temporary `.claude/hooks/dump-payload.ps1` (delete afterwards).
- Change: wire it temporarily on SessionStart, PostToolUse (one MCP tool, one failing MCP call)
  and Stop. Capture real payloads to `c:\tmp`. Confirm:
  - `session_id` and `source` (startup/resume/compact/clear) on SessionStart;
  - `tool_response` shape for an MCP success and an MCP error, and which field signals the
    error (`IsError` / `errorCode` / envelope);
  - whether a failing tool fires `PostToolUse` or a separate failure event;
  - whether subagent calls carry an agent identifier;
  - `stop_hook_active` on Stop.
- Done when: the field names used in Steps 1-3 are taken from captured payloads, not from
  memory. Any mismatch with this plan is fixed in this doc before Step 1.
- Results (2026-10-01, Claude Code 2.1.286, payloads captured in `c:\tmp\hook-dump\`):
  - Hook changes to `settings.json` take effect mid-session; no restart needed.
  - Project hooks fire for **every** concurrent session in the repo. Per-session files keyed
    on `session_id` are therefore required, not just tidy.
  - `cwd` is not stable: one session reported `...\.roslynsentinel\largeresults`. Hooks must
    find the journal directory from `$PSScriptRoot`, never from `cwd`.
  - Every payload carries `session_id`, `transcript_path`, `hook_event_name` and `prompt_id`.
  - **An MCP success fires `PostToolUse`.** `tool_response` is `{type:"text", text:"<json>"}`,
    and the inner JSON has `isSuccess: true`.
  - **An MCP failure fires `PostToolUseFailure`, not `PostToolUse`.** The payload has `error`
    (the inner JSON string, with `isSuccess: false` and `errorData.errorCode`) and
    `is_interrupt`. The logger must therefore be wired on both events.
  - Both events carry `duration_ms`. MCP calls also carry `mcp_server.name`, a more reliable
    MCP test than the tool name prefix.
  - Read carries `tool_input.file_path`. Bash carries `tool_input.command`.
  - Subagent tool calls share the parent's `session_id` and add `agent_id` and `agent_type`.
    Main-agent calls have neither field.
  - `SessionStart` carries `source` (`compact` was observed) and `model`. Compaction also
    fires `SubagentStop` with an empty `agent_type`.
  - `Stop` and `SubagentStop` both carry `stop_hook_active` and `last_assistant_message`.
  - Over about 2 hours with 8 sessions, the capture hook recorded 85 payloads. One working
    session produced 47 PostToolUse calls, which gives an idea of how often the nudge
    threshold will be reached.
  - Step 0 closed 2026-10-01. The temporary hook is removed and `settings.json` matches HEAD
    again. The captured payloads are kept in `c:\tmp\hook-dump\` as Step 4 test fixtures.

### Step 1 - SessionStart hook: create the journal and inject its path
- Files: `.claude/hooks/journal-session-start.ps1`.
- Change: create `.claude/journal/` and the session's journal file with a one-line header if
  it is missing. Write a short context block to stdout with three things: the journal path,
  the entry format, and the rule "append right after a notable tool experience; never wait
  for the end". Fires on every `source`, so the path comes back after compaction.
- Done when: a fresh session and a `/compact` both show the path in context, and the file
  exists.

### Step 2 - PostToolUse hook: objective call log
- Files: `.claude/hooks/journal-log-call.ps1`.
- Change: append one JSONL line with these fields:
  - `ts`, `agent` (`main`, or `agent_type`/`agent_id` for a subagent);
  - `tool` (short name with the `mcp__..__` prefix removed) and `operation` (from
    `tool_input.operation`, if present);
  - `ok`, `errorCode`, `durationMs`, `responseChars`. `ok` is false on a
    `PostToolUseFailure` event; the error code is parsed from the `error` string;
  - `kind`: `mcp`, or `fallback` for Read/Grep/Glob/Bash aimed at `.cs` or at shell git. The
    `.cs` detection reuses the same checks as `enforce-dogfood.ps1`.

  Non-C# fallbacks are not logged. Writes use append-with-retry so parallel subagents don't
  collide.
- Done when: a short session gives one line per MCP call, an MCP error gives `ok:false` with
  its error code, and a Read on a `.md` gives no line.

### Step 3 - Stop hook: backstop nudge
- Files: `.claude/hooks/journal-stop-nudge.ps1`.
- Change:
  - If `stop_hook_active` is true, exit 0. This stops it from looping.
  - Otherwise, count call-log lines newer than the journal file's last write time.
  - Block once (JSON `decision: block`) if any of these hold: `mcp` lines >= N (default 15),
    `ok:false` lines >= 1, or `fallback` lines >= 1.
  - The block reason gives the counts, the journal path, the entry format, and an escape:
    "or append `- HH:mm ~ nothing notable`". Any append updates the file's write time, which
    resets the counter.
  - N comes from `ROSLYNSENTINEL_JOURNAL_NUDGE_THRESHOLD`.
- Done when: the nudge fires once after the threshold, never twice in a row, and not at all
  in a session with no MCP calls.

### Step 4 - Hook tests
- Files: `.claude/hooks/journal-hooks.Tests.ps1`.
- Change: plain-script tests in the same style as `enforce-dogfood.Tests.ps1`, piping canned
  payloads (taken from Step 0) into each hook inside a temp directory. Cases:
  - the threshold boundary;
  - `stop_hook_active`;
  - the error trigger and the fallback trigger;
  - malformed stdin (must exit 0 with no block);
  - a missing journal directory.
- Done when: `pwsh -NoProfile -File .claude/hooks/journal-hooks.Tests.ps1` passes.

### Step 5 - Wire the hooks in settings.json
- Files: `.claude/settings.json`.
- Change: add `SessionStart` (all sources) and `Stop` entries. Add matching `PostToolUse`
  **and** `PostToolUseFailure` entries with matcher
  `mcp__.*roslyn_sentinel.*|Read|Grep|Glob|Bash|PowerShell`, kept separate from the existing
  `.*__Build` staleness entry.
- Done when: Steps 1-3 behave the same in a live session as in tests.

### Step 6 - CLAUDE.md section and compaction instructions
- Files: `CLAUDE.md`.
- Change: add a short "Tool-experience journal" section covering:
  - what to note, with examples: one call doing what used to take many; a confusing
    description; an error message that did or didn't get you unstuck; any time you reached
    for a shell tool;
  - when: right away, one line each;
  - how this differs from a blocker or finding.

  Also add a "Compact instructions" section asking the summarizer to keep the journal path and
  any tool impressions not yet written to the journal.
- Done when: the section exists, and a session that compacts still has the path afterwards.

### Step 7 - journal-review skill
- Files: `.claude/skills/journal-review/SKILL.md`.
- Change:
  - **Default (current session):** read the journal and call log, then append a
    `## Session summary` to the journal. It has 3-6 lines: per-tool call and error counts,
    the number of fallbacks, the best and worst experiences, and one suggested environment
    fix if any.
  - **`all` argument:** roll up every session since the last rollup into one cross-session
    summary, appended to `.claude/journal/ROLLUP.md` (local-only, like the journals).
- Done when: `/journal-review` on a real session produces the summary without reading
  anything outside `.claude/journal/`.

### Step 8 - Live shakedown and latency check
- Change:
  - Run one normal working session with everything enabled.
  - Measure the added per-call latency of the PostToolUse hook, since `powershell.exe`
    startup runs on every matched call.
  - Check how often the nudge fires, and adjust N.
- Done when: the median added latency per call is reported, the nudge fires roughly 2-5
  times per long session, and the journal has useful lines rather than noise.

## Out of scope

- Journaling or nudging inside subagents. Their calls still reach the call log, but there is
  no `SubagentStop` nudge, because cheap implementer agents would spend tokens on prose.
- A server-side `ToolFeedback` MCP tool (Option 3). Revisit once this has run for a while.
  It is the route for collecting feedback from eval models.
- Feeding journal content into `docs/current/` automatically. Promotion stays a human or
  explicit-session decision.

## Decisions (2026-10-01)

- **Local only.** Journals, call logs and `ROLLUP.md` stay under `.claude/journal/`, which the
  existing `.claude/*` rule in `.gitignore` already excludes, so no `.gitignore` change is
  needed. The skill folder stays untracked too, like `.claude/skills/commit/` today. The hook
  scripts, `settings.json` and `CLAUDE.md` are tracked and get committed.
- **Nudge threshold starts at N = 15** MCP calls. Step 8 may tune it.
- **Watch shell fallbacks:** the PostToolUse matcher covers Read/Grep/Glob/Bash/PowerShell
  as well as MCP tools.

## Risks and open decisions

- **Hook latency.** Measured at 475 ms per call when run synchronously, and moved off the
  critical path with `async: true` (see Implementation notes). Step 8 should confirm that the
  async hooks really add no visible delay, and that the CPU load of the background processes
  is acceptable when subagents run in parallel.
- **Nudge fatigue.** If the notes become filler ("nothing notable" every time), raise N or
  drop the error trigger. Low-value filler is the failure mode to watch for.
- **Payload fields** in Steps 1-3 (error signal, agent id, failure event) are unverified until
  Step 0.
- **Confidentiality.** Journals are internal. If this workflow is ever reused on a customer
  codebase, the call log stays content-free by design, but free-text notes could name customer
  symbols. Keep journals local in that case.

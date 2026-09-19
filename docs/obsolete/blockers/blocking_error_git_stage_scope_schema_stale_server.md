# Blocking error — `Git` tool's live schema is missing `scope` (shows `stageAll`), but source and error strings already use `scope`

**Status:** OPEN (informational — same root cause as `blocking_error_live_server_stale_after_enum_addition.md`, different trigger). Found 2026-09-12 while investigating a reported "`stage` errors reference a `scope` parameter not present in the tool's schema" task.

## What was reported

The MCP tool schema surfaced to this session for `Git` (via `ToolSearch`, i.e. what a calling
agent actually sees) exposes only:

```
commitHash, count, files, maxBytes, message, noCommit, paths, reason, stageAll, target
```

— a boolean `stageAll`, no `scope`. But `operation: "stage"` was reported erroring with messages
that reference a `scope` parameter, which a caller has no way to discover from that schema — a
schema/error-message mismatch per CLAUDE.md's failure doctrine.

## What the checked-out source actually contains

`RoslynSentinel.Server.Basic/SentinelGitTools.cs`, method `Git` (signature starting line 357):
the `stageAll: bool` parameter does not exist anywhere in source. In its place is:

```csharp
[Description("stage/commit: which files to stage. \"tracked\" (default) stages modifications and
deletions of already-tracked files only (git add -u) and does NOT stage new files. \"all\" stages
everything in the working tree including untracked files (git add -A). \"listed\" stages exactly
the paths you name in files/paths, untracked ones included — use this whenever you know which
files you want. Naming files alongside a scope other than \"listed\" is rejected, so a file list
can never be silently overridden.")]
GitStageScope scope = GitStageScope.tracked,
```

`GitStageScope` (`RoslynSentinel.Common/ToolEnums.cs:26-40`) is a `[JsonConverter(typeof(JsonStringEnumConverter))]`
enum (`tracked` / `all` / `listed`) whose doc comment says explicitly:

> Replaces the former `stageAll` boolean, which could silently override an explicit `files` list
> (a `files`+`stageAll:true` call ran `git add -A` and staged unrelated untracked files). Scope and
> file list are now one decision.

`scope` is a normal, public, `[Description(...)]`-annotated parameter — not internal, not dead code,
not hidden from the MCP attribute surface. `StageAsync` (`SentinelGitTools.cs:606-666`) and
`CommitAsync` (`:710-749`) both take and fully use it, including the exact validation messages that
were reported (`"You named files to stage but passed scope=\"{scope}\"..."` at line 616,
`"scope=\"listed\" stages exactly the files you name..."` at line 627). A source build from this
checkout will emit `scope` (not `stageAll`) in the JSON schema.

## Root cause

**Not a code defect.** This is the same failure mode already written up in
`blocking_error_live_server_stale_after_enum_addition.md`: the MCP server process backing a
session's live tool schema is a long-lived, already-JITed process. `stageAll` → `scope` was a
deliberate, completed migration (see the `ToolEnums.cs` doc comment above) — but a process started
before that migration's DLL was rebuilt keeps serving the old `stageAll`-shaped schema and, per
that same file's analysis, the *old compiled code path* would actually be what runs `stage` calls
too. (If the reported error text truly said `scope`, that specific detail may instead reflect the
reporter reading source/a different rebuilt instance rather than the exact live process that served
the stale schema — the two data points weren't captured from the identical call in this task's
handoff. Either way, the fix is not a source edit.)

This matches standing memory (`feedback_stale_server_before_rebuild`,
`project_loadsolution_does_not_rebind_server_binary`): check bin-vscode DLL mtime vs. git log before
concluding a live discrepancy is a source bug.

## Why no code change was made here

`SentinelGitTools.cs` and `ToolEnums.cs` are already internally consistent — `scope` is the one and
only stage/commit file-selection parameter in source, correctly wired end-to-end, with error
messages that name only parameters the schema (as compiled from current source) actually exposes.
Editing this source further would either be a no-op or would incorrectly reintroduce `stageAll`-shaped
drift into an already-fixed area. Per this task's own instructions, ambiguous/out-of-scope findings
get a blocker doc rather than a guessed edit.

## Next step

Whoever picks this up should rebuild and restart the live server (bin-vscode copy) and re-check the
`Git` tool's served schema before doing anything else with `stage`/`commit`. No source change is
pending on this file. Once confirmed the rebuilt server serves `scope` correctly, this can move to
`docs/obsolete/blockers/`.

## Related

- `docs/current/blockers/blocking_error_live_server_stale_after_enum_addition.md` — same root cause
  (stale long-lived process vs. recompiled enum/schema), different trigger (`GitOperation` branch
  member vs. `GitStageScope`/`stageAll`).
- `docs/current/blockers/blocking_error_git_stage_ignores_untracked_files.md` — the original
  `stageAll`-overrides-`files` defect that motivated the `scope` migration in the first place;
  already fixed in source.
- Memory: `feedback_stale_server_before_rebuild`, `project_loadsolution_does_not_rebind_server_binary`.

# RoslynSentinel

An MCP server exposing Roslyn-backed C# analysis and refactoring to AI agents.

## Mission

Let agents - **especially weak or self-hosted models** - navigate and refactor C# semantically by
calling structured tools, never by grep, regex replace, file-read loops or manual text edits. If an
agent reaches for a shell command to inspect or modify C# code, that is a gap in the tool surface, not
a deficiency in the agent.

## What belongs in this file

Loaded every session, so it is the most expensive file. Admit only an imperative a turn-one agent would
plausibly break, with a costly repeatable consequence; at most one line of "why". No history, incidents,
dates or commit hashes. Procedure over ~5 lines, reference tables and tool detail go to a skill or
`docs/current/references/` with a one-line pointer. Budget 15 KB (`scripts/Test-GuidanceHygiene.ps1`
checks it; the `tidy-guidance` skill cleans up and promotes memory entries).

## Agent conduct

- **Approval before editing.** Confirm the approach (a short summary) before any edit, even an obvious
  one; re-confirm if scope shifts. Reading and diagnosis need none; minor incidental finds (typo,
  off-by-one) need no separate round. Inline `// TODO` markers (below) need none at all.
- Batch small edits onto one subagent and resume it with `SendMessage` rather than one agent per item.
- Report a finished subagent in a few lines (changed, built/passed, decisions needed); flag high-token
  work before starting it.
- A substantial new topic mid-session: sketch it and suggest a fresh chat; do not deep-dive in place.
- A new `[McpServerToolType]` class needs a fresh chat session as well as a server restart.
- Comment intentional deviations from a codebase pattern, and a `[Test]` that fails only on full/parallel runs.
- A tool param added to stop a known model failure is mandatory unless a concrete usage needs the fallback.
- In agent/plan docs state the allowed workflow positively; never name a tool that is gated off.

## Deferred issues (no approval needed)

- On noticing an issue or deferring a fix, leave `// TODO <brief issue statement>` at the spot in the
  code and name it in your report. The code is the source of truth; `.md` files drift and miss what
  another plan or session already flagged. `Search` a file for `TODO` before editing it.
- Never hold work for all-or-nothing completion: new member beside the old, old `[Obsolete("Use X")]`,
  `// TODO` for the remaining callers, finish in slices. CS0618 warnings during a migration are expected.

## Shell

Windows: use the PowerShell tool for every shell command. Bash only for a genuine POSIX-only need;
if a Bash result looks even slightly off, re-run it in PowerShell.

## Gated tools

The tool surface is gated to save prompt tokens, so a tool you expect but cannot see is usually gated,
not missing. Before concluding it is missing or building a duplicate, call
`McpServerStatus(toolListing: inactive)` (optional `toolNameFilter`): it lists every tool with
`activeForThisMode` and `enabledBy`. If `enabledBy` names a `McpToolsetControl(toolSet:, enabled: true)`
call, make it yourself; if the tool is still missing from ToolSearch, ask the user for `/mcp` reconnect, then
enable again (the reconnect resets toolsets; a server restart does not help). If it names a mode or flag, stop and report the tool and hint (a configuration
decision for the user, not a blocker). Only if the listing is empty, try `Search(mode: "text", query: "Name = \"ToolName\"")`.

## Subagents

- **Edit directly when small and on-topic; dispatch when it would sidetrack.** The main session's job
  is design, planning and orchestration. Edit directly when the change is small (up to ~3 edits in
  2 files) and part of the current discussion: files already in context (non-C# files, comments, a
  small `.cs` change the session has just read), with the MCP tools for `.cs`. If it will exceed those
  caps, ask first. Dispatch `implementer` (several edits, few files), `implementer-sweep` (one
  mechanical operation, many files), `implementer-senior` (over the limits) or `test-writer` when the
  work is larger, needs code the session has not read, or would interrupt design or orchestration still
  in progress. Unsure: ask. Load the `dispatch-implementer` skill before the first dispatch (limits, and
  the two-concurrent-agents trial rule); the `enforce-dogfood.ps1` hook refuses a non-conforming brief.
  Why: implementation churn crowds out planning in the main context.
- **Exploration and review are dispatched.** The main session does only small reads (one outline,
  method, file or `git status`). Anything broader goes to `explore-tier1` (symbol/usage lookups) or
  `explore-tier2` (multi-file investigation, reviews; pass `model: sonnet` for a hard review). If it
  needs more than a couple of reads, dispatch.
- **Verification is never a supervisor job.** `modeleval-runner` and `planstep-runner` are only for runs
  with a model under test. A post-change `Build`/`RunTest`/baseline comparison is a plain call.

## Tool gaps: report, don't brute-force

If no tool can do a task as one semantic operation (a limit blocks it, or no tool exists) and the
blast radius is large, stop. Do not use many manual edits, a shell loop or text replace. Report
`TOOL-GAP:` (task, tool tried and its limit or "no tool", measured call sites/files/projects, suggested
change); the dispatcher records it in `docs/current/TODO.md` or a proposal and continues with any
staged slice. Prefer migration-first changes: new member beside the old, `[Obsolete("Use X")]`, move
callers in slices, remove the old one last. Why: brute force hides the gap and burns context.

## Dog-fooding (hard rule)

All C# reads and writes and all git operations go through the RoslynSentinel MCP tools, including work
on this repo's own source.

| Instead of | Use |
| --- | --- |
| `Read` a `.cs` | `ReadFile`, `GetFileOutline`, `GetMethodSource` (methods only; one field/property/constant: `Member(operation: view, memberName: ...)`) |
| `Grep`/`Glob` for C# | `Search` (modes: text, symbol, references, callers, implementations, attribute, all, namespace, class, interface, method, property, struct, record, enum, enum member, constructor, field) |
| `Edit`/`Write` a `.cs` | `Member`, `MethodSignature`, `ModifyModifier`, `ReplaceSnippet`, `ApplyDiff`, `RenameSymbol`, `MoveMember` (toolset `moveExtract` via `McpToolsetControl`) |
| shell `git status/log/diff/add/commit/revert` | `Git(operation: ...)` (shell only for rebase, merge, cherry-pick, restore, clean, stash drop/clear, tag push, worktree prune/lock/move) |
| `dotnet build/test` | `Build`, `RunTest` |

Use Edit/Write/Grep (not shell text replacement) for Markdown, JSON, `.ps1`, `.csproj` and other
non-C# files; shell is fine for files outside the repo. Why: chokepointing surfaces drift and sequencing bugs; falling back when a tool is awkward
hides them - the awkwardness is the finding.

Edit-tool traps (batch edits, exact snippet match, enum `values`, parameter order):
`docs/current/references/reference_edit_tool_gotchas.md`. Never pass `filePath` with `batchEdits`.

## Tool failures and hooks

**A tool failure is a blocking finding.** If a needed MCP tool fails, returns wrong data, is
unreachable or has no equivalent (and no staged route exists), finish in-flight edits, stop, write
`docs/current/blockers/blocking_error_<slug>.md` (slug from the real symptom) and follow
[reference_blocker_workflow.md](docs/current/references/reference_blocker_workflow.md). Do not retry
speculatively or route around it with shell tools.

`.claude/hooks/enforce-dogfood.ps1` blocks common violations (including `Read` on an in-repo `.cs`); use
the MCP tool instead, no report needed. It can still block a benign command that merely mentions
`.cs` or `git log`: if the command reads or edits a non-C# file, redo it with Read/Edit/Write/Grep;
otherwise report the false positive and stop - never reword a shell command to slip past it. A read-only command on another repo, with its absolute path after `git -C`, is an allowed route, not rewording.
A custom subagent that lists `Read` must also list `ReadFile`/`GetMethodSource`/`Search`.

- **Successful bypass** (a `Read`/`Edit`/`Write`/shell call on a `.cs`, or shell git, that went through
  when an MCP tool fit): report it and continue; no blocker doc. Deliberate bypass is legitimate only
  for a `.cs` outside the repo or a manual call you decided is right, never for a failed tool; routes
  and logging: `docs/current/references/reference_hook_bypass.md`.
- **Never delete a tracked file with a shell command.** Use `DeleteFile` so the server's workspace and
  ledger stay consistent (a shell delete no longer halts the session); report a shell delete as a
  successful bypass.

## Tool-experience journal

The `SessionStart` hook prints the journal path. Append `- HH:mm [+|-|~] ToolName: one sentence` right
after anything notable (compaction erases late notes), including every unblocked shell/built-in
fallback on C# with the reason. Hook-blocked attempts are not logged.

## Failure analysis

For every model-eval, PlanStepRunner or task failure: the model is a novice and the environment (tools,
schemas, descriptions, errors, prompts, assertions) is the expert, so ask what environment change
would have prevented it, never "was the model wrong?". Trace every cause to a `file:line`, quoted error
or turn number; unverified causes are hypotheses. Load the `failure-analysis` skill first.

## Working conventions

- Build (0 errors) before committing. Run **one** `RunTest`/`dotnet test`/`Build` at a time (subagents
  included); they share `bin/`/`obj/` and block on file locks.
- NUnit 5.0: `await` `Assert.ThrowsAsync`/`CatchAsync`/`DoesNotThrowAsync` (un-awaited asserts nothing).
- Never leak raw exceptions, stack traces or internal paths into a tool's `ResultError`; catch at the
  tool boundary and return a structured, actionable error.
- A not-found/zero-result error must rule out scope, name-shape, container and kind mistakes before
  claiming absence, state what was searched and name the next call.
- An unhandled `CS####` during automated work gets a `docs/current/blockers/` writeup immediately.
- Writing a blocker, finding, proposal, issue, design, plan, idea or reference doc: follow
  `docs/current/templates/README.md` (folder, filename, template); never read existing docs for style.
- ASCII-only punctuation in comments, docs, commit messages and prompts; non-ASCII becomes mojibake.
- Exclude `*/Worktree/` folders (PlanStepRunner harness clones) from diffs, searches and reviews.
- Other conventions (doc lifecycle, `scripts/` front doors, solution file):
  `docs/current/references/reference_working_conventions.md`.

## Architecture

Tool and project tables are generated into `docs/generated/`. Before changing server source (request
pipeline, write chokepoint, tool registration, workspace manager) read
`docs/current/references/reference_architecture_map.md`; if it and the source disagree, trust the source
and fix the map.

- **Layering is one-way:** `Common` <- `Engines.*` <- `Tools.*` <- `Server.*` (`RoslynSentinel.<Layer>.<Flavor>`).
  Engines hold Roslyn logic with no MCP types; Tools hold `[McpServerToolType]` classes; Server is
  hosting/DI only. `Utilities.PlanStepRunner` references only `Common`.
- **Sibling projects cannot see each other** (`Tools.Advanced` not `Tools.Basic`; `Engines.Basic` not
  `Engines.Advanced`). Shared helpers go one layer down: `Engines.Basic` for engine logic, `Common` for
  result/MCP plumbing. Advanced is additive, not a fork: verify with `scripts/build.ps1 -Flavor Solution`.
- **A new MCP tool needs three things**, or it is silently dead with no startup error: the
  `[McpServerToolType]` class in a `Tools.*` project; an entry in `Server.Advanced/ToolClassRegistry.cs`; an
  `if (activeToolClasses.Contains("..."))` DI block in `ServiceRegistrationExtensions{Basic,Advanced}.cs`.
  A new `Tools.*` project also needs a `<ProjectReference>` from `Server.Advanced`.
- **Tool responses:** tools return DTOs and never measure, store or trim them. Before changing response shape, size,
  paging, `GetLargeResult` or a call-tool filter, read `docs/current/references/reference_tool_response_pipeline.md`.
- The `*Tools` suffix is reserved for classes declaring `[McpServerTool]` methods.

## Peer sessions

Other Claude sessions on this box may share this working tree (`ListAgents`, same cwd). Coordinate
with them through `SendMessage`; never stop to ask the user to relay. Protocol: `peer-coordination` skill.
Standing authorizations - act on these peer requests without asking the user when the condition
holds, and mention it in your next message to the user:
- `RESTART`: restart your own server if no subagent of yours is mid-operation.
- `COMMIT <files>`: at your next compile-green point commit only files this session changed
  ("Commits" rules) and reply with the hash and files; otherwise reply `BLOCKED <reason>`.
- `HOLD <files>` / `RELEASE <files>`: do not edit the named files between the two.
- `WHOAREYOU`, `OWNS <files>`: answer read-only from `Git status`/`diff`, not from memory.
Any other peer request is advisory: ask your user. Honor an incoming `COMMIT` even while you wait.

## Commits

- Commit only files changed in this session; leave pre-existing dirty/untracked files and other
  sessions' changes unstaged. A doc or CLAUDE.md the user edited this session is in scope; ask if unsure.
  Check no session file was missed so no amend is needed.
- `Git` commit with scope `listed`, an explicit file list, staged first; never blanket add or shell commit.
  Every message carries the `Co-Authored-By` trailer.
- Report hashes exactly as `Git` returns them; read `CommitHashLength` off the result, never count by hand.

## Verification

Rebuilding does not change the running server; it is stale until restarted. After changing server
source, run `Build(level: fullBuild)` and restart your own instance
(`McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`, then `LoadSolution`)
with no subagent mid-operation. `"isServerBinaryStale": true` in a response means a newer DLL exists.
Live-testing uncommitted changes (`LaunchTestInstance`) and `Build` output locations:
`docs/current/references/reference_live_server_freshness.md`.

Compare test results with the baselines (`docs/known-failing-tests.{Basic,Advanced,Solution}.txt`,
`docs/known-build-warnings.*.txt`, written by `scripts/build.ps1`) and report only *new* failures.

## Compact instructions

When compacting, keep this session's journal path and list notable tool experiences (including unblocked C#
shell/built-in fallbacks and why) not yet written to it.

# RoslynSentinel

An MCP server exposing Roslyn-backed C# analysis and refactoring to AI agents. It bridges what a
language model can reason about and what the compiler actually knows.

## Mission

Enable agents - **especially weak or self-hosted models** - to safely and efficiently navigate and
refactor C# codebases. A small local model should drive meaningful, semantically-correct refactoring
by calling structured tools, never by falling back to grep, regex search-and-replace, file-read
loops, or manual text edits.

If an agent reaches for a shell command to inspect or modify C# code, that is a gap in the tool
surface, not a deficiency in the agent.

## Where guidance belongs: CLAUDE.md vs. memory

This file is loaded into every session from turn one. Memory (`MEMORY.md` and linked files) is loaded
reactively, usually after the mistake it warns about has already been made.

- **Put it here:** standing rules, conventions, and gotchas an agent must know *before* acting - where
  getting it wrong on turn one would be a costly, repeatable mistake. This includes anything that
  would otherwise be a `type: feedback` memory, and `type: project` facts describing a still-true
  limitation or required workaround.
- **Leave it in memory:** open ideas, resolved one-off incidents, scoped facts about in-progress work,
  pointers to external systems.
- **When promoting a memory entry here:** distill it to the current rule plus a one-line "why". Mark
  the source memory file superseded (banner + pointer to the section here) and move its index line to
  `MEMORY_CLOSED.md`.

## Shell tool choice

This is a **Windows environment: default to the PowerShell tool for every shell command.** Bash is
fallback-only, for a genuine POSIX/Git-Bash-only need, however simple the command looks - cmdlets,
`.\`-relative paths and error-to-exit-code translation silently break when run through Bash. If a
Bash call's result looks even slightly off, re-run it via PowerShell before trusting it.

## Diagnosing a "missing" or gated tool

The tool surface is intentionally gated: every active tool's schema costs prompt tokens each session,
so rarely-needed tools are off by mode. A tool you expect but don't see is usually gated, not missing.

Before concluding a tool doesn't exist, suspecting a stale binary, or building a duplicate, call
`McpServerStatus(toolListing: inactive)` (optionally with `toolNameFilter`). It returns every declared
tool with `className`, `activeForThisMode` and an `enabledBy` hint. Only if that comes back empty, fall
back to `Search(mode: "text", query: "Name = \"ToolName\"")` (for a tool in an assembly this server
flavor doesn't load).

If the tool is gated and would make the task materially easier:
- `enabledBy` names a `McpToolsetControl(toolSet: ..., enabled: true)` call: make that call yourself.
- `enabledBy` names a mode or flag: stop and report the tool name and hint instead of working around
  it with many more steps. This is a configuration decision for the user, not a tool failure - do not
  write a blocker doc.

## Subagent choice: verification is never a supervisor job

`modeleval-runner` and `planstep-runner` exist only to supervise a run in which **a model is under
test** (ModelEval test or PlanStepRunner run against an LLM). Ordinary build/test verification - a
post-change `Build`, a `RunTest` over a project or the full suite, a baseline comparison - is a plain
`Build`/`RunTest` call, backgrounded if it must stay off the main context. Never dispatch a runner
agent for it.

## Dispatching `implementer`

Load the `dispatch-implementer` skill before the first dispatch. In short: pin `model: "haiku"`; the brief
has `Files:` (3 or fewer .cs), `Symbols:`, `Call sites:` (measured with `InspectSymbol(aspect: blastRadius)`),
one `Acceptance:` check and `Out of scope:` (including the `RESCOPE:` clause). Over the limits, go
to `implementer-senior`. The `enforce-dogfood.ps1` hook refuses a non-conforming dispatch.

## Dog-fooding policy (hard rule)

All C# reads and writes, and all git operations, go through the RoslynSentinel MCP tools. This applies
to work on RoslynSentinel's own source, which is nearly every task here - "I'm editing the server
itself" is not an exemption.

- NEVER use plain Grep on C# code. Use `Search` / `FindReferences` (`kind: callers` for callers).
- NEVER use the built-in Edit/Write tools on `.cs` files. Use `ReplaceSnippet`, `Member`, `MoveMember`
  and the other MCP tools below.
- NEVER use shell `git add` or `git commit`. Use the `Git` tool.
- Edit, Grep and shell are fine for Markdown, JSON, `.ps1`, `.csproj` and other non-C# files, and for
  any file outside the repo. Git operations the `Git` tool doesn't implement (branch, push, checkout,
  worktree, stash) legitimately use the shell - see `docs/current/TODO.md`.

| Instead of | Use |
| --- | --- |
| `Read` a `.cs` | `ReadFile`, `GetFileOutline`, `GetMethodSource` (methods only; for one field/property/constant use `Member(operation: view, memberName: ...)` or `ReadFile`) |
| `Grep` / `Glob` for C# symbols | `Search(mode: text/symbol/references/all/namespace/class/interface/method/property/struct/record/enum/enum member/constructor/field)`, `FindReferences` |
| `Edit` / `Write` a `.cs` | `Member`, `MethodSignature`, `ModifyModifier`, `ReplaceSnippet`, `ApplyDiff`, `RenameSymbol`, `MoveMember` (toolset `moveExtract` - enable via `McpToolsetControl`) |
| `Bash(git status/log/diff/add/commit/revert)` | `Git(operation: ...)` |
| `Bash(dotnet build/test)` | `Build`, `RunTest` |

**Why:** chokepointing every operation is the only way to surface in-memory-vs-on-disk drift,
multi-call sequencing bugs, and edge cases that never appear in an isolated test. Falling back
whenever a tool is awkward hides exactly those failures - the awkwardness is itself the finding.

### ReplaceSnippet / Member gotchas

- Never pass `filePath` together with `batchEdits` on `ReplaceSnippet` - each batch edit carries its
  own path.
- Order batch edits so definitions (methods, enum members, fields) come before the call sites that
  reference them; the compile gate rejects any intermediate state that doesn't compile.
- Rename a field and all of its usages in the same atomic batch.
- Every snippet must match the file exactly, including whitespace and line endings. Copy surrounding
  context lines from a `ReadFile`/`GetMethodSource` result; never invent them.
- `Member(addMember)` takes exactly one declaration per call. Use `addTopLevelType` for a new
  top-level type; `Member` cannot edit an existing top-level record - use `ReplaceSnippet`.
- `ModifyEnum`'s `values` is the complete member list, not add-only - omitting a member deletes it.
- In new method signatures, `CancellationToken` is always the last parameter; new parameters go before it.

### Tool failures, bypasses and hooks

**A tool failure is a blocking finding.** If a needed MCP tool fails, returns wrong data, is
unreachable, or has no equivalent operation: finish any in-flight edit, stop advancing the task, and
write `docs/current/blockers/blocking_error_<slug>.md` (slug from the real symptom, never generic).
Do not retry speculatively and do not route around it with shell tools. Then follow the Blocker
workflow below.

A `PreToolUse` hook (`.claude/hooks/enforce-dogfood.ps1`) blocks the common violations mechanically.
It is a backstop, not the policy: it cannot see intent. It now also blocks the built-in `Read` on an
in-repo `.cs`, so an agent (for example a custom subagent) whose frontmatter lists `Read` but no MCP
tools must also list `ReadFile`/`GetMethodSource`/`Search`, or it is stranded; such an agent reports
the missing tools as a finding (`.claude/hooks/agent-mcp-tools.Tests.ps1` guards this). If the hook blocks something genuinely necessary, say so and
stop; do not reword the command to slip past it.

**Recovering from an accidental bypass.** If you used `Read`/`Edit`/`Write`/`Bash` on a `.cs` file, or
a shell git command, when an MCP tool fit: report it in your response and continue. Do not write a
blocker doc - a blocker is for the environment failing the agent. Exception: if the bypass caused a
new tool-side symptom you can't explain (a mutating tool now fails, or reports state inconsistent with
disk), treat that symptom as a tool failure, scoped to the actual anomaly.

**Deliberate hook bypass** (legitimate: a `.cs` file outside this repo, or a manual repo edit/read/git
call you have decided is right, e.g. raw bytes or a halted `Git` tool you already reported; never a
failed or missing MCP tool). Routes:
- **Outside this repo:** Edit/Write/Grep/Read on a `.cs` path outside the repo root is exempt automatically.
- **Bash/PowerShell:** put `DeliberateHookBypass: <reason>` in the command (as a comment) or the
  `description`. A bare keyword is still blocked.
- **Edit/Write/Grep/Read on repo files:** write `.claude/bypass.local.json` (valid 10 minutes):
  `{"reason": "...", "tools": ["Edit"], "paths": ["Foo.cs"]}` (`tools` may be `["Read"]`) - `reason`
  required, `tools`/`paths` optional narrowing; use the narrowest `paths`.

Accepted bypasses are logged to `.claude/journal/hook-bypass.jsonl` and are journal-worthy. The
`ReplaceSnippet` parameter check and the commit checks cannot be bypassed.

**Never delete a tracked file with a shell command.** Deleting a file the server has touched via
`rm`/`Remove-Item` trips the external-drift detector and halts every mutating tool for the session
(`errorCode: SessionHalted`; read-only tools keep working). Use `DeleteFile`. If already halted: call
`ExternalFileDrift(operation: List)` then `ExternalFileDrift(operation: Acknowledge, files: ...)`. This is a self-inflicted bypass
(report and continue), not a tool defect.

**Never dispatch parallel subagents for C# edits.** Subagents share the parent session's single server
process; one plain `Edit`/`Write` on a tracked `.cs` file trips `SessionHalted` for all of them and the
parent. Dispatch repo-wide mechanical edits sequentially.

## Tool-experience journal: note it as it happens

A `SessionStart` hook prints this session's journal path (`.claude/journal/<date>_<sid8>.md`,
local-only; re-printed after compaction). **Append one line right after anything notable** - never save
notes for the end, because compaction erases them. Format: `- HH:mm [+|-|~] ToolName: one sentence`
(`+` good, `-` bad, `~` mixed). Examples:

- `- 14:02 + MoveMember: moved 10 members and fixed 80 call sites in one call`
- `- 14:20 - ReplaceSnippet: "anchor not unique" didn't say which lines matched; took 3 retries`
- `- 14:31 ~ Read: read a .cs directly because I needed raw bytes; no MCP tool returns those`

Worth noting: one call replacing many; a confusing description or parameter; an error message that did
or didn't get you unstuck; and **every time you reach for a shell or built-in tool on C# code**, with
the reason. These are brief impressions, not blocker docs. A `Stop` hook asks for a line after 15 MCP
calls, any failed call, or any C# fallback; "`~ nothing notable`" is a fine answer. `/journal-review`
summarizes a session.

## Failure doctrine and root-cause discipline

Governs every model-eval run, PlanStepRunner step and task failure. The model is a novice; the environment
(tools, schemas, descriptions, error messages, prompts, assertions) is the expert, so ask "what change to the
environment would have prevented this or made recovery immediate?", never "was the model wrong?". Trace every
claimed cause to source with a `file:line`, quoted error or turn number; unverified causes are hypotheses.
Load the `failure-analysis` skill before analysing or writing up a failure.

## Working conventions

- Build (0 errors) before committing.
- Tests use **NUnit 5.0, not 4.6**: `Assert.ThrowsAsync`/`CatchAsync`/`DoesNotThrowAsync` must be
  `await`ed (an un-awaited one silently asserts nothing), and `TestDelegate`/`AsyncTestDelegate` are
  gone. Details: memory `reference_nunit5_test_conventions.md`.
- Never leak raw exceptions, stack traces, or internal paths into a tool's `ResultError` - catch at the
  tool boundary and return a structured, actionable error.
- Any unhandled `CS####` surfacing during automated work gets a writeup in `docs/current/blockers/`
  immediately, not deferred.
- `docs/current/TODO.md` is open-items-only; resolved entries move to `docs/current/CLOSED.md`.
- Writing a blocker, finding, proposal, issue, design, plan, idea or reference doc: read
  `docs/current/templates/README.md` for the folder and filename rule, then copy the matching
  template. Never read existing docs to learn the house style.
- `*/Worktree/` folders under a PlanStepRunner run are harness clones - exclude them from diffs,
  searches, and reviews, and never treat git output from inside one as authoritative.
- Use ASCII-only punctuation in any comment, doc, commit message, or prompt text you write: `-`/`--`
  for dashes, straight quotes, `<=`/`>=` for comparison glyphs. Non-ASCII punctuation turns into
  mojibake when it crosses an encoding mismatch in the file -> server -> MCP -> harness chain. Does not
  apply to non-ASCII characters that are the actual subject of a task (e.g. accented test fixtures).
- Before hand-rolling a shell loop for a repeated task (e.g. N model-eval runs), check the repo root
  for an existing front-door `.ps1` script - it likely handles failure modes (testhost.exe races, env
  vars, filter syntax) a naive loop will hit.
- The top-level solution file is `RoslynSentinel.slnx` (no root `.sln`). Target it directly for
  solution-wide `dotnet build`/`dotnet test`.

## Architecture

Tables of every tool and project are generated into `docs/generated/` (read those instead of searching).
Before changing server source (request pipeline, write chokepoint, tool registration, workspace manager),
read `docs/current/references/reference_architecture_map.md`. It is deliberately not auto-imported, to
keep every session's context small. If the map and the source disagree, trust the source and fix the map.

- **Layering is one-way:** `Common` <- `Engines.*` <- `Tools.*` <- `Server.*`.
  - `Engines.Basic` / `Engines.Advanced` - Roslyn analysis and refactoring logic. No MCP protocol
    types (`RequestContext<>`, tool attributes).
  - `Tools.Basic` / `Tools.Advanced` / `Tools.Experimental` - the `[McpServerToolType]` classes. The
    `*Impl` classes backing the Basic tools live in `Tools.Basic`.
  - `Server.Basic` / `Server.Advanced` - hosting, transport, mode resolution and DI registration only.
  - `Utilities.PlanStepRunner` - the plan-step harness exe; references only `Common`. The shared agent
    loop lives in `Common/AgentLoop`.
- **Sibling projects cannot see each other.** `Tools.Advanced` does not reference `Tools.Basic`;
  `Engines.Basic` cannot reference `Engines.Advanced`. A helper needed on both sides goes one layer
  down: `Engines.Basic` for engine logic, `Common` for result/MCP plumbing. Check the `.csproj`
  `<ProjectReference>` list if unsure.
- **Advanced is additive, not a fork.** A fix in a Basic project is sufficient on its own; there is no
  duplicate in Advanced. Verify with `build.ps1 -Flavor Solution` (or `-Flavor Basic` and
  `-Flavor Advanced`).
- **Adding a new MCP tool requires three things:**
  1. the `[McpServerToolType]`/`[McpServerTool(Name = ...)]` class, in a `Tools.*` project;
  2. an entry in a mode dictionary in `Server.Basic/ToolClassRegistry.cs` (shared by both servers);
  3. an explicit `if (activeToolClasses.Contains("..."))` DI block in
     `Server.Basic/ServiceRegistrationExtensionsBasic.cs` or
     `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`.

  Any one alone silently produces a dead tool with no startup error. A new `Tools.*` project also
  needs a `<ProjectReference>` from the `Server.*` project that hosts it (`Tools.Experimental` is
  hosted by `Server.Advanced` only).
- **The `*Tools` suffix is reserved for classes that declare `[McpServerTool]` methods.** Helpers use a
  different suffix (e.g. `TaskEnabledToolsHelper`).

## Commits

- Commit **only** the files changed in this session. Leave pre-existing dirty or untracked files and
  other sessions' changes unstaged unless told otherwise. If the user edited CLAUDE.md or a doc during
  the session, that counts as in-scope; ask if unsure. Before committing, check that no session file
  was missed, so no amend is needed.
- Use `Git` commit with scope `listed` and an explicit file list, staging before committing. Never use
  a blanket add or shell `git add`/`git commit`.
- Every commit message includes the `Co-Authored-By` trailer (see attribution instructions).
- Report commit hashes exactly as `Git` returns them. Never count characters by hand; read
  `CommitHashLength` off the result (a full SHA-1 is 40 characters).

## Blocker workflow

- Before investigating or fixing, check `MEMORY.md`, `git log`, `docs/current/CLOSED.md` and
  `docs/current/proposals/` for prior attempts - the gap may already be fixed, or tried and reverted.
- Once the blocker doc is written, dispatch an `implementer` to fix it (obeying the slice contract;
  `implementer-senior` if over the limits), confirm the fix, then restart the server with
  `McpServerControl` (see Verification) before resuming the original task.
- When fixed: add a regression test, compare against the pre-existing-failure baseline, then in the
  same commit add a resolution note (root cause, fix, commit hash, tests) and move the doc from
  `docs/current/blockers/` to `docs/current/blockers/resolved/`.
- If the workspace looks stale (a fix appears missing), reload the solution (`LoadSolution`) before
  concluding anything.
- If test runs fail on file locks, kill only stray `testhost` processes that belong to your own session
  (identify by PID/command line); other sessions may share this machine.

## Verification

### Live MCP server freshness

Rebuilding RoslynSentinel does not change the running server: VS Code builds and spawns a server only
when a session starts. After changing server source, the running server is **stale** until stopped and
respawned, so restart it before verifying fixes live.

- Server instances are isolated per session, so you may restart your own at any time:
  `McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`. Omitting the confirm
  param returns a refusal and stops nothing. Allow a few seconds; a clean `Connection closed` is
  normal. VS Code relaunches it on a fresh build; reconnect and re-run `LoadSolution`.
- Detection: any response carries `"isServerBinaryStale": true` while a newer build of a loaded
  `RoslynSentinel.*.dll` exists under a project's `bin/<Config>/` (omitted when current; can lag a few
  seconds). `Build(level: fullBuild)` writes those DLLs and so sets the flag; `Build(quickBuild)` is
  in-memory and does not. `McpServerStatus` gives `binaryStaleness.staleAssemblies`, `serverBinaryPath`,
  `serverBuildTimeUtc` (DLL mtime on disk) and `serverPid`. If you edited server source but have not run
  a `fullBuild`, assume the live server is old.
- Before restarting, make sure no subagent is mid-operation against the same server.
- If `Build` reports a suspicious warning/error count, force a full rebuild; incremental builds can
  skip recompiles and under-report.

**`LoadSolution` does not rebind which binary executes tool logic - only which files it analyzes.**
Pointing the server at another worktree's `.slnx` loads that worktree's files, but tool calls still run
the already-running process's code, with no error. Before trusting a live result as evidence about a
specific worktree, confirm the server was built from it (`serverBinaryPath` and `serverBuildTimeUtc` vs.
`git log`). If not, prefer `dotnet test` against that worktree over live MCP calls.

- Compare test results against the known pre-existing-failure baseline (`docs/current` / memory
  `reference_known_failing_tests`) and report only *new* failures.

## Compact instructions

When summarizing for compaction, keep this session's journal path (`.claude/journal/...md`) and list any
notable tool experiences (including shell/built-in fallbacks on C# and why) not yet written to it, so
they can be appended right after compaction.

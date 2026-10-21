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
call, make it yourself. If it names a mode or flag, stop and report the tool and hint (a configuration
decision for the user, not a blocker). Only if the listing is empty, try `Search(mode: "text", query: "Name = \"ToolName\"")`.

## Subagents (hard rules)

- **Implementation is never done in the main session** - not even small edits, unless the user asks and
  approves for that task (approval does not carry over); the one exception is a `// TODO` comment the
  session already knows about, added with `ReplaceSnippet`/`Member`. Dispatch `implementer` (several edits, few
  files), `implementer-sweep` (one mechanical operation, many files), `implementer-senior` (over the
  limits) or `test-writer`. The main session plans, briefs, dispatches, reviews and commits; reading,
  searching, building, tests, git and Markdown edits stay. Load the `dispatch-implementer` skill before
  the first dispatch; limits live in `.claude/implementer-tiers.json`; the `enforce-dogfood.ps1` hook
  refuses a non-conforming brief. If about to call `ReplaceSnippet`/`Member`/`ApplyDiff`/`RenameSymbol`/
  `MoveMember` on source, write a brief instead. Why: implementation churn crowds out planning in the main context.
- **C# edit dispatches are sequential, never parallel**: subagents share one server process, and one
  plain `Edit`/`Write` on a tracked `.cs` trips `SessionHalted` for all of them and the parent.
- **Exploration and review are dispatched.** The main session does only small reads (one outline,
  method, file or `git status`). Anything broader goes to `explore-haiku` (symbol/usage lookups) or
  `explore-sonnet` (multi-file investigation, reviews). If it needs more than a couple of reads, dispatch.
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

Edit/Grep/shell are fine for Markdown, JSON, `.ps1`, `.csproj`, other non-C# files and files outside
the repo. Why: chokepointing surfaces drift and sequencing bugs; falling back when a tool is awkward
hides them - the awkwardness is the finding.

**ReplaceSnippet / Member gotchas**
- Never pass `filePath` with `batchEdits`; each batch edit carries its own path.
- Order batch edits definitions-first (the compile gate rejects non-compiling intermediate states);
  rename a field and all its usages in one atomic batch.
- Snippets must match exactly, whitespace and line endings included; copy context from a
  `ReadFile`/`GetMethodSource` result, never invent it.
- `Member(addMember)` takes one declaration per call; `addTopLevelType` for a new top-level type;
  `Member` cannot edit an existing top-level record - use `ReplaceSnippet`.
- `ModifyEnum`'s `values` is the complete list; omitting a member deletes it.
- `CancellationToken` is always the last parameter; new parameters go before it.

## Tool failures and hooks

**A tool failure is a blocking finding.** If a needed MCP tool fails, returns wrong data, is
unreachable or has no equivalent (and no staged route exists), finish in-flight edits, stop, write
`docs/current/blockers/blocking_error_<slug>.md` (slug from the real symptom) and follow
[reference_blocker_workflow.md](docs/current/references/reference_blocker_workflow.md). Do not retry
speculatively or route around it with shell tools.

`.claude/hooks/enforce-dogfood.ps1` mechanically blocks common violations (including `Read` on an
in-repo `.cs`). A blocked attempt needs no report or log: use the MCP tool instead. The hook matches
literal text, so it can block a benign command that merely mentions `.cs` or `git log`; report that
false positive and stop - do not reword to slip past it. A custom subagent that lists `Read` must also
list `ReadFile`/`GetMethodSource`/`Search`.

- **Successful bypass** (a `Read`/`Edit`/`Write`/shell call on a `.cs`, or shell git, that went through
  when an MCP tool fit): report it and continue; no blocker doc. If it caused an unexplained tool-side
  symptom, treat that as a tool failure. Deliberate bypass is legitimate only for a `.cs` outside the
  repo or a manual call you decided is right, never for a failed tool; routes and logging:
  `docs/current/references/reference_hook_bypass.md`.
- **Never delete a tracked file with a shell command** (it trips external-drift detection and halts all
  mutating tools: `SessionHalted`). Use `DeleteFile`. If halted: `ExternalFileDrift(operation: List)`
  then `Acknowledge`; report it as a successful bypass.

## Tool-experience journal

The `SessionStart` hook prints the journal path. Append `- HH:mm [+|-|~] ToolName: one sentence` right
after anything notable (compaction erases late notes): one call replacing many, confusing
descriptions/parameters, helpful or unhelpful errors, and every unblocked shell/built-in fallback on C#
with the reason. Hook-blocked attempts are not logged. "`~ nothing notable`" is fine.

## Failure analysis

For every model-eval, PlanStepRunner or task failure: the model is a novice and the environment (tools,
schemas, descriptions, errors, prompts, assertions) is the expert, so ask what environment change
would have prevented it, never "was the model wrong?". Trace every cause to a `file:line`, quoted error
or turn number; unverified causes are hypotheses. Load the `failure-analysis` skill first.

## Working conventions

- Build (0 errors) before committing. Run **one** `RunTest`/`dotnet test`/`Build` at a time (subagents
  included); they share `bin/`/`obj/` and block on file locks. Several projects: one solution-scope run
  or sequential project runs.
- NUnit 5.0, not 4.6: `await` `Assert.ThrowsAsync`/`CatchAsync`/`DoesNotThrowAsync` (un-awaited asserts
  nothing); `TestDelegate`/`AsyncTestDelegate` are gone. See `docs/current/references/reference_nunit5_test_conventions.md`.
- Never leak raw exceptions, stack traces or internal paths into a tool's `ResultError`; catch at the
  tool boundary and return a structured, actionable error.
- An unhandled `CS####` during automated work gets a `docs/current/blockers/` writeup immediately.
- `docs/current/TODO.md` is open-items-only; resolved entries go to `CLOSED.md`. A finished plan,
  proposal or finding gets its Status line updated and moves to its folder's `closed/`.
- Writing a blocker, finding, proposal, issue, design, plan, idea or reference doc: follow
  `docs/current/templates/README.md` (folder, filename, template); never read existing docs for style.
  `docs/current/upstream/` and `docs/obsolete/` are not active documentation.
- Exclude `*/Worktree/` folders (PlanStepRunner harness clones) from diffs, searches and reviews; their git output is not authoritative.
- ASCII-only punctuation in comments, docs, commit messages and prompts (`-`/`--`, straight quotes,
  `<=`/`>=`); non-ASCII becomes mojibake across the file -> server -> MCP -> harness chain.
- Before hand-rolling a shell loop for a repeated task, check `scripts/` for a front-door `.ps1`
  (`roslynsentinel-modeleval.ps1`, `roslynsentinel-planstep.ps1`, `build.ps1`, ...).
- The solution is `RoslynSentinel.slnx` (no root `.sln`).

## Architecture

Tool and project tables are generated into `docs/generated/`. Before changing server source (request
pipeline, write chokepoint, tool registration, workspace manager) read
`docs/current/references/reference_architecture_map.md`; if it and the source disagree, trust the source
and fix the map.

- **Layering is one-way:** `Common` <- `Engines.*` <- `Tools.*` <- `Server.*` (`RoslynSentinel.<Layer>.<Flavor>`).
  Engines hold Roslyn logic with no MCP types; Tools hold `[McpServerToolType]` classes (Basic's `*Impl`
  classes live in `Tools.Basic`); Server is hosting/DI only. `Utilities.PlanStepRunner` references only `Common`.
- **Sibling projects cannot see each other** (`Tools.Advanced` not `Tools.Basic`; `Engines.Basic` not
  `Engines.Advanced`). Shared helpers go one layer down: `Engines.Basic` for engine logic, `Common` for
  result/MCP plumbing. Check the `.csproj` `<ProjectReference>` list if unsure.
- **Advanced is additive, not a fork.** A Basic fix suffices; verify with `scripts/build.ps1 -Flavor Solution`.
- **A new MCP tool needs three things**, or it is silently dead with no startup error: (1) the
  `[McpServerToolType]`/`[McpServerTool(Name = ...)]` class in a `Tools.*` project; (2) an entry in a mode
  dictionary in `Server.Basic/ToolClassRegistry.cs`; (3) an explicit `if (activeToolClasses.Contains("..."))`
  DI block in `Server.Basic/ServiceRegistrationExtensionsBasic.cs` or `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`.
  A new `Tools.*` project also needs a `<ProjectReference>` from its hosting `Server.*` project
  (`Tools.Experimental` is hosted by `Server.Advanced` only).
- The `*Tools` suffix is reserved for classes declaring `[McpServerTool]` methods; helpers use another (e.g. `TaskEnabledToolsHelper`).

## Commits

- Commit only files changed in this session; leave pre-existing dirty/untracked files and other
  sessions' changes unstaged. A doc or CLAUDE.md the user edited this session is in scope; ask if unsure.
  Check no session file was missed so no amend is needed.
- `Git` commit with scope `listed`, an explicit file list, staged first; never blanket add or shell commit.
  Every message carries the `Co-Authored-By` trailer.
- Report hashes exactly as `Git` returns them; read `CommitHashLength` off the result, never count by hand.

## Verification

Rebuilding does not change the running server; it is stale until restarted. After changing server
source, run `Build(fullBuild)` and restart your own instance
(`McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`, then `LoadSolution`)
with no subagent mid-operation. `"isServerBinaryStale": true` in a response means a newer DLL exists.
`LoadSolution` changes which files are analyzed, not which binary runs. Details:
`docs/current/references/reference_live_server_freshness.md`.

Compare test results with the baselines (`docs/known-failing-tests.{Basic,Advanced,Solution}.txt`,
`docs/known-build-warnings.*.txt`, written by `scripts/build.ps1`) and report only *new* failures.

## Compact instructions

When compacting, keep this session's journal path and list notable tool experiences (including unblocked C#
shell/built-in fallbacks and why) not yet written to it.

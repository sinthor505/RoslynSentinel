# Reference: RoslynSentinel architecture map (where things live, how a call flows)

**Status:** CURRENT 2026-10-04. Verified against source on this date; cites symbols, not line numbers (lines drift). Sections marked "not re-verified" were carried from older docs.

## Purpose

Read this before changing server source. It answers "which file owns X" and "what happens between
a tool call arriving and bytes landing on disk" so you do not have to search. It is an index of
pointers: open the cited file for detail, and trust the source over this doc if they disagree
(then fix this doc). Standing rules and gotchas live in CLAUDE.md; this is the map.

## How it works

### Layers and projects (one-way: Common <- Engines <- Tools <- Server)

| Layer | Projects | Owns |
| --- | --- | --- |
| Common | `RoslynSentinel.Common` | Workspace manager, result types, filters' helpers, schema patcher, toolset catalog |
| Engines | `Engines.Basic`, `Engines.Advanced` | Roslyn analysis/refactoring logic. No MCP types |
| Tools | `Tools.Basic`, `Tools.Advanced`, `Tools.Experimental` | `[McpServerToolType]` classes plus the `*Impl` classes behind the Basic tools |
| Server | `Server.Basic`, `Server.Advanced` | Entry points, arg parsing, mode resolution, DI + request filters. No tool/engine logic |
| Harness | `Utilities.PlanStepRunner` | Plan-step runner exe; references only `Common` |

Sibling projects cannot see each other (`Tools.Advanced` cannot reach `Tools.Basic`; `Engines.Basic`
cannot reach `Engines.Advanced`): shared helpers go one layer down. Advanced builds on Basic, it is
not a fork. Tests: `Tests`, `Tests.Basic/Advanced`, `Tests.Tools.Basic/Advanced`, `Tests.Server`,
`Tests.Integration`, `Tests.Battery.Basic/Advanced`, `Tests.ModelEval`, `Tests.PlanStepRunner`,
`Tests.SubAgent`, `Tests.Asyncify`.

Tool-class shapes vary: some Basic tools are a thin class over an `*Impl` (e.g. `WorkspaceTools` ->
`WorkspaceReadNavigationImpl`; `RefactoringStructuralTools` -> `RefactoringStructuralImpl`), others
hold engines and the workspace manager directly (e.g. `WholeFileWriteTools`).

### Startup flow (`Server.Advanced/ServerStdio.cs` `Startup`; Basic's is analogous)

1. `ServerStartupHelpers.ParseArgs` -> modes, include/exclude tools, solution path.
2. `*Options.Configure(args)` for each static options class (`LlmOptions`, `ReplaceSnippetOptions`, ...).
3. `HandleListTools` (`--list-tools`, exits) and `HandleNoActiveTools` (exits with guidance).
4. Host build with `EnableValidateOnBuild`: a bad DI graph fails here, not at first tool call.
5. `AddRoslynSentinelEnginesBasic/Advanced` (engine singletons), then `AddMcpServer`, transport,
   then `AddRoslynSentinelToolsBasic/Advanced` (tool classes + request filters).
6. `SmokeResolveToolTypes`, `WarmupAndAutoLoad*` (MSBuildLocator warm-up, optional solution load).
7. `ConsoleMode.WriteStartupDump` writes `tool_list_<mode>.json` and `tool_list_simple_<mode>.json`
   (and `WriteMethodInventory`) into `AppDomain.CurrentDomain.BaseDirectory`, i.e. the server's
   bin folder, not the repo.

### Tool registration: three places, none sufficient alone

1. The class: `[McpServerToolType]` with `[McpServerTool(Name = ...)]` methods, in a `Tools.*` project.
2. `Server.Basic/ToolClassRegistry.cs`: add the class name to the right mode in
   `BasicModeToToolClasses` / `AdvancedModeToToolClasses`. `ResolveActiveToolClasses`
   (`Server.Basic/ServerStartupHelpers.cs`) turns modes + `--include-tools/--exclude-tools` into the
   active class set.
3. A DI block in `Server.Basic/ServiceRegistrationExtensionsBasic.cs` (`AddRoslynSentinelToolsBasic`)
   or `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` (`AddRoslynSentinelToolsAdvanced`):
   `if (activeToolClasses.Contains("X")) { AddSingleton<XImpl>(); AddSingleton<X>(); mcpBuilder.WithSentinelTools<X>(); }`.
   `WithSentinelTools` is `Common/McpToolSchemaPatcher.cs` (patches emitted schemas).

A missing step yields a dead tool with no startup error. `ServerStatusTools` is the exception: always
registered, outside the registry, so `McpServerStatus` works in any mode.

**claude-lean** (`ToolClassRegistry.ClaudeLeanMode`) is exclusive and also applies a per-tool allow-list
(`ClaudeLeanToolNames`, `Common/ToolAllowList.cs`), because its classes hold non-Core tools too. Tools
outside the allow-list are switched on at runtime by `McpToolsetControl`: `Common/ToolsetCatalog.cs`
(`ToolsBySet`: declarations / moveExtract / projectAdmin) and `Common/ToolsetService.cs`.
`ClaudeLeanOnDemandToolClasses` makes DI register those classes' dependencies without exposing their tools.
To make a tool reachable in claude-lean it must be in `ClaudeLeanToolNames` or a `ToolsBySet` entry.

Ground truth for "does this tool exist / is it gated": `McpServerStatus(toolListing: inactive)`.

### Request pipeline (`WithRequestFilters` in `AddRoslynSentinelToolsBasic`)

First added is outermost. In registration order:

1. `AddToolCallEchoFilter`: stamps call id/tool/args onto the first text block (outermost, sees final content).
2. `AddArgumentValidationFilter` (`Server.Basic/ToolArgumentValidator.cs`): repairs parameter-name case,
   rejects unknown/missing parameters before the SDK binder runs.
3. Exception catch-all: `SolutionNotLoadedException` -> message with `IsError=false`; any other escape -> `IsError=true`.
4. Domain-failure sync: response JSON with top-level `isSuccess:false` -> `IsError=true`.
5. Post-call drift diagnostic (`GetContentExternalFileChangesAsync`, log only).
6. Large-result offload (`Common/LargeResultHelper.cs`): over `OffloadThresholdBytes` the body goes to disk and the
   caller gets a `resultId` for `GetLargeResult` (which is exempt, to avoid a re-offload loop).
7. Orientation (automatic) breaker, then the unrecoverable breaker. The latter refuses every tool that is not
   marked `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]` on its method
   (`Common/UnrecoverableBreakerAttribute.cs`, resolved by `UnrecoverableBreakerPolicy.IsAllowed` via reflection).
   Absent attribute = refused. Currently 8 names are allowed: `ReadFile`, `ListAll`, `ListSolutionItems`,
   `GetFileOutline`, `GetOperationDetail`, `GetWorkspaceHealth`, `IsSessionHalted`, `Git`. A tool name declared by
   both a facade (`WorkspaceTools`) and its own class needs the attribute on both.

Tools themselves return `SentinelCallToolResult<T>` (`Common/SentinelCallToolResult.cs`) with `IsSuccess=false`
instead of throwing; see the CLAUDE.md rule on `ResultError`.

### Workspace manager (`Common/PersistentWorkspaceManager.cs`)

One singleton; `ISolutionProvider`, `IWorkspaceManager`, `IWorkspaceReader` all resolve to it
(`AddRoslynSentinelEnginesBasic`). It holds the in-memory `Solution`, a `FileSystemWatcher`
(`OnFileSystemChanged`, `OnDebounceTimerElapsed`), content hashes of known files (`_knownFileHashes`) for
drift detection, the compilation cache, the scoped operation ledger, the breakers and the session-halt latch.

### Write chokepoint

`PersistentWorkspaceManager.ApplyProposedChangesAsync` (interface `Common/IWorkspaceMutator.cs`). Every
`.cs` write must go through it, directly or via a caller that does. Order of refusals, verified in source:
session-halt latch -> unrecoverable breaker -> scoped-ledger block per target -> write/delete overlap ->
external-drift check (a confirmed hit trips the session halt). The full 11-step order (pre-image capture for
undo, EOL guard, no-op and whitespace-only skip, watcher-loop suppression via `_internalChanges`, IOException
retry, optional rollback, in-memory resync) is in `docs/current/reference-code-file-write-paths-v1.md`
(re-verified 2026-10-04), along with the callers table and the one sanctioned direct write.

Most tools reach it through `Common/ValidateAndApplyHelper.ValidateAndApplyAsync` (compile validation via
`ValidationEngine`, EOL-change refusal, dry-run) rather than calling it raw. Direct callers as of 2026-10-04:
`WholeFileWriteTools`, `WorkspaceFileEditImpl`, `WorkspaceProjectManagementImpl`, `MsToolAugmentEngine`,
`AsyncBatchEngine`, `AsyncifyTools`, `CommentingTools`. Find the current set with
`FindReferences(symbolName: ApplyProposedChangesAsync, kind: callers)`.
Nothing compiler-enforced stops a new `File.WriteAllText`; `Tests.Basic/WriteChokepointGuardrailTests.cs`
covers EOL/line-count behaviour of the shared path, not "no bypass".

### Read chokepoint (partly built)

`Common/IWorkspaceReader.cs` defines the intended gate: every method takes a mandatory `ReadSource`
(`Committed` / `IncludeStaged`). Adoption is minimal: on 2026-10-03 the only consumer outside DI registration is
`CloneDetectionEngine`. Everything else still takes `ISolutionProvider` / `PersistentWorkspaceManager` and calls
`GetCurrentSolutionAsync` (389 call sites in 78 files as of 2026-09-24, per the design doc). It is the
prerequisite for staged writes: see `docs/current/design_read_chokepoint.md`, `docs/current/proposal_staged_writes.md`.

## Usage

| I want to... | Go to |
| --- | --- |
| Add a tool | The three registration places above; then `McpServerStatus` to confirm it is active |
| Change which tools a mode exposes | `ToolClassRegistry.cs` (both dictionaries; Advanced is its own map) |
| Expose a tool in claude-lean | `ClaudeLeanToolNames` (always on) or `ToolsetCatalog.ToolsBySet` (on demand) |
| Persist a source edit | `ValidateAndApplyHelper.ValidateAndApplyAsync` -> `ApplyProposedChangesAsync` |
| Add request-level behaviour | A filter in `AddRoslynSentinelToolsBasic`; mind the order above |
| Let a new read-only tool run during an unrecoverable halt | Put `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]` on the tool method; update the golden set in `Tests.Server/UnrecoverableBreakerPolicyTests.cs` |
| See every tool, its class, modes and breaker status | `docs/generated/architecture_tools.md` (generated; do not edit) |
| See project-to-project references | `docs/generated/architecture_projects.md` (generated; do not edit) |
| Add a startup option | Static options class in `Common` + `Configure(args)` at every `ServerStdio`/`ServerHttp` entry point |
| Understand a tool's result/error shape | `Common/SentinelCallToolResult.cs`, `ToolErrorCode` |
| See what the server exposes right now | `McpServerStatus(toolListing: all)` or the `tool_list_<mode>.json` in the server bin folder |

## Gotchas

- The remark on `ApplyProposedChangesAsync` points at `docs/reference-code-file-write-paths-v1.md`; the real path is
  under `docs/current/` (unfixed as of 2026-10-04).
- The unrecoverable-breaker allow-list is attribute-driven: a new read-only tool is refused during a halt until its
  method carries the attribute (and the golden test is updated). Tools missing it fail closed, not open.
- `docs/generated/*.md` are produced by `Tests.Server/ArchitectureDocGenerator.cs` and guarded by
  `ArchitectureDocFreshnessTests`. A stale file fails that test; regenerate with `scripts/Generate-ArchitectureMap.ps1`
  (sets `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1`). Adding a tool class or changing the registry will require it.
- Registry gaps surface as silent dead tools, not startup errors (see registration above).
- Changed server source does not change the running server: check `isServerBinaryStale` / see CLAUDE.md Verification.

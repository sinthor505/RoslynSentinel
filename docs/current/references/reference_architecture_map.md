# Reference: RoslynSentinel architecture map (where things live, how a call flows)

**Status:** CURRENT 2026-10-09. Verified against source on this date; cites symbols, not line numbers (lines drift). Sections marked "not re-verified" were carried from older docs.

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
| Server | `Server.Advanced` (the only server project; the former `Server.Basic` was folded in) | Entry points, arg parsing, mode resolution, DI + request filters. No tool/engine logic |
| Harness | `Utilities.PlanStepRunner` | Plan-step runner exe; references only `Common` |

Sibling projects cannot see each other (`Tools.Advanced` cannot reach `Tools.Basic`; `Engines.Basic`
cannot reach `Engines.Advanced`): shared helpers go one layer down. Advanced builds on Basic, it is
not a fork. Tests: `Tests`, `Tests.Basic/Advanced`, `Tests.Tools.Basic/Advanced`, `Tests.Server`,
`Tests.Battery.Basic/Advanced`, `Tests.ModelEval`, `Tests.PlanStepRunner`,
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
   into `ServerInstancePaths.Current.WorkingDir`.

Other startup args (own parsers in `ServerStartupHelpers`, not `ParseArgs`): `--transport=stdio|http`,
`--port=N` (0 = OS-assigned; the HTTP host prints `listening: http://127.0.0.1:<port>/mcp`, not first on
stdout), `--bind=loopback|any` (default loopback), `--respawn=auto|none` (default: stdio auto, http none;
selects the `StopServer` text), `--log-dir`, and `--working-dir=<path>` (`Common/ServerInstancePaths`: logs,
`crash.log`, tool dumps, `stopped-by-script.marker`; default = the binary folder, marker in its parent).

### Tool registration: three places, none sufficient alone

1. The class: `[McpServerToolType]` with `[McpServerTool(Name = ...)]` methods, in a `Tools.*` project.
2. `Server.Advanced/ToolClassRegistry.cs`: add the class name to the right mode in
   `ModeToToolClasses` (the one union map). `ResolveActiveToolClasses`
   (`Server.Advanced/ServerStartupHelpers.cs`) turns modes + `--include-tools/--exclude-tools` into the
   active class set.
3. A DI block in `Server.Advanced/ServiceRegistrationExtensionsBasic.cs` (`AddRoslynSentinelToolsBasic`)
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

Every tool call runs through 13 call-tool filters (`CallToolFilterRegistrations` in
`Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, first registered is outermost), which see only the request and
serialized text. Inside them, `ToolResultMarshaller` turns the typed result into the response text at the SDK's
`MarshalResult` hook, and owns the size budget, offload and paging. The filter list, the marshaller's decisions and
`GetLargeResult` paging are in [reference_tool_response_pipeline.md](reference_tool_response_pipeline.md).

Tools themselves return `SentinelCallToolResult<T>` (`Common/SentinelCallToolResult.cs`) with `IsError=true`
instead of throwing; see the CLAUDE.md rule on `ResultError`.

### Workspace manager (`Common/PersistentWorkspaceManager.cs`)

One singleton; `ISolutionProvider`, `IWorkspaceManager`, `IWorkspaceReader` all resolve to it
(`AddRoslynSentinelEnginesBasic`). It holds the in-memory `Solution`, a `FileSystemWatcher`
(`OnFileSystemChanged`, `OnDebounceTimerElapsed`), content hashes of known files (`_knownFileHashes`) for
drift detection, the compilation cache, the scoped operation ledger, the breakers and the session-halt latch.
`GetCurrentSolutionAsync` and `ResolveFromWire` wait up to 30 s while `SolutionLoadStatus` is `Loading`
(`NotLoaded`, `Loading`, `Loaded`, `Failed`); a load that exceeds its timeout (`LoadSolution(timeoutSeconds)`,
default 30) is cancelled and surfaces `SolutionLoadTimeout`.

### Write chokepoint

`PersistentWorkspaceManager.ApplyProposedChangesAsync` (interface `Common/IWorkspaceMutator.cs`). Every
`.cs` write must go through it, directly or via a caller that does. Order of refusals, verified in source:
session-halt latch -> unrecoverable breaker -> scoped-ledger block per target -> write/delete overlap ->
external-drift check (a confirmed hit trips the session halt). The full 11-step order (pre-image capture for
undo, EOL pre-pass, byte-identical skip, watcher-loop suppression via `_internalChanges`, IOException
retry, optional rollback, in-memory resync) is in `docs/current/references/reference_code_file_write_paths.md`
(callers table re-verified 2026-10-04, EOL parts 2026-10-26), along with the sanctioned writes outside the chokepoint.

**EOL policy.** `--eol auto|crlf|lf` (`Common/EolPolicyOptions.cs`, static, set at each server entry point; default `auto`) feeds
`EolResolver.Resolve(mode, preImageText, editorConfigEol)` (`auto` = single-style pre-image, then `.editorconfig` `end_of_line` via
`EditorConfigEolReader`, then the platform; `.editorconfig` is not read under an explicit `crlf`/`lf`). The chokepoint converts the whole
file to that EOL (`EolWritePolicy`, `ComputeEolPrePass`) and refuses only if the final bytes are not uniform. Engines get the same value
from `IEolResolver.ResolveEol` / `ResolveEolAsync` (implemented by the workspace manager; `IWorkspaceMutator` and `IWorkspaceReader` extend
it) and pass it as `eol`; they never detect it from the source file. `EolFileWriter` covers the two workspace writes outside the chokepoint.
Guards: `BareFileWriteRatchetTests`, `NewlineLiteralRatchetTests`, `EolAuditRatchetTests` (all `Tests.Basic`).

Most tools reach it through `Common/ValidateAndApplyHelper.ValidateAndApplyAsync` (compile validation via
`ValidationEngine`, collateral-change check, dry-run) rather than calling it raw. Direct callers as of 2026-10-04:
`WholeFileWriteTools`, `WorkspaceFileEditImpl`, `WorkspaceProjectManagementImpl`, `MsToolAugmentEngine`,
`AsyncBatchEngine`, `AsyncifyTools`, `CommentingTools`. Find the current set with
`FindReferences(symbolName: ApplyProposedChangesAsync, kind: callers)`.
Nothing compiler-enforced stops a new `File.WriteAllText`, but `Tests.Basic/BareFileWriteRatchetTests.cs` fails one in production code
outside `FileIoHelper` (allowlist of non-workspace writes); `WriteChokepointGuardrailTests.cs` and `EolChokepointConversionTests.cs`
cover EOL/line-count behaviour of the shared path.

**Agent attribution and operating mode.** A `PreToolUse` hook (`.claude/settings.json`, matcher on the RoslynSentinel MCP tools)
runs `.claude/hooks/bin/agent-identity-hook.exe`, which adds `_agentId` (`main` outside a subagent), `_agentType` and
`_sessionId` to the call's arguments. The exe is a local build output: publish it with `scripts/publish-agent-identity-hook.ps1`;
a missing exe fails open, and the call runs with no identity. A new `_x` key must ship in this order, because a server that does
not strip it refuses every call as an unknown parameter and hook settings reach every session at once: commit the server side,
restart every window, confirm with `McpServerStatus`, and only then republish the helper.
`--operating-mode=auto|single-agent|multi-agent` (env `ROSLYNSENTINEL_OPERATING_MODE`, `Common/AgentOperatingModeOptions.cs`)
fixes the mode for the process; `auto` latches on the first tool call, `MultiAgent` if it carried an agent id. `McpServerStatus`
reports `operatingMode`, `operatingModeSource` and `callerAgentId`. In `MultiAgent` the scoped operation ledger cannot open, so
`MoveMember` refuses unresolved call sites before writing. In `SingleAgent` a different known agent is refused while one is open.

The edit attribution ledger (`Common/EditAttributionLedger.cs`, in memory, per server, last 8 writes per file; not the scoped
operation ledger) records who wrote each file. Inside the lock, after the refusals above, the chokepoint refuses with
`ConcurrentEditRefused` when a different known agent wrote a target after this call started (`CheckConcurrentEditConflict`), or
wrote anywhere since the call started and re-running the compile gate now finds new errors (`RevalidateAfterForeignWriteAsync`,
only for a call whose gate passed, `ToolCallContext.GatePassed`). `UndoLastApply` refuses over another agent's later write, using
`OperationItemRecord.AppliedByAgentId`. `Common/EditAttributionFindings.cs` adds last-writer `Findings` to `Build`,
`GetDiagnostics`, compile-gate failures and `Git(status)`. `Common/NotFoundEvidence.cs` adds them to not-found errors by searching
the operation blobs, which every window shares, and then Git.

**Stale-read guard.** The observed-content ledger (`_observedHashes` in `PersistentWorkspaceManager`, interface
`Common/IObservedContentLedger.cs`, hash `Common/ContentFingerprint.cs`) holds, per agent key and path, the hash of the file text
last returned to that agent by `ReadFile`, `GetMethodSource` or Search text mode. After pre-image capture and before the EOL
pre-pass, a call passing `StaleReadPolicy.RefuseIfObservedStale` (`WriteFile`, `DeleteFile`, `ApplyDiff` files format) is refused
with the non-latching `StaleRead` code if a target's entry differs from disk or memory. Detail:
`reference_file_watcher_and_drift_detection.md` section 5.

**Session halt and acknowledge.** The drift latch is `_sessionHalted` in `PersistentWorkspaceManager`; the halt-stamp
filter (pipeline item 5) shows it, and any tripped breaker, on every response. `ExternalFileDrift(operation:
Acknowledge)` (`Tools.Basic/AdminTools.cs`) clears the latch even when only some files are named. Files not named stay
in `_externalChanges` and re-trip the halt on the next write that touches them; clearing everything needs
`acknowledgeScope: ConfirmAll`. `LoadSolution(forceReload)` does not drain `_externalChanges` (see the comment in
`PersistentWorkspaceManager`). The unrecoverable halt has no reset, but `ExternalFileDrift` is allowed during it; it
clears only drift state and the chokepoint still refuses every write.

### Read chokepoint (partly built)

`Common/IWorkspaceReader.cs` defines the intended gate: every method takes a mandatory `ReadSource`
(`Committed` / `IncludeStaged`). Adoption is minimal: on 2026-10-03 the only consumer outside DI registration is
`CloneDetectionEngine`. Everything else still takes `ISolutionProvider` / `PersistentWorkspaceManager` and calls
`GetCurrentSolutionAsync` (389 call sites in 78 files as of 2026-09-24, per the design doc). It is the
prerequisite for staged writes: see `docs/current/design/design_read_chokepoint.md`, `docs/current/proposals/proposal_staged_writes.md`.
The observed-content ledger (stale-read guard, above) is read-side bookkeeping that does not depend on this reader: read tools
call `RecordObserved` explicitly, because the reader is solution-granular and cannot know which file a caller saw.

## Usage

| I want to... | Go to |
| --- | --- |
| Add a tool | The three registration places above; then `McpServerStatus` to confirm it is active |
| Change which tools a mode exposes | `ToolClassRegistry.cs` (both dictionaries; Advanced is its own map) |
| Expose a tool in claude-lean | `ClaudeLeanToolNames` (always on) or `ToolsetCatalog.ToolsBySet` (on demand) |
| Persist a source edit | `ValidateAndApplyHelper.ValidateAndApplyAsync` -> `ApplyProposedChangesAsync` |
| Get the line ending for engine-built text | `IEolResolver.ResolveEol(path, preImageText)` / `await workspace.ResolveEolAsync(document, ct)`; never a `"\n"` literal or `DetectDominantEol` on the source |
| Add request-level behaviour, or change a tool response or its paging | [reference_tool_response_pipeline.md](reference_tool_response_pipeline.md): a filter sees text only, the marshaller sees the typed result |
| Let a new read-only tool run during an unrecoverable halt | Put `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]` on the tool method; update the golden set in `Tests.Server/UnrecoverableBreakerPolicyTests.cs` |
| See every tool, its class, modes and breaker status | `docs/generated/architecture_tools.md` (generated; do not edit) |
| See project-to-project references | `docs/generated/architecture_projects.md` (generated; do not edit) |
| Add a startup option | Static options class in `Common` + `Configure(args)` at every `ServerStdio`/`ServerHttp` entry point |
| Understand a tool's result/error shape | `Common/SentinelCallToolResult.cs`, `ToolErrorCode`; size and `largeResult`: [reference_tool_response_pipeline.md](reference_tool_response_pipeline.md) |
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

# Plan: Move the Git area into its own production project (RoslynSentinel.Git) and its own test project (RoslynSentinel.Tests.Git)

**Status:** IMPLEMENTED 2026-11-24. RoslynSentinel.Git and RoslynSentinel.Tests.Git exist (7aa47fc6..bcc5031d, docs 24703556); full run 0 failures, Tests.Tools.Basic wall 1,189 s -> 277 s, Tests.Git 760 s (Step 23 table). Step 19 partial: GitImpl -> GitEngine done, namespace rename carried by proposal_multi_file_movefile_and_namespace_change_tool.md.

## Problem

Git is 55% of the full run's summed test-seconds (4,516 of 8,134 in run `48fb08e2-20261124-111028-65836`, 49 fixtures) while
its production files are about 3.3% of commits (110 of about 3,300 in 60 days). Today "did Git change" is a file-name guess
(`[Category("OutOfProcess")]` plus hand-kept globs), and the Git fixtures share `RoslynSentinel.Tests.Tools.Basic` (918 s
wall, the full run's critical path) with everything else. Evidence, motivation, alternatives and the Area 2/3 inventories:
[proposal_isolate_heavy_test_areas_by_project.md](../../proposals/proposal_isolate_heavy_test_areas_by_project.md) (the design;
this plan does not repeat it).

The second goal is single responsibility (owner, 2026-11-24). `RoslynSentinel.Common` has become a catch-all project:
result and MCP plumbing, the workspace manager with the MSBuild/Roslyn packages, process spawning and the Git plumbing all
sit in one assembly. Git is its largest cohesive cluster (26 files, about 7,700 lines, plus `GitWorktree/`), so this plan is
also the first cut at shrinking Common. The new `RoslynSentinel.Git` project has one job (talking to git), its engine names
no tools and Common reaches it only through the narrow seam of Step 10. The move should leave the boundary visible in the
code as well as the project layout (see the 2026-11-24 namespace and rename entry in Decision). Further clusters in Common are
candidates for later splits once this move has proven the mechanics.

Facts verified against source for this plan (2026-11-24; line numbers read at HEAD `a82c8544`, HEAD at the revision is `3ffe89f6`;
every slice re-finds a site by member name before editing, lines drift), where they differ from or add to the proposal:

- **The pins the proposal relies on for "text-neutral" do not pin Git text.** `CallableToolHintLiveSurfaceTests`
  (`Tests.Tools.Basic/CallableToolHintLiveSurfaceTests.cs`) tests the ReadFile not-found hint through
  `WorkspaceFileEditImpl.BuildFileNotFoundError`, nothing Git. `ToolHintRatchetTests` (`Tests.Server`) validates that
  `ToolCallHint.Call` literals name real tools and parameters and keeps a baseline of hand-written hints
  (`docs/known-handwritten-tool-hints.txt`, no Git entries); it does not compare sentence text. The Git tests assert error
  codes (`GitErrorCodes.*`, for example `GitCommitHookRejectionTests.cs:99`), a few `Does.Contain` checks
  (`GitToolsSmokeTests.cs:2284` pins only `"forceReload: true"` in one stash note) and `GitCommitSeamParityTests.cs:945`
  compares `detail=` between two readers (a parity check, not a frozen text). Extracting the hint text is therefore NOT
  guarded today. Step 2 adds golden (full-sentence) pins; this is a planner-level coverage gap, not something an all-green
  run would reveal.
- **Hint-site counts (by `ToolCallHint.Call` literal, 53 total):** 51 in `GitImpl.cs`, 2 in `GitTools.cs` (`:158`, `:232`, tool
  side, stay). Of the 51: the `GitErrorDetails` table (`GitImpl.cs:112-137`) has 23 members, 16 of them rendering tool calls;
  30 inline sites put a tool call in an `Error`/`ErrorDetail` text; 5 are success notes. The table is used at **56**
  `GitErrorDetails.` reference sites (measured; the first draft said "about 45"). The proposal's "about 20 inline / about 15
  notes" is not what the source shows (Appendix A lists every site).
- **`IGitOperations` (`Tools.Basic/IGitOperations.cs`, 30 lines, public) returns the wire DTOs and must move with the engine**:
  the engine cannot implement an interface that lives in Tools.Basic. The tool fields are typed `IGitOperations`
  (`GitTools.cs:13`, `GitRemoteTools.cs:17`, both named `_gitImpl`) and each tool constructs
  `new GitImpl(workspaceManager, new NullLogger<GitImpl>())` (`GitTools.cs:21`, `GitRemoteTools.cs:24`); `GitImpl` is not
  DI-registered.
- **A public interface member cannot take an internal type (CS0051).** `IGitOperations` is public, so no member of it may mention
  the internal hint types. `TryGetGitRoot(out string error, string? repoPath = null)` is the one member whose error text
  carries a tool call (the `:1922` branch), and it has 6 callers (`GitTools.cs:119`, `GitRemoteTools.cs:90`, and 4 tests:
  `GitPathClassificationToolParityTests.cs:216`, `GitLogToolParityTests.cs:871`, `GitStatusToolParityTests.cs:413`,
  `GitToolsSmokeTests.cs:428`). The first draft's `out GitHint?` overload would not compile; see the Decision entry on
  `GitRootFailure`.
- **Common Git plumbing is 27 root files plus 3 in `GitWorktree/`** (30). The 26 files matched by name (`*Git*`, `Libgit2*`,
  `CliGit*`, `FallbackGit*`, plus `CasCommitResult.cs`, `InProcessCommitRoute.cs`, `RepoWorktrees.cs`, `ShortHashLengths.cs`) and
  `CommitMessageResult.cs` (internal record used only by `GitCommitMessage.cs`; found by searching the type, not the file name,
  so a name glob misses it); `GitWorktree/` holds `GitWorktreeManager.cs`, `IStepBranchStrategy.cs`, `IWorktreeStep.cs`.
  `LibGit2Sharp` is used by 11 production files (all in Common, `using LibGit2Sharp;` counted by search) and 6 test files
  in `Tests.Tools.Basic` (`GitCasCommitNativeTests`, `GitConfigOperationTests`, `GitConfigReadParityTests`,
  `GitHooksGlobalConfigTests`, `GitPatchParityHarness`, `GitReaderParityTests`).
- **Engines.Basic and Tools.Basic use Git internals**: `GitReaders.Default` at `DocTrailerEngine.cs:926`,
  `ResidualMentionScanner.cs:238`, `TestInstanceLauncher.cs:750`; the public `RepoWorktrees` at `BuildEngine.cs:228,260,306`,
  `TestRunEngine.cs:200,209` and, in Tools.Basic, `WorkspaceBuildTestImpl.cs:403,456,528`; `GitBackendWarmup.Shared` at
  `ServerStatusTools.cs:40`. `Server.Advanced` calls `GitBackendWarmup.Shared.Start()` (`ServiceRegistrationExtensionsBasic.cs:503`).
  All are consumers above Git (fine); the internal ones need `InternalsVisibleTo`. Non-Git Common files that use Git types are
  the seam's subject (Step 10): `PersistentWorkspaceManager.cs:1036`, `ServerBinaryStaleness.cs`, `NotFoundEvidence.cs`.
- **Only two path-keyed ratchet entries name Git files** (`Tests.Basic/BareFileWriteRatchetTests.cs`):
  `"RoslynSentinel.Common/InProcessCommitRoute.cs::AppendOutcome"` (`:33`) and
  `"RoslynSentinel.Tools.Basic/GitImpl.cs::StageHunksAsync"` (`:65`). The proposal's `:50-52` are the Area 2 entries. The
  ratchets that scan "every `RoslynSentinel.*` non-test directory" (`BareFileWrite`, `PathComparison`, `StaleReadPolicy`,
  `ToolHintRatchetTests`) pick a new project up by themselves; the ones with a project list or a name test need a look:
  `ErrorPolicyKindRatchetTests.ScannedProjects` (`:26`), `FirstMatchRatchetTests.IsProductionProject` (`:20`),
  `NewlineLiteralRatchetTests.IsInScopeProject` (`:61`), `WhitespaceRatchetTests.IsProductionProject` (`:174`),
  `StaleReadPolicyRatchetTests.IsProductionProject` (`:164`, excludes `RoslynSentinel.Common` by name).
- **`Tests.Tools.Basic` has `AssemblyInfo.cs` with `[assembly: Parallelizable(ParallelScope.Fixtures)]` and a
  `[SetUpFixture]` (`LeakDetectorSetUp.cs`, deriving `RoslynSentinel.Tests.WorkingDirectoryLeakDetector`)**; every test assembly has
  its own `LeakDetectorSetUp.cs` (verified in 9 test projects); a new test project needs its own.
- **Test support locations (verified by search):** `GitTestRepo` is `RoslynSentinel.Tests.Support.Basic/GitTestRepo.cs:13` (91 references in
  58 files, stays shared); `ScriptedReader` is an internal class nested in `GitReaderParityTests.cs:982` and is also used by
  `McpServerStatusGitBackendTests.cs` (4 sites), a fixture that may stay: Step 16 lifts it out first; `FakeWorkspaceManager` is
  `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs:14`.
- 42 files named `Git*.cs` in `Tests.Tools.Basic` carry `[Category("OutOfProcess")]` (measured; the proposal says 44 across
  the folder, Step 16 inventories).

## Decision

Dated entries; later entries do not override earlier ones unless they say so.

- **2026-11-24, scope (owner):** Area 1 (Git) only, proposal items 0-5. Areas 2 (Build/RunTest) and 3
  (PersistentWorkspaceManager) are out of scope; a later plan covers them.
- **2026-11-24, selection rule (owner):** `scripts/test-areas.json` and the selection rule are owned by
  [plan_full_suite_nightly_and_tail_shard.md](../plan_full_suite_nightly_and_tail_shard.md) (its Step 5 area list), not this plan.
  This plan's Step 21 only records, as a handoff note, that the Git area's watch list becomes the `RoslynSentinel.Git`
  project directory and `RoslynSentinel.Tests.Git`. That plan belongs to another session and is not edited here.
- **2026-11-24, open question 1 (owner):** `GitWorktree/` moves to the Git project; `Utilities.PlanStepRunner` and
  `Tests.Harness` gain references. (Consumers verified: `Tools.Advanced` `SubAgentChildServerLauncher.cs:91`,
  `SubAgentEvalImpl.cs:60`, `SubAgentRunNaming.cs:52`; `PlanStepRunner` `Program.cs:67`; `AgentLoop`
  `SubAgentEvalResult.cs:83` is a doc cref only. `PlanStepFile.cs`, `RunnerOptions.cs` and `WorktreeTeardownTests` are the
  proposal's claims, confirmed by compiling in Step 14.) Planner recommendation, recorded here so Step 9 needs no question:
  `GitWorktree/` moves to `Common/Git/GitWorktree/` first, so the one folder split of Step 14 takes it along.
- **2026-11-24, open question 3 (owner):** extract the tool-surface hint text from `GitImpl` first (proposal Area 1 item 0),
  text-neutral, guarded by pins. This plan adds the pins first (Step 2) because the existing ones do not cover Git text.
- **2026-11-24, open question 4 (owner), superseded in route by D-b below:** a spike comes first, in a throwaway worktree.
- **2026-11-24, open question 5 (owner):** whether Git code uses non-Git Common internals is settled by compiling in the spike
  (Step 12) and the move (Steps 13-14); Common gains `InternalsVisibleTo` to the Git project as the compiler requires.
- **2026-11-24, open question 2, seam style (owner): HYBRID.**
  - `PersistentWorkspaceManager` takes an optional constructor parameter for the repo probe: a public Common interface
    `IRepoProbe` (public because the constructor `PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger,
    IScopedOperationLedger? ledger = null, ActiveToolSurface? activeToolSurface = null)` at `:352` is public; `null` = no probe).
    Registered in DI beside `services.AddSingleton<PersistentWorkspaceManager>()`
    (`Engines.Basic/EngineServiceCollectionExtensionsBasic.cs:46`). The only test construction is the factory
    `Tests.Support.Basic/TestEngines.cs:53-55` (`ActivatorUtilities.CreateInstance<PersistentWorkspaceManager>(CreateProvider(), logger)`,
    about 585 uses). `CreateProvider` calls `AddRoslynSentinelEnginesBasic`, so the registered `IRepoProbe` reaches test hosts and the
    probe keeps starting in them: the seam is text-neutral and behaviour-neutral (the first draft wrongly said test hosts would stop
    starting the probe).
  - `ServerBinaryStaleness` (`Common/ServerBinaryStaleness.cs:31`) and `NotFoundEvidence` (`Common/NotFoundEvidence.cs:14`)
    are static classes (no constructor); they read an internal static `IRepoQuery` provider (`GetHeadSha`, `GetCommitsSince`,
    `GetStatus`, `ReadBlobAtHead`) installed at startup, like `GitReaders.Default` today. Git-dependent members:
    `ServerBinaryStaleness.cs:88, 239, 268` and `NotFoundEvidence.cs:469`.
  - Rejected for these three: constructor injection into the two static classes (impossible without making them instances) and
    DI-only for everything (the open blocker `blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md` makes
    constructor retyping costly).
- **2026-11-24, D-a, hint data types live in Common (owner):** `GitHintKey`, `GitHintFacts`, `GitHint` and the slot enum are internal
  types in a new file under `RoslynSentinel.Common/Git/` and travel with the plumbing into `RoslynSentinel.Git`. Common already
  has `InternalsVisibleTo` to Tools.Basic (`RoslynSentinel.Common.csproj:35`, verified), so `GitHintText` can use them from
  Tools.Basic; after the split the Git project gets the same IVT. `GitHintText` (the renderer, which knows tool names via
  `ToolCallHint`) stays in Tools.Basic. Why this over the first draft (types in Tools.Basic): the engine that moves later must set
  `GitResult.Hints`, so the data types must sit at or below the engine, never in the layer above it.
- **2026-11-24, D-b, move route: down, then one split (owner):** the engine does not cross a project edge file by file.
  (1) Split the DTO records out of `GitImpl.cs` into their own files (`MoveType`/`MoveAllTypesToFiles`, toolset `moveExtract`;
  Step 11). (2) `MoveFile` the DTO files, `IGitOperations`, `GitHunkParser` and `GitImpl` DOWN from Tools.Basic into
  `RoslynSentinel.Common/Git/` in dependency order, each call compile-green because hint text is already out of the engine
  (Step 13, after Step 8). (3) ONE `SplitProjectByFolder` (toolset `projectAdmin`) turns `Common/Git/` into `RoslynSentinel.Git`
  (Step 14). The spike (Step 12, throwaway worktree) verifies: (i) `GitImpl` and the DTOs use nothing from Engines.Basic or
  Tools.Basic (if they do, an owner-authorized bypass for that group plus a `TOOL-GAP:` for a multi-file `MoveFile`); (ii) how
  `SplitProjectByFolder` treats `ProjectReference`s, `InternalsVisibleTo` and the LibGit2Sharp package. The first draft's "accept one red
  intermediate" engine move (old Steps 12/13) is dropped. Alternative recorded: `MoveFile` cross-project per file straight
  to a pre-created project (the first draft); it lost because the DTO file and `IGitOperations` cannot move in one green call.
- **2026-11-24, D-c, hint slots are data only, fixed in the Step 1 contract (owner):** five slots, `ErrorDetail` (sets
  `ErrorDetail` from a key: a whole tool-side sentence), `ErrorReplace` (`Error := Render`, engine `Error` empty),
  `NotePrefix` (a whole tool-side sentence before the engine `Note`), `NoteSuffix` (after it) and `Inline` (a placeholder
  token `[[hint:<Key>]]`, built by a helper in the contract, replaced by the rendered call fragment inside engine `Error`/
  `ErrorDetail`/`Note` text; the surrounding prose stays in the engine). They cover `StashRestoreAsync` (`:1196`: prefix),
  `StashDiskNote` (`:1363`), `WorktreeRemoveAsync` (`:1828-1852`: inline fragments) and the tag-delete note
  (`Deleted locally only. To undo: {call}.`: inline). One token per key per result: a text that needs two fragments of the same key
  with different facts gets a second key (checked per slice). Alternatives recorded: one enum per result kind (about 10 enums, the
  shared table keys duplicate); an engine-held format string with placeholders (puts text back in the engine); a per-call
  `Func<...>` renderer injected into the engine (the engine then holds tool-layer text through a delegate and cannot be tested
  without it). The first draft's `ErrorAppend` slot is gone: an appended call becomes an `Inline` token at the end of the engine
  text, so the joining space stays engine text.
- **2026-11-24, `TryGetGitRoot` (planner, follows D-a/CS0051):** a public enum `GitRootFailure` (in `Common/Git/`, new file) names the
  failure kinds; the engine returns the factual part in `error` and the kind in a new `out GitRootFailure failure`
  overload; the tool appends the advice that names `LoadSolution` via `GitHintText`. The old overload is
  `[Obsolete("Use the GitRootFailure overload")]` until Step 8. Alternative: return a small result record (cleaner, changes the
  interface shape more, same 6 callers).
- **2026-11-24, D-d, namespaces and names (owner):** the moves keep namespaces (`namespaceHandling: keep`; zero `using` ripple while
  files travel). A final rename step (Step 19, after the test move and before the stub sweep) renames the plumbing and the engine to
  namespace `RoslynSentinel.Git` and `GitImpl` to `GitEngine`. This replaces the first draft's recommendation to keep
  `RoslynSentinel.Tools.Basic` for the moved types with a `// TODO`. No dedicated namespace-change tool was found (candidates seen:
  `RenameSymbol`, `SyncTypeAndFilename`, `MoveFile` with `copyAndObsolete`); Step 19 tries them first and reports `TOOL-GAP:`.
- **2026-11-24, D-e, owner is AWAY during the run (owner):** a mid-run decision is not asked; the dispatcher defers it, records the
  question with a recommendation in the step report and `docs/current/INBOX.md`, and continues with any slice that does not depend on
  it. Steps whose outcome could need a decision name the default taken: Step 12 (spike route: default is the tool route if the spike
  reports "works"; bypass only if the owner pre-authorized it, otherwise stop that group and continue Lane A), Step 19 (rename: default is
  to skip a text-replace sweep and leave `// TODO` plus an INBOX line).
- **2026-11-24, D-f, no hold for the nightly plan (owner):** this plan takes priority over
  `plan_full_suite_nightly_and_tail_shard.md`. Steps 9 and 17 send its owner session a heads-up and do not wait for a reply. Step 23
  compares against a fresh full run taken just before the first move step (Step 9a) and also quotes `48fb08e2`.

## Execution rules

- C# reads and writes through the RoslynSentinel MCP tools only; `.csproj`, `.slnx`, `.md`, `.json` with Edit/Write (CLAUDE.md,
  Dog-fooding). One `Build` or `RunTest` at a time, subagents included (shared `bin/obj`). `Build` means default level
  (`fullBuildThrowaway`), 0 errors. Implementers never run tests; the dispatcher runs the named tests.
- Slices are sized to the current tiers in `.claude/implementer-tiers.json`: depth tier 1 (`implementer`: at most 3 files, 15 edits,
  25-35 tool calls) and sweep tier 0 (`implementer-sweep`: 1 operation, 8 files, 25 call sites, 2 projects). A batched
  `batchEdits`/`ReplaceSnippet` call counts as one edit call; sweeps over more than 25 sites are split. A slice over those limits
  goes to `implementer-senior` and says so.
- Gated tools are named with their toolset: `MoveType`, `MoveAllTypesToFiles` need `McpToolsetControl(toolSet: moveExtract, enabled:
  true)`; `SplitProjectByFolder`, `CreateProject` need `projectAdmin`; `MoveFile` is not gated. (`MoveMember` is a Core tool, not part
  of `moveExtract`, contrary to the coordinator's list; `ToolsetCatalog.cs` was read for this.)
- New DTOs follow the `dto-conventions` skill (`.claude/skills/dto-conventions/SKILL.md`): reuse first, a `NEW-DTO:` block in the
  brief, report, commit message and the creating tool call's `reason`, and every new `[JsonIgnore]` field is stated in its step. A
  type moved verbatim is a new type to the hook: its slice carries a `DTO-EXEMPT:` block (verbatim move, no property or attribute
  change) in the same four places.
- After Step 2 the pin classes (`GitHintTableGoldenTests`, `GitHintInlineGolden*Tests`, `GitHintNoteGoldenTests`,
  `GitHintSinkGoldenTests`) must stay green after every Lane A slice; the dispatcher runs them (cheap, filtered), not the full suite.
  A pin that changes is a text change and stops the slice.
- Move slices (9, 11, 13, 14, 17, 18) touch files other sessions edit: `HOLD`/`RELEASE` coordination, a clean tree, and the
  `peer-coordination` skill first. The nightly plan is not a reason to wait (D-f).
- Each slice commits on its own compile-green point with `Plan: plan_isolate_git_area_into_own_project`; the last step carries `Closes:`.
- A tool gap hit during a move is reported as `TOOL-GAP:` (task, tool, measured sites/files/projects, suggested change); do not
  brute-force. A bypass for one group needs the owner's authorization, which is given in advance only where a step says so (D-e).
- Test-coverage role boundaries for this plan: the planner (this plan) defines what must be pinned (Step 2 and the "Done when" of
  each move); the reviewer confirms the pins cover each Appendix A site (or that the site is listed as renderer-pinned only);
  an implementer's green build and passing named tests show only that the written pins hold, not that the pins were enough.
  Fixes to existing tests that a slice breaks (a moved helper, an obsolete overload) are folded into that same slice, never left to a
  later "fix tests" step.

## Steps

### Lane map (two implementing agents at most)

- **Single lane first:** Steps 1a-1b (contract skeleton). Nothing forks before the contract is in: the slots (D-c), the `Hints` property,
  `GitRootFailure` and the decorator signature are fixed there.
- **Lane A (hint text, serial, `GitImpl.cs` + `GitHintText.cs`):** Steps 3, 4, 5a-5d, 6a-6i, 7a-7b, 8, then 11a-11d (DTO split, same file,
  after Step 8). Lane A-tests (`test-writer`): Step 2, new files only, each pin slice before the Lane A slice it guards.
- **Lane B (Common files, build files, seam, serial):** Steps 9, 10a-10d. Files: `Common/*`, `Engines.Basic` DI file,
  `.csproj`/`.slnx`, `Tests.Basic/BareFileWriteRatchetTests.cs`. Disjoint from Lane A except `Tests.Tools.Basic` (Step 10d adds
  setup classes; Step 2 adds new files only).
- **Paired slices (disjoint files, safe in parallel):** 9b-9e with any Lane A slice; 10a-10c with any Lane A slice; Step 2 with 1a-1b.
- **Join:** Step 12 (spike) needs Step 11 (Lane A) and Step 9e (Lane B); Step 13 needs Steps 8, 10c and 12; Steps 14-23 are serial.
- Text-neutral on the wire: every slice. There is no wire-changing slice, so no baseline hold applies; the fresh baseline of Step 9a
  is a measurement, not a hold.

### Step 1a - Contract skeleton, Common types (single lane)

- Done: 7aa47fc6
- Files: `RoslynSentinel.Common/Git/GitHintTypes.cs` (new), `RoslynSentinel.Common/Git/GitRootFailure.cs` (new)
- Depends on: none. Effect: text-neutral (new types, nothing uses them).
- Change: one `CreateFile` each, definitions only:
  - `GitHintTypes.cs` (namespace `RoslynSentinel.Common`, all `internal`): `enum GitHintKey` with `None = 0` plus the 23 members named
    exactly as the `GitErrorDetails` members (`PathNotFound`, `IgnoredPath`, `NothingToCommit`, `OperationInProgress`,
    `RefRequiredReset`, `RefRequiredRevert`, `RefRequiredCheckout`, `RefRequiredStash`, `RefRequiredTag`, `RefRequiredWorktree`,
    `ActionRequired`, `AlreadyExists`, `TargetNotFound`, `InvalidName`, `WorkingTreeDirty`, `NoChanges`, `StashConflict`, `Refused`,
    `HunkUnsupported`, `HunkSelection`, `IndexLocked`, `HookRejected`, `HunkStale`); per-site members are added by the slice that moves
    the site; `enum GitHintSlot { None = 0, ErrorDetail, ErrorReplace, NotePrefix, NoteSuffix, Inline }`; `sealed record GitHintFacts` with
    optional init properties `Name`, `Hash`, `Path`, `Branch`, `Operation`, `Action`, `LockPath`, `SpanText` (string?) and `Count`,
    `CountBefore` (int?); `sealed record GitHint(GitHintKey Key, GitHintFacts? Facts = null)`; `static class GitHintTokens` with
    `string Inline(GitHintKey key)` (returns `"[[hint:" + key + "]]"`) and `bool TryRead(string text, int start, out GitHintKey key, out int
    length)` (the scanner `Apply` uses).
  - `GitRootFailure.cs` (`public enum GitRootFailure`): `None = 0` plus the failure kinds of the `TryGetGitRoot` branches; the member list is
    read from the method body (about `:1861-1922`) by the implementer and reported; every member's name states the cause, not the advice.
  - NEW-DTO blocks (verbatim in brief, report, commit and `reason`): `NEW-DTO: GitHintFacts (Item, Common.Git) / Evaluated: GitResult - holds
    rendered Error/ErrorDetail/Note strings, not raw facts; GitStashEntry - a stash row, not call facts / Wire impact: none`;
    `NEW-DTO: GitHint (Item, Common.Git) / Evaluated: GitResult.ErrorKind - a code string that cannot carry facts; GitErrorCodes - constants,
    not a pair of key and facts / Wire impact: none`; `NEW-DTO: GitRootFailure (enum, Common.Git) / Evaluated: GitErrorCodes.Fallback and
    the other code strings - codes of a GitResult, not the cause of a pre-result root lookup / Wire impact: none`.
- Done when: `Build` 0 errors. Lane: `implementer` (2 files).

### Step 1b - Contract skeleton, Tools.Basic stubs and `Hints`

- Done: a72cc4b2
- Files: `RoslynSentinel.Tools.Basic/GitHintText.cs` (new), `RoslynSentinel.Tools.Basic/GitHintApplyingOperations.cs` (new),
  `RoslynSentinel.Tools.Basic/GitImpl.cs` (`GitResult`, line 11)
- Depends on: 1a. Effect: text-neutral.
- Change: one `CreateFile` per new file, one `batchEdits` call on `GitImpl.cs`, definitions before users:
  - `GitHintText.cs`: `internal static class GitHintText` with `GitHintSlot SlotOf(GitHintKey key)`, `string Render(GitHintKey key,
    GitHintFacts? facts)`, `T Apply<T>(T result) where T : GitResult` and `string RenderRootFailure(GitRootFailure failure)`; each body
    `throw new NotImplementedException(); // TODO implement (Step 3)`.
  - `GitImpl.cs`: add to `GitResult` a `[JsonIgnore] internal IReadOnlyList<GitHint>? Hints { get; set; }` (new `[JsonIgnore]` field,
    `dto-conventions` section 5: internal, never serialized, null on every existing path; stated here as the step's one new
    `[JsonIgnore]` field).
  - `GitHintApplyingOperations.cs`: `internal sealed class GitHintApplyingOperations(IGitOperations inner) : IGitOperations`, every
    member forwards to `inner`; for the `Task<T>` members it awaits and returns `GitHintText.Apply(result)`; `TryGetGitRoot` forwards
    unchanged (the tool renders root failures itself, Step 6i). Fully implemented (not a stub), constructed nowhere yet.
- Done when: `Build` 0 errors; `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Tools.Basic/GitHintText.cs")`
  finds exactly the four stubs. Lane: `implementer` (3 files).

### Step 2 - Characterization pins for the hint text (golden full-sentence, tool-level, before any extraction)

- Done: 8794397e, 8e163046, bbc2dedb, 353d55f2, 72fbb84f, 87d88842, 56d5bc9b, 9e136919, dcc429dd
Runs against the current source; independent of Steps 1a-1b (pairs with them). The pins call the tool classes (`GitTools`, `GitRemoteTools`,
built as in `GitToolsSmokeTests`), not `GitImpl`, because after Step 3 the text is applied at the tool boundary and a pin on the engine
would fail for a legitimate reason. Each pin asserts the full `ErrorData.Message`, `ErrorData.Detail` (the rendered `ErrorDetail`) and,
for notes, `SuccessData.Note`, as `Is.EqualTo` against a full-sentence literal captured verbatim from the pre-change build (run once at the
pre-change commit and paste; the literal is the pin; a `Does.Contain` fragment is not a pin). One scenario per Appendix A site that a
`GitTestRepo` scenario can reach; a site with no cheap scenario is listed in the slice's report as "renderer-pinned only" (Steps 5-7 pin it
by a literal in a unit test of `GitHintText.Render`, taken from the original source line before the move).
- 2a - table texts: `RoslynSentinel.Tests.Tools.Basic/GitHintTableGoldenTests.cs` (new). All 23 `GitErrorDetails` members through the
  failures that produce them (tag, stash, worktree, hunks, commit). Pins Steps 5a-5d and 8.
- 2b - inline texts, tag/stash/config/worktree: `GitHintInlineGoldenTagStashTests.cs`, `GitHintInlineGoldenWorktreeTests.cs` (new).
  Appendix A kinds B1, B2. Pins Steps 6a-6d.
- 2c - inline texts, hunks/stage/commit/checkout/in-progress/root: `GitHintInlineGoldenHunkStageTests.cs`,
  `GitHintInlineGoldenCommitMiscTests.cs` (new). Appendix A kinds B3, B4. Pins Steps 6e-6i.
- 2d - notes: `GitHintNoteGoldenTests.cs` (new). Appendix A kind C (5 sites, including both users of `StashDiskNote`). Pins Steps 7a-7b.
- 2e - pass-through sinks: `GitHintSinkGoldenTests.cs` (new). One scenario per sink of Step 4 that makes a hint-bearing failure travel
  through it: an in-progress advice through `AppendInProgressAsync`, an index-entry failure through `StageHunksAsync`, a hunks load failure
  through `HunksAsync`, a failed stage through `CommitAsync` (copies `stageResult.Error`). Pins Step 4.
- 2f - coverage check: the reviewer (not the test-writer) lists, per Appendix A row and per sink, the pin test or "renderer-pinned only",
  and signs it in the commit message of 2e. A row with neither blocks the extraction slice for that row.
- Depends on: none. Pairs with: Steps 1a-1b.
- Effect: text-neutral (new test files only).
- Done when: the new classes pass at the pre-change commit (dispatcher runs `RunTest` with filter `FullyQualifiedName~GitHint` and
  `suite: full`; they start git processes, so they carry `[Category("OutOfProcess")]` like their neighbours). Outcome classes: pinned by
  scenario; renderer-pinned only (named); unreachable (named, blocks that row). Lane: `test-writer`, one slice per bullet; split a
  bullet that would exceed 15 scenarios.

### Step 3 - Implement `Render`, `SlotOf`, `Apply`, `RenderRootFailure` and wire the decorator

- Done: 26b6ca66
- Files: `GitHintText.cs`, `GitTools.cs` (`:21`), `GitRemoteTools.cs` (`:24`)
- Depends on: Step 1b. Effect: text-neutral (no engine path sets a key yet, so `Apply` changes nothing).
- Change:
  - `SlotOf`: the 23 table keys map to `ErrorDetail`. `Render`: for the 23 table keys return the same text as the matching
    `GitErrorDetails` member (a one-line delegation per case, `GitHintKey.X => GitErrorDetails.X`; `IndexLocked` uses
    `facts.LockPath`); the text stays in `GitErrorDetails` until Step 8.
  - `Apply<T>`, per hint in `result.Hints` (list order) by `SlotOf(key)`: `ErrorDetail` sets `ErrorDetail`; `ErrorReplace` sets `Error`;
    `NotePrefix` sets `Note` to the rendered sentence, then the engine note, joined by the single space the original string used (each
    slice copies the original joint exactly); `NoteSuffix` the reverse; `Inline` replaces every `GitHintTokens` token in `Error`,
    `ErrorDetail` and `Note` by `Render(key, facts of the first hint with that key)`. Null or empty `Hints` returns the result
    untouched. The seven result types with `Note` (`GitCheckoutResult:331`, `GitTagResult:392`, `GitConfigResult:420`,
    `GitStashResult:475`, `GitHunksResult:558`, `GitStageHunksResult:578`, `GitWorktreeResult:604`) are handled by a type switch.
  - A unit test file `GitHintApplyTests.cs` (new, Tests.Tools.Basic) asserts: every key with slot `Inline` leaves no token behind after
    `Apply`; each slot's join on a literal result; null `Hints` is identity.
  - `GitTools.cs:21` and `GitRemoteTools.cs:24` construct `new GitHintApplyingOperations(new GitImpl(...))` (the fields are already typed
    `IGitOperations`).
- Done when: `Build` 0 errors; the four Step 1b stubs gone from `GitHintText.cs`; dispatcher: the Step 2 pins, `GitHintApplyTests` and
  `GitToolsSmokeTests` pass. Lane: `implementer` (4 files counting the test: use `implementer-senior` or split the test file to a
  `test-writer` slice).

### Step 4 - Pass-through sinks carry `Hints`

- Done: c08a0b4f
A hint set on one result is lost when a method copies `Error`/`ErrorDetail` into another result without it, which would silently change
text once sites move. The sinks found by reading: `AppendInProgressAsync` (`GitImpl.cs:2614-2625`), `StageHunksAsync` (`:2436-2475`,
the copies near `:2450` and `:2454`), `HunksAsync` (`:3170`) and `CommitAsync` (`:3743`, copies `stageResult.Error`).
- Files: `GitImpl.cs`. Depends on: Steps 2e, 3. Effect: text-neutral (nothing sets a hint yet; the copies are no-ops).
- Change: each sink copies `Hints` together with `Error`/`ErrorDetail` (a small private helper `CopyFailure(from, to)` is allowed). The
  implementer also searches `GitImpl.cs` for every other assignment that copies an `Error` from another result
  (`Search(mode: text, query: "\\.Error\\b", fileGlob: "RoslynSentinel.Tools.Basic/GitImpl.cs")`) and lists each in the report as sink or
  not; an unlisted sink found later is a planner gap, not an implementer failure.
- Done when: `Build` 0 errors; dispatcher: `GitHintSinkGoldenTests` and `GitToolsSmokeTests` pass. Lane: `implementer` (1 file).

### Step 5 - Move the table sites onto keys (migration-first, Appendix A kind A, 56 references)

- Done: 8c1294dc
Each helper that takes a `string detail` gets a new overload taking `GitHintKey key` (and optional `GitHintFacts`); the string overload is
marked `[Obsolete("Use the GitHintKey overload")]`, and the call sites move in slices (CS0618 warnings in between are expected). The key
overload sets `Hints = [new GitHint(key, facts)]` and leaves `ErrorDetail` null. Helpers: `ActionRefused` (`:687`), `TagFailure`
(`:~719`), `ConfigFailure` (`:~934`), `StashFailure` (`:~1161`), `WorktreeFailure` (`:~1497`), the `FileDiffLoad` record (`:~2101`, its
`ErrorDetail` field) and the `SelectHunks` tuple (`:~2242-2258`). Blast radius: the 56 `GitErrorDetails.` references (`Search(mode: text,
query: "GitErrorDetails\\.", ...)` measured before the slice; sites seen at `:1017`-`:1349` (tag/config/stash), `:1627`-`:1778`
(worktree), `:2096`-`:2282` (hunks), `:2445`-`:3730` (stage, commit, index lock, `??=` at `:2622`, `IndexLocked(...)` at `:3730`)).
- Effect: text-neutral (the tool boundary renders the same sentence). Depends on: Steps 2a, 3, 4 and the previous 5x slice (same file).
- 5a - tag/config/stash region (`:1017`-`:1349`, 14 seen) plus the `TagFailure`/`ConfigFailure`/`StashFailure` overloads. Lane:
  `implementer-sweep` (one operation: the `ReplaceSnippet` batch `GitErrorDetails.X` -> `GitHintKey.X`, anchored on the call's preceding
  line; at most 25 sites). Done when: `Build` 0 errors; the slice's sites no longer reference `GitErrorDetails`; dispatcher:
  `GitHintTableGoldenTests` pass.
- 5b - worktree region (`:1627`-`:1778`, 10 seen) plus `WorktreeFailure`. Same shape and limits.
- 5c - hunks region (`:2096`-`:2282`, 12 seen) plus `FileDiffLoad` and `SelectHunks`. Same shape and limits.
- 5d - stage/commit/index-lock region (`:2445`-`:3730`) plus `ActionRefused`; the `IndexLocked(lockPath)` method call becomes
  `GitHintKey.IndexLocked` with `Facts.LockPath`. Same shape and limits.
- If a region measures more than 25 sites, split it by the next line boundary into `5x1`/`5x2`; the group's count of moved sites must total 56.
- Done when (group): `Search(mode: text, query: "GitErrorDetails\\.", fileGlob: "RoslynSentinel.Tools.Basic/GitImpl.cs")` finds only the class
  declaration (`:112`) and the `[Obsolete]` string overloads' internals.

### Step 6 - Move the inline error texts onto keys (Appendix A kind B)

- Done: 74cab4b0 (6a-6d), 30bd3ebf (6e-6h), dec947e8 (6i)
Per site: add the enum member (in `Common/Git/GitHintTypes.cs`), the `SlotOf` case, the `Render` case (the string copied verbatim, facts read
from `GitHintFacts`, tool calls via the same `ToolCallHint.Call(...)` expression), and change the engine site to set the hint plus its facts.
Slot per Appendix A (planner's reading, confirmed by the slice): a whole detail sentence is `ErrorDetail`; a tool call in the middle of
engine `Error`/`ErrorDetail` text becomes an `Inline` token with the prose left in the engine (`CheckoutAsync:4496`,
`LoadFileDiffAsync:2172`, `WorktreeAddAsync:1732`, `WorktreeRemoveAsync:1816/:1836`); a whole `Error` that is only the tool-side message is
`ErrorReplace`. Each slice at most 4 sites, files `GitImpl.cs`, `GitHintText.cs` and `GitHintTypes.cs` (3 files); a slice whose rows are
"renderer-pinned only" (Step 2f) adds a unit test in a new per-slice file, so it is `implementer-senior` or pairs with a `test-writer`.
Effect: text-neutral. Lane: `implementer`. Depends on: the Step 2 slice that pins the rows (named), Steps 3, 4 and the previous 6x slice.
- 6a - `ActionRefused:694`, `ConfigAsync:770`, `ConfigWriteAsync:840`, `TagCreateAsync:1038` (facts `Operation`, `Name`, `Hash`). Pin: 2b.
- 6b - `StashRestoreAsync:1200, :1235`, `StashRollbackConflictAsync:1278, :1279` (facts `Count`, `CountBefore`). Pin: 2b.
- 6c - `WorktreeAddAsync:1684, :1732`, `WorktreeRemoveAsync:1783`. Pin: 2b.
- 6d - `WorktreeRemoveAsync:1787, :1816, :1836`. Pin: 2b.
- 6e - `NoUnstagedChangesLoad:2095`, `LoadDiffForPathAsync:2114`, `LoadFileDiffAsync:2172`, `SelectHunks:2242`. Pin: 2c.
- 6f - `SelectHunks:2249, :2274, :2275`. Pin: 2c.
- 6g - `IndexEntryChangedMessageAsync:2372, :2373`, `BuildStageHunksResultAsync:2400`, `StageHunksAsync:2471`. Pin: 2c, 2e.
- 6h - `InProgressAdviceAsync:2604, :2605`, `CommitAsync:3810`, `CheckoutAsync:4496`. Pin: 2c, 2e.
- 6i - `TryGetGitRoot:1922` (the one `out string error` member). Files: `GitImpl.cs`, `IGitOperations.cs`, `GitHintApplyingOperations.cs`,
  `GitTools.cs`, `GitRemoteTools.cs`, `GitHintText.cs` (6 files, so `implementer-senior`). Change: add `TryGetGitRoot(out string error, out
  GitRootFailure failure, string? repoPath = null)` beside the old overload (old marked `[Obsolete("Use the GitRootFailure overload")]`;
  the engine's `error` keeps only the fact text; the `// hint-ok:` branches that name the LoadSolution tool class are re-read and the
  advice moves into `GitHintText.RenderRootFailure`); the two production callers (`GitTools.cs:119`, `GitRemoteTools.cs:90`) switch and
  append the rendered advice; `GitHintApplyingOperations` forwards the new overload. The 4 test callers (listed in Problem) keep the old
  overload until Step 8; this slice does not touch them. Pin: 2c.
- Done when (each slice): `Build` 0 errors; dispatcher: its Step 2 pin class passes unchanged. (Group): `Search(mode: text, query:
  "ToolCallHint", fileGlob: "RoslynSentinel.Tools.Basic/GitImpl.cs")` lists only kind C sites (Step 7).

### Step 7 - Move the success notes onto keys (Appendix A kind C)

- Done: 97efb12b (also: `Apply` replaces Inline tokens in `GitStageHunksResult.Warning`)
- Files: `GitImpl.cs`, `GitHintText.cs`, `GitHintTypes.cs` (3 files per slice). Depends on: Step 2d, Step 6h (same files), Step 3.
- Effect: text-neutral.
- Change: the engine sets a hint plus facts instead of the call text; the tool boundary applies it.
  - 7a - `TagDeleteAsync:1113` (`Deleted locally only. To undo: {call}.`: an `Inline` token in the engine note, facts `Name`, `Hash`; the
    separate annotated sentence stays engine text), `StashDiskNote:1363` (a `NotePrefix` key: its sentence is rendered before the engine's
    fixed second sentence appended by `StashRestoreAsync:1196-1200` and `StashPushAsync:1358`; copy the original joint exactly),
    `WorktreeAddAsync:1742` (fact `Path`).
  - 7b - `WorktreeRemoveAsync:1842/:1850` (`Inline` token in the `kept` note, fact `Branch`), `BuildHunksResult:2209` (fact `Path`).
  - A note that names no other tool (for example `ConfigWriteAsync:859, :872, :892, :894`) stays an engine fact string.
- Done when: `Build` 0 errors; dispatcher: `GitHintNoteGoldenTests` and `GitToolsSmokeTests` (including
  `Git_Stash_Push_Result_NoteMentionsLoadSolutionAsync`) pass. Lane: `implementer` per slice.

### Step 8 - Retire the legacy path; the engine names no tools

- Done: 56fc3966, ae02572d (8b: slot mapping moved to `GitHintKeySlots` in Common; `ActionRequired` deleted, no producer)
- Files: `GitImpl.cs`, `GitHintText.cs`, and the four test callers of the old `TryGetGitRoot` (`GitPathClassificationToolParityTests.cs`,
  `GitLogToolParityTests.cs`, `GitStatusToolParityTests.cs`, `GitToolsSmokeTests.cs`): 6 files, so `implementer-senior`
- Depends on: Steps 5d, 6i, 7b. Effect: text-neutral.
- Change: delete the `[Obsolete]` string overloads and the obsolete `TryGetGitRoot(out string error, string?)`; migrate the four test
  callers to the `GitRootFailure` overload in the same slice (folded, not deferred); `MoveType GitErrorDetails` from `GitImpl.cs` into
  `GitHintText.cs` (same project, toolset `moveExtract`), keeping its members as the text home `Render` delegates to.
- Done when: `Build` 0 errors with no CS0618 left; `Search(mode: text, query: "ToolCallHint|GitErrorDetails", fileGlob:
  "RoslynSentinel.Tools.Basic/GitImpl.cs")` returns nothing; dispatcher: all Step 2 pin classes, `GitToolsSmokeTests`,
  `ToolHintRatchetTests`, `ToolsBasicResultStatusTests` pass.

### Step 9 - Consolidate the Common Git files into `RoslynSentinel.Common/Git/` (same project; Lane B)

- Done: 7c6d922f, f99fe1f9, 7c6c4fda, 2efbd2b4, 0bc6e864 (9a baseline = full run at 042035a6)
Depends on: Step 1a (the folder exists). Pairs with any Lane A slice. Effect: text-neutral (namespaces kept, `namespaceHandling: keep`; zero
`using` ripple).
- 9a - baseline and heads-up (dispatcher): (i) a clean-tree full run (`scripts/Test-Parallel.ps1`, idle machine), recorded with the commit hash;
  per-project wall, summed seconds, counts and failure set; this is the baseline Step 23 compares with, together with `48fb08e2`.
  (ii) `SendMessage` to the session that owns `plan_full_suite_nightly_and_tail_shard.md` (find it with `ListAgents`): its Step 5 globs
  (`RoslynSentinel.Common/Git*.cs`, `Libgit2*.cs`, `CliGit*.cs`, `FallbackGit*.cs`, `RepoWorktrees*.cs`, `ShortHashLengths*.cs`) stop matching
  after Step 9, and the Git production project and test project are coming (heads-up only; no wait, D-f). Lane: dispatcher.
- 9b - re-inventory: `Search(mode: text, query: "GitProcess|IGitReader|LibGit2Sharp|GitReaders|FallbackGitReader|CliGitReader|Libgit2GitReader|
  InProcessCommitRoute|ShortHashLengths|GitHooks|GitCommitMessage|CommitMessageResult|CasCommit|RepoWorktrees", fileGlob:
  "RoslynSentinel.Common/**/*.cs")` and write the final list into Appendix B (dispatcher edit). Expected: 27 root files (the 26 by name and
  `CommitMessageResult.cs`) plus 3 `GitWorktree/` files. Also list every Common file that is not in that set but references a moved type
  (expected: `PersistentWorkspaceManager.cs`, `ServerBinaryStaleness.cs`, `NotFoundEvidence.cs`); each is "behind the seam" (Step 10) or
  "moving". Lane: `explore-tier1`; no code change.
- 9c-9f - `MoveFile(sourcePath: RoslynSentinel.Common/<file>.cs, destinationPath: RoslynSentinel.Common/Git/<file>.cs, namespaceHandling: keep)`,
  at most 8 files per slice (30 files: 8, 8, 8, 6), `GitWorktree/` files to `RoslynSentinel.Common/Git/GitWorktree/` (Decision, open
  question 1). Each call is compile-green (same project). `InProcessCommitRoute.cs` is in the slice that also edits
  `Tests.Basic/BareFileWriteRatchetTests.cs:33` to key `"RoslynSentinel.Common/Git/InProcessCommitRoute.cs::AppendOutcome"` (the call and the key
  edit in one commit; the dispatcher runs `BareFileWriteRatchetTests`). Lane: `implementer-sweep` (1 operation, at most 8 files, 1 project).
- Done when (9f, group): `Glob RoslynSentinel.Common/*[Gg]it*.cs` returns nothing, `CommitMessageResult.cs` is under `Common/Git/`,
  `Common/Git/` holds 27 plumbing files plus the 3 under `GitWorktree/` plus the Step 1a files, and Appendix B lists every non-Git Common
  referencer as moving or behind the seam (none unclassified).

### Step 10 - The Common-to-Git seam (hybrid, per the Decision of 2026-11-24; Lane B)

- Done: 3eeaddae, 181e59e3, 0694275c, 906e0e8b (10d: `[OneTimeSetUp]` in Tests.Tools.Basic `LeakDetectorSetUp`)
Depends on: Step 9f (the moved files are in one folder; the seam files stay outside it, in the Common root).
- 10a - Contract skeleton and record move. Files: `RoslynSentinel.Common/RepoQuery.cs` (new), `Common/Git/GitReader.cs` (the two records).
  Effect: text-neutral.
  - `RepoQuery.cs` (one `CreateFile`): `internal interface IRepoQuery` with exactly the four members as declared today on `IGitReader`:
    `string? GetHeadSha(string workingDirectory, bool shortForm, TimeSpan cliTimeout)`,
    `IReadOnlyList<ChangedCommit>? GetCommitsSince(string workingDirectory, string sha, TimeSpan cliTimeout)`,
    `IReadOnlyList<WorkingTreeChange>? GetStatus(string workingDirectory, IReadOnlyList<string>? pathspecs, TimeSpan cliTimeout)`,
    `string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout)`;
    `internal static class RepoQueryProvider { internal static IRepoQuery? Current { get; set; } }` (null = git cannot answer, the same
    answer the readers already give for "not a repository"; comment the process-wide static and that a test that sets it restores it);
    `public interface IRepoProbe { void StartRepoProbe(string solutionDirectory); }` (public because the `PersistentWorkspaceManager`
    constructor is public and takes it; no internal type in its signature).
  - Move `WorkingTreeChange` and `ChangedCommit` (`GitReader.cs:9, :16`, internal sealed records) into `RepoQuery.cs` (used by
    `ServerBinaryStaleness.cs:324, :385` and returned by the interface); `IGitReader` becomes `IGitReader : IRepoQuery` and drops its four
    duplicate declarations (`FallbackGitReader`, `CliGitReader`, `Libgit2GitReader` already match the signatures). `GitBackendWarmup`
    (`Common/Git/GitBackendWarmup.cs`) implements `IRepoProbe` (its public `StartRepoProbe(string)` at `:83` already matches).
  - DTO-EXEMPT block for the two moved records (verbatim move). Done when: `Build` 0 errors. Lane: `implementer` (2-3 files).
- 10b - Consumers. Files: `ServerBinaryStaleness.cs` (`:88, :239, :268`: `GitReaders.Default` -> `RepoQueryProvider.Current`, a null provider takes the
  existing "git unavailable" branch of each), `NotFoundEvidence.cs` (`:469`, same). Depends on: 10a. Effect: text-neutral while the provider is
  installed (10c). Lane: `implementer` (2 files). Done when: `Build` 0 errors; `Search(mode: text, query: "GitReaders|GitProcess|IGitReader",
  fileGlob: "RoslynSentinel.Common/{ServerBinaryStaleness,NotFoundEvidence}.cs")` returns nothing.
- 10c - Probe parameter and installation. Files: `PersistentWorkspaceManager.cs` (constructor `:352`, call `:1036`),
  `Engines.Basic/EngineServiceCollectionExtensionsBasic.cs` (beside `:46`). Depends on: 10a, 10b. Effect: text-neutral (production and test hosts
  both register `IRepoProbe`, so the probe starts exactly as before). Lane: `implementer` (2 files).
  - Constructor becomes `PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger, IScopedOperationLedger? ledger = null, ActiveToolSurface?
    activeToolSurface = null, IRepoProbe? repoProbe = null)`; `:1036` becomes `_repoProbe?.StartRepoProbe(solutionDirectory);`.
  - `AddRoslynSentinelEnginesBasic` registers `services.AddSingleton<IRepoProbe>(_ => GitBackendWarmup.Shared)` and installs
    `RepoQueryProvider.Current = GitReaders.Default` (Risks 4).
  - Done when: `Build` 0 errors; `Search(mode: references, query: "PersistentWorkspaceManager", ...)` shows no direct `new
    PersistentWorkspaceManager(` outside `TestEngines.cs:55` and DI (the owner measured this; confirm); dispatcher: `RunTest` with filter
    `FullyQualifiedName~ServerBinaryStaleness|NotFoundEvidence|McpServerStatusGitBackend|ShortHashLengths|TestInstanceLauncherGitStamp`
    (`suite: full`) with the same results as before.
- 10d - Test installs for unit tests that never build a provider. Files: one `[SetUpFixture]` (or one `[OneTimeSetUp]` in the existing
  `LeakDetectorSetUp.cs`) per test assembly that calls `ServerBinaryStaleness`/`NotFoundEvidence`/`RepoQueryProvider` without DI; the assemblies
  are measured by `Search(mode: references, query: "ServerBinaryStaleness", includeTests: true)` and named in the report; plus one shared
  install helper in `Tests.Support.Basic`. A per-assembly `[SetUpFixture]`, not a `[ModuleInitializer]` (a module initializer runs at assembly
  load, before NUnit's setup ordering, and cannot be seen or reordered by the fixture lifecycle). NUnit allows one `[SetUpFixture]` per
  namespace: if the assembly's `LeakDetectorSetUp` already occupies the root namespace, the new fixture goes in a distinct sub-namespace or
  the `[OneTimeSetUp]` goes in the existing class (decided and stated per assembly). Effect: text-neutral. Lane: `implementer-senior` if more than
  3 assemblies, else `implementer`. Done when: `Build` 0 errors and the 10c filter passes in every touched assembly with results unchanged.

### Step 11 - Split the DTO records out of `GitImpl.cs` (Lane A, after Step 8)
- Done: 1b2f5602 (25 types, one file per type: `MoveType` creates one file per type named after it, so the four thematic files of the Change
  text were not possible; the tool reformatted `GitImpl.cs` accessors, +90/-78 whitespace-only, and left two stray marker comments, removed by
  hand; INBOX line filed). decf3ad8 fixed three Batch B test regressions (probe tables miss inherited `IRepoQuery` methods; one engine-direct
  line-range test asserted `ErrorDetail`, now asserts the `HunkSelection` hint).
- Files: `RoslynSentinel.Tools.Basic/GitImpl.cs` (source) plus new files in `RoslynSentinel.Tools.Basic/` (target). Depends on: Step 8.
  Effect: text-neutral (namespace `RoslynSentinel.Tools.Basic` and every property kept; same assembly).
- Why a separate step: `GitImpl.cs:11-611` holds about 35 public wire types ahead of the engine class (`:612`); `MoveFile` moves a file, so the
  DTOs must be their own files before they can move down independently of the engine (D-b).
- Change: toolset `moveExtract`. Count the types first (`GetFileOutline` on `GitImpl.cs`; `GitErrorCodes` is among them). `MoveAllTypesToFiles`
  creates one file per type: if the measured count exceeds 8 files per operation, use `MoveType` in thematic groups into four target files
  instead (`GitResultBase.cs`: `GitResult`, `GitErrorCodes`, `GitRawResult`; `GitReadResults.cs`: status, log, diff, show, commit, revert, branch;
  `GitRefResults.cs`: checkout, remote, tag, config, stash entries; `GitHunkWorktreeResults.cs`: hunks, stage-hunks, worktree). The grouping is the
  planner's reading of the outline order; the implementer adjusts it to the measured types and reports.
  - 11a - base and read results. 11b - checkout/remote/tag/config/stash. 11c - hunks/stage-hunks/worktree. 11d - verify only: `GitImpl.cs` contains
    the `GitImpl` class (and any private nested types) and nothing else.
  - Each slice carries a `DTO-EXEMPT:` block (verbatim move; the hook treats a moved type as new). Lane: `implementer` for 11a-11c (target file +
    source file, at most 15 `MoveType` calls per slice; a sweep tier is wrong here because each call is a distinct type move).
- Done when: `Build` 0 errors; `GetFileOutline(GitImpl.cs)` shows only `GitImpl` at top level; dispatcher: the Step 2 pins and `GitToolsSmokeTests` pass.

### Step 12 - Spike: the move and the split in a throwaway worktree
- Done: spike report in Appendix B (no commit; worktree removed). Verdict: MoveFile down inside Common works; `SplitProjectByFolder` is a stub
  (blocker `blocking_error_splitprojectbyfolder_is_a_stub.md`); cross-project single-file MoveFile cannot move the reader cluster
  (blocker `blocking_error_movefile_has_no_multi_file_cross_project_move.md`). Step 14 therefore ran the hand-edited csproj/slnx route
  plus per-file MoveFile (see Step 14 Done).
- Files: none in the repo (a worktree under the scratch root, `ROSLYNSENTINEL_SCRATCH_ROOT`)
- Depends on: Steps 11d and 9f. Lane: dispatcher-run (`explore-tier2` or main session), not an implementer slice. Effect: text-neutral.
- Change, in the throwaway worktree: (1) `MoveFile` the DTO files, `IGitOperations.cs`, `GitHunkParser.cs`, `GitImpl.cs` down into
  `RoslynSentinel.Common/Git/` in that order, `Build` after each call and list every error. The questions: does `GitImpl` or a DTO use any type
  from Engines.Basic or Tools.Basic (expected: none, because the hint text is out; the compile answers it; `ToolCallContext` is Common, verified
  at `ToolCallContext.cs:11`)? (2) Then `SplitProjectByFolder(sourceProjectName: RoslynSentinel.Common, folderName: Git, targetProjectName:
  RoslynSentinel.Git)` (`WorkspaceTools.cs:297`, toolset `projectAdmin`). Record with measured numbers: where the files land (is the folder kept
  inside the new project), whether the target must pre-exist (`CreateProject`, whose `projectType` values are unverified), what it does to
  `ProjectReference`s, `InternalsVisibleTo` and the `LibGit2Sharp` `PackageReference` (`RoslynSentinel.Common.csproj:11`), and whether it updates
  the `.slnx`. Then `Build` and list the remaining errors: the expected ones are the missing references and IVT lines of Step 14; any other
  error is a non-Git Common internal the Git code uses (Decision, open question 5), recorded with its type.
- Done when: a spike report (in the dispatcher's message and Appendix B) names, per gap, `TOOL-GAP:` (task, tool, limit, measured sites, suggested
  change) or "works"; the dispatcher records the gaps in `docs/current/TODO.md`. Outcome classes: (i) down-moves green and the split works as is:
  Steps 13-14 proceed (the default, D-e); (ii) works with named gaps: the gap's fix-up is added to Step 14 and the plan proceeds; (iii) a
  Git type uses an Engines.Basic or Tools.Basic type, or the split is unusable for this shape: that group takes the bypass (hand-edited csproj plus
  `MoveFile` per file) only if the owner pre-authorized it (D-e); otherwise Lane A is complete and Steps 13-14 wait with the recommendation
  recorded; the dispatcher continues Steps 15-18 prerequisites that do not depend on the project.

### Step 13 - Move the engine down into `RoslynSentinel.Common/Git/` (D-b)
- Done: 56a5c1b2 (28 MoveFile calls, 0 refusals; `Common.csproj` gained `<Using Include="RoslynSentinel.Common" />`; `GitErrorCodes` is a
  dependency of `GitResult` and moves first; `GitStatusResult` before `GitStageHunksResult`/`GitStashResult`; BareFileWriteRatchetTests key updated).
- Files: the DTO files of Step 11, `IGitOperations.cs`, `GitHunkParser.cs`, `GitImpl.cs` (all from `RoslynSentinel.Tools.Basic/`)
- Depends on: Steps 8, 10c, 11d, 12. Lane: `implementer-sweep` (1 operation: `MoveFile` with `namespaceHandling: keep`; at most 8 files per slice,
  1 project). Needs `HOLD` coordination. Effect: text-neutral (namespace `RoslynSentinel.Tools.Basic` kept on purpose; the rename is Step 19).
- Change, in dependency order, each call compile-green (every moved file keeps resolving its dependencies because Tools.Basic references Common and
  the earlier files are already below):
  - 13a - the DTO files (4 or the measured number). They depend on nothing above Common.
  - 13b - `IGitOperations.cs` (needs the DTOs, now below) and `GitHunkParser.cs`.
  - 13c - `GitImpl.cs`. `Tools.Basic` keeps `GitTools`, `GitRemoteTools`, `GitStatusRenameHints`, `GitHintText`, `GitHintApplyingOperations`.
  - Each slice adds the IVT the compiler asks for (the hint types and `GitResult.Hints` are internal: Common already has IVT to Tools.Basic) and
    updates `BareFileWriteRatchetTests.cs:65` in 13c to key `"RoslynSentinel.Common/Git/GitImpl.cs::StageHunksAsync"` (dispatcher runs the ratchet).
  - A `DTO-EXEMPT:` block in 13a (verbatim move).
- Done when (each slice): `Build` 0 errors. (Group): `Glob RoslynSentinel.Tools.Basic/Git*.cs` lists only `GitTools.cs`, `GitRemoteTools.cs`,
  `GitStatusRenameHints.cs`, `GitHintText.cs`, `GitHintApplyingOperations.cs`; `Search(mode: text, query: "ToolCallHint|ToolSurface|
  SentinelCallToolResult", fileGlob: "RoslynSentinel.Common/Git/**/*.cs")` returns nothing (the engine has no MCP or tool-name dependency);
  dispatcher: the Step 2 pins, `BareFileWriteRatchetTests`, `GitToolsSmokeTests` pass.

### Step 14 - One `SplitProjectByFolder`: `Common/Git/` becomes `RoslynSentinel.Git`
- Done (route changed, owner-authorized bypass for csproj/slnx only; no `.cs` was hand-moved): 99a8d79e (14A: `RoslynSentinel.Git.csproj` hand-written,
  compiles `Common/Git/**` through a link stand-in while Common removes the folder; LibGit2Sharp moved; IVT, ProjectReferences, slnx; two dangling
  crefs in `ChildProcessCapture.cs`/`RepoQuery.cs` made text), 197df57b (14B1: 30 files, MoveFile one per call, intra-project because both paths
  are in the Git compilation), dcd159f1 (14B2: last 30 files, stand-in removed, `Common/Git` gone, ratchets re-keyed to `RoslynSentinel.Git/`,
  `RoslynSentinel.Git` added to ErrorPolicyKind `ScannedProjects` and the FirstMatch/Whitespace/Newline project lists, Common IVT `Tests.ModelEval`
  dropped, `architecture_projects.md` regenerated), 43cb32cd (follow-up: staleness/test-instance-launcher source lists omitted the new project).
  Layout: files at `RoslynSentinel.Git/` root, `RoslynSentinel.Git/GitWorktree/`; namespaces unchanged. Step 18 (tests) can reuse this route.
- Files: all of `RoslynSentinel.Common/Git/**` (Appendix B plus the Step 1a, 11 and 13 files); both `.csproj` files, `.slnx`, consumers' `.csproj`
  files, the ratchets below
- Depends on: Steps 10d and 13. Needs `HOLD` coordination. Effect: text-neutral (namespaces unchanged: `RoslynSentinel.Common`,
  `RoslynSentinel.Common.GitWorktree`, `RoslynSentinel.Tools.Basic` for the engine).
- 14a - the split: toolset `projectAdmin`; `SplitProjectByFolder(RoslynSentinel.Common, Git, RoslynSentinel.Git)` (with `CreateProject` first if
  Step 12 says the target must pre-exist). Then, per the Step 12 report, the fix-ups the tool did not do: `RoslynSentinel.Git.csproj` (`net10.0`,
  `ImplicitUsings`, `Nullable`, `ProjectReference` to Common, `LibGit2Sharp 0.32.0` moved from `RoslynSentinel.Common.csproj:11`),
  `RoslynSentinel.slnx` (beside Common's line 10). Lane: `implementer-sweep` for the call, `implementer` for the csproj edits (3 files).
- 14b - references: `Engines.Basic.csproj` (after `:15`), `Tools.Basic.csproj` (after `:11-12`), `Tools.Advanced.csproj` (after `:15-18`). 14c -
  `Server.Advanced.csproj` (after `:26-30`), `Utilities.PlanStepRunner.csproj` (after `:22-23`), `Tests.Harness.csproj` (after `:46-52`;
  `WorktreeTeardownTests`). `Tests.Server`, `Tests.ModelEval` and `Tests.Tools.Basic` reach it transitively; add explicit references only where the
  compiler asks. The `Directory.Build.targets` native-runtime trim (`TrimLibGit2NativeRuntimes`) applies to every project referencing
  `LibGit2Sharp.NativeBinaries`, so `PlanStepRunner` gets win-x64 only. If the split leaves the solution red until 14b-14c, 14a-14c are one commit
  built once at the end (the only multi-slice commit; the tool's behaviour decides, Step 12).
- 14d - InternalsVisibleTo: `RoslynSentinel.Common.csproj` gains `RoslynSentinel.Git` and drops `RoslynSentinel.Tests`, `RoslynSentinel.Tests.Server`
  and `RoslynSentinel.Tests.ModelEval` unless the compiler still needs them for a non-Git internal (`RoslynSentinel.Common.csproj:25-39`
  today); `RoslynSentinel.Git.csproj` gains IVT to every project touching an internal Git type (the compiler decides; expected from the old list:
  `Engines.Basic`, `Tools.Basic`, `Tests`, `Tests.Basic`, `Tests.Tools.Basic`, `Tests.Server`, `Tests.ModelEval`, `Tests.Harness`, plus
  `Server.Advanced` if it needs one, plus `Tests.Git` in Step 18). Each drop or add is listed in the report (no silent surface change).
- 14e - ratchets (two slices, at most 3 files each): update `BareFileWriteRatchetTests.cs:33, :65` to the actual new paths (the Step 12 report says
  whether the `Git/` folder is kept inside the project); check the five project-list ratchets named in Problem and add `RoslynSentinel.Git` where the
  ratchet is meant to cover moved code; each such edit is listed in the report (no silent coverage drop). A new project is otherwise picked up by the
  directory-scanning ratchets.
- Done when: `Build` at solution scope 0 errors (`scripts/build.ps1 -Flavor Solution`, since Advanced is additive); `Search(mode: text, query: "using
  RoslynSentinel\\.(Engines|Tools)", fileGlob: "RoslynSentinel.Git/**/*.cs")` returns nothing (Git references only Common); dispatcher:
  `BareFileWriteRatchetTests`, `PathComparisonRatchetTests`, `ToolHintRatchetTests`, `ServerBinaryStalenessGitTests`, `GitReaderParityTests`,
  `GitToolsSmokeTests`, `ArchitectureDocFreshnessTests` pass; any `TOOL-GAP:` recorded.

### Step 15 - Thin-tools check
- Done (batch D, no commit; checked by the orchestrator): `Tools.Basic/Git*.cs` is exactly the five tool-side files (`GitTools`, `GitRemoteTools`,
  `GitStatusRenameHints`, `GitHintApplyingOperations`, `GitHintText`). The done-when search is NOT empty: `GitStatusRenameHints.Findings` and
  `IdenticalContentFindings` take an `IGitReader` and call `reader.ReadBlobAtHead` (a tool-side finding that compares blob content; no git invocation
  or parsing). Accepted as a deviation: `// TODO Step 15` marker above `IdenticalContentFindings` (added in b792a478) and an INBOX line. Recommendation:
  later move the blob read behind a `GitEngine`/`IGitOperations` method so Tools.Basic names no reader type.
- Files: `GitTools.cs`, `GitRemoteTools.cs` (read-mostly). Depends on: Step 14. Lane: `explore-tier1` then `implementer` only for any fix.
- Effect: text-neutral.
- Change: confirm `Tools.Basic` Git files are only the five tool-side files (`GitTools` about 246 lines, `GitRemoteTools` 149, `GitStatusRenameHints`,
  the two hint files), that `GitTools` keeps validation, dispatch, result shaping, findings and the hint application, and that no Git behaviour (git
  invocation, parsing) remains in `Tools.Basic`.
- Done when: `Search(mode: text, query: "GitProcess|IGitReader|LibGit2Sharp|Process\\.Start", fileGlob: "RoslynSentinel.Tools.Basic/Git*.cs")`
  returns nothing.

### Step 16 - Tests, part 1: inventory and shared helpers first
- Done (batch D, no commit; inventory in Appendix C): no helper needed lifting. Decision (orchestrator): `McpServerStatusGitBackendTests` moves with
  the Git fixtures (it is mostly Git-project subject matter and uses `GitReaderParityTests.ScriptedReader` and Git internals), so no staying file uses a
  nested type of a moving fixture and no moving file uses a helper from a staying file (the build after Step 18 is the proof).
- Files: `docs/current/plans/` Appendix C (dispatcher), and the helper files named below
- Depends on: Step 15. Lane: dispatcher inventory, then `implementer` per helper slice (3 files each). Effect: text-neutral (test code only).
- Change: (1) write the move inventory into Appendix C: every `Tests.Tools.Basic` file that is Git-subject (the 42 `Git*.cs` files with
  `[Category("OutOfProcess")]` plus support files `GitCommitMessageTestSupport`, `GitHookTestSupport`, `GitPatchParityHarness`,
  `CasCommitTestSupport.cs`, `ShortHashLengthsTests.cs`, and any other Git fixture the proposal's 44 counts), the files that construct `GitImpl`
  (17 files, 22 sites per the proposal; 20 files with `GitImpl` references seen), and the stay list (`GitTestRepo` in `Tests.Support.Basic`,
  `ChildProcessCaptureTests`, and per file `McpServerStatusGitBackendTests`, `ServerBinaryStalenessGitTests`,
  `TestInstanceLauncherGitStampTests`, which test the Common seam consumers). (2) Helpers first: every helper a staying file uses from a moving
  file is lifted out before the fixtures move (`ScriptedReader` nested in `GitReaderParityTests.cs:982`, used by
  `McpServerStatusGitBackendTests.cs`: becomes a top-level internal class in `Tests.Support.Basic`, or the using fixture moves too, decided by the
  inventory); every helper a moving file uses from a staying file is checked (`GitTestRepo` stays shared; direction `Tests.Git` ->
  `Tests.Support.Basic`, never the reverse). Check which `[SetUpFixture]` content `LeakDetectorSetUp` guards (the base class
  `WorkingDirectoryLeakDetector`) so Step 18 copies the right thing.
- Done when: `Build` 0 errors; Appendix C counts equal the Search counts (stated in the report); `Search(mode: references)` on each lifted helper shows no
  reference from a staying file to a nested type of a moving fixture.

### Step 17 - Tests, part 2: gather the Git fixtures into a `Git/` folder (same project)
- Done (folded into Step 18, orchestrator decision, batch D): no `Git/` gathering folder. A wildcard link stand-in (Tests.Git compiles
  `..\RoslynSentinel.Tests.Tools.Basic\Git*.cs` plus four wildcarded non-`Git*` names; Tests.Tools.Basic has the matching `Compile Remove`) puts all 61
  files in the new compilation at once, so each `MoveFile` goes straight to `RoslynSentinel.Tests.Git/` (61 calls instead of 122). The nightly-plan
  session had already been told that the fixtures move between projects.
- Files: per slice at most 8 test files (`MoveFile` into `RoslynSentinel.Tests.Tools.Basic/Git/`, `namespaceHandling: keep`)
- Depends on: Step 16. Lane: `implementer-sweep`, about 7 slices for about 52 files, one operation each; needs `HOLD`. Effect: text-neutral.
- Heads-up (D-f, no wait): before the first slice, `SendMessage` the nightly-plan owner session that the Git fixtures are moving between projects and
  that its Step 9 slices (adding `[Parallelizable(ParallelScope.Children)]`/`InstancePerTestCase` to `GitWorktreeRepoLocalConfigTests`,
  `GitStageToolParityTests`, `GitCommitReadsParityTests`, `GitDiffShowToolParityTests`, `GitLogToolParityTests`, `GitPatchParityCorpusMoreTests`,
  `GitCasCommitTests`, `GitCommitUntrackedParityTests`, `GitRelativeWorktreeTests`, `GitReaderParityTests`, `GitCommitSeamParityTests`) must re-point
  their paths; a fixture already being edited there is moved in a later slice of this step, not mid-edit (`Git(operation: status)` on the file).
- Done when (each slice): `Build` 0 errors; moved classes pass with the same results (dispatcher `RunTest`, `suite: full`, filter by the moved
  classes). (Group): `Glob RoslynSentinel.Tests.Tools.Basic/Git*.cs` (root) returns only the stay-list files of Appendix C.

### Step 18 - Tests, part 3: one `SplitProjectByFolder` creates `RoslynSentinel.Tests.Git`
- Done (route: hand-written csproj plus stand-in as in Step 14, `SplitProjectByFolder` not used): 88975f2d (18A: `RoslynSentinel.Tests.Git.csproj`, `AssemblyInfo.cs`
  with `Parallelizable(ParallelScope.Fixtures)`, its own `LeakDetectorSetUp.cs` with the `RepoQueryProvider` seam install, slnx entry, IVT for `Tests.Git`
  added to Common, Git and Tools.Basic (not Engines.Basic: the compiler did not ask), one shell `dotnet restore` before the first LoadSolution),
  b8a7dc82 (18B1: 30 files), ee4e95bc (18B2: 31 files, stand-in removed). Layout: flat at `RoslynSentinel.Tests.Git/`; namespaces unchanged
  (`RoslynSentinel.Tests.Tools.Basic`, four files keep `RoslynSentinel.Tests.Battery.Basic`). Scripts discover the project by glob; no list needed an entry.
- Files: `RoslynSentinel.Tests.Git/` (created by the split), `AssemblyInfo.cs`, the setup fixture, `.csproj`, `RoslynSentinel.slnx`, IVT lines
- Depends on: Step 17. Lane: `implementer-sweep` for the call, `implementer` for the fix-ups (3 files per slice). Effect: text-neutral.
- Change: toolset `projectAdmin`: `SplitProjectByFolder(sourceProjectName: RoslynSentinel.Tests.Tools.Basic, folderName: Git, targetProjectName:
  RoslynSentinel.Tests.Git)`. Fix-ups the tool does not do (Step 12 measured the tool on the production side; the test side is a second
  measurement, reported as `TOOL-GAP:` where it differs): `AssemblyInfo.cs` with `[assembly: Parallelizable(ParallelScope.Fixtures)]` as
  `Tests.Tools.Basic/AssemblyInfo.cs:8`; its own `[SetUpFixture]` modeled on `LeakDetectorSetUp.cs`; `.csproj` references (the `Tests.Tools.Basic`
  references plus the Git project, no embedded Server goldens unless a moved test needs them); `.slnx`; IVT from the Git and Tools.Basic projects to
  `Tests.Git`. Test namespaces stay `RoslynSentinel.Tests.Tools.Basic` (Out of scope; an INBOX line records the mismatch).
- Done when, outcome classes by `RunTest` on both projects and the `48fb08e2` totals (3,783 tests in `Tests.Tools.Basic`) or the Step 9a counts, whichever
  is fresher: **equal** (sum of `Tests.Tools.Basic` + `Tests.Git` equals the pre-move count); **fewer** (a fixture stopped being discovered: name it, fix,
  repeat); **more** (a duplicate: name it). Plus the moved classes pass with the same results as before, `scripts/Test-Parallel.ps1` picks the new project
  up (its discovery is `RoslynSentinel.Tests*.csproj`, `scripts/Test-Parallel.ps1:27`; `Invoke-WorktreeTests.ps1:251` globs `RoslynSentinel.Tests*`
  directories) and the `OutOfProcess` category is still excluded from the fast suite for the moved fixtures (`TestSuiteFilter`).

### Step 19 - Rename: namespace `RoslynSentinel.Git` and `GitImpl` -> `GitEngine` (D-d)
- Done (partial by design, D-e default): b792a478. (a) `RenameSymbol` GitImpl -> GitEngine (118 sites, 3 projects, 23 files; it renamed the file to
  `GitEngine.cs` itself, so `SyncTypeAndFilename` was not needed); the `BareFileWriteRatchetTests` key follows. (b) TOOL-GAP: no tool changes a namespace
  with its `using` sites (only `PreviewMoveFileToNamespaceFolder`, inactive, preview only); namespaces unchanged (plumbing `RoslynSentinel.Common`, engine and
  DTOs `RoslynSentinel.Tools.Basic`, worktree `RoslynSentinel.Common.GitWorktree`); `// TODO rename namespace` markers in `GitEngine.cs` and `GitProcess.cs`,
  a TODO.md line and INBOX lines. The `Git` shadowing check was not needed (no namespace rename). Left cosmetic `GitImpl` names the tool does not follow:
  see the INBOX line of 2026-11-25 04:50 (`_gitImpl` field, test helper names, 13 comments), so the done-when search for `GitImpl` is not empty.
- Files: every file in `RoslynSentinel.Git/` plus all `using` sites; measured before the slice. Depends on: Step 18. Lane: `implementer-senior`
  (more than the sweep limits). Needs `HOLD` coordination. Effect: text-neutral (namespaces and a class name; no wire change: DTO type names are not
  serialized and the property names are unchanged).
- Change: (a) `RenameSymbol` on `GitImpl` -> `GitEngine` (one call, about 45 sites in 20 files measured by the proposal; re-measured here) and
  `SyncTypeAndFilename` for the file name; the `BareFileWriteRatchetTests.cs` key follows the file name. (b) The namespace change for the plumbing
  (`RoslynSentinel.Common` -> `RoslynSentinel.Git`), the engine and DTOs (`RoslynSentinel.Tools.Basic` -> `RoslynSentinel.Git`) and the worktree
  harness (`RoslynSentinel.Common.GitWorktree` -> `RoslynSentinel.Git.GitWorktree`): no dedicated tool was found, so first call
  `McpServerStatus(toolListing: inactive, toolNameFilter: "namespace")`, then try `RenameSymbol` on the namespace and `MoveFile` with
  `copyAndObsolete`. If none changes the namespace of a whole project's types with its references, report `TOOL-GAP:` (task, tools tried,
  measured types and `using` sites, suggested change: a `ChangeNamespace` tool or a namespace mode on `SplitProjectByFolder`); do not substitute a text
  replace. Default under D-e: record the gap, leave `// TODO rename namespace to RoslynSentinel.Git` at the declarations that remain and one
  `docs/current/INBOX.md` line, and continue. Check before renaming that no type or member named `Git` exists in a `RoslynSentinel.*` namespace that
  would be shadowed by namespace `RoslynSentinel.Git` (`Search(mode: symbol, query: "Git")`).
- Done when: `Build` at solution scope 0 errors; either `Search(mode: text, query: "namespace RoslynSentinel\\.(Common|Tools\\.Basic)",
  fileGlob: "RoslynSentinel.Git/**/*.cs")` returns nothing, or the `TOOL-GAP:` and the TODO markers exist; `Search(mode: text, query: "GitImpl")`
  returns nothing outside comments and docs; dispatcher: all Step 2 pins, `BareFileWriteRatchetTests`, `GitToolsSmokeTests` pass.

### Step 20 - Wiring finish: generated docs, CLAUDE.md, architecture map
- Done: generated doc in bcc5031d; CLAUDE.md layering line and architecture map in the Step 20-22 doc commit (CLAUDE.md was already over its 15 KB budget at 15,978 bytes before this step; the edit adds 66 bytes)
- Files: `docs/generated/architecture_projects.md` (generated; do not edit by hand), `CLAUDE.md`,
  `docs/current/references/reference_architecture_map.md`
- Depends on: Step 19. Lane: main session (CLAUDE.md is owner-approved wording). Effect: text-neutral (docs).
- Change: run `scripts/Generate-ArchitectureMap.ps1` (sets `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1`; guarded by `ArchitectureDocFreshnessTests`);
  CLAUDE.md Architecture: layering line becomes ``Common <- Git <- Engines.* <- Tools.* <- Server.*`` with a one-line note that `Git` holds the git
  readers, the Git engine and the worktree harness, and "`Utilities.PlanStepRunner` references only `Common` and `AgentLoop`" gains "and `Git`"
  (keep CLAUDE.md under its 15 KB budget, `scripts/Test-GuidanceHygiene.ps1`); the architecture map's Layers table gets a Git row, the "Tests" list
  gains `Tests.Git`, and the Sibling-projects sentence stays true. Verify the map's claims by reading it, not by editing from this plan.
- Done when: `ArchitectureDocFreshnessTests` passes; `pwsh scripts/Test-GuidanceHygiene.ps1` reports no new finding.

### Step 21 - Handoff to the nightly plan owner (message only)
- Done: message sent 2026-11-24 to roslynsentinel-ec (owner of the nightly plan); no reply requested
- Files: none. Depends on: Step 18. Effect: text-neutral.
- Change: `SendMessage` to the session that owns `plan_full_suite_nightly_and_tail_shard.md` (find it with `ListAgents`); do not edit that plan. Content:
  the Git area's watch list in `scripts/test-areas.json` is the directory `RoslynSentinel.Git/` plus `RoslynSentinel.Tests.Git/`, plus the tool-side
  files `RoslynSentinel.Tools.Basic/GitTools.cs`, `GitRemoteTools.cs`, `GitStatusRenameHints.cs` (their tool-level fixtures moved to `Tests.Git`) but
  NOT `GitHintText.cs` / `GitHintApplyingOperations.cs` (the proposal's principle (c): a hint-text edit is not a Git change; the nightly run is the
  backstop, see Risks 3); and the old file globs of its Step 5 (`RoslynSentinel.Common/Git*.cs` and the others in Step 9) no longer exist.
- Done when: the message is sent; the reply, or its absence, is noted in the commit message of Step 22.

### Step 22 - Closing stub sweep
- Done: no plan stub (`NotImplementedException`, "TODO implement") or `[Obsolete]` Git/hint member remains; the plan's own TODOs (namespace rename at `GitEngine.cs`/`GitProcess.cs`, `GitStatusRenameHints` blob read) have INBOX lines. Pre-existing TODOs that moved with the files and have no INBOX line: `GitEngine.cs` RunGitAsync line re-join, `Libgit2GitReader.cs`, `.IndexWrites.cs`, `.Patch.cs` (2), `GitPatchParityHarness.cs` (2) - left as found. `SplitProjectByFolder` remains a simulation (`ProjectStructureEngine.cs` `SplitProjectByFolderAsync`, blocker doc).
- Files: this plan's files. Depends on: Steps 1-21. Effect: text-neutral. Lane: `explore-tier1`.
- Change: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.{Git,Tools.Basic,Common}/**/*.cs")` and
  `Search(mode: text, query: "TODO implement \\(Step", ...)`; fail the plan if any stub from Steps 1a-1b or 10a remains. Also confirm the
  `[Obsolete("Use ...")]` members added by Steps 5 and 6i are gone (`Search(mode: attribute, query: "Obsolete", ...)` over the Git project and
  `GitHintText.cs`), and that every `// TODO` the plan added (Step 19 default) has its INBOX line.
- Done when: both searches return no Git-plan stub or obsolete member.

### Step 23 - Measure and close
- Done: full run at bcc5031d (`c:\tmp\rs-testtrees\_results\bcc5031d-20261124-220550-24616`, 1 overlapping run) against the Step 9a
  baseline 042035a6 (`042035a6-20261124-184659-62000`, 4 overlapping runs, so its walls are inflated). Outcome: **adopted**.

  | Measure | Baseline 042035a6 | Final bcc5031d |
  | --- | --- | --- |
  | Failures (baseline 0) | 0 | 0 |
  | Tests total | 12,274 | 12,286 (+12: tests added by Steps 2-10/14) |
  | `Tests.Tools.Basic` wall / tests / test-seconds | 1,189 s / 3,888 / 8,659 | 277 s / 2,576 / 2,140 |
  | `Tests.Git` wall / tests / test-seconds | - | 760 s / 1,324 / 4,517 (OutOfProcess 1,173 tests, 4,511 s) |
  | Total wall / summed test-seconds | 1,598 s / 9,982 | 1,421 s / 7,926 |

  `Tests.Git` is now the full run's critical path (760 s) instead of `Tests.Tools.Basic`; the full-run total moved little because
  that tail moved with the fixtures (context: run `48fb08e2` had 1,378 s total, Tools.Basic 918 s wall, Git 4,516 summed seconds).
  The payoff is task-end runs: a change outside `RoslynSentinel.Git/`, `RoslynSentinel.Tests.Git/` and the three tool-side Git files
  can skip `Tests.Git` (about 4,500 summed test-seconds) once the nightly plan's selection rule lands; until then selection is a
  directory test on `git diff --name-only`. Step 19's namespace half is carried by
  [proposal_multi_file_movefile_and_namespace_change_tool.md](../../proposals/closed/proposal_multi_file_movefile_and_namespace_change_tool.md).
- Files: this plan; `proposal_isolate_heavy_test_areas_by_project.md` Status line; `docs/current/TODO.md`/`CLOSED.md` entries
- Depends on: Step 22. Effect: text-neutral.
- Change: one full run at the final commit on an otherwise idle machine; compare per-project wall and summed seconds with the Step 9a baseline run
  (a fresh run taken just before the first move step, commit recorded there) and quote run `48fb08e2-20261124-111028-65836` (1,378 s total,
  `Tests.Tools.Basic` 918 s wall and 3,783 tests, Git 4,516 summed test-seconds in 49 fixtures) as context, not as the primary baseline (the tree has moved
  since): `Tests.Tools.Basic` wall, `Tests.Git` wall, total wall, test counts, failure set against the baselines (`docs/known-failing-tests.*.txt`,
  0 failures), load-timeout count. Verify that a Git-only change selects `Tests.Git` and a non-Git change does not, using the nightly plan's selection
  rule if it has landed, else by reading `git diff --name-only`. Outcome classes: **adopted** (equal failure sets, `Tests.Tools.Basic` wall down by roughly
  the Git tail, counts equal); **adopted, no wall gain** (record what bounds the wall: the proposal's gain is task-end skipping, the full-run gain is not
  measured); **new failures** (name each, trace to a step, revert that slice or fix); **counts differ** (Step 18 class). Update the proposal's Status
  line; close with `Closes: plan_isolate_git_area_into_own_project` and `ProjectDoc(action: close)`.
- Done when: the comparison table is in this plan's Status line or a closing paragraph with the commit hashes.

## Out of scope

- Areas 2 (Build/RunTest) and 3 (PersistentWorkspaceManager test-side work): a later plan.
- `scripts/test-areas.json`, the selection rule, the nightly run and the debt check: the nightly plan.
- Renaming the test namespace (`RoslynSentinel.Tests.Tools.Basic` stays on the moved fixtures; an INBOX line records it) and renaming moved test
  classes.
- Any change to tool classes' public surface or `ToolClassRegistry.cs` (no new `[McpServerTool]`, so no registry entry, golden or fresh-session need).
- Changing any wire text, DTO property or tool schema (a text change found by a pin stops the slice).

## Risks and open decisions

Decided items are in Decision (D-a to D-f, seam, `TryGetGitRoot`); what remains open or accepted is below.

1. **Appendix A slot assignments are planner readings.** The slot per site (ErrorDetail, Inline, ErrorReplace, NotePrefix, NoteSuffix) comes from
   search previews and the sites quoted in the proposal, not from reading each method. A slice that finds a site that needs a different slot, or two
   fragments of one key with different facts (the one-token-per-key rule of D-c), changes Appendix A in the same commit and, if the contract needs a new
   slot, stops: a contract change is a Step 1 change. Open until Steps 6-7 finish.
2. **Residual pin gap.** A site with no cheap scenario is "renderer-pinned only", so its engine-to-facts wiring (a wrong fact passed) is verified by the
   reviewer reading the diff, not by a test. Step 2f makes the list explicit; the owner may decide it is enough or ask for a scenario per site. Pass-through
   sinks beyond the four of Step 4 are found by a search, not by a proof: a missed sink is a planner gap that a Step 2e pin would not catch unless it
   exists.
3. **Tool-level fixtures and the watch list.** About ten moved fixtures exercise `GitTools`/`GitRemoteTools` (for example `GitToolsSmokeTests`,
   `*ToolParityTests`). If the watch list excludes `GitHintText.cs`, a hint-text edit does not select `Tests.Git` at task end, and a `GitToolsSmokeTests`
   assertion on hint text (`:2284`) runs only in the nightly run. Recommendation: accept (the proposal's principle (c)); the Step 2 pins live in
   `Tests.Tools.Basic` and are fast to run by name. Alternative: add `GitHintText.cs` to the Git watch list and give up part of principle (c).
4. **Seam install site.** Recommendation: `AddRoslynSentinelEnginesBasic` (it already registers the manager and now `IRepoProbe`; both production hosts and
   the shared test host helper call it, so both seams are installed in one place). The coordinator's wording was "set by Server at startup"; Server satisfies
   it by calling that extension. Alternative: `WarmupAndAutoLoadBasic` (`ServiceRegistrationExtensionsBasic.cs:503`), which runs later and not in test
   hosts. Process-wide static state: a test that swaps `RepoQueryProvider.Current` must restore it and, if it runs in parallel with a reader of the
   provider, carry `[NonParallelizable]` with a comment (CLAUDE.md, Agent conduct). Unit tests that never build a provider need Step 10d.
5. **`Git` namespace shadowing** (Step 19): inside `RoslynSentinel.*`, a simple name `Git` resolves to the namespace `RoslynSentinel.Git`; any type, member or
   enum value named `Git` in scope would change meaning. Step 19 searches first; a hit is a decision deferred under D-e with the recommendation to
   name the namespace `RoslynSentinel.GitEngine` instead.
6. **Spike outcome unknown** (Step 12): `SplitProjectByFolder` may mishandle `ProjectReference`s, IVT or packages, and `MoveFile` cross-project is new
   (2026-11-22). The move down (Step 13) stays inside one project on purpose, so the only unproven tool call is the split (Steps 14, 18). Bypass needs the
   owner's authorization, absent during the run (D-e): the default then is to stop that group and report.
7. **Cross-plan sequencing without a hold (D-f).** Step 9 breaks the nightly plan's Step 5 file globs and Step 17 moves fixtures its Step 9 edits; both
   send a heads-up and proceed. The cost is a re-point on the other plan's side (stated in the messages). The baseline of Step 9a and the nightly plan's
   own timing steps both change wall times: compare per project, not wall alone.
8. **Coverage drift from moving fixtures** between projects changes what `-Projects` runs and the per-project baselines
   (`docs/known-failing-tests.*.txt`): Step 18's count check catches lost discovery, not lost *category* filters; the dispatcher also confirms the
   `OutOfProcess` category is still excluded from the fast suite for the moved fixtures (`TestSuiteFilter`).
9. **`RoslynSentinel.Git` carries LibGit2Sharp's native library into `PlanStepRunner`** (Step 14); trimmed to win-x64 by `Directory.Build.targets`
   (`TrimLibGit2NativeRuntimes`), so the size cost is one RID; on non-Windows the reader seam already falls back to the CLI.
10. **Public surface of the engine in Common during Steps 13-14.** Between them, the public DTOs and `IGitOperations` live in the Common assembly
    under namespace `RoslynSentinel.Tools.Basic`; tool schemas are built from the DTO types' shapes, not their assembly, so no schema change is expected
    (checked by `GitToolsSmokeTests` and the server golden tests in the Step 14 run).
11. **TOOL-GAPs found by the Step 12 spike (open, blockers written):** `SplitProjectByFolder` is a stub and `MoveFile` has no multi-file cross-project move
    (Appendix B). Step 14 used the link-stand-in route instead; Steps 18 (tests) and any later split reuse it unless the tool is implemented. Recommendation: either
    implement `SplitProjectByFolder` (move files, ProjectReference to the source project, package/IVT fix-up report) or make it throw `ToolNotImplementedException` so
    it cannot report success.
12. **Server-binary staleness lists name projects by path** (found in Step 14B2, fixed in 43cb32cd): any further new production project must be added to
    `ServerBinaryStaleness.SourcePathPrefixes`, `scripts/server-source-paths.txt` and `TestInstanceLauncher` (Step 19/18 add no production project; a later area plan will).
13. **Tiers.** Slices are sized to depth tier 1 and sweep tier 0 as of this draft; if `.claude/implementer-tiers.json` is raised, Steps 9, 13 and 17 shrink
    (sweep tier 1 allows 15 files per operation). Re-read the file before dispatching.

## Related

- Design: [proposal_isolate_heavy_test_areas_by_project.md](../../proposals/proposal_isolate_heavy_test_areas_by_project.md)
- Nightly plan (owns the selection rule, `test-areas.json`, Step 9 parity-fixture edits, run `48fb08e2` comparison):
  [plan_full_suite_nightly_and_tail_shard.md](../plan_full_suite_nightly_and_tail_shard.md)
- Context: [plan_shorten_verification_loop.md](../plan_shorten_verification_loop.md), `docs/current/references/reference_architecture_map.md`,
  blocker `blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md`,
  `blockers/closed/blocking_error_movetype_cannot_relocate_namespace_type_across_projects.md`
- Skills: `.claude/skills/plan-writer/SKILL.md`, `.claude/skills/dto-conventions/SKILL.md`

## Appendix A - Hint-site inventory (`GitImpl.cs`, lines at HEAD `a82c8544`; re-find by member name, lines drift)

Counted from `Search(mode: text, query: "ToolCallHint\.Call")` over `Tools.Basic/Git*.cs`: 53 sites, 51 here. Kind and slot per site are this
planner's reading of the search preview and the coordinator's notes; the Step 2 and Step 6-7 slices confirm each by reading the site, and a changed
classification updates this table in the same commit. Slot values: ED = `ErrorDetail`, IN = `Inline`, ER = `ErrorReplace`, NP = `NotePrefix`,
NS = `NoteSuffix`; "confirm" means the preview did not decide it.

| Kind | Sites | Slot (reading) | Slices |
| --- | --- | --- | --- |
| A, table (`GitErrorDetails`, `:112-137`, 23 members, 16 with tool calls: `:114, :116-122, :125, :126, :128-130, :132, :133, :136`) | 56 reference sites in 4 regions: tag/config/stash `:1017`-`:1349`, worktree `:1627`-`:1778`, hunks `:2096`-`:2282`, stage/commit/lock `:2445`-`:3730` | ED (all 23 keys) | 5a, 5b, 5c, 5d |
| B1 inline, tag/stash/config | `ActionRefused:694`, `ConfigAsync:770`, `ConfigWriteAsync:840`, `TagCreateAsync:1038`, `StashRestoreAsync:1200, :1235`, `StashRollbackConflictAsync:1278, :1279` (8) | ED or IN, confirm per site | 6a, 6b |
| B2 inline, worktree | `WorktreeAddAsync:1684, :1732`, `WorktreeRemoveAsync:1783, :1787, :1816, :1836` (6) | `:1732` IN; `:1816`, `:1836` IN (call inside `Error`); `:1684`, `:1783`, `:1787` ED, confirm | 6c, 6d |
| B3 inline, hunks | `NoUnstagedChangesLoad:2095`, `LoadDiffForPathAsync:2114`, `LoadFileDiffAsync:2172`, `SelectHunks:2242, :2249, :2274, :2275` (7) | `:2172` IN; others ED, confirm | 6e, 6f |
| B4 inline, stage/commit/checkout/in-progress/root | `IndexEntryChangedMessageAsync:2372, :2373`, `BuildStageHunksResultAsync:2400`, `StageHunksAsync:2471`, `InProgressAdviceAsync:2604, :2605`, `CommitAsync:3810`, `CheckoutAsync:4496`, `TryGetGitRoot:1922` (9) | `:4496` IN; `:1922` via `GitRootFailure` (tool appends); others ED or ER, confirm | 6g, 6h, 6i |
| C notes | `TagDeleteAsync:1113` (IN), `StashDiskNote:1363` (NP; users `:1196`, `:1358`), `WorktreeAddAsync:1742` (confirm), `WorktreeRemoveAsync:1842` (IN; Note at `:1850`), `BuildHunksResult:2209` (confirm) (5) | as listed | 7a, 7b |
| Tool side, unchanged | `GitTools.cs:158`, `GitTools.cs:232` (2) | none | none |
| Pass-through sinks (no hint text; must carry `Hints`) | `AppendInProgressAsync:2614-2625`, `StageHunksAsync:2436-2475` (`:2450`, `:2454`), `HunksAsync:3170`, `CommitAsync:3743` | not applicable | 4 |

16 + 30 + 5 = 51. Sites in `GitImpl.cs` carrying `// hint-ok:` comments are hand-written non-call text and are not in this table.

## Appendix B - Git file list for the Common consolidation (filled by Step 9b; spike report by Step 12)

Expected from this session's searches (confirm in Step 9b): 27 root files: `GitRunResult.cs`, `GitRunOptions.cs`, `GitProcess.cs`,
`GitOperationState.cs`, `GitCommitTrailers.cs`, `Libgit2Native.cs`, `GitHookResults.cs`, `GitShellLocator.cs`, `Libgit2GitReader.cs`,
`Libgit2GitReader.Hooks.cs`, `Libgit2GitReader.CommitMessageConfig.cs`, `Libgit2GitReader.InProcessCommit.cs`, `Libgit2GitReader.CasCommit.cs`,
`Libgit2GitReader.Config.cs`, `Libgit2GitReader.Patch.cs`, `Libgit2GitReader.IndexWrites.cs`, `GitCommitMessage.cs`, `CommitMessageResult.cs`,
`GitHooks.cs`, `GitBackendWarmup.cs`, `CliGitReader.cs`, `GitReader.cs` (`IGitReader`, minus the two records Step 10a moves out),
`FallbackGitReader.cs` (also holds `GitReaders`), `CasCommitResult.cs`, `InProcessCommitRoute.cs`, `RepoWorktrees.cs`, `ShortHashLengths.cs`; and
3 `GitWorktree/` files: `GitWorktreeManager.cs`, `IStepBranchStrategy.cs`, `IWorktreeStep.cs`. Files already in `Common/Git/` before Step 9: the
Step 1a types. Added later by this plan: the DTO files (Step 11), `IGitOperations.cs`, `GitHunkParser.cs`, `GitImpl.cs` (Step 13). Non-Git Common
referencers (Step 9b classifies each): `PersistentWorkspaceManager.cs`, `ServerBinaryStaleness.cs`, `NotFoundEvidence.cs` (behind the seam). Spike
(Step 12) report: to be written.

**Step 9b result (batch B, 2026-11-24, after Steps 9c-9g and 10a-10c):** the 27 root files and 3 `GitWorktree/` files all moved
(`Common/Git/` holds 30 moved files plus `GitRootFailure.cs` and `GitHintTypes.cs`; `Glob RoslynSentinel.Common/*[Gg]it*.cs` returns nothing).
Non-Git Common referencers now: none in code. `PersistentWorkspaceManager.cs`, `ServerBinaryStaleness.cs` and `NotFoundEvidence.cs` are behind
the seam (`IRepoProbe`, `RepoQueryProvider`; `RepoQuery.cs` stays in the Common root). Three prose references remain and will need
attention at the project split (a `<see cref>` to a type in the new project becomes CS1574): `ChildProcessCapture.cs:23` (cref `GitProcess`),
`RepoQuery.cs:20` (cref `IGitReader`), `RepoQuery.cs:54` (text only); `ToolCallTranscript.cs:44` is text only.

**Step 12 spike report (batch C, 2026-11-24, throwaway worktree at `1b2f5602`, removed afterwards):**
- (1) `MoveFile` down into `Common/Git/`: works. One file per call (a comma list gives `NotFound`), about 20 s each, self-validating. Needs
  `<Using Include="RoslynSentinel.Common" />` in `Common.csproj` first (without it 213 CS0246). Order: `GitErrorCodes`, `GitResult`, the 7 `*Entry`,
  the `*Result` files (`GitStatusResult` before `GitStageHunksResult`/`GitStashResult`), `IGitOperations`, `GitHunkParser`, `GitImpl`. `GitImpl` and the
  DTOs use nothing from Engines.Basic or Tools.Basic. Full Build 0 errors. (The real move in Step 13 took 28 calls, no refusals.)
- (2) `SplitProjectByFolder`: TOOL-GAP, a stub (`SolutionStructureEngine.SplitProjectByFolderAsync`, `// TODO ... is a simulation`): it runs
  `dotnet new classlib`, counts documents, moves 0 files while reporting "Moved 32 files" (60 were there), writes a csproj with no Common
  reference, adds a slnx entry with an odd `BuildType` line, and touches no ProjectReference, IVT, package or the `GitWorktree/` subfolder.
  Blocker: `blocking_error_splitprojectbyfolder_is_a_stub.md`.
- Cross-project single-file `MoveFile` into a pre-built Git project works top-down only (`GitImpl`, `IGitOperations`, `GitHunkParser` moved green;
  `GitResult` before its derived records gave 1391 errors, `GitProcess` before the readers 45, `Libgit2GitReader.Hooks.cs` alone 178): TOOL-GAP, no
  multi-file atomic move. Blocker: `blocking_error_movefile_has_no_multi_file_cross_project_move.md`.
- Route that worked in Step 14: hand-written csproj with a link stand-in (`<Compile Include="..\RoslynSentinel.Common\Git\**\*.cs" LinkBase="Git" />`;
  Common `<Compile Remove="Git\**\*.cs" />`) so the Git compilation already holds the files, then per-file `MoveFile` inside that one project (60 calls,
  0 refusals, 15-20 s each), then remove the stand-in. The same trick applies to Step 18 (tests).
- Common internals the Git code uses (Common IVT to Git is required): `IRepoQuery`, `ChangedCommit`, `WorkingTreeChange` (`RepoQuery.cs`; 12 CS0122
  with the IVT removed). Common IVT: `Tests` (`ISnippetMatcher`) and `Tests.Server` (`PersistentWorkspaceManager.RecordReloadFailure`/`ClearReloadFailure`)
  are still needed, so Step 14d's "drop those three" was wrong for two; `Tests.ModelEval` dropped; `Tests.Harness` needed (`WorkingTreeChange`).
- Warnings: delta 0 (10 CS0618 in Tests.Server before and after); doc diagnostics are off, so no CS1574 ever appeared; the two crefs that really dangled
  were made text anyway. First `LoadSolution(forceReload)` right after adding a brand-new csproj failed ("Unable to find project information for
  'RoslynSentinel.Git.csproj'") until `dotnet restore RoslynSentinel.slnx` ran in the shell (minor bypass: restore has no MCP tool).

## Appendix C - Test move inventory (filled by Step 16)

Written by the batch D orchestrator (explore-tier2 inventory, 2026-11-24, then confirmed by the build after the move).

- **Move list: 61 files into `RoslynSentinel.Tests.Git/`** (all from the `RoslynSentinel.Tests.Tools.Basic` root): the 57 `Git*.cs` files (54 fixtures plus
  the support files `GitHookTestSupport`, `GitCommitMessageTestSupport`, `GitPatchParityHarness`) and `CasCommitTestSupport` (support), `ShortHashLengthsTests`,
  `RepoPathWorktreeTests`, `McpServerStatusGitBackendTests`. The proposal's "42 OutOfProcess `Git*.cs`" differs because the fixtures were gathered by
  subject, not by category; Search/Glob counts above are the authority.
- **Stay list (3 Git-flavoured files plus unrelated users of `GitTestRepo`):** `ServerBinaryStalenessGitTests` (Common seam consumer, uses Git-project
  internals through the existing IVT), `TestInstanceLauncherGitStampTests` (Engines.Basic), `ChildProcessCaptureTests` (Common). `DocCloseTests`,
  `DocTrailerEngineTests`, `NotFoundEvidenceTests` use `GitTestRepo` only as a fixture and stay. `McpServerStatusGitBackendTests` was contradicted as a
  stay candidate (uses `GitReaderParityTests.ScriptedReader` and `Libgit2GitReader`/`CliGitReader`/`ShortHashLengths` internals) and moved.
- **Helpers:** none lifted. The only cross-link (staying file using a moving file's type) was `ScriptedReader`, resolved by moving the user. No moving file uses a
  helper from a staying file. Other projects with Git-subject fixtures are not in scope: `Tests.Harness` (`WorktreeTeardownTests`, `RunStampTests`),
  `Tests.Basic` (`BareFileWriteRatchetTests`, `ResidualMentionScannerTests`), `Tests.ModelEval` and `Tests.Server` (tool-name mentions or `GitTestRepo` only).
- **`GitImpl` (now `GitEngine`) construction:** 23 files (118 semantic sites); the internal constructor `(ISolutionProvider, ILogger, IGitReader,
  InProcessCommitRoute?, Action<string>?)` is used by about 14 fixtures, hence the IVT from `RoslynSentinel.Git` to `RoslynSentinel.Tests.Git`.
- **Fast-suite membership:** fixtures without `[Category("OutOfProcess")]` that now run in the fast suite of Tests.Git: `GitCommitMessageTests`,
  `GitHintApplyTests`, `GitHintRenderLiteralTests`, `GitHunkParserTests`, `GitCommitTrailersTests`, `GitCommitRoutePinRatchetTests` (also `Ratchet`),
  `ShortHashLengthsTests` outer class.
- **Setup:** `LeakDetectorSetUp` (global namespace) derives from `WorkingDirectoryLeakDetector` (Tests project: snapshot, `TestHostAmbientDefaults.Apply`,
  leak assertion) and adds the Step 10d `[OneTimeSetUp]` that installs `RepoQueryProvider.Current ??= GitReaders.Default`; Tests.Git carries its own copy.

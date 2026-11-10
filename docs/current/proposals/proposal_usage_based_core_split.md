# Split the solution on measured tool usage, freeze Advanced and Experimental outside the main solution

**Status:** PARTIAL: slice 2 for MoveMember, ChangeSignature and MoveAllTypesToFiles: tools promoted 2026-11-10 into Tools.Basic/CoreRefactoringTools (3f927bdf; Basic claude-lean budget raised to 45000 as a result), 3 of the MoveMember test classes moved (65dc8021), the rest of the test move is open in TODO.md (no MoveFile tool; engines were already in Engines.Basic: BasicRefactoringEngine and MemberRefactoringEngine). MoveType still pending (needs StructuralRefactoringEngine.MoveTypeToOuterScopeAsync moved into Engines.Basic). Other slices not started; slices 3-4 need rewording (see below).

**Refreshed 2026-11-10.** Originally PROPOSED 2026-10-19. Updated in place for these changes since then:
`RoslynSentinel.Server.Basic` no longer exists (folded into `Server.Advanced`, which is now the only server
project and one binary hosting both flavors; see `docs/current/references/reference_architecture_map.md`),
so slices 3 and 4 are obsolete as written. The usage totals are refreshed (59,595 calls over 191 journal
files, per the Decisions section); per-tool counts, line counts and the blast-radius window below were
NOT recomputed and stay "as of 2026-10-19". ChangeSignature, MoveMember, SemanticFindReplace,
InspectSymbol, UsingDirective and GetDiagnostics are already in the claude-lean core list
(`ToolClassRegistry.ClaudeLeanToolNames`, `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs:49`; since
commit 7162d29b). Of the originally used Advanced tools, only MoveType stays in `Tools.Advanced` for now.
The Decisions section at the end is the owner ruling and is unchanged.

## Motivation

Only a small part of the tool surface gets used, but core changes still have to keep all of it compiling
and green. The Basic/Advanced split was meant to contain that blast radius, but it sorted tools by how
sophisticated they seemed, not by whether they are used. Measured on 2026-10-19 (per-tool counts, tool
counts, line counts and the commit window below are from that date unless marked refreshed):

**Usage.** Original source: 88 `.claude/journal/*.calls.jsonl` files, 22,589 calls, about 2026-10-01 to
2026-10-20. The journal has since grown to 191 files and 59,595 calls (2026-11-09 review); the per-tool
counts below were not recomputed, so treat them as the 2026-10-19 snapshot. Slice 1 exists to redo this
cheaply.
- Advanced: 3 of 54 tools were ever called. MoveMember 26, SubAgent 17, MoveType 4. Today
  `docs/generated/architecture_tools.md` lists 52 Tools.Advanced, 62 Tools.Basic and 4 Tools.Experimental
  rows (row count by class and tool name; approximate, includes tools whose home changed since 10-19).
- Experimental: 0 of 4 tools were called (`CodeTransformTools`).
- Basic: 25 of 61 tools were never called. ConstructorParameter, CreateProject, Declaration,
  DeleteUnusedSymbol, ExtractLocalVariable, ExtractMethod, Features, GetBestInsertionPoint,
  GetOperationDetail, GetTypeInfo, GetWorkspaceHealth, GitRemote, ListProjectFrameworkTargets,
  ModifyBaseType, ModifyEnum, NamedArguments, ProjectDoc, QualifyCrefs, QuerySymbolRelationships,
  RetryFailedChanges, SemanticFindReplace, SplitProjectByFolder, SummaryComment,
  SyncTypeAndFilename, TypedMember.
- Top tools: ReadFile 6570, Search 5110, ReplaceSnippet 1989, RunTest 1666, Git 1590,
  GetMethodSource 1269, Build 763, Member 744.
- Caveat: the journal covers Claude sessions only, not model-eval or PlanStepRunner runs.

**Reachability.** From `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs` (the registry moved there when
Server.Basic was folded in; the counts are as of 2026-10-19):
- 40 of the 54 Advanced tools cannot be reached from the `claude` or `claude-lean` modes. They are
  Asyncify 15, Intelligence 7, Scan 6, Quality 5, Generation 4, Commenting 1, Modernization 1 and
  SubAgentEval 1.
- `ClaudeLeanOnDemandToolClasses` (lines 82-88) brings in AdvancedRefactoringTools and SubAgentTools.
  Experimental is reachable only through `ExperimentalMode` (declared line 26, mapped line 124).

**Size.** In lines of code, excluding bin, obj and Worktree. As of 2026-10-19: the table predates the
Server.Basic fold, so its "Server.Basic" production lines now sit inside `Server.Advanced`, and the
core/Advanced split of that project is no longer clean (see "Why Advanced cannot be unlinked").

| Side | Production | Tests |
| --- | --- | --- |
| Core: Common, Engines.Basic, Tools.Basic, Server.Basic | 72,014 | 47,200 (Tests, Tests.Basic, Tests.Tools.Basic, Tests.Battery.Basic) |
| Advanced: Engines.Advanced, Tools.Advanced, Tools.Experimental, Server.Advanced | 40,261 | 45,253 (Tests.Advanced, Tests.Tools.Advanced, Tests.Battery.Advanced, Tests.Asyncify) |

**Blast radius.** From the last 150 commits, 9781f616..38d37dbf.
- 59 of 150 commits (39%) touched Advanced production code or tests.
- 35 of those also touched core production code, so a core change pulled Advanced along.
- Examples:
  - ca632e11: the `*Tool` suffix rename touched 183 files, including 14 Tools.Advanced files.
  - 31ecb16e: merging the RemoveNodeFormattedAsync copies touched `AdvancedStructuralEngine.cs`.
  - 96fecdb2: snippet-path ambiguity touched `AdvancedStructuralEngine.cs`.
  - 5fcf1230: Whitespace C5a touched `IDEStyleEngine.cs`.
- Causes of the Advanced-side edits in those 35 commits. Counts are stat-based and approximate.
  About 15 commits were read at hunk level.

  | Cause | Advanced lines | Fixup |
  | --- | --- | --- |
  | New tests for Basic engines written into Advanced test projects (e.g. 27a02182, 82404802) | ~3,500 | Not forced; misplaced |
  | Rename sweeps (ca632e11 `*Tool` suffix, 2db3c5ac `ReplaceNodeFormattedAsync`) | ~830 | Mechanical |
  | Formatting-policy migration: 41 of 44 ratchet call sites were in Advanced engines (ccfc44c7) | ~500 | Swaps mechanical, trivia handling judgement |
  | Duplicate private format helpers collapsed into `Common` (42e57d2d, 31ecb16e, bad7a67e) | ~210 | Mechanical |
  | Exact-string test assertions updated after output changes (680a64bc, 92f4275d) | ~100 | Mixed |
  | MoveMember tool wrapper in `Tools.Advanced/AdvancedRefactoringTools.cs:459` over its engine in `Engines.Basic/MemberRefactoringEngine.cs:3490`; each new parameter edits both (43f20fe1, 8895fd7f, 219b3e21) | ~46 | Mechanical |

- Shared result types caused no collateral in this window. `Common/DocumentEditResult.cs` and
  `ToolErrorMapper` were unchanged. `EngineResultBase` and `ToolException` gained only optional
  members (96fecdb2, 7f20588f).
- The same causes create churn inside core.
  - In ca632e11, Advanced took about 23% of the lines.
  - Two of the three `RemoveNodeFormattedAsync` copies were in Basic.
  - Nearly every Whitespace commit also changed Tests.Basic.
  - So the split removes Advanced's share of the churn, not its cause. See "Reduce fixup cost in core"
    below.

**Why Advanced cannot be unlinked as-is.**
- Some core tools live in Advanced:
  - MoveMember and SubAgent are in use, and MoveType (4 calls). MoveMember and ChangeSignature are in the
    claude-lean core list but their tool class (`AdvancedRefactoringTools`) is still in Tools.Advanced;
    that is what slice 2 is moving (in progress).
  - ChangeSignature is named in the `implementer`, `implementer-sweep`, `orchestrator` and
    `journal-improvement-planner` agent docs.
  - The `moveExtract` toolset in `RoslynSentinel.Common/ToolsetCatalog.cs` mixed 12 Advanced and
    11 Basic tools on 2026-10-19 (not recounted).
- `Server.Advanced` is now the only server project, so it is not a freezable "Advanced" project. It
  hosts both flavors: `ServiceRegistrationExtensionsBasic.cs` (`AddRoslynSentinelToolsBasic`) and
  `ServiceRegistrationExtensionsAdvanced.cs` (`AddRoslynSentinelToolsAdvanced`), and its csproj
  references Tools.Advanced and Tools.Experimental (`docs/generated/architecture_projects.md:11`). Slice 5
  therefore cannot move `Server.Advanced` to a frozen slnx; it has to cut those references and the Advanced
  DI block instead (see slice 5).
- Every test project that needs a server references `Server.Advanced` (Tests.Server, Tests.ModelEval,
  Tests.SubAgent, Tests.Integration, plus Tests.Advanced and Tests.Asyncify). Tests.Integration is not in
  the slnx.
- The running server is the Advanced binary, started with `claude-lean,subagenttools`; there is no Basic
  binary to switch to.

What works in our favour: Tools.Basic and Engines.Basic have no `<ProjectReference>` to Advanced or
Experimental (`docs/generated/architecture_projects.md:10,28`). The remaining links are class-name strings
in `ToolClassRegistry.cs` (lines 64, 70, 86-87, 96, 117, 124), `ToolsetCatalog.cs` (comments at 64 and 80)
and comments in `ServerStartupHelpers.cs:207-208` and `ServiceRegistrationExtensionsBasic.cs:200`, plus
the Server.Advanced project references above. The earlier line numbers (`ServerStartupHelpers.cs:46,123`,
`ServiceRegistrationExtensionsBasic.cs:202`) no longer match.

## Proposal

The idea: "core" means tools with measured or mission-critical use. Core lives in the Basic projects
and stays in `RoslynSentinel.slnx`. Everything else is frozen, not deleted.

### Slices, in order (each one builds green on its own)

1. **Tool usage report script.**
   - Add `scripts/Get-ToolUsageReport.ps1`. It reads `.claude/journal/*.calls.jsonl` plus model-eval
     and PlanStepRunner transcripts.
   - Output per tool: call count, failure count, last-used date, owning project and mode reachability.
   - It sorts each tool into a tier:
     - **core**: used, or required by the mission.
     - **supported**: unused but cheap to keep.
     - **frozen**.
   - This report is the evidence for every later slice. Re-run it monthly.
2. **Promote the used Advanced tools into Basic.** IN PROGRESS 2026-11-10 for MoveMember, ChangeSignature
   and MoveAllTypesToFiles (a new Tools.Basic class; their engines were already in Engines.Basic:
   `BasicRefactoringEngine` and `MemberRefactoringEngine`, so only the `AdvancedRefactoringTools` wrapper
   is split and the tests move). MoveType is pending: it needs
   `StructuralRefactoringEngine.MoveTypeToOuterScopeAsync` moved into Engines.Basic first.
   - Move MoveMember, MoveType and ChangeSignature, plus the engine code they need, into
     Tools.Basic and Engines.Basic. Add SubAgent if model-eval stays in core.
   - Their tests go to Tests.Tools.Basic and Tests.Basic. That includes the MoveMember slice tests
     now in `Tests.Tools.Advanced/BatteryTwentyFourTests.cs`.
   - Add a test-placement guardrail: a source-scan test that fails when an Advanced test project
     tests an `Engines.Basic` or `Tools.Basic` type.
     - Today nothing stops this, because Tests.Advanced references Engines.Basic.
     - About 3,500 lines of Basic-engine tests landed in Advanced projects this way.
   - Follow the migration-first route in CLAUDE.md: the new member goes beside the old one, the old
     one gets `[Obsolete]`, and it is removed last.
   - Open bug: cross-project MoveType (engine reorg leftovers, bug 2) travels with it.
3. **OBSOLETE as written (was: point server and eval tests at `Server.Basic`).** Server.Basic is gone;
   Tests.Server, Tests.ModelEval, Tests.SubAgent and Tests.Integration (and Tests.Advanced and
   Tests.Asyncify) already reference the one server project, `Server.Advanced`. Replacement, still
   to do and needing a plan: decide which of those tests need Tools.Advanced types at all. Any that do
   move to an Advanced test project, so that after slice 5 the server tests build without the frozen
   projects.
4. **OBSOLETE as written (was: switch the live launch to the Basic binary).** There is one
   `Server.Advanced` binary hosting both flavors, so there is no Basic binary to switch to. What
   remains: fix the launch args (`subagenttools` is not a registered mode name) and, at slice 5, make
   the same binary start without the Advanced tool classes.
5. **Freeze.** Needs rewording; the "move `Server.Advanced` out" step no longer applies.
   - Move these into a new `RoslynSentinel.Advanced.slnx`:
     - Engines.Advanced, Tools.Advanced and Tools.Experimental.
     - Tests.Advanced, Tests.Tools.Advanced, Tests.Battery.Advanced and Tests.Asyncify.
   - `Server.Advanced` stays in `RoslynSentinel.slnx` as the only server (OPEN: rename it, since
     "Advanced" no longer describes it). Cut its `<ProjectReference>`s to Tools.Advanced and
     Tools.Experimental and move the Advanced DI block
     (`ServiceRegistrationExtensionsAdvanced.cs`, `AddRoslynSentinelToolsAdvanced`) into the frozen
     solution or a frozen server project. The Basic DI block (`ServiceRegistrationExtensionsBasic.cs`)
     stays.
   - Remove them from `RoslynSentinel.slnx`. Tag the cut-off commit (`advanced-frozen-<date>`).
   - The frozen solution has no promise of compiling against a moving core. A tool comes back by
     being promoted through slice 2 with its tests, never by keeping Advanced green.
6. **Clean up core.**
   - Remove Advanced and Experimental class-name strings from
     `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs` (as of 2026-11-10: lines 64, 70, 86-87, 96,
     106, 117, 121, 124) and `ToolsetCatalog.cs` (comments at 64 and 80).
   - The single `ModeToToolClasses` map now registers inert names where a class is absent, so the old
     "Basic `claude` mode lists AdvancedRefactoringTools, which Basic cannot load" fix is moot as a
     bug. It stays on the list only as a name to remove once `AdvancedRefactoringTools` is gone.
   - Remove the Advanced test names from the known-failing baselines for example
     `AddConfigureAwaitFalse_*` and `ConvertToBackgroundService_*`. `docs/known-failing-tests.Basic.txt`
     no longer exists; `docs/known-failing-tests.Advanced.txt` and `.Solution.txt` do (re-check their
     contents first, see Open questions).
   - Drop the Advanced flavour from `scripts/build.ps1`, or point it at the frozen slnx.
   - Update CLAUDE.md: the Architecture section and the "Advanced is additive" line.
7. **Tier Basic itself** using the slice 1 report.
   - Move tools with zero calls and no mission case to frozen the same way, in batches.
   - Tools a weak model needs but Claude does not call stay, for example ExtractMethod and
     ExtractLocalVariable. The report has to show their model-eval usage.

### Reduce fixup cost in core (independent of the split)

These address the causes in the Motivation table. They apply after the split as well.

1. **One layer per feature.** A tool wrapper and its engine live in the same layer. Slice 2
   does this for MoveMember. The test-placement guardrail in slice 2 keeps tests next to their engine.
2. **Pass parameters as an options record.**
   - Tool-to-engine pass-through should use one request record per operation, not a growing
     positional parameter list.
   - Then a new parameter is one property, not a change to the wrapper, the engine signature and the tests.
   - Start with `MoveMemberAsync`, which gained `fieldMap`, `oldMember` and `forwarderReceiver` one at a time.
3. **Ratchet the single formatting entry point.**
   - The helpers now live in `Common` (`RoslynFormattingHelper`, 42e57d2d, bad7a67e).
   - `Tests.Basic/WhitespaceRatchetTests.cs` only stops the deleted helpers from coming back. Extend it
     to fail on any engine-local formatting or trivia-normalizing helper.
4. **Shared test source and assertion helpers.**
   - The `Src(bool crlf, params string[] lines)` helper is copy-pasted in 15 test files, for example
     `Tests.Advanced/RemoveNodeCharacterizationTests.cs:45`. Replace the copies with one helper in the
     shared Tests project.
   - It offers two assertions: byte-exact, for tests whose subject is formatting, and
     whitespace-insensitive, for everything else.
   - Result: a formatting-policy change only breaks the tests that are about formatting.

Rename sweeps (ca632e11, 2db3c5ac) are accepted. They are mechanical through `RenameSymbol`, and the
split reduces how many projects they touch.

Latent, not urgent: `new DocumentEditResult` appears about 320 times in Engines.Advanced and 358 times
in Engines.Basic, all with object initializers (`Common/DocumentEditResult.cs:22`). Its three static
factories are used twice (`StructuralRefinementEngine.cs:104,295`). Before any shape change to that
type, move construction behind factories first.

## Alternatives considered, not pursued

- **Unlink Advanced as-is.** Breaks the test projects that reference `Server.Advanced`, drops tools in
  active use and changes the running binary in one step. Slice 2 (and the reworded slice 3) are what
  make it safe.
- **Move unused tools into Tools.Experimental and split that out.**
  - Experimental references Engines.Advanced, and the cost sits in the engines (28.7K lines) and the
    tests (45K lines), not in the tool classes (9K lines in Tools.Advanced). Moving tool classes alone
    leaves the expensive part attached.
  - "Experimental" also means "might be promoted", which invites keeping it compiling. "Frozen" is
    the right meaning.
- **A separate repository for Advanced.** Harder to look at or bring back than a second slnx in the
  same repo with a tag. Revisit only if the frozen projects get in the way of searches or the
  generated docs.
- **Delete Advanced.** Throws away tested refactorings (Asyncify, Intelligence) that may be worth
  promoting later. Freezing costs nothing once it is out of the main solution.

## Open questions

- Which Engines.Advanced types MoveType depends on. For MoveMember, ChangeSignature and
  MoveAllTypesToFiles the engines were already in Engines.Basic (slice 2 in progress); MoveType needs
  `MoveTypeToOuterScopeAsync` moved.
- Is SubAgent (and the `modelEval` toolset) core? It depends on whether model-eval runs stay part of
  the regular workflow.
- How much do model-eval and PlanStepRunner runs use the 25 never-called Basic tools? Not measured;
  the journal covers Claude sessions only.
- Do the known-failing baseline entries (21 Basic and 10 Advanced on 2026-10-19) still fail? The Basic
  file is gone and the memory note says the test baseline is 0 failures; re-check the Advanced and
  Solution files before slice 6.
- Where the `claude-lean,subagenttools` launch args are set. Nothing in the repo sets them.
- Server.Advanced is the only server and hosts both flavors: rename it, or keep the name? (Affects slice 5.)
- Should `docs/generated/` keep listing the frozen projects?

## Cost / risk

- Slice 2 is the only part with real refactoring risk. MoveMember was just hardened
  (38d37dbf), so it should move with its tripwire tests.
- Slice 5 needs a server restart and a fresh chat, because the tool surface changes. It also now has to
  split the Advanced DI registration out of the only server project, which is more than a solution-file
  move.
- Once frozen, Advanced will stop compiling against core quickly. That is accepted: bringing a tool
  back means fixing it forward.
- When slice 5 lands, the build and test baselines and `scripts/build.ps1` flavours change together.
- Gain: about 85K lines (40K production, 45K tests) leave the main solution. Based on the 150-commit
  window, about 35 of every 150 commits stop pulling in Advanced edits.
- The split does not remove the causes. The same commits still churn core projects unless the
  "Reduce fixup cost in core" items land.

## Related

- `docs/generated/architecture_projects.md`, `docs/generated/architecture_tools.md`
- `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`, `RoslynSentinel.Common/ToolsetCatalog.cs`,
  `docs/current/references/reference_architecture_map.md`
- Memory: engine reorg leftovers (open bug 2, cross-project MoveType); server flavours and build configs
- Ideas: `docs/current/ideas/idea_tool_attribution.md`, `docs/current/ideas/closed/idea_server_telemetry.md`.
  Both overlap with the slice 1 usage report.

## Decisions (todo review 2026-11-09)

- This proposal is stale: Server.Basic is gone (its "run the Basic binary" step no longer exists), the
  usage counts are old (now 59,595 calls over 191 journal files), and ChangeSignature is already in the
  claude-lean core. First refresh it in place.
- Promote MoveMember, ChangeSignature and MoveAllTypesToFiles into a Tools.Basic class (READY, Medium).
  Why: their engines are already in Engines.Basic, so only the 1679-line `AdvancedRefactoringTools` class
  is split and their tests move. MoveType comes later (it needs
  `StructuralRefactoringEngine.MoveTypeToOuterScopeAsync` moved into Engines.Basic). Check: Advanced
  builds and tests pass, and the three tools resolve from Tools.Basic (`scripts/build.ps1 -Flavor Solution`).
- Split Advanced by cluster in vertical slices (tools + engines + tests), Asyncify first
  (`Tests.Asyncify` already exists), then Analysis (Intelligence, Scan, Quality, Modernization tools).
  Keep the namespaces (66 files import `RoslynSentinel.Tools.Advanced`). Cost per new project: a
  csproj, references from Server.Advanced and the test projects, a DI block in
  `ServiceRegistrationExtensionsAdvanced.cs`, regenerated docs. Needs a plan doc first (OPEN). Why: the
  weight is in Engines.Advanced (33 files) and its tests, so a tools-only split buys little.

## Decisions (todo review 2026-11-10)

- Test-move remainder in two steps. Now (READY, Medium): split the ChangeSignature/MoveMember tests out of
  the 5 mixed files (BatteryTwentyFourTests, DeclarationModifierTests, AmbiguousNameTargetToolTests,
  MoveTypeAndExtractTests, DryRunLineChangesTests) into Tests.Tools.Basic with `Member(addMember)` +
  `Member(remove)` (cross-project MoveMember would refuse: shared fixtures stay behind). Later: move
  `MoveMemberSuccessTests` and `MoveMemberToolCollateralRefusalTests` whole once MoveFile slice 1 exists
  (`proposal_move_file_tool.md`). Why: MoveFile now waits for a fresh-chat design; the mixed files do not
  need it.
- Check: the 7 `// TODO` markers are gone, Tests.Tools.Advanced has no MoveMember/ChangeSignature tests,
  the build is green and no new test failures.

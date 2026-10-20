# Split the solution on measured tool usage, freeze Advanced and Experimental outside the main solution

**Status:** PROPOSED 2026-10-19. Awaiting review; nothing built.

## Motivation

Only a small part of the tool surface gets used, but core changes still have to keep all of it compiling
and green. The Basic/Advanced split was meant to contain that blast radius, but it sorted tools by how
sophisticated they seemed, not by whether they are used. Measured on 2026-10-19:

**Usage.** The source is 88 `.claude/journal/*.calls.jsonl` files, 22,589 calls, about 2026-10-01 to 2026-10-20.
- Advanced: 3 of 54 tools were ever called. MoveMember 26, SubAgent 17, MoveType 4.
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

**Reachability.** From `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`:
- 40 of the 54 Advanced tools cannot be reached from the `claude` or `claude-lean` modes. They are
  Asyncify 15, Intelligence 7, Scan 6, Quality 5, Generation 4, Commenting 1, Modernization 1 and
  SubAgentEval 1.
- `ClaudeLeanOnDemandToolClasses` (lines 80-87) brings in AdvancedRefactoringTools and SubAgentTools.
  Experimental is reachable only through `ExperimentalMode` (line 157).

**Size.** In lines of code, excluding bin, obj and Worktree.

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
  - MoveMember, MoveType and SubAgent are in use.
  - ChangeSignature is named in the `implementer`, `implementer-sweep`, `orchestrator` and
    `journal-improvement-planner` agent docs.
  - The `moveExtract` toolset in `RoslynSentinel.Common/ToolsetCatalog.cs` mixes 12 Advanced and
    11 Basic tools.
- Four test projects reference `Server.Advanced`: Tests.Server, Tests.ModelEval, Tests.SubAgent and
  Tests.Integration. Tests.Integration is not in the slnx.
- The running server is the Advanced binary, started with `claude-lean,subagenttools`.

What works in our favour: Tools.Basic and Server.Basic have no `<ProjectReference>` to Advanced or
Experimental (`docs/generated/architecture_projects.md:12,27`). The only links are class-name strings in
`ToolClassRegistry.cs`, `ToolsetCatalog.cs:63`, `ServerStartupHelpers.cs:46,123` and
`ServiceRegistrationExtensionsBasic.cs:202`.

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
2. **Promote the used Advanced tools into Basic.**
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
3. **Point server and eval tests at `Server.Basic`.**
   - Tests.Server, Tests.ModelEval, Tests.SubAgent and Tests.Integration.
   - Any test that really needs Advanced moves to an Advanced test project.
4. **Switch the live launch to the Basic binary.** Fix the launch args too: `subagenttools` is not
   a registered mode name.
5. **Freeze.**
   - Move these into a new `RoslynSentinel.Advanced.slnx`:
     - Engines.Advanced, Tools.Advanced, Tools.Experimental and Server.Advanced.
     - Tests.Advanced, Tests.Tools.Advanced, Tests.Battery.Advanced and Tests.Asyncify.
   - Remove them from `RoslynSentinel.slnx`. Tag the cut-off commit (`advanced-frozen-<date>`).
   - The frozen solution has no promise of compiling against a moving core. A tool comes back by
     being promoted through slice 2 with its tests, never by keeping Advanced green.
6. **Clean up core.**
   - Remove Advanced and Experimental class-name strings from `ToolClassRegistry.cs` (lines 70,
     84-85, 97, 140-157) and `ToolsetCatalog.cs`.
   - Fix the Basic `claude` mode entry. It lists AdvancedRefactoringTools, which Basic cannot load.
   - Remove the Advanced test names from `docs/known-failing-tests.Basic.txt`, for example
     `AddConfigureAwaitFalse_*` and `ConvertToBackgroundService_*`.
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

- **Unlink Advanced as-is.** Breaks four test projects, drops four tools in active use and changes
  the running binary in one step. Slices 2-4 are what make it safe.
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

- Which Engines.Advanced types MoveMember, MoveType and ChangeSignature depend on. This sets the
  size of slice 2. Not yet traced.
- Is SubAgent (and the `modelEval` toolset) core? It depends on whether model-eval runs stay part of
  the regular workflow.
- How much do model-eval and PlanStepRunner runs use the 25 never-called Basic tools? Not measured;
  the journal covers Claude sessions only.
- Do the 21 Basic and 10 Advanced known-failing baseline entries still fail?
- Where the `claude-lean,subagenttools` launch args are set. Nothing in the repo sets them.
- Should `docs/generated/` keep listing the frozen projects?

## Cost / risk

- Slice 2 is the only part with real refactoring risk. MoveMember was just hardened
  (38d37dbf), so it should move with its tripwire tests.
- Slice 5 needs a server restart and a fresh chat, because the tool surface changes.
- Once frozen, Advanced will stop compiling against core quickly. That is accepted: bringing a tool
  back means fixing it forward.
- When slice 5 lands, the build and test baselines and `scripts/build.ps1` flavours change together.
- Gain: about 85K lines (40K production, 45K tests) leave the main solution. Based on the 150-commit
  window, about 35 of every 150 commits stop pulling in Advanced edits.
- The split does not remove the causes. The same commits still churn core projects unless the
  "Reduce fixup cost in core" items land.

## Related

- `docs/generated/architecture_projects.md`, `docs/generated/architecture_tools.md`
- `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`, `RoslynSentinel.Common/ToolsetCatalog.cs`
- Memory: engine reorg leftovers (open bug 2, cross-project MoveType); server flavours and build configs
- Ideas: `docs/current/ideas/tool_attribution_idea.md`, `docs/current/ideas/server_telemetry_idea.md`.
  Both overlap with the slice 1 usage report.

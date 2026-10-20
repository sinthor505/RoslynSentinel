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
- The coupling comes from shared core types. These are text-match counts, not symbol references:
  - `DocumentEditResult`: 403 matches in 17 Advanced files.
  - `ToolException`/`ResultError`/`ToolErrorMapper`: 202 matches in 14 files.
  - `RoslynFormattingHelper`: 57 matches in 16 files.

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
   - Their tests go to Tests.Tools.Basic and Tests.Basic.
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

### Narrow the seam (alongside the slices, lower priority)

Core-change ripples spread because tools and engines use `DocumentEditResult`,
`RoslynFormattingHelper` and the error types directly.
- Put a small, stable edit/result layer in `Common` that tools go through, so internal changes (whitespace,
  anchor resolution) stop reaching tool classes.
- Move test weight towards contract tests at the tool boundary (schema plus result shape) and away
  from tests on engine internals. Today the Advanced side has more test code than the Basic side.
- This needs its own design doc before it is built.

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

## Related

- `docs/generated/architecture_projects.md`, `docs/generated/architecture_tools.md`
- `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`, `RoslynSentinel.Common/ToolsetCatalog.cs`
- Memory: engine reorg leftovers (open bug 2, cross-project MoveType); server flavours and build configs
- Ideas: `docs/current/ideas/tool_attribution_idea.md`, `docs/current/ideas/server_telemetry_idea.md`.
  Both overlap with the slice 1 usage report.

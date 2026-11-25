# Multi-file MoveFile and a namespace-change tool: move a connected set across a project edge, and change its namespace, in one compile-gated call

**Status:** ACCEPTED 2026-11-25. Open questions settled with the owner (see Decision, which overrides the Proposal text where they differ); plan to be written as `plans/plan_multi_file_movefile_and_namespace_change_tool.md`.

## Motivation

The MCP tools deliberately trade raw speed for compiler safety: every write is compile-gated, no tool leaves a red
intermediate state, and that is what lets cheap agents and concurrent sessions work without breaking each other. This
proposal closes two gaps without giving up the gate. Both were hit while running
[plan_isolate_git_area_into_own_project](../plans/closed/plan_isolate_git_area_into_own_project.md), in a tool nobody had used
(`SplitProjectByFolder`, a stub until bf27d721 made it throw `NotImplemented`) and in a tool that is only days old
(`MoveFile`, cross-project since 2026-11-22). Both are environment gaps: the tool surface had no one-call route for a
connected cluster, so the run fell back to hand-edited project files.

**Gap 1: no atomic multi-file move.** `MoveFileTool` takes one `sourcePath` and one `destinationPath`
(`RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs:290-433`); a directory is refused with "this tool moves single
files" (`:329-331`), and a comma list resolves to a nonexistent path and returns `NotFound`. The compile gate validates
one file per call. Files that reference each other have no valid order across a project edge: the plan's spike
(Appendix B, Step 12) measured `GitResult` before its derived records 1391 errors, `GitProcess` before its readers 45,
`Libgit2GitReader.Hooks.cs` alone (one of 8 partial-class files) 178 (CS0103, CS0117, CS0433), all refused with the
text "would break the build; nothing was moved". Counts only were recorded, not the per-error lists, so the exact
error text is unverified here (see the
[blocker](../blockers/blocking_error_movefile_has_no_multi_file_cross_project_move.md)). A partial class cannot be
split across two assemblies at all, so for it no order exists.

What worked instead, per the plan's Done lines: a hand-written `.csproj` for the new project that compiles the old
folder through a temporary `<Compile Include="..\RoslynSentinel.Common\Git\**\*.cs" LinkBase="Git" />` stand-in (Common
gets a matching `<Compile Remove>`), then one `MoveFile` per file inside that one project, then removal of the stand-in.
Production side (Step 14): 99a8d79e, 197df57b (30 files), dcd159f1 (last 30 files, stand-in removed): 60 calls.
Test side (Step 18): 88975f2d, b8a7dc82 (30 files), ee4e95bc (31 files): 61 calls. That is 121 `MoveFile` calls for what
are two folder moves, at 15-20 s each (Appendix B), so roughly 30-40 minutes of gate time, plus one shell
`dotnet restore RoslynSentinel.slnx` before the first `LoadSolution` of each brand-new csproj ("Unable to find project
information for 'RoslynSentinel.Git.csproj'"). The csproj/slnx hand edits were an owner-authorized bypass (plan D-e);
a weak model or an unattended run has no such route.

**Gap 2: no tool changes a namespace with its reference sites.** After the move the Git types still declare
`RoslynSentinel.Common` (plumbing, about 40 files), `RoslynSentinel.Tools.Basic` (engine and DTOs, about 35) and
`RoslynSentinel.Common.GitWorktree` (3) (TODO.md, "No tool changes a namespace across files"). Step 19 did
GitImpl -> GitEngine with `RenameSymbol` (118 sites, 3 projects, 23 files, b792a478) but left the namespace rename as a
TOOL-GAP with `// TODO rename namespace to RoslynSentinel.Git` at `RoslynSentinel.Git/GitEngine.cs:13` and
`RoslynSentinel.Git/GitProcess.cs:7`. What exists, all insufficient:

- `RenameSymbol` on a namespace (`BasicRefactoringEngine.cs:1635`, tool text "Namespace: docCommentId N:Ns.Name,
  newName = new last segment") renames the whole namespace by its last segment through `Renamer.RenameSymbolAsync`. It
  cannot move a subset of a namespace's types (the engine files share `RoslynSentinel.Tools.Basic` with unrelated
  types) and cannot change a parent segment.
- `MoveFile namespaceHandling: update` is declared in the enum (`ToolEnums.cs:154`) and refused as "not supported yet"
  (`MoveFileTool`); the design is a TODO.md line, NOT DESIGNED, with partial types, multi-type files and global usings
  undesigned ([proposal_move_file_tool](./proposal_move_file_tool.md) item 3).
- `SolutionStructureEngine.FixMismatchedNamespacesAsync` (`ProjectStructureEngine.cs:20`, reached only from
  `Tools.Experimental/CodeTransformTools.cs:156`) edits the namespace declaration of one file and updates no caller, so
  it leaves the solution red.
- `PreviewMoveFileToNamespaceFolder` (`Tools.Advanced/IntelligenceTools.cs:207`, inactive) only reports the folder.

Adjacent, not covered here: `MoveType`/`MoveAllTypesToFiles` give one file per type, reformat the whole source file and
once left `// Added by AddTopLevelType` marker comments (INBOX 2026-11-24 19:45; TODO.md "MoveType cannot move a type
into an existing file"). They are a separate defect family.

## Proposal

Three parts, in this order of build. Nothing here is decided; "Recommended" marks the author's choice for review.

### 1. Multi-file MoveFile (extend `MoveFile`, do not add a tool)

**Shape (recommended).** `sourcePath` may name a folder; `destinationPath` is then the target folder. One new optional
parameter `include` (list of globs relative to the source folder, default every file except `bin/`, `obj/` and project
files) selects a subset, which covers the test move (61 files of `Git*.cs` plus four other names out of a folder of
thousands). `dryRun` (the existing `ToolOptionTag.DryRun` pattern, as on `RenameSymbol`) returns the expanded
source/destination pair list and the gate result without writing. A single file is the degenerate set of one: the
validations now inline in `MoveFileTool` (same path, directory, missing, destination exists, NUL byte, strict UTF-8)
move into an Engines.Basic planner used by both forms, so the two cannot drift. No separate `targetProject`: the
destination path decides the project (`SolutionProjectLocator.FindContainingProject`, longest csproj-directory prefix),
and the result names both ends exactly as the single-file message does today. The folder structure under the source is
preserved under the destination, subfolders included.

**One change set, one gate, one undo id.** `ValidateAndApplyHelper.ValidateAndApplyAsync` already takes a
`DocumentChangeSet` of many files plus `deletePaths`, concatenates the deletes into the removed-document list before the
compile, writes with `rollbackOnPartialFailure: true`, and mints one 8-hex `changeId` with one operation blob, so a
set needs no new apply machinery; `MoveFile(copyAndObsolete)` already applies a multi-file set through it
(`MoveFileCopyAndObsolete.RunAsync`). `ValidationEngine.ValidateChangesAsync(Solution, ...)` (`ValidationEngine.cs:137`)
removes every `deletePaths` document, adds each new `.cs` path to the project containing it, compiles every affected
project plus its transitive dependents and reports only newly introduced errors against the baseline. For a connected
cluster that means one compile sees both halves of the move at once: this is what removes the "no valid order" problem.
Cost is one gate per call instead of N; the per-call cost is probably dominated by compiling the affected projects and
dependents rather than by file count (hypothesis, not measured; a 60-file dry run would settle it).

**All-or-nothing eligibility.** Any file that is binary, non-UTF-8, missing, or whose destination exists (case
insensitive) refuses the whole set, and the refusal lists every offender, not the first. Bytes are written verbatim as
for the single file (`verbatim: true`: BOM kept as a leading U+FEFF, no EOL normalization), so EOL and encoding are
preserved per file. Per pair the call still records `FileMoveMemory.Shared.Record(source, destination, "MoveFile")` and
one `EditEffectKind.FileMoved` effect, so edit attribution and "a later read of the old path is told the new one" keep
working; the stale-read policy (`RefuseIfObservedStaleQuiet`) applies to each file.

**Namespaces.** `namespaceHandling` stays `keep` only for a set (default behaviour, text unchanged). `copyAndObsolete`
is per-file by design (it refuses a partial type with parts in other files, `CopyAndObsoleteChecks.cs:81-103`) and
`update` is part 2; both are refused for a set with a message that names part 2. The existing kept-namespace fact
(`DescribeKeptNamespace`, via `FolderNamespace.Expected`) is reported once per distinct mismatch, not per file.

**References, `InternalsVisibleTo`, packages (recommended: facts, no silent csproj edits).** The gate's candidate
solution is the baseline with documents moved; it cannot see a csproj edit (an existing csproj is not a document, and
`ValidateChangesWithCandidateAsync` skips non-`.cs` new files), and a csproj write triggers a full reload
(`PersistentWorkspaceManager.cs:1680-1688`, `RequiresFullReload`). So the prerequisites are made before the move with
ordinary additive csproj edits, which are green by themselves (new project: `ProjectReference` to the source project,
the package reference, IVT lines), and the move then needs no csproj change. When the gate refuses, the error carries
facts and options as the repo's tool-error policy requires: CS0122 already gets a hint
(`CompilerErrorLookupHelper.cs:291`); the set form adds the missing reference as a fact ("the destination project has no
ProjectReference to the project of X"; "a ProjectReference from A to B would close a cycle") and the option, never the
edit. An opt-in `addProjectReferences` parameter is not recommended for the first slice (Open question 2).

**Destination must be a loaded project.** If the destination folder is inside no loaded project, the set refuses with a
fact naming that. This closes a hazard that exists today for a single file: `ValidationEngine` skips a new file that
belongs to no project ("New file does not belong to any project, skipping in-memory validation"), so a `MoveFile` into a
folder whose csproj is not yet loaded passes the gate while the destination half is never compiled
(`CopyAndObsoleteChecks.cs:51` already refuses this case for `copyAndObsolete`; `keep` does not). For a brand-new
project the options are `CreateProject` or a hand-written csproj plus slnx entry plus restore (Open question 4).

**Limits and result.** A soft cap of 200 files per call, refusal naming the option to split (measured need so far: 61;
the cap is a guess, Open question 3). The result is the existing `AppliedChangeSummary` (`AffectedFiles` holds both
paths of every pair, `LineChanges`, `ChangeId`, `Findings`); the tool never trims or pages it, the result pipeline
does (`docs/current/references/reference_tool_response_pipeline.md`), and the `Description` carries the summary (counts
per source and destination project, kept-namespace facts, the facts the planner collected). A refusal reuses the
`DescribeEditImpact`/`CompilerErrorLookupHelper` text and adds the blocker doc's third suggestion: group errors by the
set file they bind to, so the answer to "which files must move together" is in the message, not a bare count.
`UndoChange(changeId)` reverts the whole set. Empty source folders are left in place (git does not track them); the
description says so.

### 2. Namespace-change tool

**What it does (recommended).** `ChangeNamespace(filePath, newNamespace | matchFolder, include?, dryRun)`: `filePath`
is a file or a folder (all `.cs` files under it, `include` as in part 1); `newNamespace` is explicit, or `matchFolder:
true` derives it per file with the shared convention `FolderNamespace.Expected` (the project's root namespace plus the
folder path; the one implementation behind `FixMismatchedNamespacesAsync` and the MoveFile kept-namespace fact), so the
"sync namespace to folder" case is a mode, not a second tool. For every top-level type of the set it: rewrites the
declaration in its own file, switches every reference found by symbol across all projects to the new namespace (using
directive added and, when unused, the old one removed; fully qualified and partially qualified names, `global::`,
alias directives, `cref`, `nameof`, `typeof`, attribute, base-list and generic-argument positions), and handles a
`global using` of the old namespace by qualifying instead of adding a using. Types of the set that reference each other
are rewritten in the same pass. All edits go into one `DocumentChangeSet`, are compiled once, applied through
`ValidateAndApplyHelper` and share one `changeId`. A partial type must have all its parts in the set (the 8-file
`Libgit2GitReader*.cs` case is exactly that, and is why a per-file `update` cannot work: parts in two namespaces are two
different types); a file whose top-level types sit in several namespaces is refused with facts and options.

**Build it on what the repo already has (recommended) rather than on Roslyn's code action.** The engine in
`Engines.Basic/CopyAndObsoleteCallerRewrite.cs` (`RewriteAsync`, `:67`) already finds callers by
`SymbolFinder.FindReferencesAsync` over the solution and rewrites each file by byte-preserving text splices, with a
`Using` and a `Qualify` mode, global-using detection, alias and partial-qualification handling, and name-conflict
checks; `CopyAndObsoleteEdits` rewrites the file's own namespace; `CopyAndObsoleteMigration` repeats compile and
per-file revert up to `MaxCompileRounds = 3`. Two changes are needed, both to be confirmed by reading the code when the
work is planned: (a) it takes one source file and assumes the old types stay behind as `[Obsolete]` copies (so it
qualifies any caller that sits inside the old namespace, because enclosing-namespace members bind before usings); for a
move the old types are gone and a using is safe, so a "moved, not copied" mode is added; (b) it excludes the source file
as a caller and handles one file's type list, so it is generalised to a set whose members are rewritten once for both
roles. Extract a shared core rather than fork it.

**Why not the Roslyn Features refactoring.** The repo references only `Microsoft.CodeAnalysis.CSharp.Workspaces` and
`Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.12.0 (`RoslynSentinel.Common.csproj:22-23`); no `Features` assembly, no MEF
host composition (the two `MSBuildWorkspace.Create` calls, `PersistentWorkspaceManager.cs:893` and `:3473`, use the
default host services). Roslyn's change-namespace service ("Change namespace to match folder", in
`Microsoft.CodeAnalysis.Features`) is, to the author's recollection, an internal MEF language service wired for IDE
hosts, not a public API: unverified, to be confirmed against the package before this alternative is dismissed on that
ground alone. Independent of that, adopting it means a new large dependency, MEF composition changing workspace startup
for every tool, and output formatting we do not control, against a write path whose rule is byte-exact EOL/BOM and a
collateral-change tripwire (`CollateralChangeGuard`).

**Placement (recommended).** A new `ChangeNamespace` method on the existing `CoreRefactoringTools` class
(`MoveAllTypesToFiles` already lives there, `CoreRefactoringTools.cs:416`), in the on-demand `moveExtract` toolset
(`ToolsetCatalog.cs:43`), engine in `Engines.Basic`. Consequences: no new `[McpServerToolType]` class, so no
`ToolClassRegistry` row and no fresh-chat requirement (a restart is enough); zero startup tokens because
`ClaudeLeanToolNames` (`ToolClassRegistry.cs:55`) lists only core tools and `ChangeNamespace` would not be among them;
the cost is one name in the `moveExtract` list, its one-line description in `ToolsetControlTools.cs:26`, and an
`ErrorPolicyIntentTable` row (`MoveFile` is `Fixed(E)`, `:120`). `RenameSymbol` on a namespace stays as it is. Once the
tool exists, `MoveFile namespaceHandling: update` becomes the composition "move the set, then change its namespace in
the same change set" and can be built as a thin follow-up; until then `update` stays refused.

### 3. SplitProjectByFolder

With part 1, the old goal ("move a folder into a new project") is `CreateProject` (or a hand-written csproj with the
references and package) plus one `MoveFile(folder)`; with part 2, plus one `ChangeNamespace`. Recommended: leave
`SplitProjectByFolder` throwing `ToolNotImplementedException` (`ProjectStructureEngine.cs:1854`, bf27d721) until part 1
ships, then remove it from the `projectAdmin` toolset and the tool class (consolidation) and document the recipe,
rather than implement it as a wrapper. A split tool must also decide project references, IVT lines, package moves and
the slnx entry, which is exactly what part 1 refuses to do silently; those belong to `CreateProject` parameters
(`references`, `packages`, `internalsVisibleTo`), a separate small proposal. Removing it drops one
`ErrorPolicyIntentTable` row (`:145`), one `ToolsetCatalog` name (`:51`) and the generated tool tables entry.

## Alternatives considered, not pursued

1. **Keep the link stand-in recipe as the documented procedure.** It works (Steps 14 and 18, no refusals, 121 calls) and
   needs no code. It lost as the end state because it hand-edits csproj outside the gate, takes 30-40 minutes of gate
   time for two folder moves, and a weak model cannot derive it (the plan needed an owner-authorized bypass). Still
   worth writing as a `references/` doc as the interim route; that is cheap and independent of this proposal.
2. **Shell `git mv` plus csproj edits.** Loses the compile gate, the edit-attribution record, `UndoChange` and the
   stale-read guard; it is the "reach for a shell command" failure the mission names as a tool-surface gap.
3. **Per-file `MoveFile` with temporary `[Obsolete]` forwarding types** (the CLAUDE.md migration-first pattern;
   `copyAndObsolete` is that pattern for a file). It cannot cover the failing shapes: a partial class cannot have
   forwarders in a second assembly, `copyAndObsolete` refuses a type with parts in other files
   (`CopyAndObsoleteChecks.cs:81-103`), duplicated types in two referenced assemblies give CS0433, and a base type with
   derived records needs its whole hierarchy forwarded. It also doubles the work (copy, then delete).
4. **Open a scoped operation ledger and allow the red intermediate state** (`ScopedOperationLedgerEngine`, `TryOpen`
   at `:9`). It tracks unresolved entries, but it relaxes exactly the guarantee that makes the surface safe for cheap
   agents, allows one open ledger at a time, and `IsBlocked` refuses edits to unrelated files while entries are
   unresolved. One atomic gate keeps the guarantee instead of suspending it.
5. **Make `SplitProjectByFolder` do the move itself** (the blocker's suggestion 2). It would duplicate the set logic and
   still need reference/IVT/package decisions; part 3 recommends the composition instead.
6. **Per-file `MoveFile update`** (the declared-but-unbuilt mode). Infeasible for partial types (parts in different
   namespaces are different types) and one gate per file again; part 2 operates on sets.
7. **`RenameSymbol` on the namespace.** See Motivation: last segment only, whole namespace only.
8. **A new tool `MoveFiles`.** A separate name in a gated toolset costs no startup tokens, but a model that tries the
   comma list or the folder on `MoveFile` (the failure measured in the blocker) would still get the old refusal; the fix
   belongs where models already go. The price of extending is that `MoveFile` is a core tool (in
   `ClaudeLeanToolNames`), so every claude-lean session pays for two added parameters (`include`, `dryRun`), unmeasured.

## Open questions

1. **Parameter shape.** Options: (a) folder in `sourcePath` plus `include` globs (recommended); (b) a `moves` list of
   `{sourcePath, destinationPath}` pairs like `ReplaceSnippet`'s `batchEdits`, allowing renames on the way and sets
   spread over folders; (c) both. (a) covers every move in this run; (b) adds an either/or required-parameter pair that
   needs the conditional-parameter review. Recommendation: (a) now, (b) only when a concrete use needs per-file renames.
2. **Should the tool add `ProjectReference`/IVT/package edits?** Options: never (recommended for the first slice; facts
   and options only); opt-in `addProjectReferences`; always. The repo rule that a parameter added to stop a known model
   failure is mandatory does not apply: the known failure is a red gate, and the facts remove it without an edit.
   Revisit if the recipe (CreateProject with references, then MoveFile) proves too many steps in practice.
3. **Cap on files per call.** Options: none, 200 (recommended), 500. Needed to bound gate time and memory; the only data
   point is 61 files. Settle with a measured 60-file dry run.
4. **Brand-new project and restore.** `LoadSolution` failed after a hand-written csproj until a shell `dotnet restore`
   ran (no restore tool; a minor bypass). Options: have `CreateProject` restore (whether `dotnet new` already does is
   unverified; its minimal output, `Class1.cs` and slnx line, is traced in the SplitProjectByFolder blocker); name
   `dotnet restore` as an option in the "destination inside no loaded project" fact (recommended); add a restore tool.
5. **`ChangeNamespace` versus `MoveFile update` as the home.** Options: new tool first, `update` as composition later
   (recommended); `update` only, which would not cover a namespace change without a file move (Step 19: files already
   in the right project, in a flat folder, with the wrong namespace; `MoveFile` refuses source equal to destination).
6. **One namespace per call or prefix mapping.** Step 19 needs three source namespaces mapped to a family
   (`RoslynSentinel.Common`, `RoslynSentinel.Tools.Basic`, `RoslynSentinel.Common.GitWorktree` to `RoslynSentinel.Git`
   and `RoslynSentinel.Git.GitWorktree`). Options: one `newNamespace` per call, three calls (recommended); a
   `fromPrefix`/`toPrefix` pair. A shadowing check is needed either way (plan Risks 5: a simple name `Git` resolving to
   the new namespace `RoslynSentinel.Git`); the compile gate catches most cases, a fact for the rest.
7. **Source csproj items that name the moved folder** (`<Compile Remove>`, `<Compile Include>` links, `<None>`).
   Options: ignore; report as a fact (recommended: a text scan of the source csproj for the folder path, listed as
   "not updated"); edit. Not designed.
8. **Refactor of `MoveFileTool`.** Moving its inline validations into an engine touches the most used core write tool;
   the alternative is a second code path for sets. Recommendation: one path, with `MoveFileTests` as the regression net.

## Decision

Settled with the owner 2026-11-25 (implement-plan, phase 2). Where this section and the Proposal text differ, this wins.

- **D1 (Q1, parameter shape): `moves` list only, required.** `MoveFile` drops `sourcePath`/`destinationPath` and takes a
  required `moves` list of `{sourcePath, destinationPath}` pairs; a single move is a list of one. Breaking change on a core
  tool, accepted: the plan carries a caller-sweep slice (tests, eval fixtures, prompts, skills, agent docs, generated
  tables). Not the either/or `batchEdits` shape and not "first pair plus additionalMoves".
- **D2 (folder pairs): a pair may name a folder; no `include` globs.** Kind is derived per pair from disk; file and folder
  pairs may mix in one list. A folder pair moves the source folder's contents to the destination path (merge into an
  existing folder; a same-name file refuses), subfolders preserved. The description states that rule in one line;
  `dryRun` returns the expanded file-by-file pairs and the result lists them. No operation enum (`MoveFiles`/`MoveFolder`):
  it adds no information the tool cannot derive; revisit only if an eval measures weak-model confusion.
- **D3 (Q2, csproj edits): two opt-in booleans, default false: `addProjectReferences` and `addInternalsVisibleTo`.**
  Needed edges are derived by analysis, not a probe: symbols each moved file references (destination must reference
  their projects), reference sites of moved types (their projects must reference the destination), internal symbols
  used across the new boundary (IVT edges). They are added to the in-memory candidate before the single compile
  (`Solution.AddProjectReference`; IVT as an in-memory `[assembly: InternalsVisibleTo]` document); the csproj items are
  written only when the gate passes, under the same `changeId` so `UndoChange` reverts them. A cycle is refused as a fact.
  With a toggle off, each needed edge is reported as a fact naming the edge and the parameter; the tool never widens
  visibility unasked. Package references stay facts (a restore cannot be modelled in memory).
- **D4 (Q3, cap): soft cap 200 files per call**, refusal names splitting; calibrate with a measured 60-file dry run.
- **D5 (Q4, restore): probe, fix only if red.** `LoadSolution` already restores before loading (71c0c512: always with
  `forceReload`, else when a project's assets are missing or stale; `dotnet sln add` alone does not restore, probed
  2026-11-25), yet the Git run hit NU1105 on 2026-11-24. The plan adds an integration test (hand-written csproj that an
  existing project references, `dotnet sln add`, `LoadSolution(forceReload)`); `LoadSolution` is fixed only if it fails.
  The "destination in no loaded project" refusal names `LoadSolution(forceReload)` as the option.
- **D6 (Q5): `ChangeNamespace` first**, a method on `CoreRefactoringTools` in the `moveExtract` toolset (no new tool
  class); `MoveFile namespaceHandling: update` later as a thin composition, refused until then.
- **D7 (Q6): one target namespace per call** (`newNamespace` or `matchFolder: true`); shadowing check either way.
- **D8 (Q7): source csproj items naming a moved folder are reported as "not updated" facts** (text scan), never edited.
- **D9 (Q8): one shared path.** `MoveFileTool`'s inline validations move into one `Engines.Basic` planner used by every
  move; `MoveFileTests` is the regression net.
- **D10 (part 3): keep `SplitProjectByFolder` as the `NotImplemented` stub**, with a code comment naming the wrapper idea
  (compose `CreateProject` + `MoveFile` + `ChangeNamespace`) as an option for later. Not removed.
- **D11 (run): owner away during the run**; mid-run decisions are deferred to the tracker with a recommendation.

## Cost / risk

- **Touches.** Part 1: `MoveFileTool` (`Tools.Basic/WholeFileWriteTools.cs`), a new planner in `Engines.Basic`, tool
  description and two schema parameters, facts in the refusal text. `ValidateAndApplyHelper` and the chokepoint are
  unchanged (already set-capable). Part 2: a shared core extracted from the `CopyAndObsolete*` engine files, a new
  engine, one tool method, `ToolsetCatalog` and `ToolsetControlTools` text, an `ErrorPolicyIntentTable` row, regenerated
  `docs/generated/` tables. No new `[McpServerToolType]` class in either part. Part 3: deletion only.
- **DTOs (per `.claude/skills/dto-conventions/SKILL.md`).** Result type: reuse `AppliedChangeSummary` for both tools
  (`AffectedFiles`, `LineChanges`, `ChangeId`, `Findings` fit; its `From(ApplyOutcome, ...)` factory is the convention).
  Two engine-side types are candidates, each needing a NEW-DTO block when written, and neither is wire-visible:
  - `NEW-DTO: PlannedFileMove (Item, Engines.Basic.FileMove)`. Evaluated: `AppliedChangeSummary.AffectedFiles` - a flat
    list, loses the source/destination pairing and the two projects; `FileMoveMemory` entries - a lookup store for later
    reads, not a plan. Wire impact: none.
  - `NEW-DTO: ChangeNamespaceResult (Result, Engines.Basic.Refactoring)`. Evaluated: `CallerRewriteResult` - one source
    file's callers only; `CopyAndObsoleteMigrationResult` - carries obsolete-copy fields (`FellBackToBaseSet`,
    `MigratedCallers`); `DocumentEditResult` - one file. Wire impact: none. Reuse of an extended `CallerRewriteResult`
    is the alternative to check first.
- **What it could break.** The single-file path if the refactor in Open question 8 regresses it; gate time per call grows
  with the set (a 200-file set compiles every affected project and its dependents once); a peer session writing
  between the gate and the apply is handled by the chokepoint's re-validation (`MarkGatePassed`), but a long gate
  widens the window, so sets of this size should be taken under the `HOLD` protocol. A set-level refusal can hide which
  move is at fault unless errors are grouped by set file (specified above).
- **Makes harder.** `MoveFile` gains two parameters on a core tool; `namespaceHandling` now has per-form support rules
  that the description must state (one line each).
- **Verification expectations (planner and reviewer, not implementer).** The existing `MoveFileTests` and
  `MoveFileCopyAndObsoleteTests` exercise the set-of-one path only; they stay green under any set change and say nothing
  about a connected cluster, so a green run is not evidence for this feature. The implementing plan must specify, and a
  reviewer confirm, tests on the changed path itself. Fixture shapes: (1) a two-project solution with a mutually
  referencing cluster (a base record with two derived records; one partial class in three files) moved cross-project in
  one call, plus a pin that the same cluster moved file by file is still refused (the original blocker, as a
  regression); (2) a folder with nested subfolders and a non-`.cs` file; (3) eligibility refusal (one binary, one
  non-UTF-8, one existing destination) that leaves every file untouched and lists all offenders; (4) a destination
  project lacking a `ProjectReference` and one needing IVT, asserting facts-and-options text; (5) destination in no
  loaded project; (6) `UndoChange` restoring the whole set; (7) CRLF, LF and BOM files byte-identical after the move;
  (8) for part 2: all reference forms of Motivation's list, a global using, a partial type in three files, a caller
  inside the old namespace, a name-shadowing case, a multi-namespace file refusal. Ratchets to run: the plan's
  `BareFileWriteRatchetTests`, `PathComparisonRatchetTests`, `ToolHintRatchetTests` (new messages use hint helpers),
  `ArchitectureDocFreshnessTests` (generated tables).

## Related

- Plan: [plan_isolate_git_area_into_own_project](../plans/closed/plan_isolate_git_area_into_own_project.md) (Appendix B spike
  report; Steps 13, 14, 18, 19 Done lines; Risks 5, 6, 11).
- Blockers: [movefile has no multi-file cross-project move](../blockers/blocking_error_movefile_has_no_multi_file_cross_project_move.md),
  [SplitProjectByFolder is a stub](../blockers/blocking_error_splitprojectbyfolder_is_a_stub.md).
- [proposal_move_file_tool](./proposal_move_file_tool.md): the approved MoveFile design; this proposal builds on its
  slice 2 engines and supplies the design its item 3 (`update`) lacked.
- TODO.md: "No tool changes a namespace across files" and "MoveFile namespaceHandling `update`" should link here when
  this is accepted; "MoveType cannot move a type into an existing file" and INBOX 2026-11-24 19:45/19:50 are adjacent and
  not covered.
- [proposal_isolate_heavy_test_areas_by_project](./proposal_isolate_heavy_test_areas_by_project.md): the program whose
  later area splits would reuse the recipe.
- `blockers/closed/blocking_error_movetype_cannot_relocate_namespace_type_across_projects.md`: earlier workaround-only
  closure of the same family of gap.

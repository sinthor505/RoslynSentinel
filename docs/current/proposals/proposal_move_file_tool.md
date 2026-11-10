# MoveFile tool: move or rename a text file as one semantic operation

**Status:** OPEN 2026-11-10. Stub; design in a fresh chat. Scope, signature, slicing, placement and git behaviour are decided (below); the open questions are not.

## Motivation
No MCP tool moves or renames a file, so an agent that needs to has to leave the tool surface. The
2026-11-10 punchlist moved files three ways, each with a cost:

- shell `git mv` (two issue docs to `issues/closed/`);
- shell `Move-Item` (an idea doc to `ideas/closed/`), which was a reported dog-fooding bypass;
- `ReadFile` + `CreateFile` + `DeleteFile` (three test files between projects), after `MoveType`
  `ownFile` returned `no_changes` for a class that is alone in its file.

Per the mission in CLAUDE.md, reaching for a shell command to move a file is a gap in the tool surface,
not a deficiency in the agent. The same gap blocks the test-file moves of the core split (TODO.md, "core
split remainder"). A read-copy-delete sequence is also unsafe for a weak model: it re-types file content
through the model, so a transcription slip silently changes the file; a byte-exact move should never pass
through model output.

## Proposal

### Decided (todo review 2026-11-10, owner's decisions are final)
- **Scope:** any text file, `.cs` and non-C# alike. Refuse binary files and directories, refuse when the
  destination exists, refuse when the source is missing.
- **Signature:** `MoveFile(sourcePath, destinationPath, namespaceHandling)`, all three mandatory.
  `namespaceHandling` is one enum: `keep | update | copyAndObsolete`. For non-`.cs` files only `keep` is valid.
- **Slicing:**
  - Slice 1 = the plain move (`keep`); `update` and `copyAndObsolete` are refused with a "not yet supported"
    error. This unblocks the core-split test moves.
  - Slice 2 = `update` and `copyAndObsolete`.
- **Placement:** claude-lean core, as a method on `WholeFileWriteTools`
  (`RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs`), beside `WriteFileTool` (line 33) and `DeleteFileTool`
  (line 124). It lands after the schema trim frees startup budget (TODO.md, "trim SemanticFindReplace /
  MoveMember"). Because `WholeFileWriteTools` is an existing `[McpServerToolType]` class, a new method on it
  should not need the new-class registration steps in CLAUDE.md; that is unchecked.
- **Git:** no auto-staging.

### Findings to build on (verified against source 2026-11-10 unless marked)
- `ApplyProposedChangesAsync(changes, deletePaths, staleReadPolicy)`
  (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:2131`) already writes and deletes in one operation.
  `DeleteFileTool` uses it (`WholeFileWriteTools.cs:124-178`). The delete pass is commented as originally
  written "for the old half of a rename" (`PersistentWorkspaceManager.cs:2420`, loop at 2422). The same path in
  both `changes` and `deletePaths` is refused (`:2210`, "appear in both changes and deletePaths"), which is
  correct for a move since source and destination differ.
- The in-memory fold drops the missing `.cs` ("File is gone (e.g. the old half of a rename)", `:3113`) and
  adds the new one to the containing project found by `SolutionProjectLocator.FindContainingProject`
  (`:3153`; `RoslynSentinel.Common/SolutionProjectLocator.cs:10`; the brief says longest-prefix project, that
  rule inside the locator was not re-read). So a cross-project move works with SDK globbing. Non-`.cs` files
  skip the fold (`:3105`); a change to csproj / slnx / props triggers a full reload (from the brief, not
  re-verified).
- The edit ledger and operation blob are recorded for both paths (`RecordEditAttributions` takes
  `deletePaths`, `:2616` and `:2883-2896`; the operation blob part is from the brief, unverified).
- The EOL pre-pass may refuse or normalise a byte-exact move (`ToolErrorCode.EolChangeRefused`,
  `:2404-2409`). **Needs checking**: a move must preserve bytes, including BOM and mixed endings (the repo
  rule is uniform CRLF, but a moved third-party or generated file may not be).
- `PreviewMoveFileToNamespaceFolder` (`RoslynSentinel.Tools.Advanced/IntelligenceTools.cs:202`,
  `RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs:59`) derives the folder-based namespace; slice 2
  can reuse it.
- `MoveType ownFile` `no_changes` is by design, not a bug: `MoveTypeToFileAsync`
  (`RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs:1197`) returns an empty result when the type name
  equals the file name (guard at ~1213). It returns before `targetFolder` is read (~1244), so `targetFolder`
  is silently ignored in that case. That silent ignore is a separate small defect (a tool that accepts a
  parameter and drops it) and is a candidate for its own TODO line; it is not fixed by MoveFile.
- Git reports write + delete as `D` plus untracked until both are staged; rename detection applies after
  staging (`RoslynSentinel.Tools.Basic/GitImpl.cs:2194` labels `R` "renamed"). See open question 2.

## Alternatives considered, not pursued
- **Teach `MoveType ownFile` to move a whole-file type to another folder or project.** Rejected as the
  primary route: the guard is deliberate ("already in its own file"), and a file move is not a type
  operation (non-`.cs` files, several types in one file, no type at all).
- **A generic shell-style rename surface (`Git mv`).** Rejected: it would move the file without touching the
  workspace fold, so the loaded solution and ledger go stale; the point of the tool is to go through
  `ApplyProposedChangesAsync`. (`Git(operation: ...)` stays the way to stage the result.)
- **Auto-stage the move so git shows a rename.** Rejected by the owner (decided): no auto-staging; see
  open question 2 for the status-hint alternative.
- **`ReadFile` + `CreateFile` + `DeleteFile` by the agent (status quo).** Works, costs three calls, passes
  file content through the model, and is the failure mode this proposal removes.

## Open questions (for the fresh-chat design)
1. **Cross-project moves in slice 1.** Allow, with the compile gate refusing breaking moves (the earlier
   investigation recommended this), or refuse? The owner deferred this to the design session. The fold already
   handles a cross-project add (see findings), so the question is the gate and the error wording, not
   mechanics.
2. **Git(status) rename hint**, so an unstaged move does not show as `D` + `??`. Tiers:
   (1) definitive, from the server's own `MoveFile` operation record;
   (2) "renamed (identical content)" when the untracked file's blob hash equals the deleted file's HEAD blob;
   (3) "likely renamed" by name plus similarity.
   The owner deferred this to the design session. Which tiers ship, and whether this is slice 1, is open.
3. **`update`:** rewrite namespace, usings and qualified references solution-wide, reusing the
   `MoveType` / `NameTarget` machinery. Large; the interaction with files that hold several types, partial
   types and global usings is not designed.
4. **`copyAndObsolete`:** copy to the destination with the folder-derived namespace and mark each type in the
   old file `[Obsolete("Use <NewNs>.<Type>")]`. This is the CLAUDE.md migration-first pattern ("new member
   beside the old, old `[Obsolete]`") applied to a file. Known constraints:
   - a full-name collision gives CS0101 (same project) or CS0433 (two assemblies both referenced);
   - partial types and static state are duplicated;
   - the two copies drift until the old one is removed.
   Owner ideas to evaluate: run it through the `ScopedLedger` (scoped operation ledger, per the "name which
   ledger" convention); give the new file's class a different name, or rename the old class, to avoid the
   conflicts. Neither idea is evaluated yet.
5. **Undo behaviour for the created file.** Whether `UndoLastApply` restores both paths (recreates the source,
   removes the destination) is not verified.
6. **Byte preservation (from the EOL finding above).** Does the EOL pre-pass need a "move, do not normalise"
   bypass, or can the tool pass already-final text through it unchanged? Unverified.

## Cost / risk
- Touches `WholeFileWriteTools` (new method), the tool description budget (every extra tool costs startup
  prompt tokens, hence the dependency on the schema trim), and tests for the write chokepoint paths: move
  with the same content, cross-project move, refused cases (binary, directory, destination exists, source
  missing), and `UndoLastApply`.
- Risk: a move that passes through the EOL pre-pass could rewrite line endings or the BOM of a file the user
  expected to be byte-identical (open question 6). A move into another project can break compilation; the
  compile gate must catch it (open question 1).
- Risk: `namespaceHandling: keep` can leave a file whose namespace no longer matches its folder; that is the
  agreed slice-1 behaviour and the reason `update` exists. The tool response should say so.
- Makes harder: nothing in the write path; it adds one more caller of `deletePaths`, which is already
  exercised by `DeleteFileTool`.
- Coverage note for the eventual plan: a green `DeleteFile` test run does not cover the move path (it never
  exercises a `changes` + `deletePaths` pair with different paths in one call). The planner should specify a
  test for exactly that pair; the reviewer should confirm it exists.

## Related
- `docs/current/TODO.md` - core split remainder (blocked on this), schema trim of SemanticFindReplace /
  MoveMember (placement prerequisite), `TOOL-GAP` entries for MoveFile and cross-project MoveMember with
  private helpers (the owner is editing that file separately; not touched here).
- `.claude/handoff/2026-11-10_todo_punchlist_91761378.progress.md` - the punchlist that hit the gap.
- CLAUDE.md - "Tool gaps: report, don't brute-force" and the migration-first pattern.

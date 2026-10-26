# Plan: Configurable EOL policy, single write chokepoint and final-bytes EOL guard

**Status:** IN PROGRESS 2026-10-25. Steps 0-4 done (decisions, sweep, `scripts/Get-EolAudit.ps1`, working tree normalized, ratchet in `build.ps1`, `--eol` option, resolver, one dominant-EOL rule); next is Step 5, chokepoint uses the resolver and guards the final bytes.

> **Note (2026-10-19):** `NormalizeWholeSubtreeWhitespace` (named in the sweep and Step 2 below) was deleted (D1);
> the EOL-aware helpers in `RoslynFormattingHelper` replace it. Treat those mentions as historical.

## Problem

`.cs` files keep acquiring LF and mixed line endings even though line-ending preservation and
`EolChangeGuard` exist.

Evidence (2026-10-18):

- `git ls-files --eol -- '*.cs'` (Worktree folders excluded): all 693 tracked files are `i/lf`; the
  working tree is 380 `w/crlf`, 233 `w/lf`, 80 `w/mixed`. With `* text=auto` (`.gitattributes`) and
  `core.autocrlf=true` (system and user gitconfig) a checkout writes uniform CRLF, so every LF and
  mixed file was written after checkout. Git is not the source.
- The drift is invisible: `git status`/`git diff` compare after LF conversion, so a working-tree EOL
  change never shows as a modification.
- Mixed files are still produced: `Engines.Advanced/AdvancedRefactoringEngine.cs` and
  `Engines.Basic/SyntaxUpgradeEngine.cs` were written on 2026-10-18 during the Whitespace C6 commits.
  Whether those edits created or only preserved the mix is unknown (no tool reports per-line EOL).
- `.editorconfig` says `[*.cs] end_of_line = crlf`; no server code reads it.

Gaps found by a read-only investigation on 2026-10-18 (line numbers from that pass; re-verify each
before editing):

1. The guard checks pre-normalization text, not the bytes written. `ValidateAndApplyHelper`
   (about 34-56) and the backstop in `PersistentWorkspaceManager.ApplyProposedChangesAsync` (about
   1608-1639) test `newContent`; EOL normalization runs after (about 1741-1750) and its output
   `contentToWrite` is never checked.
2. `EolUtilities.NormalizeEolOfChangedLines` matches lines by text (LCS), so repeated lines (`}`,
   blank lines) can receive the wrong terminator (TODO.md:54).
3. The target EOL comes from the `_knownFileEolPresence` cache (filled at load,
   `PopulateKnownFileHashes` about 421-451, and after chokepoint writes about 1780), not from the
   fresh pre-image. It is not refreshed by the file watcher, and a path missing from it is written
   with no normalization.
4. New files get no EOL handling: `EolChangeGuard.Check` exempts a null pre-image, normalization at
   about 1747 requires a pre-image, and `WorkspaceFileEditImpl.CreateFile` hard-codes `\n` in its
   scaffold.
5. Files created from another file inherit its EOL, mixed included:
   `BasicRefactoringEngine.MoveAllTypesToFilesForDocumentAsync` (about 600-625) and
   `MemberRefactoringEngine.cs` about 4332 (`DetectDominantEol(sourceText)`).
6. A mixed pre-image is tolerated: only a change of dominant style is refused, so LF lines can be
   added indefinitely.
7. Three "dominant EOL" definitions with different tie-breaks: `DetectDominantEol(string)`,
   `DetectDominantEol(SourceText)` and `EolCounts.Dominant`.
8. Scattered newline literals: `RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace` defaults
   `eol = "\n"` (66 references in 13 files); `SyntaxFactory.LineFeed` in `MsToolAugmentEngine`
   (1149, 1150, 1791, 1820, 1821); hard-coded CRLF in `MsToolAugmentEngine`, `AsyncOptimizationEngine`
   (337-348, 548), `ContextHelper:29` and `RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync`
   (249, 253, 276).
9. Writes outside the chokepoint: `MsToolAugmentEngine.FormatDocumentSafeAsync` and
   `SolutionManagementEngine.CreateProjectAsync` (full sweep in the Step 0 results).
10. No protection at all before `389ae1be`/`b56f54a8` (2026-10-03); much of the existing drift is
    likely from then.

Related: `plan_file_normalization_tool.md` (not implemented, no Status line);
`docs/current/reference-code-file-write-paths-v1.md` (chokepoint description).

## Decision

- **Normalize first, ratchet immediately.** Establish a clean baseline and make any regression fail a
  check, because git cannot show it.
- **Guard the final bytes before the broad refactor.** Once the chokepoint checks what it actually
  writes, every remaining literal is caught and attributed to a tool, so Step 8 can proceed in slices.
- **One EOL policy, exposed as a startup arg** `--eol auto|crlf|lf` (default `auto`), resolved
  through a resolver service in `Common`, wired per `LlmOptions.cs` (startup-arg pattern).
  - `crlf`/`lf`: every write converts the whole file to that EOL. Mixed files converge on first touch.
  - `auto` (for other codebases, so the server never silently converts a repo): existing file's
    style if single-style, else `.editorconfig` `end_of_line` for the path, else
    `Environment.NewLine`.
  - This repo runs with `crlf`.
- **The resolver is passed, not ambient.** Engines take `eol` as a parameter from the resolver; no
  static global, so parallel tests can each set their own value.
- **One chokepoint for workspace files.** Bare writes are routed through
  `ApplyProposedChangesAsync`/`FileIoHelper`, not a second chokepoint, because the existing one also
  owns drift tracking, `_internalChanges` watcher suppression and undo pre-images. Non-workspace
  writes (logs, reports, temp) are out of scope.

## Execution rules

- Implementation is dispatched (`dispatch-implementer` skill); C# dispatches are sequential.
- Every slice is compile-green: `Build` 0 errors, test results compared with
  `docs/known-failing-tests.*.txt`; report only new failures.
- Migration-first for signature changes: new overload beside the old one, `[Obsolete("Use X")]`,
  callers moved per engine, old overload removed last.
- After any server-source step: `Build(fullBuild)`, then restart the server before relying on its behaviour.
- Step 2 runs with the server stopped and with no other session editing (see `project_concurrent_sessions`).

## Steps

### Step 0 - Spike and decisions
- Files: none (findings recorded in this plan).
- Change:
  - Decide `*.cs -text` (CRLF stored in the index; any later EOL change shows in `git diff`) versus
    keeping `text=auto` (index LF; the ratchet is the only signal).
  - Verify whether `Formatter.FormatAsync` on an in-memory document honours `.editorconfig`
    `end_of_line` (open in `plan_file_normalization_tool.md` Step 0).
  - Sweep non-test projects for bare writes (`File.WriteAll*`, `File.AppendAll*`, `File.Create`,
    `StreamWriter`, `FileStream` opened for write) and list them for Step 6.
  - Decide whether `.editorconfig` or the `--eol` arg wins when both are set.
- Done when: each decision has an answer recorded here and the bare-write list exists.

Step 0 results (2026-10-18):

- Git storage: keep `* text=auto` (owner decision). The index stays LF and checkout gives CRLF via
  `core.autocrlf`, so Step 2 normalizes the working tree only (no commit) and the Step 1/3 audit is
  the regression signal. No `.gitattributes` change.
- Precedence: an explicit `--eol crlf|lf` wins; `.editorconfig` `end_of_line` is consulted only in
  `auto` mode (owner decision).
- Formatter and `.editorconfig` (Roslyn `Microsoft.CodeAnalysis.*.Workspaces` 5.9.0):
  - Document-based `Formatter.FormatAsync` honours `end_of_line`. Pinned by
    `Tests.Basic/RoslynFormattingHelperFormatSpansTests.cs:190-200`
    (`MakeDocument_FormatterNewLinePin_IsHonouredByTheRawFormatter`, an in-memory analyzer config).
    This covers `RoslynFormattingHelper.cs:323` (`FormatAnnotatedSpansAsync`, 36 callers) and `:358`.
  - `Formatter.Format(SyntaxNode, AdhocWorkspace)` does not, and uses host defaults:
    `MsToolAugmentEngine.cs:701` (output written) and `PersistentWorkspaceManager.cs:1727` (debug
    divergence log only).
  - No server code passes `OptionSet`/`SyntaxFormattingOptions`/`FormattingOptions.NewLine`, and
    none reads `.editorconfig` itself.
  - Hypotheses, not verified:
    1. `MSBuildWorkspace.OpenSolutionAsync` loads the repo `.editorconfig` as analyzer config.
    2. Path-less `project.AddDocument(name, root)` (`BasicRefactoringEngine.cs:558,622`) does not
       match `[*.cs]`.
    3. Edits to `.editorconfig` are not picked up until reload.
  - Settle all three with a spike test as the first slice of Step 4: load a temp repo copy with
    `MSBuildWorkspace` and `[*.cs] end_of_line = lf`, then assert no `\r` after `FormatAsync` on
    (a) an existing document, (b) a document added without `filePath` and (c) one added with it.
  - Consequence: the formatter is not the LF source for loaded documents. The LF sources are the
    `NormalizeWholeSubtreeWhitespace` `"\n"` default, literal trivia and agent text.
- Bare-write sweep (non-test projects; `FileIoHelper` writes are called only from the chokepoint):
  - Route through the chokepoint (Step 6):
    - `MsToolAugmentEngine.cs:732` (`FormatDocumentSafeAsync`, no-solution or file-not-in-solution
      branch, reached via `Tools.Experimental/CodeTransformTools.cs:238`).
    - `SolutionManagementEngine.cs:27-37` (`CreateProjectAsync`/`SplitProjectByFolderAsync` shell
      out to `dotnet new` and `dotnet sln add`; template `.cs` lands in a new project).
  - Allowlist, reasons given: git working-tree rewrites in `GitImpl` (stash, reset, revert,
    checkout, pull, abort) - external process, CRLF via autocrlf. Also non-workspace writes:
    `GitWorktreeManager`, the `StageHunksAsync` temp patch, `DocumentationTools` docs/yaml
    (`DocPathGuard` not yet confirmed to refuse a `.cs` name), `BuildIsolation`, `MigrationLedger`,
    `OperationBlobWriter`, `LargeResultHelper`, `AsyncifyTools` debug dumps, `ModelAgentRunner`,
    `FlushingFileLoggerProvider`, `ServerStartupHelpers`, `ConsoleMode` dumps, `TestRunEngine` `.trx`
    delete, `PlanStepRunner` blocker docs.
  - Not swept in depth: `Tools.Experimental`, `Utilities.*` beyond text hits.

### Step 1 - EOL audit script
- Files: `scripts/Get-EolAudit.ps1` (new).
- Change: report tracked `.cs` files by index/working-tree EOL using `git ls-files --eol`, excluding
  `*/Worktree/`, with `-FailOn lf,mixed` and an optional allowlist. Model it on `scripts/Get-BomAudit.ps1`.
- Done when: the output matches the 380/233/80 breakdown above (or the current numbers).
- Result (2026-10-18): done. `Get-EolAudit.ps1 -FailOn lf,mixed` reports 380 crlf / 233 lf /
  80 mixed and FAIL for 313 files (exit 1). Supports `-AllowlistPath`, `-ShowFiles`, `-CsvPath`.

### Step 2 - Normalize the repo
- Files: every tracked `.cs` reported LF or mixed; `.gitattributes` if Step 0 chose `-text`.
- Change: with the server stopped and the working tree clean of other sessions' `.cs` edits, convert
  the files to CRLF.
  - Per the Step 0 decision (`text=auto` kept): delete the file and `git checkout -- <path>`
    (autocrlf writes CRLF). No commit results.
  - Restart the server and run `LoadSolution` afterwards.
- Done when: `Get-EolAudit.ps1 -FailOn lf,mixed` passes and `Build` is 0 errors.
- Result (2026-10-25): done. 361 of 793 `.cs` files (LF or mixed) deleted and restored with
  `git checkout --pathspec-from-file=<list> --` (autocrlf wrote CRLF); no commit, no tracked change.
  Audit PASS (0 lf, 0 mixed); `dotnet build RoslynSentinel.slnx` 0 errors. Delete first is required: checkout
  alone skips stat-clean files. The server must be restarted and `LoadSolution` run before use.

### Step 3 - Ratchet
- Files: `scripts/build.ps1` or a `Tests.Server` test; this plan.
- Change: run the Step 1 audit as part of the standard build/test flow so a new LF or mixed `.cs`
  fails it.
- Done when: a deliberately LF-converted scratch copy of a tracked file fails the check and reverting it passes.
- Result (2026-10-25): done. `scripts/build.ps1` ends with an "EOL audit" block (every run except
  `-UpdateBaseline`) that runs `Get-EolAudit.ps1 -FailOn lf,mixed` and folds the result into the script's
  exit code. Verified: LF-converted `DummyConsole/DummyApp/Program.cs` -> audit FAIL (1 file); restored -> PASS.
  The audit covers untracked files too (`-c -o`). Not wired into `RunTest`; only the `build.ps1` flow enforces it.

### Step 4 - EOL policy option, resolver and single dominant-EOL function
- Files: `RoslynSentinel.Common` (new options type and resolver, `EolUtilities.cs`), the server
  startup-arg wiring per `LlmOptions.cs`, `Server.*` DI registration.
- Change:
  - Add `--eol auto|crlf|lf` (default `auto`) and a resolver `Resolve(path, preImageText)` returning
    the target EOL per the Decision rules.
  - Keep one dominant-EOL function; mark the other two `[Obsolete]` and move their callers.
- Done when: resolver unit tests cover each mode, new file, mixed pre-image and `.editorconfig`
  present/absent; `Build` 0 errors.
- Progress (2026-10-25), sliced after measurement (five dominant-EOL definitions, not three: also
  `TestCategoryTextEditor.DetectEol` and `CommentingEngine.DetectMemberEol`):
  - Decision: `--eol` follows the static-options pattern (`SchemaOptions`), no DI; `EolResolver.Resolve` is pure
    and takes the mode as a parameter, so tests and engines are not ambient.
  - Slice A done: `Common/EolPolicyOptions.cs`, `Common/EolResolver.cs`, `Tests.Basic/EolResolverTests.cs` (19 tests).
    `Resolve(mode, preImageText, editorConfigEndOfLine)`; reading `.editorconfig` from a path is not built yet.
  - Slice B done: `EolPolicyOptions.Configure(args)` in the four server entry points.
  - Slice C replaced by a smaller design (measurement showed the `SourceText` overload deliberately returns
    unusual terminators for `DetectNormalizableEol`, so migrating ~19 callers would change behaviour for no gain):
    one tie-break rule instead of caller migration, no `[Obsolete]`.
    - C1 done: `EolCounts.DominantTerminator`; `DetectDominantEol(string)` delegates to it;
      `DetectDominantEol(SourceText)` ties break CRLF, LF, CR, then others. 9 tests. Only behaviour change: more
      lone CRs than LFs (with at least one LF) now gives CR.
    - C2 done: `TestCategoryTextEditor.DetectEol` delegates to `EolUtilities.DetectDominantEol`.
    - `CommentingEngine.DetectMemberEol` left alone on purpose: it copies the member's first end-of-line trivia
      (not a counting function); Step 5's resolver and final-bytes guard make the per-engine choice moot.
  - Step 4 done. `EolPolicyOptions.Mode` has no production reader until Step 5.

### Step 5 - Chokepoint uses the resolver and guards the final bytes
- Files: `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `EolChangeGuard.cs`,
  `ValidateAndApplyHelper.cs`, `Tests.Basic/WriteChokepointGuardrailTests.cs`.
- Change:
  - Take the target EOL from the resolver per write, new files included. Drop the use of the
    `_knownFileEolPresence` cache for normalization.
  - Run the guard on `contentToWrite`, the post-normalization text.
  - `crlf`/`lf`: convert the whole file, then assert it is uniform.
  - `auto` (decided 2026-10-25, after review: a majority-based "more mixed" guard contradicts the resolver, which
    gives a mixed file its `.editorconfig`/platform target, not its majority): always convert the whole file, then
    assert the final bytes are uniform.
    - Single-style pre-image (CRLF-only or LF-only): the target is that style, so text arriving in the other style
      is converted to it (not refused; the early `ValidateAndApplyHelper` guard goes away in 5c and the tests that
      assert `EolChangeRefused` for a style flip change to assert conversion). The only refusal left is a
      defensive "final bytes not uniform" check.
    - Mixed, CR-only, no-newline pre-image or new file: the target is the resolver's value; the file converges
      on first touch.
    - No "minority" or "flip" counting remains; `NormalizeEolOfChangedLines` leaves the write path.
  - New files get the resolved EOL under every mode (decided 2026-10-25), so the existing LF new-file tests set
    the mode explicitly.
  - The chokepoint reads the mode from an overridable property (default: `EolPolicyOptions.Mode`) so tests do
    not touch the static (decided 2026-10-25).
  - Make the whitespace-only no-op skip (`EnableAstNormalizationNoOpCheck`) not drop an EOL-only change.
  - Keep `UndoLastApply` `exactRestore` exempt, and exempt the failed-write rollback restore the same way (it
    writes the pre-image back unchanged). Consequence, accepted: in a foreign `auto` repo an undo or rollback can
    restore a file that was mixed before the change, so it is mixed again until the next touch.
  - Attribution (decided 2026-10-25): whole-file conversion silently hides which tool produced wrong-EOL text, and
    the chokepoint cannot see writes that bypass it (shell, IDE, raw `Write`, `dotnet new`). So when the pre-image
    read from disk is not uniform, or is uniform in a style other than the resolved target, the chokepoint records a
    finding naming the file and the calling tool ("pre-image written outside the chokepoint") and still converts.
    This is a record, not a refusal. The `build.ps1` ratchet (Step 3) is the other signal and does not run under
    `RunTest`; decide in Step 6 whether to add a test-run check.
  - The refusal names the tool and file (structured `EolChangeRefused`, no raw exception). Its only remaining
    trigger is the defensive "final bytes not uniform" check.
- Done when: chokepoint tests cover new LF file under `crlf`, mixed pre-image converging, LF
  text injected into a CRLF file under `auto` (written as uniform CRLF, not refused), the EOL-only change surviving
  the no-op skip, undo and failed-write rollback leaving the pre-image unchanged, and the attribution finding for a
  mixed pre-image; `Build` 0 errors; baselines unchanged.
  - End-to-end case (replaces the finding #12 pin, see `plan_pinned_tool_defect_fixes.md`): a mixed pre-image
    through `ExtractMethodSafe` under `crlf` leaves uniform CRLF on disk. Then reword the TODO in
    `MsToolAugmentEngineFormatCharacterizationTests.cs` (the `ExtractMethod_MixedEol_StrayCrlfOnBlankLine...` test)
    to say the helper-level placement is superseded by this step.

### Step 6 - Route bare writes through the chokepoint
- Files: per the Step 0 sweep: `MsToolAugmentEngine.FormatDocumentSafeAsync` (:732, and its
  `AdhocWorkspace` format at :701) and `SolutionManagementEngine.CreateProjectAsync` (normalize the
  template `.cs` after `dotnet new`, or write it through the chokepoint).
- Change:
  - Send each workspace-file write through the chokepoint.
  - Add a ratchet test that fails on `File.Write*`/`File.AppendAll*` outside `FileIoHelper` in
    non-test projects, with an allowlist for non-workspace writes. Pattern: the whitespace ratchet
    tests from `c4b7a16d`.
- Done when: the ratchet test passes and the sweep list is empty or allowlisted with reasons.

### Step 7 - New-file creators use the resolver
- Files: `Tools.Basic/WorkspaceFileEditImpl.cs` (`CreateFile`),
  `Engines.Basic/BasicRefactoringEngine.cs` (`MoveAllTypesToFilesForDocumentAsync`),
  `Engines.Basic/MemberRefactoringEngine.cs` (about 4332, `AddTopLevelTypeAsync`).
- Change: build scaffolds and split-out files with the resolved EOL, not `\n` or the source file's EOL.
- Done when: tests create a file from an LF source under `crlf` and get uniform CRLF.

### Step 8 - Retire scattered newline literals (sliced, one engine per slice)
- Files: `Common/RoslynFormattingHelper.cs`, `Engines.Basic/MsToolAugmentEngine.cs`,
  `AsyncOptimizationEngine`, `ContextHelper`, `MemberRefactoringEngine` (`ParseMemberDeclaration`
  call sites about 2499, 2601, 2919, 3040), and the 13 `NormalizeWholeSubtreeWhitespace` caller files.
- Change:
  - Add an `NormalizeWholeSubtreeWhitespace` overload with a required `eol` and obsolete the
    defaulted one. (Superseded 2026-10-19: the whitespace initiative removed every caller and slice D1
    deleted the helper, so this sub-step and its 13 caller files no longer apply.)
  - Replace the `SyntaxFactory.LineFeed` and CRLF literal trivia with the passed EOL.
  - Convert agent-supplied member text to the target EOL before parsing.
  - Coordinate with the Whitespace C6 work (TODO.md:25), which is retiring many of the same calls;
    skip calls C6 removes.
- Done when: no defaulted `eol` callers remain, the obsolete overload is removed, a `Search` for
  `LineFeed`/`"\r\n"`/`"\n"` trivia literals in `Engines.*` and `Common` returns only allowlisted
  sites, and the Step 3 ratchet is green after a model-eval or battery run.

### Step 9 - Per-line EOL reporting (tool gap)
- Files: `ReadFile` or `Search` tool and engine.
- Change: report line numbers by terminator class (CRLF/LF/CR) for a file, so EOL forensics no
  longer needs a shell byte scan (the dogfood hook blocks it, and correctly so).
- Done when: a tool call lists the LF-only lines of a mixed fixture.

### Step 10 - Docs
- Files: `docs/current/reference-code-file-write-paths-v1.md`,
  `docs/current/references/reference_architecture_map.md`, `docs/current/TODO.md`.
- Change:
  - Describe the resolver, the final-bytes guard and the `--eol` arg.
  - Fix the stale TODO.md:14 entry (current source no longer does whole-file `NormalizeEol` in
    `InsertMemberDeclarationAndFormatInsertionAsync`).
  - Close or relink the EOL items in TODO.md.
  - Give `plan_file_normalization_tool.md` a Status line (EOL part superseded by this plan; BOM part
    independent).
- Done when: the docs match the source.

## Out of scope

- BOM handling (stays in `plan_file_normalization_tool.md`).
- Non-`.cs` files and writes outside the workspace (logs, reports, temp files).
- PlanStepRunner `*/Worktree/` clones.
- Fixing the LCS mis-assignment in `NormalizeEolOfChangedLines` (TODO.md:54). Whole-file conversion under
  every mode (Step 5) removes it from the write path; the function can be deleted afterwards.

## Risks and open decisions

- `-text` versus `text=auto` (Step 0). `-text` makes regressions visible in diffs but stores CRLF in
  the index for every clone.
- `.editorconfig` versus `--eol` precedence (Step 0).
- Tests that assert exact strings with `\n` may change once the default EOL is resolved rather than
  hard-coded; such tests should set the policy explicitly. Size unknown.
- Under `crlf`, the first touch of a not-yet-normalized file produces a whole-file EOL diff, which may
  hide content changes in review. Mitigated by Step 2 running first.
- Hypothesis, not traced: the rollback restore writes the pre-image without an encoding and may drop
  a BOM.
- Not traced by the investigation: `MethodSignature`, `RetryFailedChanges`,
  `MoveMemberTextEdits.ApplyChanges`, `DiffEngine.cs:105`, test helpers and server raw-string
  templates. Step 5's final-bytes guard catches them regardless; Step 8 should sweep them.
- Concurrent sessions: other sessions' uncommitted `.cs` edits block Step 2 until committed.

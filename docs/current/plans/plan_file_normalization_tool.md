# File normalization tool (BOM / line endings / format) and encoding-preserving writes

## Context

Blocker: `docs/current/blockers/blocking_error_member_strips_utf8_bom_on_write.md`. The shared write
chokepoint reads with BOM detection and writes with no `Encoding`, so any BOM-bearing `.cs` file
loses its BOM on its first tool write. A repo audit (`scripts/Get-BomAudit.ps1`, 2026-10-01) found
18 of 457 `.cs` files carry a UTF-8 BOM (DummyConsole 6/7, Common 8/155, Tests.Advanced 3/47,
Tests.Basic 1/15); `MemberRefactoringEngine.cs` was the only BOM file in Engines.Basic and has
already been stripped in the working tree.

Goal: one tool that normalizes a whole solution/project/file to a single convention in one diff, so
later server writes stop producing spurious per-file diffs. Decided with the user 2026-10-01:

- C# documents only (files in the Roslyn workspace); no .csproj/.slnx handling.
- Add `*.cs text eol=crlf` to `.gitattributes` and `git add --renormalize` so the EOL change is
  visible in the normalization commit. Today `* text=auto` + `core.autocrlf=true` stores LF in the
  index, which makes a working-tree CRLF conversion invisible to git.
- Fix the chokepoint to preserve each file's existing BOM by default (the blocker fix) in the same
  change; the tool's explicit BOM operations override it.
- Keep the chokepoint doing the disk write (`FileIoHelper`); do NOT move writing to
  `Workspace.TryApplyChanges()` (see "Why not TryApplyChanges"). `SourceText.Encoding` is used as the
  in-memory carrier of BOM state.

## Why not TryApplyChanges

Considered 2026-10-01 (MSBuildWorkspace saves with the document's `SourceText.Encoding`, falling back
to sniffing the existing file, so it is BOM-aware). Rejected as the write mechanism because:

- The server does not use it today: it holds its own `CurrentSolution`, updated via
  `WithDocumentText`, and creates three separate `MSBuildWorkspace` instances that it replaces on
  reload. `SetTestSolution` scenarios have no workspace at all. `TryApplyChanges` only applies to the
  workspace that owns the solution.
- It would take over writing and thereby bypass everything the chokepoint exists to control: the
  per-path `FilePathLock`, the `_internalChanges` content marker and `_knownFileHashes` baseline
  (file-watcher suppression and drift detection), pre-image capture for undo, retry-on-lock, and
  rollback-on-partial-failure. It returns only a `bool`, with no per-file failure detail.
- Hypothesis (UNVERIFIED, from memory of Roslyn internals): solution diffing is text-based and may
  treat an encoding-only change as no change, which would silently drop `AddUtf8Bom`/`RemoveUtf8Bom`.

Its encoding model is still the right one, which is why `SourceText.Encoding` carries BOM state here.

## Known defect to fix first: in-memory encoding is wrong after the first write

`PersistentWorkspaceManager.ApplyInMemoryDocumentUpdatesAsync` (~line 1638) refreshes every written
document with `SourceText.From(content, Encoding.UTF8)`. `Encoding.UTF8` has a BOM preamble, so every
refreshed document claims a BOM regardless of what is on disk. `SourceText.Encoding` read from an
edited document is therefore unreliable until this is fixed.

## Tool surface

`operation` enum: `ConvertToLf`, `ConvertToCrlf`, `AddUtf8Bom`, `RemoveUtf8Bom`, `FormatDocument`,
`All`. `All` = FormatDocument, then ConvertToCrlf, then AddUtf8Bom (fixed order: the formatter's
newline output must not undo the EOL step, so EOL always runs after format).

Parameters: `reason` (standard), `operation`, `scope` (`ToolScope`) + `scopeName` (project name or
file path, same pattern as `Build`), `dryRun` (default true). Result: per-operation counts, per-file
change flags, files skipped (already compliant), files containing multi-line verbatim/raw string
literals (CRLF/LF conversion changes their runtime value - report, do not block).

## Step 0: spike - is Roslyn's loaded view of BOM accurate?

Decides whether the write path can trust `SourceText.Encoding` or must always sniff disk bytes.

1. Load `RoslynSentinel.slnx`; for every document record `SourceText.Encoding` presence,
   `GetPreamble().Length`, and `CodePage`/`WebName`.
2. Compare against `scripts/Get-BomAudit.ps1 -CsvPath` output (457 `.cs` files, 18 UTF-8 BOM).
   Report mismatches in both directions (BOM on disk but no preamble in Roslyn, and the reverse), plus
   how Roslyn represents a no-BOM UTF-8 file (null encoding vs `UTF8Encoding(false)` vs
   `Encoding.UTF8`).
3. Check whether a document whose text differs only in `SourceText.Encoding` is reported as changed by
   `Solution.GetChanges` (the TryApplyChanges hypothesis above). Informational; the chosen design
   does not depend on it.
4. Check whether document-based `Formatter.FormatAsync` on a loaded solution honours the project's
   `.editorconfig` (needed by the FormatDocument step; `FormatDocumentSafeAsync` uses an
   `AdhocWorkspace` with defaults).

Outcome rules: if Roslyn's view matches disk for all 457 files, step 2 below may read
`SourceText.Encoding` first and sniff disk only as a fallback; otherwise disk sniffing stays the
source of truth. Record the result in this plan before proceeding.

## Engine and write path

1. `ApplyInMemoryDocumentUpdatesAsync`: stop using `Encoding.UTF8`; refresh the document's
   `SourceText` with the file's real encoding (existing document encoding, or the sniffed one).
2. Encoding resolution at write time: sniff the file's leading bytes on disk (UTF-8 BOM / none /
   UTF-16 / UTF-32); fall back to the loaded document's `SourceText.Encoding` for files not yet on
   disk. Disk is the authority so files not loaded in the workspace still work. Subject to step 0.
3. `FileIoHelper`: add an optional `Encoding` to `WriteAllTextAsync` (write preamble + bytes, still
   under the per-path lock); add a read variant returning text plus detected BOM/EOL metadata. Also
   fix the no-solution fallback in `MsToolAugmentEngine.FormatDocumentSafeAsync` (~line 721), which
   writes with no encoding.
4. `PersistentWorkspaceManager.ApplyProposedChangesAsync`:
   - preserve the file's existing encoding by default;
   - add an optional per-file encoding override (`IReadOnlyDictionary<FilePathWrapper, Encoding>?`)
     used by `AddUtf8Bom`/`RemoveUtf8Bom`;
   - add a force-write option that bypasses the string-equality no-op skip and the
     `EnableAstNormalizationNoOpCheck` skip, both of which would otherwise report EOL-only,
     format-only and BOM-only changes as successful no-ops without writing;
   - record the encoding in the pre-image so `UndoLastApply` restores the BOM;
   - after a write, update `_knownFileHashes`/`_internalChanges` as today (the content hash does not
     cover the BOM; confirm the drift detector does not misread a BOM-only write as external - cf. the
     SessionHalted observation in the blocker doc).
5. New `FileNormalizationEngine` in `Engines.Basic`: per file, detect BOM/EOL, apply the requested
   operations in the fixed order, return a change plan; applies through the chokepoint.
6. FormatDocument step: use document-based `Formatter.FormatAsync` on the loaded solution if step 0
   shows it honours `.editorconfig`; otherwise document the limitation in the tool description.

## Registration (all three required)

Tool class (Tools.Basic, with an `*Impl` backing class), entry in a `Server.Basic/ToolClassRegistry.cs`
mode dictionary, DI block in `ServiceRegistrationExtensionsBasic.cs`. Needs a fresh session after the
new tool class is added.

## Tests

Byte-level assertions on temp files: each operation, idempotence (second run reports zero changes),
`All` on a mixed-EOL BOM-less file, EOL-only and BOM-only changes actually reaching disk (the no-op
skip regression), verbatim-string reporting, dryRun writes nothing, undo restores the BOM, default
writes through other tools preserve an existing BOM, and a refreshed in-memory document reports the
file's true encoding (regression for the `Encoding.UTF8` defect).

## Rollout

0. Spike (above); record results here.
1. Chokepoint + in-memory encoding fix + tests (also closes the BOM blocker).
2. Tool + engine + tests.
3. `.gitattributes` update, then run the tool with `operation: All` at solution scope, then
   `git add --renormalize .`, review, commit as one normalization commit.

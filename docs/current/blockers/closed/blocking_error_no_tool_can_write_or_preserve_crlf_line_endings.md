# `ReplaceSnippet` / `WriteFile` write LF into CRLF `.cs` files, and no tool can fix it

**Status:** RESOLVED (uncommitted).

## Resolution

Fixed by adding EOL preservation to the write chokepoint (ApplyProposedChangesAsync). Changes:

1. **EolUtilities.cs**: Added `DetectDominantEol(string)` overload to detect line endings from string content (complements the existing SourceText version).

2. **PersistentWorkspaceManager.cs**:
   - Added `_knownFileEolPresence` dictionary to track each file's dominant EOL style, populated on `LoadSolutionAsync` and updated after each successful write.
   - Modified `ApplyProposedChangesAsync` to normalize incoming content to the file's existing EOL before writing (for existing files only; new files keep LF as-is).
   - Updated `_internalChanges` tracking and hash/EOL baseline updates to use the normalized content.

3. **DiskWriteRoundTripTests.cs**: Added two regression tests:
   - `Write_CrlfFileEditedWithLf_PreservesOriginalCrlfAsync`: Verifies CRLF files stay CRLF when edited with LF content.
   - `Write_LfFileEditedWithCrlf_PreservesOriginalLfAsync`: Verifies LF files stay LF when edited with CRLF content.

All tests pass; build is clean (0 errors).

## Original problem

Agents (especially small models) type LF line endings. When tools like `ReplaceSnippet` and `WriteFile` spliced new LF content into CRLF files without normalization, the file drifted toward LF, breaking the repo's `.editorconfig` requirement (CRLF on Windows). The failure was silent — no diagnostic warned the agent.

## Root cause (traced)

- **WriteFile** (RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs:62): Passed content straight to `ApplyProposedChangesAsync` with no EOL handling.
- **ReplaceSnippet**: Spliced new content into the file without calling `EolUtilities.NormalizeEol`.
- The write chokepoint (`ApplyProposedChangesAsync`) had no EOL preservation (unlike the BOM preservation that was added separately).

## Design

Mirrors the BOM preservation approach already in place:
1. Detect the file's original line-ending style on load.
2. Store it in a concurrent dictionary keyed by file path.
3. Before writing to an existing file, normalize new content to match that EOL.
4. Update the dictionary after a successful write to track any EOL changes.

New files are not normalized (they keep the agent's LF), so future edits inherit their source EOL. Existing files preserve their existing EOL unconditionally.

## Testing

Two regression tests verify:
- A CRLF file edited with LF content stays all-CRLF.
- An LF file edited with CRLF content stays all-LF.

All 9 DiskWriteRoundTrip tests pass (7 existing + 2 new).

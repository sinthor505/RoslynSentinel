# `Member` (and the shared write path behind it) silently strips the UTF-8 BOM from a `.cs` file on write

**Status:** OPEN 2026-10-01. Root cause is TRACED to the write chokepoint (see "Source trace"). It
is not specific to `Member`: every mutating tool that writes through `ApplyProposedChangesAsync`
inherits it. Which tools were observed to trigger it is narrower (see "What is and is not
confirmed"). The `MemberRefactoringEngine.cs` BOM was deliberately NOT restored, so the defect stays
visible in `git diff`.

## What was being attempted

Implementing the `callSiteFixups` parameter on `ConstructorParameter(add)` (unblocks
`blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md`). The work added methods to
`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` using `Member` and `ReplaceSnippet`.

## The exact symptom

`Git diff` on `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` shows line 1 changed from
`<U+FEFF>using Microsoft.CodeAnalysis;` to `using Microsoft.CodeAnalysis;`. The only intended change
to the file was added methods. The BOM removal is an unrequested whole-file side effect, and it makes
the file show as modified at line 1 in every future diff and blame.

`RefactoringSignatureImpl.cs` and `RefactoringSignatureTools.cs` (also edited this session, via
`ReplaceSnippet`) show no line-1 change.

## Calls that touched the file (from the implementer-senior transcript)

1. 07:02:47Z, `Member(operation: "addMember", filepath: ...\RoslynSentinel.Engines.Basic\MemberRefactoringEngine.cs, containerName: "MemberRefactoringEngine", position: "before:AddConstructorParameterAsync")`. Added `ResolveAddConstructorTargetClass`. First successful write to this file in the run.
2. 07:04:13Z and 07:04:24Z, `ReplaceSnippet` edits to the same file. These came after call 1, so they cannot show whether `ReplaceSnippet` preserves a BOM.
3. 07:07:46Z, `Member(operation: "addMember", position: "after:AddConstructorParameterToDocumentAsync")`. Added `AddConstructorParameterWithCallSitesAsync`.

Transcript: `C:\Users\Administrator\.claude\projects\c--Users-Administrator-source-repos-RoslynSentinel\ba2f6e48-c350-411c-b02d-dd69691a95ce\subagents\agent-a827e6de3ba412158.jsonl`.

## Source trace (read-only MCP tools)

- `RoslynSentinel.Common/PersistentWorkspaceManager.cs` `ApplyProposedChangesAsync`, around line 1420:
  `await FileIoHelper.WriteAllTextAsync(filePath, newContent, cancellationToken);`
- `RoslynSentinel.Common/FileIoHelper.cs` line 42, inside `WriteAllTextAsync`:
  `await File.WriteAllTextAsync(filePath.Absolute, content, cancellationToken);`
  This is the three-argument overload with no `Encoding`. .NET then writes UTF-8 **without** a BOM.
- The read side is symmetrical: `FileIoHelper.ReadAllTextAsync` uses `File.ReadAllTextAsync(path, ct)`,
  which detects and discards a leading BOM. The in-memory text and `newContent` therefore never
  contain U+FEFF, and nothing records that the original file had one.
- A text search for `File.WriteAll*` outside tests finds no other `.cs` write path in the tool layer
  (only `MsToolAugmentEngine.FormatDocumentSafeAsync` line 721, also with no encoding, and
  non-source JSON writers that deliberately use `new UTF8Encoding(false)`). So `Member`, which
  builds its new text in the engine and hands it to `ApplyProposedChangesAsync`, has no BOM handling
  of its own to blame.

Conclusion (traced): the BOM is lost because the chokepoint reads with BOM detection and writes with
no encoding, discarding the original encoding. Any file that had a BOM at HEAD loses it on its first
write through this path, whichever tool initiated it.

## What is and is not confirmed

- Confirmed: `MemberRefactoringEngine.cs` lost its BOM during a session in which it was written by
  `Member` and `ReplaceSnippet`.
- Hypothesis: `ReplaceSnippet` strips it equally. The code path is shared, but I could not isolate it
  because `Member` wrote first.
- Hypothesis: `RefactoringSignatureImpl.cs` and `RefactoringSignatureTools.cs` simply had no BOM at
  HEAD, which is why they show no line-1 change. I did not verify their HEAD bytes (no MCP tool
  exposes raw bytes, and shell reads of `.cs` files are barred by the dog-fooding rule).
- Not tested: whether `CreateFile`/`WriteFile` emit a BOM for new files (repo convention is mixed).

## Why this matters

- Silent: no tool reports an encoding change, and the "AppliedChangeSummary" looks normal.
- Diff noise: line 1 of every BOM-bearing file changes on first edit, and any mixed BOM/no-BOM repo
  drifts file by file as agents edit it.
- Per the failure doctrine, the environment should preserve a file's encoding by default.

## Suggested direction (not implemented)

Capture the original file's BOM presence (and encoding) when it is read for the workspace, and have
`FileIoHelper.WriteAllTextAsync` write with a matching `UTF8Encoding(encoderShouldEmitUTF8Identifier)`.
Add a regression test that writes a BOM-bearing temp file through `ApplyProposedChangesAsync` and
asserts the first three bytes are unchanged.

## Related observation: false-positive SessionHalted drift latch

- When: 07:03:44Z, right after the first successful `Member` write (07:02:47Z).
- Trigger: the first `ReplaceSnippet` `apply` against the same file, using a lowercase `c:\` path.
  An earlier capital-`C:\` path had returned "File not found", so path-case differences may be
  involved (hypothesis).
- `ListExternalDiskChanges` (07:03:49Z) listed only `MemberRefactoringEngine.cs`.
- `Git diff` showed only the intended edit plus the BOM removal, so no external writer existed.
- Cleared with `AcknowledgeExternalFileChanges` at 07:04:04Z. It did not recur.
- Hypothesis (untraced): the drift detector compared the on-disk bytes (BOM stripped by `Member`'s
  write) against a baseline that still reflected the BOM-bearing original, and read its own write as
  external. This fits the known pattern in memory "ExternalDrift false-positive vs git status".
  Whether the BOM mismatch or path-case handling is the actual cause was not traced.

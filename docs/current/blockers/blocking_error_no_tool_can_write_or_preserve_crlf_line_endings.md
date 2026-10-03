# `ReplaceSnippet` / `WriteFile` write LF into CRLF `.cs` files, and no tool can fix it

**Status:** OPEN 2026-10-02. WriteFile symptom confirmed by byte count and traced to the verbatim apply path; the ReplaceSnippet half is observed in the file but not traced to a source line.

## What was being attempted

Normalize `RoslynSentinel.Tests.Battery.Basic/PreviewInstanceMoveCallSitesTests.cs` back to CRLF, as the
repo requires (`.editorconfig` line 37: `end_of_line = crlf`; `.gitattributes`: `* text=auto`). The
file was a CRLF file until an implementer subagent converted tests with `ReplaceSnippet`
(commits 85a94ae .. 47d4180). After those edits a byte count showed 173 CRLF and 825 bare LF.

Probe for a fix through the MCP surface: `WriteFile(operation: CreateFile, filepath:
RoslynSentinel.Tests.Battery.Basic/EolProbeScratch.cs, content: <5 lines>)`, then a read-only byte
count. The probe file was removed afterwards with `DeleteFile`.

## The exact symptom

Byte count of the probe file written by `WriteFile`:

```
CR=0 LF=5
```

The tool echo of the call shows the content as `\n`-separated only, so a model cannot even request
CRLF: there is no parameter for it and the content string carries LF.

Byte count of `PreviewInstanceMoveCallSitesTests.cs` after the ReplaceSnippet batches:

```
CRLF=173 bareLF=825
```

## Source trace

- `WriteFile` applies the string verbatim: `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs:62`
  passes `changes` straight to `ApplyProposedChangesAsync` with no EOL step.
- EOL handling exists only in `RoslynSentinel.Common/EolUtilities.cs` (`DetectDominantEol`, line 7;
  `NormalizeEol`, line 20) and is called from `DiffEngine.cs:105`, `RoslynFormattingHelper.cs`
  (lines 55, 66, 125, 151, 168, 203, 238, 294) and `MemberRefactoringEngine.cs` (lines 463, 1616,
  2052). The `ReplaceSnippet` path is not in that list.
- Hypothesis (not traced): `ReplaceSnippet` splices `newContent` into the file without calling
  `EolUtilities.NormalizeEol`, so every multi-line `newContent` typed with LF lands as LF.
- Consequence for `DiffEngine`: once bare LF outnumbers CRLF, `DetectDominantEol` reports LF, so
  later diff-based edits will normalize toward LF and entrench the drift.

## What is and is not confirmed

Confirmed: `WriteFile` cannot produce CRLF (probe above); the fixture file is now mostly bare LF;
the repo convention is CRLF in the working tree. Not confirmed: which exact statement in the
ReplaceSnippet implementation skips normalization; whether other converted fixtures
(`ModifyAttributeBatchTests`, `MemberSingleDeclarationTests`, `MemberInsertAfterEolTests`,
`MutatingToolRejectionMessageTests`) have the same drift - not counted.

Shell rewrite of the file was not used: `.claude/hooks/enforce-dogfood.ps1` blocks shell access to
`.cs`, and the dog-fooding rule forbids routing around it.

## Why this matters

Agents (especially small ones) type LF. Every multi-line edit to a CRLF file silently shifts the
file toward mixed endings, and the tool result says nothing. The failure is invisible until a diff
or `DetectDominantEol` consumer misbehaves. The environment gave the agent no way to do the right
thing and no signal that it did the wrong thing.

## Suggested direction (not implemented)

1. `ReplaceSnippet`, `ApplyDiff` and `WriteFile`: call `EolUtilities.NormalizeEol(newText,
   DetectDominantEol(originalText))` on the incoming text before writing, so edits inherit the file's
   existing endings (for `WriteFile ReplaceFile`, the existing file's; for `CreateFile`, the
   `.editorconfig` value or CRLF).
2. Add an explicit repair operation (a `WriteFile` option or small dedicated tool) that rewrites a
   file to a stated EOL so drift can be fixed through the tools.
3. Regression test: a CRLF file edited by each tool with LF `newContent` must stay all-CRLF.

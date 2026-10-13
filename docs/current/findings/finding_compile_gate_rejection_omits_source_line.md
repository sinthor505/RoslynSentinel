# Finding: Compile-gate rejections do not quote the offending source line

**Status:** OPEN 2026-10-12. Phase 1 fix proposed (carry and print the candidate source line); not yet approved or scheduled.

## Context
SubAgent run `20261013-044103-799-354f4699` (qwen 9B on LM Studio). Brief: add a distinct error in
`ContextHelper.FindExactSnippetPosition`. At turn 14 and again at turn 20 the model called
`Member(operation: replace, memberName: FindExactSnippetPosition)` and was rejected by the compile gate.

## What is broken
Rejection text (turns 14 and 20):

> Member: the change was valid and matched its target(s), but introduces new compiler errors - change not applied.

with detail:

> CS1503 at RoslynSentinel.Common\ContextHelper.cs:332: Argument 1: cannot convert from 'string' to '(string Before, string After)'

Turn 14 also carried a CS0117 for a missing enum value; that one was an accurate, actionable hint.

The CS1503 was the model's own typo: `descriptions.Add(($"... and {n} more"));` on a
`List<(string Before, string After)>`. The extra parentheses collapse the argument to a single string.
At turn 20 the same CS1503 came back alone and the model did not fix it. It switched tools instead
(WriteFile on `ContextErrorBuilder.cs`) rather than fixing one line.

The message identifies the error by a line number in the post-edit file (332) that the model never saw,
and gives no source text and no offset within the submitted `newMemberSource`. A 9B model cannot map
"file line 332" onto a line of its own ~40-line snippet.

(Reasoning trail: I have only the brief's summary of turns 14/20, not the transcript. The claim that
the missing line mapping caused the tool switch is a hypothesis consistent with the evidence, not
verified from the model's reasoning text.)

## Root cause
The information is dropped at three layers:

1. `DiagnosticInfo(Id, Severity, Message, FilePath, StartLine, StartColumn, EndLine, EndColumn, Project)`
   (`RoslynSentinel.Common/DiagnosticReport.cs:54-65`) has no source-text field. It is populated by
   `DiagnosticExtensions.ToInfo` from `Location.GetLineSpan()` (path and line/column only), called from
   `ValidationEngine.cs:232`. `ValidationEngine` builds the candidate with `SourceText.From(newContent)`,
   so the line text is in hand there but is not carried forward.
2. `CompilerErrorLookupHelper.DescribeAsync` (`RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs:30-61`)
   formats the single case as `"{Id} at {FilePath}:{StartLine}: {Message}"` (plus an optional hint) and the
   grouped case as `"{Id} (xN): {Message} at: {locations}"`. No source text. 12 production callers in 7 files.
3. The rejection sentence is a literal in `RoslynSentinel.Common/ValidateAndApplyHelper.cs:80-86`
   (`ValidateAndApplyAsync`; used by Member via `RefactoringStructuralImpl.ValidateAndApplyAsync`,
   16 callers; 9 direct callers in 9 files). `ReplaceSnippet` has its own copies:
   `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:654-662` (single; code `Exception`, no
   `StructuredDetail`) and `:896-899` (batch). ApplyDiff/ApplyUnifiedDiff copies: `WholeFileWriteTools.cs:397,:530,:664`,
   `WorkspaceTools.cs:284,:392`, `WorkspaceProjectManagementImpl.cs:492`.

Line numbers for the helper and DescribeAsync were re-read against source on 2026-10-12; the other
call-site line numbers are from an earlier source-level exploration and were not re-verified.

## Why it matters
Under the failure doctrine the environment is the expert. A compiler error that points at a line the
caller cannot see does not make recovery immediate; weak models are pushed to abandon the structured tool
(here, to WriteFile) for a one-character fix. This runs against the mission of keeping small models on
structured tools. The `ValidationFailed` path is shared by most write tools, so the gap is repo-wide,
not Member-specific.

## Recommendation
Decided direction (not yet scheduled):

1. **Carry the candidate source line.** Add an optional trailing `string? SourceLine = null` to
   `DiagnosticInfo` (source-compatible for existing constructors), filled in `ValidationEngine` from the
   candidate `SourceText` (line containing the diagnostic start, trimmed, capped ~160 chars). Print it in
   `DescribeAsync`: `"<Id> at <file>:<line>: <message>\n    > <source line>"`. Because `DescribeAsync` is the
   shared detail formatter, this covers Member, ReplaceSnippet and the WriteFile family in one place.
   For the incident, the model would have seen `> descriptions.Add(($"... and {n} more"));`, which is
   sufficient to spot the extra parentheses.
2. **Optional, only if (1) is not enough:** report the line offset within the submitted snippet
   ("line 8 of your newMemberSource"). Needs the call site to know the snippet start line.
3. **Separate cleanup, not this change:** `ReplaceSnippet`'s two literal copies use
   `ToolErrorCode.Exception` and omit `StructuredDetail`, unlike Member's `ValidationFailed`. Consider
   routing them through the shared helper.

Open questions:
- Cap for the quoted line, and cap on the number of quoted diagnostics (detail is already grouped by Id+Message).
- Quote for warnings? Leaning no (errors only).
- Effect of the extra text on `structuredDetail` size and the large-detail offload threshold
  (`ResultError.ForPossiblyLargeDetailAsync`).

Cost and risk: `DiagnosticInfo` is serialised into `structuredDetail`, so anything comparing serialised
JSON would change; none found by search. Test coverage: no test asserts on the rejection sentence or on
`structuredDetail`. `ValidationFailed` as a code is asserted in `TestCategoryApplyTests.cs:201/229/255` and
`ResultStatusTests.cs:235`; `CompilerErrorLookupHelperTests.cs:73/115/142` assert on the `DescribeAsync`
detail body (assertions not read, so whether a new appended line breaks them is unknown). Passing those
tests would not show the change is right: the change needs a new test that a single-diagnostic rejection
detail contains the source line, and one that a grouped (xN) detail behaves as decided. Planner should
specify those; reviewer should confirm they exercise `DescribeAsync` output, not only `ValidationEngine`.

## Out of scope
- Diagnosing why the 9B model switched tools rather than retrying; only the missing-information
  environment defect is addressed here.
- Changing the rejection sentence wording.

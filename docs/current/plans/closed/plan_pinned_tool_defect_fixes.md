# Plan: Fix the tool and engine defects pinned by tests during the Battery reorganisation

**Status:** COMPLETE 2026-10-25 (moved to closed/; #12 pin comment reworded in 82a8771f, nothing left open; the accepted deferrals below stay marked inline and the three #20 feature gaps stay in TODO.md). Earlier status 2026-10-22. DONE: #1 (63c46bfc), #5/#6 (92567696), #14 (81d25534), #13 (cc95ff2d), #15 (02ca2fb6), #11 (4da6d32c), #17 (668cc3d2), #18 (a51fb3bd), #16 and #19 (1ddc6f79), #10 ConvertToSwitch (05a0831c) and ConvertToPattern (7a0d8fbe), #4 (5c99ee20; the range must start with an IDisposable declaration), #8 (7728fbd0; refuses NotImplemented), #2 (c33aeec8; new `ExtractMembersToPartialWithSourceAsync`), #3 (9e5bc5ff; new `{From}To{To}Mapper.cs`), #20 test-only parts and #21 TODO reword (b0481af0); #7 was already fixed. #9 (8813f43a) and the `AdvancedStructuralEngine` no-op of #10 (24e3b2b4) also landed. #12 comment part (13a5333b), #20 disk reads (1fc7ccfe), #10 `FormatDocument` AlreadyInTargetState (4e651693) and #21 engine fix (039eacf6, `TestingEngine.AddBenchmarkStub`) also landed. #12 stray CRLF on a blank line is SUPERSEDED 2026-10-25 by `plan_eol_policy_and_write_chokepoint.md` Step 5 (whole-file EOL conversion at the chokepoint in every mode; no `MatchMiddle` change, owner decision: no mixed-EOL tolerance); its pin test is reworded when Step 5 lands. Nothing else is OPEN in this plan. (Battery25 DetectUnreachableCode message, FindMissingCancellationTokens fixture and AnalyzeExceptionHandling assertions are done.) ACCEPTED 2026-10-22: `ExtractSuperclass` and `InlineParameter` stay deliberately unimplemented (refuse with NotImplemented). ACCEPTED 2026-10-22 (inline comments added, do not re-litigate): `FormatDocumentAsync` stays as dead code kept for its 11 tests (each marked); the remaining #20 feature gaps (GenerateConstructor ignores auto-properties, DocumentPocoFields skips fields, GenerateTestScaffold emits xUnit/Moq usings), #22 (CheckPackageInconsistency disk read, `UpdatedText` omitted from JSON summaries) and #24 (the `// Document not found.` result string at ~36 engine sites) are deferred to later work; the throw-migration covers #22/#24. Umbrella plan; slices land one by one. Defect list, causes and numbering (#1-#24): findings/closed/finding_pinned_tool_defects_from_battery_reorganisation.md.

## Problem
Tests pin 24 tool/engine defects with `// TODO` markers. Silent wrong results (success with no edit, always-invalid output, dropped comments)
and leaked exceptions hurt weak models most. See the finding for evidence per defect.

## Decision
- One umbrella plan, slices by root-cause group, each slice one Haiku `implementer` dispatch (D0: files<=3, edits<=10).
- Every fix flips its pinning test: the slice updates the test to assert the correct behaviour and removes the `// TODO`.
- Where a real fix needs design, ship an honest refusal first (clear, actionable error; no write).
- Approved design choices: #3 emit a new file with a complete static class (never overwrite the source); #4 require the range to start with a
  local declaration of an `IDisposable`, otherwise refuse; #8 refuse now; #9 infer property types from the semantic model (deferred, medium);
  #12 read-only trace first; #20 per tool only when that tool is worked on; #24 stays with throw-migration S1.

## Execution rules
- C# edits dispatched sequentially to `implementer`; main session briefs, reviews, runs tests, commits (explicit file list, `Plan: plan_pinned_tool_defect_fixes` trailer).
- Build 0 errors per slice; run the owning test project; one `RunTest` at a time; compare with baselines.
- Anything needing user judgement is deferred and recorded in the Deferred section, not decided here.

## Steps

### Step 1 - Guard group A and C (#1, #5, #6)
- Files: `Tools.Advanced/AdvancedRefactoringTools.cs` (WrapRangeTool guards), `Engines.Advanced/.../MemberRefactoringEngine.cs` (attribute target resolution), their pinning tests.
- Done when: WrapRange on a ctor-header range returns a structured error, not an NRE; ModifyAttribute on a class named like its ctor edits the type; #6 asserts on `ErrorData.Detail` first.

### Step 2 - One-liners (#7 refusal, #14, #13, #11, #15)
- Done when: each pinning test asserts the corrected behaviour and the TODO is gone.

### Step 3 - Contract and no-op group (#17, #18, #10 per engine, #16)
- #10 uses `AlreadyInTargetState`, not the obsolete `NoChange`; one engine per slice (ConvertToSwitch, ConvertToPattern, FormatDocument).

### Step 4 - #2 ExtractMembers partialClass
- Remove the moved members from the source and add `partial`; <=3 files.

### Step 5 - Test-only corrections (#19, #6 wording, throw-proposal appendix lines 99-103)

### Step 6 - Approved refusals and designs (#4, #8, #3, #9)
- #4 and #8 refusals; #3 new-file emit; #9 type inference (last).

### Step 7 - Trace #12 (read-only) and record the outcome

## Out of scope
- The `throw-migration` markers, #20 (until a tool is touched), #21-#24.

## Risks and open decisions
- Causes were read from source, not run; each slice's agent re-verifies before editing and replies RESCOPE: if wrong.
- Deferred items (needs user judgement) are appended below as they arise.

## Deferred
- #12 stray CRLF on a blank line moves out of an extracted selection. Not an `ExtractMethodSafe` bug: `EolUtilities.MatchMiddle` (`RoslynSentinel.Common/EolUtilities.cs` ~214/~226) matches whitespace-only lines by text, so the old blank line's CRLF binds to the first generated blank line. Fix would be global to every `FormatAnnotatedSpansAsync` caller (skip empty-text lines in the LCS match); decide whether that is worth the blast radius or reword the pin (`MsToolAugmentEngineFormatCharacterizationTests.cs`, mixed-EOL test) as an accepted limitation.
- #9 limit: a nested or private type used by an anonymous initializer may not resolve in the new top-level file; the compile gate refuses the write. Only the first anonymous object in the file is converted.

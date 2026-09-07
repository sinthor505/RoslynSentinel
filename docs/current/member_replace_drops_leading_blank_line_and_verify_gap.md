
Found while investigating a .113 `Model_FixesWholeFileRewriteBug_PlanImplementVerify` failure
(run 8/10 of the second overnight PIV batch, archived at
`ModelTestingResults/113/Model_FixesWholeFileRewriteBug_PlanImplementVerify/20260907-053919-255`).
The plan/implement/verify phases each have their own `transcript.json` — used the same
reconstruct-and-interrogate technique from [[project_qwen36_35b_ladder_preferred_branch_extract_bug]]
(rebuild OpenAI-format messages from `transcript.json`, append a follow-up user turn, replay via
raw `/v1/chat/completions` against `.112`) to get the model's own explanation once, on the same
day's investigation, this time targeting a PlanImplementVerify (not ladder) failure.

**The bug**: the model correctly followed the given fix plan — changed `ReplaceBlockFormatted`
from `private` to `internal` (via `ChangeAccessibility`), rewrote `ConvertAbstractClassToInterface`
to call it (via `Member(operation:replace)`), and deleted the dead `ReformatWholeFile` method (via
`Member(operation:remove)`). Build succeeded, 0 errors. But the final file has a formatting defect
the task explicitly tests for ("everything else... byte-for-byte unchanged... no incidental
reformatting"): the blank line that should separate `UnrelatedMethodBefore` from
`ConvertAbstractClassToInterface` is gone —

```
    public string UnrelatedMethodBefore(int x, int y)
    {
        return (x + y).ToString();
    }
    public string ConvertAbstractClassToInterface(string fileText, string className)
```

— while the blank line before/after `UnrelatedMethodAfter` (untouched by any tool call) is intact.
This points at `Member(operation:replace)`'s leading-trivia handling: when it replaces a member, it
appears to not preserve the blank line immediately *before* the replaced member's declaration,
even though the member's own body/trailing trivia round-trips fine. Not yet root-caused in the
tool's C# implementation itself — this file documents the symptom and the verify-phase gap it
causes, not a fix.

**The self-verification gap (the more actionable finding)**: the model's own verify phase read the
final file, declared "no unrelated changes... byte-for-byte unchanged" and returned
`VERIFIED: PASS` — but NUnit failed the run. Asked directly, in-context, why it reported success,
the model correctly self-diagnosed on the first try: *"Neither a visual scan nor a compiler check
catches subtle whitespace or blank-line inconsistencies... I should have compared the raw newline
sequences between every method definition to ensure the fix didn't strip or add blank lines."* It
then reversed its own verdict to `VERIFIED: FAIL` unprompted. This confirms a hypothesis floated in
[[project_qwen36_35b_ladder_preferred_branch_extract_bug]]'s Phase-12 update: the verify step
pattern-matches on "does the logic/structure look right" (signatures, control flow, build status)
rather than doing an actual line-by-line/whitespace diff against a known-good baseline — and this
is a general property of how the model verifies, not specific to the ladder bug's branch-extraction
scenario. The model *can* catch this class of bug when explicitly told to look for it; it does not
spontaneously check for it during normal self-verification.

**How to apply**: two independent, non-overlapping levers here:
1. **Tool-side**: investigate `Member`'s `replace` operation (likely in `RefactoringEngine` or
   wherever `Member` is implemented) for how it computes/preserves leading trivia on the replaced
   node — compare against how `ApplyDiff`/`ApplyUnifiedDiff` anchor and preserve surrounding
   blank-line context, since those don't appear to have this defect. A minimal repro: any file with
   `methodA() {...}\n\nmethodB() {...}` where `Member(operation:replace, memberName:"methodB")` is
   called — check if the blank line before `methodB` survives.
2. **Prompt/verify-phase side**: the PlanImplementVerify verify prompt could explicitly instruct the
   model to diff raw file bytes/whitespace against the pre-edit read, not just re-read and
   eyeball — same category of fix as the reason-param and Disambiguated prompt tightenings that
   worked elsewhere. Given the model demonstrably CAN do this check correctly when asked, this is a
   promising, low-risk wording addition to try on the verify-phase system prompt.

**Batch context, FULLY categorized (2026-09-07)**: this batch (2nd .113 PlanImplementVerify
overnight batch, 10 runs, 5 fail / 5 pass) had all 5 failures read in full. Result: **zero were
genuine model reasoning/coding mistakes.**

| Run | Archive | Cause | Category |
|---|---|---|---|
| 1 | `20260907-043332-733` | LM Studio stream aborted mid-plan-explanation, before any tool call | Infra |
| 5 | `20260907-051058-411` | `Member(operation:remove)` (deleting `ReformatWholeFile`) reformatted `UnrelatedMethodBefore`'s spacing from `( int    x , int y )` to `(int x, int y)`. Model's own logic was fully correct. **Model caught the drift itself** — turn 9 reasoning: *"Wait - I see that the `UnrelatedMethodBefore` method's formatting got changed... Let me check the original more carefully"* — tried `UndoLastApply`, reapplied the same `Member(remove)` call, got the identical corruption back, gave up chasing it. | Tool bug |
| 6 | `20260907-052214-110` | Same symptom via a different call sequence (`ChangeAccessibility` + `Member(operation:replace)`). Model never noticed, declared success without re-diffing against the true original. | Tool bug |
| 8 | `20260907-053919-255` | `Member(operation:replace)` dropped the blank line before the replaced member (this file's original finding) | Tool bug |
| 9 | (streaming abort, run 9) | LM Studio stream aborted mid-generation | Infra |

**This broadens the diagnosis**: it is not narrowly "`Member(replace)` drops a leading blank line."
`Member`'s write path reformats unrelated content elsewhere in the file under at least 3 different
call patterns (`replace` alone, `remove` alone, `ChangeAccessibility`+`replace` combined) — both as
a dropped blank line AND as method-signature re-spacing (deliberately-odd spacing normalized to
conventional spacing). This is squarely a tool bug in `Member`'s write/formatting path, not
something addressable via prompt wording — run 5 is the strongest evidence of that: the model
correctly identified the corruption in its own reasoning and attempted a legitimate recovery
(`UndoLastApply` + retry), and the SAME tool bug reproduced identically on retry. No amount of
prompt tightening fixes a bug that reproduces even when the model does everything right and
actively tries to route around it.

**Combined with [[project_qwen36_35b_ladder_preferred_branch_extract_bug]]'s 12/17 ladder
failures (a real model instruction-misreading bug) and the 3/17 ladder LM Studio streaming
aborts**, the overall picture for tonight's .113 overnight testing: the model's actual reasoning
failure rate is lower than raw pass/fail tallies suggest — a meaningful chunk of "failures" across
both batches are tool-side formatting bugs or LM Studio infra flakiness, not model incapability.
This is direct supporting evidence for [[project_modeleval_fixture_test_suite_redesign]]'s
premise: today's brittle text-matching assertions are actively producing wrong conclusions about
model capability, and both the `Member` formatting bug (user fixing separately) and the fixture
redesign (design written, not yet implemented) are the correct fixes — not further prompt
engineering.

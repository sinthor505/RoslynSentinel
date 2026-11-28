# Finding: passing ImplementationScenario runs are classed SilentIncomplete under the default (legacy) contract

**Status:** CLOSED 2026-11-28. Fixed by plan_outcome_class_contract_aware_classification (447e4202, 44a1391e, d8589a07, 701dc48e, 7e02d5b3, d17f08f8, e31a1bce); live smoke on 113u classed a passing legacy run CorrectDone. Option 2 (default to standing) stays with proposal_modeleval_standing_workflow_contract.

## Context

Found while tabulating the replay-form runs of plan_triggered_thinking_reasoning_as_content
(`scripts/Get-ThinkingArmTable.ps1`). Every run had `outcomeClass=SilentIncomplete`, passes included:
- the Step 8 runs at commit 5006238e;
- the 48 slip-sibling runs at commit 56ed5373 (WidenAcrossFilesSlip, WidenDictionaryLeafFirstSlip,
  SweepNamedSitesSlip; Qwen3.5-9B on hosts 112/113, 2026-11-27).

## What is broken

- 43 of the 48 slip runs graded `Pass`, yet all 48 are `SilentIncomplete`.
- Example outcome line:
  `WidenAcrossFilesSlip step 1: outcome=Pass, turns=12, ... stopReason=ModelFinished. ok outcomeClass=SilentIncomplete`
  (`step-1.outcome.txt`, run `ImplScenario_WidenAcrossFilesSlip_Step1\20261127-055740-340` on 112).
- None of the 48 final messages has a line starting with `DONE:` or `BLOCKED:`. They end with a
  free-form "## Summary" report.
- All 48 transcripts carry `ContractHash=81cf1b6a07a9`, `ContractPlacement=system`.
- Their system prompt has no `DONE:` anywhere. Its sections are the legacy coding-agent ones:
  "When you are blocked", "Example", "Workflow". Workflow step 6 says "reply with your report ...
  and no tool call". Only the blocked path names a token ("Begin your final message with `BLOCKED:`").

## Root cause

- `WorkflowContract.ParseVariant` (`RoslynSentinel.AgentLoop/WorkflowContract.cs:59-73`) maps an unset
  `ROSLYNSENTINEL_MODELEVAL_CONTRACT` to `ContractVariant.Legacy`.
- `TextFor(Legacy)` returns `AgentSystemPrompts.CodingAgent` (`WorkflowContract.cs:34`), which never
  asks for `DONE:`. Only `CodingAgentContract` (the standing variant) defines the `## Endings` section
  (`AgentSystemPrompts.cs:201`).
- The grader classifies regardless of variant:
  - `ImplementationScenarioChecks.cs` (the local `Verdict` function) parsed the token with
    `FinalOutcomeToken.Parse` (None here) and called `ScenarioClassifier.Classify`.
  - `ScenarioVerdict.cs` `ScenarioClassifier.Classify` returns `CorrectDone` only for
    `token == Done && grade == Pass`. Every other Done-expected case falls through to `SilentIncomplete`.
- So under the default contract, every Done-expected run that is not Bypass or FalseBlocked is
  `SilentIncomplete`, whatever its grade.

Secondary:
- `EvalContractOptions.BuildContractStamp` stamps `ContractPlacement=system` for legacy runs too
  (`RoslynSentinel.Tests.Harness/EvalContractOptions.cs:77-78`). The stamp then reads as "Endings
  placed in the system prompt" when there are no Endings.
- `docs/generated/configuration.md:254` describes `ROSLYNSENTINEL_MODELEVAL_CONTRACT` as "Path of the
  eval contract file". It is a variant name (legacy/standing).

## Why it matters

- `outcomeClass` is the eval's signal for "the model finished and said so" versus "stopped
  silently". Under the default it is constant, so arm tables (`outcomeClasses` column) and any
  roll-up on CorrectDone/SilentIncomplete carry no information.
- A reader can take it for a real behaviour finding ("the model never reports completion").
- Nothing warns that the classification is meaningless for the chosen contract.

## Recommendation

Open, not decided. Options by leverage:

1. Make the classifier contract-aware. Under legacy, treat a `ModelFinished` stop with no token as
   Done (CorrectDone on Pass), or emit a distinct class such as `Unclassified`/`NoTokenContract`.
   This needs the variant in the verdict context.
2. Default `ROSLYNSENTINEL_MODELEVAL_CONTRACT` to `standing` for ImplementationScenario runs, so the
   prompt asks for the token the classifier expects. This changes the prompt baseline of every eval,
   so it needs an A/B and the owner's call.
3. Stamp `ContractPlacement` as empty or `none` for legacy runs, and fix the generated config
   description. Cheap and independent of 1/2.

## Resolution

Plan: [plan_outcome_class_contract_aware_classification](../../plans/closed/plan_outcome_class_contract_aware_classification.md) (decisions D1-D6, 2026-11-27).

- Options 1 and 3 done, option 2 left to proposal_modeleval_standing_workflow_contract (its A/B is open).
- Classifier: under the legacy contract a finished run with no token and a non-blank final message is classified as if it said `DONE:` (`ScenarioClassifier.EffectiveToken`). Pass is CorrectDone, any other grade is SilentIncomplete; a blank message stays SilentIncomplete. Under `standing` nothing changes.
- Legacy runs stamp `ContractPlacement=none`; the `ROSLYNSENTINEL_MODELEVAL_CONTRACT` description now says it is a variant name.
- Runs recorded before the fix commit are not comparable on `outcomeClass` (all SilentIncomplete under legacy); their grade is still correct. Old runs are not re-classified.
- Live smoke (plan Step 8, 2026-11-28, host 113u, committed tree 95c201a3, contract env var unset): WidenAcrossFilesSlip step 1 ended `outcome=Pass ... stopReason=ModelFinished ... outcomeClass=CorrectDone`, and the transcript stamps `ContractPlacement=none`. Before the fix the same shape of run was `SilentIncomplete` (48 of 48 recorded slip runs).

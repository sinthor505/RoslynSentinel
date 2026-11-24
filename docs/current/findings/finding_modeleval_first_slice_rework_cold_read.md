# Finding: cold read of the 15 reworked first-slice prompts and 5 stop-family prompts finds two wording defects fixed and four owner questions open

**Status:** OPEN 2026-11-23. Two clear-cut wording defects are fixed (0747e792); four questions that change what a scenario asks or how every prompt renders wait for the owner (Recommendation).

## Context
Step 51 of [plan_modeleval_first_slice_scenario_rework](../plans/plan_modeleval_first_slice_scenario_rework.md) (authoring rule 7). The rendered step prompts (`Steps[k].Prompt`), the step-start files (`FilesForStep(k-1)`, visible tests included), the five formatted stop-family messages with their starting files, and `AgentSystemPrompts.CodingAgentContract` were extracted from the built `RoslynSentinel.Tests.ModelEval.dll` at HEAD 29535d2a by an extractor in the scratch folder (`RoslynSentinel-ScratchDir\scenario_rework_coldread_20261123\`). Three fresh read-only `explore-tier2` agents (sonnet) each read one input file (scenarios 1-5, 6-10, 11-15 plus the five stop-family messages) and nothing else: no plan, grader, golden, canned message or variant. For each prompt they listed interpretations, questions, the reaction to a failed Condition, what they would leave on disk, the final message, and an ambiguity rating.

## What is broken
Ratings (low / medium / high), per scenario: PlanStalenessReview medium, WidenToReadOnlyDictionary low, WideningRejectedByGate medium, ChangeSetTypeFromSpec medium, RetypeSliceExactLines low, SweepConstructionSites high, PropertyFirstRetype low, RemovePinnedSentence low, ThreadOptionalParameter low-medium, NotFoundToTypedThrow step 1 medium and step 2 medium, BreakCycleWithForwarders medium, ShareHelperDifferingRules medium-high, ObsoleteNamespaceCopy medium (low after the fix), MigrateOntoEnvironmentHelper low, LargeBlockRewrite low, StopAdherenceMissingFile low, MissingMethod low, MissingFileToolError and MissingMethodToolError low-medium, StopAdherenceControl low.

Outcome classes of the findings:

- **Answered by a prompt edit (fixed, 0747e792).** S1 `Files: none: change no file.` read as a double colon: now `none (change no file).`. S1 Report `<File>=<n>` left open whether the name carries a folder: the Calls clause now says `<File>` is the file name without its folder (the grader accepts `CallerA` and `CallerA.cs`, case-insensitively, so the extension is not narrowed). S14 `has no byte order mark requirement` read as either "none needed" or "must not have one": now `needs no byte order mark` (the grader requires a BOM only on Legacy and allows zero or one on Modern). Golden, previous and variant tests for both scenarios: 20 of 20 pass.
- **Rejected, with the reason.**
  - "Make no further changes; list what you wrote" does not say whether to revert (every scenario): settled by Decision 1 and the proposal's "File state on stop" rule.
  - CRLF/BOM cannot be verified (S8, S14): the reviewers were not shown the tool list; `ReadFile(showLineEndings: Summary)` reports line endings and a BOM.
  - S3 "which files fail to build" (does ChangeWriter.cs, inside Files, count): the reviewer's literal reading matched the grader's accepted set (`ChangeWriter.cs: CS1503`, `ChangePublisher.cs: CS9174`, `ChangeWriterTests.cs: CS0144`; `WideningRejectedByGateScenario.cs:387-423`).
  - Stop-family `Files:` path `C:\work\ContosoOrders/FixtureHelpers/...` lacking `ContosoOrders.Core/`: an artefact of the extractor's dummy solution directory, not the prompt (`{0}` is the real solution directory at run time).
  - S10/S11 step 2 name collision with `TypedThrowTests`/`OutcomeTests`: possible only if the model reuses a seed class name; the grader requires both seed test files unchanged.
  - S13 and S2 small points (doc-comment placement, prose crefs, accessibility of moved members): pinned by tests, free choices.
- **Accepted as passing variants.** S6 forks A (leave the three `var`-then-reassigned sites unconverted and name them) and B (`Dictionary<FilePath, string> x = new DocumentChangeSet();`, Report `none`) both pass: `CheckSweep` flags only objects whose compiled type is exactly `Dictionary<FilePath,string>` (`SweepConstructionSitesScenario.cs:576-622`) and R3 settles B as a pass.

## Root cause
Wording of the prompts as written (the places cited above) and, for the open questions, plan decisions that fix what a scenario asks. No environment defect beyond the three fixes.

## Why it matters
A reader who cannot tell what a Report line or a Done-when sentence means scores on the grader's guess, which the rework exists to remove.

## Recommendation
Open owner questions (no edit made):

1. **S13 deviation sentence (Decision 13 (r), owner text).** "where a caller's rule differs from the helper's, a comment starting `// Deviation:` says how" has no referent when `Expected(root, folder)` takes the root as a parameter, because the helper then has no root rule (reviewer: medium-high). The grader accepts one `// Deviation:` comment in either caller file (`ShareHelperDifferingRulesScenario.CheckSharing`), the golden puts it in `ProjectStructure.cs`. Recommend rewording to "where a caller's root rule differs from the other caller's, a comment starting `// Deviation:` says how." in the DoneWhen; one sentence, grader unchanged.
2. **`If a condition fails` line on tasks with no Conditions** (for example ThreadOptionalParameter and NotFoundToTypedThrow print `PlainIfFails` with no Conditions line above it; two reviewers noted it). Recommend leaving: the contract says the same default when the line is absent, and one fixed shape is what the contract A/B compares.
3. **S6 wording** (high): the sweep prompt says "end with DONE:" for a skipped site while the contract maps a failed Condition to BLOCKED, and the two forks differ in diff and Report though both pass. Recommend no edit; add a model-free variant `ExplicitTypeLocal` (fork B, Pass) to the outcome table only if the table lacks it.
4. **Tool-error variants** (`StopAdherenceMissingFileToolError`, `StopAdherenceMissingMethodToolError`): a careful reader checks the Conditions first, finds the file or method absent through Search and never calls the named tool, which scores `ToolNotTried`. That is the designed measurement (Decision 11, Steps 60-62), so recommend keeping; the question is only whether the owner wants that reading counted as a model failure or as a prompt ambiguity when the A/B results come in.

## Out of scope
Prompt-independent observations: the `CalcDisc` doc comment in the shared FixtureHelpers fixture mentions "the model is asked to extract that shared calculation", which reads like an instruction to a cold reader (StopAdherenceControl); the fixture is shared with other scenario families and is not part of this rework.

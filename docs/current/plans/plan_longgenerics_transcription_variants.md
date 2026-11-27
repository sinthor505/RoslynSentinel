# Plan: Two transcription-first variants of the LongGenerics slip family

**Status:** IN PROGRESS 2026-11-27. Steps 1-7 done (87f7268d, d501ea47, 5d0329bd, 7b70a640, 878d8f84, 40bbce12; gate green at 40bbce12; 24 runs queued); Step 8 waits for the runs.

## Problem

LongGenericsSlip (plans/closed/plan_slip_first_scenarios.md) is meant as a transcription exercise, but
its per-site prompt ("change `Dictionary` to `SortedDictionary`; change `decimal` to `double`") makes the
model locate a token inside a four-level generic and rebuild the type. E1 plus the overnight runs at
5e5a814c (112 runs, 28 LongGenerics) show the cost:

- residual per-site slip rate 0.27 (112) / 0.22 (113), against 0.01-0.09 for the other three families;
- the four sites reworded in 91c493ad were Wrong in 83 of 94 graded attempts; the other 26 sites in 86
  of 582 (15%), with the same failure shapes: outer wrapper dropped (PickingTasks), container change
  applied at the wrong level (DiscountMatrix), wrong type argument changed (SessionTokens);
- E2 sizing at that variance: 196 / 255 runs per host, against 17-50 for the others.

The failures are comprehension (which occurrence, which level), not copy slips, so the family does not
measure what it is for.

## Decision

- D1 (owner, 2026-11-27): the slip families are transcription exercises; any reasoning a prompt demands
  beyond copying is a defect to design out.
- D2: add two new scenario ids beside LongGenericsSlip (kept as the baseline), not `PromptVariants`
  entries: the evalqueue and `roslynsentinel-modeleval.ps1` have no prompt-variant parameter (the
  variant is an env var, `TestHarnessSettings.cs:28`), and a separate id gets its own `Scenario` row
  in `Get-SlipCounts.ps1` and its own golden cases automatically.
  - `LongGenericsFullLineSlip` (variant A): the same 36 sites as LongGenericsSlip (current seeds,
    including the 91c493ad rewording); the prompt gives each changed site's complete new declaration.
  - `ConcurrentCollectionsSlip` (variant B, owner's idea): new seeds and one global type map
    `Dictionary` -> `ConcurrentDictionary`, `Queue` -> `ConcurrentQueue`, `Stack` -> `ConcurrentStack`
    (real `System.Collections.Concurrent` types, same arity, `new()` still valid); the prompt lists the
    map and the names of the members to edit.
- D3: variant B's seed rules, each preventing one failure mode: every changed site holds exactly two
  different map keys, each exactly once (the builder's exactly-once and 2-changes-per-site rules,
  `SlipFamilyBuilder.ApplyChanges`, `SlipFamilyLayout.ChangesPerSite`, stay unchanged); no seed contains
  `SortedDictionary` or any `Concurrent` name (a key inside another type name, or a target already
  present); controls contain no map key. Enforced by `Validate` (Step 1), not by review.
- D4: no tool can apply the map mechanically in an eval run, so no tool hiding is needed:
  `SemanticFindReplace` has no text replace and is not on the lean eval surface
  (`AgentToolAllowLists.Lean`); `RenameSymbol` cannot rename BCL types. A model can still write short
  `ReplaceSnippet` fragments; that is true of every slip family today and is measured, not prevented.
- D5: `PreferredHost`, turn cap and the 36-site layout are unchanged, so the three ids compare directly.
- D6 (owner, 2026-11-27, after Step 7): `SlipFamilyBuilder.TurnCap` 40 -> 60 for all slip families
  (11 of 19 baseline runs at 91c493ad hit the cap with about 30 writes each). The 27 open jobs were
  cancelled; baseline and both variants are requeued at the turn-cap commit (purpose
  `slipsite-cap60-<sha8>`), so Step 8 compares those runs only.

## Execution rules

- Dog-fooding per CLAUDE.md; every slice commits with `Plan: plan_longgenerics_transcription_variants`
  above `Co-Authored-By`.
- The default prompt style must render byte-identical prompts for the existing four families (Step 1 is
  text-neutral for them; `TranscriptionScenarioTests` and the golden Budget cases pin it).
- Implementers run `Build` only; the dispatcher runs tests.

## Steps

### Step 1 - Prompt styles and type-map validation in the slip builder
- Done: 87f7268d
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/SlipSite.cs`,
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/SlipFamilyBuilder.cs`
- Depends on: none
- Effect: text-neutral (existing families keep the default style; no caller uses the new styles yet)
- Change:
  - `SlipSite.cs`: add `internal enum SlipPromptStyle { PerSiteChanges, FullLine, TypeMap }` (new) and
    two optional trailing parameters on the record
    `internal sealed record SlipSiteSet(string FilePath, string Namespace, string ClassName, string Preamble, IReadOnlyList<SlipSite> Sites)`:
    `SlipPromptStyle PromptStyle = SlipPromptStyle.PerSiteChanges` and
    `IReadOnlyList<SiteChange>? TypeMap = null`.
  - `SlipFamilyBuilder.RenderPrompt(SlipSiteSet set)`: keep Goal, Files, IfFails and DoneWhen; build
    `Conditions` by style:
    - `PerSiteChanges`: unchanged (`"Make exactly these edits and change nothing else:\n"` plus
      `RenderChangeLine` per changed site).
    - `FullLine`: `"Make exactly these edits and change nothing else:\n"` plus one line per changed
      site: ``$"- {site.Name}: replace the declaration with `{ApplyChanges(site).Trim()}`."``
    - `TypeMap`: `"Make exactly these edits and change nothing else.\nType names to replace:\n"` plus
      one line per map entry ``$"- `{change.Old}` -> `{change.New}`"``, then
      `"\nMembers to edit (replace every listed type name in each): "` plus the changed sites' names
      joined with `", "` and a final `"."`.
  - `SlipFamilyBuilder.Validate`: when `PromptStyle == TypeMap`, add violations for: `TypeMap` null or
    empty; a changed site's change that is not an entry of `TypeMap` (same Old and New); for every site
    and every map entry, the ordinal occurrence count of `Old` in `SeedText` is not 1 when it is one of
    the site's change `Old`s, or not 0 otherwise (this rejects `SortedDictionary` and keys in controls);
    any map `New` occurring in any seed. Reuse the existing occurrence-count helper (`FindOccurrences`).
- Done when: `Build` 0 errors.
- Lane: implementer, Tier D1. Effort: S

### Step 2 - Builder tests for the new styles
- Done: d501ea47
- Files: `RoslynSentinel.Tests.ModelEval/SlipFamilyBuilderTests.cs`
- Depends on: 1. Pairs with: Step 3.
- Effect: text-neutral
- Change: add tests (small in-test `SlipSiteSet`s are fine; reuse the file's existing fixture helpers):
  `RenderPrompt_FullLine_ListsEachChangedSitesNewDeclaration`,
  `RenderPrompt_TypeMap_ListsMapAndChangedMemberNames`,
  `RenderPrompt_DefaultStyle_Unchanged` (equals the pre-change output for one existing family, e.g.
  compare with the `RenderChangeLine` composition),
  `Validate_TypeMap_RejectsKeyInControl`, `Validate_TypeMap_RejectsKeyInsideLongerName`
  (`SortedDictionary`), `Validate_TypeMap_RejectsTargetAlreadyPresent`,
  `Validate_TypeMap_RejectsChangeNotInMap`.
- Done when: `Build` 0 errors; the dispatcher runs `SlipFamilyBuilderTests`.
- Lane: test-writer. Effort: S

### Step 3 - Variant A scenario: LongGenericsFullLineSlip
- Done: 5d0329bd
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Transcription/LongGenericsSlipScenario.cs`,
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Transcription/LongGenericsFullLineSlipScenario.cs` (new)
- Depends on: 1
- Effect: text-neutral (not registered until Step 5)
- Change:
  - `LongGenericsSlipScenario.Sites`: `private` -> `internal` (`ModifyModifier`), so the variant shares
    the exact sites.
  - New `internal static class LongGenericsFullLineSlipScenario` in namespace
    `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription`, mirroring
    `LongGenericsSlipScenario`: a `private static readonly SlipSiteSet Sites = LongGenericsSlipScenario.Sites with { FilePath = "ContosoOrders.Core/Evals/LongGenericsFullLineSlip/LongGenericDeclarations.cs", Namespace = "ContosoOrders.Evals.LongGenericsFullLineSlip", PromptStyle = SlipPromptStyle.FullLine };`
    declared before
    `public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build("LongGenericsFullLineSlip", "Copy 30 long nested generic declarations given in full", "plans/plan_longgenerics_transcription_variants.md (variant A)", Sites);`
    with the same initialisation-order comment.
- Done when: `Build` 0 errors.
- Lane: implementer, Tier D1. Effort: S

### Step 4 - Variant B scenario: ConcurrentCollectionsSlip seeds
- Done: 7b70a640
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Transcription/ConcurrentCollectionsSlipScenario.cs` (new)
- Depends on: 1. Pairs with: Step 2 or 3 (disjoint files).
- Effect: text-neutral (not registered until Step 5)
- Change: new `internal static class ConcurrentCollectionsSlipScenario`, shaped like
  `LongGenericsSlipScenario` (same `Changed`/`Control` helpers), with
  `FilePath: "ContosoOrders.Core/Evals/ConcurrentCollectionsSlip/ConcurrentCollectionDeclarations.cs"`,
  `Namespace: "ContosoOrders.Evals.ConcurrentCollectionsSlip"`, `ClassName: "ConcurrentCollectionDeclarations"`,
  `Preamble: "using System.Collections.Concurrent;"`, `PromptStyle: SlipPromptStyle.TypeMap`, `TypeMap`:
  the three D2 entries; `Build("ConcurrentCollectionsSlip", "Make the collections in 30 long nested generic declarations concurrent", "plans/plan_longgenerics_transcription_variants.md (variant B)", Sites)`.
  36 sites, controls at positions 4, 10, 16, 22, 28, 34; new member names (no overlap with
  LongGenericsSlip, none starting with `Calc`); a mix of `public static readonly ... = new();` fields
  and `public static ... Name() => null!;` methods, as in LongGenericsSlip. Each changed seed: at least
  three generic nesting levels; exactly two different keys of `Dictionary`, `Queue`, `Stack`, each once,
  their starts at least 20 characters apart; the three key pairs used about equally, and keys at varied
  depths (outer, middle, inner); other types only from `List`, `HashSet`, `SortedSet`, `SortedList`,
  `KeyValuePair`; leaves from `int`, `long`, `short`, `byte`, `char`, `string`, `decimal`, `double`,
  `Guid`, `Uri`, `DateTime`. Controls: same type pool, no map key. No seed contains `SortedDictionary`
  or `Concurrent`.
- Done when: `Build` 0 errors, and `SlipFamilyBuilder.Validate(Sites)` returns no violations (checked by
  the dispatcher's `TranscriptionScenarioTests` run after Step 5).
- Lane: implementer-senior (authoring 36 seeds to D3's rules). Effort: M

### Step 5 - Register both ids and regenerate the catalog
- Done: 878d8f84, 40bbce12
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenarioRegistry.cs`,
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs`,
  `docs/generated/modeleval_scenarios.md` (generated)
- Depends on: 3, 4
- Effect: behaviour-changing (two new live-model cases `ImplScenario_LongGenericsFullLineSlip_*` and
  `ImplScenario_ConcurrentCollectionsSlip_*`)
- Change: add `LongGenericsFullLineSlipScenario.Definition` and `ConcurrentCollectionsSlipScenario.Definition`
  to `ImplementationScenarioRegistry.All` right after `LongGenericsSlipScenario.Definition`; in
  `Registry_IdsUniqueAndMatchClassNames` change `Has.Count.EqualTo(22)` to `Has.Count.EqualTo(24)`; in
  `Registry_AxisAssignment_MatchesThePlan` add both ids to the `EvalAxis.Transcription` array after
  `"LongGenericsSlip"`. Then the dispatcher regenerates the catalog: `RunTest` on
  `ScenarioCatalogFreshnessTests` with `environment: { ROSLYNSENTINEL_UPDATE_GENERATED_DOCS: "1" }`, and
  adds one sentence on the two variants to the per-site families paragraph of
  `docs/current/references/reference_model_eval_procedure.md` ("Slip instruments").
- Done when: `Build` 0 errors.
- Lane: implementer, Tier D1 (C# part); dispatcher (catalog, doc). Effort: S

### Step 6 - Gate
- Done: gate green at 40bbce12 (10 golden cases, 64 unit/catalog tests)
- Files: none (verification)
- Depends on: 2, 5
- Effect: none
- Change: `Build` (solution) 0 errors; `Search(mode: text, query: "NotImplementedException")` over the
  plan's files finds nothing new; `RunTest` (Tests.ModelEval) on `SlipFamilyBuilderTests`,
  `SiteGraderTests`, `TranscriptionScenarioTests`, `ScenarioCatalogFreshnessTests` and the registry tests
  in `ImplementationScenarioGoldenTests` (`Name~Registry_`); then, `suite: full`, the explicit cases
  `FullyQualifiedName~ImplementationScenarioGoldenTests&(Name~LongGenericsFullLineSlip|Name~ConcurrentCollectionsSlip)`
  (Seed, Golden, Previous, Budget, Files).
- Done when: all pass; per id the golden grades every site Correct and the no-op grades every changed
  site Unchanged (D18 pre-E1 gate of the predecessor plan).
- Lane: dispatcher. Effort: S

### Step 7 - Queue the comparison runs
- Done: 24 jobs queued, purpose slipsite-variant-40bbce12
- Files: none
- Depends on: 6 (and the gate commit pushed to the worktree HEAD used by the queue)
- Effect: none
- Change: per host 112u and 113u and per new id, 6 runs at the gate commit, priority 9:
  `scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress <h> -Test ImplementationScenario -Model unsloth/Qwen3.5-9B-MTP-GGUF -Session slipsite -Purpose 'slipsite-variant-<sha8> scenario=<Id> format=plain host=<h>' -Commit <sha> -Repeats 6 -Priority 9 -NoRunFolderMinutes 0 -EvalArgs '-Scenario <Id> -StepWallClockMinutes 30'`.
  Baseline: the 24 LongGenericsSlip runs already queued at 91c493ad (`slipsite-reword-91c493ad`).
- Done when: 24 jobs queued (evalqueue `status` shows them).
- Lane: dispatcher. Effort: S

### Step 8 - Compare and record
- Files: this plan, `docs/current/TODO.md`
- Depends on: 7 (runs drained, roughly 4 h after queueing)
- Effect: none
- Change: `& .\scripts\Get-SlipCounts.ps1 -Machine <112|113> -PurposePattern <pattern> -Summary -BootstrapSeed 1`
  for `slipsite-reword-91c493ad*` and `slipsite-variant-<sha8>*`. Report per id and host: D18 outcome
  classes (CleanRun, SlipRun, ShortRun, Broken; ShortRun = Correct+Wrong below 20 graded changed sites),
  residual slip rate with CI, completion, collateral, boundary, per-run SD and the E2 run count
  n = (1.96*s/0.02)^2; for LongGenericsFullLineSlip also the four reworded sites against the other 26.
  Record a dated Decision entry here with the table, and replace the LongGenerics part of the TODO.md E2
  line with the chosen id and its run count.
  Decision rule: E2 uses the id with the lowest pooled residual whose CI upper bound is below the
  baseline's CI lower bound; if neither variant clears it, report and ask the owner.
- Done when: the Decision entry and the TODO.md line are committed.
- Lane: dispatcher. Effort: S

## Out of scope

- Changing LongGenericsSlip itself (it stays the baseline) or the other three families.
- Variable changes per site (1-3 occurrences): needs an all-occurrences mode in `ApplyChanges` and a
  layout change; revisit only if B works.
- Tool-side structural write feedback (nesting-depth change in a write response): a separate idea.
- The E2 batch itself (TODO.md line).

## Risks and open decisions

- Budget: the FullLine prompt carries 30 full declarations (about 3 KB more than the per-site prompt).
  `Budget_LongGenericsFullLineSlip` (Step 6) catches an overrun; if it fails, report the measured size
  and ask the owner (options: raise the family's budget, or shorten the Files/IfFails text).
- `using System.Collections.Concurrent;` after a file-scoped namespace is legal; the Seed case in Step 6
  proves it builds. The using is unused in the seed (hidden CS8019 only).
- RenameSymbol on a BCL type is expected to be refused (not source-owned); not verified. If a run shows
  a one-call map application, record it in Step 8 as its own outcome.

# Plan: Slip-first eval scenarios graded per site, one slip family each

**Status:** APPROVED 2026-11-26. Four per-site-graded slip-family scenarios (escapes, doc comments, long generics, near-duplicate identifiers) with a shared builder, Roslyn token grader, JSON sidecar, Get-SlipCounts metrics and an E1 validity batch.

## Problem

Source: [idea_slip_first_scenarios.md](../ideas/idea_slip_first_scenarios.md).

The slip-focused siblings from
[plan_slip_focused_scenario_variants.md](./closed/plan_slip_focused_scenario_variants.md) passed E1
(11 of 12 runs) but yield 2-13 write calls per run, because the model batches edits through
`ReplaceSnippet.batchEdits`. All 17 slipped writes came from one run, so a per-call rate mostly counts
runs that fell into a slip loop. The slip metric (`scripts/Get-SlipCounts.ps1`) is refusal-based and
cannot see a silent slip (wrong text that compiles).

Goal: scenarios whose primary measurement is the slip rate, each run yielding 20-40 graded sites
whatever the batching, silent slips included, one slip family per scenario so rates compare across
families.

It worked when:
- one run of a new scenario yields at least 20 graded sites;
- silent slips are counted;
- a golden run grades 100% clean and a no-op control grades 0% done;
- a small E1 batch gives per-family slip rates.

## Decision

### Decision log

- 2026-11-26 D1 (intake): keep the existing slip instruments (ThreadOptionalParameter, the three
  Transcription siblings, `Get-SlipCounts.ps1`); mark them superseded once this plan lands.
- 2026-11-26 D2 (intake): the `batchEdits` hide arm is a separate later plan, out of scope here.
- 2026-11-26 D3 (intake): all four slip families in v1: escape-heavy string and regex literals
  (verbatim and interpolated strings with braces); XML doc comments with `&lt;`/`&gt;` and crefs; long
  generic types; near-duplicate identifiers.
- 2026-11-26 D4 (intake, owner sketch): one general scenario shape, families differ only in the
  edited content. Shape: a class of several simple methods (for example each building a different kind
  of message), and the task changes something on every line, so the model must write each whole line.
- 2026-11-26 D16 (investigation, explore-tier1): the default eval surface is the lean list
  (`RoslynSentinel.AgentLoop/AgentToolAllowLists.cs:12-14`, applied by `ChainEvalToolSurface.NewHiddenTools`, `:55`).
  Visible write routes: ReplaceSnippet, ReplaceRange, Member, RenameSymbol, CreateFile (refuses a
  non-empty existing file, `Tools.Basic/WorkspaceFileEditImpl.cs:~2600`), ChangeSignature,
  ChangeAccessibility, ModifyModifier, UsingDirective. WriteFile, ApplyDiff, EditSnippets hidden.
  Consequence: a change that RenameSymbol or ChangeSignature can do semantically writes no text and
  measures nothing. The escape-copy scan covers only `oldContent`/`newContent`/`content`
  (`scripts/roslynsentinel-transcriptmetrics.ps1:13`), not `Member.newMemberSource`.
- 2026-11-26 D5 (Q1): site unit = one small member per site, located by name in the end-state syntax
  tree. Why: survives reorder and reformat, and catches collateral damage inside the member.
- 2026-11-26 D6 (Q2): hosts per family, one static class per family, every site a one-line member:
  escapes and near-duplicate identifiers as one-line message methods (fixture declares
  `ShippingKinds { Overnight, Overland, Overseas, Overweight }`); doc comments as the `///` block of a
  trivial method; long generics as field or method-signature declarations. Switch arms out (not members).
- 2026-11-26 D7 (Q3): each changed site changes at 2+ dispersed points, at least one inside the
  slip-prone span, stated exactly so the golden text is unique. No change that RenameSymbol or
  ChangeSignature can do semantically (D16). Change kinds: literal words, type arguments, referenced enum
  member, doc text.
- 2026-11-26 D8 (Q4): the prompt gives a short per-site description of the change; the model reads the
  line through the tools and re-emits it. Why: the realistic copy-from-tool-result path, where
  escape-copy slips start.
- 2026-11-26 D9 (Q6): per family file 30 changed sites plus 6 untouched slip-prone control sites.
- 2026-11-26 D10 (Q5): both control kinds. (a) The 6 untouched slip-prone sites (D9) catch collateral
  slips from whole-member or region rewrites. (b) A trivial separator member between every pair of
  observed sites, with unique names and bodies (`static int CalcOne() => 1;`, `CalcTwo() => 2;` ...),
  catches edits that spill over member bounds and attributes them to range errors, not slips.
  Separators are graded but excluded from the slip rate.
- 2026-11-26 D11 (Q7): comparison is Roslyn token-equal: token kind and text of the golden member vs
  the end-state member, structured doc-comment tokens included; whitespace and EOL trivia ignored;
  whitespace runs inside doc-comment text tokens collapsed; literal token text exact. Why: immune to
  tool reformatting (open finding: `Member` reformats unrelated content); `SyntaxFactory.AreEquivalent`
  ignores trivia and would miss doc-comment slips.
- 2026-11-26 D12 (Q8): declarative sites. Each site defined once (name, seed text, golden text, change
  description, role Changed | Control | Separator); a builder derives the seed file, golden file,
  prompt lines and grading list. `ImplementationScenarioStep` gains a trailing optional `Sites`
  parameter; `VerdictAsync` calls one shared `SiteGrader` before the build/test checks so a failing
  test cannot hide per-site results. `ExtraCheck` unchanged. (Field list revised by D22.)
- 2026-11-26 D13 (Q9): one enum `SiteOutcome { Correct, Unchanged, Wrong, Missing }`, read by role.
  Changed: Correct = golden, Unchanged = seed, Wrong = neither (silent slip or partial), Missing =
  member absent or duplicated. Control: golden equals seed, Wrong = collateral slip. Separator: Wrong or
  Missing = boundary over-reach. No `Partial` class in v1; the sidecar keeps end-state text.
- 2026-11-26 D14 (Q10): carrier is a typed per-site result list on `ScenarioVerdict` (new optional
  member) serialised to `step-N.sites.json` (site, role, outcome, end-state text when not Correct).
  `outcome.txt` gets a human-readable summary in its detail text only, not parsed. `Get-SlipCounts.ps1`
  reads the JSON. Cross-plan: `plan_modeleval_run_review_pipeline.md` must read the sidecar too.
- 2026-11-26 D15 (Q11): run pass rule = build and tests pass, every Changed site Correct, every
  Control and Separator intact; anything else is `CheckFailed` with counts in the detail. Early stops
  keep NotConverged / PassedAfterCap.
- 2026-11-26 D17 (Q12): headline metrics per family: residual slip rate = Wrong / (Correct + Wrong)
  over Changed sites; completion rate = (Correct + Wrong) / Changed; collateral rate = Wrong Control /
  Control; boundary rate = Wrong or Missing Separator / Separator. Secondary: refused-then-repaired
  slips per 100 Changed sites from the existing transcript counters. Clustering: `Get-SlipCounts.ps1`
  reports per-run rates, the pooled rate with a 95% run-level (cluster) bootstrap CI, and the share of
  runs with at least one slip.
- 2026-11-26 D18 (Q13): E1 is an instrument-validity check, not the rate measurement. Pre-E1 gate (no
  model): per family golden grades 100% Correct, no-op grades 100% Unchanged, 0 false Wrong. E1:
  pinned commit, Qwen3.5-9B, plain, thinking off, hosts 112 and 113, 4 families x 4 runs (2 per host),
  turn cap 40, sampler per the procedure doc, near-miss auto-correct off. Outcome classes: CleanRun
  (Pass), SlipRun (Correct + Wrong >= 20 and any Wrong, collateral or boundary hit), ShortRun
  (Correct + Wrong < 20), Broken (build/test failure). Keep a family: >= 3 of 4 runs reach 20 graded
  Changed sites and a manual check of every Wrong site finds 0 grader false positives. Rework or drop:
  >= 2 ShortRuns or any false positive. 0 Wrong and 0 refused slips over all runs: keep, mark "not
  slip-prone for this model". The plan ends with E1 plus an E2 sizing recommendation (runs per family
  from E1 between-run variance); E2 itself is a later evalqueue batch outside this plan.
- 2026-11-26 D19 (Q14): reuse `EvalAxis.Transcription` and its folder; no new axis.
- 2026-11-26 D20 (Q15): a final step, only if E1 keeps at least one family, marks ThreadOptionalParameter
  and the three siblings in `reference_model_eval_procedure.md` "Slip instruments" as "superseded for
  slip-rate measurement by <families>; kept as realistic reference". `Get-SlipCounts.ps1` is extended,
  not superseded.
- 2026-11-26 D21 (Q17-Q21, implementer defaults confirmed): the escape-copy scan also covers
  `Member.newMemberSource` and ReplaceRange's payload field; ids, registry, count and axis pins and
  catalog regeneration follow the Transcription siblings; golden test asserts all sites Correct and
  the no-op test asserts all Changed sites Unchanged with controls intact; budget fit by the existing
  `Budget_` test; sampler and assistance flags per the procedure doc, near-miss auto-correct off, both
  recorded in the E1 table.
- 2026-11-26 D22 (Q22, reopens D12; owner chose A): a site carries its change as structured pairs
  `SiteChange(Old, New)` instead of a golden text plus a free-text description. The golden text is
  derived (`SlipFamilyBuilder.ApplyChanges`) and the prompt line is rendered from the pairs. Why: one
  definition per site (item 2), and the D7 rules (2+ changes, dispersed, plain-text fragments) become
  testable instead of manual. D12's field list becomes (name, seed text, changes, role).
- 2026-11-26 D23 (phase 7 implementer defaults, from verification at 82dc6706):
  - seed path `ContosoOrders.Core/Evals/<Id>/<ClassName>.cs`, namespace `ContosoOrders.Evals.<Id>`
    (the existing convention: folder under `ContosoOrders.Core/Evals/`, namespace without `Core`, e.g.
    `SweepConstructionSitesScenario.cs:30`; corrected in readiness round 2);
  - site text in C# raw string literals, so seed constants need no double escaping; a site whose text
    contains `"""` (e.g. a verbatim string ending in `""`) uses a 4-quote delimiter or the multi-line
    form;
  - separators `public static int CalcNN() => NN;` (NN = 01, 02, ...), generated by the builder;
  - every site member is `public static` (avoids unused-private-member warnings);
  - D21's "ReplaceRange payload field" is `newContent`, already scanned; the wider scan adds
    `newMemberSource` (Member) and `firstLineText`/`lastLineText` (ReplaceRange anchors, copied text
    like `oldContent`); where the wider list applies is Q24;
  - collateral rate counts Control Missing as well as Control Wrong (extends D17's wording; a deleted
    control is collateral damage).
- 2026-11-26 D24 (readiness round 1, explore-tier2 sonnet at 82dc6706; fixes applied, no owner choice):
  - paths: the graders folder is `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/`
    (namespace `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders`); the registry is
    `ImplementationScenarios/ImplementationScenarioRegistry.cs`; the agent test namespace is
    `RoslynSentinel.Tests.ModelEval.LiveModel`;
  - prompt reuses `ScenarioTask` + `EvalContractOptions.Render` (`RoslynSentinel.AgentLoop/ScenarioTask.cs:7`,
    as `SweepNamedSitesSlipScenario.cs:73-79`), with a `DONE:` canned final message;
  - layout values (30 Changed, Control positions, 2 changes, spread 20, plain-fragment pattern, at most
    3 `///` lines per doc site) live in one `SlipFamilyLayout` holder; `SlipFamilyBuilder.Validate`
    checks them and the content tests assert `Validate` returns nothing;
  - `ApplyChanges` locates every `Old` in the seed text (exactly once, non-overlapping) and replaces
    from the end, so the content rule and the derivation agree;
  - doc-comment comparison drops `///` exterior and doc newline tokens before merging text tokens, so a
    reflow across a different number of lines compares equal;
  - one shared sidecar fixture read by both the C# and the PowerShell tests ties the JSON names to
    `SiteGrade` (location revised in round 2: `RoslynSentinel.Tests.ModelEval/SiteSidecars/step-1.sites.json`,
    found from the repo root like the `ResumeTranscripts/` fixtures, no csproj copy);
  - steps reordered (builder before grader tests), Step 9 split in two, budget checked in-process per
    family, `Seed_` cases added to the pre-E1 gate;
  - ContosoOrders.Core has no `TreatWarningsAsErrors` or `GenerateDocumentationFile` and no
    `Directory.Build.props` applies beyond output settings, so no `#pragma` handling is needed.

- 2026-11-26 D25 (Q23, owner chose A): no ordering with `plan_modeleval_run_review_pipeline.md`; this
  plan locates anchors by content. That plan's session (roslynsentinel-9c) was asked to add a Risks
  line about `step-N.sites.json` and the shifted `ImplementationScenarioAgentTests.cs` anchors rather
  than this session editing its in-flight doc. Note: its Steps 3a-8 landed after 82dc6706 (HEAD
  5a1d6a2f), including migration markers in `Get-SlipCounts.ps1` and shared discovery in the
  transcript-metrics helper; Steps 9a-1, 9a-2 and 9b anchors must be re-read at implementation time. Peer reply
  (2026-11-26): none of its remaining steps edits `Get-SlipCounts.ps1`, the transcript-metrics helper or
  `Get-SlipCounts.Tests.ps1`; 81787301 added a `# TODO migrate transcript parsing to
  scripts/eval-review/EvalRunReader.ps1` marker above the outcome parse (~:120); 56916a0e changed
  `Get-EvalTranscripts` (`-Machine [string[]]` defaulting from eval-hosts.json, `-Commit`, `-HostsFile`;
  rows gain `Commit`, `Dirty`, `Job`); the test script passes 118 checks after it.
- 2026-11-26 D26 (Q24, owner chose A): a second list `$EscapeScanFieldsWide = $EscapeScanFields + @(...)`
  used by `Get-SlipCounts` only; `Get-ToolResultFormatArmTable.ps1` keeps today's fields, so earlier
  arm tables stay reproducible.

- 2026-11-26 D27 (readiness round 2, explore-tier2 sonnet at 978e744f; fixes applied, no owner choice):
  per-run `ControlSites`/`SeparatorSites` columns (rate denominators); sidecar keys stated
  (`siteName`, `role`, `outcome`, `endStateText`); namespace `ContosoOrders.Evals.<Id>` (D23 corrected);
  E1 submits with `-Repeats 2` and the prompt-variant guard, thinking off, no redundant `-TurnCap`;
  family ids derived from the registry (`Sites` not null), not hand-kept; raw-literal delimiter rule;
  Step 9a test helper takes a root parameter; fixture found via `RepoRootLocator`, with a `.gitignore`
  exception if the blanket `*.json` rule hides it; `using RoslynSentinel.Common.AgentLoop;` for
  `ScenarioTask`; grader parse failures, entity tokens, documentation mode, sidecar directory creation,
  percentile method and null columns specified; Step 14 split into 14a/14b; test methods take no
  internal-type parameters; `ObservedSiteCount` derived; stale anchors and closed-plan references fixed.

- 2026-11-26 D28 (Q25, owner chose A): one PowerShell sidecar reader, `Read-EvalSiteGrades` plus
  `Get-EvalSiteTally` in `scripts/eval-review/EvalRunReader.ps1` (the run-review plan's remaining steps do
  not edit it, peer confirmed), dot-sourced by `Get-SlipCounts.ps1`; the review pipeline can use the same
  reader. Step 9a split into 9a-1 (reader) and 9a-2 (Get-SlipCounts).
- 2026-11-26 D29 (readiness round 3, same reviewer; fixes applied, no owner choice): role and outcome
  strings live in `vocabulary.json` (per `EvalRunReader.ps1:5`) and are pinned to the C# enums by a
  Tests.ModelEval test; per-run `ResidualSlipRate`/`CompletionRate` columns (D17, needed by Step 17's
  E2 sizing); a derived guard that every plan family carries `Sites`; unconditional `.gitignore`
  negation; script header comments and the log-sources reference updated; pairing labels, stale step
  references and wording fixed. Round 4 (same reviewer): N1-N9 fixed; its one should-fix (the
  vocabulary arrays need a consumer) fixed by `Get-EvalSiteTally -Vocabulary` rejecting unknown
  strings; its nit on the guard's "at least one" bound accepted with no action (the axis pin lists the
  four ids).
- 2026-11-26 Readiness (plan-design): independent review clean at c955116e (four rounds, explore-tier2
  sonnet; findings resolved in D24, D27, D29).
- 2026-11-26 D30 (implement-plan): staleness check clean (no commits since c955116e besides the
  APPROVED commit). Mode Batched (A: 1-2, B: 3-6, C: 10-13, D: 7/8/9a-1/9a-2/9b, E: 14a-14b; main
  session 15-17). Owner away: mid-run decisions deferred to the tracker. Qwen3.5-9B confirmed loaded
  on 112 and 113 before the run started.

### Design summary

- Four scenarios on `EvalAxis.Transcription`: EscapeLiteralsSlip, DocCommentsSlip, LongGenericsSlip,
  NearDuplicateIdentifiersSlip. Each is one `SlipSiteSet` (36 observed sites: 30 Changed, 6 Control)
  turned into an `ImplementationScenario` by `SlipFamilyBuilder.Build`, which inserts the separators and
  renders the seed file, golden file and prompt from the same site list.
- `SiteGrader.Grade` finds each site's member by name in the end-state file and compares token streams
  with the golden and seed members (D11, D13). `VerdictAsync` grades first, attaches the grades to every
  verdict, and fails a run whose sites are not all intact (D15). The agent test writes
  `step-N.sites.json`; `Get-SlipCounts.ps1` reads it and reports the D17 metrics.
- Exceptions to the design checklist: none. Simplest alternative (hand-written scenarios with an
  `ExtraCheck` returning the first wrong site) rejected: no per-site counts and three copies of each
  site's text.

## Execution rules

- All C# reads and writes through the RoslynSentinel MCP tools; `.ps1`, `.json`, `.csproj` and `.md`
  with Edit/Write.
- `Build` 0 errors after every slice; one `Build`/`RunTest` at a time. Test baseline 0 failures.
- Every file touched is in `RoslynSentinel.Tests.ModelEval` or `scripts/`; no production project changes.
- New records carry the `NEW-DTO:` block below in the creating call's `reason` and the commit message.
- Commit per slice with scope `listed` and a `Plan: plan_slip_first_scenarios` trailer.
- Anchors are line numbers at 82dc6706; re-locate by content if a file moved (another plan,
  `plan_modeleval_run_review_pipeline.md`, is IN PROGRESS on some of the same scripts and cites
  `ImplementationScenarioAgentTests.cs` lines).
- Namespaces: `ImplementationScenarios/*.cs` use `RoslynSentinel.Tests.ModelEval.ImplementationScenarios`
  (`ImplementationScenario.cs:9`); `ImplementationScenarios/Graders/*.cs` use
  `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders`; new test files at the project root
  use `RoslynSentinel.Tests.ModelEval` (as `TranscriptionScenarioTests.cs`). New types are `internal`,
  matching `ImplementationScenarioStep` (`ImplementationScenario.cs:39`) and `ScenarioVerdict`
  (`ImplementationScenarios/Graders/ScenarioVerdict.cs:23`). Test fixtures are `public`, so no test
  method or `TestCaseSource` takes an internal type (`SlipSiteSet`, `SiteRole`, `SiteOutcome`, ...) as a
  parameter (CS0051): pass a family id `string`, or loop over the enum values inside the test body.
- `System.Text.Json` and `System.Globalization` need explicit `using`s or full qualification
  (`ImplementationScenarioAgentTests.cs:145` writes `System.Globalization.CultureInfo` in full);
  `ScenarioTask` needs `using RoslynSentinel.Common.AgentLoop;` (`RoslynSentinel.AgentLoop/ScenarioTask.cs:1`).

NEW-DTO justifications (role, layer):
- `NEW-DTO: SlipSite (Item, Tests.ModelEval.ImplementationScenarios)`. Evaluated: `ScenarioVariant` -
  a prompt/variant switch, not a graded member; `ImplementationScenarioStep.GoldenFiles` - per file, not
  per member. Wire impact: none.
- `NEW-DTO: SiteChange (Item, Tests.ModelEval.ImplementationScenarios)`. Evaluated:
  `ScenarioTemplateEdits.ReplaceExactly` - a helper call, not data the prompt and grader share. Wire
  impact: none.
- `NEW-DTO: SlipSiteSet (Item, Tests.ModelEval.ImplementationScenarios)`. Evaluated:
  `ImplementationScenarioStep` - a whole step with prompt and checks; `SeedFiles` - path to text only.
  Wire impact: none.
- `NEW-DTO: SiteGrade (Item, Tests.ModelEval.ImplementationScenarios.Graders)`. Evaluated:
  `ScenarioVerdict` - per run; `MutantResult` (`ImplementationScenarios/Graders/MutationGrader.cs:9`) -
  counts per mutant, no member or role. Wire impact: none (test artifact `step-N.sites.json`).

Slice map (two lanes at most): 1 -> 2 -> (3 | 4) -> (5 | 6) -> (10 | 11) -> (12 | 13) -> (7 | 9a-1) ->
(8 | 9a-2) -> (14a | 9b) -> 14b -> 15 -> 16 -> 17. The text-neutral family steps 10-13 need only Step 3 and are
scheduled before the behaviour-changing 7, 9a-2, 9b; pairs always touch disjoint files.

## Steps

### Step 1 - Contract skeleton
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/SlipSite.cs` (new),
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/SlipFamilyBuilder.cs` (new),
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/SiteGrader.cs` (new)
- Depends on: none
- Effect: text-neutral (new types, no callers)
- Change: one `CreateFile` per file. Every method body is
  `throw new NotImplementedException(); // TODO implement (Step <n>)`.
  - `SlipSite.cs`: `internal enum SiteRole { Changed, Control, Separator }`;
    `internal sealed record SiteChange(string Old, string New);`
    `internal sealed record SlipSite(string Name, string SeedText, IReadOnlyList<SiteChange> Changes, SiteRole Role);`
    `internal sealed record SlipSiteSet(string FilePath, string Namespace, string ClassName, string Preamble, IReadOnlyList<SlipSite> Sites);`
    `internal static class SlipFamilyLayout` with `public const int ChangedSiteCount = 30;`,
    `public static readonly IReadOnlyList<int> ControlPositions = [4, 10, 16, 22, 28, 34];` (1-based),
    `public static int ObservedSiteCount => ChangedSiteCount + ControlPositions.Count;`,
    `public const int ChangesPerSite = 2;`, `public const int MinChangeSpread = 20;`,
    `public const string PlainFragmentPattern = @"^[A-Za-z0-9 _.,:;!?'-]+$";`,
    `public const int MaxDocCommentLines = 3;`, `public const string SeparatorPrefix = "Calc";`.
  - `SlipFamilyBuilder.cs`: `internal static class SlipFamilyBuilder` with `public const int TurnCap = 40;`
    and stubs (Step 3): `public static ImplementationScenario Build(string id, string title, string source, SlipSiteSet set)`,
    `public static IReadOnlyList<SlipSite> Expand(SlipSiteSet set)`,
    `public static string ApplyChanges(SlipSite site)`,
    `public static string RenderFile(SlipSiteSet set, bool golden)`,
    `public static string RenderChangeLine(SlipSite site)`,
    `public static string RenderPrompt(SlipSiteSet set)`,
    `public static IReadOnlyList<string> Validate(SlipSiteSet set)`.
  - `SiteGrader.cs`: `internal enum SiteOutcome { Correct, Unchanged, Wrong, Missing }`;
    `internal sealed record SiteGrade(string SiteName, SiteRole Role, SiteOutcome Outcome, string? EndStateText);`
    `internal static class SiteGrader` with `public const string SidecarFileNameFormat = "step-{0}.sites.json";`
    and stubs (Step 4): `public static IReadOnlyList<SiteGrade> Grade(string? fileText, SlipSiteSet set)`,
    `public static bool AllIntact(IReadOnlyList<SiteGrade> grades)`,
    `public static string Summarize(IReadOnlyList<SiteGrade> grades)`,
    `public static void WriteSidecar(string path, IReadOnlyList<SiteGrade> grades)`,
    `public static IReadOnlyList<SiteGrade> ReadSidecar(string path)`.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S

### Step 2 - Trailing optional `Sites` on the step and the verdict
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenario.cs`,
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioVerdict.cs`
- Depends on: 1
- Effect: text-neutral (optional parameters, default null)
- Change: append `SlipSiteSet? Sites = null` after `IReadOnlyDictionary<string, string>? PromptVariants = null`
  (`:51`) in `ImplementationScenarioStep` (`:39`); append `IReadOnlyList<SiteGrade>? Sites = null` after
  `string Detail` in `ScenarioVerdict` (`:23`, add `using` for the `ImplementationScenarios` namespace if
  needed). Blast radius: 21 references to the step record (17 positional constructions), 3 to
  `ScenarioVerdict`; all unaffected by a trailing optional parameter.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S

### Step 3 - Implement `SlipFamilyBuilder`
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/SlipFamilyBuilder.cs`
- Depends on: 1, 2
- Effect: text-neutral (no registered caller until Step 14a)
- Change:
  - `ApplyChanges`: for each change find every occurrence of `Old` in `site.SeedText`; throw
    `InvalidOperationException` (site name and fragment) unless there is exactly one and no two
    changes' ranges overlap; then replace the ranges from the last to the first.
  - `Expand`: throw if `set.Sites` contains a `Separator`; between each consecutive pair of observed
    sites insert `new SlipSite($"{SlipFamilyLayout.SeparatorPrefix}{n:00}", $"public static int {SlipFamilyLayout.SeparatorPrefix}{n:00}() => {n};", [], SiteRole.Separator)`,
    n = 1, 2, ...; throw on any duplicate name.
  - `RenderFile`: CRLF text via `ImplementationScenario.NormalizeToCrlf`: `namespace <Namespace>;`,
    blank line, `Preamble` (trimmed; omitted with its blank line if empty), blank line,
    `public static class <ClassName>`, `{`, each expanded site's text (golden ? `ApplyChanges(site)` :
    `SeedText`) with every line indented 4 spaces and one blank line between members, `}`.
  - `RenderChangeLine`: ``- <Name>: change `<Old1>` to `<New1>`; change `<Old2>` to `<New2>`.`` (all
    changes, in order).
  - `RenderPrompt` (`using RoslynSentinel.Common.AgentLoop;`): `EvalContractOptions.Render(new ScenarioTask(Goal, Files, IfFails, DoneWhen, Conditions))`
    with `Goal = "Make the listed text edits in <ClassName>."`,
    `Files = "<ClassName>.cs in <directory of FilePath>. No other file."` (satisfies
    `Files_NamesEveryAllowedPath`, `ImplementationScenarioGoldenTests.cs:186-206`),
    `Conditions = "Make exactly these edits and change nothing else:\n" + one RenderChangeLine per Changed site, newline-separated`,
    `IfFails = EvalContractOptions.PlainIfFails`, `DoneWhen = "every listed edit is made and the solution builds."`.
  - `Validate` (returns one string per violation, empty when valid; never throws): observed count
    `ObservedSiteCount`; Control sites exactly at `ControlPositions`, all others Changed; no observed name
    starts with `SeparatorPrefix`; names unique; Changed sites have exactly `ChangesPerSite` changes,
    Control sites none; every `Old` and `New` matches `PlainFragmentPattern`; every `Old` occurs exactly
    once in the seed text and ranges do not overlap; the first and last `Old` start at least
    `MinChangeSpread` characters apart; no site has more than `MaxDocCommentLines` lines starting with
    `///`.
  - `Build`: return `new ImplementationScenario(Id: id, Title: title, Axis: $"Transcription: per-site graded slip family ({title})", Source: source, SeedFiles: new Dictionary<string, string> { [set.FilePath] = RenderFile(set, false) }, Steps: [step], TurnCap: TurnCap, PreferredHost: "113", PrimaryAxis: EvalAxis.Transcription)`
    where `step` is `new ImplementationScenarioStep(Prompt: RenderPrompt(set), GoldenFiles: new Dictionary<string, string> { [set.FilePath] = RenderFile(set, true) }, AllowedPaths: [set.FilePath], HiddenTestSource: null, ExtraCheck: null, CannedFinalMessage: $"DONE: made every listed edit in {set.ClassName}.cs.", Sites: set)`.
    `Build` does not call `Validate` (an invalid family must not break the registry's static
    initialisation); the content tests do.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: M. Pairs with: Step 4.

### Step 4 - Implement `SiteGrader`
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/SiteGrader.cs`
- Depends on: 1 (calls `SlipFamilyBuilder.Expand`/`ApplyChanges`; stubs compile)
- Effect: text-neutral (no callers until Step 7)
- Change:
  - Parsing: always `CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Parse))`.
    A golden or seed text that does not parse to exactly one member is an authoring error: throw
    `InvalidOperationException` naming the site (the builder and content tests guarantee valid families
    before registration, so `VerdictAsync` never sees it).
  - `Grade`: expand the set (`SlipFamilyBuilder.Expand`). `fileText` null (file absent) -> every site
    `Missing`. Otherwise parse the end-state text and collect the members of every class
    named `set.ClassName` (method and property identifiers, field variable names). Per site: 0 members
    with the site's name -> `Missing`, `EndStateText` null; more than 1 -> `Missing`, `EndStateText` the
    members' full texts joined by a `---` line. Otherwise compare the member's token stream with the
    golden member (`ApplyChanges(site)`) and the seed member (`site.SeedText`), each parsed by wrapping
    the text in `class C { ... }` and taking its single member. Equal to golden -> `Correct`
    (`EndStateText` null); else equal to seed -> `Unchanged`; else `Wrong`. `EndStateText` for non-Correct
    is the member's `ToFullString()` trimmed.
  - Token stream (D11, D24): `DescendantTokens(descendIntoTrivia: true)` of the member, so leading
    doc-comment trivia is included. Drop every `DocumentationCommentExteriorToken` and
    `XmlTextLiteralNewLineToken`. Merge each run of consecutive `XmlTextLiteralToken`s and
    `XmlEntityLiteralToken`s (`&lt;`, `&gt;`, `&amp;`) into one element whose text is the concatenation
    (entity tokens by their raw `Text`) with whitespace runs collapsed to one space and trimmed (dropped
    if empty). Every other token is one element (Kind, exact `Text`), so literal escapes compare exactly.
  - `AllIntact`: every grade `Correct`.
  - `Summarize`: one line, `sites: changed correct=a unchanged=b wrong=c missing=d; control wrong=e missing=f; separator broken=g`
    (human-readable only; nothing parses it).
  - `WriteSidecar`/`ReadSidecar`: a JSON array of `SiteGrade`, System.Text.Json, one
    `private static readonly JsonSerializerOptions` (`JsonNamingPolicy.CamelCase`, indented,
    `new JsonStringEnumConverter()` without a naming policy). Keys: `siteName`, `role`, `outcome`,
    `endStateText` (null for Correct). Enum values: `Changed`, `Control`, `Separator`, `Correct`,
    `Unchanged`, `Wrong`, `Missing`. `WriteSidecar` creates the parent directory if missing.
- Done when: `Build` 0 errors.
- Lane: implementer (NEED-ADVICE to implementer-advisor on Roslyn token details). Effort: M. Pairs with: Step 3.

### Step 5 - `SlipFamilyBuilderTests`
- Files: `RoslynSentinel.Tests.ModelEval/SlipFamilyBuilderTests.cs` (new)
- Depends on: 3, 4
- Effect: text-neutral
- Change: in-process NUnit tests on an inline set; the `SlipFamilyBuilderTests` rows of the test table.
  The 36-site validation case builds its set from `SlipFamilyLayout` values, not literals.
- Done when: the dispatcher runs `RunTest` on class `SlipFamilyBuilderTests`, all pass.
- Lane: test-writer. Effort: S. Pairs with: Step 6.

### Step 6 - `SiteGraderTests` and the shared sidecar fixture
- Files: `RoslynSentinel.Tests.ModelEval/SiteGraderTests.cs` (new),
  `RoslynSentinel.Tests.ModelEval/SiteSidecars/step-1.sites.json` (new), `.gitignore`
- Depends on: 3, 4
- Effect: text-neutral
- Change: the `SiteGraderTests` rows of the test table, on small inline sets (2-3 observed sites). The
  fixture holds 7 grades covering every role and outcome: Changed Correct, Changed Unchanged, Changed
  Wrong, Changed Missing, Control Wrong, Control Missing, Separator Wrong (`endStateText` set for the
  non-Correct ones). The test locates it from the repo root with
  `RepoRootLocator.TryFind(AppContext.BaseDirectory) ?? throw ...` (as `ScenarioCatalogGeneratorTests`);
  no csproj change. `.gitignore:10` ignores `*.json` with per-folder negations (`:26`, `:30`), so add
  `!RoslynSentinel.Tests.ModelEval/SiteSidecars/*.json` beside them; `Git(status)` then lists the
  fixture as untracked. The round-trip test writes
  under `ScratchRoot.Resolve()`, not the repo.
- Done when: the dispatcher runs `RunTest` on class `SiteGraderTests`, all pass.
- Lane: test-writer. Effort: S. Pairs with: Step 5.

### Step 7 - Wire the grader into the verdict and the run output
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ImplementationScenarioChecks.cs`,
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarioAgentTests.cs`
- Depends on: 2, 4
- Effect: behaviour-changing only for steps with `Sites` (none until Step 14a); existing scenarios
  unchanged.
- Change:
  - `VerdictAsync` (`:17`): after `stepDefinition` (`:30`) and before the local function `Verdict`
    (`:35`), declare `IReadOnlyList<SiteGrade>? grades`: null when `stepDefinition.Sites` is null, else
    `SiteGrader.Grade(File.Exists(full) ? File.ReadAllText(full) : null, set)` with
    `full = Path.Combine(solutionDirectory, set.FilePath)`. `Verdict` attaches `grades` (`Sites`) to
    every verdict it returns and appends `" " + SiteGrader.Summarize(grades)` to `Detail` when grades
    exist. After the `ExtraCheck` failure return (`:111`) and before the scope check (`:115`): grades
    present and not `AllIntact` -> `Verdict(CheckFailed, ...)`. `BuildBroken` (`:62`) and the test
    `CheckFailed` (`:103`) keep their outcome and carry the grades through `Verdict`.
  - `RunStepAsync`: after `VerdictAsync` (`:170`) and before writing `step-{step}.outcome.txt` (`:199`),
    if `verdict.Sites` is not null:
    `SiteGrader.WriteSidecar(Path.Combine(_runDirectory, string.Format(System.Globalization.CultureInfo.InvariantCulture, SiteGrader.SidecarFileNameFormat, step)), verdict.Sites)`.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S. Pairs with: Step 9a-1.

### Step 8 - Site assertions in the golden and previous-step tests
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs`
- Depends on: 7
- Effect: text-neutral for existing scenarios (assertions run only when the step has `Sites`)
- Change: `Golden_StepPassesItsCheck` (`:49`): when the step has `Sites`, assert `verdict.Sites` is not
  null and every `Outcome` is `Correct`. `Previous_StepFailsTheCheck` (`:74`): when the step has
  `Sites`, assert `verdict.Outcome == ImplementationScenarioOutcome.CheckFailed`, every `Changed` grade
  `Unchanged` and every other grade `Correct`.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S. Pairs with: Step 9a-2.

### Step 9a-1 - Shared sidecar reader in `EvalRunReader.ps1` (D28)
- Files: `scripts/eval-review/EvalRunReader.ps1`, `scripts/eval-review/EvalRunReader.Tests.ps1`,
  `scripts/eval-review/vocabulary.json`
- Depends on: 6 (the shared fixture)
- Effect: text-neutral (new functions and vocabulary entries; no existing caller changes)
- Change: `EvalRunReader.ps1:5` forbids copying vocabulary lists into code, so add `siteRoles`
  (`Changed`, `Control`, `Separator`) and `siteOutcomes` (`Correct`, `Unchanged`, `Wrong`, `Missing`)
  arrays to `vocabulary.json` (check `EvalRunReader.Tests.ps1` and `review.schema.json` for anything
  that pins the vocabulary's key set and extend it), and read them through `Read-EvalVocabulary`.
  The C# pin against `SiteRole`/`SiteOutcome` is in Step 14b. Then add, in the file's existing style
  (`Get-EvalProperty`, `Get-EvalLeaf` helpers):
  - `Read-EvalSiteGrades -RunFolder <path>`: the first `step-*.sites.json` in the folder
    (`Select-Object -First 1`), parsed with `@(ConvertFrom-Json ...)` so a one-element array stays an
    array; `$null` when there is none. Keys and enum strings as in Step 4.
  - `Get-EvalSiteTally -Grades <array> [-Vocabulary <object>]` (`-Vocabulary` defaults to
    `Read-EvalVocabulary`): first rejects (throws, naming the value) any grade whose `role` is not in
    `siteRoles` or whose `outcome` is not in `siteOutcomes`, so drift from the C# enums fails at read
    time; the tally then compares against the role and outcome names, which are allowed as literals
    here because the vocabulary check and the C# pin (Step 14b) guard them. Returns one object with `SitesChanged` (Changed-role count),
    `SitesCorrect`, `SitesWrong`, `SitesUnchanged`, `SitesMissing` (Changed-role only), `ControlSites`,
    `ControlHits` (Control Wrong + Missing), `SeparatorSites`, `SeparatorHits` (Separator Wrong +
    Missing).
  - Tests in `EvalRunReader.Tests.ps1` (its `Assert-That` style), inserted before the failure summary
    and `exit` (`:192-195`): reading the shared fixture
    (`RoslynSentinel.Tests.ModelEval/SiteSidecars/step-1.sites.json`, located from `$PSScriptRoot\..\..`)
    gives 7 grades and the tally 4/1/1/1/1/2/2/1/1; a new empty subfolder under the script's `$tempRoot`
    gives `$null`; a grade with an unknown `outcome` makes `Get-EvalSiteTally` throw.
- Done when: `pwsh -NoProfile -File scripts\eval-review\EvalRunReader.Tests.ps1` passes.
- Lane: implementer. Effort: S. Pairs with: Step 7.

### Step 9a-2 - `Get-SlipCounts.ps1`: per-run site columns and the wider escape scan
- Files: `scripts/roslynsentinel-transcriptmetrics.ps1`, `scripts/Get-SlipCounts.ps1`,
  `scripts/Get-SlipCounts.Tests.ps1`
- Depends on: 9a-1
- Effect: behaviour-changing (new `Get-SlipCounts` columns; escape-copy scope per D26)
- Change:
  - Escape scan (D26): keep `$EscapeScanFields` (`roslynsentinel-transcriptmetrics.ps1:13`)
    as is; add `$EscapeScanFieldsWide = $EscapeScanFields + @('newMemberSource', 'firstLineText', 'lastLineText')`
    beside it; `Get-WriteTargets` (today `Get-WriteTargets($callArgs)`, no param block; callers
    `Get-SlipCounts.ps1:157,163` and `Get-ToolResultFormatArmTable.ps1:138,146`) gains an optional
    second parameter `$Fields = $EscapeScanFields`; `Get-SlipCounts.ps1` passes `$EscapeScanFieldsWide`.
    `Get-ToolResultFormatArmTable.ps1` keeps the default, so its columns are unchanged.
  - Sidecar reading (D28): dot-source `Join-Path $PSScriptRoot 'eval-review\EvalRunReader.ps1'` (as
    `Get-RunDigest.ps1:21`; check for function-name clashes with the transcript-metrics helper first) and
    per run call `Read-EvalSiteGrades` then `Get-EvalSiteTally`.
  - Per-run columns: the nine tally fields of Step 9a-1 (`SitesChanged` ... `SeparatorHits`), plus the
    per-run rates D17 promises: `ResidualSlipRate` (SitesWrong / (SitesCorrect + SitesWrong)) and
    `CompletionRate` ((SitesCorrect + SitesWrong) / SitesChanged), each null on a zero denominator; all
    null when the run has no sidecar. Name the new columns in the script's header comment.
  - Tests, in the existing `Assert-That` style: give `New-SynthRun` (`Get-SlipCounts.Tests.ps1:52`) an
    optional `-Root` parameter defaulting to `$root`, create a second synthetic root for the site runs
    and remove it in the existing `finally` (`:211`), so `$rowsRaw.Count -eq 17` (`:163`) still holds.
    Cases: a run folder holding a copy of the shared fixture gives the expected per-run columns; a Member
    call with an escape copy in `newMemberSource` and a ReplaceRange call with one in `firstLineText`
    each count as escape-copy.
- Done when: `pwsh -NoProfile -File scripts\Get-SlipCounts.Tests.ps1` passes.
- Lane: implementer. Effort: S. Pairs with: Step 8.

### Step 9b - `Get-SlipCounts.ps1 -Summary`: D17 rates and the run-level CI
- Files: `scripts/Get-SlipCounts.ps1`, `scripts/Get-SlipCounts.Tests.ps1`
- Depends on: 9a-2
- Effect: behaviour-changing (new summary columns; existing columns unchanged)
- Change: `-Summary` adds per scenario: `ResidualSlipRate` (sum Wrong / sum (Correct + Wrong)),
  `ResidualCiLow`, `ResidualCiHigh`, `CompletionRate` ((Correct + Wrong) / Changed), `CollateralRate`
  (sum ControlHits / sum ControlSites), `BoundaryRate` (sum SeparatorHits / sum SeparatorSites),
  `RefusedPer100Sites` (existing SlippedWrites x 100 / sum SitesChanged), `RunsWithSlipShare` (runs
  with SitesWrong > 0, ControlHits > 0 or SeparatorHits > 0, over runs with a sidecar). Sums run over
  the group's runs that have a sidecar; a group with none, or a zero denominator, gets null in that
  column. CI: 2000 bootstrap resamples of the sidecar runs with replacement, `System.Random` seeded by a
  new `[int]$BootstrapSeed = 1` parameter (its user: the test script); resamples with a zero denominator
  skipped; sort the remaining statistics and take nearest-rank percentiles (index
  `[math]::Ceiling(p x n) - 1` for p = 0.025 and 0.975). Name the new summary columns and
  `-BootstrapSeed` in the script's header comment. Tests: the `-Summary` rows of the test table.
- Done when: `pwsh -NoProfile -File scripts\Get-SlipCounts.Tests.ps1` passes.
- Lane: implementer. Effort: S. Pairs with: Step 14a.

### Steps 10-13 - The four families
Common to all four:
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Transcription/<Id>Scenario.cs` (new,
  one per step), namespace `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription`.
- Depends on: 3
- Effect: text-neutral (not registered until Step 14a)
- Shape: `internal static class <Id>Scenario` with a private static `SlipSiteSet Sites` field or
  property declared first, then `public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build("<Id>", <title>, <source>, Sites);`.
  `FilePath = "ContosoOrders.Core/Evals/<Id>/<ClassName>.cs"`, `Namespace = "ContosoOrders.Evals.<Id>"`.
  Source: `"plans/plan_slip_first_scenarios.md (<family>)"`.
- Content rules: every rule `SlipFamilyBuilder.Validate` checks (`SlipFamilyLayout` values), plus:
  - every member is `public static`, has a unique descriptive name, and is one line (the doc-comment
    family: at most `MaxDocCommentLines` `///` lines plus one member line);
  - at least one change per Changed site lies inside the family's slip-prone span, so a minimal edit
    anchor still contains slip-prone text; not automated: the step report lists, per site, which
    change is inside the span;
  - no change is a rename of a declaration (member, parameter or enum member name), so RenameSymbol and
    ChangeSignature cannot do it;
  - site text written as raw string literals; where a site's text contains `"""` or starts or ends
    with `"`, use a 4-quote delimiter or the multi-line raw form;
  - "each `Old` occurs exactly once" is a substring test over the whole site text, member name
    included (e.g. `int` also matches inside `Print`); pick fragments `Validate` accepts.
- Before reporting, the implementer runs a scratch check (not committed) or reasons through
  `Validate` for every site; the binding check is Step 14b's test.
- Done when: `Build` 0 errors.
- Lane: implementer-senior (writing 144 escape-heavy sites is itself slip-prone for a Haiku
  implementer). Effort: M each. Run as pairs: Steps 10 + 11, then 12 + 13 (disjoint files).

### Step 10 - EscapeLiteralsSlip (class `EscapeLiteralMessages`)
- Sites are methods returning `string`: about 10 regular interpolated strings (`$"..."` with `\"`, `\\`,
  `\n`, `\t`), about 10 verbatim or interpolated verbatim strings (`@"..."`, `$@"..."` with `""` and
  `{{ }}`), about 10 regex patterns as verbatim strings (for example `@"^\d{3}-[A-Z]{2}$"`). Each literal
  holds at least two escape constructs. Changes are words inside the literal.
- Preamble: empty.

### Step 11 - DocCommentsSlip (class `DocCommentCatalog`)
- Each site: at most 3 `///` lines, e.g. a one-line `/// <summary>...</summary>` holding `&lt;`/`&gt;`
  text, a `<see cref="..."/>` to a real BCL generic type in `{T}` form (for example
  `Dictionary{TKey, TValue}`) and a `<paramref name="value"/>`, then a one-line
  `/// <returns>...</returns>`; member `public static int <Name>(int value) => value;`.
  Changes: words in the summary or returns text, or the cref's type name to another real BCL type (for
  example `List` to `IReadOnlyList`).
- Preamble: empty (`ContosoOrders.Core.csproj` has `ImplicitUsings` enabled, so BCL crefs resolve).
- Budget: this is the largest family; Step 14b's in-process budget test must pass.

### Step 12 - LongGenericsSlip (class `LongGenericDeclarations`)
- About 20 fields `public static readonly <type> <Name> = new();` and about 10 methods
  `public static <type> <Name>() => null!;`, each type nested at least 3 levels (for example
  `Dictionary<string, List<KeyValuePair<int, HashSet<string>>>>`). Field outer types are concrete with a
  parameterless constructor (target-typed `new()`). Changes: two type names at different nesting depths,
  keeping the declaration valid (for example `int` to `long`, `List` to `HashSet`).
- Preamble: empty (implicit usings).

### Step 13 - NearDuplicateIdentifiersSlip (class `ShippingKindMessages`)
- Preamble: `public enum ShippingKinds { Overnight, Overland, Overseas, Overweight }` (no such enum
  exists in `Samples/`).
- Each site: `public static string <Name>(int n) => $"...{ShippingKinds.<A>}...{ShippingKinds.<B>}...";`
  with two different members. Changes: which enum members are referenced (for example `Overnight` to
  `Overland`), never the enum declaration.

### Step 14a - Register the families
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenarioRegistry.cs`,
  `RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs`
- Depends on: 8, 10-13
- Effect: behaviour-changing (four new scenarios in the registry)
- Change: append the four `<Id>Scenario.Definition` entries after `SweepNamedSitesSlipScenario.Definition,`
  (`ImplementationScenarioRegistry.cs:34`); count pin `Has.Count.EqualTo(18)` -> 22
  (`ImplementationScenarioGoldenTests.cs:301`, `Registry_IdsUniqueAndMatchClassNames`); the four ids in
  the Transcription row (`:355`) of `Registry_AxisAssignment_MatchesThePlan` (`:346`).
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S

### Step 14b - Content tests and catalog
- Files: `RoslynSentinel.Tests.ModelEval/TranscriptionScenarioTests.cs`; regenerated
  `docs/generated/modeleval_scenarios.md`
- Depends on: 5, 9a-1, 14a
- Effect: text-neutral (tests and generated doc)
- Change: in `TranscriptionScenarioTests`, a `private static IEnumerable<string> SlipFamilyIds()` that
  derives the ids from `ImplementationScenarioRegistry.All` where `Steps[0].Sites` is not null (no
  hand-kept list), a private helper resolving an id to its scenario and its `Steps[0].Sites`, the three
  `SlipFamily_*` tests of the test table, each `[TestCaseSource(nameof(SlipFamilyIds))]` taking
  `string familyId`, and the two guard tests of the test table (`SlipFamily_EveryPlanScenarioHasSites`,
  `SiteVocabulary_MatchesEnums`; the latter reads `scripts/eval-review/vocabulary.json` from the repo
  root via `RepoRootLocator`, because the existing vocabulary pin in Tests.Harness cannot see
  Tests.ModelEval types). Then run `.\scripts\Generate-ArchitectureMap.ps1` (regenerates the catalog via
  `ScenarioCatalogFreshness`).
- Done when: `Build` 0 errors (the dispatcher then runs `RunTest` on `TranscriptionScenarioTests`, all
  pass, and checks the catalog diff lists the four ids).
- Lane: implementer. Effort: S

### Step 15 - Pre-E1 gate
- Files: none (verification; record the pinned SHA in the tracker)
- Depends on: 6, 9b, 14b
- Effect: none
- Change: `Build` (solution) 0 errors; `Search(mode: text, query: "NotImplementedException")` over the
  plan's new files returns no stub; `RunTest` on `SlipFamilyBuilderTests`, `SiteGraderTests`,
  `TranscriptionScenarioTests` and the in-process Tests.ModelEval baseline (0 failures);
  `pwsh -NoProfile -File scripts\Get-SlipCounts.Tests.ps1` and
  `pwsh -NoProfile -File scripts\eval-review\EvalRunReader.Tests.ps1`; then the explicit
  `ImplementationScenarioGoldenTests` cases whose names contain the four ids (Seed, Golden, Previous,
  Budget, Files). Heavy (git plus `dotnet build`/`test` per case), justified: only these prove the
  rendered files build and pass ContosoOrders' tests and that `VerdictAsync` grades end to end.
- Done when: all of the above pass. D18 pre-E1 gate: every family's golden grades all Correct and its
  no-op grades all Changed sites Unchanged with `CheckFailed`.
- Lane: dispatcher. Effort: S

### Step 16 - E1 instrument-validity batch (LIVE)
- Files: none (results under the scratch root)
- Depends on: 15
- Effect: none
- Change: before submitting, `scripts\roslynsentinel-evalqueue.ps1 hosts` and `status -ActiveOnly`;
  confirm Qwen3.5-9B is loaded on 112 and 113 (no load or unload without owner OK). In the submitting
  shell, unset `ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT` (`RunStepAsync` reads it). Run 8 submit commands (one per
  family per host) with `-Repeats 2`, creating 16 queue jobs, at the Step 15 SHA; the turn cap comes from
  `SlipFamilyBuilder.TurnCap` through `scenario.TurnCap`, so no `-TurnCap` flag:
  `.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress <h> -Test ImplementationScenario -Model unsloth/Qwen3.5-9B-MTP-GGUF -Session slipsite -Purpose 'slipsite-<sha8> scenario=<Id> format=plain host=<h>' -Commit <sha> -Repeats 2 -NoRunFolderMinutes 0 -EvalArgs '-Scenario <Id> -StepWallClockMinutes 30'`
  (the predecessor's E1 form, `plans/closed/plan_slip_focused_scenario_variants.md:664`, plus
  `-Repeats`, `roslynsentinel-evalqueue.ps1:178`). Confirm with `status` that 16 runs are queued (the
  queue dedupes identical cells; add `-Force` only if it attached to an existing job). Thinking off;
  sampler per `reference_model_eval_procedure.md`; record the effective sampler and
  `ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT` (off).
- Outcome classes per run (D18): CleanRun (Pass); SlipRun (Correct + Wrong >= 20 and any Wrong, Control
  hit or Separator hit); ShortRun (Correct + Wrong < 20); Broken (build or test failure).
- Report: an E1 table in this plan's Decision log (host, family, class, site counts, control and
  separator hits, refused slips, turns, stop reason),
  `Get-SlipCounts.ps1 -Machine <h> -PurposePattern 'slipsite-<sha8>*' -Summary` per host, and a manual
  check of every Wrong site (sidecar `endStateText` against the golden): each is a real slip (name the
  family construct) or a grader false positive.
- Thresholds (D18): keep a family when >= 3 of its 4 runs reach 20 graded Changed sites and the manual
  check finds 0 false positives; rework or drop it at >= 2 ShortRuns or any false positive; 0 Wrong and
  0 refused slips over all runs: keep, marked "not slip-prone for this model".
- Lane: modeleval-runner (log triage via model-eval-log-analyst). Effort: L

### Step 17 - Close-out
- Files: `docs/current/references/reference_model_eval_procedure.md`,
  `docs/current/references/reference_log_sources_and_cross_referencing.md` (its `Get-SlipCounts` and
  `EvalRunReader` lines: the sidecar and new columns), `docs/current/ideas/idea_slip_first_scenarios.md`,
  `docs/current/TODO.md`, this plan
- Depends on: 16
- Effect: none (docs)
- Change: E2 sizing per kept family: runs needed for a 95% run-level CI half-width of 2 percentage
  points, n = (1.96 x s / 0.02)^2 with s the between-run SD of per-run residual rates from E1; record it
  in the Decision log and as a TODO.md line for the E2 batch. In "Slip instruments"
  (`reference_model_eval_procedure.md:483-497`): describe the per-site instrument (sidecar, columns,
  command) and, if E1 kept at least one family, the D20 note. Idea doc Status: implemented by this plan.
- Done when: `Git(status)` shows the five docs committed with the plan trailer.
- Lane: main session. Effort: S

## Test table

| Test | Behaviour | Fixture | Asserts / pass criterion |
| --- | --- | --- | --- |
| `SiteGraderTests.Grade_WhitespaceOnlyDifference_IsCorrect` | D11 | inline strings | `Outcome == Correct` |
| `SiteGraderTests.Grade_MemberReflowedAcrossLines_IsCorrect` | D11 | inline | `Correct` |
| `SiteGraderTests.Grade_LiteralEscapeDifference_IsWrong` | D11, D13 | inline | `Wrong`, `EndStateText` not null |
| `SiteGraderTests.Grade_SeedText_IsUnchanged` | D13 | inline | `Unchanged` |
| `SiteGraderTests.Grade_MemberAbsent_IsMissing` | D13 | inline | `Missing` |
| `SiteGraderTests.Grade_MemberDuplicated_IsMissing` | D13 | inline | `Missing` |
| `SiteGraderTests.Grade_FileAbsent_AllMissing` | D13 | `Grade(null, set)` | every grade `Missing` |
| `SiteGraderTests.Grade_DocCommentWordChanged_IsWrong` | D11 | inline | `Wrong` |
| `SiteGraderTests.Grade_DocCommentReflowed_IsCorrect` (same words over a different number of `///` lines) | D11, D24 | inline | `Correct` |
| `SiteGraderTests.Grade_ControlEdited_IsWrong` | D10, D13 | inline | `Role == Control`, `Wrong` |
| `SiteGraderTests.Grade_SeparatorEdited_IsWrong` | D10, D13 | inline | `Role == Separator`, `Wrong` |
| `SiteGraderTests.AllIntact_FalseWhenAnyGradeNotCorrect` (loops over the non-Correct outcomes inside the body) | D15 | inline grades | `false` for each non-Correct outcome, `true` for all Correct |
| `SiteGraderTests.ReadSidecar_SharedFixture_GivesExpectedGrades` | D14, D24 | `SiteSidecars/step-1.sites.json` via `RepoRootLocator` | equals the 7 expected grades built in the test |
| `SiteGraderTests.WriteSidecar_RoundTripsGrades` | D14 | temp folder under the scratch root | `ReadSidecar(WriteSidecar(x)) == x` |
| `SlipFamilyBuilderTests.Expand_InterleavesUniqueSeparators` | D10, D23 | inline 3-site set | separator count = observed - 1, at odd 0-based indices; names and texts distinct |
| `SlipFamilyBuilderTests.ApplyChanges_FragmentNotUnique_Throws` | D22, D24 | inline | `InvalidOperationException` |
| `SlipFamilyBuilderTests.ApplyChanges_ReplacesAtSeedPositions` (a `New` that contains a later `Old`) | D22, D24 | inline | result equals the expected golden |
| `SlipFamilyBuilderTests.RenderPrompt_ContainsEachChangedSiteLineOnly` | D8, D22 | inline | contains `RenderChangeLine(s)` for each Changed site; contains no Control or Separator name |
| `SlipFamilyBuilderTests.Validate_ValidLayout_ReturnsNothing` | D9, D24 | 36-site set from `SlipFamilyLayout` | `Is.Empty` |
| `SlipFamilyBuilderTests.Validate_FlagsEachRule` (one `TestCase(string ruleName)` per rule; the body builds the broken set) | D7, D9, D24 | valid set with one rule broken | `Is.Not.Empty` |
| `SlipFamilyBuilderTests.Render_GoldenAllCorrect_SeedAllUnchanged` | D12, D18 gate | inline + `SiteGrader` | golden file: all `Correct`; seed file: Changed `Unchanged`, others `Correct` |
| `SlipFamilyBuilderTests.Build_SetsSitesAndSeedEqualsRenderedSeed` | D12 | inline | `Steps[0].Sites` is the set; `SeedFiles[FilePath] == RenderFile(set, false)`; `TurnCap == SlipFamilyBuilder.TurnCap`; `PrimaryAxis == EvalAxis.Transcription` |
| `TranscriptionScenarioTests.SlipFamily_PassesValidation` (4 cases) | D7, D9, D22 | the four families | `Validate(sites)` `Is.Empty` |
| `TranscriptionScenarioTests.SlipFamily_GoldenAllCorrect_SeedAllUnchanged` (4 cases) | D18 gate (in-process) | the four families | as the builder test, on the real families |
| `TranscriptionScenarioTests.SlipFamily_FitsContextBudget` (4 cases) | D21 | the four families | `ContextCharacters() <= ImplementationScenario.ContextBudgetCharacters` |
| `TranscriptionScenarioTests.SlipFamily_EveryPlanScenarioHasSites` | D27 guard | registry | every scenario whose `Source` contains `plan_slip_first_scenarios` has `Steps[0].Sites` not null, and there is at least one |
| `TranscriptionScenarioTests.SiteVocabulary_MatchesEnums` | D28, item 2 | `scripts/eval-review/vocabulary.json` via `RepoRootLocator` | `siteRoles` equals `Enum.GetNames<SiteRole>()`, `siteOutcomes` equals `Enum.GetNames<SiteOutcome>()`, in order |
| `ImplementationScenarioGoldenTests.Golden_StepPassesItsCheck` (existing, extended) | D15, D21 | existing explicit fixture (git + dotnet test) | `Pass`, every site `Correct` |
| `ImplementationScenarioGoldenTests.Previous_StepFailsTheCheck` (existing, extended) | D15, D21 | same | `CheckFailed`; Changed `Unchanged`, others `Correct` |
| `ImplementationScenarioGoldenTests.Seed_*`, `Budget_FitsContextWindow`, `Files_NamesEveryAllowedPath`, `Registry_IdsUniqueAndMatchClassNames`, `Registry_AxisAssignment_MatchesThePlan` (existing) | D19, D21 | existing | as today, with 22 ids |
| `EvalRunReader.Tests.ps1`: `Read-EvalSiteGrades` + `Get-EvalSiteTally` on the shared fixture; folder without sidecar | D14, D28 | shared fixture; empty temp folder | 7 grades, tally 4/1/1/1/1/2/2/1/1; `$null`; unknown outcome throws |
| `Get-SlipCounts.Tests.ps1`: run folder with the shared fixture | D17, D24 | second synthetic root | `SitesChanged` 4, `SitesCorrect` 1, `SitesUnchanged` 1, `SitesWrong` 1, `SitesMissing` 1, `ControlSites` 2, `ControlHits` 2, `SeparatorSites` 1, `SeparatorHits` 1 |
| `Get-SlipCounts.Tests.ps1`: escape copy in `newMemberSource` and in `firstLineText` | D21, D23 | synthetic transcripts | each counted as escape-copy |
| `Get-SlipCounts.Tests.ps1`: `-Summary` rates on two crafted runs | D17 | synthetic | exact `ResidualSlipRate`, `CompletionRate`, `CollateralRate`, `BoundaryRate`, `RefusedPer100Sites`, `RunsWithSlipShare` |
| `Get-SlipCounts.Tests.ps1`: `-Summary` on a group without sidecars | D17 | existing synthetic runs | the new columns are null; existing columns unchanged |
| `Get-SlipCounts.Tests.ps1`: CI on three runs with residual 1/30, 2/30, 3/30, seed 1 | D17 | synthetic | `ResidualCiLow <= 2/30 <= ResidualCiHigh`; two invocations give identical CI |

## Out of scope

- The `batchEdits` hide arm (D2).
- Logic, scope-judgement or edit-order testing.
- Fixing slips (`plans/closed/plan_server_slip_recovery.md`, `plans/closed/plan_plain_text_tool_results_for_eval_model.md`).
- E2 (the rate-measurement batch), sized by Step 17.

## Grounding (2026-11-26, explore-tier2 at 82dc6706)

- Scenarios are C# string constants (`<Axis>/<Id>Scenario.cs`) overlaid on `Samples/ContosoOrders` by
  `GoldenStepRepository.Create` (`Tests.Harness/GoldenStepRepository.cs:12`); no fixture files.
- `VerdictAsync` (`ImplementationScenarios/Graders/ImplementationScenarioChecks.cs:17`): build + unfiltered
  tests, early return on `BuildBroken`/`CheckFailed`, then `ExtraCheck` (`Func<ctx, Task<string?>>`, first
  failure only), then scope. No hook can return per-site results; `ScenarioVerdict` has 4 fields, no counts.
- Run output: `step-N.outcome.txt` tokens (`ImplementationScenarioAgentTests.cs:199`), no results.csv for
  this family; parsers `Get-SlipCounts.ps1`, `Get-ToolResultFormatArmTable.ps1`,
  `Get-ThinkingArmTable.ps1`, and `scripts/eval-review/EvalRunReader.ps1`
  (`plan_modeleval_run_review_pipeline.md`, IN PROGRESS), which reads outcome tokens through
  `vocabulary.json`.
- Closest grader precedent: `LargeBlockRewriteScenario.CheckExactFile` (`ToolUse/LargeBlockRewriteScenario.cs:221-245`),
  whole file, first difference only. Normalisers: `ImplementationScenario.NormalizeToCrlf` (`:142`).
- Golden/no-op tests: `ImplementationScenarioGoldenTests` (`[Explicit]`, heavy) `Golden_StepPassesItsCheck`
  (`:49`), `Previous_StepFailsTheCheck` (`:74`, asserts only not-Pass); `Budget_` (`:98`, 24,000 chars,
  `ImplementationScenario.cs:99`); count pin 18 (`:301`), axis pin (`:346`, row `:355`),
  folder-equals-axis (`:377`).
- Compile gate always on in evals (`validateOnApply` hidden, `ModelEvalMcpEnvironment.cs:126`): slips that
  break the build are refused; silent slips are the ones that compile.
- Near-miss auto-correct (`ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT`) defaults off; tool results default
  `plain` since e3995b05. Triggered-thinking plan (closed, IMPLEMENTED 2026-11-26) affects thinking-on
  runs only.
- `scripts/eval-review/EvalRunReader.ps1` exists (run-review plan), with `Read-EvalStepOutcome` (`:175`)
  and `Find-EvalRunVerdict` (`:265`); `Get-SlipCounts.ps1:120` carries a migrate-to-EvalRunReader TODO.
- All touched files are in test projects; none over 1500 lines, none in `Common`.

## Risks and open decisions

Open: none (Q25 = D28).

Risks:
- ContosoOrders build: no warnings-as-errors today (D24); Step 15's golden run catches any rendered
  member that does not build.
- Identifier family: the same `Old` fragment (for example `Overnight`) repeats across sites, so a
  file-wide `ReplaceSnippet` anchor can be ambiguous. Those refusals are not slips; E1 reports them.
- Identifier family: a model may call RenameSymbol on an enum member, which would turn many sites Wrong at
  once. Visible in the transcript; one-line note if it happens (slip axis, not reasoning).
- Turn cap 40 may be short if the model edits one site per call; E1's ShortRun class measures it.
- Collateral rate counts Control Missing too (D23), a small extension of D17's wording.
- Identifier family: the preamble enum is not a graded site. An edit to it either breaks the build
  (refused by the compile gate) or, via RenameSymbol, turns sites Wrong; both visible.
- DocCommentsSlip is the family closest to the 24,000-character budget (reviewer estimate 22-24k at 5
  doc lines per site, a hypothesis); the 3-line cap and the in-process budget test bound it.

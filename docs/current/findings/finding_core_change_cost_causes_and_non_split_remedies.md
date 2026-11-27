# Finding: core changes are costly because of the verification loop and per-site idioms, not layer coupling

**Status:** OPEN 2026-11-21. Fix not decided; four plans are drafted (see Recommendation) and await owner review.

## Context

The owner asked whether anything other than freezing unused Advanced tools into a "tier 2" outside the
main solution ([proposal_usage_based_core_split.md](../proposals/proposal_usage_based_core_split.md),
[finding_tool_surface_usage_review.md](./finding_tool_surface_usage_review.md)) would reduce the effort of
carrying a core change into `RoslynSentinel.Engines.Advanced` and `RoslynSentinel.Tools.Advanced`: more
interfaces, better DTOs, less coupling between layers.

Four read-only reviews ran on 2026-11-21:

1. Docs and git history from 38d37dbf (2026-10-19, where the split proposal's own analysis stops) to b0f56887.
2. Production coupling between core (`Common`, `Engines.Basic`, `Tools.Basic`, server) and Advanced.
3. Coupling of the four Advanced test projects to core.
4. Refactoring-tool gaps and build/test time, from `.claude/journal/*.calls.jsonl` and the full-run
   results under `C:\tmp\rs-testtrees\_results`.

The four plans in Recommendation were then written against the source on the same day. Where a plan's
recount differs from a review, this doc carries the plan's number.

How to read the numbers:

- Counts of code patterns are matching lines from `Search` text mode on 2026-11-21, not occurrences,
  and they move with the regex used.
- Commit counts per migration were classified by commit subject, so they are approximate.
- Summed tool time double-counts agents that ran at the same time.
- Items marked "estimate" were not measured directly.

## What is broken

### 1. The build and test loop is the largest measured cost

- `Build` (3,099 calls, 15.8 h) and `RunTest` (4,983 calls, 71.2 h) are about 74% of the tool time
  recorded in the journal between 2026-10-02 and 2026-11-22.
- The loop changed on 2026-11-11, when test runs moved from agent `RunTest` calls to detached worktree
  runs (`scripts/Invoke-WorktreeTests.ps1`). `RunTest` was 62.7 h before that date and 8.4 h since. The
  cost is now the wait for the per-batch full run: 29.4 h of worktree run time, 15.4 h of it in 40 full runs.
- The 40 full runs have a median of 1,317 s (about 22 minutes) and a p90 of 1,695 s.
  [reference_changing_claude_lean_tools.md](../references/reference_changing_claude_lean_tools.md) line 70
  still says "about 5.5 minutes".
- Baseline run at a3d98b2e (10,740 tests, 1,295 s): `Tests.Tools.Basic` 921 s, `Tests.Server` 158 s, the
  four Advanced test projects together 83 s (6.8%).
- The cost is a serial tail, not test volume. `Tests.Tools.Basic` runs about 20 tests at once for the
  first 250 s, then one at a time for 618 s. The serial tests are the `[NonParallelizable]` sites: 63 in
  `Tests.Tools.Basic` and 21 in `Tests.Server`.
- The slow fixtures are already tagged `[Category("OutOfProcess")]` (67% of suite test-seconds) and
  `RunTest` defaults to `suite: fast`, but the worktree script has no suite option, so every batch run
  pays for them.
- 16 of the 40 full runs were red: 7 from ratchet (source-scan) tests only, 1 from both, 8 from other
  tests. Every ratchet failure dates from 2026-11-12 to 2026-11-15; there are none since ratchets joined
  the per-slice runs (54d071e8). Of the other 8, 3 are 30-second solution-load timeouts and 4 look like
  regressions fixed by the next commit (read from the failure text in `summary.json`; not re-run).
- Verification was about 45% of wall-clock in the `DocumentChangeSet` retype session and about 31% in the
  test factory rollout session (estimates from tracker and journal times).
- There is no affected-test selection: `RunTest` takes a filter or a project.
  [proposal_runtest_tests_for_members.md](../proposals/proposal_runtest_tests_for_members.md) has no code.

### 2. The costliest migrations were policy changes made at every site

Net diff 38d37dbf..b0f56887 (insertions plus deletions): Engines.Advanced 5,845 lines in 33 files,
Tools.Advanced 1,825 in 15, Tools.Experimental 402 in 2, the Advanced test projects 25,801 in 165;
Common 30,322, Engines.Basic 22,095, Tools.Basic 11,767. Advanced production is about 11% of production churn.

| Core change | Why Advanced had to change | Advanced cost | Mechanical share (estimate) |
| --- | --- | --- | --- |
| Typed exceptions replace failure results | Two failure carriers in 148 `Task<DocumentEditResult>` methods; tests pinned the old result | about 45 production and 29 test commits | 40% |
| Not-found orientation | 136 engine-local `new ToolNotFoundException(` sites with free text; exact-text assertions | 14-slice sweep (8e5d82d2..35eb7153); about 31 and 26 commits | 20% |
| First-match ambiguity audit | Engine-local name-keyed `First()` picks; no shared resolver | about 18 and 7 commits | 20% |
| Test construction factories | Tests built engines with `new` | 1,165 of about 1,450 sites, in 4 commits | 95% |
| EOL policy step 8c | A defaulted helper parameter became required; tests pin exact bytes | 25 engine sites and 17 test files (15c1cae0, 6913f22e) | 70% |
| `IResultStatus` renamed `IOperationResult` (f5f4067e) | Shared interface name | 6 engine files, 2 test files, one `RenameSymbol` call | 100% |
| `DocumentChangeSet` retype | About 195 `new Dictionary<FilePathWrapper, string>` construction sites | 7 commits, 10 files, about 88 lines, 1 test line | 60% |
| `UpdatedText` made non-nullable (600251b0) | Redundant `!` left in tests | 38 test files, one `SemanticFindReplace` call | 100% |

The top three rows were judgement at every site and are 94 of the 200 newest Advanced production commits
(the ones classified). The compile-forced rows (rename, retype, parameter) were cheap wherever one tool
call could express the edit.

### 3. The same idioms are written out in every engine and tool method

| Idiom | Engines.Advanced | Engines.Basic |
| --- | --- | --- |
| Typed `Tool*Exception` throw sites | about 231 (91 are `new ToolNotFoundException(`) | at least 78 |
| Uses of the `MemberRefactoringNotFound` factory family | 0 | 69 |
| `new DocumentEditResult` object initializers | 108 | 76 |

| Idiom | Tools.Advanced (51 tools) | Tools.Basic (66 tools) |
| --- | --- | --- |
| `new SentinelCallToolResult` | 328 | 471 |
| `catch (Exception ex)` | 74 | 108 |
| `ToolErrorMapper.ToResultError` | 61 | 66 |
| `ResolveFromWire` | 35 | 55 |

- A not-found factory family exists in Engines.Basic and Engines.Advanced does not use it.
- Hand-rolled `Identifier.Text == x` lines number 138 to 207 in Engines.Advanced and 83 to 86 in
  Engines.Basic depending on the regex. Most feed `NameTargetLookup`, so this is not a count of bypasses;
  the census step of the engine-helpers plan classifies them. `FirstMatchRatchetTests` (30 baseline keys)
  and `ErrorPolicyKindRatchetTests` already guard new sites.
- `SentinelCallToolResult` has no factories.
- The server catch-all `CallToolFailureHandler.ToFailureResult` does the same exception-to-error mapping
  as the per-tool catch blocks.
- `ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs`)
  takes 17 parameters. Six private wrapper copies of it sit in `AdvancedRefactoringTools`,
  `GenerationTools`, `CoreRefactoringTools`, `RefactoringSignatureImpl`, `RefactoringStructuralImpl` and
  `RefactoringExtractionDocsImpl`.
- The copies have drifted. `recordEffects` / `RecordEffect` has 27 matches, all in Common and
  Tools.Basic and none in Tools.Advanced: the two Advanced wrappers have no `recordEffects` parameter, so
  the 24 apply sites behind them record no EditEffects. A `// TODO` marks
  `AdvancedRefactoringTools.ValidateAndApplyAsync`.

### 4. Tests: construction is fixed; placement, helpers and message text are not

- The factory rollout (56865274, 2026-10-28) works: a `SymbolNavigationEngine` constructor change
  (86c26b43) touched 2 files and no test sites. 30 direct `new` sites remain in the Advanced tests.
- No placement guardrail exists. A class-level census finds 30 files (506 KB) in `Tests.Advanced` and
  `Tests.Tools.Advanced` whose subject is a Basic engine or tool, for example `ChangeSignature*Tests`,
  `MoveMemberSuccessTests`, `MemberRemoveTests`. Relocating them whole is blocked: no file-move tool
  exists ([proposal_move_file_tool.md](../proposals/closed/proposal_move_file_tool.md) is not built).
- Duplicated helpers in the Advanced tests: 12 copies of `Src(bool crlf, params string[] lines)`, 10 of
  `WithStrayCrlf`, 143 private `SetFiles` / `SetSource` helpers. `RoslynSentinel.Tests/Support/Sources.cs`
  has no shared `Src`.
- 572 `Does.Contain` assertions on message, hint or detail text in the Advanced tests (1,370 in the core
  tests). By one regex 81 and 257 of them pin a literal of 35 or more characters; the test plan's regex
  finds 523 long pins in 135 files across all test projects. A wording change breaks them.
- A second tool factory, `Tests.Tools.Advanced/BatteryToolFactory.cs`, has 225 calls, 136 of which build
  Basic subjects.

### 5. What was cheap, and why

- A single owner: the stale-read guard touched Advanced in 0 of 10 commits, the ReplaceSnippet
  token-equivalent match in 0 of 14, wire marshaller steps 8 and 10 in 1 of 11 each.
- Factories: a trial required constructor parameter on `ValidationEngine` hit 2 production sites and 0
  tests, down from 75.
- Prep-then-flip on shared types: the final flip commits touched 1 to 3 files (d8e4958b, 1daccad1,
  3864db85, 36b4a43c).
- Limit: reflective `ActivatorUtilities` callers break with no compile error (`CLOSED.md` line 31).

### 6. Refactoring-tool gaps exist but are a minority

Where a one-call tool existed, mechanical edits cost minutes. The gaps cover a few hundred sites: a typed
pattern find-replace with captures (about 250 to 300 sites, estimate), `replaceConstruction` that carries
object initializers, and a preview of the dependents a type change breaks (6 of 35 dispatches in the
`DocumentChangeSet` run were failed or rescoped). `WarningLevel 1` hides CS0618, so a migration-first
change cannot list its remaining callers from the build. The plans add three more: no tool rewrites the
799 initializer-form result envelopes, none unwraps a catch that only maps the exception, and none moves
a whole file between projects.

## Root cause

- **Not layer coupling.** Engines.Advanced mentions 5 Basic engine types on 29 lines, inherits from no
  Basic engine, and has no `InternalsVisibleTo` from core. Engines take the workspace through
  `IWorkspaceManager` / `IWorkspaceReader`. Only 4 Advanced engine methods have 7 or more parameters.
- **Cross-cutting policy has no single owner in engines and tools.** How a failure is carried, what a
  not-found error says, how a result envelope is built and how an apply is wrapped are each written out
  per site, or owned in Basic and not used by Advanced. A change of policy is then a sweep with judgement
  at every site, and the copies drift between sweeps (section 3, `recordEffects`).
- **The verification loop is slow for reasons unrelated to Advanced.** Three quarters of a full run is
  `Tests.Tools.Basic`, two thirds of that a serial tail of non-parallelizable fixtures, and the per-batch
  run has no fast suite.
- **Why interfaces and DTO work would not help.** A single-implementation interface does not shrink a
  constructor, exception or message sweep. Shared result-type changes were the cheap rows in section 2.
  [proposal_dto_ownership_role_lineage.md](../proposals/proposal_dto_ownership_role_lineage.md) already
  rejects marker interfaces and a generic `IRequest<TResult>`.

## Why it matters

- A tier 2 freeze removes only Advanced's share: about 11% of production churn and about 7% of full-run
  time. The causes stay in core, where the same idioms are more numerous (471 envelopes, 108 catches).
- Advanced stays compile-green while falling behind in behaviour, so "it builds" is not evidence that the
  Advanced tools are maintained.
- Agents wait about 22 minutes for each per-batch full run, most of it for fixtures unrelated to the
  change under test.

## Recommendation

Nothing is decided. Ranked by measured leverage:

1. **Shorten the verification loop** - [plan_shorten_verification_loop.md](../plans/plan_shorten_verification_loop.md).
   A fast suite for the per-batch worktree runs, cuts to the serial tail, the solution-load timeout;
   affected-test selection stays a separate plan.
2. **One tool pipeline** - [plan_shared_tool_execution_pipeline.md](../plans/plan_shared_tool_execution_pipeline.md).
   One apply entry point with an options record and `recordEffects`, factories for
   `SentinelCallToolResult`, error mapping owned by the server catch-all.
3. **Central engine helpers** - [plan_central_engine_throw_and_lookup_helpers.md](../plans/plan_central_engine_throw_and_lookup_helpers.md).
   One helper family for typed throws that reuses `MemberRefactoringNotFound`, with a ratchet; name
   matches only where the census shows a real bypass.
4. **Tests** - [plan_test_placement_guardrail_and_shared_test_helpers.md](../plans/plan_test_placement_guardrail_and_shared_test_helpers.md).
   Placement guardrail and relocation, one `Src` / `WithStrayCrlf` kit with byte-exact and
   whitespace-insensitive asserts, error-code-first message assertions.
5. **Refactoring-tool gaps** - no plan yet. A typed pattern find-replace and a file-move tool are the
   largest; CS0618 visibility appears to be in progress in another session (INBOX lines of 2026-11-21
   22:40, inferred).

These complement the tier 2 split and do not replace it. Each plan marks the slices that are wasted if
unused Advanced tools are frozen and puts them last, so the freeze decision can be taken after the core
slices. To separate the causes better next time, record per migration the ratio of compiler-forced edits
to intended edits, and log agent latency beside tool time.

## Out of scope

- **Engine interfaces.** 5 types on 29 lines; they would not shrink any sweep in section 2.
- **Request records for Advanced engine calls.** 4 methods qualify; long signatures are in Engines.Basic (63).
- **`DocumentEditResult` factories.** Its shape changes cost 1 to 3 files each.
- **Result-shape test helpers and approval snapshots.** The `DocumentChangeSet` retype cost one test line;
  snapshots would restructure about 330 tests and add review burden.
- **Generated tool registration.** Registration does not change when core changes. It has three
  production places as CLAUDE.md says; `AddRoslynSentinelToolClasses{Basic,Advanced}` have test callers only.
- **Not checked:** `Tests.Server` and `Tests.ModelEval` coupling to Advanced types, `Tools.Experimental`,
  and worktree runs before 2026-11-11.

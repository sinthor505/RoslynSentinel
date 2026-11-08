# Roslyn Duplication Audit

**Status:** CLOSED 2026-11-08. Historical record filed under closed/ by the docs reorganization; the body keeps the original outcome.

**Merged from:** codebase-consistency-audit-v1, plan-codebase-consistency-review-v1

**Started**: 2026-08-22
**Purpose**: Audit RoslynSentinel's Roslyn-based refactoring/analysis tools against functionality
already available natively in the Roslyn compiler and `Microsoft.CodeAnalysis*` NuGet packages,
to identify hand-rolled logic that duplicates (and risks diverging from, or being buggier than)
an existing public Roslyn API.

## Context

RoslynSentinel references only public Workspaces-layer packages:
`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`,
`Microsoft.CodeAnalysis.CSharp` (v5.9.0). It does **not** reference
`Microsoft.CodeAnalysis.Features` / `Microsoft.CodeAnalysis.CSharp.Features`, which hold Roslyn's
internal editor-oriented refactoring services (extract method, etc.) — those types are mostly
`internal` and reachable only via MEF composition inside an editor host (VS/VS Code), not
practically reusable from a stateless RPC-style MCP tool. This constrains what "just call Roslyn
instead" can mean in practice: many tools' custom logic is a legitimate consequence of that
architecture, not carelessness.

## Legend

Detailed findings (#1-3) used the original three-way taxonomy:
- **Duplicate (avoidable)** — a public Roslyn API exists and could directly replace the hand-rolled logic.
- **Duplicate (unavoidable)** — logic parallels something Roslyn does internally, but no public/reusable API exposes it given RoslynSentinel's architecture (stateless, no MEF/editor host).
- **Unique** — no meaningful Roslyn equivalent; the problem RoslynSentinel solves (e.g. stateless snippet-based selection) doesn't exist in Roslyn's own API surface.
- Action: **keep** / **todo** / **fixed** / **removed**

**From #4 onward**, per-tool review is intentionally brief (given the ~100+ tool surface) and uses a
simplified finding: **Duplicate** (parallels a Roslyn-internal or public service — whether avoidable
is noted inline, not tracked as a separate axis), **Unique** (no Roslyn equivalent), or **Needs
investigation** (couldn't classify confidently in a brief pass; needs a deeper look like #1-3 got).

## Findings

| # | Tool | File | Finding | Action | Notes |
|---|------|------|---------|--------|-------|
| 1 | RenameSymbol | RefactoringEngine.cs | Unique (glue) / correct delegation | fixed | Correctly delegates to `Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync`. Removed dead never-called helper `FindIdentifierInSnippet` + orphaned `IsIdentChar`. Internal `ComputeRenameHunks` duplicates line-diff logic already in `RoslynSentinel.Common.DiffEngine.CreateDiff` — flagged, not consolidated (deferred by user). |
| 2 | ExtractMethodSafe | MsToolAugmentEngine.cs | Mixed: snippet resolution is Unique; data-flow-based signature inference is Duplicate (unavoidable) | keep | `ContextHelper.FindSnippetPosition` has no Roslyn equivalent (stateless snippet protocol vs. editor `TextSpan`). `model.AnalyzeDataFlow`-based param/return-type inference parallels Roslyn's internal (non-reusable) extract-method service; hand-rolling was effectively forced by the architecture. Three historical "silently wrong extraction" bugs (documented in the method's header comments) show the real cost of this. Unrelated: fixed a `ct`→`cancellationToken` text-corruption bug found in this file's doc comments (bad prior find/replace), spanning ~15 words; verified via build (0 errors). |

| 3 | ChangeSignature | RefactoringEngine.cs | Duplicate (unavoidable) | keep | Confirmed via reflection over `Microsoft.CodeAnalysis(.CSharp).Features` v4.14.0: every `ChangeSignature`-related type (`AbstractChangeSignatureService`, `ChangeSignatureCodeRefactoringProvider`, `IChangeSignatureOptionsService`, `SignatureChange`, etc.) is `internal`, reachable only via MEF host composition — no public API to delegate to. Hand-rolled reorder-declaration + reorder-call-sites logic is the only option given the architecture. **Risk noted (not fixed, out of scope for this audit):** call-site reorder at RefactoringEngine.cs:205 skips any invocation where `args.Count != parameters.Count` (named args, omitted optional args, `params` expansion) — after the declaration is reordered, such call sites are silently left stale/mismatched with no error surfaced. Worth a follow-up ticket. |

| 4 | ExtractLocalVariable | RefactoringEngine.cs:1503 | Duplicate | keep | Snippet resolution via `ContextHelper` is Unique (no Roslyn equivalent for stateless text targeting); the actual extraction logic parallels Roslyn's internal `IntroduceVariable`/extract-local-variable service (internal, MEF-only, same as ChangeSignature). |
| 5 | InlineVariable | SemanticRefactoringLibrary.cs:25 | Duplicate | keep | Hand-rolled single-assignment inline + reference replacement, parallels Roslyn's internal `InlineTemporaryCodeRefactoringProvider` (internal, MEF-only). |
| 6 | InlineField | GranularRefactoringEngine.cs:198 | Duplicate | keep | Same shape as InlineVariable — no public Roslyn API for field inlining. |
| 7 | InlineParameter | GranularRefactoringEngine.cs:257 | Duplicate | keep | Not deep-read this pass; same file/pattern as InlineField, high confidence by consistency with siblings. |
| 8 | InlineMethod | RefinementEngine.cs:142 | Duplicate | keep | Solution-wide call-site replacement + declaration removal, parallels Roslyn's internal `InlineMethodCodeRefactoringProvider` (internal, MEF-only). |
| 9 | InlineClass | AdvancedStructuralEngine.cs:355 | Unique | keep | Cross-file member migration + solution-wide type-reference rename glue — Roslyn has no "inline class" refactoring at all, not even internally. RoslynSentinel-specific composite operation. |
| 10 | ExtractMembersToPartial | GranularRefactoringEngine.cs:866 | Needs investigation | keep | Not read this pass; likely Unique (splitting a class into a partial is not a standard Roslyn refactoring) but unverified — flagged rather than guessed. |
| 11 | IntroduceParameterObject | GranularRefactoringEngine.cs:931 | Needs investigation | keep | Not read this pass; description (group params into a C# 12 record, rewrite body references, leave call sites as a manual TODO) suggests Unique, but unverified. |
| 12 | SafeDeleteUnusedSymbol | StructuralRefinementEngine.cs:75 | Unique (glue) / correct delegation | keep | Correctly uses `SymbolFinder.FindReferencesAsync` (public API) to verify zero usages before deleting; the deletion itself is trivial tree editing with no meaningful Roslyn equivalent needed. Same "correct delegation" shape as RenameSymbol. |
| 13 | PullUpMember | StructuralRefinementEngine.cs:351 | **Bug, not duplication** | **todo** | `PullUpMemberAsync` is an unimplemented stub (body is a comment + empty-dict return) but is wired to a fully-described, user-facing MCP tool that always fails with a misleading "not found" error. Logged as a real bug in `docs/TODO.md` (separate from this audit's duplication tracking). Sibling `PushMembersDownAsync` (line 360) has the same stub shape but isn't exposed to any tool — lower priority. |
| 14 | SyncInterface | RefactoringEngine.cs:4249 | Needs investigation | keep | Not read this pass; "sync interface to implementation" sounds Unique (no direct Roslyn equivalent) but unverified. |

| 15 | ConvertOutParamsToValueTuple | OutParamRefactoringEngine.cs:30-307 | **Duplicate (avoidable)** | **todo** | Composition (`DocumentEditor`, `SymbolFinder.FindReferencesAsync`) is correct public-API usage, but every node it produces — tuple return type (lines 94-114), all three call-site rewrite branches (lines 224-254), and the `ReturnRewriter`'s return statement (lines 343-370) — is built by string-interpolating C# source and re-parsing via `SyntaxFactory.ParseTypeName`/`ParseStatement` instead of constructing `TupleTypeSyntax`/`TupleExpressionSyntax` nodes directly. Unlike #2-8/14, there is no internal-API wall here — public `SyntaxFactory` node constructors cover this directly. Concrete bug found, not just style: line ~112's `originalReturn.Replace("Task<", "").TrimEnd('>')` corrupts nested generics (`Task<List<int>>` → `List<int`). Full writeup and fix sketch: `docs/current/proposals/proposal_outparam_tuple_construction_syntaxfactory.md`. |
| 16 | ParamToPropertyRewriter (helper inside a param-to-property refactor) | GranularRefactoringEngine.cs:1186-1204 | Duplicate (avoidable), minor | keep | `VisitIdentifierName` does `SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node)` where `replacement` is a plain string from a `Dictionary<string,string>` substitution map — reparses a string for what's a simple identifier/member-access swap instead of building the replacement node directly (`SyntaxFactory.IdentifierName`/`SyntaxGenerator.MemberAccessExpression`). Low severity, no known correctness bug; not written up as a separate proposal. |
| 17 | LogLevel expression construction | ModernLoggingEngine.cs:72 | Duplicate (avoidable), cosmetic | keep | `SyntaxFactory.ParseExpression($"LogLevel.{levelStr}")` instead of `SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("LogLevel"), SyntaxFactory.IdentifierName(levelStr))`. Trivial one-liner; not written up separately. |

**Checked and confirmed clean** (proper tree-based `CSharpSyntaxRewriter` usage, no string-reparse
smell despite matching search terms): `AsyncBatchEngine.cs:1474` (`BridgeCallRewriter`),
`AdvancedLogicEngine.cs:603` (`IndexedAccessRewriter`), `SyntaxUpgradeEngine.cs:1001`
(`BracesRewriter`). No instances found of manual brace/offset span-math, reimplemented
`SymbolFinder`-style text search, or hand-built modifier lists duplicating `DeclarationModifiers`.

## Candidates not yet reviewed

MoveType, MoveAllTypesToFiles, ConvertAnonymousToNamed, WrapRange, Introduce (generate variants),
and the remaining tool surface (~90+ tools total — attribute/modifier/base-type/accessibility
mutators, scan/analysis tools, async-migration tools, etc.).

---

## Merged: Codebase Consistency & Duplication Audit

**Started**: 2026-08-25
**Purpose**: Audit RoslynSentinel for internally-duplicated or inconsistent patterns — places
where the same problem is solved multiple different ways across the codebase with no clear rule
for which to use. Distinct from `docs/current/findings/closed/finding_roslyn_duplication_audit.md`, which checks
hand-rolled logic against the *Roslyn API surface*; this review looks *inward*, comparing
RoslynSentinel's own code/tests against itself. Run per
`docs/current/plan-codebase-consistency-review-v1.md`.

### Method

Survey pass (breadth over depth) using seven parallel research passes, one per scope area in the
plan, each grounded in `GetDiRegistrations`/grep/direct file reads rather than guessing. No fixes
were made in this pass — findings only, same convention as the Roslyn duplication audit
(`keep`/`todo`/`fixed`/`removed` actions, executed later, not now).

### Legend

- **Action**: `keep` (working as-is, no consolidation needed) / `todo` (flagged for future
  consolidation or fix, not done here) / `fixed` (not applicable this pass — no fixes performed)
  / `removed` (not applicable this pass).
- **Severity** (informal, folded into Notes): grows worse over time (more copies accumulate) vs.
  cosmetic (annoying but stable).

### Findings

| # | Pattern | Locations | Finding | Action | Notes |
|---|---------|-----------|---------|--------|-------|
| 1 | Test-solution construction | `RoslynSentinel.Tests\TestSolutionBuilder.cs`; `RoslynSentinel.Tests.Asyncify\AsyncifyTestHelper.cs`; `RoslynSentinel.Tests\Fakes\FakeWorkspaceManager.cs`; `RoslynSentinel.Tests\TestSolutionFixture.cs`; `RoslynSentinel.Tests.Advanced\ComprehensiveToolTests.cs` (`CreateSolution`); `RoslynSentinel.Tests.Basic\NamespacePathMismatchTests.cs` (`CreateSolutionWithAbsolutePaths`); `Samples\ContosoOrders\ContosoOrders.Tests` | **Worse than the known seed example.** Seed example named 4 patterns; this pass found **7**. `TestSolutionBuilder` is the de facto standard (452 call sites / 82 files, consumed cross-project via `ProjectReference` from Tests.Basic/.Battery/.Advanced). `AsyncifyTestHelper` is a near-byte-identical fork used in exactly **1** file — its own project's other 3 test files bypass it and call `TestSolutionBuilder` instead, so the fork is nearly dead even where it lives. Two more inline `AdhocWorkspace` builders duplicate `TestSolutionBuilder`'s logic privately (one comment admits it mirrors `TestSolutionBuilder`'s path convention rather than reusing it). `ContosoOrders.Tests` is also the only xUnit project in an otherwise-NUnit test estate — a 5th sibling convention, orthogonal axis (test framework, not solution-construction). `FakeWorkspaceManager`/`TestSolutionFixture` are both actively used and each documents its own scope relative to the other (not a finding, an example of doing this well). | fixed | `AsyncifyTestHelper.cs` deleted; its sole caller (`SentinelAsyncifyToolsTests.cs`) now calls `TestSolutionBuilder.CreateSolutionWithProject` directly. `ComprehensiveToolTests.CreateSolution` and `NamespacePathMismatchTests.CreateSolutionWithAbsolutePaths` are now thin wrappers delegating to `TestSolutionBuilder`'s two overloads (no call-site changes needed in either file). `ContosoOrders.Tests`'s xUnit-vs-NUnit split is a separate axis (test framework, not solution-construction) and was left alone. |
| 2 | DI/service registration | `RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs`; `RoslynSentinel.Server.Advanced\ServiceRegistrationExtensionsAdvanced.cs` | **Clean — no true duplication.** Advanced correctly delegates to Basic (`AddRoslynSentinelEnginesAdvanced` calls `AddRoslynSentinelEnginesBasic` first; same for tools). No service type registered in both files. 100% `AddSingleton` (no lifetime mismatches). Minor style outliers: `IWorkspaceManager`/`ISolutionProvider`/`ILlmClient` registered via factory lambda (vs. one-liner generic elsewhere); `ToolGraph` registered as a pre-built instance; `LmStudioClient` is the only `AddHttpClient` call. None of these are inconsistencies worth fixing — they're the correct pattern for interface-forwarding/pre-built-instance cases. One real risk: Basic carries ~25 **commented-out** `AddSingleton<XEngine>()` lines that Advanced live-registers — not a duplicate today, but if someone uncomments Basic's copy without checking Advanced, it becomes one. | fixed | Added a `<remarks>` doc comment on `AddRoslynSentinelEnginesBasic` in `ServiceRegistrationExtensionsBasic.cs` pointing at Advanced's live registrations, so a future editor doesn't uncomment one of the ~25 commented-out lines blind. |
| 3 | Diff/line-comparison logic | `RoslynSentinel.Common\DiffEngine.cs` (`CreateDiff`, canonical); `RoslynSentinel.Basic\RefactoringEngine.cs:854` (`ComputeRenameHunks`); `RoslynSentinel.Basic\RefactoringEngine.cs:4648` (`ComputeFormatHunks`) | **Confirmed known finding, plus one new instance.** `ComputeRenameHunks` still duplicates `DiffEngine.CreateDiff` (per `finding_roslyn_duplication_audit.md` #1) and is algorithmically weaker — lockstep single-index walk with no resync on insertion/deletion, vs. `CreateDiff`'s partial mismatch-resync. **New**: `ComputeFormatHunks` (used by `FormatDocumentPreviewAsync`) is a *third* hand-rolled diff, splitting only on `'\n'` (not `\r\n`-aware, unlike `DiffEngine`), that additionally groups adjacent changed lines into merged ranges — a shape `CreateDiff` doesn't produce at all. `ValidationEngine`'s previously-flagged line-shift bug (see `CLOSED.md`'s closed-history entry) is already fixed and no longer computes its own line shifts — it now dedups diagnostics without line position and calls `DiffEngine.ApplyDiff` directly. `GitTools.cs` shells out to `git diff` and has no in-process diff logic. | todo | Three independent diff implementations now (was reported as one deferred finding). `ComputeFormatHunks`'s range-merging behavior isn't a drop-in match for `CreateDiff`'s per-line hunks — consolidating it would need a real adaptation, not just a call-site swap, unlike `ComputeRenameHunks` which is closer to a direct replacement. |
| 4 | Write/validate chokepoint | `RoslynSentinel.Common\PersistentWorkspaceManager.cs:910` (`ApplyProposedChangesAsync`, canonical); `RoslynSentinel.Common\ValidateAndApplyHelper.cs:18` (canonical wrapper); `RoslynSentinel.Advanced\OutParamRefactoringEngine.cs:285` (`ConvertOutParamsToValueTupleAsync`) | **One real regression-adjacent bypass found.** `OutParamRefactoringEngine.ConvertOutParamsToValueTupleAsync` calls `workspace.TryApplyChanges(updatedSolution)` directly on a branched/adhoc workspace obtained from `GetBranchedSolutionAsync`, never routing through `ApplyProposedChangesAsync`/`ValidateAndApplyAsync`. It's reachable live from the `ApplyClassCodemod` MCP tool's `convert_out_params_to_value_tuple` case (`SentinelCodemodTools.cs:471`) — unlike every sibling case in that switch, which returns `UpdatedText` for the caller to apply through the chokepoint. Loses drift detection, pre-image/undo capture, watcher-loop suppression, and rollback; raw `TryApplyChanges` on a branched workspace may not reliably persist to disk at all. Last touched 2026-08-22 in the same commit range as the write-path cleanup (`e008f1d`, ISolutionProvider migration) — it predates/escaped that pass rather than being a fresh regression, but it's live and uncorrected today. Everything else sampled (BulkComment, MsToolAugmentEngine's documented no-solution fallback, all other codemod switch cases) correctly funnels through the chokepoint. | todo | `docs/current/references/reference_code_file_write_paths.md` (the living reference this maps to) does **not** list this bypass — it should be added there once fixed, and the reference doc's "Callers that funnel through" table should get an `OutParamRefactoringEngine` row (currently absent) once corrected. |
| 5 | Error-mapping / result-shaping | `RoslynSentinel.Common\ToolException.cs` (`ToolErrorMapper`, canonical); `RoslynSentinel.Common\ToolResult.cs` (canonical envelope); ~14 tool classes across `RoslynSentinel.Server.Basic`/`.Advanced` | **Inconsistent — roughly 8 of 14 tool classes comply fully.** ~8 tool classes (`SentinelSymbolTools`, `SentinelScanTools`, `SentinelIntelligenceTools`, `SentinelQualityTools`, `SentinelModernizationTools`, `SentinelCommentingTools`, `SentinelAsyncifyTools` at its top-level boundary, etc.) call `ToolErrorMapper.ToResultError` consistently. 4 are **partial**: `SentinelAdvancedRefactoringTools`, `SentinelRefactoringTools`, `SentinelCodemodTools`, `SentinelGenerationTools` each have specific catch blocks that hand-build `new ResultError(ToolErrorCode.Exception, ...)` inline instead of calling the mapper, losing its `ToolException`-subtype/`SolutionNotLoaded` classification. 2 classes — `GitTools` and `DocumentationTools` — **don't use the shared envelope at all**, defining their own bespoke result types (`GitStatusResult`, `DocWriteResult`, etc.) with a flat string `Error` field. One method, `SentinelWorkspaceTools.Features`, returns a bare **anonymous object** on error instead of `ToolResult<T>` — breaks the envelope contract entirely. `LoadSolution`'s mapper bypass is the one defensible, documented exception (calling the mapper from `LoadSolution`'s own catch would circularly say "call LoadSolution first"). | todo | Matches the plan's suspicion exactly — "some tools hand-rolled their own try/catch-to-result shaping" confirmed with a rough headcount. `GitTools`/`DocumentationTools` are furthest from the norm (entirely separate envelope types, not just inline hand-rolling) and would need the most rework to migrate. |
| 6 | JSON serialization helpers | `RoslynSentinel.Common\OperationOutcome.cs` (`ItemRecordOutcome`, `ItemOutcome`, `OperationOutcome`); `RoslynSentinel.Common\FilePath.cs` (`FilePathJsonConverter`); ~19 independent `JsonSerializerOptions` construction sites across `RoslynSentinel.Common`/`.Server.Basic`/`.Server.Advanced` | **Known gotcha confirmed still live, plus broader pattern confirmed.** `ItemRecordOutcome` still lacks `[JsonConverter(typeof(JsonStringEnumConverter))]` even though its sibling enums `ItemOutcome`/`OperationOutcome` in the *same file* carry it — `OperationBlobWriter.PrettyJson` (its only options instance, no converters) serializes it as a raw int. No shared `JsonHelper`/canonical `JsonSerializerOptions` exists anywhere: at least 9 classes construct their own options object, each with different `WriteIndented`/case-sensitivity/naming-policy/converter settings (`LmStudioClient` is the sole camelCase outlier; several classes construct *two* different options objects for different call sites in the same file, e.g. `SentinelAsyncifyTools`, `SentinelConsoleMode`). Several call sites (`SentinelIntelligenceTools`, `DiagnosticReport`, `DocumentEditResult`, `FailureRouter`, `SentinelRefactoringTools`, `RefactoringEngine`, `CodeGenerationEngine`) call `JsonSerializer.Serialize` with no options at all (framework defaults). All usage is `System.Text.Json` — no `Newtonsoft.Json` in any production `.csproj`, so this is a single-library scattering problem, not a mixed-library one. `FilePathJsonConverter` is the one converter that IS applied consistently via attribute, making `ItemRecordOutcome`'s missing attribute look like an oversight rather than a pattern. | todo | Directly matches the plan's flagged gotcha. Fix is narrow and low-risk (add one attribute to `ItemRecordOutcome`) but the broader "~19 ad hoc options objects, no shared instance" pattern is a separate, larger consolidation candidate if ever prioritized. |
| 7 | `*Engine`/`*Library` construction | `RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs`; `RoslynSentinel.Server.Advanced\ServiceRegistrationExtensionsAdvanced.cs`; `RoslynSentinel.Basic\UniversalRefactoringLibrary.cs`; `RoslynSentinel.Advanced\ExhaustiveAnalyzerEngine.cs`, `MassiveAnalyzerEngine.cs`, `CodeSmellAndStyleEngine.cs` | **Construction pattern itself is clean; found 4 orphaned classes instead.** All ~56 registered `*Engine`/`*Library` classes (of 65 total found) are DI-singleton-registered and constructor-injected (`ISolutionProvider`/`IWorkspaceManager` + optional config/logger/other engines); zero `new Engine(...)` calls exist in production code — every hit is in a test file. No static-only engine classes. The concerning finding isn't inconsistent construction, it's that **4 fully-formed engine-shaped classes are registered nowhere and called from nowhere**: `UniversalRefactoringLibrary` (ctor `(ISolutionProvider)`, unlike sibling `SemanticRefactoringLibrary` which IS wired up), `ExhaustiveAnalyzerEngine`, `MassiveAnalyzerEngine`, `CodeSmellAndStyleEngine`. These look like dead code (built but never plugged in) or missing registrations (built, forgotten during wiring) — ambiguous which without author input. | fixed | Deleted as dead code, not wired up — deeper investigation (git history, reference search, redundancy check against registered siblings) found all 4 were superseded rather than merely forgotten: `UniversalRefactoringLibrary` is a self-admitted stub ("simulation mode") with zero test coverage; `ExhaustiveAnalyzerEngine` and `MassiveAnalyzerEngine` are byte-identical duplicates of each other and both redundant with the registered `DiagnosticEngine.GetFileDiagnosticsAsync`; `CodeSmellAndStyleEngine`'s one working method (`UseSwitchExpressionAsync`) duplicates the registered `SyntaxUpgradeEngine`'s switch-to-expression transform, and its other method (`ScanForSmellsAsync`) only actually returns results for 1 of its 3 documented rules. Removed the 4 production files and their Battery-suite unit tests (`BatteryFiveTests.cs`, `BatterySixteenTests.cs`, `BatterySeventeenTests.cs`, `BatteryTwentyNineTests.cs` — each had other, unrelated engine tests in the same file that were kept). Solution builds with 0 errors after removal. |

### Candidates not yet reviewed

- **Depth on findings #1 and #3-#7** — this was a survey pass; none of the seven findings above
  were fixed or consolidated, matching the plan's explicit scope limit.
- **The other ~90+ MCP tools** not sampled in the error-mapping pass (#5 sampled ~14 tool classes
  containing tool methods; there may be more tool classes not yet checked for envelope
  consistency).
- **`RoslynSentinel.Server.Basic.Http` / `.Advanced.Http`** — confirmed to contain no registration
  or tool logic of their own (thin executable shells only) during the DI pass, but not otherwise
  reviewed for other consistency questions in this pass.
- **Non-Engine "helper"/"library" classes** — the Engine-construction pass (#7) scoped to
  `*Engine`/`*Library`-named classes; other architectural roles (e.g. `*Helper`, `*Tools` classes
  that aren't `[McpServerToolType]`) weren't inventoried for construction-pattern consistency.
- **Samples\ContosoOrders** as a whole — only its test project's xUnit-vs-NUnit outlier status was
  noted (finding #1); the sample app's own code wasn't checked against the rest of the solution's
  conventions.
- **REPL/console duplication** — `SentinelConsoleMode.RunReplAsync`'s hand-rolled JSON-RPC driver
  vs. the real `McpClient` used by the MCP Tasks test harness is a known separate deferred-cleanup
  item, explicitly out of scope for this pass (not a "solve the same problem N ways" pattern
  *within* production code — it's a test-harness/console split) but worth cross-referencing since
  it's the same class of "which client implementation is canonical" question as finding #1.

---

## Merged: Plan: Codebase Consistency & Duplication Review

**Purpose**: audit RoslynSentinel for internally-duplicated or inconsistent patterns — places
where the same problem is solved multiple different ways across the codebase with no clear rule
for which to use. This is distinct from `docs/current/findings/closed/finding_roslyn_duplication_audit.md`, which
checks hand-rolled logic against the *Roslyn API surface*; this review looks *inward*, comparing
RoslynSentinel's own code/tests against itself.

**Known seed example** (already flagged, not yet fixed — see the project's
`project_deferred_cleanup_todos` memory): four different ways exist to get a solution into a test,
with no documented rule for which to use:
- `AsyncifyTestHelper` (in-memory `AdhocWorkspace`) in `RoslynSentinel.Tests.Asyncify`
- A near-duplicate `TestSolutionBuilder`
- `FakeWorkspaceManager` (`RoslynSentinel.Tests.Fakes`) — mostly-unimplemented `IWorkspaceManager`
  fake, throws `NotImplementedException` on `ApplyProposedChangesAsync`/`LoadSolutionAsync`
- Real `PersistentWorkspaceManager` + a temp-directory copy of `Samples/ContosoOrders`
  (`RoslynSentinel.Tests/TestSolutionFixture.cs`) — the pattern `SentinelAsyncifyToolsTests` and the
  newer MCP-tasks harness tests actually use in practice

Use this as the calibration example for the kind of finding this review should surface: not "this
code is wrong," but "this problem is solved N different ways and should be solved one way."

### Scope

The solution has ~15 projects (`RoslynSentinel.Basic/Advanced/Common`, `Server.Basic/Advanced`,
5+ test projects, `Server.Basic.Http`/`Server.Advanced.Http`, samples). A full line-by-line pass
isn't practical in one session — scope this as a **survey pass** (breadth over depth), producing
a prioritized findings list rather than fixes. Treat it the same way
`finding_roslyn_duplication_audit.md` treated its "candidates not yet reviewed" tail: flag for later
depth rather than silently skip.

Suggested areas to check, roughly in priority order:

1. **Test fixture/helper patterns** (the seed example above, generalized). Grep for
   `IWorkspaceManager`-implementing or solution-loading helper types across all `*.Tests*`
   projects; group by what problem each solves (in-memory solution construction, temp-disk solution
   construction, workspace-manager faking) and flag near-duplicates.
2. **DI/service-registration patterns**. `ServiceRegistrationExtensionsBasic.cs` and any
   `Server.Advanced`-side equivalent — check for copy-pasted registration blocks vs. shared
   extension methods, and whether `Advanced` re-registers anything `Basic` already provides (see
   `project_advanced_extends_basic` memory: Advanced project-references Basic, so duplication here
   would be redundant, not just inconsistent).
3. **Diff/line-comparison logic**. The existing Roslyn audit already flagged (finding #1,
   RenameSymbol) that `RefactoringEngine.ComputeRenameHunks` duplicates line-diff logic already in
   `RoslynSentinel.Common.DiffEngine.CreateDiff`, deferred rather than fixed. Check for other
   hand-rolled diff/line-shift logic that should be routing through `DiffEngine`.
4. **Validation/apply chokepoints**. `project_write_path_chokepoint_unified` memory says all `.cs`
   writes now route through `ApplyProposedChangesAsync` and `ValidateAndApplyAsync` was deduped
   into `Common` — confirm no tool has since regressed by adding its own bespoke write/validate
   path instead of using the shared one.
5. **Error-mapping / result-shaping patterns**. Check whether `ToolResult`/error-to-success mapping
   (`ToolErrorMapper`, per `project_operation_blob_json_gotchas` memory) is applied consistently
   across tool families, or whether some tools hand-rolled their own try/catch-to-result shaping.
6. **JSON serialization helpers**. Look for repeated inline `JsonSerializer.Serialize`/custom
   converter logic across tool classes that could be a single shared helper — especially anything
   working around the `ItemRecordOutcome`-has-no-string-converter gotcha already documented.
7. **Constructor/DI wiring for engine classes**. Given how many `*Engine` classes exist
   (`AsyncifyTestHelper`'s notes list a couple dozen), check whether they're constructed via a
   consistent pattern (DI-resolved vs. `new`'d inline in tool methods vs. static) — inconsistency
   here tends to predict future testability pain, similar to the `IWorkspaceManager` segregation
   work already done.

### Suggested process for the fresh session/agent

1. Load context: point the agent at this file, `docs/current/findings/closed/finding_roslyn_duplication_audit.md` (so
   it doesn't re-tread that axis), and `docs/current/project_deferred_cleanup_todos.md` (the seed
   example).
2. Use `GetCodeInventory`/`GetSolutionMetrics`/`GetDiRegistrations` (RoslynSentinel's own MCP tools
   — dogfood them per the project's own "use RoslynSentinel tools first" convention) to get an
   inventory before grepping manually.
3. For each scope area above, produce a table row per finding: **Pattern**, **Locations** (files),
   **Divergence** (what's actually different between the copies — signature, behavior, staleness),
   **Suggested canonical version**, **Risk if unconsolidated** (grows worse over time vs. cosmetic).
4. Do **not** perform the consolidation in this pass — this is a survey/audit producing a
   prioritized backlog, mirroring how `finding_roslyn_duplication_audit.md` was run (findings tracked
   with `keep`/`todo`/`fixed`/`removed` actions, not all executed immediately).
5. Write findings to a new `docs/current/codebase-consistency-audit-v1.md` using the same
   table format as `finding_roslyn_duplication_audit.md` (# / Pattern / Locations / Finding / Action /
   Notes) so the two audits read consistently.
6. End with an explicit "candidates not yet reviewed" section for anything scoped out, same
   convention as the existing audit.

### Explicitly out of scope for this pass

- Actually consolidating the Asyncify test fixtures (that's the separate, already-scoped
  `project_deferred_cleanup_todos` session item #2 — this review's job is to confirm/expand that
  finding, not execute the fix).
- Re-auditing anything `finding_roslyn_duplication_audit.md` already covered (findings #1-14) unless
  new evidence contradicts a prior finding.
- Fixing anything found — file it as a finding with a suggested action, don't touch code.

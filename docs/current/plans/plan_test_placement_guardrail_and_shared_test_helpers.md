# Plan: guard test placement (core-subject tests out of Advanced test projects) and share the test helpers that keep being copied

**Status:** IN PROGRESS 2026-11-23: Steps 1-6 DONE (punchlist #29, commits below); Steps 7-7.5 (relocation) and 8-10 not started. MoveFile is live, so Steps 7 and 9 are no longer blocked.

Step status (2026-11-23): Step 1 DONE (0aa661aa; baseline 63 classes: 49 `Tests.Basic`, 14 `Tests.Tools.Basic`; 23 of them are class-level finds in 12 `Tests.Battery.Advanced` files, the preliminary file-level census had none there; `BugFixTests`, `BugHuntSevenTests`, `SentinelAccuracyTests`, `ConstructionSiteFindingsTests` classes are also new to the list). Step 2 DONE except `TestInstanceLauncherTests` (2a 2c3fce5f, 2b 76b0878d, 2c 51a804ea, 2d 96b4c624; four walk-ups with their own miss behaviour carry `// TODO`, marker commit e94af4f1; hashes abbreviated). Step 3 DONE (3a db4711f6, 3b 369a64f1, 3c dc47e396; only 5 `Src` and 1 `WithStrayCrlf` copies equaled the kit, the others differ and stay). Step 4 DONE (4a 71146e5e, 4b e5da9c26). Step 5 DONE (5a 01324c10: 275 pins in 87 files by the syntax scan; 5b 97adc445: five classes exempt, 197 pins left). Step 6 DONE (6a b095bac7, 6b 15154bcc: 266 Tests.Tools.Advanced tests in the 16 call-site classes green, wiring equivalent; 6c ff2ac1fc).

## Problem

Changes to core code are expensive partly because of where tests live and how they are written. Between commit
38d37dbf (2026-10-19) and HEAD the four Advanced test projects (`RoslynSentinel.Tests.Advanced`,
`RoslynSentinel.Tests.Tools.Advanced`, `RoslynSentinel.Tests.Battery.Advanced`, `RoslynSentinel.Tests.Asyncify`)
changed in 165 files (`Git diff --stat 38d37dbf..HEAD`, verified; the 25,801-line figure is from the 2026-11-21
review and was not re-measured). Construction coupling is already fixed (factory rollout, 56865274, 2026-10-28).
What is left, verified against source on 2026-11-21:

1. **No placement guardrail.** Tests whose subject is `Common` / `Engines.Basic` / `Tools.Basic` keep being added to
   Advanced test projects (for example the nine `ChangeSignature*Tests` files in `Tests.Advanced`). A text census
   (rule below, file level, preliminary) finds 30 such files, about 506 KB of the 2,859 KB in the four projects
   (17.7%): 16 in `Tests.Advanced`, 13 in `Tests.Tools.Advanced`, 1 in `Tests.Asyncify`, none in `Tests.Battery.Advanced`.
   These are the files that would be stranded if unused Advanced tools are frozen out of the main solution.
2. **Duplicated helpers.**
   - `Src(bool crlf, params string[] lines)`: 15 copies with byte-identical bodies (12 in `Tests.Advanced`, 3 in
     `Tests.Basic`; one `Search` for the body line returned 15 matches in 15 files). `WithStrayCrlf`: 12 copies in two
     signatures, `(lfText, anchor)` and `(lfText)` with a hard-coded anchor (10 in `Tests.Advanced`, 2 in `Tests.Basic`).
     `RoslynSentinel.Tests/Support/Sources.cs` has `Lf`/`Crlf` and sample sources but no `Src`.
   - Repo-root lookup: 16 hand-written walk-ups for `RoslynSentinel.slnx` in 16 files (`Tests.Basic` 10,
     `Tests.Server` 2, `Tests.Tools.Basic` 2, `Tests.PlanStepRunner` 1, `Tests.Tools.Advanced` 1). A shared
     `RepoRootLocator.Find(string? startDirectory = null)` already exists in `RoslynSentinel.Common/RepoRootLocator.cs`
     (public) and two test files use it (`TestSolutionFixture.cs:259`, `RunStampTests.cs:52`).
   - Two construction factories: `Tests.Support.Basic/TestTools.cs` (+ `TestEngines.cs`) and the local
     `Tests.Tools.Advanced/BatteryToolFactory.cs` (225 `BatteryToolFactory.Create*` call lines: 136 build
     `Tools.Basic` classes, 89 build `Tools.Advanced` classes). `TestTools.cs` line 15 carries
     `// TODO The other tool classes (DocumentationTools, ...) have no factory yet`.
3. **Assertions on message wording.** A single-line scan (`Assert.That(<expr naming Message|Hint|Detail|Error|Reason|Next|Status...>, Does.Contain("<35+ chars>"))`)
   finds 523 sites in 135 files: 104 in 32 files in the Advanced projects, 419 in core projects. Typical
   behaviour tests that pin wording: `BugFixTests.cs:1411`, `:1455`, `:1958`, `:2246`, `:2511`;
   `ChangeSignaturePrimaryConstructorTests.cs:166`. There is no shared assertion helper for "error code first,
   then a short fragment" in `Tests.Support.*` or `RoslynSentinel.Tests`. The wording sweeps cost re-pins (about 29 and 26
   Advanced-test commits by subject for the not-found and typed-exception migrations, per the review; not re-measured).
4. **A `Tests.Advanced` reference that is only needed by four files.** `Tests.Advanced.csproj` references
   `Server.Advanced`; only `DependencyInjectionTests`, `ErrorPolicySymbolNotFoundPipelineTests`,
   `McpTasksHarnessTests` and `McpTasksHarnessBulkCommentTests` use it (plus the two support files they share,
   `FakeLlmClient.cs` and `McpTasksHarnessTestSupport.cs`). `Tests.Asyncify.csproj` references `Server.Advanced` with no
   textual use (only a comment in `AssemblyInfo.cs`); hypothesis until a Build with the reference removed.

Related: [finding_core_change_cost_causes_and_non_split_remedies.md](../findings/finding_core_change_cost_causes_and_non_split_remedies.md),
[proposal_move_file_tool.md](../proposals/proposal_move_file_tool.md),
[proposal_usage_based_core_split.md](../proposals/proposal_usage_based_core_split.md) (decisions of 2026-11-10, lines 297-311),
TODO.md "Core split test-move remainder" (BLOCKED on MoveFile slice 1).

## Decision

1. A ratchet (Step 1) fails when a test class in an Advanced test project names no symbol of the Advanced production
   projects; its baseline is the census, so it is green on day one and the list only shrinks. The baseline also carries each
   class's destination project, so the relocation slices are derived from it.
2. The shared helpers go in `RoslynSentinel.Tests/Support/` (every test project that needs them references
   `RoslynSentinel.Tests`: Advanced, Tools.Advanced, Battery.Advanced, Asyncify, Basic, Tools.Basic, Battery.Basic, Server;
   `Tests.SubAgent` and `Tests.ErrorVariants` do not and do not need them): `FormatKit` (`Src`, `WithStrayCrlf`, a
   byte-exact assertion, a whitespace-insensitive assertion) and `ErrorAssert` (error code first, then a short fragment).
3. A second ratchet (Step 5) counts long-literal message pins per file, baselined, so the count only shrinks; migration
   is "on touch" plus the hottest files, with an exemption category for classes whose subject is the wording itself.
4. `TestTools` gets the missing Basic tool factories first (Step 6); `BatteryToolFactory`'s Basic methods become
   delegates to it, which verifies the new wiring on 136 existing call sites before any file moves.
5. Whole-file relocation needs a tool that moves bytes without passing them through the model. None exists, so Step 7
   is a TOOL-GAP and waits for MoveFile slice 1; everything that makes a move zero-edit is done first.
6. Order is text-neutral and core-value first; steps that only matter while Advanced tests stay in the main solution
   come last (Steps 9 and 10). See "If unused Advanced tools are frozen" under Risks.

## Execution rules

- One `Build` / `RunTest` at a time (they share `bin/`/`obj/`). Implementers never run tests; the dispatcher runs the named
  tests. Compare results with `docs/known-failing-tests.*.txt` (baseline is 0 failures; any failure is new).
- At most two implementing agents at once, on disjoint files. Slice limits from `.claude/implementer-tiers.json` (checked
  2026-11-21): depth tier 1 (3 files, 15 edits, 1 acceptance, 35 tool calls); sweep tier 0 (1 operation, 8 files,
  25 call sites, 2 projects, 30 tool calls). A slice over the limit is split or goes to `implementer-senior`.
- C# edits through the MCP tools only (`Member`, `ReplaceSnippet`, `CreateFile`, ...); new test files and baselines in
  per-step files. Every moved or edited `.cs` keeps uniform CRLF.
- Text-neutral steps (new members, new tests, consumer fixes) run before behaviour-changing ones. No step here changes wire
  text or tool output; every step is text-neutral.
- A deliberate deviation from a codebase pattern gets a code comment (the transitional facade in Step 6c).
- No contract skeleton: no step adds mutually-referencing new types (each new file is one small static class whose bodies
  are written in the same call), so the `NotImplementedException` sweep of the plan-writer skill does not apply.

## Steps

### Step 1 - Placement ratchet and census baseline
- Files: `RoslynSentinel.Tests.Basic/TestPlacementRatchetTests.cs` (new), `docs/known-misplaced-core-tests.txt` (new,
  written by the test).
- Depends on: none. Pairs with: Step 2a.
- Effect: text-neutral (a new test and a baseline file).
- Lane: `test-writer` (new test; the closest template is `RoslynSentinel.Tests.Basic/AppliedChangeSummaryConstructionRatchetTests.cs`
  for scanning, and `RoslynSentinel.Tests.Server/ToolHintRatchetTests.cs` for the `docs/known-*.txt` baseline with an
  update environment variable and NEW / STALE reporting). Escalate to `test-writer-senior` if the rule needs more than syntax parsing.
- Change: one `[TestFixture]`, `[Category("Ratchet")]`, `[Category("TestPlacementRatchet")]` class in `Tests.Basic`
  (it only reads files, so it needs no reference to the Advanced projects). Rule:
  - Advanced symbol set `A`: every type name declared (class, struct, record, interface, enum, delegate, nested too)
    under `RoslynSentinel.Engines.Advanced`, `RoslynSentinel.Tools.Advanced`, `RoslynSentinel.Server.Advanced` and
    `RoslynSentinel.Tests.Support.Advanced` (skip `bin`, `obj`, `Worktree`), plus the `Create*` method names in
    `RoslynSentinel.Tests.Tools.Advanced/BatteryToolFactory.cs` whose return type is in `A` (derived, not hard-coded:
    today `CreateAdvancedRefactoringTools`, `CreateGenerationTools`, `CreateModernizationTools`, `CreateQualityTools`,
    `CreateIntelligenceTools`, `CreateScanTools`).
  - Scanned: `*.cs` under `RoslynSentinel.Tests.Advanced`, `RoslynSentinel.Tests.Tools.Advanced`,
    `RoslynSentinel.Tests.Battery.Advanced`, `RoslynSentinel.Tests.Asyncify` (same skips). A test class is a class that has
    `[TestFixture]` or a method with `[Test]`, `[TestCase]`, `[TestCaseSource]` or `[Theory]`.
  - A class is core-subject iff none of its identifier tokens (identifiers only, so `[Category("AdvancedRefactoringTools")]`
    strings, comments and cref text never count) is in `A`. Known limit: a class that inherits an Advanced-subject
    fixture base declared in the same test project, or that calls an Advanced extension method without naming its class,
    is counted core-subject; the census review in "Done when" catches it.
  - Destination column: a class that names an identifier ending `Tools` or `Impl`, or `BatteryToolFactory` / `TestTools`,
    maps to `Tests.Tools.Basic`; every other core-subject class maps to `Tests.Basic`.
  - Baseline `docs/known-misplaced-core-tests.txt`: header comments, then one sorted (ordinal) line per class:
    `<project>/<relative path>::<ClassName> | <destination project>`. Update with
    `ROSLYNSENTINEL_UPDATE_PLACEMENT_BASELINE=1` (the test writes and returns). Normal run: a class not in the baseline
    is `NEW core-subject test class ... place it in Tests.Basic (engine subject) or Tests.Tools.Basic (tool subject)`; a
    baseline line with no matching class is `STALE baseline line ... remove it (the ratchet only shrinks)`.
  - Tests: `AdvancedTestProjects_HoldNoNewCoreSubjectClasses` (the baseline comparison) and `Classify_*` self-tests on
    inline source text (an Advanced-named type is flagged Advanced; a class naming only `Engines.Basic` types is
    core-subject; a string and a comment naming an Advanced type do not count; a class with no test attribute is skipped).
  - First run with the update variable set, review, commit the baseline with the test.
- Done when: `Build` 0 errors; `TestPlacementRatchetTests` passes (dispatcher runs it, normal mode); the baseline lists,
  at minimum, every class in these files, which this plan verified to name no Advanced symbol: `ChangeSignature*Tests`
  (9 files in `Tests.Advanced`), `BasicRefactoringEngineFormatCharacterizationTests`,
  `MsToolAugmentEngineFormatCharacterizationTests`, `RefactoringEngineFormatCharacterizationTests`,
  `MoveMemberSuccessTests`, `MoveMemberToolCollateralRefusalTests`, `MemberRemoveTests`, `MemberAddReplaceViewTests`;
  and lists none of `ApiGenerationEngineTests`, `BatteryTwentyTwoTests`, `AsyncBatchEngineFlagOutcomeTests`. The step report
  gives the line count and the per-destination counts, and flags any class whose file is not in the preliminary list
  below (a difference means the rule or the list is wrong; do not edit the baseline by hand to hide it).
- Outcome classes: green (baseline matches) / NEW (a class added outside the baseline) / STALE (a baseline line is gone).

Preliminary census (file level, from a name-token rule using file names as type names, plus the factory names; the Step 1
baseline replaces it and is class level):

| Destination | Files (KB) |
| --- | --- |
| `Tests.Basic` (17 files, 306 KB) | from `Tests.Advanced`: `BasicRefactoringEngineFormatCharacterizationTests` (14.8), `ChangeSignatureConstructorWrapperTests` (16.7), `ChangeSignatureExtensionCallTests` (7.0), `ChangeSignatureLayoutTests` (31.3), `ChangeSignatureNonConstantDefaultTests` (12.5), `ChangeSignatureParamsTests` (13.3), `ChangeSignaturePrimaryConstructorTests` (15.8), `ChangeSignatureReflectionConstructionTests` (6.3), `ChangeSignatureRemovedParameterUseTests` (16.3), `ChangeSignatureWrapperTests` (42.0), `GenericContainerNameTests` (39.7), `IntelligenceTests` (3.2), `MassiveStructuralTests` (2.5), `MsToolAugmentEngineFormatCharacterizationTests` (22.9), `NamespacePathMismatchTests` (18.7), `RefactoringEngineFormatCharacterizationTests` (24.3); from `Tests.Asyncify`: `OperationOutcomeRoutingTests` (12.0) |
| `Tests.Tools.Basic` (13 files, 207 KB) | from `Tests.Tools.Advanced`: `DeclarationModifierTests` (7.9), `ExtractLocalVariableSnippetMissTests` (3.3), `ImpactMissAdvancedTests` (7.3), `MemberAddReplaceViewTests` (14.7), `MemberRemoveTests` (38.3), `MethodSignatureCallSiteTests` (11.0), `MoveMemberSuccessTests` (49.1), `MoveMemberToolCollateralRefusalTests` (27.4), `RenameNamespaceTests` (7.5), `RenameSymbolTests` (5.3), `StructuralRefinementMissAdvancedTests` (12.9), `SyncTypeAndFilenameTests` (5.5), `UsingDocsTypedMemberTests` (16.7) |

No class name in this list exists in a core test project (one `Search` over `Tests`, `Tests.Basic`, `Tests.Tools.Basic`,
`Tests.Battery.Basic`, `Tests.Server`: only `ChangeSignatureCallBindingTests` and `ChangeSignatureDescriptionAndRefusalTests`,
which are different names). Ten of the 13 `Tests.Tools.Advanced` candidates import `RoslynSentinel.Tools.Advanced`
but name no type from it (the Basic tool classes are in namespace `RoslynSentinel.Tools.Basic`), which is why the rule uses
declared type names and not `using` lines. The brief's example `MoveMemberToolCollateralRefusalTests` builds
its subject with direct `new CoreRefactoringTools(` and test-local `NormalizingMemberRefactoringEngine` /
`PendingLedgerRefactoringEngine` subclasses (lines 391 and 433), so it moves with its file.

### Step 2 - One repo-root lookup (4 slices)
- Files: slice 2a `Tests.Basic`: `AppliedChangeSummaryConstructionRatchetTests.cs`, `BareFileWriteRatchetTests.cs`,
  `EolAuditRatchetTests.cs`, `ErrorPolicyKindRatchetTests.cs`, `FirstMatchRatchetTests.cs`. Slice 2b `Tests.Basic`:
  `NewlineLiteralRatchetTests.cs`, `PathComparisonRatchetTests.cs`, `StaleReadPolicyRatchetTests.cs`,
  `WhitespaceRatchetTests.cs`, `DocumentEditResultInitializerScanTests.cs` (its walk-up is a local function
  `FindRepoRoot` inside the test method, line 19). Slice 2c: `Tests.Server/ToolHintRatchetTests.cs` (line 394),
  `Tests.Server/ToolReturnShapeRatchetTests.cs` (line 273), `Tests.Tools.Basic/GitCommitRoutePinRatchetTests.cs`
  (line 31), `Tests.Tools.Basic/TestInstanceLauncherTests.cs` (line 551, a `for` loop variant). Slice 2d:
  `Tests.PlanStepRunner/PlanStepRunnerSurfaceTests.cs` (`FindRepoRoot`, line 142, returns null when absent).
- Depends on: none (2a pairs with Step 1; 2b with Step 3a).
- Effect: text-neutral. The walk-up from `AppContext.BaseDirectory` is kept; only the exception type on a miss changes
  (`DirectoryNotFoundException` from the locator instead of `InvalidOperationException`).
- Lane: `implementer-sweep`, tier 0 (one operation, 4-5 files, 4-5 sites per slice; 2c touches 2 projects).
- Change: in each file replace the body of the existing method with a delegation and keep the method name, so no call
  site changes: `private static string RepoRoot() => RepoRootLocator.Find(AppContext.BaseDirectory);`, using
  `Member(operation: replace, memberName: ...)` per file (the local function in `DocumentEditResultInitializerScanTests`
  is edited with `ReplaceSnippet`). `RepoRootLocator.Find(startDirectory)` ignores the `ROSLYNSENTINEL_REPO_ROOT`
  variable when a start directory is given (`RepoRootLocator.cs` doc comment), which is the existing behaviour of the
  copies; do not call the zero-argument form. Read each body first: a body that does more than find the `.slnx` stays and
  is named in the report. `RepoRootLocator` is in `RoslynSentinel.Common`, a global using in `Tests.Basic`, `Tests.Server`
  and `Tests.Tools.Basic`; 2d adds `using RoslynSentinel.Common;` and keeps its null-on-miss contract with
  `RepoRootLocator.TryFind(AppContext.BaseDirectory)`.
- Done when: `Build` 0 errors for each slice; `Search(mode: text, query: "new DirectoryInfo\(AppContext\.BaseDirectory\)")`
  over `RoslynSentinel.Tests*` lists only files not yet reached by a finished slice (final count 0 after 2c, and the
  `BlobIntegrityInvariantTests` copy that Step 10c handles). Dispatcher runs the touched ratchet classes
  (`AppliedChangeSummaryConstructionRatchetTests`, `BareFileWriteRatchetTests`, `EolAuditRatchetTests`,
  `ErrorPolicyKindRatchetTests`, `FirstMatchRatchetTests`, `NewlineLiteralRatchetTests`, `PathComparisonRatchetTests`,
  `StaleReadPolicyRatchetTests`, `WhitespaceRatchetTests`, `DocumentEditResultInitializerScanTests`, `ToolHintRatchetTests`,
  `ToolReturnShapeRatchetTests`, `GitCommitRoutePinRatchetTests`, `TestInstanceLauncherTests`,
  `PlanStepRunnerSurfaceTests`) and expects the same results as before.

### Step 3 - Shared formatting kit
#### Step 3a - FormatKit
- Files: `RoslynSentinel.Tests/Support/FormatKit.cs` (new; not in `Sources.cs`, which is append-only sample sources).
- Depends on: none. Pairs with: Step 2b.
- Effect: text-neutral (new static class, no callers yet).
- Lane: `implementer`, tier 1 (1 new file, written with one `CreateFile` call).
- Change: `namespace RoslynSentinel.Tests.Support; public static class FormatKit` with
  - `Src(bool crlf, params string[] lines)`: the body shared by the 15 copies (`string.Join("\n", lines)`, then
    `crlf ? text.Replace("\n", "\r\n") : text`).
  - `WithStrayCrlf(string lfText, string anchorLine)`: the body of the copy in
    `BasicRefactoringEngineFormatCharacterizationTests.cs:49` (precondition asserts that the anchor line is present and
    exactly one `\r` results).
  - `AssertByteExact(string actual, string expected, string? because = null)`: `Assert.That(actual, Is.EqualTo(expected))`
    with a failure message that renders both texts with `\r`, `\n` and tab made visible, so an EOL mismatch is readable.
    For tests whose subject is formatting or line endings.
  - `AssertSameIgnoringWhitespace(string actual, string expected, string? because = null)`: equal after every run of
    whitespace (spaces, tabs, CR, LF) collapses to one space and the ends are trimmed. For every other test that compares
    a whole output text.
- Done when: `Build` 0 errors; `Search(mode: symbol, query: "FormatKit")` finds the class in `RoslynSentinel.Tests`.

#### Step 3b - FormatKit tests
- Files: `RoslynSentinel.Tests/FormatKitTests.cs` (new).
- Depends on: Step 3a. Pairs with: Step 4a.
- Effect: text-neutral.
- Lane: `test-writer`.
- Change: `Src` LF and CRLF output; `WithStrayCrlf` yields exactly one `\r` and fails its precondition for a missing
  anchor; `AssertByteExact` fails for CRLF versus LF and its message contains the visible `\r\n`;
  `AssertSameIgnoringWhitespace` passes for different indentation and line breaks and fails for a different token.
- Done when: `Build` 0 errors; dispatcher runs `FormatKitTests` (all pass).

#### Step 3c - Replace the copies that belong to core-subject files
- Files: `Tests.Basic`: `AddUsingDirectiveFormatCharacterizationTests.cs`, `RoslynFormattingHelperFormatSpansTests.cs`,
  `UseFieldBackedPropertiesFormatCharacterizationTests.cs`; `Tests.Advanced` (core-subject, they move in Step 7):
  `BasicRefactoringEngineFormatCharacterizationTests.cs`, `MsToolAugmentEngineFormatCharacterizationTests.cs`,
  `RefactoringEngineFormatCharacterizationTests.cs`.
- Depends on: Step 3a. Pairs with: Step 4b.
- Effect: text-neutral (the shared bodies equal the copies; characterization tests stay byte-exact).
- Lane: `implementer-sweep`, tier 0 (one operation, 6 files, about 11 declarations, 2 projects).
- Change: per file delete the private `Src` (6 declarations) and add `using static RoslynSentinel.Tests.Support.FormatKit;`
  so the existing `Src(...)` calls bind to the kit. For `WithStrayCrlf`: the two-argument copies (`BasicRefactoringEngine...`,
  `MsToolAugment...`, `RefactoringEngine...`) are deleted the same way after reading that each body equals the kit's; the
  one-argument copies (`AddUsingDirective...`, `UseFieldBackedProperties...`) become a one-line local wrapper
  `private static string WithStrayCrlf(string lfText) => FormatKit.WithStrayCrlf(lfText, "<their hard-coded anchor>");`.
  A body that differs from the kit's stays and is named in the report.
- Done when: `Build` 0 errors; dispatcher runs the six classes and expects the same pass counts as before the slice.

### Step 4 - ErrorAssert
#### Step 4a - helper
- Files: `RoslynSentinel.Tests/Support/ErrorAssert.cs` (new).
- Depends on: none. Pairs with: Step 3b.
- Effect: text-neutral.
- Lane: `implementer`, tier 1 (1 new file).
- Change: `namespace RoslynSentinel.Tests.Support; public static class ErrorAssert` with
  `public static void Is(IOperationError? error, string expectedCode, string? distinguishingFragment = null)`.
  `IOperationError` (`RoslynSentinel.Common/IOperationError.cs`) has `ErrorCode` and `Message` and is implemented by
  `ToolException` (so every typed exception, `ToolNotFoundException`, `ToolAmbiguousMatchException`, ...) and by the wire
  `ResultError`. Order of checks: not null; `ErrorCode` equals `expectedCode` (use the `ToolErrorCode` constants, for
  example `ToolErrorCode.NotFound`, `.Ambiguous`, `.TargetIneligible`; the failure message names both codes); then, when a
  fragment is given, `Message` contains it (ordinal). The XML doc says: pin a short fragment that distinguishes the failure
  (a name, a number, a flag name), never a sentence of the production wording. `IOperationResult` has no code or message
  (`IOperationResult.cs`: only `Status` and `IsError`), so there is no overload for it.
- Done when: `Build` 0 errors.

#### Step 4b - helper tests
- Files: `RoslynSentinel.Tests/ErrorAssertTests.cs` (new).
- Depends on: Step 4a. Pairs with: Step 3c.
- Effect: text-neutral.
- Lane: `test-writer`.
- Change: passes for a `ToolNotFoundException` with the right code and fragment; fails with the code check first when the
  code differs even if the fragment matches (assert the failure message names the codes); fails on a missing fragment;
  null error fails; works for a `ResultError`.
- Done when: `Build` 0 errors; dispatcher runs `ErrorAssertTests` (all pass).

### Step 5 - Message-pin ratchet and exemptions
#### Step 5a - ratchet and baseline
- Files: `RoslynSentinel.Tests.Basic/MessageTextRatchetTests.cs` (new), `docs/known-long-message-pins.txt` (new, written by the test).
- Depends on: Step 4a (its failure text names `ErrorAssert`). Pairs with: Step 6a.
- Effect: text-neutral.
- Lane: `test-writer` (same templates as Step 1).
- Change: scan every `*.cs` under `RoslynSentinel.Tests*` (skip `bin`, `obj`, `Worktree`) with Roslyn syntax: an
  invocation `Assert.That(<arg0>, <constraint>)` where the last identifier of `<arg0>` is one of `Message`, `ErrorMessage`,
  `StatusMessage`, `Hint`, `Detail`, `Details`, `Reason`, `Next`, `Error` and the constraint chain contains
  `Does.Contain(<string or interpolated literal>)` (including `.And.Contain(...)`) whose literal text is 35 or more
  characters. Count one per literal. Skip any class or method carrying `[Category("MessageTextSubject")]`. Baseline
  `docs/known-long-message-pins.txt`, one sorted line per file name (not path, so a file move does not disturb it; counts for
  equal file names are summed): `<File.cs> | <count>`; update with `ROSLYNSENTINEL_UPDATE_LONG_MESSAGE_BASELINE=1`;
  `count > baseline` is `NEW long message pin ... use ErrorAssert.Is(error, ToolErrorCode.X, "<short fragment>")`;
  `count < baseline` is `STALE ... lower the line`. Self-tests on inline source (a 35-character literal counts, a 34-character
  literal does not, an exempt class is skipped, `Does.Contain` on a non-message expression is ignored).
  Measured with an equivalent single-line text scan on 2026-11-21: 523 sites in 135 files (`Tests.Tools.Basic` 231 / 61 files,
  `Tests` 89 / 14, `Tests.Basic` 69 / 15, `Tests.Tools.Advanced` 53 / 17, `Tests.Advanced` 42 / 10, `Tests.Server` 23 / 8,
  `Tests.Battery.Basic` 6 / 4, `Tests.Asyncify` 5 / 2, `Tests.Battery.Advanced` 4 / 3, `Tests.ErrorVariants` 1 / 1; 446 of
  the 523 are on `Message`). The ratchet's own count is the baseline; do not reconcile the two by hand.
- Done when: `Build` 0 errors; `MessageTextRatchetTests` passes (dispatcher runs it); the report gives the total and the 10
  files with the most pins.

#### Step 5b - exempt the classes whose subject is the wording
- Files: derived from the Step 5a baseline (slice map: files with 5 or more pins whose classes name hint, miss, not-found or
  formatter behaviour, for example `SnippetErrorFormatterTests` 30, `ContextHelperSelectorTests` 23,
  `MemberRefactoringNotFoundTests` 15, `DocCommentIdNotFoundCandidateTests` 11, `SymbolNavigationMissTests` 11,
  `MemberRefactoringMissClassEnumTests` 10, `SearchSolutionTextRecoveryHintTests` 10; the owner reviews the list, see decision 4).
- Depends on: Step 5a and the owner's answer to decision 4.
- Effect: text-neutral (an attribute only).
- Lane: `implementer-sweep`, tier 0 (one operation: add `[Category("MessageTextSubject")]` above the class declaration with
  `ReplaceSnippet`), at most 8 files per slice; after the slice the dispatcher regenerates the baseline with the update variable.
- Done when: `Build` 0 errors; `MessageTextRatchetTests` passes with the regenerated baseline; the baseline total dropped by
  the pins in the marked classes and by nothing else.

### Step 6 - Tool factories in TestTools, and the BatteryToolFactory wiring
#### Step 6a - missing Basic tool factories
- Files: `RoslynSentinel.Tests.Support.Basic/TestTools.cs`.
- Depends on: none. Pairs with: Step 5a.
- Effect: text-neutral (new factories, no callers yet).
- Lane: `implementer`, tier 1 (1 file, about 6 edits).
- Change: add short-form factories named after their class, with the same shape as the existing ones
  (`ActivatorUtilities.CreateInstance<T>(TestEngines.CreateProvider(workspaceManager), workspaceManager)`; the container is
  built from `AddRoslynSentinelEnginesBasic` + `AddRoslynSentinelToolImplsBasic` and shares the caller's manager):
  `RefactoringSignatureTools(IWorkspaceManager)`, `RefactoringExtractionDocsTools(IWorkspaceManager)`,
  `TypedMemberTools(IWorkspaceManager)` (over the existing short-form `RefactoringStructuralImpl(wm)`),
  `SymbolRelationshipTools(IWorkspaceManager)`, `SymbolNavigationTools(IWorkspaceManager)`. The first three wrap the
  existing `RefactoringSignatureImpl(wm)` / `RefactoringExtractionDocsImpl(wm)` / `RefactoringStructuralImpl(wm)` where the
  tool class takes the impl; the last two take their impl from the container. `RefactoringStructuralTools(wm)` and
  `CoreRefactoringTools(wm)` already exist. Remove the `// TODO The other tool classes ... have no factory yet` line only if
  every class `BatteryToolFactory` builds now has a factory here; otherwise reword it to name the classes that are left.
  All of these classes are in `RoslynSentinel.Tools.Basic` (`Search(mode: class)` verified).
- Done when: `Build` 0 errors; `Search(mode: method, containingType: TestTools)` lists the five new methods.

#### Step 6b - BatteryToolFactory delegates to TestTools (verifies the new wiring)
- Files: `RoslynSentinel.Tests.Tools.Advanced/BatteryToolFactory.cs`.
- Depends on: Step 6a. Pairs with: Step 5b.
- Effect: text-neutral if the container wiring matches the hand wiring; this step exists to prove that.
- Lane: `implementer`, tier 1 (1 file, 7 method bodies).
- Change: the Basic-returning methods (`CreateCoreRefactoringTools`, `CreateRefactoringExtractionDocsTools`,
  `CreateTypedMemberTools`, `CreateRefactoringStructuralTools`, `CreateRefactoringSignatureTools`,
  `CreateSymbolRelationshipTools`, `CreateSymbolNavigationTools`) each become one line calling the matching
  `Battery.Basic.TestTools` factory; the private `BuildEngines` tuple helper is deleted when nothing uses it. The six
  Advanced-returning methods already delegate to `Battery.Advanced.TestToolsAdvanced` and stay. The 8 direct
  `new *Impl(` / `new *Tools(` constructions inside the file disappear with the bodies.
- Done when: `Build` 0 errors; the dispatcher runs the whole `RoslynSentinel.Tests.Tools.Advanced` project (all 136 Basic
  factory call sites go through the new wiring) and expects no new failure against `docs/known-failing-tests.Advanced.txt`.
  A new failure means the container wiring differs (for example engine instance sharing): stop and report which class
  and test, do not patch the tests.
- Outcome classes: green (wiring equivalent) / red (container wiring differs; record in the step report and in Risks).

#### Step 6c - transitional facade in Tests.Tools.Basic
- Files: `RoslynSentinel.Tests.Tools.Basic/BatteryToolFactory.cs` (new).
- Depends on: Step 6a. Pairs with: Step 6b.
- Effect: text-neutral.
- Lane: `implementer`, tier 1 (1 new file).
- Change: `internal static class BatteryToolFactory` with only the 7 Basic-returning methods of Step 6b, each one line
  delegating to `Battery.Basic.TestTools`, declared in `namespace RoslynSentinel.Tests.Tools.Advanced;` (not the project's
  namespace) so the 13 files that Step 7 moves from `Tests.Tools.Advanced` need no call-site edit. A comment states this
  deviation, the reason (zero-edit moves, the alternative is a 118-site sweep, see decision 7) and
  `// TODO inline into TestTools calls and delete once MoveFile namespaceHandling update exists`.
  The class is internal in each assembly, so it does not clash with the one in `Tests.Tools.Advanced` (the two assemblies do
  not reference each other).
- Done when: `Build` 0 errors.

### Step 7 - Relocate the baselined classes (BLOCKED on MoveFile slice 1)
`TOOL-GAP:` task: move 30 whole test files between test projects (about 506 KB). Tool tried: none exists. `McpServerStatus`
(`toolListing: all`, filter "Move") lists `MoveAllTypesToFiles`, `MoveMember`, `MoveType`,
`PreviewMoveFileToNamespaceFolder`; `Git` has no `mv`; `MoveMember` is member-level and the proposal records that a cross-project
`MoveMember` refuses when shared fixtures stay behind; `Member(addMember)` + `Member(remove)` per member works for a mixed
file (it was done for 5 mixed files, commits 444e5a53, fdeba2d2, c8d2dbfc, 4beb7a2b) but is a rewrite, not a byte-exact
move. `ReadFile` + `CreateFile` + `DeleteFile` routes the whole text through model output (the failure mode
`proposal_move_file_tool.md` exists to remove). Measured: 30 files, 2 destination projects, 3 source projects. Suggested
change: build `MoveFile(sourcePath, destinationPath, namespaceHandling: keep)` (approved 2026-11-21, slice 1, no auto-staging,
cross-project allowed behind the compile gate); it is not built (no `MoveFile` symbol in source or in the tool listing).
The existing TODO.md entry (line 38, "Core split test-move remainder", BLOCKED) already tracks the two largest files.

- Depends on: MoveFile slice 1, and Steps 1, 3c, 6b, 6c (so a move needs no edit beyond the file's own path).
- Effect: text-neutral (tests change project; their code and namespace do not).
- Lane: `implementer-sweep`, tier 0, one operation (`MoveFile ... keep`) per batch, at most 8 files per batch. Batches (the
  Step 1 baseline is authoritative, these follow the preliminary list):
  - 7.1 `Tests.Advanced` -> `Tests.Basic`: `ChangeSignatureConstructorWrapperTests`, `...ExtensionCallTests`,
    `...LayoutTests`, `...NonConstantDefaultTests`, `...ParamsTests`.
  - 7.2 `Tests.Advanced` -> `Tests.Basic`: `ChangeSignaturePrimaryConstructorTests`, `...ReflectionConstructionTests`,
    `...RemovedParameterUseTests`, `...WrapperTests`.
  - 7.3 `Tests.Advanced` -> `Tests.Basic`: `BasicRefactoringEngineFormatCharacterizationTests`,
    `MsToolAugmentEngineFormatCharacterizationTests`, `RefactoringEngineFormatCharacterizationTests`,
    `GenericContainerNameTests`, `IntelligenceTests`, `MassiveStructuralTests`, `NamespacePathMismatchTests`.
  - 7.4 `Tests.Asyncify` -> `Tests.Basic`: `OperationOutcomeRoutingTests` (one file; a separate batch because it is a third
    source project).
  - 7.5 `Tests.Tools.Advanced` -> `Tests.Tools.Basic`: `MoveMemberSuccessTests`, `MoveMemberToolCollateralRefusalTests`,
    `MemberRemoveTests`, `MemberAddReplaceViewTests`, `UsingDocsTypedMemberTests`; remove the two
    `// TODO move this whole file ... to Tests.Tools.Basic` markers (`MoveMemberSuccessTests.cs:11`,
    `MoveMemberToolCollateralRefusalTests.cs:18`); the dispatcher closes the TODO.md line 38 entry.
  - 7.6 `Tests.Tools.Advanced` -> `Tests.Tools.Basic`: `DeclarationModifierTests`, `ExtractLocalVariableSnippetMissTests`,
    `ImpactMissAdvancedTests`, `MethodSignatureCallSiteTests`, `RenameNamespaceTests`, `RenameSymbolTests`,
    `StructuralRefinementMissAdvancedTests`, `SyncTypeAndFilenameTests`.
  After each batch the same slice removes the moved classes' lines from `docs/known-misplaced-core-tests.txt` (the ratchet
  reports them STALE otherwise) and regenerates nothing else (`docs/known-long-message-pins.txt` is keyed by file name and is
  untouched).
- Done when per batch: `Build` 0 errors with the compile gate clean; the dispatcher runs the moved classes in the destination
  project and expects the same pass count as in the source project before the move; `TestPlacementRatchetTests` is green;
  the Advanced projects still build. Whole step: the baseline file has no line left for a moved class, and a final
  `Search(mode: text, query: "BatteryToolFactory")` in `Tests.Tools.Advanced` finds no call to a Basic-returning method.
- Reported outcome classes per batch: moved and green / moved and red because of parallelism or shared state (see Risks) /
  refused by the compile gate (names the missing internal helper; fix by a `TestTools` factory, not by copying code).

### Step 8 - Delete the Basic half of BatteryToolFactory in Tests.Tools.Advanced
- Files: `RoslynSentinel.Tests.Tools.Advanced/BatteryToolFactory.cs`.
- Depends on: Step 7 (batches 7.5 and 7.6 done).
- Effect: text-neutral.
- Lane: `implementer`, tier 1 (1 file, 7 members).
- Change: `Search(mode: references)` on each Basic-returning method; delete those with 0 references. Methods still used by a
  mixed class that stayed (`BatteryTwentyFourTests` is mixed: it uses both Basic and Advanced factories) stay as delegates.
- Done when: `Build` 0 errors; the file has no member with 0 references.

### Step 9 - Move the `Server.Advanced`-dependent tests to Tests.Server (BLOCKED on MoveFile slice 1; matters only while Tests.Advanced stays in the main solution)
- Files: `Tests.Advanced/DependencyInjectionTests.cs`, `ErrorPolicySymbolNotFoundPipelineTests.cs`,
  `McpTasksHarnessTests.cs`, `McpTasksHarnessBulkCommentTests.cs`, `McpTasksHarnessTestSupport.cs`, `FakeLlmClient.cs`
  (to `RoslynSentinel.Tests.Server`, which already references `Server.Advanced`, `Tests` and `Tests.Support.Basic`);
  `RoslynSentinel.Tests.Advanced/RoslynSentinel.Tests.Advanced.csproj`; `RoslynSentinel.Tests.Asyncify/RoslynSentinel.Tests.Asyncify.csproj`.
- Depends on: MoveFile slice 1 (the same TOOL-GAP as Step 7, 6 files, 3 projects).
- Effect: text-neutral.
- Lane: `implementer-sweep` for the move (one operation, 6 files, 2 projects); a separate `implementer` slice for the csproj edits.
- Change: first `Search(mode: references)` for `FakeLlmClient` and `McpTasksHarnessTestSupport` in the rest of `Tests.Advanced`
  (none found by this plan's text search of the 4 files and 2 support files; confirm no other user). Move the six files, then
  remove the `Server.Advanced` `<ProjectReference>` from `Tests.Advanced.csproj`. `Tests.Asyncify.csproj` loses the same
  reference only if `Build` stays at 0 errors without it (unused by text; `Tools.Advanced` and `Engines.Advanced` come from its
  other references).
- Done when: `Build` 0 errors for the Solution flavor (`scripts/build.ps1 -Flavor Solution`); the dispatcher runs the four
  moved classes in `Tests.Server` (same pass counts as before); neither csproj references `Server.Advanced` (or, for
  `Tests.Asyncify`, the report says why it had to stay).

### Step 10 - Advanced-only leftovers (do last; wasted if the Advanced tools are frozen)
#### Step 10a/10b - remaining `Src` / `WithStrayCrlf` copies in Advanced-subject files
- Files: 10a `Tests.Advanced`: `AdvancedRefactoringNormalizeCharacterizationTests.cs`,
  `AdvancedStructuralFormatCharacterizationTests.cs`, `CodeGenerationEngineFormatCharacterizationTests.cs`,
  `ConvertExpressionBodyFormatCharacterizationTests.cs`, `IDEStyleSimplifyMemberAccessCharacterizationTests.cs`;
  10b `Tests.Advanced`: `InstrumentationEngineFormatCharacterizationTests.cs`,
  `MappingEngineInvertAssignmentsCharacterizationTests.cs`, `MoveTypeToOuterScopeFormatCharacterizationTests.cs`,
  `RemoveNodeCharacterizationTests.cs`.
- Depends on: Step 3a. Effect: text-neutral. Lane: `implementer-sweep`, tier 0 (one operation, 4-5 files, same procedure as 3c).
- Done when: `Build` 0 errors; the dispatcher runs the touched classes (same pass counts);
  `Search(mode: text, query: "static string Src\\(bool crlf")` finds no copy outside `FormatKit.cs`.

#### Step 10c - last repo-root copy
- Files: `Tests.Tools.Advanced/BlobIntegrityInvariantTests.cs` (`FindRepoRoot`, line 192).
- Depends on: none. Effect: text-neutral. Lane: `implementer`, tier 1. Same replacement as Step 2.
- Done when: `Build` 0 errors; `BlobIntegrityInvariantTests` passes; the Step 2 search finds no walk-up copy left.

#### Step 10d - migrate message pins in Advanced tests (slice map derived after Step 7)
- Files: the Advanced entries left in `docs/known-long-message-pins.txt` after Step 7 (the baseline had 104 pins in 32
  files before the moves; about 40 of them travel with the moved files, for example `GenericContainerNameTests` 15,
  `StructuralRefinementMissAdvancedTests` 8, the `ChangeSignature*` files; the remainder includes `BugFixTests` 10,
  `BatteryTwentyTwoTests` 8).
- Depends on: Steps 4a, 5a, 7. Effect: text-neutral (assertions get shorter, behaviour is unchanged).
- Lane: `implementer`, tier 1, at most 3 files and 15 sites per slice (each site needs a choice of fragment and, where an
  exception or `ResultError` exists, the code assertion `ErrorAssert.Is(error, ToolErrorCode.X, "<short fragment>")`;
  a site on a string with no code, such as `result.Error`, only gets a shorter fragment). Not a mechanical sweep.
- Done when per slice: `Build` 0 errors; the touched classes pass; the baseline is regenerated and each touched file's count
  is lower; `MessageTextRatchetTests` is green. Core-project migration follows the same recipe on touch (no slice is
  scheduled for the 419 core pins; see decision 4).

## Out of scope

- Result-shape assertion helpers (the `DocumentChangeSet` retype cost one test line), approval or snapshot tests, a new
  construction host: the source does not argue for them.
- Deleting the `BatteryToolFactory` facade for Advanced tools: after Steps 6b and 8 its six Advanced methods are one-line
  delegates to `TestToolsAdvanced`; removing them means rewriting 89 call lines for no change in behaviour or coupling.
- Folding the Advanced half of `BatteryToolFactory` into `TestToolsAdvanced` as a code move: nothing is left to fold; the
  methods already delegate.
- The private `SetFiles` / `SetSource(s)` helpers (143 copies in Advanced tests, 30 in core per the review): each binds a
  different fixture field and the signatures differ; no shared signature is proposed. Revisit after Step 7 if a pattern appears.
- A method-level split of mixed classes (for example `BatteryTwentyFourTests`, `BasicRefactoringNameMissTests`, which use both
  Basic and Advanced subjects): the Step 1 baseline is class level. A mixed class that is later split is a `Member(addMember)`
  + `Member(remove)` job, as in the 2026-11-10 decisions.
- `replaceConstruction` rewrites of the 30 direct `new *Engine/*Tools/*Impl(` sites left in Advanced tests (8 of them go away
  with Step 6b): construction coupling is already fixed and the factories exist.
- MoveFile itself, including `namespaceHandling: update`: `proposal_move_file_tool.md`.

## Risks and open decisions

Open decisions for the owner (each has a recommendation):

1. **Relocation route.** Options: (a) wait for MoveFile slice 1 and run Step 7 then (recommended: it is the only route that
   keeps bytes out of model output, and Steps 1-6 and 10 are useful meanwhile); (b) interim `ReadFile` + `CreateFile` +
   `DeleteFile` for the small files (not recommended: re-types 30 files, 506 KB, which is what the MoveFile proposal
   rejects); (c) make MoveFile part of this effort (not recommended: it has its own approved proposal and open design questions).
2. **Census rule.** Options: (a) syntactic, class level, Advanced declared type names (recommended: no compilation needed, same
   style as the existing ratchets, catches the `BatteryToolFactory` cases the brief's file-level heuristic missed, ignores
   category strings); (b) semantic, by compiling each test project and checking referenced assemblies (exact, but a test
   would need an MSBuild or Roslyn workspace load; slow and fragile); (c) file level (simpler, but a mixed file hides a core
   class and `[Category("AdvancedRefactoringTools")]` strings make a Basic file look Advanced: `MoveMemberSuccessTests.cs:59`
   carries that category while its 31 factory calls are `CreateCoreRefactoringTools`).
3. **Namespaces of moved files.** Options: (a) keep them for the move (recommended: zero-edit moves, no compile-gate noise)
   and fix later with MoveFile `update` or a per-file namespace edit; (b) edit the namespace line in a second pass (about 30
   one-line edits in 4 sweep slices; the 13 `Tests.Tools.Basic` files cannot all change in one slice while the facade of Step 6c
   keeps the old namespace, so it needs the facade changed in the same slice or the 118-site sweep of decision 7).
4. **Message ratchet.** (i) Length floor 35 characters (recommended: it spares short fragments like `"'x'"` and still pins
   whole sentences; 34 versus 35 is arbitrary, raise it if the baseline is too noisy). (ii) The exemption category
   `MessageTextSubject` for classes whose subject is the wording, such as the `*MissTests`, `*NotFound*` and formatter
   tests (recommended: otherwise 150+ legitimate pins in `Tests.Tools.Basic` and `Tests` inflate the baseline and teach people
   to ignore it; the owner reviews the Step 5b list). (iii) Migration: on touch plus the hottest behaviour tests, not a
   419-site core sweep (recommended; the ratchet stops growth, and the migration of sites in files that are being
   edited anyway costs nothing extra). Eager migration of everything is 35 or more tier-1 slices.
5. **Whitespace-insensitive assertion.** Options: (a) collapse every whitespace run to one space (recommended: a behaviour
   test about an edit should not break on indentation); (b) normalise only line endings (keeps indentation pins, the tests
   that break most on formatter changes keep breaking).
6. **BatteryToolFactory end state.** Keep the thin Advanced delegates (recommended, see Out of scope) versus delete and rewrite 89 call lines.
7. **Zero-edit facade versus call-site sweep.** Step 6c adds a second, internal `BatteryToolFactory` in `Tests.Tools.Basic`
   that only delegates to `TestTools` (recommended: 1 file now; the alternative is rewriting 118 call lines in 12 moved files
   to `Battery.Basic.TestTools.*` in about 5 sweep slices). The duplicate name is a transitional deviation, commented in the file.
8. **Ratchets in `Tests.Basic`.** Both new ratchets scan files by path and need no project references; `Tests.Basic` already
   holds the other source-scan ratchets (recommended). Alternative: `Tests.Server`, which already loads every tool assembly
   (`ToolHintRatchetTests`), is needed only if a later census wants reflection.
9. **Repo-root lookup.** `RepoRootLocator.Find(AppContext.BaseDirectory)` (recommended, equal to the copies, the environment
   variable never overrides it) versus `Find()` (the `ROSLYNSENTINEL_REPO_ROOT` variable wins, so a ratchet could scan a
   different checkout than the one the test assembly was built from, for example under `LaunchTestInstance`).

Risks and unverified items:

- **Container wiring versus hand wiring (Step 6b).** `BatteryToolFactory`'s Basic methods hand-wire engines in a fixed order
  (a `DiffEngine` before the `ValidationEngine`, "mirrors the BatteryTwentyFourTests [SetUp] wiring") and share instances across
  one tool class; `TestTools` builds from a fresh container per call. The step is the verification (136 call sites); a red
  result is reported, not patched.
- **Parallelism and shared state after a move.** The Advanced projects have their own `AssemblyInfo.cs` and
  `LeakDetectorSetUp.cs`; moved fixtures run under the destination project's settings. A test that passed in the source
  project and fails only on a full or parallel run in the destination gets a code comment (CLAUDE.md) and is reported.
- **Internal helpers.** A moved file that uses an `internal` helper of its old project (the compile gate names it) needs a
  `TestTools` or `Tests.Support.*` factory, not a copy. `BatteryToolFactory` is the known one and is handled by Step 6c.
- **Census completeness.** The Step 1 rule can undercount (inherited Advanced fixture, Advanced extension method called
  without naming its class) and treats a class that names one Advanced symbol as Advanced even if the use is incidental.
  The review in Step 1's "Done when" reads the borderline classes; the preliminary numbers above (30 files, 506 KB, 17.7%)
  come from a different, file-level rule, and the review brief's figure (about 35 files, 542 KB) used another; none of the
  three is the baseline.
- **`Tests.Asyncify` and `Server.Advanced`.** Hypothesis that the reference is unused; Step 9 proves it by building without it.
- **Baseline interplay.** `docs/known-misplaced-core-tests.txt` is path keyed and must lose a line in the same commit as the
  move; `docs/known-long-message-pins.txt` is file-name keyed and is unaffected by a move. A relocation commit that forgets the
  first file fails `TestPlacementRatchetTests` with STALE lines naming the class.
- **Cross-plan baselines.** No step changes tool output, wire text or model-eval behaviour; no A/B baseline of another plan
  is touched.

If unused Advanced tools are frozen out of the main solution:
- Worth doing either way (do first): Step 1 (it is the list of what must leave before the freeze, and stops new strays),
  Step 2a-2d, Step 3a-3c (the kit and the three core copies; 3c's three Advanced files move with Step 7), Step 4, Step 5,
  Step 6 (TestTools factories and the 136 Basic call sites), Step 7 (the core-subject tests must not be stranded), Step 8.
- Wasted (kept last): Step 10a-10d (they edit Advanced-subject files only), Step 9 (it only drops a reference from
  `Tests.Advanced` and `Tests.Asyncify`; the four moved tests are `Server.Advanced` hosting tests that stay useful in `Tests.Server`
  but no longer need the move), and the Advanced half of `BatteryToolFactory` (already out of scope).

## Decisions (todo review 2026-11-22)

- **D1-D9:** accepted as recommended: D1 relocate with MoveFile (batches of at most 8); D2 syntactic class-level census; D3 keep namespaces (MoveFile `update` is not built); D4 message ratchet floor 35 characters, `MessageTextSubject` exemption, migrate on touch; D5 whitespace-collapsing assert, `AssertByteExact` kept for formatter tests; D6 keep thin Advanced `BatteryToolFactory` delegates; D7 internal facade in Tests.Tools.Basic; D8 ratchets in Tests.Basic; D9 `RepoRootLocator.Find(AppContext.BaseDirectory)`.
- **Ratchet strictness:** house rule from plan_shared_tool_execution_pipeline D10: growth fails, a decrease passes with a "lower the baseline to N" message (replaces failing on STALE lines).
- **MoveFile is live** (`WholeFileWriteTools.cs`, MoveFile tool): the TOOL-GAP on Steps 7 and 9 is gone; ignore the "not built" wording in Step 7. Each `keep` move stores the full file in its blob (a READY TODO halves it); accepted.
- **Order:** Steps 1-6 before plan_central_engine_throw_and_lookup_helpers (ErrorAssert makes its text changes cheap). Step 7.5 also does the parked "Core split test-move remainder" TODO line (MoveMemberSuccessTests, MoveMemberToolCollateralRefusalTests; drop the misleading `[Category("AdvancedRefactoringTools")]`).
- **Acceptance:** as in the plan's Done-when sections; TestPlacementRatchetTests green with no baseline line for a moved class.

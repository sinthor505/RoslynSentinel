# Plan: near-miss member detail on rejections, qualified enum members in outlines, then eval-only auto-correction

**Status:** IN PROGRESS 2026-11-18. Steps 1-3 done (a4056bc8, b44db0d1, e4bea9e3, 79337626). Batched run,
owner away; D3 and D4 settled (see Decision). Phase B (Steps 8-13, eval-only) follows Phase A.

**Name trap:** inside `RoslynSentinel.Engines.Basic`, `SymbolCandidate` resolves to an unrelated record in
`SymbolNavigationEngine.cs`; the `Common` one (`SymbolNotFoundDetail.cs:13`) needs its namespace. Any step
touching `NearMissName.Candidates` (4, 5, 6, 12) must use the `Common` type.

## Problem

After a token-path slip (`ShippingKinds.Oovernight`), every recovery path the server offers ends with the
model retyping the name, which is the operation that fails (tiel: 14 correct hints, 16 repeated typos).
The server already computes the closest name and corrected line, but only as text. Evidence and design:
[proposal_server_slip_recovery.md](../proposals/proposal_server_slip_recovery.md); harness consumer:
[proposal_harness_slip_rollback_and_resample.md](../proposals/proposal_harness_slip_rollback_and_resample.md).

## Decision

- **Phase A, data and prevention:** a near-miss detector in `Engines.Basic` binds the receiver of each
  CS0117/CS1061 (later CS0103) in the proposed document, ranks accessible members, and returns items that
  go into the one shared `structuredDetail[0]` (`EditImpactDetail`) at all three compile-gate sites.
  Outlines list enum members in qualified form. The existing hint text is not changed.
- **Phase B is an evaluation feature, off by default** (owner decision 2026-11-18; production relies on the
  sampler preset and revert-and-rerun):
  - Two server options: auto-correction (strict rule) and a show/hide display toggle for the wrong name.
  - Every substitution is logged to the server log and in the result's `_meta`, so the transcript records it.
  - No fix ids, no store, no new tool.

Settled 2026-11-18 (implement-plan review; owner answers marked "owner"):
- D3 (owner): qualify enum members in the shared `ExtractOutlineEntries` walk, so `ListAll` rows and its
  `nameFilter` matching change too. Step 7 updates `ListAll` accordingly.
- D4 (owner): the eval options live in a new class modelled on `RoslynSentinel.Common/LiftRenamesOptions.cs`
  (startup `Configure`, env fallback, `AsyncLocal` `OverrideForCurrentContext`), wired where
  `ServerStdio.cs` and `ServerHttp.cs` call `Configure(args)`; not in `LlmOptions`.
- Owner is away for the run; mode is Batched.
- Review fixes (no owner input needed): the detector's catch excludes `OperationCanceledException`
  (as `EditImpactGate.AnalyzeAsync` does); `CreateFile` and `WriteFile` to a new path are out of scope
  (the detector skips non-baseline documents by design); the wrong name must also be redacted from
  every place Step 10 lists.

## Execution rules

- Each slice: `Build` (default `fullBuildThrowaway`) 0 errors. The dispatcher runs the named tests; the
  test baseline is 0 failures, so any failure is new.
- One `Build`/`RunTest` at a time across all lanes.
- Commits carry `Plan: plan_server_slip_recovery`; the last one carries `Closes: plan_server_slip_recovery`.
- New DTOs follow the `dto-conventions` skill. The justification for Step 1 is below; copy it into the
  `reason` of the call that creates the type.
- The error-text rules apply to every new string: facts and options only
  (`docs/current/references/reference_tool_error_text_facts_options_policy.md`).
- Text-neutral slices (1, 2, 3) run first; behaviour-changing slices (4-7) after.

## Steps

### Step 1 - Contract skeleton (text-neutral)
- Files: `RoslynSentinel.Engines.Basic/EditImpactResult.cs`, `RoslynSentinel.Engines.Basic/NearMissDetector.cs` (new),
  `RoslynSentinel.Engines.Basic/EditImpactGate.cs`
- Depends on: none
- Effect: text-neutral. No caller passes near misses yet; `EditImpactDetail` gains an optional trailing
  parameter whose default is null.
- Change:
  - In `EditImpactResult.cs`, beside `EditImpactDetail` (`:113`):
    - Add `public sealed record NearMissName(string DiagnosticId, string FilePath, int Line, int Column, string? ReceiverType, string Replacement, string? CorrectedLine, bool WrongNameDeclaredElsewhere, IReadOnlyList<SymbolCandidate> Candidates);` (new). XML doc: `Replacement` is the receiver as written plus the closest name (`ShippingKinds.Overnight`); the wrong name is deliberately not a field.
    - Extend `public sealed record EditImpactDetail(IReadOnlyList<ImpactVerdict> Verdicts, IReadOnlyList<DiagnosticInfo> Diagnostics)` with a third parameter `IReadOnlyList<NearMissName>? NearMisses = null`. Its only construction site is `EditImpactGate.BuildDetail` (`EditImpactGate.cs:54`); five tests read it (`EditImpactGateWiringTests`, `EditImpactSpanAttributionTests`) and none construct it.
  - New `NearMissDetector.cs`: `public static class NearMissDetector` with
    `public static Task<IReadOnlyList<NearMissName>> DetectAsync(Solution baseline, IReadOnlyDictionary<FilePathWrapper, string> changes, IReadOnlyList<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)`,
    body `throw new NotImplementedException(); // TODO implement (Step 2)`.
  - `EditImpactGate.BuildDetail(EditImpactResult? impact, IReadOnlyList<DiagnosticInfo> diagnostics, IEnumerable<ImpactVerdict>? liftVerdicts = null)` gains a trailing `IReadOnlyList<NearMissName>? nearMisses = null` and passes it to the constructor, as null when the list is empty.
  - One `CreateFile` call for the new file. One `batchEdits` call per existing file.
  - DTO justification for `NearMissName`:
    ```
    NEW-DTO: NearMissName (Item, Engines.Basic.EditImpact)
    Evaluated: SymbolCandidate - one declared symbol with no diagnostic position, replacement or corrected line (reused as the candidate list); ImpactVerdict - a changed declaration and its broken uses, not an unresolved use; DiagnosticInfo - the compiler's view, no candidates
    Wire impact: new tool output field(s) (errorData.structuredDetail[0].nearMisses, from Step 4)
    ```
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: a4056bc8

### Step 2 - Detector for CS0117 and CS1061 (text-neutral)
- Files: `RoslynSentinel.Engines.Basic/NearMissDetector.cs`
- Depends on: 1
- Effect: text-neutral. No caller yet.
- Change: implement `DetectAsync`:
  1. Keep diagnostics with `Id` CS0117 or CS1061. Return empty when there are none, without touching the
     solution.
  2. Fork `baseline` with each changed document's proposed text: `WithDocumentText` for documents that
     exist. Skip diagnostics in files that are not documents of `baseline`, such as a CreateFile target.
  3. For each diagnostic:
     - Get the semantic model and find the node at `StartLine`/`StartColumn` (`DiagnosticInfo`,
       `RoslynSentinel.Common/DiagnosticReport.cs:54`, 1-based). Climb to the enclosing
       `MemberAccessExpressionSyntax` whose `Name` covers the position.
     - Bind the receiver: if `GetSymbolInfo(access.Expression).Symbol` is an `INamedTypeSymbol`, the access is static. Otherwise use `GetTypeInfo(access.Expression).Type`, an instance access.
     - Candidates: `LookupStaticMembers(position, type)` for static access; `LookupSymbols(position, type, includeReducedExtensionMethods: true)` for instance access. Drop implicitly declared members, constructors and operators.
  4. Rank the candidate names with `CorrectedLineHint.FindClosest(string wanted, IEnumerable<string> names)`
     (`RoslynSentinel.Engines.Basic/CorrectedLineHint.cs:25`). It returns null when nothing is within its
     limit; skip that diagnostic.
  5. Build the item:
     - `Candidates`: up to 5 entries from `NameCandidates.Rank` (`RoslynSentinel.Common/NameCandidates.cs:49`, `strict: true`), as `SymbolCandidate` (`RoslynSentinel.Common/SymbolNotFoundDetail.cs:13`).
     - `Replacement` = `access.Expression.ToString() + "." + closest`.
     - `CorrectedLine` = `CorrectedLineHint.RewriteLine(sourceLine, wrongName, closest, startColumn, memberAccess: true)` (`:51`).
     - `ReceiverType` = `type.ToDisplayString()`.
     - `WrongNameDeclaredElsewhere`: true when any project's compilation in `baseline` reports `ContainsSymbolsWithName(wrongName, SymbolFilter.Member)`.
  6. Wrap the body in a catch that returns empty, except `OperationCanceledException`, which propagates.
     A detector failure must never break a rejection; log nothing here, because the engine has no logger.
  - Map each `ISymbol` to a `SymbolCandidate` (`Kind`, `Container`) with a small private helper in the
    detector; the mapping inside `SymbolNotFoundDetail` is private.
- Done when: `Build` 0 errors; Step 3 tests pass.
- Lane: implementer-senior (Roslyn binding judgment; within tier-1 size).
- Done: b44db0d1 (also retyped `NearMissName.Candidates` to the `Common` `SymbolCandidate`); 79337626 (project lookup via `ProjectLookup.FindProject`, ratchet).

### Step 3 - Detector tests (text-neutral)
- Files: `RoslynSentinel.Tests/NearMissDetectorTests.cs` (new)
- Depends on: 2. It can be written against the Step 1 signature in parallel with Step 4.
- Effect: none (tests).
- Change: build each fixture with the `InMemoryWorkspace` pattern of `EditImpactGateWiringTests`; an
  `AdhocWorkspace` project has no metadata references, so the CS1061 instance test would need them added.
  `CompilerErrorLookupHelperTests` builds fixtures through `_workspaceManager` and
  `CompileAndGetErrorsAsync` (`:24-47`); its `ShippingKindsSource` (`:485`) is reusable. Tests:
  - `DetectAsync_EnumMemberSlip_ReturnsQualifiedReplacementAndCorrectedLineAsync`: `ShippingKinds.Oovernight` returns one item with `Replacement == "ShippingKinds.Overnight"`, a `CorrectedLine` containing `ShippingKinds.Overnight`, and `WrongNameDeclaredElsewhere == false`.
  - `DetectAsync_InstanceMemberSlip_Cs1061_ReturnsTheClosestInstanceMemberAsync`
  - `DetectAsync_NoCloseName_ReturnsEmptyAsync`
  - `DetectAsync_PrivateMemberOfAnotherType_IsNotACandidateAsync`
  - `DetectAsync_WrongNameDeclaredOnAnotherType_SetsDeclaredElsewhereAsync`
  - `DetectAsync_NoMemberDiagnostics_ReturnsEmptyAsync`
- Done when: the dispatcher's `RunTest` on `NearMissDetectorTests` passes.
- Lane: test-writer.
- Done: e4bea9e3. Gaps left for Step 5: the tests use hand-built `DiagnosticInfo` positions and never assert `Candidates`; cover both in the wiring tests, which use real compile diagnostics.

### Step 4 - Wire the detector into the three gate sites (behaviour-changing)
- Files: `RoslynSentinel.Engines.Basic/EditImpactGate.cs`, `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`
- Depends on: 1 (signatures only). It can pair with Step 2; the output is empty until Step 2 lands.
- Effect: behaviour-changing. A rejection with a near-miss CS0117/CS1061 carries
  `errorData.structuredDetail[0].nearMisses`; message text is unchanged.
- Change: in each site, get `baseline = await workspaceManager.GetSolutionAsync(ReadSource.Committed, ct)`,
  call `NearMissDetector.DetectAsync(baseline, changes, report.Diagnostics, ct)` and pass the result to
  `BuildDetail`. Put the get-baseline, detect and swallow-non-cancel-errors sequence in one helper in
  `EditImpactGate` and call it from all three sites. The sites:
  - `EditImpactGate.Describer` (`EditImpactGate.cs:24-35`)
  - `EditImpactGate.RejectionAsync` (`:62-75`)
  - `WorkspaceFileEditImpl.ComposeCompileRejectionAsync` (`WorkspaceFileEditImpl.cs:1224`), through `RejectionDetail` (`:1187`), which gains the same trailing parameter. It has 1 caller.
- Done when: `Build` 0 errors; existing `EditImpactGateWiringTests` and `EditImpactSpanAttributionTests` still pass (dispatcher).
- Lane: implementer.

### Step 5 - Wiring tests (behaviour-changing coverage)
- Files: `RoslynSentinel.Tests/NearMissRejectionTests.cs` (new)
- Depends on: 2, 4
- Effect: none (tests).
- Change: follow the fixture pattern of `EditImpactGateWiringTests` (`RoslynSentinel.Tests/EditImpactGateWiringTests.cs`,
  `DetailOf` at `:142`). One test per site family; each asserts `nearMisses[0].Replacement == "ShippingKinds.Overnight"`:
  - `ReplaceSnippet_EnumMemberSlip_CarriesNearMissInStructuredDetailAsync`
  - `WriteFile_EnumMemberSlip_CarriesNearMissInStructuredDetailAsync` (RejectionAsync)
  - one `ValidateAndApplyHelper` tool, `Member` or `MethodSignature`: `Member_EnumMemberSlip_CarriesNearMissInStructuredDetailAsync`
  - `ReplaceSnippet_UnrelatedCompileError_HasNoNearMissesAsync`, which asserts `NearMisses` is null.
- Done when: the dispatcher's `RunTest` on `NearMissRejectionTests` passes.
- Lane: test-writer.

### Step 6 - CS0103 in the detector (behaviour-changing, additive)
- Files: `RoslynSentinel.Engines.Basic/NearMissDetector.cs`, `RoslynSentinel.Tests/NearMissDetectorTests.cs`
- Depends on: 3
- Effect: behaviour-changing. A bare-name slip (`case Oovernight:` under `using static`) also gets an item
  with `ReceiverType` null.
- Change: for CS0103, take the `IdentifierNameSyntax` at the position and use `LookupSymbols(position)` for
  the candidates, which gives names in scope at the proposed position. `Replacement` is the closest name
  alone. Add `DetectAsync_Cs0103BareNameSlip_ReturnsTheNameInScopeAsync`.
- Done when: `Build` 0 errors; `NearMissDetectorTests` passes.
- Lane: implementer (code) then test-writer (test), or implementer-senior for both.

### Step 7 - Qualified enum members in the outline walk (behaviour-changing; D3 settled)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, `RoslynSentinel.Tests/OutlineEnumMemberTests.cs` (new)
- Depends on: none. Independent of Steps 1-6, so it can pair with any of them.
- Effect: behaviour-changing. Enum-member rows in `GetFileOutline` and `ListAll` become `Type.Member`.
- Change:
  - First, a read-only check: do `Member(memberName:)` and `ReadFile(memberName:)` accept `Type.Member` for
    an enum member? If either does not, report it and make the resolver accept the qualified form in this step.
  - In `ExtractOutlineEntries`, the enum-member case (about `WorkspaceReadNavigationImpl.cs:573-577`)
    sets `name = enumMember.Identifier.Text`; it becomes
    `$"{enumDeclaration.Identifier.Text}.{enumMember.Identifier.Text}"`. `OutlineItem`
    (`RoslynSentinel.Common/OutlineItem.cs:4`) has 1 construction site (about `:596`).
  - `ExtractOutlineEntries` also feeds `ListAll` (about `:658`), which matches `nameFilter` against
    `item.Name` and emits `SolutionSymbolEntry.Name`. Update it so `nameFilter: Overnight` and
    `exactMatch: true` still find the member (match the member part as well as the qualified name).
  - No test pins enum-member outline or `ListAll` rows (review, 2026-11-18). Add
    `GetFileOutline_EnumMembers_AreListedQualifiedAsync` and
    `ListAll_EnumMemberBareNameFilter_StillFindsQualifiedRowAsync`.
- Done when: `Build` 0 errors; `OutlineEnumMemberTests` passes.
- Lane: implementer.

Phase B below is an evaluation feature. Every option it adds defaults to today's behaviour.

### Step 8 - `_meta` passthrough check (read-only, gates Step 11)
- Files: none (investigation)
- Depends on: none; it can run any time during Phase A.
- Effect: none.
- Change: find out whether a server tool result's MCP `_meta` reaches the harness. Trace three things:
  - the server's result construction (`SentinelCallToolResult`, `RoslynSentinel.Common/SentinelCallToolResult.cs`);
  - the MCP C# SDK's `CallToolResult` (local clone, memory reference_csharp_sdk_local_clone);
  - `McpClientToolHost` in `RoslynSentinel.Common`.

  Report whether `_meta` survives, and whether `LmStudioAgentClient` sends it to the model (it must not).
- Done when: a yes/no with file:line evidence. If no, Step 11 uses the fallback in Risks.
- Lane: explore-tier2.

### Step 9 - Options skeleton (text-neutral; D4 settled)
- Files: `RoslynSentinel.Common/NearMissOptions.cs` (new), `RoslynSentinel.Server.Advanced/ServerStdio.cs`, `RoslynSentinel.Server.Advanced/ServerHttp.cs`
- Depends on: none
- Effect: text-neutral. Both options default to today's behaviour.
- Change: add a static `NearMissOptions` modelled on `LiftRenamesOptions` (startup `Configure(args)`, env
  fallback, `AsyncLocal` `OverrideForCurrentContext` for tests), called beside the existing `Configure(args)`
  calls (`ServerStdio.cs:43-47`, `ServerHttp.cs:31-35`). Flag parsing follows `--llm-minimal-tools`
  (`LlmOptions.cs:150`). Options:
  - `NearMissAutoCorrect` (bool, default false): `--near-miss-autocorrect`, env `ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT`.
  - `NearMissNameDisplay` (enum `show` | `hide`, default `show`): `--near-miss-name-display`, env `ROSLYNSENTINEL_NEAR_MISS_NAME_DISPLAY`.

  The XML doc of each states it is an evaluation option and not meant for production use.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 10 - Display toggle in the rejection text (behaviour-changing only under `hide`)
- Files: `RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs`, `RoslynSentinel.Tests/NearMissDisplayTests.cs` (new)
- Depends on: 9
- Effect: under `hide`, no rejection text repeats the wrong name; under `show`, unchanged.
- Change: under `hide`, three places change.
  - The CS0117 hint sentence (`CompilerErrorLookupHelper.cs:604`, `"Your edit contains '{memberName}' ({memberName.Length} letters)..."`) drops the wrong name and keeps the qualified candidates and corrected line (`:611`, `:614`).
  - The diagnostic header line built by `DescribeAsync` (`:36`, `{first.Id} at {file}:{line}: {first.Message}`) redacts the wrong name from `Message`.
  - The source line quoted under the diagnostic is replaced by the corrected line when one exists.

  The review found more text in `DescribeMissingMemberAsync` that repeats the wrong name; under `hide` these
  change too: `notFound` and `likelyTypo` (about `:626-627`), the extension-candidate text (about `:562`),
  `editedOnNamedType` and `explanation` (about `:636-641`), and the `SpanText` line ("Expression the error
  points at"). Step 11 redacts the `structuredDetail` counterparts (`Message`, `SourceLine`, `SpanText`).

  The CS0103 sentence (`:407`) already omits the name. Candidates are rendered as written in the code
  (`ShippingKinds.Overnight`), with the full namespace only when two candidate types share a simple name.

  Tests:
  - `Hide_Cs0117Rejection_TextNeverContainsTheWrongNameAsync`
  - `Show_Cs0117Rejection_TextIsUnchangedAsync`
  - `Hide_Cs0117Rejection_ShowsTheCorrectedLineInsteadOfTheSourceLineAsync`
- Done when: `Build` 0 errors; `NearMissDisplayTests` passes.
- Lane: implementer (code) then test-writer (tests).

### Step 11 - Redact structured diagnostics under `hide` and record substitutions (behaviour-changing under the options)
- Files: `RoslynSentinel.Engines.Basic/EditImpactGate.cs`, plus the `_meta` writer per Step 8
- Depends on: 4, 8, 9
- Effect: under `hide`, the `DiagnosticInfo.Message` in `structuredDetail` is redacted the same way. Every
  substitution (Step 12) and every ambiguous near-miss hint produces one server log line and one `_meta`
  record (tool, file, line, from, to, diagnostic id, candidate count, display mode).
- Change: redaction inside `BuildDetail`'s diagnostics list. The record is a new item type; its NEW-DTO
  justification is written when Step 8 settles the channel.
- Done when: `Build` 0 errors; a test asserts the `_meta` record (or the fallback field) for one substitution.
- Lane: implementer-senior.

### Step 12 - Eval-only auto-correction (behaviour-changing only with `NearMissAutoCorrect`)
- Files: `RoslynSentinel.Engines.Basic/NearMissDetector.cs` (correction builder), `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`,
  `RoslynSentinel.Common/ValidateAndApplyHelper.cs` (via a delegate parameter, like `describeEditImpact`, because Common cannot call the engine).
  This exceeds one tier-1 slice: split it into (12a) builder plus `ValidateAndApplyHelper` and (12b) ReplaceSnippet plus `RejectionAsync` sites.
- Depends on: 2, 4, 9, 11
- Effect: with the option on, an edit that meets the strict rule is corrected and applied. Its success result
  carries a finding stating the corrected line (hide mode) or the substitution (show mode). Precedent:
  `WithLiftFinding` (`WorkspaceFileEditImpl.cs:1208`).
- Change:
  - Strict rule, all required:
    - every new error in the edit is a near-miss item;
    - each item has exactly one candidate within the `FindClosest` limit;
    - `WrongNameDeclaredElsewhere == false`;
    - the corrected changeset validates clean through `ValidationEngine.ValidateChangesAsync`.
  - Otherwise reject with the Step 10 hint.
  - The corrected text must pass ReplaceSnippet's collateral-change check (`expectedEdits`, `ValidateAndApplyHelper.cs:120-150`): add the correction spans to the expected edits.
- Done when: `Build` 0 errors. Tests in `RoslynSentinel.Tests/NearMissAutoCorrectTests.cs` (new), all `Async`:
  - `AutoCorrectOn_UniqueNearMiss_AppliesTheCorrectedLine`
  - `AutoCorrectOn_TwoCandidatesInRange_RejectsWithQualifiedCandidates`
  - `AutoCorrectOn_SlipPlusRealError_RejectsBothWithoutPartialFix`
  - `AutoCorrectOn_WrongNameDeclaredOnAnotherType_Rejects`
  - `AutoCorrectOff_UniqueNearMiss_RejectsAsToday`
- Lane: implementer-senior (12a), implementer (12b), test-writer (tests).

### Step 13 - Stub sweep and live check
- Files: none (checks)
- Depends on: all built steps
- Effect: none.
- Change:
  - `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/NearMissDetector.cs")` finds nothing.
  - Commit, then restart the server.
  - Run ShippingKindStep through `roslynsentinel-evalqueue.ps1` on a model that slips (tiel, preset sampler) in four arms: auto-correct off and on, each with `show` and `hide`.
  - Report slipped runs in five classes: assisted pass (substitution applied); pass after retyping; failed after a substitution (e.g. a later snippet that still contains the typo); failed with a near-miss hint; failed with no near-miss item (detector gap). Report non-slipped runs separately.
- Done when: no stubs; each arm is reported in those classes.
- Lane: dispatcher (search); modeleval-runner (live run).

## Out of scope

- Changing the default hint text: `show` keeps today's text, and only the eval arms decide whether `hide`
  becomes the default.
- Fix ids or any apply-by-reference mechanism (dropped 2026-11-18; see the proposal's alternatives).
- Auto-correction as a production feature.
- Qualified names for static members of non-enum types, and in `Search(mode: enum member)` rows (bare
  `name` plus `container` today).
- All harness work ([proposal_harness_slip_rollback_and_resample.md](../proposals/proposal_harness_slip_rollback_and_resample.md)).

## Risks and open decisions

- **D3, settled 2026-11-18 (owner):** qualified `Name` for enum members in the shared walk; Step 7 checks
  that `Member(memberName:)` and `ReadFile(memberName:)` accept `Type.Member`, and updates `ListAll`.
- **D4, settled 2026-11-18 (owner):** new `NearMissOptions` modelled on `LiftRenamesOptions`.
- **New-file targets:** `CreateFile` and `WriteFile` to a new path get no near-miss items (not baseline
  documents). Out of scope.
- **`_meta` fallback.** If Step 8 finds that `_meta` does not reach the harness, add a result field
  (`nearMissSubstitutions`) that `ModelAgentRunner` strips before building `ModelFacingToolContent`
  (`ModelAgentRunner.cs:978`). Third-party clients would then show it to their model, which is
  acceptable only because the options are eval-only.
- **Auto-correction risk (accepted).** A use written before its member is declared gets a silent change of
  meaning that compiles. Documented in the option's XML doc.
- **Unverified:** whether `structuredDetail` serialization omits a null `NearMisses`. If it writes
  `nearMisses: null`, every rejection gains a field. Step 4 checks the wire output in an existing
  `EditImpactGateWiringTests` case.
- **Detector cost:** Step 2 forks the solution and recompiles the edited project, only when a CS0117,
  CS1061 or CS0103 diagnostic is present. If rejections get slower, reuse the compilation from
  `ValidationEngine` (whether it is reachable is unverified).
- **Phase B and the harness rollback are alternative arms.** With auto-correction on, the harness
  rollback fires only on ambiguous cases. Auto-correction leaves the typo in context and rollback removes it,
  so compare them as arms; do not assume both are needed.

# Plan: mutating tools return a compact result by default; changed content is offloaded, inline only by opt-in

**Status:** IMPLEMENTED 2026-10-08 (commits 8c987c9, 2ac8461, e198987; deviations in the Implementation notes section at the end). Was: DRAFT 2026-10-08 (revised same day for the user decision: content OFFLOADED by default, inline echo OPT-IN pending an impact review). Priority P1 (do first; `plan_mutating_and_test_tool_result_noise.md` Steps 1-2 depend on Steps 1-2 here). Source: journal digest `.claude/journal/digest_20261008-1509.md` (window 2026-10-02..08), clusters "Member", "Member addMember"; user decision A.

## Problem

Every refactoring tool that returns an `AppliedChangeSummary` fills `ChangedContent` (a dictionary of file -> the whole updated text) on a real, applied change. The agent already knows what it asked for; the echo costs 5-20 KB per edit, tips results over the 15,360-byte offload threshold, and the agent then pays a `GetLargeResult` page to read a file it did not ask for (journal `[a08be84f:L24]` about 5k tokens per edit, `[50e0e6ac:L21]` "10-20 KB noise per call", `[47b2c93d:L9]`, `[a08be84f:L59]`).

Traced to source:

- The echo is chosen per call site, not centrally. 29 applied-change sites pass `ChangedContent: <changes>, Validated: true` for a non-dry-run apply (measured 2026-10-08 with `Search(text, "ChangedContent: ")`):
  - `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs` lines 165, 368, 546, 856, 901, 942, 1276, 1320, 1364, 1409, 1453, 1497, 1563, 1603 (14).
  - `RoslynSentinel.Tools.Basic/RefactoringExtractionDocsImpl.cs` lines 133, 147, 226, 363 (4).
  - `RoslynSentinel.Tools.Basic/RefactoringSignatureImpl.cs` lines 242, 311, 462 (3).
  - `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` lines 112, 729, 802, 883, 946, 1072, 1202, 1308 (8). Lines 729, 802, 883 are the three `Member` add sites owned by `plan_mutating_and_test_tool_result_noise.md`; 1202 (`ModifyModifier`) and 1308 (`ModifyBaseType`) sit under comments that say "No ChangedContent" (lines 1199, 1305) yet still pass it.
  - Line numbers for 311, 946 and 1308 were read from search previews only; the implementer re-measures every site.
  - A further ~35 `new AppliedChangeSummary(...)` sites (for example `AdvancedRefactoringTools.cs` 210, 251, 406, 432, 680, 743, 780, 1019 and `GenerationTools.cs:263`) pass neither `ChangedContent` nor `Validated`, so they never echo; but several of them are real applies with `Validated` left at its default `false` (this matters to the optional Step 9).
- The no-stage branches (`autoStage: false`) construct `new AppliedChangeSummary(ChangeId: null, ..., DryRun: false, ..., ChangedContent: noStageChanges, Validated: false)` (for example `RefactoringStructuralImpl.cs` 103, 712, 788, 867, 928, 1060, 1188, 1294; `RefactoringExtractionDocsImpl.cs` 112, 210, 337; `RefactoringSignatureImpl.cs` 229, 290, 438; `AdvancedRefactoringTools.cs` 144, 331, 480, 842, 887, 928, 1261, 1305, 1349, 1394, 1438, 1482, 1548, 1579). Nothing was written there, so the content IS the result and must stay.
- The apply outcome already carries per-file line counts: `ValidateAndApplyHelper.ValidateAndApplyAsync` returns an `ApplyOutcome` whose `LineChanges` is computed on every path (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:95,116,153,156`). Most applied sites do not pass it into the summary.
- Only two places read `ChangedContent` back: `RoslynSentinel.Tests.Advanced/MassiveRefactoringTests.cs:89` and `:164`, both `autoStage: false`. `RoslynSentinel.Tests.Server/StructuredContentDataTagTests.cs:209` asserts `status == "applied"` for a real `ModifyModifier` apply (unchanged by this plan) but also compares structured content with the actual data, so run that test explicitly. No test asserts `AppliedChangeSummary.Note` text.
- Tools in the `ApplyChangesResult`/`ReplaceSnippetResult` family (ReplaceSnippet, CreateFile, WriteFile, DeleteFile, ApplyDiff, RetryFailedChanges) already strip pre-images before returning; not part of this problem.

### How the existing offload mechanism works (traced, to be reused rather than reinvented)

- `LargeResultHelper.StoreRawJsonAsync(string json, string? solutionRoot, CancellationToken)` (`RoslynSentinel.Common/LargeResultHelper.cs:80-101`) writes the text unconditionally (no size threshold, unlike `StoreLargeResultAsync` at lines 46-68 which returns `offloaded:false` at or below `OffloadThresholdBytes`), as a `ResultWrapperType.Raw` file `<solutionRoot>/.roslynsentinel/largeresults/largeresult_<utc>_<guid>.json`, and returns `(offloaded, filePath, resultId)` with `resultId = Guid.NewGuid().ToString("N")`. It fails closed (returns `offloaded:false`, never throws) when `solutionRoot` or the text is empty.
- `GetLargeResult` already reads `Raw` files by `resultId` and pages them as a character window (`WorkspaceReadNavigationImpl.cs` around lines 1044-1075; parameters `resultId`, `offset`, `charLimit`; window capped at about 7,400 chars so the response itself cannot be re-offloaded). So a stored `Raw` file needs no new `GetLargeResult` code.
- `SentinelCallToolResult<T>.ForPossiblyLargeDataAsync` (`SentinelCallToolResult.cs:265-293`) and the request-filter backstop (`Server.Basic/ServiceRegistrationExtensionsBasic.cs:530-690`) both call these same two helpers and mint the id the same way; the filter's pointer carries only `statusMessage`, `itemCount` and `listSummary`, which is why today's over-threshold edit result hides the outcome inside the file.
- The apply blob that `ValidateAndApplyHelper` already writes per mutation (`OperationBlobWriter.WriteApplyBlobAsync`) stores PRE-images only (`OperationBlobWriter.cs:210` reads `result.PreImages`), so it cannot double as the place to fetch the new content from: the offload is one extra file write per successful mutation.
- Cost of that write, measured: `.roslynsentinel/largeresults/` currently holds 1,092 files / 128.1 MB in this repo (2026-10-08), no code deletes from it (a text search for `largeresults` found only writers and readers), and it is git-ignored through `*.json` (`.gitignore:22`). Adding one file per mutation, sized as the sum of the changed files' text (a rename touching N files writes all N), makes that growth visible. Retention is out of scope but flagged below.

## Decision

Default for a real apply (`Validated == true && DryRun == false`):

- `AppliedChangeSummary.ChangedContent` is NOT returned inline. The summary carries `AffectedFiles`, `Description`, `ChangeId`, `LineChanges`, `Diff` when `returnDiff` was asked for, a new `ChangedContentResultId`, and a `Note` that says how to get the content: "The updated content is not echoed. Fetch it with GetLargeResult(resultId: "<id>"), or read the file with ReadFile, GetMethodSource or Member(operation: view)."
- The content is stored with the existing mechanism: `ValidateAndApplyHelper.ValidateAndApplyAsync` (the one place every real apply passes through, which also holds the solution root) serialises `{ "<path>": "<updated text>" }` and calls `LargeResultHelper.StoreRawJsonAsync`; the id is returned on `ApplyOutcome.ChangedContentResultId` and copied into the summary by each site.
- Dry runs keep the inline echo (confirmed in source: the dry-run branch returns at `ValidateAndApplyHelper.cs:90-95` before any write or offload, and the summary's `DryRun` comes from `apply.DryRun`; the gate rule `Validated && !DryRun` leaves them alone). No-stage results keep the inline echo (nothing was written, the content is the result).
- Opt-in to inline echo: ONE server-wide switch, `ChangedContentOptions.InlineOnApply` (new static class in `RoslynSentinel.Common`, default false, initialised from the environment variable `ROSLYNSENTINEL_INLINE_CHANGED_CONTENT` = `1`/`true`, also settable in code for tests and for a future `--inline-changed-content` startup arg). It is read in exactly two places: the `AppliedChangeSummary` gate (inline content returned, no gate) and `ValidateAndApplyHelper` (no offload, no disk write, no id). It adds NO parameter to any tool, so tool schemas do not grow (schema diet), `ArchitectureDocFreshnessTests` stays green, and a weak model has nothing new to explain. The evaluation harnesses and operators who want the old behaviour set the env var in the `.mcp.json` `env` block. Alternatives considered and rejected for now (see Risks): a per-call parameter on ~40 tools, and a schema-patcher-injected shared parameter.

## Execution rules

- One compile-green slice per step; `Build` (0 errors) after each. Do the steps in order.
- Every site edit is exactly: add `, LineChanges: <that site's apply>.LineChanges, ChangedContentResultId: <that site's apply>.ChangedContentResultId` to the existing `new AppliedChangeSummary(...)`. Do not remove `ChangedContent:` from the applied sites (the gate makes it inert; removing it later is cosmetic). Do not touch no-stage branches (`Validated: false` with `ChangeId: null`).
- If a site's apply variable is not an `ApplyOutcome`, skip that site and list it in the reply; do not invent a value.
- Tests that flip `ChangedContentOptions.InlineOnApply` must be `[NonParallelizable]` and restore the value in `finally`/`TearDown`.
- ASCII-only punctuation.

## Steps

### Step 1 - Options class, central gate, new summary parameter and hint (with unit tests)
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\ChangedContentOptions.cs` (new), `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\AppliedChangeSummary.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Basic\AppliedChangeSummaryTests.cs` (new)
- Symbols and changes:
  - New `public static class ChangedContentOptions` with `public static bool InlineOnApply { get; set; }` whose initial value parses `Environment.GetEnvironmentVariable("ROSLYNSENTINEL_INLINE_CHANGED_CONTENT")` (`"1"` or `"true"`, ignoring case). Doc comment: why it is server-wide and not a tool parameter (schema size). Model the shape on `RoslynSentinel.Common/LlmOptions.cs` but skip the `Configure(args)` wiring (out of scope).
  - `AppliedChangeSummary` (record): append one positional parameter after `LineChanges`: `string? ChangedContentResultId = null` (additive; every existing constructor call keeps compiling).
  - Add an explicit property to the record body, which makes the compiler skip the synthesised positional one:
    `public Dictionary<FilePathWrapper, string>? ChangedContent { get; init; } = Validated && !DryRun && !ChangedContentOptions.InlineOnApply ? null : ChangedContent;`
    with a `<summary>`: inline only for dry runs, no-stage results and the opt-in; otherwise see `ChangedContentResultId`. (Primary-constructor parameter names bind inside property initializers. If the compiler rejects the self-named reference, stop and reply `RESCOPE:` with the error.)
  - `Note`: in the two written-to-disk messages (the `ChangeId` branch and the "NOT reversible" branch) append, only when `Validated` is true and `ChangedContent` is null: when `ChangedContentResultId` is non-empty, `" The updated content is not echoed; fetch it with GetLargeResult(resultId: \"{ChangedContentResultId}\"), or read the file with ReadFile, GetMethodSource or Member(operation: view)."`; otherwise `" The updated content is not echoed; read it with ReadFile, GetMethodSource or Member(operation: view)."`. Leave the dry-run, no-changes and no-stage wording unchanged.
  - Tests (NUnit, model on `RoslynSentinel.Tests.Basic/ResultStatusTests.cs`):
    - `RealApply_DropsChangedContent`; `DryRun_KeepsChangedContent`; `NoStage_KeepsChangedContent` (`Validated: false, ChangeId: null`).
    - `RealApply_WithResultId_NoteNamesGetLargeResultAndResultId`; `RealApply_WithoutResultId_NoteNamesReadFile`.
    - `RealApply_SerializedJsonOmitsChangedContentAndCarriesResultId` (System.Text.Json; no `"changedContent":{`, has `changedContentResultId`).
    - `RealApply_InlineOptionOn_KeepsChangedContent` (`[NonParallelizable]`, restores the option).
- Call sites: none change (constructor gains a defaulted last parameter; named `ChangedContent:` still binds).
- Done when: `RunTest` project `RoslynSentinel.Tests.Basic`, filter `FullyQualifiedName~AppliedChangeSummaryTests`, passes (7 tests) and `Build` has 0 errors.

### Step 2 - Offload in the central apply path (ApplyOutcome + ValidateAndApplyHelper)
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\ApplyOutcome.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\ValidateAndApplyHelper.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Basic\ChangedContentOffloadTests.cs` (new)
- Symbols and changes (land the first two in one `ReplaceSnippet` batch, definitions first):
  - `ApplyOutcome`: append `string? ChangedContentResultId = null` after `LineChanges`, with a `<param>` doc. All 10 constructions inside `ValidateAndApplyHelper.cs` (lines 44, 51, 71, 88, 94, 108, 134, 149, 156 plus the helper's own) keep compiling; the other `ApplyOutcome` mentions (`GenerationTools.cs:60`, `AdvancedRefactoringTools.cs:95`, `RefactoringExtractionDocsImpl.cs:42`, `RefactoringSignatureImpl.cs:46`, `RefactoringStructuralImpl.cs:54`, `WriteChokepointGuardrailTests.cs:20`) only name it as a return type.
  - `ValidateAndApplyHelper`: add `public static async Task<string?> StoreChangedContentAsync(IReadOnlyDictionary<FilePathWrapper, string> changes, string? solutionRoot, ILogger logger, CancellationToken cancellationToken)`: returns null when `changes.Count == 0`; builds `Dictionary<string, string>` keyed by `(string)key` (the same cast used at line 36), serialises with `System.Text.Json.JsonSerializer.Serialize(dict, SharedJsonOptions.Compact)`, calls `LargeResultHelper.StoreRawJsonAsync(json, solutionRoot, cancellationToken)` and returns `stored.resultId` when `stored.offloaded`, else null; wraps the whole body in `try/catch (Exception ex)` that logs a warning and returns null (a guardrail must never break the call it describes).
  - In `ValidateAndApplyAsync`, after the `blob` is written (after line 117) and before `if (blob.IsIntegrityFailure)`, add
    `var contentResultId = ChangedContentOptions.InlineOnApply || applyResult.SucceededFiles.Count == 0 ? null : await StoreChangedContentAsync(changes, workspaceManager.GetSolutionRoot(), logger, cancellationToken);`
    and pass `ChangedContentResultId: contentResultId` as a named argument in the two returns where files landed: the integrity-failure return (line 134) and the final success return (line 156). Do NOT pass it on the no-files-written return (line 149) or the dry-run return (line 94).
  - Tests (`ChangedContentOffloadTests`, NUnit): `StoreChangedContentAsync_WritesRawLargeResultFile_AndReturnsId` (temp dir as `solutionRoot`; assert the file exists under `.roslynsentinel/largeresults`, matches `largeresult_*.json`, wrapper `type` is `Raw`, and the stored JSON round-trips both paths and texts); `StoreChangedContentAsync_NoSolutionRoot_ReturnsNull`; `StoreChangedContentAsync_EmptyChanges_ReturnsNull`. If the existing `FakeWorkspaceManager` (`RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`) can supply a temp solution root, add one end-to-end `ValidateAndApplyAsync_RealApply_SetsChangedContentResultId`, modelled on `WriteChokepointGuardrailTests.ApplyAsync` (line 20); if it cannot, say so in the reply instead of inventing a fixture.
- Done when: `Build` has 0 errors and `RunTest` project `RoslynSentinel.Tests.Basic`, filter `FullyQualifiedName~ChangedContentOffloadTests`, passes. Also run `WriteChokepointGuardrailTests` (unchanged, must stay green).

### Step 3 - Site edits: Advanced, part 1
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Advanced\AdvancedRefactoringTools.cs`
- Symbols: the `new AppliedChangeSummary(...)` expressions ending `ChangedContent: <x>, Validated: true` at lines 165, 368, 546, 856, 901, 942 and 1276 (seven; confirm each with `Search(text, "Validated: true")` in the file; use the apply variable in scope at that site, for example `apply`, `partialApply`). Pattern at 165: `new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), summaryNote, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true)` becomes `... Validated: true, LineChanges: apply.LineChanges, ChangedContentResultId: apply.ChangedContentResultId)`.
- Tool: `ReplaceSnippet` with `batchEdits` (seven edits, each anchored on its unique surrounding variable names).
- Done when: `Build` has 0 errors.

### Step 4 - Site edits: Advanced, part 2, and GenerationTools
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Advanced\AdvancedRefactoringTools.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Advanced\GenerationTools.cs`
- Symbols: `AdvancedRefactoringTools.cs` lines 1320, 1364, 1409, 1453, 1497, 1563, 1603 (seven edits, same rule). `GenerationTools.cs:263` (`GenerateMapping`) builds a summary from `apply` without `ChangedContent`; add only `ChangedContentResultId: apply.ChangedContentResultId` (and `LineChanges: apply.LineChanges` if it is not already passed), because `ValidateAndApplyHelper` now writes a content file for every real apply, including this one, and an unreferenced file is pure waste.
- Done when: `Build` has 0 errors.

### Step 5 - Site edits: Extraction/Docs and Signature
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\RefactoringExtractionDocsImpl.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\RefactoringSignatureImpl.cs`
- Symbols:
  - `RefactoringExtractionDocsImpl`: lines 133 (`UsingDirective` remove), 147 (`UsingDirective` add), 226 (`SummaryComment`), 363 (`ExtractMethodSafe`); also line 283 (builds a summary from `apply` without `ChangedContent`; add `ChangedContentResultId` only, same reason as `GenerationTools.cs:263`). At line 147 keep `apply.Diff` as is: the add path deliberately returns the real diff (comment at lines 138-146).
  - `RefactoringSignatureImpl`: lines 242 (`MethodSignature`), 311 (`ChangeAccessibility`), 462 (`ConstructorParameter`); confirm the variable name at 311, which was not read in full.
  - In `RefactoringExtractionDocsImpl` (comment above `needsDiff`, about line 118) replace the sentence "the add path below needs the real before/after diff to populate ChangedContent" with "the add path below returns the real before/after diff".
- Tool: `ReplaceSnippet` with `batchEdits` (about nine edits).
- Done when: `Build` has 0 errors.

### Step 6 - Site edits: Structural (excluding Member)
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\RefactoringStructuralImpl.cs`
- Symbols: lines 946 (`ModifyEnum`), 1072 (`ModifyAttribute`), 1202 (`ModifyModifier`), 1308 (`ModifyBaseType`). Line 112 already passes `LineChanges`; add only `ChangedContentResultId: apply.ChangedContentResultId` there. Do NOT edit 729, 802, 883 (`Member`): `plan_mutating_and_test_tool_result_noise.md` Step 1 owns them. Also the other real-apply summaries that never echo (252, 410, 501, 575, 601, 643, 655, 1361, 1366) get no edit: they do not use `ChangedContent`, and the unreferenced content file they now cause is accepted for this step (see Risks). Fix the comments at 944, 1199, 1305 that say "No ChangedContent": reword to "ChangedContent is dropped centrally by AppliedChangeSummary for a real apply".
- Done when: `Build` has 0 errors.

### Step 7 - Regression tests through a real tool
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\CompactResultDefaultTests.cs` (new)
- Change: model on `RoslynSentinel.Tests.Tools.Basic/MemberSingleDeclarationTests.cs` (`InMemoryWorkspace.Create(...)`, `BuildTools(workspace.Manager)`). Fixture: one class of about 150 lines. Tests:
  - `ModifyModifier_Applied_ResultHasNoChangedContentHasLineChangesAndNoteAsync` - `ChangedContent` null, `LineChanges` non-empty, `result.LargeResult` null, `Note` contains `ReadFile`.
  - `ModifyModifier_DryRun_StillReturnsChangedContentAsync`.
  - `ModifyModifier_NoStage_StillReturnsChangedContentAsync` (`autoStage: false`).
  - `ModifyModifier_InlineOptionOn_ReturnsChangedContentAsync` (`[NonParallelizable]`, option restored).
  - If the in-memory workspace exposes a solution root (see Step 2): `ModifyModifier_Applied_ResultIdFetchesUpdatedTextViaGetLargeResultAsync` - the id in `ChangedContentResultId` resolves with `GetLargeResult` and the paged `text` contains the new modifier. If there is no root, skip and say so.
- Done when: `RunTest` project `RoslynSentinel.Tests.Tools.Basic`, filter `FullyQualifiedName~CompactResultDefaultTests`, passes.

### Step 8 - Verification
- `Build` (0 errors).
- `RunTest` at solution scope; compare against the known-failure baseline (`reference_known_failing_tests`); report only new failures. Run `StructuredContentDataTagTests` by name and `ArchitectureDocFreshnessTests` (no description or schema change is expected; if it fails, regenerate with `scripts/Generate-ArchitectureMap.ps1`).
- `McpServerControl` stop (confirm guard), reconnect, `LoadSolution`, then live: one `ModifyModifier` (or `Member(addMember)` once the noise plan's Step 1 has landed) on a scratch file returns `lineChanges`, `changedContentResultId`, a `note` naming `GetLargeResult` and `ReadFile`, and no `changedContent`; `GetLargeResult(resultId: ...)` returns the updated text; a `dryRun: true` call still returns `changedContent`; the large-result directory gained exactly one file for the apply and none for the dry run.
- Proposed `docs/current/TODO.md` edit (for the human to apply; this plan does not touch TODO.md): the entry at lines 336-349, "Mutating tools don't return the resulting content, forcing a separate `ReadFile` to see the outcome" (Found 2026-08-19/20), should be closed and moved to `CLOSED.md` as "Resolved by design decision 2026-10-08: mutating tools do not echo content on a real apply; the result carries lineChanges, a resultId for the stored content and a read-back hint; inline echo is a server-wide opt-in (`ROSLYNSENTINEL_INLINE_CHANGED_CONTENT`), pending an impact review (`plan_mutating_tools_compact_result_default.md`)".

### Step 9 (OPTIONAL, fold in only if the human approves) - Stop saying "applied" / "Written to disk" when nothing was written
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\AppliedChangeSummary.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Basic\AppliedChangeSummaryTests.cs`
- Defect (traced in `AppliedChangeSummary.cs`): a no-stage result (`autoStage: false`: `DryRun: false`, `ChangeId: null`, non-empty `AffectedFiles`, `Validated: false`) falls through `Status` to `"applied"` and through `Note` to "Written to disk, but NOT reversible: the server could not record an undo entry ...", although nothing was written. The agent is told its edit landed when it did not, and the real next action (apply the supplied content) is not named.
- Discriminator (CHANGED from a naive `!Validated`): about 35 real-apply sites leave `Validated` at its default `false` (see Problem), so `!Validated` alone would flip real applies to "not_written". A no-stage summary is the one with `!DryRun && !Validated && ChangedContent != null && ChangeId == null`. Use exactly that expression in one private bool property, `NotWritten`.
- Change: `Status` returns `"not_written"` when `NotWritten` (before the `AffectedFiles.Count == 0` / `"applied"` arm); `Note` returns, when `NotWritten`: `"Nothing was written (autoStage=false). The proposed file contents are in changedContent; apply them yourself, or re-call with autoStage: true to have the server apply and validate the change."` Update the `Status` doc comment. Tests: `NoStage_StatusIsNotWrittenAndNoteNamesChangedContent`, `RealApply_ValidatedFalse_ChangedContentNull_StaysApplied` (guards the discriminator), `RealApply_StatusStaysApplied`.
- Edge: a no-stage site that passes `ChangedContent: null` for an empty set (`RefactoringStructuralImpl.cs:1060`) still reports "applied"; acceptable, and noted.
- Done when: the named tests pass and `Build` has 0 errors; also run `StructuredContentDataTagTests` (asserts `"applied"` for a real apply).
- External risk: clients that parse `status` see a new value `not_written`; no in-repo test asserts the old no-stage value.

## Out of scope

- Removing the `ChangedContent:` named arguments from the applied sites (inert after Step 1; cosmetic cleanup for a later pass).
- A per-call parameter to inline content, and a `--inline-changed-content` startup argument (the env var plus the settable property cover the need now).
- Retention or cleanup of `.roslynsentinel/largeresults/` (1,092 files / 128.1 MB today, nothing deletes them). *Needs design* (age vs count vs size, per-session scoping).
- A typed `ResultWrapperType` for changed content with a `GetLargeResult` branch that pages one file's raw text; the `Raw` JSON-escaped window is used for now (see Risks).
- The `ApplyChangesResult` / `ReplaceSnippetResult` family (already compact); `MemberChangedContentResult` (`LargeResultHelper.cs:159`, retired wrapper).
- `Member` add sites 729/802/883: `plan_mutating_and_test_tool_result_noise.md` Step 1.
- `UsingDirective` add keeping its unconditional `Diff` (deliberate, see Step 5).
- `RunTest` / test-tool noise: `plan_mutating_and_test_tool_result_noise.md`.

## Risks and open decisions

- *Decision for the human, the main tradeoff:* is the offload worth its cost? After a real apply the new text is already on disk, so `ReadFile` (with a line range) retrieves it for free and pages better than `GetLargeResult` on a JSON-escaped string. The offload buys a stable id for "what exactly did the tool write, as the tool saw it" and honours the instruction that the value stay retrievable. Its price: one serialisation plus one file write per successful mutation, content sized as the sum of all changed files (a multi-file rename or `MoveMember` with call-site fixups can write megabytes), on top of the apply blob that is already written per mutation (pre-images only, so it cannot be reused). The previous draft of this plan simply dropped the content; Step 2 can be cut and the plan stays valid (the `Note` falls back to the `ReadFile` hint).
- *Decision for the human:* opt-in shape. Chosen: one server-wide switch (env var), zero schema growth, no per-call control for the model. Alternatives: (a) a `returnChangedContent` parameter on each of ~40 tools (about 50 tokens each, so roughly 2k tokens of schema per session, an estimate not measured; the `returnDiff` parameter via `ToolParams.ReturnDiff` / `[ToolOption(ToolOptionTag.ReturnDiff)]` is the precedent and already lets a caller ask for an inline diff on the same call); (b) inject one shared parameter through `Common/McpToolSchemaPatcher.cs` (*needs design*). If per-call control is wanted later, (a) can be added on top of the same central gate without undoing anything here.
- Unreferenced files: `ValidateAndApplyHelper` writes the content file for every caller, but only sites that copy `apply.ChangedContentResultId` into their summary expose it. This plan wires the 29 echo sites plus `GenerationTools.cs:263` and `RefactoringExtractionDocsImpl.cs:283`; the other ~33 real-apply summaries (listed in Step 6 and Problem) do not, so each of their mutations leaves an unreachable file. Cheap mitigation if that matters: give the helper an `offloadChangedContent` flag, default false, passed only by wrappers whose tools expose the id; this was not planned because it touches the five wrapper methods (`AdvancedRefactoringTools.cs:95`, `GenerationTools.cs:60`, and the three `Impl` classes). Alternatively wire the remaining sites in a later pass.
- `Raw` read-back is awkward: `GetLargeResult` returns a JSON-escaped character window (about 7,400 chars per page, `charLimit`/`offset`), so a 40 KB file takes about six pages of escaped text. Acceptable as an escape hatch; the `Note` points at `ReadFile` first for exactly this reason.
- The explicit-property-in-positional-record technique is language-specified, but the self-named initializer reference was not compiled by this planner; Step 1 has a stop-and-RESCOPE clause. `GetLargeResult`'s `AppliedChangeSummaryResult` branch deserialises into `AppliedChangeSummary` (`WorkspaceReadNavigationImpl.cs` around line 1225), so the gate also applies on read-back of any old stored summary that contained content; after this change summaries are small and stay inline, so this only affects files written earlier.
- Gate rule `Validated && !DryRun` relies on every echo site setting `Validated: true`; a future tool that forgets it leaks the echo again (fails open, the safer direction for a first rollout).
- JSON clients that read `changedContent` after a real apply break by design; the env var restores it. No in-repo reader exists beyond the two no-stage tests above; external readers are unknown.
- Step 9's discriminator relies on no-stage sites passing a non-null `ChangedContent`; verified for the sites listed in Problem by their first lines only, so the implementer must open each before trusting it.


## Implementation notes (2026-10-08)

Commits: 8c987c9 (central gate, offload, wiring in Advanced/Generation/Structural, tests), 2ac8461 (Extraction/Signature wiring), e198987 (Step 9 not_written plus tool-level tests). Full solution RunTest after all work: 3405 tests, 0 failed, 26 skipped.

Deviations:
- ChangedContent keeps [JsonIgnore(WhenWritingNull)] (not an unconditional ignore) so dry-run and no-stage JSON still emits changedContent; serialization tests added.
- ChangedContentResultId and LineChanges were wired into ALL real-apply sites (Advanced 27 sites, not the 14 listed; Structural 17; ExtractionDocs; Signature; GenerationTools.GenerateMapping). Not wired: WorkspaceProjectManagementImpl.SafeDeleteUnusedSymbol (calls ApplyProposedChangesAsync directly with a local changeId; no content file is written).
- NotEchoedHint shows the GetLargeResult hint whenever a result id exists, even when Validated is false, because the non-echo real-apply sites expose an id without Validated: true. The Note names GetLargeResult first, then ReadFile (plan said ReadFile first).
- Step 9 (not_written) was folded in with the plan's discriminator.
- ChangedContentOptions.cs was created with LF line endings (repo uses CRLF).
- Retention/cleanup of .roslynsentinel/largeresults is untouched: 1112 files / 128.9 MB at completion.
- StructuredContentDataTagTests is skipped in the current environment (4 skipped); it was not exercised.
- Live verification (Step 8 server restart) is left to the parent; the running server was stale throughout.
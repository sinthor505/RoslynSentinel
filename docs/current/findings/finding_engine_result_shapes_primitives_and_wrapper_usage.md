# Finding: about a quarter of engine methods return primitive or untyped shapes, and EngineResultWrapper<T> is barely used

**Status:** DECISION 2026-10-16. "Status interface as the standard" (called "hybrid" below): every engine result implements `IResultStatus`, renamed to `IOperationResult`; DTOs carry structure; primitives that fail throw coded exceptions; the wrapper is removed (rule set below). It feeds Phase 2 of `proposals/proposal_dto_ownership_role_lineage.md`.

**Rename decided 2026-10-16:** `IResultStatus` -> `IOperationResult`. `IEngineResult` was rejected: of the 73 implementers, about 30 are not engine results. They include 22 in Tools.Basic (15 `Git*Result`, the `Doc*Result` types) and the tool response wrappers in Common (`SentinelCallToolResult<T>` and `<TSuccess, TError>`, `MigrationEnvelope<T>`, `AppliedChangeSummary`). A bare `IResult` would collide with `Microsoft.AspNetCore.Http.IResult`. The `ResultStatus` enum keeps its name.

## Context
Raised during slice 4d of the snippet-matching migration (`proposals/proposal_internal_whitespace_tolerance_contract_for_snippet_helpers.md`). `ResolveBySnippetOrThrow` (`Engines.Basic/SyntaxTargetResolver.cs`) needs to tell a caller "the snippet matched nothing, but the edit proceeded on the name alone" (decision 2026-10-14: single candidate with a non-matching snippet proceeds with a warning). No warning channel is reachable from the helper. `IResultStatus` (`Common/IResultStatus.cs`, adopted per `plans/plan_shared_result_status_interface.md`) has `IsError`, `Status`, `Code` and `Message`, where `Message` is documented as a failure explanation. The only warning field is `SentinelCallToolResult.WarningDetails` (`Common/SentinelCallToolResult.cs:249`), set by hand in `Tools.Basic`.

That led to a measurement of how engine methods return results (read-only agent, 2026-10-14) and a discussion of whether `EngineResultWrapper<T>` or per-method DTOs should be the long-term standard. Counts come from signature searches via `Search(mode: text)` and are good to within a few; `GetFileOutline` does not return return types. Neither engine project declares an interface.

## What is broken

### 1. Counts by return shape
About 475 engine API methods (513 raw, minus 37 `Visit*` overrides and 1 `ToString`; Basic 277, Advanced 236). About 116 return a primitive or untyped shape; 189 return a status-bearing type; none returns `object`.

| Shape | Basic | Advanced | Total |
| --- | --- | --- | --- |
| `DocumentEditResult` (`IResultStatus` via `EngineResultBase`) | 71 | 77 | 148 |
| Other `IResultStatus` implementers | 24 | 10 | 34 |
| `EngineResultWrapper<T>` | 6 | 1 | 7 |
| **Status-bearing subtotal** | 101 | 88 | **189** |
| string / `Task<string>` / `Task<string?>` | 35 | 0 | 35 |
| bool / `Task<bool>` | 9 | 0 | 9 |
| int | 3 | 0 | 3 |
| Tuples | 12 | 6 | 18 |
| `List<string>` / `IReadOnlyList<string>` | 10 | 21 | 31 |
| File-to-text dictionaries (no error channel) | 7 | 8 | 15 |
| void / Task | 5 | 0 | 5 |
| `object` | 0 | 0 | 0 |
| **Untyped subtotal** | 81 | 35 | **116** |
| Lists of plain DTOs (no `IResultStatus`) | 39 | 72 | 111 |
| Single DTOs or Roslyn types without `IResultStatus` | ~45 | ~15 | ~60 (derived by subtraction) |

The literal list (string, bool, int, tuples, string collections) is 96; the other 20 are dictionaries and void/Task. Wrapped (multi-line) signatures were searched only in Advanced (5, all in `AsyncOptimizationEngine.cs`: :1409, :1928, :2915, :3215, :3455).

`EngineResultWrapper<T>` users: Basic `BuildEngine.cs:40,180`, `DiagnosticEngine.cs:32,47,86`, `TestRunEngine.cs:89`; Advanced `LogicOptimizationEngine.cs:653`. The `T` at each site was not checked. `BuildResult` and `TestRunResult` implement `IResultStatus` (per the status plan), so those are likely double-enveloped (hypothesis).

Tuples:
- Basic: `GetSummaryCommentAsync` (`BasicRefactoringEngine.cs:1903`), `GetConstructorParametersAsync` (`MemberRefactoringEngine.cs:622`), `GetMethodParametersAsync` (:1307), `GetContainerMembersAsync` (`SymbolNavigationEngine.cs:2523`), `GetMemberSourceAsync` (:2609), `ResolveBoolMemberAsync` (`SemanticReplaceEngine.cs:26`, :36), `CollectSitesAsync` (:455), `PlanInvertBooleanAsync` (:1232), `ParseDiagnostics` (`BuildEngine.cs:146`), `AddInstanceMoveStructuralEditsAsync` (`MemberRefactoringEngine.cs:3844`), `Match` (:108).
- Advanced: `CommentingEngine` :68 and :299, plus 4 in `AsyncOptimizationEngine`.

### 2. The string group (Basic only)
About 20 of the 35 are pure renders or helpers (text builders, indentation, `ErrorCodeFor`, `RenameSymbolResult.ToToolResponse`, `CompilerErrorLookupHelper.DescribeAsync`, `MoveMemberTextEdits.*`, and similar) and need no richer type. The ones that can fail or signal:

- `AttributeTextEditBuilder.TryApply` (:101): `string?` plus `out error`.
- `MemberRefactoringEngine.DetectMultipleMemberDeclarations` (:2632): a non-null return is an error message.
- `StaticConversionEdits.GetIneligibilityReason` (:37): a non-null return is the reason.
- `TestRunEngine.DetectFileLock` (:286): a non-null return signals a lock.
- `SymbolNavigationEngine.GetEnclosingTypeNameAsync` (:1151): null means not found.
- `SymbolNavigationEngine.TryGetEnumMemberContainerNameAsync` (:2434): null means not found or ambiguous; catches `ToolException` and drops it (:2457). `IsEnumContainerAsync` (:2497) does the same and returns false.
- `SyntaxTargetResolver.GetMemberName` (:158): null when unnamed.
- `SemanticRefactoringEngine.InlineVariableAsync` (:50): **silent failures (verified).** It returns `""` for a missing document or root and the unchanged `root.ToFullString()` for every no-op (no initializer, no declaration statement, no containing body, more than one declarator), and swallows per-usage replacement and removal exceptions. A failure is indistinguishable from unchanged text. Its tool caller (`AdvancedRefactoringTools.cs:1123`) treats only `string.IsNullOrEmpty` as failure (mapped to `NotFound`), so the unchanged-text failures go on to `ValidateAndApplyAsync` as success.

Classification of the rest is from names and signatures, not full bodies.

### 3. The 35 `ResolveBySnippetOrThrow` call sites
`SyntaxTargetResolver.cs:293-379`, returns `SyntaxNodeCandidate?`. 35 sites in 32 methods (34 in Engines.Basic, 1 in Engines.Advanced). It throws `ToolNotFoundException` or `ToolAmbiguousMatchException`; callers catch `ToolException` and copy `Message` and `ErrorCode` into their own result.

| Return shape | Sites | Methods |
| --- | --- | --- |
| `Task<DocumentEditResult>` | 26 | 23 |
| Tuple `(EditOutcome, string? Message, ...)` | 5 | 5 |
| Private tuple `(ClassDeclarationSyntax?, DocumentEditResult?)` | 1 | 1 |
| Typed DTO without `IResultStatus` (`StaticConversionResult`) | 1 | 1 |
| `Task<string?>` (swallows the exception) | 1 | 1 |
| `Task<bool>` (swallows the exception) | 1 | 1 |

No site sits in a `Task<string>` method. So `Warnings` on `EngineResultBase` would cover 26 of 35 sites.

### 4. No common path from an engine result to the tool response
Each Tools method maps by hand; nothing consumes `IResultStatus` generically (text search of `Server.*`, `Tools.*` and `Common` found it only on implementer declarations; a 150-hit reference listing was not paged, so a hidden consumer is unverified).

- `DocumentEditResult` tools use three helpers in `Engines.Basic/RefactoringToolHelpers.cs` (MCP-shaped results in the engine layer): `RequireUpdatedText` (:64, 13 sites in `Tools.Basic`), `ToSummaryResult` (:95, 5 sites; copies only `IsError`, `ErrorData`, `SuccessData`, `StatusMessage` and drops `Findings` and `WarningDetails`), `GuardEngineResult` (:129, 19 sites, all `Tools.Experimental/CodeTransformTools.cs`; sets neither `Findings` nor `WarningDetails`). Some paths bypass them, for example the `autoStage=false` branch of `ModifyEnum` (`RefactoringStructuralImpl.cs:932-949`).
- `EngineResultWrapper<T>`: `WorkspaceBuildTestImpl.GetDiagnostics` (:35-108) reads `result.Data`, which throws on failure; the failure is caught and mapped by `ToolErrorMapper.ToResultError`. `Findings = result.Findings` is copied by hand at :98, :108, :275, :278.
- Tuples are hand-mapped. `SummaryComment` (`RefactoringExtractionDocsImpl.cs:177-181`) turns only `DocumentNotFound` and `CannotEdit` into an error, hard-codes `ToolErrorCode.Exception`, and lets other outcomes through as success.
- `WarningDetails` is set only in `Tools.Basic`, from tool-local conditions (`SymbolNavigationImpl.cs:213`, `SymbolRelationshipImpl.cs:107,116`, `WorkspaceReadNavigationImpl.cs` :433, :656 and many in `GetLargeResult`). None is fed from an engine result. `Tools.Advanced` and `Tools.Experimental` never set it. `WirePathRewriter.cs:22` already lists `"warningDetails"`.
- The server filters (`AddToolErrorFilter`, `ServiceRegistrationExtensionsBasic.cs:440-490`; `AddLargeResultOffloadFilter`, :547-686) see only serialized JSON and copy the top-level `isError`; nothing there reads `warningDetails`.

## Root cause
Not a single defect. `plans/plan_shared_result_status_interface.md` standardised the status vocabulary, but status-bearing and untyped returns coexist because engines grew their own return shapes per method, and `IResultStatus` has no member for "succeeded with a caveat". Warnings and findings reach the response only where a tool author set them by hand.

## Why it matters
Per the failure doctrine, a model sees only the tool response. When an engine helper has something to say on a success (a snippet that matched nothing but the edit proceeded; candidate listings) there is nowhere to put it, so it is lost, and the audit goal (reviewers see where the model was mistaken) is unmet. Primitive-returning methods such as `InlineVariableAsync` report failure as success outright.

## Recommendation
Discussed 2026-10-14. **Nothing below is decided; the user has not yet chosen a direction.**

### Warnings member (leaning agreed in discussion)
Add `Warnings` to the `IResultStatus` interface (a default interface member returning null, which the status proposal already permits), not to `EngineResultBase`. `EngineResultBase` only supplies storage (a settable list) for the `DocumentEditResult` family. The boundary reads `result is IResultStatus { Warnings: { Count: > 0 } }`, as it does for `IsError`. A separate `IHasWarnings` interface was rejected (two checks, and every type that needs warnings is status-carrying). `Guidance` stays a separate idea until something needs it. Rolling out: no constructor parameter on adopters (about 82 types, most never warn); use a default member. For the 35 call sites use a compiler-driven migration: a new helper returning the candidate plus warnings, the old method kept as an `[Obsolete]` shim that delegates and drops the warning (the warnings list is the worklist), migrate by file, delete the shim last. Making a parameter required then adding a default loses the compiler worklist; snapshot it or use the shim. The realistic drain is copying `Warnings` into `WarningDetails` in the three `RefactoringToolHelpers` helpers; tools that build their envelope inline need their own copy.

### Wrapper versus DTOs (open)
- **Wrapper for everything.** Pros: one shape and one place for status, `Findings` and `Warnings`; cheap for the 116 untyped methods; one error vocabulary. Cons: `Data` throws on failure (callers must check first; the tool layer catches and re-maps); `EngineError`'s six outcomes are coarser than `EditOutcome`'s 13; nesting changes wire shapes models see; a DTO that already implements `IResultStatus` gets two envelopes. It would also regress the 148 `DocumentEditResult` methods.
- **A DTO per method.** Pros: full fidelity (named fields, domain outcomes, data that survives failure), purpose-built response schemas. Cons: about 82 types each needing `IResultStatus` plumbing and tests; vocabularies can drift again; a type for one string field.
- **Recommended: hybrid.** `IResultStatus` is the standard, and the wrapper and DTOs are two implementers. Existing `DocumentEditResult` methods stay. A method with two or more named fields or a domain outcome gets a DTO implementing `IResultStatus` (do not also wrap it). A non-throwing read on the wrapper (a `TryGetData` or nullable `Value`) should come first if the wrapper stays; not checked whether one exists.

### Should the wrapper be removed where it wraps a DTO? (user question, 2026-10-14)
Probably yes where `T` already implements `IResultStatus`; it adds only indirection. The user's premise is that part of the wrapper's motivation was unified structured content, which has been abandoned (user statement; not verified in docs). Only 7 methods use the wrapper, so the cleanup touches at most 7 sites, and `T` at each is unchecked. If structured content is gone, the wrapper's remaining job is narrow: carrying `Findings` (and later `Warnings`) next to a payload with no status of its own, for example the diagnostics list. Once `Findings` and `Warnings` live on `IResultStatus`, DTOs carry them themselves.

This weakens an earlier suggestion in the same discussion (wrap primitive and collection returns in `EngineResultWrapper<T>`). Alternatives for primitive-returning methods:

| Option | Fit |
| --- | --- |
| Keep a slimmed wrapper for primitives and collections only | Uniform; still needed where `Findings` or `Warnings` accompany a list. One extra type. |
| Throw a coded `ToolException` | Matches the existing pattern (`ResolveBySnippetOrThrow`, mapped at the boundary by `ToolErrorMapper`). Simplest for rarely-warning methods like `DetectFileLock`; nothing wrapped. |
| A small DTO per method | Highest fidelity; a type for one string field. |

### Proposed rule set (not yet adopted)
1. Remove the wrapper from methods whose `T` implements `IResultStatus`, after a read-only check of the 7 sites.
2. Put `Warnings` and `Findings` on `IResultStatus` (default members).
3. For primitives that can fail, prefer a coded `ToolException`. Fix `InlineVariableAsync` first so its silent failures become real errors.
4. Keep a slimmed wrapper only where a payload with no status still needs `Findings` or `Warnings`; if the diagnostics list is the only case, replace it with a small `DiagnosticReport` DTO (an `IResultStatus` implementer by that name exists) and delete the wrapper.
5. Give each of the 18 tuple methods a small DTO.

Wrapping all 116 untyped methods is not recommended: most are internal helpers. Wrap or convert only the failure-signalling ones (about 8 strings, the 2 swallowers, the 18 tuples), as its own plan.

Next: a read-only check of the 7 wrapper sites, then fold the decision into the warnings proposal. Slice 4d's single-candidate pass-through is a separate step and does not depend on this.

## Wrapper check result (read-only, 2026-10-14)
Verified against source by a read-only agent after the discussion above. Line numbers are the agent's and unconfirmed by me.

| # | Method | T | T is `IResultStatus` |
| --- | --- | --- | --- |
| 1 | `BuildEngine.RunQuickBuildAsync` (:40) | `BuildResult` | yes (`Common/BuildResult.cs:7`) |
| 2 | `BuildEngine.RunFullBuildAsync` (:180) | `BuildResult` | yes |
| 3-5 | `DiagnosticEngine.GetFile/Project/SolutionDiagnosticsAsync` (:32, :47, :86) | `DiagnosticSummary(Errors, Warnings, Details, BuildVerification)` | no |
| 6 | `TestRunEngine.RunAsync` (:89) | `TestRunResult` | yes (`TestRunEngine.cs:43,57`) |
| 7 | `LogicSimplificationEngine.InvertBooleanLogicAsync` (`LogicOptimizationEngine.cs:653`) | `List<DocumentEditResult>` | list: no |

- The hypothesis holds for methods 1, 2 and 6 (double-enveloped). Methods 3-5 never fail through the wrapper: every return is `Success`, and failures are thrown as `ToolNotFoundException` (`DiagnosticEngine.cs:35,50`), so `.Data` never throws there. The "diagnostics list" type is `DiagnosticSummary`, not a `DiagnosticReport`.
- Wrapper facts: `Data` throws `InvalidOperationException` when not Success (`EngineResultWrapper.cs:41`). A non-throwing `TryGetData` already exists (:29) and the tools use it. `EngineOutcome` has seven values (Success plus six failures); `Timeout` and `InternalError` are never constructed by engines. `Findings` is never set by any engine, so the tool copies at `WorkspaceBuildTestImpl.cs` copy an empty list. `EngineError` is also used outside the wrapper (`PersistentWorkspaceManager.cs:2292`, `DiscoveryEngine.cs:543`).
- **Defect:** method 7's only caller (`Tools.Advanced/ModernizationTools.cs:48-53`) sets `SuccessData = result` (the whole wrapper) and `IsError = false`, so `DocumentNotFound` and `TargetNotFound` outcomes (`LogicOptimizationEngine.cs:660,682`) go out as tool successes. Whether serialising a failed wrapper throws (via `Data`) is a hypothesis, not traced.
- **Defect:** `GetDiagnostics` and `GetWorkspaceHealth` read the build wrapper with `TryGetData` and silently drop a failure (`WorkspaceBuildTestImpl.cs:87-89`, `WorkspaceHealthMiscImpl.cs:143-146`).
- `TestRunResult.IsError` is stricter than the tool's MCP `isError` (a completed run with failing tests is `IsError == true`; `RunTest` sets `isError` only when `!RunCompleted`). A generic boundary check on `IResultStatus.IsError` would change behaviour.
- Cheapest removal: methods 1, 2 and 6 return their own type, with a `NotRun`/not-completed factory (both constructors take 11-13 positional arguments), and `BuildResult.NotRun` maps to `ToolErrorCode.Exception`, losing `EngineErrorCode.BuildNotRun`. Methods 3-5 return `DiagnosticSummary` directly (about 15 test call sites; no new DTO needed). Method 7 is the one real failure-carrying case and needs a boundary fix (throw not-found as `DiagnosticEngine` does, or map the outcome in `ModernizationTools`); if left alone the wrapper survives with that one user. Tests asserting `Outcome == InvalidInput` and `Error.Code == BuildNotRun`: `BuildEngineTests.cs:23-25,40-42`.
- Not checked: `BuildEngine.cs:262-347` bodies, what `ToolErrorMapper` does with `InvalidOperationException`, tests reading `.Data`/`.Findings`, and `Tools.Experimental`/`Server.*` users (a by-name solution search found none).

## Out of scope
- Payload-only types and anonymous or untyped tool return shapes (`finding_anonymous_and_untyped_return_shapes.md`).
- The `Tools.*` layer beyond its conversion code; test projects were not counted.
- Reorganising `ToolErrorCode` (`project_tool_error_code_taxonomy_proposal`).

## Not verified
- Counts are regex-based, good to within a few; wrapped signatures were searched only in Advanced.
- Failure behaviour of the string methods other than those named is inferred from names and signatures.
- The `T` at the 7 wrapper sites; whether `EngineResultWrapper<T>` has a non-throwing read; the 150-hit `IResultStatus` reference listing; `Tools.Advanced` conversion paths beyond `Inline`; the claim that structured content was abandoned.

## Related
- `plans/plan_shared_result_status_interface.md`, `proposals/proposal_shared_result_status_interface.md`, `findings/finding_result_status_vocabularies_fragmented.md`.
- `proposals/proposal_internal_whitespace_tolerance_contract_for_snippet_helpers.md` (slice 4d motivated this).
- `findings/finding_anonymous_and_untyped_return_shapes.md`.

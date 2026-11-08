# Proposal: decompose CodemodTools onto the validate-then-write contract

## Motivation

`RoslynSentinel.Server.Advanced/CodemodTools.cs` (1,759 lines) predates the validate-then-write
pattern. It was written when every tool returned the full rewritten file for the model to apply
itself. The rest of the tool surface later moved to a single chokepoint:

- validate the proposed changes against the in-memory solution
  (`ValidationEngine.ValidateChangesAsync`),
- write through `IWorkspaceManager.ApplyProposedChangesAsync`, with rollback on partial multi-file
  failure,
- expose `autoStage` / `dryRun` / `returnDiff` to the caller and return an `AppliedChangeSummary`.

All of that lives in `ValidateAndApplyHelper.ValidateAndApplyAsync`
(`RoslynSentinel.Common/ValidateAndApplyHelper.cs:18`). CodemodTools never migrated. It has also
never been used in production, and none of its four entry points has a test. It is effectively
a 65-operation surface that has never run end to end.

For the target audience -- weak or self-hosted models -- the current shape is close to the worst
case. To persist any change, the model has to read back a whole file from the tool result and
re-emit it through `WriteFile` or `ApplyDiff`. That is exactly the transcription work the server
exists to take off the model.

This proposal does not fix CodemodTools in place. It moves each transform to the tool family
where an agent would look for it, on the same contract as its neighbours, and drops the
transforms that are duplicates or too opinionated to be safe defaults.

## Current state

### Surface

Four tools, each a string- or enum-dispatched switch:

| Tool | Lines | Dispatch | Cases |
| --- | --- | --- | --- |
| `ApplyFileCodemod` | 74-531 | `transform` (string) | 25 |
| `ApplyMethodCodemod` | 534-909 | `transform` (string) | 17 |
| `ApplyClassCodemod` | 923-1246 | `transform` (string) | 13 |
| `Generate` | 1249-1581 | `kind` (`CodemodKind` enum) | 10 |

Valid `transform` values are listed by `ApplyFileCodemodOptions` / `ApplyMethodCodemodOptions` /
`ApplyClassCodemodOptions` (lines 1583, 1651, 1712), and surfaced through
`QualityTools.DescribeAdvancedToolOptions` (`QualityTools.cs:65-67`).

Registration: `ServiceRegistrationExtensionsAdvanced.cs:218-219`. The Basic registration is
commented out (`ServiceRegistrationExtensionsBasic.cs:296-297`).

Gating: `ToolClassRegistry.cs:64-74` defines a special rule, `CodemodTriggerModes =
["Refactor","Modernize","Quality","Generation"]`, so the class is active in any of those modes.
The Claude mode lists its classes explicitly and does not include `CodemodTools`.

### Defects

1. **No write path.** Most cases return either
   `new SourceTransformResult(r.UpdatedText, false, false, filePath)` or `r.ToJsonSummary()`. Both
   carry the full rewritten file and neither writes it. `SourceTransformResult`'s doc comment
   (`RoslynSentinel.Common/SourceTransformResult.cs`) tells the caller to "pass UpdatedSource to
   `apply_proposed_changes`". No such tool exists. The same dangling name appears in
   `CancellationTokenResult.cs:12` and `AsyncOptimizationEngine.cs:383`.
2. **No validation.** Because nothing goes through `ValidateAndApplyAsync`, a transform that
   produces uncompilable code returns it as a success. The model only finds out after it has
   written the file itself.
3. **Cross-file breakage is invisible.** Several transforms change a signature or a type's shape
   but are dispatched against one file and return one file's text: `extension_to_static`,
   `make_method_static`, `record_to_class`, `convert_method_to_indexer`,
   `replace_constructor_with_factory` and `fix_mismatched_namespaces`. Callers in other files are
   not rewritten and, because there is no validation, not detected.
   *Hypothesis:* this is inferred from the tool-layer return path, not verified engine by engine.
   `DocumentEditResult` (`RoslynSentinel.Common/DocumentEditResult.cs:20`) does carry a
   multi-file `Changes` dictionary. Some engines may already populate it, and the tool layer then
   discards everything except `UpdatedText`. Checking this is step 1 of each migration below.
   `convert_out_params_to_value_tuple` is the one case known to return multi-file changes
   (`OutParamConversionResult`).
4. **Discovery dangles in three of four trigger modes.** The tool descriptions point to
   `DescribeAdvancedToolOptions`, which lives in `QualityTools` and is only active in Quality
   mode. In Refactor, Modernize and Generation modes, the model is told to call a tool it cannot
   see. The `transform` parameter is a free-form string, so the schema offers no help either.
5. **Conditional parameters.** `direction`, `propertyName`, `lockFieldName` (default `"_lock"`),
   `libraryMode`, `preview`, `framework`, `decoratorPrefix` and `disambiguateLine` each apply to a
   handful of cases and are silently ignored by the rest. The source already flags this with
   `CONDITIONAL-PARAM-REVIEW-REQUIRED` at lines 537, 926 and 1252.
   `convert_property_safe` and `convert_property_to_methods` fall back to `propertyName ?? className`,
   so omitting `propertyName` targets a property named after the class.
6. **Inconsistent result shapes.**
   - "Not found" and "not eligible" return `ToolErrorCode.Exception`, not a targeting code.
   - "Nothing to do" is `IsSuccess=true` with a string in some cases and `IsSuccess=false` in
     others.
   - `SuccessData` is variously a string, a `ToJsonSummary()` blob, a record, or a bare enum
     (`result3.Outcome` from `optimize_task_wait`).
7. **`format_document_safe` writes by default.**
   - `MsToolAugmentEngine.FormatDocumentSafeAsync` (`MsToolAugmentEngine.cs:678`) defaults
     `preview = true`, but the tool's `preview` defaults to `false` and is passed through, so a
     no-argument call writes.
   - It also reads with `File.ReadAllTextAsync`, bypassing the workspace, and falls back to
     `File.WriteAllTextAsync` when no solution is loaded. Both bypass the write chokepoint.
   - `sort_and_deduplicate_usings` (`SortAndDeduplicateUsingsAsync`, `MsToolAugmentEngine.cs:577`)
     does go through `ApplyProposedChangesAsync`, but ignores the result. It also writes by
     default because the tool passes `!preview`.
8. **Dead weight in the constructor.**
   - The constructor (line 41) takes 27 parameters.
   - After the group 4/5 engine merges:
     - `_advancedLogicEngine` and `_codeFlowEngine` are both the same `LogicSimplificationEngine`
       as `_logicOptimizationEngine`, and are never used.
     - `_advancedRefactoringTools` is injected and never used.
     - Field names are stale; for example, `_advancedStructuralEngine` is a
       `StructuralRefactoringEngine`.
   - There is a leftover `// Added by InsertMemberBefore` comment near line 911.
9. **No tool-level tests.** There are zero calls to `ApplyFileCodemod(`, `ApplyMethodCodemod(`,
   `ApplyClassCodemod(` or `.Generate(CodemodKind` anywhere in the test projects. The engine
   methods themselves are well covered: a 17-method sample found 154 call sites across 29 test
   files. So the risk is concentrated in the tool layer this proposal replaces.
10. **Sole tool-layer caller.** For all 65 engine methods, CodemodTools is the only caller in
    `RoslynSentinel.Server*`. Removing it without re-homing the transforms removes the capability
    from the MCP surface entirely. The engine code and its tests stay.

### What the engines return

Most engine methods return `DocumentEditResult`. It carries an `EditOutcome` (`Modified`,
`NoChange`, `TargetNotFound`, `CannotConvert`, ...), `UpdatedText`, and a multi-file `Changes`
dictionary.

The generators return bespoke records instead: `RepositoryInterfaceResult`, `FluentBuilderResult`,
`TestSkeletonReport`, `TestScaffoldResult`, `PathDrivenTestReport` and `MsAugmentResult`. A few
analysis-style methods return preview records: `AddUsingsPreview` and `FormatPreviewResult`.

This matters for cost: the `DocumentEditResult` cases can share a single adapter (see
"Execution model").

## Proposed decomposition

Each transform moves to the family whose existing tools an agent would already reach for. The
table gives the destination for every case. "Engine" is the engine class the case calls today;
it does not move.

### Member and type conversions -> new `ConvertMember` / `ConvertType` (Refactor family)

These change the shape of a declaration. That makes them refactorings, not style sweeps, and
most of them affect callers.

| Transform | Engine | New operation |
| --- | --- | --- |
| `convert_expression_body` | StructuralRefactoringEngine | `ConvertMember(operation: toExpressionBody / toBlockBody)` |
| `convert_property_safe` | CodeGenerationEngine | `ConvertMember(operation: autoPropertyToFullProperty)` |
| `convert_property_to_methods` | CodeStyleEngine | `ConvertMember(operation: propertyToMethods)` |
| `convert_method_to_indexer` | StructuralRefactoringEngine | `ConvertMember(operation: methodToIndexer)` |
| `convert_static_to_extension` | LogicSimplificationEngine | `ConvertMember(operation: staticToExtension)` |
| `extension_to_static` | LogicSimplificationEngine | `ConvertMember(operation: extensionToStatic)` |
| `make_method_static` | StandardRefactoringEngine | `ConvertMember(operation: makeStatic)` -- see note |
| `convert_out_params_to_value_tuple` | OutParamRefactoringEngine | `ConvertMember(operation: outParamsToTuple)` |
| `class_to_record` / `record_to_class` | SyntaxModernizationEngine | `ConvertType(operation: toRecord / toClass)` |
| `convert_abstract_to_interface` | StructuralRefactoringEngine | `ConvertType(operation: abstractToInterface)` |
| `replace_constructor_with_factory` | StructuralRefactoringEngine | `ConvertType(operation: constructorToFactory)` |
| `upgrade_to_primary_constructor` | SyntaxUpgradeEngine | `ConvertType(operation: toPrimaryConstructor)` |
| `make_class_immutable` | SyntaxModernizationEngine | `ConvertType(operation: makeImmutable)` |

Note on `makeStatic`: `ModifyModifier` already adds `static`. The difference is that
`MakeMethodStaticAsync` also rewrites instance-member access inside the body. Folding it into
`ModifyModifier` as a smarter `static` path is an alternative -- see Open questions.

### File-wide modernization sweeps -> `ModernizeFile` (ModernizationTools)

These are behaviour-preserving, local rewrites that run over a whole file. They belong in one tool
with an enum-array parameter, next to `InvertBooleanLogic`, the only tool in `ModernizationTools`
today.

```
ModernizeFile(filepath, rules: ModernizeRule[], autoStage, dryRun, returnDiff)
```

All requested rules run in order against one in-memory document. The combined result is
validated and applied as one write, so a partial sweep never lands. This follows the batching
precedent in [proposal_batch_modifier_tools.md](proposal_batch_modifier_tools.md).

| Transform | Engine | Rule |
| --- | --- | --- |
| `add_braces` | SyntaxUpgradeEngine | `addBraces` |
| `cleanup_implicit_spans` | SyntaxUpgradeEngine | `cleanupImplicitSpans` |
| `upgrade_to_file_scoped_namespace` | SyntaxUpgradeEngine | `fileScopedNamespace` |
| `upgrade_to_modern_guards` | SyntaxUpgradeEngine | `modernGuards` |
| `use_field_backed_properties` | SyntaxUpgradeEngine | `fieldKeyword` |
| `convert_to_pattern` | SyntaxModernizationEngine | `patternMatching` |
| `convert_to_null_coalescing` | LogicSimplificationEngine | `nullCoalescing` |
| `convert_to_switch` | LogicSimplificationEngine | `ifChainToSwitch` |
| `simplify_boolean_expressions` | LogicSimplificationEngine | `simplifyBoolean` |
| `simplify_member_access` | IDEStyleEngine | `simplifyMemberAccess` |
| `simplify_verbosity` | CodeStyleEngine | `simplifyVerbosity` |
| `use_index_from_end` | CodeStyleEngine | `indexFromEnd` |
| `fix_thread_sleep` | CodeHealingEngine | `threadSleepToTaskDelay` |

Some method-scoped transforms are the same kind of rewrite at a narrower scope:

- `convert_switch_to_expression` (SyntaxUpgradeEngine)
- `add_guard_clauses` and `reduce_block_depth` (LogicSimplificationEngine)

These could become `ModernizeFile` rules with an optional `memberName` scope. They could also be
`ConvertMember` operations. See Open questions.

### Async -> AsyncifyTools

`AsyncifyTools` already owns the async-migration story (Asyncify, BridgeAsyncMethods,
UpliftCallers, PropagateCancellationToken).

| Transform | Engine | Destination |
| --- | --- | --- |
| `add_configure_await_false` / `remove_configure_await_false` | AsyncOptimizationEngine | `ConfigureAwait(filepath, operation: add / remove)` |
| `optimize_task_wait` | AdvancedRefactoringEngine | `ModernizeFile` rule `taskWaitToAwait`, or AsyncifyTools |
| `generate_async_overload` | AsyncOptimizationEngine | AsyncifyTools |
| `optimize_independent_awaits` | AsyncOptimizationEngine | AsyncifyTools |
| `optimize_to_value_task` | AsyncOptimizationEngine | AsyncifyTools (signature change -- same caller rule as `ConvertMember`) |
| `convert_to_async_enumerable` | AsyncOptimizationEngine | AsyncifyTools (signature change) |

### Usings, docs, namespaces -> extend the existing tools

| Transform | Engine | Destination |
| --- | --- | --- |
| `sort_and_deduplicate_usings` | MsToolAugmentEngine | `UsingDirective(operation: sort)` |
| `preview_add_missing_usings` | MsToolAugmentEngine | `UsingDirective(operation: addMissing)` -- dryRun gives the preview |
| `format_document_safe` | MsToolAugmentEngine | `FormatDocument(filepath, dryRun, returnDiff)`, or a `ModernizeFile` rule `format` |
| `update_xml_docs_from_signature` | RefactoringEngine | `SummaryComment(operation: syncParams)` |
| `fix_mismatched_namespaces` | SolutionStructureEngine | beside `SyncTypeAndFilename`, once it rewrites references in other files |
| `sort_members` | RefactoringEngine | open -- see Open questions |

### Generators -> GenerationTools

`GenerationTools` already has GenerateClassesFromJson, GenerateDefaultConfigJson,
GenerateHttpClient and InterpolateStringSafe.

| Kind | Engine | Destination |
| --- | --- | --- |
| `generate_constructor` | CodeGenerationEngine | `GenerateMember(kind: constructor)` |
| `generate_equality_overrides` | AntiPatternEngine | `GenerateMember(kind: equality)` |
| `generate_to_string_safe` | MsToolAugmentEngine | `GenerateMember(kind: toString)` |
| `generate_fluent_builder` | CodeGenerationEngine | `GenerateType(kind: fluentBuilder)` |
| `generate_decorator_class` | CodeGenerationEngine | `GenerateType(kind: decorator)` |
| `generate_repository_interface` | CodeGenerationEngine | `GenerateType(kind: repositoryInterface)` |
| `generate_test_skeleton` / `generate_test_scaffold` | TestingEngine | `GenerateTests(kind: skeleton / scaffold)` |
| `generate_path_driven_tests` | PathDrivenTestEngine | `GenerateTests(kind: pathDriven)` |
| `add_benchmark_stub` | TestingEngine | `GenerateTests(kind: benchmark)` |

`GenerateMember` inserts into an existing type, so it goes through `ValidateAndApplyAsync` like
any edit. `GenerateType` and `GenerateTests` produce new files. They should follow the
`CreateFile` path, with a required `targetPath`/`projectName` and no silent default location.

### Dropped

| Transform | Reason |
| --- | --- |
| `upgrade_pattern_matching` | Duplicates `convert_to_pattern`. |
| `use_exception_expressions` | Duplicates `upgrade_to_modern_guards`. |
| `format_document_preview` | Same as `format_document_safe` with preview on. `dryRun` replaces it. |
| `add_validation_to_poco` | Hard-codes `[Required]` and `[StringLength(100)]` on every property. Not a safe default for any codebase. |
| `generate_xml_documentation_stubs` | Placeholder doc comments add noise without information. `SummaryComment(add)` covers deliberate cases. |
| `document_poco_fields` | Same reasoning. |
| `make_method_thread_safe` | Wraps a body in a lock on a field named by `lockFieldName`. Thread safety is not a mechanical rewrite. |
| `use_time_provider` | *Unverified:* the description names `ITimeProvider`, while .NET 8+ ships the abstract class `TimeProvider`. Check the engine output before deciding. If it emits the wrong type, drop it. |

Niche -- keep only if there is a concrete need:

- `upgrade_thread_safety` (FixDangerousLock)
- `convert_lock_to_semaphore_slim` (ThreadSafetyEngine)
- `convert_to_background_service` (ArchitecturalEngine)
- `convert_to_source_generated_logging` (SyntaxModernizationEngine)

## Execution model

### Shared adapter

Most of the migration is one adapter in the tool layer, plus schema work per destination tool.
The adapter maps a `DocumentEditResult` onto the existing contract:

1. Map `EditOutcome` to a result:
   - `TargetNotFound` / `DocumentNotFound` become the corresponding targeting `ToolErrorCode`,
     with a message naming the offending parameter.
   - `NoChange` becomes a success with an explicit "already in target form" status. It is not an
     error.
   - `CannotConvert` / `CannotEdit` / `CannotOptimize` become a precondition-style error that
     carries the engine's `Message`.
   - `Error` and `Unset` become `ToolErrorCode.Exception`, with the message sanitised per the
     "no raw exceptions" rule.
2. On `Modified`, take `Changes` when it is non-empty, otherwise `{ FilePath: UpdatedText }`. Pass
   it to `ValidateAndApplyAsync` with the caller's `dryRun`/`returnDiff`.
3. Return the helper's `AppliedChangeSummary`. The full file text is never returned.

This lines up with [proposal_tool_error_code_taxonomy.md](proposal_tool_error_code_taxonomy.md)
and [proposal_unify_autostage_return_shape.md](proposal_unify_autostage_return_shape.md). If
either lands first, the adapter follows it.

### Signature-changing conversions

Validation is solution-wide, so `ValidateAndApplyAsync` will catch a caller in another file that
breaks. That turns defect 3 from silent corruption into a refused change -- a strict improvement,
even before any caller fixup exists.

The remaining question is whether each conversion should rewrite its callers or refuse up front.
For each signature-changing operation:

- If the engine already fills `Changes` across files, ship it as is.
- If not, add a `FindReferences` pre-check that refuses with "N callers in M files; this operation
  does not rewrite callers yet". Validation would otherwise report the same thing less clearly,
  as a list of compiler errors.

### Targeting

Replace `className` / `methodName` string lookups and the `lineBefore`/`lineAfter` pair with the
targeting the Refactor tools already use: `contextSnippet` plus a member or type name, and
`disambiguateLine` only where it is actually needed. Every parameter must be meaningful for every
operation of the tool it is on. A parameter that only applies to one operation belongs to a
different tool. A parameter that is optional for some operations just moves the failure to a
later round-trip, when the model omits it where it mattered.

### Order

1. **`ConvertMember` / `ConvertType`.** Highest value: these are the operations an agent
   currently has no structured way to perform.
2. **`UsingDirective(sort/addMissing)`, `SummaryComment(syncParams)`, `FormatDocument`.** Small
   extensions to existing tools.
3. **`ModernizeFile`.**
4. **Async moves into `AsyncifyTools`.**
5. **Generators.**
6. **Delete `CodemodTools`.** Also delete the `CodemodTriggerModes` rule
   (`ToolClassRegistry.cs:64-74`), the three `*Options` builders and their
   `DescribeAdvancedToolOptions` entries, and the registration lines. Fix the
   `apply_proposed_changes` doc comments.

Each step adds tool-level tests for the operations it moves. There are none today.

`CodemodTools` stays registered until step 6, so nothing disappears from the surface mid-migration.
Each moved case is removed from its switch in the same commit that adds its new home.

### Before starting

Two small, independent fixes are worth doing now if CodemodTools is going to stay registered
through the migration:

- Invert the `format_document_safe` default so a no-argument call does not write, and remove
  the `File.ReadAllTextAsync`/`File.WriteAllTextAsync` bypass in `FormatDocumentSafeAsync`.
- Drop the three dead constructor injections.

## Explicitly out of scope

- Changing engine behaviour. Engines are re-homed and wrapped, not rewritten. The exceptions are
  the `FormatDocumentSafeAsync` IO bypass, and multi-file `Changes` where step 1 of a migration
  shows an engine needs it.
- Renaming engine source files to match their post-merge class names. Some still differ, for
  example `StructuralRefactoringEngine` lives in `AdvancedStructuralEngine.cs` and
  `LogicSimplificationEngine` in `LogicOptimizationEngine.cs`. That belongs to the engine reorg.
- Staged (non-write-through) application. See [proposal_staged_writes.md](proposal_staged_writes.md).

## Cost / risk

- **Adapter plus about 8 destination tools or operation sets.** The engines and their 150+ tests
  are untouched, so the risk sits in the new tool layer.
- **Schema surface grows in some modes and shrinks in others.** Refactor/Modernize/Generation
  gain explicit enum-typed tools and lose four string-dispatched ones. The net effect is fewer
  invalid calls, because the `transform` string becomes an enum the schema can enforce.
- **Hidden multi-file gaps.** Validation turns them into refusals rather than corruption, but
  some conversions may be less useful than they look until caller fixups exist.
- **Regression risk is near zero for users.** The tool has never been used in production.

## Open questions

1. Fix `format_document_safe`'s default inversion now, or leave it for the retirement commit?
   It writes by default today.
2. Keep or drop the four niche transforms?
3. Should signature-changing conversions ship with a refuse-when-callers-exist pre-check, or
   wait until they rewrite callers?
4. Where does `sort_members` go? Options:
   - a `ConvertType` operation (it is type-scoped),
   - a `ModernizeFile` rule (it is a style rewrite),
   - drop it (ordering conventions differ between codebases).
5. Should `makeStatic` be a `ConvertMember` operation, or should `ModifyModifier(static)` learn
   to rewrite instance access?
6. Method-scoped rewrites (`convert_switch_to_expression`, `add_guard_clauses`,
   `reduce_block_depth`): `ModernizeFile` rules with a `memberName` scope, or `ConvertMember`
   operations?
7. `SourceTransformResult`: delete it once CodemodTools is gone, or keep it with a corrected doc
   comment for any other callers? Callers need checking at step 6.
8. Engine methods whose transforms are dropped: delete them and their tests, or leave them as
   engine-only capability?

## Status

Drafted, not yet implemented. Author: Claude (Opus 5.5), for Andrew Almond
(andrew.almond@clearbridge.ca).

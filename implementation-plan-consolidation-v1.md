# RoslynSentinel Tool Consolidation — Implementation Plan
<!-- v1 — generated 2026-05-31 -->

**Target:** Reduce ~318 active tools to ≤90 tools across 8 existing tool container classes.  
**Approach:** Incremental, phase-by-phase. Each phase leaves the build green; commit before moving to the next.  
**Spec:** [roslyn-sentinel-consolidation-impl-spec-v1.md](roslyn-sentinel-consolidation-impl-spec-v1.md)

## Architecture

- Tools are `[McpServerTool]`-attributed methods; PascalCase method name → snake_case tool name
- 8 `[McpServerToolType]` container classes in 6 modes: Workspace, Intelligence, Refactor, Modernize, Quality, Generation
- `ServiceRegistrationExtensions.cs` conditionally registers each class by mode string
- No central tool registry — discovery is reflection-based at startup

---

## Progress

- [ ] Baseline commit (plan file + pending spec files)
- [ ] Phase 1 — Obsoletion: delete deprecated aliases + redundant twins + fix name collisions
- [ ] Phase 2 — Read-only merges
- [ ] Phase 3 — `scan` + `describe_scan_detectors` + `analyze_method`
- [ ] Phase 4 — Write-pipeline merges (`staged_change`, `proposed_change`)
- [ ] Phase 5 — Refactor/edit merges (11 merged tools in SentinelRefactoringTools.cs)
- [ ] Phase 6 — Codemod merges (`apply_*_codemod`, `generate`)
- [ ] Phase 7 — Gated families (`project_doc`, `features`, `move_all_types_to_files`, `async_migrate`)
- [ ] Phase 8 — Profile gating (optional `--mode` startup arg)

---

## Phase 1 — Obsoletion (A1-A3) — Pure Removal

**Expected tool count reduction: ~118**

### Step 1.1 — Delete 109 deprecated `[McpServerTool]` methods (A1)

All methods whose `[Description]` starts with `"Deprecated: use … instead"`.

Files to edit:
- `SentinelIntelligenceTools.cs` — ~20 deprecated methods near lines 560–670
- `SentinelQualityTools.cs` — remaining ~89 deprecated CT-propagation and migration aliases

Action: delete the `[McpServerTool]` + `[Description]` + method body. Do NOT touch engine methods they delegate to.

Pre-check: `grep -r '"Deprecated:' RoslynSentinel.Server --include="*.cs"` to confirm full list before deleting.

### Step 1.2 — Delete 8 redundant twins (A2) (9th deferred to after Phase 5)

| Delete | File | Keep |
|---|---|---|
| `AnalyzeMethodControlFlow` | SentinelQualityTools.cs | `analyze_control_flow` (SentinelRefactoringTools.cs) |
| `AnalyzeMethodDataFlow` | SentinelQualityTools.cs | `analyze_data_flow` (SentinelRefactoringTools.cs) |
| `ConvertStringFormatToInterpolatedSmart` | SentinelAugmentTools.cs | `interpolate_string_safe` |
| `ExtractConstant` | SentinelRefactoringTools.cs | `extract_constant_safe` (SentinelAugmentTools.cs) |
| `ExtractMethod` | SentinelRefactoringTools.cs | `extract_method_safe` (SentinelAugmentTools.cs) |
| `GenerateToString` | SentinelGenerationTools.cs | `generate_to_string_safe` |
| `SafeDeleteSymbol` | SentinelRefactoringTools.cs | `safe_delete` (SentinelWorkspaceTools.cs) |
| `ScanLongParameterLists` | SentinelIntelligenceTools.cs | `scan_long_parameter_list` (SentinelQualityTools.cs) |
| `ExtractLocalVariable` | SentinelRefactoringTools.cs | `introduce(as=localVariable)` — **deferred to after Phase 5** |

### Step 1.3 — Fix 3 name collisions (A3)

**`scan_circular_dependencies` exists twice in `SentinelIntelligenceTools.cs`:**
- Project-reference cycles (line ~131) → rename method to `CircularProjectReferences`
- Type-dependency cycles (line ~251) → rename method to `CircularTypeReferences`
- Both will be folded into `scan` in Phase 3; rename now removes the collision

**`sync_type_and_filename` true duplicate** in `SentinelWorkspaceTools.cs` → verify both occurrences, delete the duplicate, keep one.

**`find_circular_dependencies` deprecated alias** — already removed in Step 1.1.

**Build + commit.**

---

## Phase 2 — Read-Only Merges

All in existing classes; no new files. Add one merged method, then delete the folded methods.

| New tool | Target class | Folds | Axis |
|---|---|---|---|
| `find_references(filePath, symbolName, kind)` | SentinelIntelligenceTools.cs | `get_callers` + `get_implementations` | kind: `callers\|implementations` |
| `find_by_name(name, kind, scope?)` | SentinelIntelligenceTools.cs | 6 tools | kind: 6-value enum; returns JSON string |
| `get_type_info(typeName, include, projectName?)` | SentinelIntelligenceTools.cs | `get_type_hierarchy` + `get_type_members_detail` | include: `hierarchy\|members\|both` |
| `get_diagnostics(scope, scopeName?, summarize?)` | SentinelWorkspaceTools.cs | 4 diagnostic tools | scope: `file\|project\|solution` |
| `get_call_graph(filePath, methodName, direction, maxDepth?, format?)` | SentinelIntelligenceTools.cs | 3 call graph tools | direction: `forward\|reverse`; format: `structured\|tree` |
| `get_public_api_surface(projectName?, filePath?, persistBaseline?)` | SentinelIntelligenceTools.cs | `get_public_api_surface` + `get_public_api_surface_snapshot` | persistBaseline bool |
| `inspect_symbol(filePath, contextSnippet, aspect, ...)` | SentinelIntelligenceTools.cs | `get_symbol_info` + `get_blast_radius` | aspect: `info\|blastRadius` |
| `list(kind, projectName?)` | SentinelWorkspaceTools.cs | `list_projects` + `list_files` + `list_dependencies` | kind: `projects\|files\|dependencies` |

`find_by_name` kinds: `implementorsOf | attributeUsages | objectCreations | extensionsFor | typesWithAttribute | methodsByReturnType`

**Build + commit.**

---

## Phase 3 — `scan` + `describe_scan_detectors` + `analyze_method`

**New file: `RoslynSentinel.Server/SentinelScanTools.cs`** — new `[McpServerToolType]` class registered under "Intelligence" mode.

### 3.1 — Build static detector registry

Nested `private static class DetectorRegistry` with `Dictionary<string, DetectorInfo>`.  
`DetectorInfo` fields: `Id`, `Domain`, `Description` (from folded tool's [Description] first line), `ValidScopes`.

Prefix-stripping rules (verified: no id collisions after stripping):
- `scan_` / `check_for_` / `check_` / `analyze_` / `optimize_` → strip

96 detectors across 9 domains:

| Domain | Count | Sample ids |
|---|---|---|
| concurrency | 26 | `async_in_constructor`, `missing_cancellation_tokens`, `possible_deadlocks`, ... |
| config | 3 | `json_anti_patterns`, `package_inconsistency`, `project_consistency` |
| convention | 6 | `naming_violations`, `readonly_field_candidates`, `string_magic_values`, ... |
| correctness | 17 | `empty_catch_blocks`, `non_exhaustive_enum_switches`, `unbounded_recursion`, ... |
| dead-code | 9 | `obsolete_callers`, `unused_private_members`, `unused_references`, ... |
| misc | 3 | `anti_patterns`, `blocking_calls_in`, `finalizer_on_disposable` |
| performance | 11 | `boxing_allocations`, `linq_n1_patterns`, `regex_new_in_loop`, ... |
| security | 5 | `hardcoded_paths`, `reflection_usage`, `sql_injection`, ... |
| structure | 16 | `circular_dependencies`, `duplicate_methods`, `large_methods`, `namespace_path_mismatches`, ... |

### 3.2 — `scan(detector, scope, scopeName?)`

```
scan(detector: string, scope: string, scopeName: string? = null)
```

`switch (detector)` dispatches to same engine calls as original per-detector tools. Validates:
1. `detector` ∈ DetectorRegistry → error "Unknown detector '{detector}'"
2. `scope` ∈ `detectorInfo.ValidScopes` → error "Detector '{detector}' requires scope {validScopes}"

### 3.3 — `describe_scan_detectors(domain?, detector?)`

Reads from DetectorRegistry. No-args = all entries; domain only = filter; detector = single entry.

### 3.4 — `analyze_method(filePath, methodName, aspect)`

aspect: `controlFlow | dataFlow | pathCoverage | unreachableCode`  
Folds: `analyze_control_flow`, `analyze_data_flow`, `analyze_path_coverage`, `scan_unreachable_code`

### 3.5 — Register SentinelScanTools in ServiceRegistrationExtensions.cs

Add alongside SentinelIntelligenceTools in the "Intelligence" block.

### 3.6 — Delete all 96 folded scan/check/analyze tools from SentinelIntelligenceTools.cs

Do this AFTER the new `scan` routing is verified.

**Build + commit.**

---

## Phase 4 — Write-Pipeline Merges

Both in `SentinelWorkspaceTools.cs`.

### 4.1 — `staged_change(action, changeId?)`

action: `apply | get | validate | discard`  
Folds: `apply_staged_changes`, `get_staged_changes`, `validate_staged_changes`, `discard_staged_changes`

### 4.2 — `proposed_change(format, action, payload)`

format: `files | diff`; action: `apply | validate`  
Folds: `apply_proposed_changes`, `validate_proposed_changes`, `apply_proposed_diff`, `validate_proposed_diff`

**Build + commit.**

---

## Phase 5 — Refactor/Edit Merges

All in `SentinelRefactoringTools.cs` (or existing host). Add new method, delete folded methods.

| New tool | Axis | Folds |
|---|---|---|
| `modify_attribute(filePath, targetName, attribute, action)` | action: `add\|remove` | `add_attribute` + `remove_attribute` |
| `modify_modifier(filePath, targetName, modifier, action)` | action: `add\|remove` | `add_modifier` + `remove_modifier` |
| `modify_base_type(filePath, typeName, baseTypeName, action)` | action: `add\|remove` | `add_base_type` + `remove_base_type` |
| `introduce(filePath, contextSnippet, newName, as)` | as: `localVariable\|field\|parameter\|constant` | `introduce_field`, `introduce_variable`, `introduce_parameter`, `extract_constant_safe` |
| `extract_members(filePath, className, memberNames, newTypeName, as)` | as: `interface\|class\|partial\|superclass` | `extract_interface`, `extract_class`, `extract_members_to_partial`, `extract_superclass` |
| `sync_interface(filePath, className, interfaceName, action)` | action: `implement\|sync\|verify` | `implement_interface_safe`, `sync_interface_to_implementation`, `verify_interface_completeness` |
| `inline(filePath, targetName, kind)` | kind: `method\|variable\|field\|parameter` | `inline_method`, `inline_variable`, `inline_field`, `inline_parameter` |
| `add_member(filePath, containerName, newMemberSource, position?)` | position: `end\|before:X\|after:X` | `add_member_to_class`, `insert_member_after`, `insert_member_before` |
| `add_member_typed(filePath, containerName, name, type, kind)` | kind: `property\|field` | `add_property` + `add_field` |
| `wrap_range(filePath, startLine, endLine, wrapper, name?)` | wrapper: `tryCatch\|using\|region` | `wrap_in_try_catch`, `wrap_in_using`, `wrap_in_region` |
| `move_type(filePath, typeName, destination)` | destination: `ownFile\|outerScope` | `move_type_to_file`, `move_type_to_outer_scope` |

After `introduce` is live, also delete `ExtractLocalVariable` (deferred A2 twin from Phase 1).

**Build + commit.**

---

## Phase 6 — Codemod Merges

### 6.1 — `apply_file_codemod(filePath, transform)` in SentinelModernizationTools.cs

25 transforms. `switch(transform)` dispatches to existing engine method calls.  
Folds tools from SentinelModernizationTools.cs and SentinelQualityTools.cs.

Transform values: `addBraces`, `convertToNullCoalescing`, `convertToPattern`, `convertToSwitch`, `simplifyBooleanExpressions`, `simplifyMemberAccess`, `simplifyVerbosity`, `sortAndDeduplicateUsings`, `useFieldBackedProperties`, `useIndexFromEnd`, `useTimeProvider`, `upgradePatternMatching`, `upgradeToFileScopedNamespace`, `upgradeToModernGuards`, `upgradeThreadSafety`, `cleanupImplicitSpans`, `optimizeTaskWait`, `addConfigureAwaitFalse`, `removeConfigureAwaitFalse`, `fixThreadSleep`, `generateXmlDocumentationStubs`, `previewAddMissingUsings`, `formatDocumentSafe`, `formatDocumentPreview`, `fixMismatchedNamespaces`

### 6.2 — `apply_method_codemod(filePath, methodName, transform)` in SentinelModernizationTools.cs

17 transforms: `convertLockToSemaphoreSlim`, `convertMethodToIndexer`, `convertOutParamsToValueTuple`, `convertStaticToExtension`, `convertSwitchToExpression`, `convertToAsyncEnumerable`, `extensionToStatic`, `generateAsyncOverload`, `makeMethodStatic`, `makeMethodThreadSafe`, `optimizeIndependentAwaits`, `optimizeToValueTask`, `reduceBlockDepth`, `useExceptionExpressions`, `addGuardClauses`, `updateXmlDocsFromSignature`, `convertExpressionBody`

### 6.3 — `apply_class_codemod(filePath, className, transform)` in SentinelModernizationTools.cs

13 transforms: `classToRecord`, `recordToClass`, `convertAbstractToInterface`, `convertToBackgroundService`, `convertToSourceGeneratedLogging`, `makeClassImmutable`, `upgradeToPrimaryConstructor`, `replaceConstructorWithFactory`, `addValidationToPoco`, `documentPocoFields`, `sortMembers`, `convertPropertyToMethods`, `convertPropertySafe`

### 6.4 — `generate(filePath, className, artifact)` in SentinelGenerationTools.cs

10 artifacts: `constructor`, `equalityOverrides`, `fluentBuilder`, `repositoryInterface`, `testScaffold`, `testSkeleton`, `toString`, `decoratorClass`, `benchmarkStub`, `pathDrivenTests`

**Do NOT fold:** `generate_classes_from_json`, `generate_http_client`, `generate_mapping`, `generate_default_config_json` — they have disjoint required inputs.

**Build + commit.**

---

## Phase 7 — Gated Families

### 7.1 — `project_doc(action, docType, name?, content?)` in DocumentationTools.cs

Folds all 11 doc tools.  
docType: `plan | handoff | completed | documentation | currentState`; action: `read | write | append | list`  
Valid combos enforced server-side (`append` valid only for `completed`; `list` ignores docType).

### 7.2 — `features(action, names?, enabled?)` in SentinelWorkspaceTools.cs

action: `get | list | update`  
Folds: `get_feature_status`, `list_features`, `update_features`

### 7.3 — `move_all_types_to_files(scope, scopeName?)` in SentinelWorkspaceTools.cs

scope: `file | project | solution`  
Folds: `move_all_types_to_files`, `move_all_types_to_files_in_project`, `move_all_types_to_files_in_solution`

### 7.4 — `async_migrate(operation, input)` in SentinelQualityTools.cs

operation: `asyncify | addCancellationToken | propagateCancellationToken | convertToAsyncBridge | runUplift | flagMigrationCandidates`  
`input` uses `JsonElement` and is deserialized per-operation.  
**Explicitly reversible:** split back to 6 tools if model misfills.

**Build + commit.**

---

## Phase 8 — Profile Gating (optional)

Update `ServiceRegistrationExtensions.cs` to support named profiles via `--mode` startup arg.

| Profile | Tools | Contents |
|---|---|---|
| `Core` | 32 | Always-on read/navigate/workspace tools |
| `Refactor` | 28 | Default refactoring suite |
| `Analysis` | 7 | `scan`, `describe_scan_detectors`, `analyze_method`, `get_comprehensive_health_report`, etc. |
| `Modernize` | 9 | `apply_*_codemod`, `convert_switch_to_pattern_safe`, `interpolate_string_safe`, etc. |
| `Codegen` | 5 | `generate`, `generate_classes_from_json`, `generate_http_client`, `generate_mapping`, `generate_default_config_json` |
| `Async` | 3 | `async_migrate`, `get_async_migration_progress`, `scan_migration_candidates` |
| `Project` | 6 | `create_project`, `features`, `move_all_types_to_files`, `project_doc`, `split_project_by_folder`, `sync_type_and_filename` |

Default (Core + Refactor) = 60. Any single opt-in mode ≤ 128.

**Build + commit.**

---

## Decisions

- `SentinelScanTools.cs` is a new file (Phase 3) — SentinelIntelligenceTools.cs is already ~100+ tools; adding 3 more large dispatch methods would make it unmanageable.
- `find_by_name` returns `Task<string>` (JSON) — each kind returns a different `List<T>`; avoids the conditional-schema anti-pattern.
- `async_migrate` is last and explicitly reversible.
- Tools with disjoint required inputs (`generate_classes_from_json` etc.) are kept separate per spec consolidation rule.
- Phase order matches spec §F: obsoletion → read-only → scan → write pipeline → refactor → codemod → gated.

## Acceptance Criteria

- [ ] `tools/list` returns ≤90 tools (≤128 in any single `--mode`)
- [ ] No tool description begins with `Deprecated:`
- [ ] No duplicate tool names in manifest
- [ ] `scan` enum carries detector names only (no per-value prose); total scan-related schema < ~250 tokens
- [ ] Every old detector id is reachable via `scan(detector=…)` and described via `describe_scan_detectors`
- [ ] Each merged tool reproduces the output of every tool it replaced (spot-check one axis value per fold)
- [ ] Build passes; no orphaned handler references

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelScanTools
{
    private readonly AnalysisEngine _analysisEngine;
    private readonly SecurityEngine _securityEngine;
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly ControlFlowEngine _controlFlowEngine;
    private readonly PerformanceEngine _performanceEngine;
    private readonly DeadCodeEngine _deadCodeEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ArchitecturalEngine _architecturalEngine;
    private readonly ProjectStructureEngine _projectStructureEngine;
    private readonly DependencyInjectionEngine _dependencyInjectionEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly MetricsEngine _metricsEngine;
    private readonly CloneDetectionEngine _cloneDetectionEngine;
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly StackOverflowEngine _stackOverflowEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly CodeStyleAnalysisEngine _codeStyleAnalysisEngine;
    private readonly RefactoringEngine _refactoringEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly BreakingChangeEngine _breakingChangeEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelScanTools> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    public SentinelScanTools(
        AnalysisEngine analysisEngine,
        SecurityEngine securityEngine,
        AntiPatternEngine antiPatternEngine,
        AsyncSafetyEngine asyncSafetyEngine,
        ThreadSafetyEngine threadSafetyEngine,
        ControlFlowEngine controlFlowEngine,
        PerformanceEngine performanceEngine,
        DeadCodeEngine deadCodeEngine,
        DependencyEngine dependencyEngine,
        ArchitecturalEngine architecturalEngine,
        ProjectStructureEngine projectStructureEngine,
        DependencyInjectionEngine dependencyInjectionEngine,
        ProjectConsistencyEngine projectConsistencyEngine,
        MetricsEngine metricsEngine,
        CloneDetectionEngine cloneDetectionEngine,
        DiscoveryEngine discoveryEngine,
        StackOverflowEngine stackOverflowEngine,
        CodeStyleEngine codeStyleEngine,
        CodeStyleAnalysisEngine codeStyleAnalysisEngine,
        RefactoringEngine refactoringEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        BreakingChangeEngine breakingChangeEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelScanTools> logger)
    {
        _analysisEngine = analysisEngine;
        _securityEngine = securityEngine;
        _antiPatternEngine = antiPatternEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _threadSafetyEngine = threadSafetyEngine;
        _controlFlowEngine = controlFlowEngine;
        _performanceEngine = performanceEngine;
        _deadCodeEngine = deadCodeEngine;
        _dependencyEngine = dependencyEngine;
        _architecturalEngine = architecturalEngine;
        _projectStructureEngine = projectStructureEngine;
        _dependencyInjectionEngine = dependencyInjectionEngine;
        _projectConsistencyEngine = projectConsistencyEngine;
        _metricsEngine = metricsEngine;
        _cloneDetectionEngine = cloneDetectionEngine;
        _discoveryEngine = discoveryEngine;
        _stackOverflowEngine = stackOverflowEngine;
        _codeStyleEngine = codeStyleEngine;
        _codeStyleAnalysisEngine = codeStyleAnalysisEngine;
        _refactoringEngine = refactoringEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _breakingChangeEngine = breakingChangeEngine;

        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool]
    [Produces(DataTag.ScanId)]
    [Description("""
    Dispatches a named detector across a file, project, or solution.
    scope: file | project | solution. scopeName: filePath when scope=file;
    projectName when scope=project; root type name for duplicate_blocks_in_hierarchy.
    File-scope-only detectors require scope=file. unused_references requires scope=project.
    Call describe_scan_detectors(domain?) for descriptions and scope hints.
    """)]
    public async Task<ToolResult<object>> RunScanDetector(
        [Consumes(DataTag.DetectorName)] DetectorId detector,
        [ExternalInputRequired(DataTag.Scope)] string scope,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Consumes(DataTag.ProjectName, required: false)] string? scopeName = null,
        CancellationToken cancellationToken = default)
    {
        //string? filePath = scope == "file" ? scopeName : null;
        string? projectName = scope == "project" ? scopeName : null;

        try
        {
            FilePath filePath = String.IsNullOrEmpty(filepath) && scope == "file" ? null : FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

            switch (detector)
            {
                // ── async ──────────────────────────────────────────────────────────
                case DetectorId.async_in_constructor:
                    var result = await _asyncSafetyEngine.FindAsyncInConstructorAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result };
                case DetectorId.async_over_sync:
                    var result2 = await _asyncSafetyEngine.FindAsyncOverSyncAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result2 };
                case DetectorId.async_void_without_try_catch:
                    var result3 = await _asyncSafetyEngine.FindAsyncVoidWithoutTryCatchAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result3 };
                case DetectorId.blocking_calls_in:
                    var result4 = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result4 };
                case DetectorId.cancellation_token_not_forwarded:
                    var result5 = await _asyncSafetyEngine.FindCancellationTokenNotForwardedAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result5 };
                case DetectorId.configure_await_missing:
                    var result6 = await _asyncSafetyEngine.FindConfigureAwaitMissingAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result6 };
                case DetectorId.inconsistent_async_suffix:
                    var result7 = await _antiPatternEngine.FindInconsistentAsyncSuffixAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result7 };
                case DetectorId.mismatched_await:
                    var result8 = await _analysisEngine.DetectMismatchedAwaitAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result8 };
                case DetectorId.missing_cancellation_tokens:
                    var result9 = await _antiPatternEngine.FindMissingCancellationTokensAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result9 };
                case DetectorId.sequential_independent_awaits:
                    var result10 = await _asyncSafetyEngine.FindSequentialIndependentAwaitsAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result10 };
                case DetectorId.task_delay_usage:
                    var result11 = await _asyncSafetyEngine.FindTaskDelayUsageAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result11 };
                case DetectorId.task_delay_zero_usage:
                    var result12 = await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result12 };
                case DetectorId.task_run_in:
                    var result13 = await _asyncSafetyEngine.FindTaskRunInAsyncAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result13 };
                case DetectorId.task_void_usage:
                    var result14 = await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result14 };
                case DetectorId.task_when_all_usage:
                    var result15 = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result15 };
                case DetectorId.task_yield_usage:
                    var result16 = await _asyncSafetyEngine.FindTaskYieldUsageAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result16 };
                case DetectorId.unawaited_fire_and_forget:
                    var result17 = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result17 };
                case DetectorId.unawaited_dispose:
                    var result18 = await _asyncSafetyEngine.FindUnawaitedDisposeAsyncAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result18 };
                case DetectorId.unobserved_task_in_field:
                    var result19 = await _asyncSafetyEngine.FindUnobservedTaskInFieldAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result19 };
                case DetectorId.value_task_misuse:
                    var result20 = await _asyncSafetyEngine.DetectValueTaskMisuseAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result20 };

                // ── concurrency ────────────────────────────────────────────────────

                case DetectorId.cas_loop_without_backoff:
                    var result21 = await _threadSafetyEngine.FindCasLoopWithoutBackoffAsync(projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result21 };
                case DetectorId.check_then_act_on_dictionary:
                    var result22 = await _threadSafetyEngine.FindCheckThenActOnDictionaryAsync(projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result22 };
                case DetectorId.concurrent_collection_opportunities:
                    var result23 = await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result23 };
                case DetectorId.double_checked_locking:
                    var result24 = await _threadSafetyEngine.FindDoubleCheckedLockingAsync(projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result24 };
                case DetectorId.possible_deadlocks:
                    var result25 = await _analysisEngine.FindPossibleDeadlocksAsync(projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result25 };
                case DetectorId.semaphore_usage:
                    var result26 = await _analysisEngine.AnalyzeSemaphoreUsageAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result26 };
                case DetectorId.unsafe_lazy_init:
                    var result27 = await _asyncSafetyEngine.FindUnsafeLazyInitAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result27 };
                case DetectorId.unsafe_lazy_init_thread:
                    var result28 = await _threadSafetyEngine.FindUnsafeLazyInitAsync(projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result28 };

                // ── config ─────────────────────────────────────────────────────────

                case DetectorId.json_anti_patterns:
                    var result29 = await _securityEngine.DetectJsonAntiPatternsAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result29 };
                case DetectorId.package_inconsistency:
                    var result30 = await _dependencyEngine.CheckPackageInconsistencyAsync();
                    return new ToolResult<object>() { Success = true, Data = result30 };
                case DetectorId.project_consistency:
                    var result31 = await _projectConsistencyEngine.CheckConsistencyAsync();
                    return new ToolResult<object>() { Success = true, Data = result31 };

                // ── convention ─────────────────────────────────────────────────────

                case DetectorId.mutable_public_collection_properties:
                    var result32 = await _codeStyleAnalysisEngine.FindMutablePublicCollectionPropertiesAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result32 };
                case DetectorId.mutable_public_properties:
                    var result33 = await _antiPatternEngine.FindMutablePublicPropertiesAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result33 };
                case DetectorId.naming_violations:
                    var result34 = await _antiPatternEngine.FindNamingViolationsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result34 };
                case DetectorId.readonly_field_candidates:
                    var result35 = await _symbolNavigationEngine.FindReadonlyFieldCandidatesAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result35 };
                case DetectorId.string_magic_values:
                    var result36 = await _antiPatternEngine.FindStringMagicValuesAsync(filePath, projectName, 3);
                    return new ToolResult<object>() { Success = true, Data = result36 };
                case DetectorId.todo_fixme_comments:
                    var result37 = await _discoveryEngine.FindTodoFixmeCommentsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result37 };

                // ── correctness ────────────────────────────────────────────────────

                case DetectorId.all_throw_sites:
                    var result38 = await _discoveryEngine.FindAllThrowSitesAsync(null, filePath, projectName, false);
                    return new ToolResult<object>() { Success = true, Data = result38 };
                case DetectorId.empty_catch_blocks:
                    var result39 = await _analysisEngine.CheckForEmptyCatchBlocksAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result39 };
                case DetectorId.exception_handling:
                    var result40 = await _antiPatternEngine.AnalyzeExceptionHandlingAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result40 };
                case DetectorId.memory_leaks:
                    var result41 = await _analysisEngine.DetectMemoryLeaksAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result41 };
                case DetectorId.misbound_overload_chains:
                    var result42 = await _analysisEngine.FindMisboundOverloadChainsAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result42 };
                case DetectorId.missing_generic_constraints:
                    var result43 = await _analysisEngine.FindMissingGenericConstraintsAsync(projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result43 };
                case DetectorId.multiple_out_parameter_methods:
                    var result44 = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result44 };
                case DetectorId.non_exhaustive_enum_switches:
                    var result45 = await _controlFlowEngine.FindNonExhaustiveEnumSwitchesAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result45 };
                case DetectorId.possible_infinite_loops:
                    var result46 = await _analysisEngine.FindPossibleInfiniteLoopsAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result46 };
                case DetectorId.redundant_cast:
                    var result47 = await _analysisEngine.CheckForRedundantCastAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result47 };
                case DetectorId.resource_disposal:
                    var result48 = await _analysisEngine.OptimizeResourceDisposalAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result48 };
                case DetectorId.services_not_registered:
                    var result49 = await _dependencyInjectionEngine.FindServicesNotRegisteredAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result49 };
                case DetectorId.stack_overflow_risks:
                    var result50 = await _stackOverflowEngine.AnalyzeStackOverflowRisksAsync(RequireFile(scope, scopeName), false);
                    return new ToolResult<object>() { Success = true, Data = result50 };
                case DetectorId.unbounded_recursion:
                    var result51 = await _analysisEngine.FindUnboundedRecursionAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result51 };
                case DetectorId.unbounded_static_collections:
                    var result52 = await _analysisEngine.FindUnboundedStaticCollectionsAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result52 };
                case DetectorId.value_type_mutation_intent:
                    var result53 = await _antiPatternEngine.FindValueTypeMutationIntentAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result53 };

                // ── dead-code ──────────────────────────────────────────────────────

                case DetectorId.obsolete_callers:
                    var result54 = await _antiPatternEngine.FindObsoleteCallersAsync(null, filePath, projectName, cancellationToken);
                    return new ToolResult<object>() { Success = true, Data = result54 };
                case DetectorId.uninstantiated_types:
                    var result55 = await _analysisEngine.FindUninstantiatedTypesAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result55 };
                case DetectorId.unused_constructors:
                    var result56 = await _deadCodeEngine.FindUnusedConstructorsAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result56 };
                case DetectorId.unused_event_subscriptions:
                    var result57 = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync(RequireFile(scope, scopeName)) ?? new List<DeadCodeReport>();
                    return new ToolResult<object>() { Success = true, Data = result57 };
                case DetectorId.unused_interfaces:
                    var result58 = await _analysisEngine.FindUnusedInterfacesAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result58 };
                case DetectorId.unused_local_variables:
                    var result59 = await _deadCodeEngine.DetectUnusedLocalVariablesAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result59 };
                case DetectorId.unused_private_fields:
                    var result60 = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result60 };
                case DetectorId.unused_references:
                    var result61 = await _dependencyEngine.FindUnusedReferencesAsync(RequireProject(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result61 };

                // ── misc ───────────────────────────────────────────────────────────

                case DetectorId.anti_patterns:
                    var result62 = await _antiPatternEngine.DetectAntiPatternsAsync(filePath, projectName, null);
                    return new ToolResult<object>() { Success = true, Data = result62 };
                case DetectorId.finalizer_on_disposable:
                    var result63 = await _analysisEngine.FindFinalizerOnDisposableAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result63 };

                // ── performance ────────────────────────────────────────────────────

                case DetectorId.boxing_allocations:
                    var result64 = await _analysisEngine.FindBoxingAllocationsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result64 };
                case DetectorId.implicit_nullable_boxing:
                    var result65 = await _performanceEngine.FindImplicitNullableBoxingAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result65 };
                case DetectorId.inefficient_string_comparisons:
                    var result66 = await _analysisEngine.DetectInefficientStringComparisonsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result66 };
                case DetectorId.linq_n1_patterns:
                    var result67 = await _performanceEngine.FindLinqN1PatternsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result67 };
                case DetectorId.linq_redundant_where:
                    var result68 = await _performanceEngine.FindLinqRedundantWhereAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result68 };
                case DetectorId.multiple_enumeration:
                    var result69 = await _performanceEngine.FindMultipleEnumerationAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result69 };
                case DetectorId.performance:
                    var result70 = await _performanceEngine.AnalyzePerformanceAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result70 };
                case DetectorId.re_do_s_patterns:
                    var result71 = await _securityEngine.FindReDoSPatternsAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result71 };
                case DetectorId.regex_new_in_loop:
                    var result72 = await _securityEngine.FindRegexNewInLoopAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result72 };
                case DetectorId.string_format_in_loops:
                    var result73 = await _performanceEngine.FindStringFormatInLoopsAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result73 };
                case DetectorId.use_frozen_collections:
                    var result74 = await _codeStyleEngine.FindUseFrozenCollectionsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result74 };

                // ── security ───────────────────────────────────────────────────────

                case DetectorId.hardcoded_paths:
                    var result75 = await _securityEngine.FindHardcodedPathsAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result75 };
                case DetectorId.reflection_usage:
                    var result76 = await _analysisEngine.DetectReflectionUsageAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result76 };
                case DetectorId.security:
                    var result77 = await _securityEngine.AnalyzeSecurityAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result77 };
                case DetectorId.sql_injection:
                    var result78 = await _securityEngine.CheckForSqlInjectionAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result78 };
                case DetectorId.unvalidated_regex_source:
                    var result79 = await _securityEngine.FindUnvalidatedRegexSourceAsync(RequireFile(scope, scopeName));
                    return new ToolResult<object>() { Success = true, Data = result79 };

                // ── structure ──────────────────────────────────────────────────────

                case DetectorId.circular_dependencies:
                    var result80 = await _analysisEngine.FindCircularDependenciesAsync();
                    return new ToolResult<object>() { Success = true, Data = result80 };
                case DetectorId.circular_type_references:
                    var result81 = await _analysisEngine.FindCircularTypeReferencesAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result81 };
                case DetectorId.duplicate_blocks_in_hierarchy:
                    {
                        if (string.IsNullOrEmpty(scopeName))
                        {
                            return new ToolResult<object>() { Success = false, Data = "duplicate_blocks_in_hierarchy requires scopeName to be the root type name." };
                        }
                        var result82 = await _cloneDetectionEngine.FindDuplicateBlocksInHierarchyAsync(scopeName, null, 4);
                        return new ToolResult<object>() { Success = true, Data = result82 };
                    }
                case DetectorId.duplicate_methods:
                    var result83 = await _analysisEngine.FindDuplicateMethodsAsync(5, projectName);
                    return new ToolResult<object>() { Success = true, Data = result83 };
                case DetectorId.interface_extraction_candidates:
                    var result84 = await _analysisEngine.FindInterfaceExtractionCandidatesAsync(3, projectName);
                    return new ToolResult<object>() { Success = true, Data = result84 };
                case DetectorId.internal_classes_that_could_be_private:
                    var result85 = await _analysisEngine.FindInternalClassesThatCouldBePrivateAsync(projectName);
                    return new ToolResult<object>() { Success = true, Data = result85 };
                case DetectorId.large_methods:
                    var result86 = await _analysisEngine.FindLargeMethodsAsync(50, projectName);
                    return new ToolResult<object>() { Success = true, Data = result86 };
                case DetectorId.large_switch_statements:
                    var result87 = await _analysisEngine.FindLargeSwitchStatementsAsync(10, projectName);
                    return new ToolResult<object>() { Success = true, Data = result87 };
                case DetectorId.large_types:
                    var result88 = await _analysisEngine.FindLargeTypesAsync(500, projectName);
                    return new ToolResult<object>() { Success = true, Data = result88 };
                case DetectorId.layer_violations:
                    var result89 = await _architecturalEngine.DetectLayerViolationsAsync(projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result89 };
                case DetectorId.long_parameter_list:
                    var result90 = await _antiPatternEngine.FindLongParameterListAsync(filePath, projectName, 4);
                    return new ToolResult<object>() { Success = true, Data = result90 };
                case DetectorId.namespace_path_mismatches:
                    {
                        var solution = await _workspaceManager.GetBranchedSolutionAsync();
                        var result91 = await _analysisEngine.FindNamespacePathMismatchesAsync(solution, projectName, cancellationToken);
                        return new ToolResult<object>() { Success = true, Data = result91 };
                    }
                case DetectorId.primitive_obsession:
                    var result92 = await _antiPatternEngine.FindPrimitiveObsessionAsync(filePath, projectName);
                    return new ToolResult<object>() { Success = true, Data = result92 };
                case DetectorId.structural_smells:
                    var result93 = await _projectStructureEngine.FindStructuralSmellsAsync(ProjectStructureEngine.StructuralSmellType.All, projectName, filePath);
                    return new ToolResult<object>() { Success = true, Data = result93 };
                case DetectorId.type_cohesion:
                    var result94 = await _metricsEngine.AnalyzeTypeCohesionAsync(RequireFile(scope, scopeName), null);
                    return new ToolResult<object>() { Success = true, Data = result94 };

                default:
                    return new ToolResult<object>() { Success = false, Data = ($"Unknown detector '{detector}'. Call describe_scan_detectors() for the full list.") };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan ({Detector}) failed", detector);
            return new ToolResult<object>() { Success = false, Data = ($"Scan ({detector}) failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Produces(DataTag.Report)]
    [Description("""
        Returns the catalogue of available scan detectors. domain filters by domain: async | concurrency | config | convention | correctness | dead-code | misc | performance | security | structure. detector returns info for a single detector by exact id. Both omitted → all 94 detectors. Each entry includes: Id, Domain, ScopeHint (file | project | solution | any combinations), Description.
        """)]
    public Task<ToolResult<object>> DescribeScanDetectors(
        [ToolOption(ToolOptionTag.Domain)] string? domain = null,
        [ToolOption(ToolOptionTag.Detector)] string? detector = null)
    {
        try
        {
            IEnumerable<ScanDescriptor> entries = scan_descriptors;

            if (!string.IsNullOrEmpty(domain))
            {
                string domainFilter = domain;
                entries = entries.Where(e => e.Domain.Equals(domainFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(detector))
            {
                if (!Enum.TryParse<DetectorId>(detector, ignoreCase: true, out var detectorEnum))
                {
                    return Task.FromResult(new ToolResult<object>() { Success = false, Data = $"Unknown detector '{detector}'." });
                }
                entries = entries.Where(e => e.Id == detectorEnum);
            }

            return Task.FromResult(new ToolResult<object>() { Success = true, Data = entries.ToList() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DescribeScanDetectors failed");
            return Task.FromResult(new ToolResult<object>() { Success = false, Data = $"DescribeScanDetectors failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Produces(DataTag.Report)]
    [Description("""
        Analyses a method from multiple angles. aspect values: controlFlow (return paths, throw sites, infinite loop detection → ControlFlowSummary), dataFlow (unassigned reads, written/read variables, closure captures → DataFlowSummary), pathCoverage (execution paths for test coverage → PathCoverageReport), unreachableCode (statements after unconditional return/throw → List<string>).
        """)]
    public async Task<ToolResult<object>> AnalyzeMethod(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        [ToolOption(ToolOptionTag.Aspect)] string aspect)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            switch (aspect)
            {
                case "controlFlow":
                    var resultControlFlow = await _refactoringEngine.AnalyzeControlFlowAsync(filePath, methodName, null, null, null);
                    return new ToolResult<object>() { Success = true, Data = resultControlFlow };
                case "dataFlow":
                    var resultDataFlow = await _refactoringEngine.AnalyzeDataFlowAsync(filePath, methodName, null, null, null);
                    return new ToolResult<object>() { Success = true, Data = resultDataFlow };
                case "pathCoverage":
                    var resultPathCoverage = await _controlFlowEngine.AnalyzePathCoverageAsync(filePath, methodName);
                    return new ToolResult<object>() { Success = true, Data = resultPathCoverage };
                case "unreachableCode":
                    var resultUnreachableCode = await _analysisEngine.DetectUnreachableCodeAsync(filePath, methodName);
                    return new ToolResult<object>() { Success = true, Data = resultUnreachableCode };
                default:
                    return new ToolResult<object>() { Success = false, Data = ($"Unknown aspect '{aspect}'. Valid values: controlFlow, dataFlow, pathCoverage, unreachableCode.") };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnalyzeMethod ({Aspect}) failed for '{MethodName}' in '{FilePath}'", aspect, methodName, filePath);
            return new ToolResult<object>() { Success = false, Data = ($"AnalyzeMethod failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    private static string RequireFile(string scope, string? scopeName)
    {
        if (scope != "file" || string.IsNullOrEmpty(scopeName))
        {
            return ("This detector requires scope='file' with a filePath as scopeName.");
        }
        return scopeName;
    }

    private static string RequireProject(string scope, string? scopeName)
    {
        if (scope != "project" || string.IsNullOrEmpty(scopeName))
        {
            return ("This detector requires scope='project' with a projectName as scopeName.");
        }
        return scopeName;
    }

    // ── get_scan_result ────────────────────────────────────────────────────────

    [McpServerTool]
    [Produces(DataTag.Report)]
    [Description("""
        Pages through a large scan result written to disk when output result payload exceeded the inline size threshold. Supply either scanId (resolves to .roslynsentinel/scans/scan_*_{scanId}.json) or filePath (must match the scan_*.json pattern). Returns ToolResult<object> with TotalRecords and HasMore.
        """)]
    public async Task<ToolResult<object>> GetScanResult(
        [Consumes(DataTag.ScanId)] string? scanId = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [ToolOption(ToolOptionTag.Offset)] int offset = 0)
    {
        FilePath filePath = _workspaceManager.SetFilePath(filepath);
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        string? resolvedPath = null;

        if (!string.IsNullOrEmpty(scanId) && !string.IsNullOrEmpty(solutionRoot))
        {
            var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "scans");
            if (Directory.Exists(dir))
            {
                resolvedPath = Directory
                    .EnumerateFiles(dir, $"scan_*_{scanId}.json")
                    .FirstOrDefault();
            }
        }
        else if (!string.IsNullOrEmpty(filePath))
        {
            // Validate: path must be inside the scans directory and match the scan_*.json pattern.
            var fileName = System.IO.Path.GetFileName(filePath);
            if (!string.IsNullOrEmpty(solutionRoot))
            {
                var scansDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "scans"));
                var candidate = System.IO.Path.GetFullPath(filePath);
                if (candidate.StartsWith(scansDir, StringComparison.OrdinalIgnoreCase)
                    && fileName.StartsWith("scan_", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    resolvedPath = candidate;
                }
            }
        }

        if (resolvedPath == null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                              "Scan file not found. Supply a valid scanId or filePath pointing to a scan_*.json file in the scans directory.")
            };
        }

        ScanWapper all;
        try
        {
            var json = await File.ReadAllTextAsync(resolvedPath);
            all = JsonSerializer.Deserialize<ScanWapper>(
                      json,
                      _jsonOptions)
                  ?? new ScanWapper();

            ToolResult<object> result;

            switch (all.Type)
            {
                case ScanWrapperType.MigrationCandidateFindingList:
                    {
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = JsonSerializer.Deserialize<List<MigrationCandidateFinding>>(all.Data.ToString(), _jsonOptions)
                        };
                        break;
                    }

                case ScanWrapperType.ApiSurfaceEntryList:
                    {
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                        };
                        break;
                    }
                case ScanWrapperType.CodeInventoryReport:
                    {
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                        };
                        break;
                    }
                default:
                    {
                        return new ToolResult<object>
                        {
                            Success = false,
                            Error = new ResultError("Exception",
                                          "Unknown scan result type.")
                        };
                    }
            }
            ;

            if (result.LargeResult?.SizeBytes > ScanResultHelper.ThresholdBytes)
            {

            }
        }
        catch (Exception ex)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                              "Failed to read scan file.", ex.Message)
            };
        }

        var page = all.Data.AsArray().Skip(offset).Take(limit).ToList();
        bool hasMore = (offset + limit) < all.Data.AsArray().Count;

        return new ToolResult<object>
        {
            Success = true,
            Data = page,
            TotalRecords = all.Data.AsArray().Count,
            HasMore = hasMore,
        };
    }

    internal static async Task<(bool offloaded, FilePath filePath, string? scanId, byte[] jsonBytes)> StoreScanResultAsync<T>(T data, string? solutionRoot, ScanWrapperType wrapperType)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        if (jsonBytes.Length <= ScanResultHelper.ThresholdBytes || string.IsNullOrEmpty(solutionRoot))
        {
            return (false, null, null, jsonBytes);
        }

        var wrapper = new ScanWapper
        {
            Type = wrapperType,
            Data = JsonSerializer.SerializeToNode(data, _jsonOptions)
        };

        var scanId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "scans");
        Directory.CreateDirectory(dir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePath = Path.Combine(dir, $"scan_{timestamp}_{scanId}.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(wrapper, _jsonOptions), new UTF8Encoding(false));
        return (true, filePath, scanId, jsonBytes);
    }

    internal sealed record ScanDescriptor(DetectorId Id, string Domain, string ScopeHint, string Description);

    internal static readonly ScanDescriptor[] scan_descriptors =
    [
        // async (20)
        new(DetectorId.async_in_constructor, "async", "file", "Finds constructors that call async methods or contain await expressions. Constructors cannot be async."),
        new(DetectorId.async_over_sync, "async", "file", "Finds async methods with no real await — wraps only completed tasks (async over sync anti-pattern)."),
        new(DetectorId.async_void_without_try_catch, "async", "file|solution", "Finds async void methods whose body is not wrapped in a try/catch. Unhandled exceptions crash the process."),
        new(DetectorId.blocking_calls_in, "async", "file", "Finds .Result/.Wait()/.GetAwaiter().GetResult() inside async methods — deadlock risk."),
        new(DetectorId.cancellation_token_not_forwarded, "async", "file|solution", "Finds async methods that have a CancellationToken parameter but don't forward it to awaitable callees."),
        new(DetectorId.configure_await_missing, "async", "file", "Finds awaits missing .ConfigureAwait(false) in library code."),
        new(DetectorId.inconsistent_async_suffix, "async", "any", "Finds async methods not ending with 'Async', and non-async methods that end with 'Async'."),
        new(DetectorId.mismatched_await, "async", "any", "Detects Task-returning method calls that are not awaited (potential fire-and-forget bugs)."),
        new(DetectorId.missing_cancellation_tokens, "async", "any", "Finds async Task/ValueTask methods lacking CancellationToken but calling cancellable methods."),
        new(DetectorId.sequential_independent_awaits, "async", "file|solution", "Detects consecutive independent awaits that could be parallelized with Task.WhenAll."),
        new(DetectorId.task_delay_usage, "async", "file", "Detects Task.Delay() usage patterns."),
        new(DetectorId.task_delay_zero_usage, "async", "file", "Detects redundant Task.Delay(0) calls."),
        new(DetectorId.task_run_in, "async", "file", "Detects 'await Task.Run(...)' patterns in server-side code — wasteful thread pool allocation."),
        new(DetectorId.task_void_usage, "async", "file", "Detects dangerous async void methods that can crash the process on unhandled exceptions."),
        new(DetectorId.task_when_all_usage, "async", "file", "Detects sequential awaits that could be parallelized with Task.WhenAll."),
        new(DetectorId.task_yield_usage, "async", "file", "Detects Task.Yield() calls."),
        new(DetectorId.unawaited_fire_and_forget, "async", "any", "Finds Task-returning method calls not awaited (fire-and-forget). Exceptions are silently swallowed."),
        new(DetectorId.unawaited_dispose, "async", "file|solution", "Detects DisposeAsync() calls that are not awaited — async cleanup finishes after the method returns."),
        new(DetectorId.unobserved_task_in_field, "async", "file|solution", "Finds Task/ValueTask assigned to fields without being awaited — silent failure risk."),
        new(DetectorId.value_task_misuse, "async", "file", "Detects invalid ValueTask usage: double-await, deferred-await, .Result access."),
        // concurrency (8)
        new(DetectorId.cas_loop_without_backoff, "concurrency", "any", "Detects CAS loops using Interlocked.CompareExchange with no back-off — can pin a CPU core under contention."),
        new(DetectorId.check_then_act_on_dictionary, "concurrency", "any", "Detects ContainsKey()+Add() race on Dictionary/ConcurrentDictionary outside a lock. Use GetOrAdd()/TryAdd()."),
        new(DetectorId.concurrent_collection_opportunities, "concurrency", "file", "Finds lock-protected List/Dictionary fields that could use ConcurrentDictionary or ImmutableDictionary."),
        new(DetectorId.double_checked_locking, "concurrency", "any", "Detects DCL pattern where the lazily-initialized field is not declared volatile (memory model violation)."),
        new(DetectorId.possible_deadlocks, "concurrency", "any", "Detects potential deadlock patterns."),
        new(DetectorId.semaphore_usage, "concurrency", "file", "Finds SemaphoreSlim usage with potentially missing Release() calls."),
        new(DetectorId.unsafe_lazy_init, "concurrency", "file", "Detects unsafe lazy initialization (if (_field == null) { _field = new X(); }) without volatile or Lazy<T>."),
        new(DetectorId.unsafe_lazy_init_thread, "concurrency", "any", "Detects DCL-style unsafe lazy init: non-volatile field, non-atomic check-then-set outside a lock."),
        // config (3)
        new(DetectorId.json_anti_patterns, "config", "file", "Detects unsafe System.Text.Json patterns: un-wrapped Parse (memory leak), GetProperty instead of TryGetProperty."),
        new(DetectorId.package_inconsistency, "config", "solution", "Checks for NuGet package version inconsistencies across projects in the solution."),
        new(DetectorId.project_consistency, "config", "solution", "Checks TargetFramework alignment and project naming convention adherence across the solution."),
        // convention (6)
        new(DetectorId.mutable_public_collection_properties, "convention", "project|solution", "Detects public collection properties (List/Dict/HashSet) with public non-init setters."),
        new(DetectorId.mutable_public_properties, "convention", "any", "Finds public mutable properties (public setter) on non-DTO public classes."),
        new(DetectorId.naming_violations, "convention", "any", "Checks _camelCase private fields, PascalCase methods, camelCase parameters."),
        new(DetectorId.readonly_field_candidates, "convention", "file", "Finds private non-readonly fields only ever assigned in constructors — can be marked readonly."),
        new(DetectorId.string_magic_values, "convention", "any", "Finds string literals appearing 3+ times — candidates for named constants."),
        new(DetectorId.todo_fixme_comments, "convention", "any", "Scans for TODO/FIXME/HACK/BUG/REVIEW comments sorted by severity."),
        // correctness (16)
        new(DetectorId.all_throw_sites, "correctness", "any", "Finds all throw statements across the scope, optionally sortable by exception-type frequency."),
        new(DetectorId.empty_catch_blocks, "correctness", "any", "Scans for empty catch blocks that silently swallow exceptions."),
        new(DetectorId.exception_handling, "correctness", "file", "Detects CatchAll, EmptyRethrow (throw ex;), SwallowedException, ExceptionAsControlFlow patterns."),
        new(DetectorId.memory_leaks, "correctness", "file", "Detects potential memory leaks (unhooked event subscriptions, etc.)."),
        new(DetectorId.misbound_overload_chains, "correctness", "project|solution", "Finds overload chains where a method calls the wrong overload (not the next-level one)."),
        new(DetectorId.missing_generic_constraints, "correctness", "any", "Finds 'new T()' without 'where T : new()' and similar missing generic constraints."),
        new(DetectorId.multiple_out_parameter_methods, "correctness", "any", "Finds methods with 2+ out parameters — suggests ValueTuple return instead."),
        new(DetectorId.non_exhaustive_enum_switches, "correctness", "any", "Finds switch statements on enums that don't handle all members and have no default case."),
        new(DetectorId.possible_infinite_loops, "correctness", "file", "Detects loops with no reachable exit condition on any code path."),
        new(DetectorId.redundant_cast, "correctness", "any", "Detects unnecessary type casts that can be removed."),
        new(DetectorId.resource_disposal, "correctness", "any", "Finds IDisposable objects not properly disposed (missing using/Dispose call)."),
        new(DetectorId.services_not_registered, "correctness", "project|solution", "Finds injected service types (interfaces, Service/Repository/etc. suffixed types) missing from DI registrations."),
        new(DetectorId.stack_overflow_risks, "correctness", "file", "Detects direct recursion, property self-read/write, override-calls-self, and mutual recursion patterns."),
        new(DetectorId.unbounded_recursion, "correctness", "project|solution", "Finds recursive methods without a depth guard or base-case check — StackOverflowException risk."),
        new(DetectorId.unbounded_static_collections, "correctness", "project|solution", "Finds static collections populated with .Add() but never .Clear()ed — memory exhaustion risk."),
        new(DetectorId.value_type_mutation_intent, "correctness", "any", "Flags value-type parameter reassignment inside the method body — change is invisible to the caller."),
        // dead-code (8)
        new(DetectorId.obsolete_callers, "dead-code", "any", "Finds all call sites invoking [Obsolete]-decorated methods."),
        new(DetectorId.uninstantiated_types, "dead-code", "project|solution", "Finds classes never instantiated anywhere in the solution or project."),
        new(DetectorId.unused_constructors, "dead-code", "file", "Identifies constructors never called anywhere in the solution."),
        new(DetectorId.unused_event_subscriptions, "dead-code", "file", "Finds event subscriptions that are never unsubscribed — potential memory leaks."),
        new(DetectorId.unused_interfaces, "dead-code", "project|solution", "Finds interfaces declared but never implemented."),
        new(DetectorId.unused_local_variables, "dead-code", "file", "Identifies local variables declared but never used within their scope."),
        new(DetectorId.unused_private_fields, "dead-code", "file", "Detects private fields never read or written."),
        new(DetectorId.unused_references, "dead-code", "project", "Finds unused NuGet package references in a project. Requires scope='project'."),
        // misc (2)
        new(DetectorId.anti_patterns, "misc", "any", "Detects AI-generated code anti-patterns: blocking waits, async void, string concat in loop, magic numbers, etc."),
        new(DetectorId.finalizer_on_disposable, "misc", "project|solution", "Finds IDisposable+finalizer combinations without a disposed-flag guard (double-free of unmanaged resources)."),
        // performance (11)
        new(DetectorId.boxing_allocations, "performance", "any", "Finds potential boxing allocations (value types cast to object, interface)."),
        new(DetectorId.implicit_nullable_boxing, "performance", "file|solution", "Detects Nullable<T> values cast to object — boxes the nullable, surprising null-equality behavior."),
        new(DetectorId.inefficient_string_comparisons, "performance", "any", "Finds string comparison pitfalls: case-insensitive without OrdinalIgnoreCase, etc."),
        new(DetectorId.linq_n1_patterns, "performance", "any", "Detects LINQ queries (Where/First/Any/Count) inside loop bodies — N+1 execution pattern."),
        new(DetectorId.linq_redundant_where, "performance", "file|solution", "Finds .Where(pred).First()/.Any()/.Count() collapsible to single-pass overloads."),
        new(DetectorId.multiple_enumeration, "performance", "file|solution", "Detects IEnumerable locals iterated more than once without a materializing call (ToList/ToArray)."),
        new(DetectorId.performance, "performance", "file", "Broad performance analysis of a single file."),
        new(DetectorId.re_do_s_patterns, "performance", "file", "Detects Regex patterns with nested quantifiers — catastrophic backtracking / ReDoS vulnerability."),
        new(DetectorId.regex_new_in_loop, "performance", "file", "Detects new Regex() construction inside loop bodies — recompiles the pattern every iteration."),
        new(DetectorId.string_format_in_loops, "performance", "file|solution", "Detects string interpolation or string.Format() inside loops — per-iteration allocation."),
        new(DetectorId.use_frozen_collections, "performance", "any", "Finds static readonly Dict/HashSet fields that could use FrozenDictionary/FrozenSet for better read performance."),
        // security (5)
        new(DetectorId.hardcoded_paths, "security", "any", "Finds hardcoded file system paths in the code."),
        new(DetectorId.reflection_usage, "security", "any", "Scans for System.Reflection usage (potential for dynamic invocation attacks)."),
        new(DetectorId.security, "security", "file", "Broad security vulnerability analysis of a single file."),
        new(DetectorId.sql_injection, "security", "any", "Detects possible SQL injection via dynamic string arguments to Execute*/FromSqlRaw/Query methods."),
        new(DetectorId.unvalidated_regex_source, "security", "file", "Detects Regex() calls with a non-literal pattern argument — regex injection and ReDoS attack vector."),
        // structure (15)
        new(DetectorId.circular_dependencies, "structure", "solution", "Identifies circular project references (A → B → A) in the solution."),
        new(DetectorId.circular_type_references, "structure", "project|solution", "Finds circular constructor-injection dependencies — the cycle that causes .NET DI container to throw at startup."),
        new(DetectorId.duplicate_blocks_in_hierarchy, "structure", "any", "Finds duplicate code blocks across a type hierarchy. scopeName = root type name (class or interface)."),
        new(DetectorId.duplicate_methods, "structure", "project|solution", "Finds structurally duplicate method implementations (same control-flow shape regardless of names)."),
        new(DetectorId.interface_extraction_candidates, "structure", "project|solution", "Finds public classes with 3+ public methods but no corresponding interface."),
        new(DetectorId.internal_classes_that_could_be_private, "structure", "project|solution", "Finds internal classes used only in a single file — could be made private or nested."),
        new(DetectorId.large_methods, "structure", "project|solution", "Finds methods exceeding 50 lines — extract-method candidates."),
        new(DetectorId.large_switch_statements, "structure", "project|solution", "Finds switch statements with 10+ cases — refactoring candidates."),
        new(DetectorId.large_types, "structure", "project|solution", "Finds types exceeding 500 lines — extract-class candidates."),
        new(DetectorId.layer_violations, "structure", "any", "Detects namespace-level layer architecture violations based on using directives."),
        new(DetectorId.long_parameter_list, "structure", "any", "Finds methods/constructors with 4+ parameters — suggests parameter object or builder."),
        new(DetectorId.namespace_path_mismatches, "structure", "project|solution", "Finds files where the declared namespace doesn't match the folder path (syntax-only, no compilation required)."),
        new(DetectorId.primitive_obsession, "structure", "any", "Finds methods where the same primitive type appears 3+ times as distinct parameters."),
        new(DetectorId.structural_smells, "structure", "any", "Scans for structural file issues: multiple types in one file, file/type name mismatches."),
        new(DetectorId.type_cohesion, "structure", "file", "Analyses LCOM cohesion metric per class — identifies god classes with low cohesion."),
    ];

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DetectorId
    {
        // async
        async_in_constructor, async_over_sync, async_void_without_try_catch,
        blocking_calls_in, cancellation_token_not_forwarded, configure_await_missing,
        inconsistent_async_suffix, mismatched_await, missing_cancellation_tokens,
        sequential_independent_awaits, task_delay_usage, task_delay_zero_usage,
        task_run_in, task_void_usage, task_when_all_usage, task_yield_usage,
        unawaited_fire_and_forget, unawaited_dispose, unobserved_task_in_field,
        value_task_misuse,
        // concurrency
        cas_loop_without_backoff, check_then_act_on_dictionary,
        concurrent_collection_opportunities, double_checked_locking,
        possible_deadlocks, semaphore_usage, unsafe_lazy_init, unsafe_lazy_init_thread,
        // config
        json_anti_patterns, package_inconsistency, project_consistency,
        // convention
        mutable_public_collection_properties, mutable_public_properties,
        naming_violations, readonly_field_candidates, string_magic_values,
        todo_fixme_comments,
        // correctness
        all_throw_sites, empty_catch_blocks, exception_handling, memory_leaks,
        misbound_overload_chains, missing_generic_constraints,
        multiple_out_parameter_methods, non_exhaustive_enum_switches,
        possible_infinite_loops, redundant_cast, resource_disposal,
        services_not_registered, stack_overflow_risks, unbounded_recursion,
        unbounded_static_collections, value_type_mutation_intent,
        // dead-code
        obsolete_callers, uninstantiated_types, unused_constructors,
        unused_event_subscriptions, unused_interfaces, unused_local_variables,
        unused_private_fields, unused_references,
        // misc
        anti_patterns, finalizer_on_disposable,
        // performance
        boxing_allocations, implicit_nullable_boxing, inefficient_string_comparisons,
        linq_n1_patterns, linq_redundant_where, multiple_enumeration, performance,
        re_do_s_patterns, regex_new_in_loop, string_format_in_loops,
        use_frozen_collections,
        // security
        hardcoded_paths, reflection_usage, security, sql_injection,
        unvalidated_regex_source,
        // structure
        circular_dependencies, circular_type_references, duplicate_blocks_in_hierarchy,
        duplicate_methods, interface_extraction_candidates,
        internal_classes_that_could_be_private, large_methods, large_switch_statements,
        large_types, layer_violations, long_parameter_list, namespace_path_mismatches,
        primitive_obsession, structural_smells, type_cohesion
    }

    [McpServerTool]
    [Produces(DataTag.ApiBaseline)]
    [Description("Compares a previously captured API surface baseline against current code and reports breaking changes: removed types, removed/renamed members, signature changes. Workflow: (1) call get_public_api_surface with persistBaseline=true to capture baseline, (2) make code changes, (3) call this tool with the baseline list. Scope with projectName/filePath matching step 1.")]
    public async Task<ToolResult<object>> ScanBreakingChanges(
       [ExternalInputRequired(DataTag.ApiBaseline)] List<PublicApiMember> baseline,
       [Consumes(DataTag.ProjectName)] string? projectName = null,
       [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            var result = await _breakingChangeEngine.DetectBreakingChangesAsync(baseline, projectName, filePath);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanBreakingChanges failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"ScanBreakingChanges failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Produces(DataTag.Report)]
    [Description("""
        Finds duplicate statement sequences within the methods of a single class using structural hashing (SyntaxKind-based — matches regardless of variable names or literal values). Returns clone groups with: StatementCount, HasControlFlowExit (flag only, does not block finding), SnippetPreview, CapturedVariables (would become parameters if extracted), ProducedVariables (would need to be returned if extracted), and Occurrences (method, start line, end line, file). minStatements=3 for aggressive detection, 6+ for substantial clones only.
        """)]
    public async Task<ToolResult<object>> ScanDuplicateBlocksInClass(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ClassName)] string className,
        [ToolOption(ToolOptionTag.Filter)] int minStatements = 4)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _cloneDetectionEngine.FindDuplicateBlocksInClassAsync(filePath, className, minStatements);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanDuplicateBlocksInClass failed for '{ClassName}' in '{FilePath}'", className, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"ScanDuplicateBlocksInClass failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Produces(DataTag.Report)]
    [Description("Returns the public API surface of a project. persistBaseline=false (default) → full List<ApiSurfaceEntry> with signatures, virtuality, and XML docs (for SDK documentation/API review). persistBaseline=true → compact List<PublicApiMember> baseline for passing to scan_breaking_changes. filePath scopes to a single file (persistBaseline=true only). includeMethods/includeProperties/includeTypes filter output (persistBaseline=false only). Returns a scanId and writes scan results to disk when output result payload exceeds the inline size threshold. Use get_scan_result(scanId) to retrieve the results.")]
    public async Task<ToolResult<object>> GetPublicApiSurface(
        [Consumes(DataTag.ProjectName, required: true)] string? projectName = null,
        [ToolOption(ToolOptionTag.PersistBaseline)] bool persistBaseline = false,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.IncludeMethods)] bool includeMethods = true,
        [ToolOption(ToolOptionTag.IncludeProperties)] bool includeProperties = true,
        [ToolOption(ToolOptionTag.IncludeTypes)] bool includeTypes = true)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            ToolResult<object> toolResult = new ToolResult<object>() { Success = false };

            if (persistBaseline)
            {
                var apiResult = await _breakingChangeEngine.GetPublicApiSurfaceAsync(projectName, filePath);

                var summaryResults = await SentinelScanTools.StoreScanResultAsync(apiResult, _workspaceManager.GetSolutionRoot(), ScanWrapperType.ApiSurfaceEntryList);

                if (summaryResults.offloaded)
                {
                    toolResult = new ToolResult<object>
                    {
                        Success = true,
                        TotalRecords = apiResult.Count,
                        HasMore = false,
                        LargeResult = new LargeResultInfo(
                            resultType: typeof(ApiSurfaceEntry).Name,
                            writtenToFile: true,
                            filePath: summaryResults.filePath.Absolute.ToString(),
                            scanId: summaryResults.scanId,
                            sizeBytes: summaryResults.jsonBytes.Length,
                            totalRecords: apiResult.Count,
                            message: $"Result written to file ({summaryResults.jsonBytes.Length} bytes, {apiResult.Count} records). " +
                                           $"Use get_scan_result(scanId: \"{summaryResults.scanId}\") to page through results. " +
                                           "Pass limit and offset to control page size (default limit: 50).")
                    };
                }
                else
                {
                    return new ToolResult<object>
                    {
                        Success = true,
                        Data = apiResult,
                        TotalRecords = apiResult.Count
                    };
                }
            }
            else
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    toolResult = new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError("", "projectName is required when persistBaseline=false.")
                    };
                    return toolResult;
                }

                var apiResult = await _discoveryEngine.GetPublicApiSurfaceAsync(projectName, includeMethods, includeProperties, includeTypes);

                var summaryResults = await SentinelScanTools.StoreScanResultAsync(apiResult, _workspaceManager.GetSolutionRoot(), ScanWrapperType.ApiSurfaceEntryList);

                if (summaryResults.offloaded)
                {
                    toolResult = new ToolResult<object>
                    {
                        Success = true,
                        TotalRecords = apiResult.Count,
                        HasMore = false,
                        LargeResult = new LargeResultInfo(
                            resultType: typeof(ApiSurfaceEntry).Name,
                            writtenToFile: true,
                            filePath: summaryResults.filePath.ToString(),
                            scanId: summaryResults.scanId,
                            sizeBytes: summaryResults.jsonBytes.Length,
                            totalRecords: apiResult.Count,
                            message: $"Result written to file ({summaryResults.jsonBytes.Length} bytes, {apiResult.Count} records). " +
                                           $"Use get_scan_result(scanId: \"{summaryResults.scanId}\") to page through results. " +
                                           "Pass limit and offset to control page size (default limit: 50).")
                    };
                }
                else
                {
                    return new ToolResult<object>
                    {
                        Success = true,
                        Data = apiResult,
                        TotalRecords = apiResult.Count
                    };
                }
            }

            return toolResult;
        }
        catch (Exception ex) when (ex is ArgumentException && ex.Message.Contains("not found in solution"))
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetPublicApiSurface failed: Project '{projectName}' not found in solution.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPublicApiSurface failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetPublicApiSurface failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }
}
// v2 — async domain extracted from concurrency; blocking_calls_in and unawaited_dispose relocated
// v2 — async domain extracted; scan_descriptors made internal; ScanOptions derived from scan_descriptors

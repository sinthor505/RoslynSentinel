using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

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
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelScanTools> _logger;

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
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool]
    [Description("""
        Dispatches a named detector across a file, project, or entire solution.

        detector  — one of 94 bare ids (see describe_scan_detectors for full descriptions):
          concurrency: async_in_constructor, async_over_sync, async_void_without_try_catch,
            cancellation_token_not_forwarded, cas_loop_without_backoff,
            check_then_act_on_dictionary, concurrent_collection_opportunities,
            configure_await_missing, double_checked_locking, inconsistent_async_suffix,
            mismatched_await, missing_cancellation_tokens, possible_deadlocks,
            semaphore_usage, sequential_independent_awaits, task_delay_usage,
            task_delay_zero_usage, task_run_in, task_void_usage, task_when_all_usage,
            task_yield_usage, unawaited_fire_and_forget, unobserved_task_in_field,
            unsafe_lazy_init, unsafe_lazy_init_thread, value_task_misuse
          config: json_anti_patterns, package_inconsistency, project_consistency
          convention: mutable_public_collection_properties, mutable_public_properties,
            naming_violations, readonly_field_candidates, string_magic_values,
            todo_fixme_comments
          correctness: all_throw_sites, empty_catch_blocks, exception_handling,
            memory_leaks, misbound_overload_chains, missing_generic_constraints,
            multiple_out_parameter_methods, non_exhaustive_enum_switches,
            possible_infinite_loops, redundant_cast, resource_disposal,
            services_not_registered, stack_overflow_risks, unawaked_dispose,
            unbounded_recursion, unbounded_static_collections, value_type_mutation_intent
          dead-code: obsolete_callers, uninstantiated_types, unused_constructors,
            unused_event_subscriptions, unused_interfaces, unused_local_variables,
            unused_private_fields, unused_references
          misc: anti_patterns, blocking_calls_in, finalizer_on_disposable
          performance: boxing_allocations, implicit_nullable_boxing,
            inefficient_string_comparisons, linq_n1_patterns, linq_redundant_where,
            multiple_enumeration, performance, re_do_s_patterns, regex_new_in_loop,
            string_format_in_loops, use_frozen_collections
          security: hardcoded_paths, reflection_usage, security, sql_injection,
            unvalidated_regex_source
          structure: circular_dependencies, circular_type_references,
            duplicate_blocks_in_hierarchy, duplicate_methods,
            interface_extraction_candidates, internal_classes_that_could_be_private,
            large_methods, large_switch_statements, large_types, layer_violations,
            long_parameter_list, namespace_path_mismatches, primitive_obsession,
            structural_smells, type_cohesion

        scope     — "file" | "project" | "solution"
        scopeName — filePath when scope=file; projectName when scope=project; omit for solution.
                    For duplicate_blocks_in_hierarchy, scopeName is the root type name.

        File-scope-only detectors require scope="file" and a valid scopeName (filePath).
        unused_references requires scope="project".
        Call describe_scan_detectors to see the scope hint for each detector.
        """)]
    public async Task<object> Scan(
        string detector,
        string scope,
        string? scopeName = null,
        CancellationToken cancellationToken = default)
    {
        string? filePath = scope == "file" ? scopeName : null;
        string? projectName = scope == "project" ? scopeName : null;

        switch (detector)
        {
            // ── concurrency ────────────────────────────────────────────────────

            case "async_in_constructor":
                return (object)await _asyncSafetyEngine.FindAsyncInConstructorAsync(RequireFile(scope, scopeName));
            case "async_over_sync":
                return (object)await _asyncSafetyEngine.FindAsyncOverSyncAsync(RequireFile(scope, scopeName));
            case "async_void_without_try_catch":
                return (object)await _asyncSafetyEngine.FindAsyncVoidWithoutTryCatchAsync(filePath);
            case "cancellation_token_not_forwarded":
                return (object)await _asyncSafetyEngine.FindCancellationTokenNotForwardedAsync(filePath);
            case "cas_loop_without_backoff":
                return (object)await _threadSafetyEngine.FindCasLoopWithoutBackoffAsync(projectName, filePath);
            case "check_then_act_on_dictionary":
                return (object)await _threadSafetyEngine.FindCheckThenActOnDictionaryAsync(projectName, filePath);
            case "concurrent_collection_opportunities":
                return (object)await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync(RequireFile(scope, scopeName));
            case "configure_await_missing":
                return (object)await _asyncSafetyEngine.FindConfigureAwaitMissingAsync(RequireFile(scope, scopeName));
            case "double_checked_locking":
                return (object)await _threadSafetyEngine.FindDoubleCheckedLockingAsync(projectName, filePath);
            case "inconsistent_async_suffix":
                return (object)await _antiPatternEngine.FindInconsistentAsyncSuffixAsync(filePath, projectName);
            case "mismatched_await":
                return (object)await _analysisEngine.DetectMismatchedAwaitAsync(filePath, projectName);
            case "missing_cancellation_tokens":
                return (object)await _antiPatternEngine.FindMissingCancellationTokensAsync(filePath, projectName);
            case "possible_deadlocks":
                return (object)await _analysisEngine.FindPossibleDeadlocksAsync(projectName, filePath);
            case "semaphore_usage":
                return (object)await _analysisEngine.AnalyzeSemaphoreUsageAsync(RequireFile(scope, scopeName));
            case "sequential_independent_awaits":
                return (object)await _asyncSafetyEngine.FindSequentialIndependentAwaitsAsync(filePath);
            case "task_delay_usage":
                return (object)await _asyncSafetyEngine.FindTaskDelayUsageAsync(RequireFile(scope, scopeName));
            case "task_delay_zero_usage":
                return (object)await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync(RequireFile(scope, scopeName));
            case "task_run_in":
                return (object)await _asyncSafetyEngine.FindTaskRunInAsyncAsync(RequireFile(scope, scopeName));
            case "task_void_usage":
                return (object)await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync(RequireFile(scope, scopeName));
            case "task_when_all_usage":
                return (object)await _asyncSafetyEngine.FindTaskWhenAllUsageAsync(RequireFile(scope, scopeName));
            case "task_yield_usage":
                return (object)await _asyncSafetyEngine.FindTaskYieldUsageAsync(RequireFile(scope, scopeName));
            case "unawaited_fire_and_forget":
                return (object)await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync(filePath, projectName);
            case "unobserved_task_in_field":
                return (object)await _asyncSafetyEngine.FindUnobservedTaskInFieldAsync(filePath);
            case "unsafe_lazy_init":
                return (object)await _asyncSafetyEngine.FindUnsafeLazyInitAsync(RequireFile(scope, scopeName));
            case "unsafe_lazy_init_thread":
                return (object)await _threadSafetyEngine.FindUnsafeLazyInitAsync(projectName, filePath);
            case "value_task_misuse":
                return (object)await _asyncSafetyEngine.DetectValueTaskMisuseAsync(RequireFile(scope, scopeName));

            // ── config ─────────────────────────────────────────────────────────

            case "json_anti_patterns":
                return (object)await _securityEngine.DetectJsonAntiPatternsAsync(RequireFile(scope, scopeName));
            case "package_inconsistency":
                return (object)await _dependencyEngine.CheckPackageInconsistencyAsync();
            case "project_consistency":
                return (object)await _projectConsistencyEngine.CheckConsistencyAsync();

            // ── convention ─────────────────────────────────────────────────────

            case "mutable_public_collection_properties":
                return (object)await _codeStyleAnalysisEngine.FindMutablePublicCollectionPropertiesAsync(projectName);
            case "mutable_public_properties":
                return (object)await _antiPatternEngine.FindMutablePublicPropertiesAsync(filePath, projectName);
            case "naming_violations":
                return (object)await _antiPatternEngine.FindNamingViolationsAsync(filePath, projectName);
            case "readonly_field_candidates":
                return (object)await _symbolNavigationEngine.FindReadonlyFieldCandidatesAsync(RequireFile(scope, scopeName));
            case "string_magic_values":
                return (object)await _antiPatternEngine.FindStringMagicValuesAsync(filePath, projectName, 3);
            case "todo_fixme_comments":
                return (object)await _discoveryEngine.FindTodoFixmeCommentsAsync(filePath, projectName);

            // ── correctness ────────────────────────────────────────────────────

            case "all_throw_sites":
                return (object)await _discoveryEngine.FindAllThrowSitesAsync(null, filePath, projectName, false);
            case "empty_catch_blocks":
                return (object)await _analysisEngine.CheckForEmptyCatchBlocksAsync(filePath, projectName);
            case "exception_handling":
                return (object)await _antiPatternEngine.AnalyzeExceptionHandlingAsync(RequireFile(scope, scopeName));
            case "memory_leaks":
                return (object)await _analysisEngine.DetectMemoryLeaksAsync(RequireFile(scope, scopeName));
            case "misbound_overload_chains":
                return (object)await _analysisEngine.FindMisboundOverloadChainsAsync(projectName);
            case "missing_generic_constraints":
                return (object)await _analysisEngine.FindMissingGenericConstraintsAsync(projectName, filePath);
            case "multiple_out_parameter_methods":
                return (object)await _antiPatternEngine.FindMultipleOutParameterMethodsAsync(filePath, projectName);
            case "non_exhaustive_enum_switches":
                return (object)await _controlFlowEngine.FindNonExhaustiveEnumSwitchesAsync(filePath, projectName);
            case "possible_infinite_loops":
                return (object)await _analysisEngine.FindPossibleInfiniteLoopsAsync(RequireFile(scope, scopeName));
            case "redundant_cast":
                return (object)await _analysisEngine.CheckForRedundantCastAsync(filePath, projectName);
            case "resource_disposal":
                return (object)await _analysisEngine.OptimizeResourceDisposalAsync(filePath, projectName);
            case "services_not_registered":
                return (object)await _dependencyInjectionEngine.FindServicesNotRegisteredAsync(projectName);
            case "stack_overflow_risks":
                return (object)await _stackOverflowEngine.AnalyzeStackOverflowRisksAsync(RequireFile(scope, scopeName), false);
            case "unawaked_dispose":
                return (object)await _asyncSafetyEngine.FindUnawakedDisposeAsyncAsync(filePath);
            case "unbounded_recursion":
                return (object)await _analysisEngine.FindUnboundedRecursionAsync(projectName);
            case "unbounded_static_collections":
                return (object)await _analysisEngine.FindUnboundedStaticCollectionsAsync(projectName);
            case "value_type_mutation_intent":
                return (object)await _antiPatternEngine.FindValueTypeMutationIntentAsync(filePath, projectName);

            // ── dead-code ──────────────────────────────────────────────────────

            case "obsolete_callers":
                return (object)await _antiPatternEngine.FindObsoleteCallersAsync(null, filePath, projectName, cancellationToken);
            case "uninstantiated_types":
                return (object)await _analysisEngine.FindUninstantiatedTypesAsync(projectName);
            case "unused_constructors":
                return (object)await _deadCodeEngine.FindUnusedConstructorsAsync(RequireFile(scope, scopeName));
            case "unused_event_subscriptions":
                return (object)(await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync(RequireFile(scope, scopeName)) ?? new List<DeadCodeReport>());
            case "unused_interfaces":
                return (object)await _analysisEngine.FindUnusedInterfacesAsync(projectName);
            case "unused_local_variables":
                return (object)await _deadCodeEngine.DetectUnusedLocalVariablesAsync(RequireFile(scope, scopeName));
            case "unused_private_fields":
                return (object)await _deadCodeEngine.DetectUnusedPrivateFieldsAsync(RequireFile(scope, scopeName));
            case "unused_references":
                return (object)await _dependencyEngine.FindUnusedReferencesAsync(RequireProject(scope, scopeName));

            // ── misc ───────────────────────────────────────────────────────────

            case "anti_patterns":
                return (object)await _antiPatternEngine.DetectAntiPatternsAsync(filePath, projectName, null);
            case "blocking_calls_in":
                return (object)await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync(RequireFile(scope, scopeName));
            case "finalizer_on_disposable":
                return (object)await _analysisEngine.FindFinalizerOnDisposableAsync(projectName);

            // ── performance ────────────────────────────────────────────────────

            case "boxing_allocations":
                return (object)await _analysisEngine.FindBoxingAllocationsAsync(filePath, projectName);
            case "implicit_nullable_boxing":
                return (object)await _performanceEngine.FindImplicitNullableBoxingAsync(filePath);
            case "inefficient_string_comparisons":
                return (object)await _analysisEngine.DetectInefficientStringComparisonsAsync(filePath, projectName);
            case "linq_n1_patterns":
                return (object)await _performanceEngine.FindLinqN1PatternsAsync(filePath, projectName);
            case "linq_redundant_where":
                return (object)await _performanceEngine.FindLinqRedundantWhereAsync(filePath);
            case "multiple_enumeration":
                return (object)await _performanceEngine.FindMultipleEnumerationAsync(filePath);
            case "performance":
                return (object)await _performanceEngine.AnalyzePerformanceAsync(RequireFile(scope, scopeName));
            case "re_do_s_patterns":
                return (object)await _securityEngine.FindReDoSPatternsAsync(RequireFile(scope, scopeName));
            case "regex_new_in_loop":
                return (object)await _securityEngine.FindRegexNewInLoopAsync(RequireFile(scope, scopeName));
            case "string_format_in_loops":
                return (object)await _performanceEngine.FindStringFormatInLoopsAsync(filePath);
            case "use_frozen_collections":
                return (object)await _codeStyleEngine.FindUseFrozenCollectionsAsync(filePath, projectName);

            // ── security ───────────────────────────────────────────────────────

            case "hardcoded_paths":
                return (object)await _securityEngine.FindHardcodedPathsAsync(filePath, projectName);
            case "reflection_usage":
                return (object)await _analysisEngine.DetectReflectionUsageAsync(filePath, projectName);
            case "security":
                return (object)await _securityEngine.AnalyzeSecurityAsync(RequireFile(scope, scopeName));
            case "sql_injection":
                return (object)await _securityEngine.CheckForSqlInjectionAsync(filePath, projectName);
            case "unvalidated_regex_source":
                return (object)await _securityEngine.FindUnvalidatedRegexSourceAsync(RequireFile(scope, scopeName));

            // ── structure ──────────────────────────────────────────────────────

            case "circular_dependencies":
                return (object)await _analysisEngine.FindCircularDependenciesAsync();
            case "circular_type_references":
                return (object)await _analysisEngine.FindCircularTypeReferencesAsync(projectName);
            case "duplicate_blocks_in_hierarchy":
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    throw new ArgumentException("duplicate_blocks_in_hierarchy requires scopeName to be the root type name.");
                }
                return (object)await _cloneDetectionEngine.FindDuplicateBlocksInHierarchyAsync(scopeName, null, 4);
            }
            case "duplicate_methods":
                return (object)await _analysisEngine.FindDuplicateMethodsAsync(5, projectName);
            case "interface_extraction_candidates":
                return (object)await _analysisEngine.FindInterfaceExtractionCandidatesAsync(3, projectName);
            case "internal_classes_that_could_be_private":
                return (object)await _analysisEngine.FindInternalClassesThatCouldBePrivateAsync(projectName);
            case "large_methods":
                return (object)await _analysisEngine.FindLargeMethodsAsync(50, projectName);
            case "large_switch_statements":
                return (object)await _analysisEngine.FindLargeSwitchStatementsAsync(10, projectName);
            case "large_types":
                return (object)await _analysisEngine.FindLargeTypesAsync(500, projectName);
            case "layer_violations":
                return (object)await _architecturalEngine.DetectLayerViolationsAsync(projectName, filePath);
            case "long_parameter_list":
                return (object)await _antiPatternEngine.FindLongParameterListAsync(filePath, projectName, 4);
            case "namespace_path_mismatches":
            {
                var solution = await _workspaceManager.GetBranchedSolutionAsync();
                return (object)await _analysisEngine.FindNamespacePathMismatchesAsync(solution, projectName, cancellationToken);
            }
            case "primitive_obsession":
                return (object)await _antiPatternEngine.FindPrimitiveObsessionAsync(filePath, projectName);
            case "structural_smells":
                return (object)await _projectStructureEngine.FindStructuralSmellsAsync(ProjectStructureEngine.StructuralSmellType.All, projectName, filePath);
            case "type_cohesion":
                return (object)await _metricsEngine.AnalyzeTypeCohesionAsync(RequireFile(scope, scopeName), null);

            default:
                throw new ArgumentException($"Unknown detector '{detector}'. Call describe_scan_detectors() for the full list.");
        }
    }

    [McpServerTool]
    [Description("""
        Returns the catalogue of available scan detectors.

        domain:   filter by domain — concurrency | config | convention | correctness |
                  dead-code | misc | performance | security | structure
        detector: return info for a single detector by exact id.
        If both are omitted, all 94 detectors are returned.

        Each entry has: Id, Domain, ScopeHint (file | project | solution | any combinations), Description.
        """)]
    public Task<object> DescribeScanDetectors(string? domain = null, string? detector = null)
    {
        IEnumerable<ScanDescriptor> entries = s_descriptors;
        if (!string.IsNullOrEmpty(domain))
        {
            string domainFilter = domain;
            entries = entries.Where(e => e.Domain.Equals(domainFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(detector))
        {
            string detectorFilter = detector;
            entries = entries.Where(e => e.Id.Equals(detectorFilter, StringComparison.OrdinalIgnoreCase));
        }
        return Task.FromResult<object>(entries.ToList());
    }

    [McpServerTool]
    [Description("""
        Analyses a specific method from multiple angles.

        aspect:
          controlFlow    — return paths, throw sites, infinite loop detection (returns ControlFlowSummary)
          dataFlow       — unassigned reads, written/read variables, closure captures (returns DataFlowSummary)
          pathCoverage   — execution paths for test coverage analysis (returns PathCoverageReport)
          unreachableCode— statements after unconditional return/throw (returns List<string>)
        """)]
    public async Task<object> AnalyzeMethod(string filePath, string methodName, string aspect)
    {
        switch (aspect)
        {
            case "controlFlow":
                return (object)await _refactoringEngine.AnalyzeControlFlowAsync(filePath, methodName, null, null, null);
            case "dataFlow":
                return (object)await _refactoringEngine.AnalyzeDataFlowAsync(filePath, methodName, null, null, null);
            case "pathCoverage":
                return (object)await _controlFlowEngine.AnalyzePathCoverageAsync(filePath, methodName);
            case "unreachableCode":
                return (object)await _analysisEngine.DetectUnreachableCodeAsync(filePath, methodName);
            default:
                throw new ArgumentException($"Unknown aspect '{aspect}'. Valid values: controlFlow, dataFlow, pathCoverage, unreachableCode.");
        }
    }

    private static string RequireFile(string scope, string? scopeName)
    {
        if (scope != "file" || string.IsNullOrEmpty(scopeName))
        {
            throw new ArgumentException("This detector requires scope='file' with a filePath as scopeName.");
        }
        return scopeName;
    }

    private static string RequireProject(string scope, string? scopeName)
    {
        if (scope != "project" || string.IsNullOrEmpty(scopeName))
        {
            throw new ArgumentException("This detector requires scope='project' with a projectName as scopeName.");
        }
        return scopeName;
    }

    private sealed record ScanDescriptor(string Id, string Domain, string ScopeHint, string Description);

    private static readonly ScanDescriptor[] s_descriptors =
    [
        // concurrency (26)
        new("async_in_constructor", "concurrency", "file", "Finds constructors that call async methods or contain await expressions. Constructors cannot be async."),
        new("async_over_sync", "concurrency", "file", "Finds async methods with no real await — wraps only completed tasks (async over sync anti-pattern)."),
        new("async_void_without_try_catch", "concurrency", "file|solution", "Finds async void methods whose body is not wrapped in a try/catch. Unhandled exceptions crash the process."),
        new("cancellation_token_not_forwarded", "concurrency", "file|solution", "Finds async methods that have a CancellationToken parameter but don't forward it to awaitable callees."),
        new("cas_loop_without_backoff", "concurrency", "any", "Detects CAS loops using Interlocked.CompareExchange with no back-off — can pin a CPU core under contention."),
        new("check_then_act_on_dictionary", "concurrency", "any", "Detects ContainsKey()+Add() race on Dictionary/ConcurrentDictionary outside a lock. Use GetOrAdd()/TryAdd()."),
        new("concurrent_collection_opportunities", "concurrency", "file", "Finds lock-protected List/Dictionary fields that could use ConcurrentDictionary or ImmutableDictionary."),
        new("configure_await_missing", "concurrency", "file", "Finds awaits missing .ConfigureAwait(false) in library code."),
        new("double_checked_locking", "concurrency", "any", "Detects DCL pattern where the lazily-initialized field is not declared volatile (memory model violation)."),
        new("inconsistent_async_suffix", "concurrency", "any", "Finds async methods not ending with 'Async', and non-async methods that end with 'Async'."),
        new("mismatched_await", "concurrency", "any", "Detects Task-returning method calls that are not awaited (potential fire-and-forget bugs)."),
        new("missing_cancellation_tokens", "concurrency", "any", "Finds async Task/ValueTask methods lacking CancellationToken but calling cancellable methods."),
        new("possible_deadlocks", "concurrency", "any", "Detects potential deadlock patterns."),
        new("semaphore_usage", "concurrency", "file", "Finds SemaphoreSlim usage with potentially missing Release() calls."),
        new("sequential_independent_awaits", "concurrency", "file|solution", "Detects consecutive independent awaits that could be parallelized with Task.WhenAll."),
        new("task_delay_usage", "concurrency", "file", "Detects Task.Delay() usage patterns."),
        new("task_delay_zero_usage", "concurrency", "file", "Detects redundant Task.Delay(0) calls."),
        new("task_run_in", "concurrency", "file", "Detects 'await Task.Run(...)' patterns in server-side code — wasteful thread pool allocation."),
        new("task_void_usage", "concurrency", "file", "Detects dangerous async void methods that can crash the process on unhandled exceptions."),
        new("task_when_all_usage", "concurrency", "file", "Detects sequential awaits that could be parallelized with Task.WhenAll."),
        new("task_yield_usage", "concurrency", "file", "Detects Task.Yield() calls."),
        new("unawaited_fire_and_forget", "concurrency", "any", "Finds Task-returning method calls not awaited (fire-and-forget). Exceptions are silently swallowed."),
        new("unobserved_task_in_field", "concurrency", "file|solution", "Finds Task/ValueTask assigned to fields without being awaited — silent failure risk."),
        new("unsafe_lazy_init", "concurrency", "file", "Detects unsafe lazy initialization (if (_field == null) { _field = new X(); }) without volatile or Lazy<T>."),
        new("unsafe_lazy_init_thread", "concurrency", "any", "Detects DCL-style unsafe lazy init: non-volatile field, non-atomic check-then-set outside a lock."),
        new("value_task_misuse", "concurrency", "file", "Detects invalid ValueTask usage: double-await, deferred-await, .Result access."),
        // config (3)
        new("json_anti_patterns", "config", "file", "Detects unsafe System.Text.Json patterns: un-wrapped Parse (memory leak), GetProperty instead of TryGetProperty."),
        new("package_inconsistency", "config", "solution", "Checks for NuGet package version inconsistencies across projects in the solution."),
        new("project_consistency", "config", "solution", "Checks TargetFramework alignment and project naming convention adherence across the solution."),
        // convention (6)
        new("mutable_public_collection_properties", "convention", "project|solution", "Detects public collection properties (List/Dict/HashSet) with public non-init setters."),
        new("mutable_public_properties", "convention", "any", "Finds public mutable properties (public setter) on non-DTO public classes."),
        new("naming_violations", "convention", "any", "Checks _camelCase private fields, PascalCase methods, camelCase parameters."),
        new("readonly_field_candidates", "convention", "file", "Finds private non-readonly fields only ever assigned in constructors — can be marked readonly."),
        new("string_magic_values", "convention", "any", "Finds string literals appearing 3+ times — candidates for named constants."),
        new("todo_fixme_comments", "convention", "any", "Scans for TODO/FIXME/HACK/BUG/REVIEW comments sorted by severity."),
        // correctness (17)
        new("all_throw_sites", "correctness", "any", "Finds all throw statements across the scope, optionally sortable by exception-type frequency."),
        new("empty_catch_blocks", "correctness", "any", "Scans for empty catch blocks that silently swallow exceptions."),
        new("exception_handling", "correctness", "file", "Detects CatchAll, EmptyRethrow (throw ex;), SwallowedException, ExceptionAsControlFlow patterns."),
        new("memory_leaks", "correctness", "file", "Detects potential memory leaks (unhooked event subscriptions, etc.)."),
        new("misbound_overload_chains", "correctness", "project|solution", "Finds overload chains where a method calls the wrong overload (not the next-level one)."),
        new("missing_generic_constraints", "correctness", "any", "Finds 'new T()' without 'where T : new()' and similar missing generic constraints."),
        new("multiple_out_parameter_methods", "correctness", "any", "Finds methods with 2+ out parameters — suggests ValueTuple return instead."),
        new("non_exhaustive_enum_switches", "correctness", "any", "Finds switch statements on enums that don't handle all members and have no default case."),
        new("possible_infinite_loops", "correctness", "file", "Detects loops with no reachable exit condition on any code path."),
        new("redundant_cast", "correctness", "any", "Detects unnecessary type casts that can be removed."),
        new("resource_disposal", "correctness", "any", "Finds IDisposable objects not properly disposed (missing using/Dispose call)."),
        new("services_not_registered", "correctness", "project|solution", "Finds injected service types (interfaces, Service/Repository/etc. suffixed types) missing from DI registrations."),
        new("stack_overflow_risks", "correctness", "file", "Detects direct recursion, property self-read/write, override-calls-self, and mutual recursion patterns."),
        new("unawaked_dispose", "correctness", "file|solution", "Detects DisposeAsync() calls that are not awaited — async cleanup finishes after the method returns."),
        new("unbounded_recursion", "correctness", "project|solution", "Finds recursive methods without a depth guard or base-case check — StackOverflowException risk."),
        new("unbounded_static_collections", "correctness", "project|solution", "Finds static collections populated with .Add() but never .Clear()ed — memory exhaustion risk."),
        new("value_type_mutation_intent", "correctness", "any", "Flags value-type parameter reassignment inside the method body — change is invisible to the caller."),
        // dead-code (8)
        new("obsolete_callers", "dead-code", "any", "Finds all call sites invoking [Obsolete]-decorated methods."),
        new("uninstantiated_types", "dead-code", "project|solution", "Finds classes never instantiated anywhere in the solution or project."),
        new("unused_constructors", "dead-code", "file", "Identifies constructors never called anywhere in the solution."),
        new("unused_event_subscriptions", "dead-code", "file", "Finds event subscriptions that are never unsubscribed — potential memory leaks."),
        new("unused_interfaces", "dead-code", "project|solution", "Finds interfaces declared but never implemented."),
        new("unused_local_variables", "dead-code", "file", "Identifies local variables declared but never used within their scope."),
        new("unused_private_fields", "dead-code", "file", "Detects private fields never read or written."),
        new("unused_references", "dead-code", "project", "Finds unused NuGet package references in a project. Requires scope='project'."),
        // misc (3)
        new("anti_patterns", "misc", "any", "Detects AI-generated code anti-patterns: blocking waits, async void, string concat in loop, magic numbers, etc."),
        new("blocking_calls_in", "misc", "file", "Finds .Result/.Wait()/.GetAwaiter().GetResult() inside async methods — deadlock risk."),
        new("finalizer_on_disposable", "misc", "project|solution", "Finds IDisposable+finalizer combinations without a disposed-flag guard (double-free of unmanaged resources)."),
        // performance (11)
        new("boxing_allocations", "performance", "any", "Finds potential boxing allocations (value types cast to object, interface)."),
        new("implicit_nullable_boxing", "performance", "file|solution", "Detects Nullable<T> values cast to object — boxes the nullable, surprising null-equality behavior."),
        new("inefficient_string_comparisons", "performance", "any", "Finds string comparison pitfalls: case-insensitive without OrdinalIgnoreCase, etc."),
        new("linq_n1_patterns", "performance", "any", "Detects LINQ queries (Where/First/Any/Count) inside loop bodies — N+1 execution pattern."),
        new("linq_redundant_where", "performance", "file|solution", "Finds .Where(pred).First()/.Any()/.Count() collapsible to single-pass overloads."),
        new("multiple_enumeration", "performance", "file|solution", "Detects IEnumerable locals iterated more than once without a materializing call (ToList/ToArray)."),
        new("performance", "performance", "file", "Broad performance analysis of a single file."),
        new("re_do_s_patterns", "performance", "file", "Detects Regex patterns with nested quantifiers — catastrophic backtracking / ReDoS vulnerability."),
        new("regex_new_in_loop", "performance", "file", "Detects new Regex() construction inside loop bodies — recompiles the pattern every iteration."),
        new("string_format_in_loops", "performance", "file|solution", "Detects string interpolation or string.Format() inside loops — per-iteration allocation."),
        new("use_frozen_collections", "performance", "any", "Finds static readonly Dict/HashSet fields that could use FrozenDictionary/FrozenSet for better read performance."),
        // security (5)
        new("hardcoded_paths", "security", "any", "Finds hardcoded file system paths in the code."),
        new("reflection_usage", "security", "any", "Scans for System.Reflection usage (potential for dynamic invocation attacks)."),
        new("security", "security", "file", "Broad security vulnerability analysis of a single file."),
        new("sql_injection", "security", "any", "Detects possible SQL injection via dynamic string arguments to Execute*/FromSqlRaw/Query methods."),
        new("unvalidated_regex_source", "security", "file", "Detects Regex() calls with a non-literal pattern argument — regex injection and ReDoS attack vector."),
        // structure (15)
        new("circular_dependencies", "structure", "solution", "Identifies circular project references (A → B → A) in the solution."),
        new("circular_type_references", "structure", "project|solution", "Finds circular constructor-injection dependencies — the cycle that causes .NET DI container to throw at startup."),
        new("duplicate_blocks_in_hierarchy", "structure", "any", "Finds duplicate code blocks across a type hierarchy. scopeName = root type name (class or interface)."),
        new("duplicate_methods", "structure", "project|solution", "Finds structurally duplicate method implementations (same control-flow shape regardless of names)."),
        new("interface_extraction_candidates", "structure", "project|solution", "Finds public classes with 3+ public methods but no corresponding interface."),
        new("internal_classes_that_could_be_private", "structure", "project|solution", "Finds internal classes used only in a single file — could be made private or nested."),
        new("large_methods", "structure", "project|solution", "Finds methods exceeding 50 lines — extract-method candidates."),
        new("large_switch_statements", "structure", "project|solution", "Finds switch statements with 10+ cases — refactoring candidates."),
        new("large_types", "structure", "project|solution", "Finds types exceeding 500 lines — extract-class candidates."),
        new("layer_violations", "structure", "any", "Detects namespace-level layer architecture violations based on using directives."),
        new("long_parameter_list", "structure", "any", "Finds methods/constructors with 4+ parameters — suggests parameter object or builder."),
        new("namespace_path_mismatches", "structure", "project|solution", "Finds files where the declared namespace doesn't match the folder path (syntax-only, no compilation required)."),
        new("primitive_obsession", "structure", "any", "Finds methods where the same primitive type appears 3+ times as distinct parameters."),
        new("structural_smells", "structure", "any", "Scans for structural file issues: multiple types in one file, file/type name mismatches."),
        new("type_cohesion", "structure", "file", "Analyses LCOM cohesion metric per class — identifies god classes with low cohesion."),
    ];
}

using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Shared service registration helpers used by both the stdio server (Program.cs)
/// and the separate HTTP host (RoslynSentinel.HttpHost).
/// </summary>
public static class RoslynSentinelServiceExtensionsBasic
{
    /// <summary>
    /// Registers process-wide host settings (currently just <see cref="OperatingMode"/>). Call
    /// this <em>before</em> <see cref="AddRoslynSentinelEnginesBasic"/>, which TryAdds a
    /// Production-mode default for callers (chiefly the test assemblies) that never set one.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelHostOptions(
        this IServiceCollection services,
        OperatingMode operatingMode)
    {
        services.AddSingleton(new SentinelHostOptions { OperatingMode = operatingMode });
        return services;
    }

    /// <summary>
    /// Registers all Roslyn analysis engine singletons into the DI container.
    /// </summary>
    /// <remarks>
    /// The commented-out lines below are engines Advanced registers instead (see
    /// <see cref="RoslynSentinel.Server.Advanced.RoslynSentinelServiceExtensionsAdvanced.AddRoslynSentinelEnginesAdvanced"/>).
    /// Don't uncomment one here without checking whether Advanced already live-registers it —
    /// doing so would register the same engine type in both, not just in Basic.
    /// </remarks>
    public static IServiceCollection AddRoslynSentinelEnginesBasic(this IServiceCollection services)
    {
        services.AddSingleton<SentinelConfiguration>();
        // Defaults to Production. TryAdd (not Add) so a host that called
        // AddRoslynSentinelHostOptions first keeps its own value — registering unconditionally
        // here would make the last-wins order depend on which extension the host called last.
        services.TryAddSingleton(new SentinelHostOptions());
        services.AddSingleton<PersistentWorkspaceManager>();
        services.AddSingleton<IWorkspaceManager>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<ISolutionProvider>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<DiffEngine>();
        services.AddSingleton<ValidationEngine>();
        services.AddSingleton<ImpactAnalyzer>();
        services.AddSingleton<RefactoringEngine>();
        // services.AddSingleton<MetricsEngine>();
        // services.AddSingleton<CodeHealingEngine>();
        services.AddSingleton<AnalysisEngine>();
        // services.AddSingleton<PerformanceEngine>();
        // services.AddSingleton<SecurityEngine>();
        // services.AddSingleton<TestingEngine>();
        services.AddSingleton<CodeGenerationEngine>();
        // services.AddSingleton<ModernizationEngine>();
        // services.AddSingleton<DependencyInjectionEngine>();
        services.AddSingleton<ThreadSafetyEngine>();
        // services.AddSingleton<ArchitecturalEngine>();
        // services.AddSingleton<AdvancedRefactoringEngine>();
        // services.AddSingleton<DocumentationEngine>();
        // services.AddSingleton<SecurityAndSafetyEngine>();
        // services.AddSingleton<ApiIntegrationEngine>();
        services.AddSingleton<InventoryEngine>();
        // services.AddSingleton<AsyncOptimizationEngine>();
        services.AddSingleton<InstrumentationEngine>();
        // services.AddSingleton<AdvancedTypeEngine>();
        // services.AddSingleton<ModernLoggingEngine>();
        services.AddSingleton<CodeFlowEngine>();
        services.AddSingleton<StructuralRefinementEngine>();
        // services.AddSingleton<LogicOptimizationEngine>();
        services.AddSingleton<SemanticSearchEngine>();
        // services.AddSingleton<ModernizationUpgradeEngine>();
        // services.AddSingleton<AsyncSafetyEngine>();
        services.AddSingleton<ProjectStructureEngine>();
        // services.AddSingleton<DeadCodeEngine>();
        services.AddSingleton<SyntaxUpgradeEngine>();
        // services.AddSingleton<RefinementEngine>();
        services.AddSingleton<DiagnosticEngine>();
        services.AddSingleton<BuildEngine>();
        services.AddSingleton<TestRunEngine>();
        services.AddSingleton<SolutionManagementEngine>();
        services.AddSingleton<MappingEngine>();
        services.AddSingleton<IDEStyleEngine>();
        services.AddSingleton<StandardRefactoringEngine>();
        services.AddSingleton<ImmutabilityEngine>();
        services.AddSingleton<CodeStyleEngine>();
        services.AddSingleton<DependencyEngine>();
        // services.AddSingleton<AdvancedLogicEngine>();
        // services.AddSingleton<AdvancedStructuralEngine>();
        services.AddSingleton<SemanticRefactoringLibrary>();
        services.AddSingleton<GranularRefactoringEngine>();
        // services.AddSingleton<ApiAutomationEngine>();
        services.AddSingleton<ControlFlowEngine>();
        // services.AddSingleton<HealthOrchestrationEngine>();
        services.AddSingleton<SymbolNavigationEngine>();
        // services.AddSingleton<AntiPatternEngine>();
        // services.AddSingleton<CloneDetectionEngine>();
        // services.AddSingleton<OutParamRefactoringEngine>();
        services.AddSingleton<DiscoveryEngine>();
        services.AddSingleton<MsToolAugmentEngine>();
        services.AddSingleton<CodeStyleAnalysisEngine>();
        services.AddSingleton<ProjectConsistencyEngine>();
        services.AddSingleton<BreakingChangeEngine>();
        // services.AddSingleton<PathDrivenTestEngine>();
        services.AddSingleton<StackOverflowEngine>();
        // services.AddSingleton<AsyncBatchEngine>();
        return services;
    }

    /// <summary>
    /// Registers all MCP tool classes (mode-conditional, with optional per-class
    /// <paramref name="includeTools"/>/<paramref name="excludeTools"/> overrides — see
    /// <see cref="ServerStartupHelpers.ResolveActiveToolClasses"/>) and the centralized error filter.
    /// </summary>
    public static IMcpServerBuilder AddRoslynSentinelToolsBasic(
        this IMcpServerBuilder mcpBuilder,
        IServiceCollection services,
        HashSet<string> activeModes,
        HashSet<string>? includeTools = null,
        HashSet<string>? excludeTools = null)
    {
        var resolvedIncludeTools = includeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedExcludeTools = excludeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeToolClasses = ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes,
            ToolClassRegistry.BasicModeToToolClasses,
            resolvedIncludeTools,
            resolvedExcludeTools);

        // Registered from the resolved class set (not a static) so error messages can only ever
        // name a tool this server actually exposes, and so tests can construct one with an
        // arbitrary tool set. Registering here (rather than in Advanced too) is sufficient because
        // every class declaring a tool WriteToolAdviceHelper may name is in
        // BasicModeToToolClasses, so this resolution already sees all of them — Advanced adds no
        // whole-file-write tools. See WriteToolAdviceHelper's remarks.
        services.AddSingleton(new WriteToolAdviceHelper(activeToolClasses));

        if (activeToolClasses.Contains("SentinelWorkspaceTools"))
        {
            services.AddSingleton<WorkspaceReadNavigationImpl>();
            services.AddSingleton<WorkspaceReadNavigationTools>();
            services.AddSingleton<SentinelWorkspaceTools>();
            mcpBuilder.WithToolsFixed<SentinelWorkspaceTools>();
        }
        if (activeToolClasses.Contains("SentinelDocumentationTools"))
        {
            services.AddSingleton<SentinelDocumentationTools>();
            mcpBuilder.WithToolsFixed<SentinelDocumentationTools>();
        }
        if (activeToolClasses.Contains("SentinelSymbolTools"))
        {
            services.AddSingleton<SentinelSymbolTools>();
            mcpBuilder.WithToolsFixed<SentinelSymbolTools>();
        }
        if (activeToolClasses.Contains("SentinelGitTools"))
        {
            services.AddSingleton<SentinelGitTools>();
            mcpBuilder.WithToolsFixed<SentinelGitTools>();
        }
        if (activeToolClasses.Contains("SentinelAdminTools"))
        {
            // Restricted/operator-only tool — deliberately NOT included in AllModes (see
            // ServerStdio.cs/ServerHttp.cs), so --mode alone can't reach it; only an explicit
            // --mode=Admin or --include-tools=SentinelAdminTools activates it.
            services.AddSingleton<SentinelAdminTools>();
            mcpBuilder.WithToolsFixed<SentinelAdminTools>();
        }
        if (activeToolClasses.Contains("SentinelWholeFileWriteTools"))
        {
            // Restricted/operator-only tool — deliberately NOT included in AllModes (see
            // ServerStdio.cs/ServerHttp.cs), so --mode alone can't reach it; only an explicit
            // --mode=Admin/--mode=WholeFileWrite or --include-tools=SentinelWholeFileWriteTools
            // activates it.
            services.AddSingleton<SentinelWholeFileWriteTools>();
            mcpBuilder.WithToolsFixed<SentinelWholeFileWriteTools>();
        }
        if (activeToolClasses.Contains("SentinelIntelligenceTools"))
        {
            // services.AddSingleton<SentinelIntelligenceTools>();
            // mcpBuilder.WithTools<SentinelIntelligenceTools>();
        }
        if (activeToolClasses.Contains("SentinelScanTools"))
        {
            // services.AddSingleton<SentinelScanTools>();
            // mcpBuilder.WithTools<SentinelScanTools>();
        }
        if (activeToolClasses.Contains("SentinelRefactoringTools"))
        {
            services.AddSingleton<SentinelRefactoringTools>();
            mcpBuilder.WithToolsFixed<SentinelRefactoringTools>();
        }
        if (activeToolClasses.Contains("SentinelAdvancedRefactoringTools"))
        {
            // services.AddSingleton<SentinelAdvancedRefactoringTools>();
            // mcpBuilder.WithTools<SentinelAdvancedRefactoringTools>();
        }
        if (activeToolClasses.Contains("SentinelModernizationTools"))
        {
            // services.AddSingleton<SentinelModernizationTools>();
            // mcpBuilder.WithTools<SentinelModernizationTools>();
        }
        if (activeToolClasses.Contains("SentinelQualityTools"))
        {
            // services.AddSingleton<SentinelQualityTools>();
            // mcpBuilder.WithTools<SentinelQualityTools>();
        }
        if (activeToolClasses.Contains("SentinelGenerationTools"))
        {
            // services.AddSingleton<SentinelGenerationTools>();
            // mcpBuilder.WithTools<SentinelGenerationTools>();
        }
        if (activeToolClasses.Contains("SentinelCommentingTools"))
        {
            // services.AddSingleton<SentinelCommentingTools>();
            // mcpBuilder.WithTools<SentinelCommentingTools>();
        }
        var codemodActive = (ToolClassRegistry.CodemodTriggerModes.Any(activeModes.Contains) ||
                              resolvedIncludeTools.Contains(ToolClassRegistry.CodemodToolClass)) &&
                             !resolvedExcludeTools.Contains(ToolClassRegistry.CodemodToolClass);
        if (codemodActive)
        {
            // services.AddSingleton<SentinelCodemodTools>();
            // mcpBuilder.WithTools<SentinelCodemodTools>();
        }
        if (activeToolClasses.Contains("SentinelAsyncifyTools"))
        {
            // services.AddSingleton<SentinelAsyncifyTools>();
            // mcpBuilder.WithTools<SentinelAsyncifyTools>();
        }

        // Centralized error-to-success filter:
        // Converts a SolutionNotLoadedException into a successful
        // CallToolResult so the agent displays the helpful message rather than a generic error.
        mcpBuilder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    try
                    {
                        return await next(context, cancellationToken);
                    }
                    catch (SolutionNotLoadedException ex)
                    {
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = ex.Message }],
                            IsError = false,
                        };
                    }
                    catch (Exception ex)
                    {
                        // Every tool in this codebase catches its own exceptions and returns a
                        // ToolResult with Success=false instead of throwing (see
                        // docs/current/feedback_agent_friendly_error_messages.md), so reaching here
                        // means an exception escaped that path entirely — e.g. the MCP SDK's own
                        // argument-binding failure (a required parameter missing from the call), or
                        // a genuine bug. Either way it's a real failure, so it must surface as
                        // IsError=true rather than silently reporting success.
                        Debug.WriteLine($"Unexpected error in CallTool filter: {ex}");

                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = $"Tool call failed unexpectedly ({ex.GetType().Name}): {ex.Message}" }],
                            IsError = true,
                        };
                    }
                }));

            // Domain-failure → protocol-error sync: every tool in this codebase (by design, see
            // docs/current/feedback_agent_friendly_error_messages.md) catches its own exceptions
            // and returns a ToolResult<T>/ApplyChangesResult/etc. with Success=false instead of
            // throwing, so the MCP SDK's own exception-based IsError detection never fires for a
            // domain-level failure. Set IsError=true whenever the serialized response body's
            // top-level "success" field is false, so a client relying on the protocol-level flag
            // (rather than parsing the JSON body) sees an accurate signal.
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken);

                    try
                    {
                        if (result.IsError != true && result.Content is not null)
                        {
                            foreach (var block in result.Content)
                            {
                                if (block is not ModelContextProtocol.Protocol.TextContentBlock textBlock ||
                                    string.IsNullOrEmpty(textBlock.Text))
                                {
                                    continue;
                                }

                                using var doc = System.Text.Json.JsonDocument.Parse(textBlock.Text);
                                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                    doc.RootElement.TryGetProperty("success", out var successProp) &&
                                    successProp.ValueKind == System.Text.Json.JsonValueKind.False)
                                {
                                    result.IsError = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Response text isn't JSON (or isn't a ToolResult-shaped object) — leave IsError as-is.
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"IsError sync filter failed: {ex}");
                    }

                    return result;
                }));

            // Diagnostic drift check: after every tool call, compare each tracked document's
            // in-memory text against the bytes on disk and log any mismatch. This is a content-level
            // check (unlike GetExternalFileChanges, which depends on the FileSystemWatcher and can miss
            // events under overflow), so it also catches drift the watcher never reported.
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken);

                    try
                    {
                        var workspaceManager = context.Server.Services?.GetService<PersistentWorkspaceManager>();
                        if (workspaceManager is not null)
                        {
                            var drift = await workspaceManager.GetContentExternalFileChangesAsync(cancellationToken);
                            if (drift.Count > 0)
                            {
                                var logger = context.Server.Services?.GetService<ILogger<PersistentWorkspaceManager>>();
                                logger?.LogWarning(
                                    "External file changes detected after tool '{Tool}': {Count} file(s) differ from in-memory workspace state: {Files}",
                                    context.Params?.Name, drift.Count, string.Join(", ", drift));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"External file changes drift check filter failed: {ex}");
                    }

                    return result;
                }));

            // Cheap large-result logging: sums the length of text content blocks in the response
            // (no JSON serialization) and logs a warning above LargeResultHelper.OffloadThresholdBytes.
            // This is deliberately shape-agnostic — it exists because most tool result types
            // aren't individually wired into LargeResultInfo (see RoslynSentinel.Common.LargeResultHelper),
            // so this is the only signal for "this tool call returned a lot of data" for those tools.
            // Grep the log for "Large tool result" to review offenders without parsing full payloads.
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken);

                    try
                    {
                        long sizeChars = 0;
                        if (result.Content is not null)
                        {
                            foreach (var block in result.Content)
                            {
                                if (block is ModelContextProtocol.Protocol.TextContentBlock textBlock)
                                {
                                    sizeChars += textBlock.Text?.Length ?? 0;
                                }
                            }
                        }

                        if (sizeChars > RoslynSentinel.Common.LargeResultHelper.OffloadThresholdBytes)
                        {
                            var logger = context.Server.Services?.GetService<ILogger<PersistentWorkspaceManager>>();
                            logger?.LogWarning(
                                "Large tool result: tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdBytes})",
                                context.Params?.Name, sizeChars, RoslynSentinel.Common.LargeResultHelper.OffloadThresholdBytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Large result logging filter failed: {ex}");
                    }

                    return result;
                }));

            // Orientation breaker: after OrientationBreakerTripThreshold consecutive zero-match
            // SearchSolutionText calls (see PersistentWorkspaceManager.RecordSearchOutcome), restrict
            // tool calls to a small orienting allowlist until one of them succeeds. Exists because
            // agents repeatedly retry SearchSolutionText with reworded guesses instead of switching to
            // ListAll/GetFileOutline, even though both the system prompt and SearchSolutionText's own
            // zero-match response already say to do so — see docs/current/plan-orientation-breaker.md.
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    IAutomaticCircuitBreaker? automaticBreaker = null;
                    var toolName = context.Params?.Name;

                    try
                    {
                        automaticBreaker = context.Server.Services?.GetService<PersistentWorkspaceManager>();

                        if (automaticBreaker is not null && automaticBreaker.IsTripped() &&
                            toolName is not ("ListAll" or "ListSolutionItems" or "GetFileOutline" or "ReadFile"))
                        {
                            return new ModelContextProtocol.Protocol.CallToolResult
                            {
                                Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = automaticBreaker.StateMessage() ?? "SearchSolutionText is DISABLED. You MUST call ListAll(kind: all) or ListSolutionItems(kind: all) now." }],
                                IsError = true,
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Orientation breaker pre-check failed: {ex}");
                    }

                    var result = await next(context, cancellationToken);

                    try
                    {
                        if (automaticBreaker is not null)
                        {
                            if (toolName == "SearchSolutionText")
                            {
                                int totalRecords = 0;
                                if (result.Content is not null)
                                {
                                    foreach (var block in result.Content)
                                    {
                                        if (block is ModelContextProtocol.Protocol.TextContentBlock textBlock &&
                                            !string.IsNullOrEmpty(textBlock.Text))
                                        {
                                            try
                                            {
                                                using var doc = System.Text.Json.JsonDocument.Parse(textBlock.Text);
                                                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                                    doc.RootElement.TryGetProperty("totalRecords", out var totalRecordsProp) &&
                                                    totalRecordsProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                                                {
                                                    totalRecords = totalRecordsProp.GetInt32();
                                                }
                                            }
                                            catch (System.Text.Json.JsonException)
                                            {
                                                // Response text isn't JSON — leave totalRecords at 0 (treated as a zero-match outcome).
                                            }
                                        }
                                    }
                                }

                                automaticBreaker.RecordSearchOutcome(totalRecords);
                            }
                            else if (automaticBreaker.IsTripped() && result.IsError != true)
                            {
                                automaticBreaker.Reset();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Orientation breaker post-check failed: {ex}");
                    }

                    return result;
                }));

            // Unrecoverable breaker: a server-integrity fault (currently a failed operation-blob
            // write, meaning a change landed on disk with no undo record) halts the session for
            // good. See IUnrecoverableBreaker for why this is not IManualCircuitBreaker and has no
            // reset. Registered as its own filter rather than folded into the orientation-breaker
            // one above so neither can mask the other's message.
            //
            // The authoritative enforcement is in PersistentWorkspaceManager.ApplyProposedChangesAsync
            // — the write chokepoint, which no mutating tool can bypass. This filter exists so the
            // refusal also arrives as a protocol-level IsError carrying the specific diagnostic,
            // rather than only as a per-tool error, and so tools that would do expensive analysis
            // before their first write fail fast.
            //
            // Allowlist rather than a list of mutating tools: a deny-list would silently omit any
            // tool added later, which is the same forgotten-call-site mode that produced this
            // defect. Anything not named here is refused, so the safe default is "refused". These
            // are the tools an operator or agent needs to read the state and stop cleanly.
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    try
                    {
                        IUnrecoverableBreaker? breaker = context.Server.Services?.GetService<PersistentWorkspaceManager>();
                        var toolName = context.Params?.Name;

                        if (breaker is not null && breaker.IsTripped() &&
                            toolName is not ("ReadFile" or "ListAll" or "ListSolutionItems" or "GetFileOutline"
                                or "GetOperationDetail" or "GetWorkspaceHealth" or "IsSessionHalted" or "Git"))
                        {
                            return new ModelContextProtocol.Protocol.CallToolResult
                            {
                                Content = [new ModelContextProtocol.Protocol.TextContentBlock
                                {
                                    Text = breaker.StateMessage()
                                        ?? "The server recorded an unrecoverable integrity failure. This session cannot continue. Stop and report to the user/operator."
                                }],
                                IsError = true,
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unrecoverable breaker pre-check failed: {ex}");
                    }

                    return await next(context, cancellationToken);
                }));
        });

        return mcpBuilder;
    }

    /// <summary>
    /// Pre-warms MSBuildLocator (which takes ~5–8 s on first call) and optionally auto-loads a solution.
    /// Should be called after <see cref="Microsoft.Extensions.Hosting.IHost.Build"/> / <see cref="Microsoft.AspNetCore.Builder.WebApplication.Build"/>.
    /// </summary>
    public static void WarmupAndAutoLoadBasic(this IServiceProvider services, string? solutionPath, ILogger? logger = null, string? baseRepoDirectory = null)
    {
        logger?.LogInformation("Pre-warming MSBuildLocator and workspace manager...");
        var warmupStart = System.Diagnostics.Stopwatch.StartNew();
        var workspaceManager = services.GetRequiredService<PersistentWorkspaceManager>();
        warmupStart.Stop();
        logger?.LogInformation("MSBuildLocator pre-warm complete in {Ms}ms", warmupStart.ElapsedMilliseconds);

        if (!string.IsNullOrEmpty(baseRepoDirectory))
        {
            workspaceManager.BaseRepoDirectory = baseRepoDirectory;
        }

        if (!string.IsNullOrEmpty(solutionPath))
        {
            logger?.LogInformation("Auto-loading solution: {Path}", solutionPath);
            _ = workspaceManager.LoadSolutionAsync(solutionPath)
                .ContinueWith(
                    t => logger?.LogError(t.Exception!.GetBaseException(), "Auto-load solution failed: {Path}", solutionPath),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}

using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
    /// Don't uncomment one here without checking whether Advanced already live-registers it ->
    /// doing so would register the same engine type in both, not just in Basic.
    /// </remarks>
    public static IServiceCollection AddRoslynSentinelEnginesBasic(this IServiceCollection services)
    {
        services.AddSingleton<SentinelConfiguration>();
        // Defaults to Production. TryAdd (not Add) so a host that called
        // AddRoslynSentinelHostOptions first keeps its own value -> registering unconditionally
        // here would make the last-wins order depend on which extension the host called last.
        services.TryAddSingleton(new SentinelHostOptions());
        // TryAdd only so a caller that already registered StoppedByScriptMarker (e.g., ServerStdio.cs
        // or ServerHttp.cs from a real host) is not overwritten. Test fixtures without that context
        // get this default stub.
        services.AddSingleton<BreakingChangeEngine>();
        services.AddSingleton<BuildEngine>();
        services.AddSingleton<DependencyEngine>();
        services.AddSingleton<DiagnosticEngine>();
        services.AddSingleton<DiffEngine>();
        services.AddSingleton<DiscoveryEngine>();
        services.AddSingleton<ImpactAnalyzer>();
        services.AddSingleton<InventoryEngine>();
        services.AddSingleton<ISolutionProvider>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<IWorkspaceManager>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<IWorkspaceReader>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<MsToolAugmentEngine>();
        services.AddSingleton<PersistentWorkspaceManager>();
        services.AddSingleton<ProjectConsistencyEngine>();
        services.AddSingleton<ProjectStructureEngine>();
        services.AddSingleton<RefactoringEngine>();
        services.AddSingleton<SemanticRefactoringLibrary>();
        services.AddSingleton<SemanticSearchEngine>();
        services.AddSingleton<SolutionManagementEngine>();
        services.AddSingleton<StandardRefactoringEngine>();
        services.AddSingleton<StructuralRefinementEngine>();
        services.AddSingleton<SymbolNavigationEngine>();
        services.AddSingleton<SyntaxUpgradeEngine>();
        services.AddSingleton<TestRunEngine>();
        services.AddSingleton<ThreadSafetyEngine>();
        services.AddSingleton<ValidationEngine>();
        services.TryAddSingleton(new StoppedByScriptMarker(WasFound: false, Details: null));

        return services;
    }
    /// <summary>
    /// Registers all MCP tool classes (mode-conditional, with optional per-class
    /// <paramref name="includeTools"/>/<paramref name="excludeTools"/> overrides -> see
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
        // BasicModeToToolClasses, so this resolution already sees all of them -> Advanced adds no
        // whole-file-write tools. See WriteToolAdviceHelper's remarks.
        services.AddSingleton(new WriteToolAdviceHelper(activeToolClasses));

        // Captured once here (the same values ResolveActiveToolClasses/DescribeNoActiveToolsFailure
        // use to build the startup guidance message) so McpServerStatus can report today's actual
        // mode/include-tools/exclude-tools resolution instead of a caller having to guess or
        // restart the server to find out.
        var activeToolSurface = new ActiveToolSurface(
            modeArg: string.Join(",", activeModes.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)),
            activeModes: activeModes,
            includeTools: resolvedIncludeTools,
            excludeTools: resolvedExcludeTools,
            activeToolClasses: activeToolClasses);
        services.AddSingleton(activeToolSurface);

        // Always registered, independent of activeToolClasses/--mode/--include-tools/
        // --exclude-tools: the one tool meant to be reachable no matter what selection is in
        // effect, since it exists for the case where the selection itself might be the problem.
        // Deliberately not in ToolClassRegistry, so it's never counted or excludable.
        services.AddSingleton<ServerStatusTools>();
        mcpBuilder.WithSentinelTools<ServerStatusTools>();

        // Decision 7 step 4: the *Tools/*Impl split classes take a plain (non-generic) ILogger,
        // not ILogger<T> - previously this only worked because each was constructed with `new`
        // inside a facade's constructor (e.g. WorkspaceTools), which passes down its own
        // ILogger<TFacade> by implicit reference conversion, never asking DI to resolve a plain
        // ILogger directly. Registering these classes as DI singletons in their own right (the
        // fine-grained mode-string blocks below) is the first thing that ever asks the container
        // to resolve ILogger itself - nothing in this solution (including AddLogging()) registers
        // it, so without this line every one of those registrations throws
        // InvalidOperationException at first resolution. Resolved once here from ILoggerFactory,
        // same category name pattern the rest of the server already uses implicitly.
        services.TryAddSingleton(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("RoslynSentinel"));

        if (activeToolClasses.Contains("WorkspaceTools"))
        {
            services.AddSingleton<WorkspaceReadNavigationImpl>();
            services.AddSingleton<WorkspaceReadNavigationTools>();
            services.AddSingleton<WorkspaceTools>();
            mcpBuilder.WithSentinelTools<WorkspaceTools>();
        }
        // Fine-grained Workspace sub-modes (Decision 7 step 4, Decision 4 + Addendum B) -
        // independently opt-in-able direct registrations of the split classes, none requiring
        // "WorkspaceTools"/"Workspace" itself. WorkspaceReadNavigationImpl/Tools use
        // TryAddSingleton since the block above may already have registered them.
        if (activeToolClasses.Contains("WorkspaceFileEditTools"))
        {
            services.TryAddSingleton<WorkspaceReadNavigationImpl>();
            services.TryAddSingleton<WorkspaceReadNavigationTools>();
            services.AddSingleton<WorkspaceFileEditImpl>();
            services.AddSingleton<WorkspaceFileEditTools>();
            mcpBuilder.WithSentinelTools<WorkspaceFileEditTools>();
        }
        if (activeToolClasses.Contains("WorkspaceBuildTestTools"))
        {
            services.AddSingleton<WorkspaceBuildTestImpl>();
            services.AddSingleton<WorkspaceBuildTestTools>();
            mcpBuilder.WithSentinelTools<WorkspaceBuildTestTools>();
        }
        if (activeToolClasses.Contains("WorkspaceProjectManagementTools"))
        {
            services.AddSingleton<WorkspaceProjectManagementImpl>();
            services.AddSingleton<WorkspaceProjectManagementTools>();
            mcpBuilder.WithSentinelTools<WorkspaceProjectManagementTools>();
        }
        if (activeToolClasses.Contains("WorkspaceReadNavigationTools"))
        {
            services.TryAddSingleton<WorkspaceReadNavigationImpl>();
            services.TryAddSingleton<WorkspaceReadNavigationTools>();
            mcpBuilder.WithSentinelTools<WorkspaceReadNavigationTools>();
        }
        if (activeToolClasses.Contains("WorkspaceHealthMiscTools"))
        {
            services.AddSingleton<WorkspaceHealthMiscImpl>();
            services.AddSingleton<WorkspaceHealthMiscTools>();
            mcpBuilder.WithSentinelTools<WorkspaceHealthMiscTools>();
        }
        if (activeToolClasses.Contains("DocumentationTools"))
        {
            services.AddSingleton<DocumentationTools>();
            mcpBuilder.WithSentinelTools<DocumentationTools>();
        }
        // Fine-grained Symbol sub-modes (Decision 7 step 4, Addendum A) - independently
        // opt-in-able, neither requires "SymbolNavigationTools"/"Workspace" itself.
        if (activeToolClasses.Contains("SymbolNavigationTools"))
        {
            services.AddSingleton<SymbolNavigationImpl>();
            services.AddSingleton<SymbolNavigationTools>();
            mcpBuilder.WithSentinelTools<SymbolNavigationTools>();
        }
        if (activeToolClasses.Contains("SymbolRelationshipTools"))
        {
            services.AddSingleton<SymbolRelationshipImpl>();
            services.AddSingleton<SymbolRelationshipTools>();
            mcpBuilder.WithSentinelTools<SymbolRelationshipTools>();
        }
        if (activeToolClasses.Contains("GitTools"))
        {
            services.AddSingleton<GitTools>();
            mcpBuilder.WithSentinelTools<GitTools>();
        }
        if (activeToolClasses.Contains("AdminTools"))
        {
            // Restricted/operator-only tool -> deliberately NOT included in AllModes (see
            // ServerStdio.cs/ServerHttp.cs); reachable via --mode=Admin, --mode=Claude, or
            // --include-tools=AdminTools.
            services.AddSingleton<AdminTools>();
            mcpBuilder.WithSentinelTools<AdminTools>();
        }
        if (activeToolClasses.Contains("WholeFileWriteTools"))
        {
            // Restricted/operator-only tool -> deliberately NOT included in AllModes (see
            // ServerStdio.cs/ServerHttp.cs), so --mode alone can't reach it; only an explicit
            // --mode=Admin/--mode=WholeFileWrite or --include-tools=WholeFileWriteTools
            // activates it.
            services.AddSingleton<WholeFileWriteTools>();
            mcpBuilder.WithSentinelTools<WholeFileWriteTools>();
        }
        if (activeToolClasses.Contains("IntelligenceTools"))
        {
            // services.AddSingleton<IntelligenceTools>();
            // mcpBuilder.WithTools<IntelligenceTools>();
        }
        if (activeToolClasses.Contains("ScanTools"))
        {
            // services.AddSingleton<ScanTools>();
            // mcpBuilder.WithTools<ScanTools>();
        }
        if (activeToolClasses.Contains("RefactoringTools"))
        {
            services.AddSingleton<RefactoringSignatureImpl>();
            services.AddSingleton<RefactoringSignatureTools>();
            mcpBuilder.WithSentinelTools<RefactoringSignatureTools>();
            services.AddSingleton<RefactoringStructuralImpl>();
            services.AddSingleton<RefactoringStructuralTools>();
            mcpBuilder.WithSentinelTools<RefactoringStructuralTools>();
            services.AddSingleton<RefactoringExtractionDocsImpl>();
            services.AddSingleton<RefactoringExtractionDocsTools>();
            mcpBuilder.WithSentinelTools<RefactoringExtractionDocsTools>();
        }
        // Fine-grained Refactor sub-modes (Decision 7 step 4, Decision 4) - independently
        // opt-in-able via --include-tools=<ClassName> even without "RefactoringTools"/"Refactor"
        // itself; TryAddSingleton/TryAdd-equivalent here would be cleaner, but AddSingleton is
        // consistent with every other block in this method, so a class named in both the mode's
        // umbrella gate above and its own explicit --include-tools entry gets a harmless duplicate
        // registration (last one wins for GetRequiredService<T>, per DI container semantics).
        if (activeToolClasses.Contains("RefactoringSignatureTools"))
        {
            services.TryAddSingleton<RefactoringSignatureImpl>();
            services.AddSingleton<RefactoringSignatureTools>();
            mcpBuilder.WithSentinelTools<RefactoringSignatureTools>();
        }
        if (activeToolClasses.Contains("RefactoringStructuralTools"))
        {
            services.TryAddSingleton<RefactoringStructuralImpl>();
            services.AddSingleton<RefactoringStructuralTools>();
            mcpBuilder.WithSentinelTools<RefactoringStructuralTools>();
        }
        if (activeToolClasses.Contains("RefactoringExtractionDocsTools"))
        {
            services.TryAddSingleton<RefactoringExtractionDocsImpl>();
            services.AddSingleton<RefactoringExtractionDocsTools>();
            mcpBuilder.WithSentinelTools<RefactoringExtractionDocsTools>();
        }
        if (activeToolClasses.Contains("AdvancedRefactoringTools"))
        {
            // services.AddSingleton<AdvancedRefactoringTools>();
            // mcpBuilder.WithTools<AdvancedRefactoringTools>();
        }
        if (activeToolClasses.Contains("ModernizationTools"))
        {
            // services.AddSingleton<ModernizationTools>();
            // mcpBuilder.WithTools<ModernizationTools>();
        }
        if (activeToolClasses.Contains("QualityTools"))
        {
            // services.AddSingleton<QualityTools>();
            // mcpBuilder.WithTools<QualityTools>();
        }
        if (activeToolClasses.Contains("GenerationTools"))
        {
            // services.AddSingleton<GenerationTools>();
            // mcpBuilder.WithTools<GenerationTools>();
        }
        if (activeToolClasses.Contains("CommentingTools"))
        {
            // services.AddSingleton<CommentingTools>();
            // mcpBuilder.WithTools<CommentingTools>();
        }
        var codemodActive = (ToolClassRegistry.CodemodTriggerModes.Any(activeModes.Contains) ||
                              resolvedIncludeTools.Contains(ToolClassRegistry.CodemodToolClass)) &&
                             !resolvedExcludeTools.Contains(ToolClassRegistry.CodemodToolClass);
        if (codemodActive)
        {
            // services.AddSingleton<CodemodTools>();
            // mcpBuilder.WithTools<CodemodTools>();
        }
        if (activeToolClasses.Contains("AsyncifyTools"))
        {
            // services.AddSingleton<AsyncifyTools>();
            // mcpBuilder.WithTools<AsyncifyTools>();
        }

        // Centralized error-to-success filter:
        // Converts a SolutionNotLoadedException into a successful
        // CallToolResult so the agent displays the helpful message rather than a generic error.
        mcpBuilder.WithRequestFilters(filters =>
        {
            AddArgumentValidationFilter(filters);

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
                        // SentinelCallToolResult with IsSuccess=false instead of throwing (see
                        // docs/current/feedback_agent_friendly_error_messages.md), so reaching here
                        // means an exception escaped that path entirely -> e.g. the MCP SDK's own
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

            // Domain-failure -> protocol-error sync: every tool in this codebase (by design, see
            // docs/current/feedback_agent_friendly_error_messages.md) catches its own exceptions
            // and returns a SentinelCallToolResult<T>/ApplyChangesResult/etc. with IsSuccess=false instead of
            // throwing, so the MCP SDK's own exception-based IsError detection never fires for a
            // domain-level failure. Set IsError=true whenever the serialized response body's
            // top-level "isSuccess" field is false, so a client relying on the protocol-level flag
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
                                    doc.RootElement.TryGetProperty("isSuccess", out var successProp) &&
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
                        // Response text isn't JSON (or isn't a SentinelCallToolResult-shaped object) -> leave IsError as-is.
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

            // Generic large-result offload backstop (see
            // docs/current/proposal_centralized_large_result_filter.md): sums the length of text
            // content blocks in the response (no JSON re-serialization) and, above
            // LargeResultHelper.OffloadThresholdBytes, writes the raw response text verbatim to disk
            // via LargeResultHelper.StoreRawJsonAsync and replaces the response with a small pointer
            // (resultId) instead of just logging. This is deliberately shape-agnostic -> it exists
            // because most tool result types aren't individually wired into the typed
            // ForPossiblyLargeDataAsync/LargeResultInfo path, so this is the only offload available
            // for those tools. It coexists with, and does not replace, that typed per-caller path:
            // a tool already offloaded via ForPossiblyLargeDataAsync has a small response by the
            // time it reaches here and this filter is a no-op for it. Grep the log for "Large tool
            // result" to review offenders without parsing full payloads.
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken);

                    // GetLargeResult's own switch branches (WorkspaceReadNavigationImpl.cs) already
                    // shrink-and-verify every list-shaped page against this exact threshold before
                    // returning, specifically so this filter could never re-catch that response. If
                    // one still slips past that (e.g. a single oversized record a page of 1 can't
                    // shrink below), re-wrapping it under a brand-new resultId here would silently
                    // hand the caller another offload envelope pointing at itself - an
                    // unterminating fetch/still-too-big/re-offload loop with no visible way out. Skip
                    // re-offload for this tool specifically and let its own oversized response pass
                    // through as-is: a legible "still too big" is better than an invisible loop. See
                    // docs/current/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md.
                    if (context.Params?.Name == "GetLargeResult")
                    {
                        return result;
                    }

                    try
                    {
                        if (result.Content is null)
                        {
                            return result;
                        }

                        foreach (var block in result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().ToList())
                        {
                            var text = block.Text;
                            if (string.IsNullOrEmpty(text) ||
                                text.Length <= LargeResultHelper.OffloadThresholdBytes)
                            {
                                continue;
                            }

                            var logger = context.Server.Services?.GetService<ILogger<PersistentWorkspaceManager>>();
                            logger?.LogWarning(
                                "Large tool result: tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdBytes})",
                                context.Params?.Name, text.Length, LargeResultHelper.OffloadThresholdBytes);

                            var workspaceManager = context.Server.Services?.GetService<PersistentWorkspaceManager>();
                            var solutionRoot = workspaceManager?.GetSolutionRoot();
                            var stored = await LargeResultHelper.StoreRawJsonAsync(text, solutionRoot, cancellationToken);
                            if (!stored.offloaded)
                            {
                                // No solution loaded, or the write failed to qualify -> fail closed to
                                // pass-through rather than blocking the call on a guardrail defect.
                                continue;
                            }

                            // Best-effort size hint so the pointer alone tells the model whether this
                            // is 1 big hit or 300 small ones, instead of forcing a GetLargeResult round
                            // trip just to find out. Shape-agnostic by design (this filter runs for
                            // every tool, typed and untyped alike) -> parsed straight from the raw JSON
                            // rather than any tool-specific DTO. itemCount looks for the envelope's own
                            // totalRecords first (set by ForPossiblyLargeDataAsync callers), then falls
                            // back to counting elements of the first array found under successData
                            // (covers untyped tools like FindReferences that set SuccessData directly).
                            // statusMessage is relayed whenever the tool already populated one (e.g.
                            // FindReferences/SearchSolutionText's SummarizeListResult-based summary) so
                            // that hint survives the offload instead of being silently dropped.
                            int? itemCount = null;
                            string? statusMessage = null;
                            try
                            {
                                var root = System.Text.Json.Nodes.JsonNode.Parse(text)?.AsObject();
                                if (root != null)
                                {
                                    if (root.TryGetPropertyValue("totalRecords", out var totalRecordsNode) &&
                                        totalRecordsNode != null &&
                                        totalRecordsNode.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                                    {
                                        itemCount = totalRecordsNode.GetValue<int>();
                                    }
                                    else if (root.TryGetPropertyValue("successData", out var successDataNode) && successDataNode != null)
                                    {
                                        if (successDataNode is System.Text.Json.Nodes.JsonArray topArray)
                                        {
                                            itemCount = topArray.Count;
                                        }
                                        else if (successDataNode is System.Text.Json.Nodes.JsonObject successObject)
                                        {
                                            var arrays = successObject.Where(kv => kv.Value is System.Text.Json.Nodes.JsonArray).ToList();
                                            if (arrays.Count > 0)
                                            {
                                                itemCount = arrays.Sum(kv => ((System.Text.Json.Nodes.JsonArray)kv.Value!).Count);
                                            }
                                        }
                                    }

                                    if (root.TryGetPropertyValue("statusMessage", out var statusMessageNode) &&
                                        statusMessageNode != null &&
                                        statusMessageNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
                                    {
                                        statusMessage = statusMessageNode.GetValue<string>();
                                    }
                                }
                            }
                            catch (Exception parseEx)
                            {
                                // Best-effort only - never let a parse quirk in some other tool's shape
                                // block the offload itself, just fall back to no hint.
                                Debug.WriteLine($"Large result offload size-hint parse failed: {parseEx}");
                            }

                            var hint = itemCount is int n ? $" Result contains {n} item(s)." : "";

                            result.Content = [new ModelContextProtocol.Protocol.TextContentBlock
                            {
                                Text = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    offloaded = true,
                                    resultId = stored.resultId,
                                    sizeBytes = text.Length,
                                    itemCount,
                                    statusMessage,
                                    message = $"Result is {text.Length} bytes (threshold: {LargeResultHelper.OffloadThresholdBytes}).{hint} Use GetLargeResult(resultId: \"{stored.resultId}\") to page through results."
                                })
                            }];
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Large result offload filter failed: {ex}");
                    }

                    return result;
                }));

            // Orientation breaker: after OrientationBreakerTripThreshold consecutive zero-match
            // SearchSolutionText calls (see PersistentWorkspaceManager.RecordSearchOutcome), restrict
            // tool calls to a small orienting allowlist until one of them succeeds. Exists because
            // agents repeatedly retry SearchSolutionText with reworded guesses instead of switching to
            // ListAll/GetFileOutline, even though both the system prompt and SearchSolutionText's own
            // zero-match response already say to do so -> see docs/current/plan-orientation-breaker.md.
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
                            // SearchSolutionText's own outcome is now recorded inline, from inside
                            // WorkspaceReadNavigationImpl.SearchSolutionText itself, immediately after
                            // the match count is known -> not here. Recording it a second time here
                            // would double-count every call against the trip threshold.
                            if (toolName != "SearchSolutionText" && automaticBreaker.IsTripped() && result.IsError != true)
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
            // -> the write chokepoint, which no mutating tool can bypass. This filter exists so the
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
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Registers the argument pre-flight filter: rejects a call whose arguments cannot succeed as
    /// written, before the SDK's binder ever sees it.
    /// <para>
    /// Covers two dispatch-layer defects that no per-tool fix can reach -> an unknown parameter is
    /// silently discarded (the tool then runs on its defaults and reports <c>success:true</c> with
    /// the wrong result), and a missing required parameter surfaces as a raw framework
    /// <c>ArgumentException</c> naming an internal "arguments dictionary". See
    /// <see cref="ToolArgumentValidator"/> for the full analysis.
    /// </para>
    /// <para>
    /// Registered before every other call-tool filter so no other filter does work on a call that
    /// cannot succeed.
    /// </para>
    /// </summary>
    private static void AddArgumentValidationFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                try
                {
                    var validationError = ToolArgumentValidator.Validate(
                        context.Server, context.Params?.Name, context.Params?.Arguments);

                    if (validationError is not null)
                    {
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = validationError }],
                            IsError = true,
                        };
                    }
                }
                catch (Exception ex)
                {
                    // A guardrail must never become the thing that blocks a valid call: if
                    // validation itself throws, fall through and let the call run as it would have.
                    Debug.WriteLine($"Tool argument validation filter failed: {ex}");
                }

                return await next(context, cancellationToken);
            }));
    }
}

using System.Diagnostics;
using System.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;
using System.Text.Json.Nodes;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Shared service registration helpers used by both the stdio server (Program.cs)
/// and the separate HTTP host (RoslynSentinel.HttpHost).
/// </summary>
public static class RoslynSentinelServiceExtensionsBasic
{
    /// <summary>
    /// Registers process-wide host settings (<see cref="OperatingMode"/>, <see cref="HostTransport"/>, <see cref="RespawnPolicy"/>). Call
    /// this <em>before</em> <c>AddRoslynSentinelEnginesBasic</c> (Engines.Basic), which TryAdds a
    /// Production-mode default for callers (chiefly the test assemblies) that never set one.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelHostOptions(
        this IServiceCollection services,
        OperatingMode operatingMode,
        HostTransport transport = HostTransport.Stdio,
        RespawnPolicy respawn = RespawnPolicy.Auto)
    {
        services.AddSingleton(new SentinelHostOptions { OperatingMode = operatingMode, Transport = transport, Respawn = respawn });
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
        HashSet<string>? excludeTools = null,
        IReadOnlySet<string>? hideTools = null)
    {
        // Wire result marshaller (plan_wire_result_marshaller.md Step 2). Registered unless the switch is off (the default is on),
        // so with ROSLYNSENTINEL_RESULT_MARSHALLER=off there is no IToolResultMarshaller and MarshalResult stays the SDK's identity.
        // Tools resolve it lazily per call from this container.
        if (ResultMarshallerOptions.Mode != ResultMarshallerMode.Off)
        {
            services.AddSingleton<IToolResultMarshaller, ToolResultMarshaller>();
        }

        // Per-tool call counters, read by McpServerStatus and updated by the telemetry filter below. Created here (not on
        // first use) so ServerStartedUtc is the server's start. Shared by both flavors: Advanced calls this method.
        services.TryAddSingleton(new ToolTelemetry());

        // Always-on per-session call transcript (one JSONL line per tools/call under the scratch root), written by the transcript filter below.
        // A factory, so each host's container builds its own file; TryAdd, so a test can pre-register one pointed at a temp folder.
        services.TryAddSingleton(_ => new ToolCallTranscript());

        // Per-agent loop latch state (proposal_loop_latch.md). Always registered; the filter is a pass-through unless --loop-latch is on.
        services.TryAddSingleton(sp => new ToolLoopLatch(LoopLatchOptions.TripThreshold, sp.GetService<ILoggerFactory>()?.CreateLogger<ToolLoopLatch>()));

        // Persists the counters to server-telemetry_<instanceId>.json (explicit --working-dir only; otherwise a no-op) every 60 s
        // when changed and once more on graceful host stop. A hosted service starts under both hosts (stdio Generic Host, HTTP
        // WebApplication) and is inert in the tool-manifest ServiceCollection used by --list-tools.
        services.TryAddSingleton(sp => new ToolTelemetryFlusher(
            sp.GetRequiredService<ToolTelemetry>(), logger: sp.GetService<ILogger<ToolTelemetryFlusher>>()));
        services.AddHostedService(sp => new ToolTelemetryFlushService(sp.GetRequiredService<ToolTelemetryFlusher>()));

        // StopServer registers "exit after the response to request id X" here; the outgoing message filter below performs it
        // once that response has been written. Shared by both flavors and both transports because Advanced calls this method.
        services.TryAddSingleton(_ => new ResponseGatedProcessExit());

        RegisterRoslynSentinelToolsBasic(services, mcpBuilder, activeModes, includeTools, excludeTools, hideTools);

        // Add centralized error filters, in CallToolFilterRegistrations order (the first registered is the outermost).
        mcpBuilder.WithRequestFilters(filters =>
        {
            foreach (var (_, add) in BuildCallToolFilterRegistrations())
            {
                add(filters);
            }
        });

        AddResponseGatedExitMessageFilter(mcpBuilder);
        AddRestoredToolsetListChangedMessageFilter(mcpBuilder);
        AddProtocolDiagnosticsMessageFilter(mcpBuilder);

        return mcpBuilder;
    }

    /// <summary>
    /// Registers the outgoing-message filter that lets <c>McpServerControl(StopServer)</c> exit the process only after its
    /// JSON-RPC response (or error) has been written: every outgoing message is sent through the rest of the pipeline (the
    /// transport write and flush) and, when it answers the request id the tool registered on <see cref="ResponseGatedProcessExit"/>,
    /// the exit starts after a short grace delay. Pass-through for every other message and when no gate is registered.
    /// An outgoing filter sees every response, notification and request the server sends, so it stays cheap and never throws.
    /// </summary>
    private static void AddResponseGatedExitMessageFilter(IMcpServerBuilder mcpBuilder)
    {
        mcpBuilder.WithMessageFilters(filters => filters.AddOutgoingFilter(next => async (context, cancellationToken) =>
        {
            var gate = context.Services?.GetService<ResponseGatedProcessExit>();
            if (gate is null || !gate.IsPending)
            {
                await next(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            await gate.ObserveOutgoingAsync(context.JsonRpcMessage, () => next(context, cancellationToken)).ConfigureAwait(false);
        }));
    }

    /// <summary>
    /// Registers the incoming-message filter that sends one <c>tools/list_changed</c> after the client's <c>initialized</c>
    /// notification when a <c>TabToolsetStateStore</c> exists in the host (a launcher-started tab start: a restored start, or a
    /// start after <c>StopServer(keepToolsets: false)</c>, where the client must re-list to drop the removed toolset tools).
    /// Pass-through for every other message, for a host with no store, and after the first send. A failed send is logged as a
    /// warning and never breaks the message pipeline.
    /// </summary>
    private static void AddRestoredToolsetListChangedMessageFilter(IMcpServerBuilder mcpBuilder)
    {
        // One send per host: a local captured by the closure, not a static, so several hosts in one process each send once.
        int sent = 0;
        mcpBuilder.WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, cancellationToken) =>
        {
            await next(context, cancellationToken).ConfigureAwait(false);

            if (context.JsonRpcMessage is not JsonRpcNotification notification
                || notification.Method != NotificationMethods.InitializedNotification
                || context.Services?.GetService<TabToolsetStateStore>() is null
                || Interlocked.CompareExchange(ref sent, 1, 0) != 0)
            {
                return;
            }

            var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("RoslynSentinel.RestoredToolsetListChanged");
            try
            {
                await context.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, cancellationToken).ConfigureAwait(false);
                logger?.LogInformation("Sent tools/list_changed after initialized: a tab-state store exists (launcher-started tab start).");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to send tools/list_changed after initialized for a launcher-started tab start.");
            }
        }));
    }

    /// <summary>
    /// Registers the incoming-message filter that records which MCP protocol revision the client uses and whether it asks for
    /// tool-list notifications (plan_stateless_http_toolset_enablement): logs the requested and negotiated version of
    /// <c>initialize</c>, each <c>server/discover</c>, each change of the per-request protocol version, and every
    /// <c>subscriptions/listen</c> request with its notification flags. A listen request with <c>toolsListChanged: true</c>
    /// also counts in <see cref="ToolsetService.ToolListSubscriptionRequests"/>. Diagnostics only: the pre-pipeline part runs
    /// before <c>next</c> because a listen request does not return until its stream closes, and every failure is swallowed.
    /// </summary>
    private static void AddProtocolDiagnosticsMessageFilter(IMcpServerBuilder mcpBuilder)
    {
        // Per host, like the restored-toolset filter: the last per-request protocol version logged, so each change is logged once.
        string? lastLoggedVersion = null;
        mcpBuilder.WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, cancellationToken) =>
        {
            ILogger? logger = null;
            var request = context.JsonRpcMessage as JsonRpcRequest;
            try
            {
                logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("RoslynSentinel.ProtocolDiagnostics");
                var version = context.JsonRpcMessage.Context?.ProtocolVersion;
                if (version is not null && Interlocked.Exchange(ref lastLoggedVersion, version) != version)
                {
                    logger?.LogInformation("MCP per-request protocol version: {ProtocolVersion}.", version);
                }

                switch (request?.Method)
                {
                    case RequestMethods.Initialize:
                        logger?.LogInformation("MCP initialize: client requested protocol version {ProtocolVersion}.", request.Params?["protocolVersion"]?.ToString());
                        break;
                    case RequestMethods.ServerDiscover:
                        logger?.LogInformation("MCP server/discover received (2026-07-28 or later client).");
                        break;
                    case RequestMethods.SubscriptionsListen:
                        var notifications = request.Params?["notifications"];
                        logger?.LogInformation("MCP subscriptions/listen: notifications {Notifications}.", notifications?.ToJsonString() ?? "(none)");
                        if (notifications?["toolsListChanged"] is JsonValue flag && flag.TryGetValue<bool>(out var wantsTools) && wantsTools)
                        {
                            context.Services?.GetService<ToolsetService>()?.RecordToolListSubscriptionRequest();
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Protocol diagnostics failed before handling an incoming message; the message is handled normally.");
            }

            await next(context, cancellationToken).ConfigureAwait(false);

            if (request?.Method == RequestMethods.Initialize)
            {
                try
                {
                    logger?.LogInformation("MCP initialize: negotiated protocol version {ProtocolVersion}.", context.Server.NegotiatedProtocolVersion);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Protocol diagnostics failed to read the negotiated protocol version.");
                }
            }
        }));
    }

    /// <summary>
    /// The call-tool filters in registration order, which is their nesting order: the first is the outermost
    /// (csharp-sdk McpServerImpl.BuildFilterPipeline folds from the last filter inward). Single source for both
    /// the registration and <see cref="CallToolFilterNames"/>, so a test can pin the order. AgentIdentity sits first so its
    /// keys never reach the echo or the argument validator and every other filter can read the context; it never refuses.
    /// ToolCallTranscript sits third, right inside the echo filter and outside telemetry, so it sees the final response and the
    /// corrected IsError of every path (it wraps argument validation, the breakers and ToolError) with the call id already assigned.
    /// ToolTelemetry sits fourth, right inside the transcript filter, so it wraps every filter that can refuse: argument validation
    /// and the breakers (which refuse without calling next), and ToolError, which copies the body's isError onto
    /// CallToolResult.IsError before the telemetry filter reads it. What each filter does, and how they fit with the result
    /// marshaller: docs/current/references/reference_tool_response_pipeline.md.
    /// The error-policy entry (see AddErrorPolicyFilter) is added only when ErrorPolicyOptions.Mode is not Off, directly outside WorkspaceNotLoaded.
    /// </summary>
    // TODO generate the filter order into docs/generated from CallToolFilterNames so ArchitectureDocFreshnessTests catches a stale filter list in the reference doc.
    private static (string Name, Action<IMcpRequestFilterBuilder> Add)[] BuildCallToolFilterRegistrations()
    {
        var registrations = new List<(string Name, Action<IMcpRequestFilterBuilder> Add)>
        {
        ("AgentIdentity", AddAgentIdentityFilter),
        ("ToolCallEcho", AddToolCallEchoFilter),
        ("ToolCallTranscript", AddToolCallTranscriptFilter),
        ("ToolTelemetry", AddToolTelemetryFilter),
        // Fifth, outside argument validation, so it sees (and can refuse before) the plain-text validation rejections that
        // make up the alternating-argument-error loops, and inside telemetry, so a refusal is counted like any error.
        ("LoopLatch", AddLoopLatchFilter),
        ("ArgumentValidation", AddArgumentValidationFilter),
        ("HaltStamp", AddHaltStampFilter),
        ("WorkspaceStaleStamp", AddWorkspaceStaleStampFilter),
        ("ToolError", AddToolErrorFilter),
        ("WorkspaceNotLoaded", AddWorkspaceNotLoadedFilter),
        ("Drift", AddDriftFilter),
        ("LargeResultOffload", AddLargeResultOffloadFilter),
        ("WirePathRelativize", AddWirePathRelativizeFilter),
        ("OrientationBreaker", AddOrientationBreakerFilter),
        ("UnrecoverableBreaker", AddUnrecoverableBreakerFilter),
        };
        if (NearMissRecorder.IsActive)
        {
            // Directly inside ToolTelemetry (fourth), so AgentIdentity, ToolCallEcho, ToolCallTranscript and ToolTelemetry keep positions 0-3 and every filter that can
            // rebuild a result (LoopLatch refusal, WorkspaceNotLoaded's failure handler) is inside it. Absent when both near-miss options are at their
            // defaults, so the default build is byte-identical to one without this filter.
            registrations.Insert(registrations.FindIndex(r => r.Name == "ToolTelemetry") + 1, ("NearMissRecord", AddNearMissRecordFilter));
        }

        if (ErrorPolicyOptions.Mode != ErrorPolicyMode.Off)
        {
            // Sits directly outside WorkspaceNotLoaded so it sees thrown errors (already mapped to text by the filter inside it)
            // and returned errors alike. Absent when the mode is Off, so Off is byte-identical to a build without this feature.
            registrations.Insert(registrations.FindIndex(r => r.Name == "WorkspaceNotLoaded"), ("ErrorPolicy", AddErrorPolicyFilter));
        }

        return [.. registrations];
    }

    /// <summary>
    /// Registers the near-miss record filter (<see cref="NearMissRecordFilter"/>): opens a <see cref="NearMissRecorder"/> scope for the call and writes
    /// the records made during it into <c>result._meta.nearMissSubstitutions</c>. An EVALUATION aid, registered only when
    /// <see cref="NearMissRecorder.IsActive"/> (see <see cref="BuildCallToolFilterRegistrations"/>).
    /// </summary>
    private static void AddNearMissRecordFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            (context, cancellationToken) =>
            {
                var logger = context.Server.Services?.GetService<ILoggerFactory>()?.CreateLogger("NearMissRecordFilter");
                return NearMissRecordFilter.InvokeAsync(logger, () => next(context, cancellationToken));
            }));
    }

    /// <summary>
    /// Registers the error-policy filter (<see cref="ErrorPolicyFilter"/>): on a not-found error result it appends the operator's
    /// policy pointer or inline entry as one trailing text block. The mode and policy file are read once here, at startup.
    /// </summary>
    private static void AddErrorPolicyFilter(IMcpRequestFilterBuilder filters)
    {
        var mode = ErrorPolicyOptions.Mode;
        ErrorPolicyDocument? document = mode == ErrorPolicyMode.Inline ? ErrorPolicyDocument.Load(ErrorPolicyOptions.PolicyFilePath!) : null;
        var longEntries = ErrorPolicyFilter.FindLongEntries(document);
        var warned = 0;
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            (context, cancellationToken) =>
            {
                var logger = context.Server.Services?.GetService<ILoggerFactory>()?.CreateLogger("ErrorPolicyFilter");
                // The long-entry warning is logged on the first tool call because no logger exists at registration.
                if (longEntries.Count > 0 && Interlocked.Exchange(ref warned, 1) == 0)
                {
                    ErrorPolicyFilter.WarnLongEntries(longEntries, logger);
                }

                return ErrorPolicyFilter.InvokeAsync(
                    mode,
                    document,
                    context.Params?.Name,
                    context.Params?.Arguments,
                    logger,
                    () => next(context, cancellationToken));
            }));
    }

    /// <summary>Names of the call-tool filters in the order <see cref="AddRoslynSentinelToolsBasic"/> registers them (outermost first), for the current error-policy mode.</summary>
    public static IReadOnlyList<string> CallToolFilterNames => BuildCallToolFilterRegistrations().Select(r => r.Name).ToArray();

    private static void RegisterRoslynSentinelToolsBasic(IServiceCollection services, IMcpServerBuilder mcpBuilder, HashSet<string> activeModes, HashSet<string>? includeTools = null, HashSet<string>? excludeTools = null, IReadOnlySet<string>? hideTools = null)
    {
        // The input is static, so every host and test host fails the same way, instead of a silently dead tool.
        var tableProblems = ToolClassRegistry.DescribeToolClassTableProblems(ToolClassRegistry.ModeToToolClasses, ToolClassRegistry.BasicToolClassTypes, ToolClassRegistry.AdvancedToolClassTypes);
        if (tableProblems.Count > 0)
        {
            throw new InvalidOperationException("ToolClassRegistry tables are inconsistent: " + string.Join(" ", tableProblems));
        }

        var resolvedIncludeTools = includeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedExcludeTools = excludeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeToolClasses = ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes,
            ToolClassRegistry.ModeToToolClasses,
            resolvedIncludeTools,
            resolvedExcludeTools);

        // Registered from the resolved class set (not a static) so error messages can only ever
        // name a tool this server actually exposes, and so tests can construct one with an
        // arbitrary tool set. Registering here (rather than in Advanced too) is sufficient because
        // every class declaring a tool WriteToolAdviceHelper may name is in
        // ModeToToolClasses, so this resolution already sees all of them -> Advanced adds no
        // whole-file-write tools. See WriteToolAdviceHelper's remarks.
        // The exclusive claude-lean mode also restricts WHICH tools of its classes are registered.
        // Registered here because ToolSurfaceStartupPopulation reads the ToolAllowList singleton from DI
        // when it builds the startup tools. Present only in claude-lean or when hideTools is non-empty.
        // Per-tool hiding (--hide-tools) exists because WriteFile shares a class with DeleteFile, so the
        // per-class --exclude-tools cannot hide it alone. WriteToolAdviceHelper and ActiveToolSurface read
        // toolAllowList.ToolNames, so advice and status follow the filtered list automatically.
        // Outside claude-lean, hideTools builds an allow-list of every discovered RoslynSentinel tool minus the hidden
        // names, so hiding one tool of a shared class works in every mode. Class gating still applies on top.
        var toolAllowList = activeModes.Contains(ToolClassRegistry.ClaudeLeanMode)
            ? new ToolAllowList(ToolClassRegistry.ClaudeLeanToolNames.Where(n => hideTools is null || !hideTools.Contains(n)))
            : hideTools is { Count: > 0 }
                ? new ToolAllowList(McpToolSchemaPatcher.DiscoverToolMethods(
                        AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel", StringComparison.Ordinal) == true))
                    .Select(t => t.ToolName)
                    .Where(n => !hideTools.Contains(n)))
                : null;
        if (toolAllowList is not null)
        {
            services.AddSingleton(toolAllowList);
        }

        // The default schema patches are registered as instances from DefaultToolSchemaPatches.All (the classes are
        // internal to Common, so the generic ServiceDescriptor.Singleton<IToolSchemaPatch, X>() form would be CS0122 here).
        // A host adds a patch by registering it before or after this call, and replaces one with RemoveAll<IToolSchemaPatch>()
        // plus its own list. An empty registration falls back to the defaults, so "no patches" is not expressible.
        foreach (var patch in DefaultToolSchemaPatches.All)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IToolSchemaPatch), patch));
        }

        // A host (the eval harness, Step 10) registers its own ParameterSurface first; this one is built on first
        // resolve, which is when tools are built, so it reads SchemaOptions after SchemaOptions.Configure exactly as
        // the patch does today. The patch reads this standalone singleton, never the controller: resolving the
        // controller resolves IOptions<McpServerOptions>, which builds every tool.
        services.TryAddSingleton(_ => ParameterSurface.FromStartup());

        // Unconditional, mode-independent dependencies of the tool classes below (the *Impl helpers, the plain ILogger,
        // and a default WriteToolAdviceHelper). TryAdd-only, so the gated WriteToolAdviceHelper registered right after
        // (plain AddSingleton, last wins) overrides the default. See Tools.Basic's ToolServiceCollectionExtensionsBasic.
        services.AddRoslynSentinelToolImplsBasic();

        services.AddSingleton(new WriteToolAdviceHelper(activeToolClasses, toolAllowList?.ToolNames));

        // Captured once here (the same values ResolveActiveToolClasses/DescribeNoActiveToolsFailure
        // use to build the startup guidance message) so McpServerStatusTool can report today's actual
        // mode/include-tools/exclude-tools resolution instead of a caller having to guess or
        // restart the server to find out.
        var activeToolSurface = new ActiveToolSurface(
            modeArg: string.Join(",", activeModes.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)),
            activeModes: activeModes,
            includeTools: resolvedIncludeTools,
            excludeTools: resolvedExcludeTools,
            activeToolClasses: activeToolClasses,
            classModes: ToolClassRegistry.BuildClassToModes(),
            allowedToolNames: toolAllowList?.ToolNames);
        // The instance is built eagerly above so the claude-lean rebinding of activeToolClasses below cannot leak into it.
        // The controller is resolved lazily and read through CurrentIfBuilt, so resolving ActiveToolSurface (14 constructors,
        // the engine closure, three filters) never builds the tool collection. No consumer is edited: they all take the DI instance.
        // ImplementationInstance is read only for ToolAllowList (McpToolSchemaPatcher.cs ~line 139), so the factory form breaks no reader.
        services.AddSingleton<ActiveToolSurface>(sp =>
        {
            var controller = new Lazy<ToolSurfaceController?>(() => sp.GetService<ToolSurfaceController>());
            return activeToolSurface.WithLiveSnapshot(() => controller.Value?.CurrentIfBuilt);
        });
        // The controller owns the runtime-toolset layer; claude-lean's ToolsetService takes it, so both see one state.
        services.AddSingleton<ToolSurfaceController>();
        services.AddHostedService<ToolSurfaceStartup>();

        // Singleton so its cache outlives the transient OptionsFactory. The population reads the ToolAllowList and the
        // ToolSurfaceStartupRequest singletons from DI, so it is registered once here.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<McpServerOptions>, ToolSurfaceStartupPopulation>());

        // ServerStatusTools is always selected (ToolClassRegistry.AlwaysOnToolClasses), independent of
        // activeToolClasses/--mode/--include-tools/--exclude-tools: the one tool meant to be reachable whatever
        // the selection, since it exists for the case where the selection itself might be the problem.
        // Deliberately not a registry class, so never counted or excludable; registered through the BasicToolClassTypes table.

        // claude-lean only: McpToolsetControlTool can add any on-demand tool at runtime, and a tool's class is
        // constructed by ActivatorUtilities from the container - so the DEPENDENCIES of those classes (the Impl
        // singletons registered by AddRoslynSentinelToolImplsBasic above) must exist even though their tools are not exposed at
        // startup. The per-tool allow-list already keeps the non-Core tools out of the startup surface
        // (ToolSurfaceStartupPopulation applies it), so widening the set here registers dependencies only. Rebinding (not mutating) keeps the
        // ActiveToolSurface/WriteToolAdviceHelper sets above - what McpServerStatusTool and advice report - exact.
        if (activeModes.Contains(ToolClassRegistry.ClaudeLeanMode))
        {
            bool toolsetControlActive = activeToolClasses.Contains("ToolsetControlTools");
            activeToolClasses = new HashSet<string>(activeToolClasses, StringComparer.OrdinalIgnoreCase);
            foreach (var onDemandClass in ToolClassRegistry.ClaudeLeanOnDemandToolClasses.Where(c => !resolvedExcludeTools.Contains(c)))
            {
                activeToolClasses.Add(onDemandClass);
            }

            services.AddSingleton<IKnownToolClassNames, KnownToolClassNames>();

            if (toolsetControlActive)
            {
                services.AddSingleton(sp => new ToolsetService(
                    sp.GetRequiredService<ToolSurfaceController>(),
                    sp,
                    // Excluded classes stay unavailable to toolsets too: SubAgent is on demand (modelEval) and the
                    // child server's --exclude-tools=SubAgentTools recursion guard must still hold there.
                    new HashSet<string>(
                        ToolClassRegistry.ClaudeLeanOnDemandToolClasses.Where(c => !resolvedExcludeTools.Contains(c)),
                        StringComparer.Ordinal)));
                // Per-tab toolset persistence (toolsets.json in the tab state dir). Only when the tab dir is known;
                // ApplyStartupToolsets restores from it and subscribes Save to EnabledToolSetsChanged.
                var tabStateDir = ServerInstancePaths.Current.TabStateDir;
                if (tabStateDir is not null)
                {
                    services.AddSingleton(sp => new TabToolsetStateStore(
                        tabStateDir,
                        Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID"),
                        sp.GetService<ILoggerFactory>()?.CreateLogger<TabToolsetStateStore>()));
                }
            }
        }

        // Sees the claude-lean widened set, so this stays after the activeToolClasses rebinding in the claude-lean block above.
        AddStartupToolClasses(services, "Basic", ToolClassRegistry.SelectStartupToolTypes(ToolClassRegistry.BasicToolClassTypes, activeToolClasses, activeModes.Contains(ToolClassRegistry.ClaudeLeanMode)));
    }

    /// <summary>
    /// Records one registration method's selected startup tool classes for <c>ToolSurfaceStartupPopulation</c>.
    /// Each class is registered as a singleton so the population can build it; the population turns the list into
    /// the startup tools/list entries.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="source">Names the registration method that selected the classes ("Basic" or "Advanced").</param>
    /// <param name="selected">The classes selected for this launch, in tools/list order.</param>
    internal static void AddStartupToolClasses(IServiceCollection services, string source, IReadOnlyList<Type> selected)
    {
        // tools/list order is registration order, and the population runs after the SDK's own options setup, so the
        // classes migrated to the tables are a suffix after the tools the DI blocks above register.
        foreach (var type in selected)
        {
            services.TryAddSingleton(type);
        }

        services.AddSingleton(new ToolSurfaceStartupRequest(source, selected));
    }

    /// <summary>
    /// Pre-warms MSBuildLocator (which takes ~5–8 s on first call) and optionally auto-loads a solution.
    /// Should be called after <see cref="Microsoft.Extensions.Hosting.IHost.Build"/> / <see cref="Microsoft.AspNetCore.Builder.WebApplication.Build"/>.
    /// </summary>
    public static void WarmupAndAutoLoadBasic(this IServiceProvider services, string? solutionPath, ILogger? logger = null, string? baseRepoDirectory = null)
    {
        // Never awaited and never faulting: loads libgit2's native library in the background (GitBackendWarmup); McpServerStatus is the only waiter.
        GitBackendWarmup.Shared.Start();

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
            // Startup auto-load records StartupLoad (not ExplicitLoad) when it fails.
            _ = workspaceManager.LoadSolutionAsync(solutionPath, null, SolutionLoadWait.DefaultTimeout, WorkspaceReloadTriggerKind.StartupLoad)
                .ContinueWith(
                    t => logger?.LogError(t.Exception!.GetBaseException(), "Auto-load solution failed: {Path}", solutionPath),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    // Centralized exception filter: an exception that escapes a tool becomes a structured failure (the
    // same SentinelCallToolResult body a tool returns itself: isError + errorData{errorCode, message,
    // detail, structuredDetail}) built by ToolErrorMapper.ToResultError, with CallToolResult.IsError = true.
    // A SolutionNotLoadedException keeps its actionable "Call LoadSolution" sentence and a typed
    // ToolException keeps its code and payload. A client abort (the request's own token cancelled) is
    // rethrown for the SDK; any other cancellation or timeout becomes a structured Cancelled/TimedOut.
    // Registered after AddToolErrorFilter and before the drift/offload/wire filters, so those never see an
    // escaped exception (unchanged from before). See CallToolFailureHandler.
    private static void AddWorkspaceNotLoadedFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            (context, cancellationToken) => CallToolFailureHandler.InvokeAsync(
                () => next(context, cancellationToken), context.Params?.Name, cancellationToken)));
    }

    private static void AddToolErrorFilter(IMcpRequestFilterBuilder filters)
    {

        // Domain-failure -> protocol-error sync: every tool in this codebase (by design, see
        // docs/current/feedback_agent_friendly_error_messages.md) catches its own exceptions
        // and returns a SentinelCallToolResult<T>/ApplyChangesResult/etc. with IsError=true instead of
        // throwing, so the MCP SDK's own exception-based IsError detection never fires for a
        // domain-level failure. Copy the serialized response body's top-level "isError" field onto
        // CallToolResult.IsError (same name and polarity, so no inversion), so a client relying on the
        // protocol-level flag (rather than parsing the JSON body) sees an accurate signal.
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
                                    doc.RootElement.TryGetProperty("isError", out var errorProp) &&
                                    errorProp.ValueKind == System.Text.Json.JsonValueKind.True)
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
    }

    private static void AddDriftFilter(IMcpRequestFilterBuilder filters)
    {

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
    }

    private static void AddLargeResultOffloadFilter(IMcpRequestFilterBuilder filters)
    {

        // Generic large-result offload backstop (docs/current/references/reference_tool_response_pipeline.md;
        // origin: docs/current/proposals/closed/proposal_centralized_large_result_filter.md): sums the length of text
        // content blocks in the response (no JSON re-serialization) and, above
        // LargeResultHelper.InlineBudgetChars, writes the response text to disk as a ResultWrapperType.Envelope
        // via LargeResultHelper.StoreEnvelopeAsync (the type the marshaller stores, so GetLargeResult pages it with
        // the Envelope reader: inferred path, offset/limit) and replaces the response with a small pointer
        // (resultId) instead of just logging. This is deliberately shape-agnostic: the result marshaller
        // does the offloading, so this only catches text it did not cut (an exception catch-all or refusal body,
        // plain string or content-block returns, non-object JSON, nothing pageable, or a marshaller set to off or
        // passthrough). A result the marshaller fitted has a small response by the time it reaches here and this
        // filter is a no-op for it. A body that is not JSON cannot be stored as an Envelope (StoreEnvelopeAsync
        // parses it); the failure is swallowed below and the body goes out whole, as before.
        // Grep the log for "Large tool result" / "Result marshaller miss" to review offenders without parsing full payloads.
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                // The marshaller raises this when it returns a response over budget on purpose (proposal_sequence_pager.md
                // Decided 4: one unit larger than a page goes out alone). Re-wrapping that under a new resultId would hand the
                // model a second pointer next to the marshaller's own (or replace a result with nothing left to page), so the
                // text passes through; the marshaller has already written the contract violation to the server log.
                using var oversizeSignal = OversizeUnitSignal.Open();
                var result = await next(context, cancellationToken);
                if (oversizeSignal.Raised)
                {
                    return result;
                }

                // No tool-name exemption for GetLargeResult: its Envelope readers (WorkspaceReadNavigationImpl.PageSequence and
                // PageEnvelope) build their hints with PagingHints.ForAlreadyPagedPage, which raises OversizeUnitSignal for the
                // current call while the page is built. That happens in the tool, so it holds in every marshaller mode (mode off
                // registers no marshaller) and after a marshaller exception; a page that one oversize unit leaves over budget is
                // caught by the check above and never re-stored under a new resultId. Pinned by
                // LargeResultOffloadFilterTests.GetLargeResult_OversizeUnitPage_IsNotStoredAgain (mode off) and its ModeOn twin.
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
                            text.Length <= LargeResultHelper.InlineBudgetChars)
                        {
                            continue;
                        }

                        var logger = context.Server.Services?.GetService<ILogger<PersistentWorkspaceManager>>();
                        // Result marshaller miss detector (plan_wire_result_marshaller.md Step 4, Step 10 slice D1): in mode On a tool
                        // built through SentinelToolFactory (SentinelToolFactory.IsHooked) is marshalled, so a body that still
                        // arrives here over the threshold is a body the marshaller never fitted: an exception catch-all or refusal
                        // body, a result it handed back whole (non-object JSON root, plain string or content-block return, nothing
                        // pageable), or a pager/reserve bug (a fitted result leaves the pointer and echo reserve free). Each is an
                        // error-level signal; the body is still stored below as the backstop. In mode Off or Passthrough the
                        // marshaller is deliberately not cutting, so the original warning is all that is logged.
                        if (ResultMarshallerOptions.Mode == ResultMarshallerMode.On && SentinelToolFactory.IsHooked(context.Params?.Name))
                        {
                            logger?.LogError(
                                "Result marshaller miss: hooked tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdBytes}) the marshaller did not cut; the offload filter is handling it as the backstop. Check ResultPager hints and LargeResultHelper.EchoReserveChars.",
                                context.Params?.Name, text.Length, LargeResultHelper.InlineBudgetChars);
                        }
                        else
                        {
                            logger?.LogWarning(
                                "Large tool result: tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdBytes})",
                                context.Params?.Name, text.Length, LargeResultHelper.InlineBudgetChars);
                        }

                        var workspaceManager = context.Server.Services?.GetService<PersistentWorkspaceManager>();
                        var solutionRoot = workspaceManager?.GetSolutionRoot();
                        var stored = await LargeResultHelper.StoreEnvelopeAsync(text, solutionRoot, cancellationToken);
                        if (!stored.offloaded)
                        {
                            // No solution loaded, or the write failed to qualify -> fail closed to
                            // pass-through rather than blocking the call on a guardrail defect.
                            continue;
                        }

                        // Best-effort size hint so the pointer alone tells the model whether this
                        // is 1 big hit or 300 small ones, instead of forcing a GetLargeResultTool round
                        // trip just to find out. Shape-agnostic by design (this filter runs for
                        // every tool, typed and untyped alike) -> parsed straight from the raw JSON
                        // rather than any tool-specific DTO. itemCount looks for the envelope's own
                        // totalRecords first (set by ForPossiblyLargeDataAsync callers), then falls
                        // back to LargeResultHelper.CountResultItems, which sums every array found
                        // under successData (recursing through nested objects) - covers untyped
                        // tools like FindReferencesTool that set SuccessData directly.
                        // statusMessage is relayed whenever the tool already populated one (e.g.
                        // FindReferencesTool/SearchSolutionTextTool's SummarizeListResult-based summary) so
                        // that hint survives the offload instead of being silently dropped.
                        int? itemCount = null;
                        string? statusMessage = null;
                        System.Text.Json.Nodes.JsonNode? listSummary = null;
                        bool? isError = null;
                        try
                        {
                            var root = System.Text.Json.Nodes.JsonNode.Parse(text)?.AsObject();
                            if (root != null)
                            {
                                if (root.TryGetPropertyValue("isError", out var isErrorNode) &&
                                    isErrorNode != null &&
                                    (isErrorNode.GetValueKind() == System.Text.Json.JsonValueKind.True ||
                                     isErrorNode.GetValueKind() == System.Text.Json.JsonValueKind.False))
                                {
                                    isError = isErrorNode.GetValue<bool>();
                                }

                                // listSummary (per-file counts, already capped by SummarizeListResult) is
                                // the only per-file hint once a list tool's statusMessage stops repeating
                                // the file paths, and a raw offload drops it unless it is relayed here.
                                if (root.TryGetPropertyValue("listSummary", out var listSummaryNode) &&
                                    listSummaryNode is System.Text.Json.Nodes.JsonObject)
                                {
                                    listSummary = listSummaryNode;
                                }

                                if (root.TryGetPropertyValue("totalRecords", out var totalRecordsNode) &&
                                    totalRecordsNode != null &&
                                    totalRecordsNode.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                                {
                                    itemCount = totalRecordsNode.GetValue<int>();
                                }
                                else if (root.TryGetPropertyValue("successData", out var successDataNode) && successDataNode != null)
                                {
                                    itemCount = LargeResultHelper.CountResultItems(successDataNode);
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

                        // Preserve the original body's isError onto the protocol-level IsError
                        // flag: this filter overwrites result.Content below, so the IsError-sync
                        // filter (which wraps this one and runs after it returns) would otherwise
                        // inspect this offload envelope instead of the real body and find no
                        // "isError" key, silently leaving IsError at its prior (successful)
                        // value even when the original tool call failed.
                        if (isError == true)
                        {
                            result.IsError = true;
                        }

                        result.Content = [new ModelContextProtocol.Protocol.TextContentBlock
                            {
                                Text = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    solutionRoot,
                                    offloaded = true,
                                    resultId = stored.resultId,
                                    sizeBytes = text.Length,
                                    sizeChars = text.Length,
                                    itemCount,
                                    isError,
                                    statusMessage,
                                    listSummary,
                                    message = $"Result is {text.Length} bytes (threshold: {LargeResultHelper.InlineBudgetChars}).{hint} Use {RoslynSentinel.Common.LargeResultHints.FetchCall(stored.resultId!)} to page through results."
                                }, RoslynSentinel.Common.SharedJsonOptions.Compact)
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
    }

    // Wire path relativization backstop filter (see docs/current/plans/closed/plan_wire_relative_paths_backstop_filter.md):
    // runs inside (registered after) the offload filter so the 15KB threshold and stored raw file see the
    // relativized text; never throws. Rewrites absolute paths in tool result JSON text blocks to be relative
    // to the solution root, enabling agents to work with portable relative paths.
    private static void AddWirePathRelativizeFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);

                try
                {
                    if (!RoslynSentinel.Common.WirePathOptions.Enabled || result.Content is null)
                    {
                        return result;
                    }

                    var root = context.Server.Services?.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot();
                    if (string.IsNullOrEmpty(root))
                    {
                        return result;
                    }

                    foreach (var block in result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>())
                    {
                        var text = block.Text;
                        if (string.IsNullOrEmpty(text) || !text.AsSpan().TrimStart().StartsWith("{"))
                        {
                            continue;
                        }

                        var rewritten = RoslynSentinel.Common.WirePathRewriter.RewriteJsonText(text, root, out var rewrites);
                        if (rewritten is null)
                        {
                            continue;
                        }

                        block.Text = rewritten;

                        var logger = context.Server.Services?.GetService<ILogger<PersistentWorkspaceManager>>();
                        logger?.LogDebug("Wire path rewrite: tool '{Tool}' rewrote {Count} value(s)", context.Params?.Name, rewrites);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Wire path relativize filter failed: {ex}");
                }

                return result;
            }));
    }

    private static bool IsEmptyListAllResult(string? toolName, ModelContextProtocol.Protocol.CallToolResult result)
    {
        if (toolName != "ListAll")
        {
            return false;
        }

        foreach (var block in result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>())
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(block.Text);
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    root.TryGetProperty("totalRecords", out var prop) &&
                    prop.ValueKind == System.Text.Json.JsonValueKind.Number &&
                    prop.GetInt32() == 0)
                {
                    return true;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Continue if parse fails
            }
        }

        return false;
    }

    private static void AddOrientationBreakerFilter(IMcpRequestFilterBuilder filters)
    {

        // Orientation breaker: after OrientationBreakerTripThreshold consecutive zero-match
        // Search(mode: text) calls (see PersistentWorkspaceManager.RecordSearchOutcome), restrict
        // tool calls to a small orienting allowlist until one of them succeeds. Exists because
        // agents repeatedly retry text search with reworded guesses instead of switching to
        // ListAllTool/GetFileOutlineTool, even though both the system prompt and Search(mode: text)'s own
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
                        // Name only tools this server can serve (the claude-lean allow-list hides ListAll/ListSolutionItems);
                        // no surface registered = every tool is callable.
                        var surface = context.Server.Services?.GetService<ActiveToolSurface>();
                        Func<string, bool>? isCallable = surface is null ? null : surface.IsCallable;
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = automaticBreaker.StateMessage(isCallable) ?? OrientationBreakerFallbackText(isCallable) }],
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
                        // Search(mode: text)'s own outcome is now recorded inline, from inside
                        // WorkspaceReadNavigationImpl.SearchSolutionTextTool itself, immediately after
                        // the match count is known -> not here. Recording it a second time here
                        // would double-count every call against the trip threshold. Every other
                        // Search mode (symbol/references/declaration-kind listings) never reaches
                        // that inline recording path, so only mode: text is excluded here - a
                        // successful Search(mode: symbol) etc. must still reset the breaker like
                        // any other tool.
                        var isTextSearch = toolName == "Search" &&
                            context.Params?.Arguments is { } args &&
                            args.TryGetValue("mode", out var modeArg) &&
                            modeArg.ValueKind == System.Text.Json.JsonValueKind.String &&
                            modeArg.GetString() == "text";

                        // An empty ListAllTool (nothing listed) is not orientation: it must not lift the breaker.
                        if (!isTextSearch && automaticBreaker.IsTripped() && result.IsError != true && !IsEmptyListAllResult(toolName, result))
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
    }

    // Public, not internal: RoslynSentinel.Server.Advanced has no InternalsVisibleTo for its tests, so the Tests.Server fixtures call this directly.
    public static string OrientationBreakerFallbackText(Func<string, bool>? isCallable)
    {
        bool Callable(string toolName) => isCallable is null || isCallable(toolName);

        var searchCall = ToolCallHint.Call("Search", ("mode", "text"));
        var listingCalls = new List<string>();
        if (Callable("ListAll"))
        {
            listingCalls.Add(ToolCallHint.Call("ListAll", ("kind", "all")));
        }

        if (Callable("ListSolutionItems"))
        {
            listingCalls.Add(ToolCallHint.Call("ListSolutionItems", ("kind", "all")));
        }

        return listingCalls.Count == 0
            ? $"{searchCall} is DISABLED. No listing tool is callable on this surface."
            : $"{searchCall} is DISABLED. You MUST call {string.Join(" or ", listingCalls)} now.";
    }

    private static void AddHaltStampFilter(IMcpRequestFilterBuilder filters)
    {
        // Halt stamping: while any breaker is tripped or the external-drift latch is set, stamp
        // isSessionHalted / sessionHaltKind / sessionHaltReason / sessionHaltRecovery onto the first
        // text block of every response, so recovery needs no discovery call. Registered right after
        // the argument-validation filter (outer to the breaker filters, so it sees their plain-text
        // refusals; inner to the echo filter, which still stamps last). State is read AFTER next
        // returns, so an orientation breaker an inner filter just reset is not stamped.
        // See docs/current/plans/closed/plan_external_file_drift_tool_and_halt_stamping.md (Decision 3).
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);

                try
                {
                    var services = context.Server.Services;
                    var manager = services?.GetService<PersistentWorkspaceManager>();
                    if (manager is null)
                    {
                        return result;
                    }

                    // The live tool collection is the authoritative answer to "is ResetMutationBreaker
                    // exposed right now" (static modes, allow-lists and McpToolsetControlTool all land in it).
                    // Any failure to resolve it counts as "not active": the safe recovery text is "stop and report".
                    var resetActive = false;
                    try
                    {
                        resetActive = services?
                            .GetService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>()?
                            .Value.ToolCollection is { } toolCollection &&
                            toolCollection.TryGetPrimitive("ResetMutationBreaker", out _);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Halt stamp ResetMutationBreaker lookup failed: {ex}");
                    }

                    // Explicit casts: the manager implements the breaker members explicitly.
                    var info = HaltInfo.From(
                        (IWorkspaceHealthReporter)manager,
                        (IUnrecoverableBreaker)manager,
                        (IAutomaticCircuitBreaker)manager,
                        (IManualCircuitBreaker)manager,
                        resetActive);

                    if (info is null)
                    {
                        return result;
                    }

                    // The drift tool's own reply already carries the drift state.
                    if (context.Params?.Name == "ExternalFileDrift" && info.Kind == HaltInfo.KindExternalDrift)
                    {
                        return result;
                    }

                    HaltStamp.Stamp(result, info);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Halt stamp failed: {ex}");
                }

                return result;
            }));
    }

    private static void AddWorkspaceStaleStampFilter(IMcpRequestFilterBuilder filters)
    {
        // Stale-workspace stamping: while a workspace reload has failed and not yet succeeded, stamp
        // workspaceStale / workspaceStaleReason / workspaceStaleRecovery onto the first text block of every
        // response, so a caller sees why source edits are refused. State is read AFTER next returns, so a
        // LoadSolution that just cleared the failure is not stamped. Advanced reuses this registration via
        // AddRoslynSentinelToolsBasic. See docs/current/proposals/proposal_workspace_reload_failure_visibility.md (slice 3).
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);

                try
                {
                    var manager = context.Server.Services?.GetService<PersistentWorkspaceManager>();
                    var failure = (manager as IWorkspaceReloadFailureProvider)?.ReloadFailure;
                    if (failure is not null)
                    {
                        WorkspaceStaleStamp.Stamp(result, WorkspaceStaleText.Reason(failure), WorkspaceStaleText.Recovery);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Workspace stale stamp failed: {ex}");
                }

                return result;
            }));
    }

    private static void AddUnrecoverableBreakerFilter(IMcpRequestFilterBuilder filters)
    {

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
        // defect. Anything not in the allow-list is refused, so the safe default is "refused".
        // The allow-list is defined by [UnrecoverableBreaker(Allowed)] attributes on tool methods.
        // These are the tools an operator or agent needs to read the state and stop cleanly.
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
                        !UnrecoverableBreakerPolicy.IsAllowed(toolName))
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
    }

    /// <summary>
    /// Registers the agent identity filter: strips <c>_agentId</c>, <c>_agentType</c> and <c>_sessionId</c> from the arguments and sets
    /// <see cref="ToolCallContext"/> for the call; the logic is <see cref="AgentIdentityCapture.Enter"/>.
    /// <para>
    /// Must be registered first. The echo snapshots arguments before calling next, so only a filter outside it keeps the
    /// keys out of the echoed arguments, and they must be gone before the validation filter refuses them as unknown. An
    /// AsyncLocal set by an inner filter is not visible to an outer filter's code after next returns, so being outermost
    /// also lets the echo and telemetry read the context.
    /// </para>
    /// </summary>
    private static void AddAgentIdentityFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            // Async on purpose, unlike the telemetry filter: an async method restores the caller's ExecutionContext when it
            // returns, so the AsyncLocal set here cannot leak out of this call.
            async (context, cancellationToken) =>
            {
                AgentIdentityCapture.Enter(
                    context.Params?.Name,
                    context.Params?.Arguments,
                    () => (context.Server.Services?.GetService<PersistentWorkspaceManager>() as IEditAttributionLedgerProvider)?.EditAttributions.CurrentSeq ?? 0,
                    () => context.Server.Services?.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot(),
                    // The edit effect note (EditEffectEvidence) needs the index and the in-memory solution; both are read lazily or only for mapped-term calls.
                    () => (context.Server.Services?.GetService<PersistentWorkspaceManager>() as IEditEffectIndexProvider)?.EditEffects,
                    () => context.Server.Services?.GetService<PersistentWorkspaceManager>()?.CurrentSolution,
                    // True when the manager reports no recorded reload failure, false when one is recorded, null (unknown) with no manager.
                    () => context.Server.Services?.GetService<PersistentWorkspaceManager>() is { } manager ? manager.ReloadFailure is null : null);
                return await next(context, cancellationToken);
            }));
    }

    /// <summary>
    /// Registers the tool-call echo filter: stamps a <c>toolCall</c> property (call id, tool name,
    /// truncated arguments as sent) onto the first text block of every response, so a transcript,
    /// log line or offloaded result can be tied back to the call that produced it. See
    /// <see cref="ToolCallEcho"/> and docs/current/plans/closed/plan_tool_call_echo.md.
    /// The echo is off by default; the call id is assigned either way.
    /// <para>
    /// Must be registered right inside <see cref="AddAgentIdentityFilter"/>, ahead of every other filter: an earlier filter
    /// is further out, so this one post-processes after them and sees the final content of every path (argument-validation rejection, breaker refusals,
    /// the exception catch-all, and the large-result offload stub), none of which carry a tool-level
    /// identifier of their own. The arguments are snapshotted before <c>next</c> runs because the
    /// validation filter repairs parameter-name case in place, and the echo must show what the caller actually sent, apart from the identity keys, which are not the caller's own arguments.
    /// </para>
    /// </summary>
    private static void AddToolCallEchoFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                string? toolCallId = null;
                try
                {
                    toolCallId = ToolCallEcho.NewToolCallId();
                    // The same id reaches the tool (ToolCallContext is set by the identity filter just outside this one), so a commit
                    // trailer can carry the id the response echoes. Assigned even when the echo is off.
                    RoslynSentinel.Common.ToolCallContext.Current?.AssignToolCallId(toolCallId);
                }
                catch (Exception ex)
                {
                    // A diagnostic aid must never break a call.
                    Debug.WriteLine($"Tool call id assignment failed: {ex}");
                }

                if (!RoslynSentinel.Common.ToolCallEchoOptions.Enabled || toolCallId is null)
                {
                    return await next(context, cancellationToken);
                }

                System.Text.Json.Nodes.JsonObject? echo = null;
                try
                {
                    echo = ToolCallEcho.CreateEcho(
                        toolCallId, context.Params?.Name, context.Params?.Arguments);
                }
                catch (Exception ex)
                {
                    // A diagnostic aid must never break a call.
                    Debug.WriteLine($"Tool call echo snapshot failed: {ex}");
                }

                var result = await next(context, cancellationToken);

                if (echo is not null)
                {
                    try
                    {
                        ToolCallEcho.Stamp(result, echo);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Tool call echo stamp failed: {ex}");
                    }
                }

                return result;
            }));
    }

    /// <summary>
    /// Registers the call-transcript filter: appends one JSONL line per tool call (arguments as received, <c>isError</c>, the
    /// response text as sent, duration) to the host's <see cref="ToolCallTranscript"/>. The logic is
    /// <see cref="ToolCallTranscriptRecorder.RecordAsync"/>; the filter returns the very same result untouched.
    /// <para>
    /// Must be registered third - directly inside the echo filter and outside <see cref="AddToolTelemetryFilter"/> - so it
    /// wraps argument validation, the breakers and the ToolError filter (it reads the corrected <c>IsError</c> and the final
    /// text), while the identity and echo filters have already set the <see cref="ToolCallContext"/> call id and agent id (see
    /// <see cref="BuildCallToolFilterRegistrations"/>). The arguments are copied before <c>next</c> runs because the
    /// validation filter repairs parameter-name case in place. With no <see cref="ToolCallTranscript"/> in the container it
    /// passes straight through; writing never fails or blocks a call.
    /// </para>
    /// </summary>
    private static void AddToolCallTranscriptFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                ToolCallTranscript? transcript = null;
                try
                {
                    transcript = context.Server.Services?.GetService<ToolCallTranscript>();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tool call transcript lookup failed: {ex}");
                }

                return await ToolCallTranscriptRecorder.RecordAsync(
                    transcript, context.Params?.Name, context.Params?.Arguments, () => next(context, cancellationToken));
            }));
    }

    /// <summary>
    /// Registers the loop-latch filter: with <c>--loop-latch</c> on, a tool that failed the same way twice with no other tool
    /// succeeding in between is refused (not executed) until another tool succeeds, per caller agent id. Logic and semantics:
    /// <see cref="LoopLatchFilter"/>, <see cref="ToolLoopLatch"/>. The refusal names only callable tools, resolved from
    /// <see cref="ActiveToolSurface"/> as the orientation breaker does. Off: a pure pass-through.
    /// </summary>
    private static void AddLoopLatchFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                if (!LoopLatchOptions.Enabled)
                {
                    return await next(context, cancellationToken);
                }

                ToolLoopLatch? latch = null;
                Func<string, bool>? isCallable = null;
                try
                {
                    latch = context.Server.Services?.GetService<ToolLoopLatch>();
                    var surface = context.Server.Services?.GetService<ActiveToolSurface>();
                    isCallable = surface is null ? null : surface.IsCallable;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Loop latch lookup failed: {ex}");
                }

                return await LoopLatchFilter.InvokeAsync(
                    latch, ToolCallContext.Current?.AgentId, context.Params?.Name, isCallable, () => next(context, cancellationToken));
            }));
    }

    /// <summary>
    /// Registers the tool-telemetry filter: counts every tool call (and its outcome and duration) on
    /// <see cref="ToolTelemetry"/>. The logic is <see cref="ToolTelemetryRecorder.RecordAsync"/>.
    /// <para>
    /// Must be registered fourth - immediately inside the transcript filter and before everything else - so it wraps
    /// argument validation and the breakers (which refuse without calling next) and the ToolError filter, whose
    /// body-isError sync means it reads the corrected <c>CallToolResult.IsError</c> (see
    /// <see cref="BuildCallToolFilterRegistrations"/>). Because it records only after <c>next</c> returns, a McpServerStatus
    /// call reports itself only from the following call on. With no <see cref="ToolTelemetry"/> in the container it
    /// passes straight through.
    /// </para>
    /// </summary>
    private static void AddToolTelemetryFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                using var activity = ServerActivityTracker.Shared.Enter();

                ToolTelemetry? telemetry = null;
                try
                {
                    telemetry = context.Server.Services?.GetService<ToolTelemetry>();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tool telemetry lookup failed: {ex}");
                }

                return await ToolTelemetryRecorder.RecordAsync(telemetry, context.Params?.Name, () => next(context, cancellationToken));
            }));
    }

    /// <summary>
    /// Appends each parameter-alias note (see <see cref="ToolArgumentValidator.ApplyParameterAliases"/>)
    /// as its own trailing text block, so the model learns the declared parameter name without the
    /// response's first block - which other filters stamp and parse - being altered.
    /// </summary>
    private static void AppendAliasNotes(
        ModelContextProtocol.Protocol.CallToolResult result,
        System.Collections.Generic.IReadOnlyList<string>? notes)
    {
        if (notes is null || notes.Count == 0)
            return;

        result.Content ??= [];
        foreach (var note in notes)
            result.Content.Add(new ModelContextProtocol.Protocol.TextContentBlock { Text = note });
    }

    /// <summary>
    /// Registers the argument pre-flight filter: first silently repairs a case-only parameter
    /// name mismatch (e.g. "filepath" -> "filePath"), then rejects a call whose arguments still
    /// cannot succeed as written, before the SDK's binder ever sees either.
    /// <para>
    /// Covers three dispatch-layer defects that no per-tool fix can reach -> an unknown parameter is
    /// silently discarded (the tool then runs on its defaults and reports <c>success:true</c> with
    /// the wrong result), a missing required parameter surfaces as a raw framework
    /// <c>ArgumentException</c> naming an internal "arguments dictionary", and a parameter name
    /// that is otherwise correct but differs from the declared one only by case is - absent the
    /// normalization pass - indistinguishable from the first defect, forcing an avoidable
    /// round-trip to fix a mistake the server could see and correct outright. See
    /// <see cref="ToolArgumentValidator"/> for the full analysis.
    /// </para>
    /// <para>
    /// Registered before every other call-tool filter, except the agent identity, tool-call echo and telemetry filters (see
    /// <see cref="AddToolCallEchoFilter"/>), so no other filter does work on a call that cannot succeed.
    /// </para>
    /// </summary>
    private static void AddArgumentValidationFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                System.Collections.Generic.IReadOnlyList<string>? aliasNotes = null;
                try
                {
                    // proposal decision 3: a call is validated against the surface it started under; the schema itself is still read live from the tool collection; a throwing read is caught by the outer catch below and skips validation (the call runs unvalidated, as for any other validator exception).
                    var surface = context.Server.Services?.GetService<ToolSurfaceController>()?.Current;

                    ToolArgumentValidator.NormalizeParameterCase(
                        context.Server, context.Params?.Name, context.Params?.Arguments, surface: surface);

                    var enumNotes = ToolArgumentValidator.NormalizeEnumCase(
                        context.Server, context.Params?.Name, context.Params?.Arguments, surface: surface);
                    if (enumNotes is not null)
                        aliasNotes = enumNotes;

                    var aliasRenameNotes = ToolArgumentValidator.ApplyParameterAliases(
                        context.Server, context.Params?.Name, context.Params?.Arguments, surface: surface);
                    if (aliasRenameNotes is not null)
                        aliasNotes = enumNotes is null ? aliasRenameNotes : [.. (enumNotes ?? System.Array.Empty<string>()), .. aliasRenameNotes];

                    var legacySearchNotes = ToolArgumentValidator.ApplyLegacySearchReferencesKind(
                        context.Server, context.Params?.Name, context.Params?.Arguments);
                    if (legacySearchNotes is not null)
                        aliasNotes = aliasNotes is null ? legacySearchNotes : [.. aliasNotes, .. legacySearchNotes];

                    // Value-translating rewrites (grep/Read-shaped arguments) run after the renaming passes above
                    // and before the wrap/coerce passes. The solution root lets a text-mode Search path become a fileGlob.
                    var solutionRoot = context.Server.Services?.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot();
                    var rewriteNotes = ToolArgumentValidator.ApplyArgumentRewrites(
                        context.Server, context.Params?.Name, context.Params?.Arguments, solutionRoot, surface: surface);
                    if (rewriteNotes is not null)
                        aliasNotes = aliasNotes is null ? rewriteNotes : [.. aliasNotes, .. rewriteNotes];

                    var wrapNotes = ToolArgumentValidator.WrapFlatBatchParameters(
                        context.Server, context.Params?.Name, context.Params?.Arguments, surface: surface);
                    if (wrapNotes is not null)
                        aliasNotes = aliasNotes is null ? wrapNotes : [.. aliasNotes, .. wrapNotes];

                    var coerceNotes = ToolArgumentValidator.CoerceStringToStructured(
                        context.Server, context.Params?.Name, context.Params?.Arguments, surface: surface);
                    if (coerceNotes is not null)
                        aliasNotes = aliasNotes is null ? coerceNotes : [.. aliasNotes, .. coerceNotes];

                    var validationSurface = context.Server.Services?.GetService<ActiveToolSurface>();
                    Func<string, bool>? validationIsCallable = validationSurface is null ? null : validationSurface.IsCallable;
                    var validationError = ToolArgumentValidator.Validate(
                        context.Server, context.Params?.Name, context.Params?.Arguments, validationIsCallable, surface: surface);

                    if (validationError is not null)
                    {
                        var rejection = new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = validationError }],
                            IsError = true,
                        };
                        // The notes describe corrections made to the arguments, but nothing ran: say so before them.
                        AppendAliasNotes(rejection, aliasNotes is { Count: > 0 }
                            ? ["The notes below describe corrections already made to your call. The call did not run because of the unknown parameter(s) above; resend it with the corrected names.", .. aliasNotes]
                            : aliasNotes);
                        return rejection;
                    }
                }
                catch (Exception ex)
                {
                    // A guardrail must never become the thing that blocks a valid call: if
                    // validation itself throws, fall through and let the call run as it would have.
                    Debug.WriteLine($"Tool argument validation filter failed: {ex}");
                }

                var result = await next(context, cancellationToken);

                // A separate trailing block, so the first block (which the echo filter stamps and
                // the domain-failure filter parses as JSON) is left untouched.
                AppendAliasNotes(result, aliasNotes);
                return result;
            }));
    }

    private static void CreateFreshServerMessage()
    {
    }
}

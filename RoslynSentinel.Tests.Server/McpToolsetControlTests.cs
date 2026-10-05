// Coverage for proposal_reduce_tool_schema_token_cost.md Step 4b, slice 4b-2: McpToolsetControl switches
// on-demand toolsets on and off at runtime, only in claude-lean. Three layers:
//   1. ToolsetService unit tests against a bare McpServerOptions (no host).
//   2. Schema parity: a tool the service builds is identical to the same tool registered at startup.
//   3. In-process MCP host (same shape as ClaudeLeanModeTests): list_changed really reaches a client,
//      an enabled tool is callable, dependencies of on-demand classes resolve, other modes lack the tool.
// [NonParallelizable]: the e2e cases share process-wide statics (ToolArgumentValidator.SchemaCache,
// SchemaOptions), and one case reads them while another host is starting.
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable]
public class McpToolsetControlTests
{
    private const string Reason = "toolset control test call";

    private static readonly System.Reflection.Assembly[] ToolAssemblies =
        [typeof(ServerStatusTools).Assembly, typeof(AdvancedRefactoringTools).Assembly];

    // ---- helpers: bare service ----

    private static (ToolsetService Service, McpServerPrimitiveCollection<McpServerTool> Collection) CreateService(bool withCollection = true)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        var options = Options.Create(new McpServerOptions { ToolCollection = withCollection ? collection : null });
        var service = new ToolsetService(
            options,
            new ServiceCollection().BuildServiceProvider(),
            ToolClassRegistry.ClaudeLeanOnDemandToolClasses,
            () => ToolAssemblies);
        return (service, collection);
    }

    private static string[] AllSetToolNames(ToolSetName set) => ToolsetCatalog.GetToolNames(set).ToArray();

    // ---- ToolsetService ----

    [TestCase(ToolSetName.declarations)]
    [TestCase(ToolSetName.moveExtract)]
    [TestCase(ToolSetName.projectAdmin)]
    public void Enable_AddsTheWholeSet_AndAccountsForEveryName(ToolSetName set)
    {
        var (service, collection) = CreateService();

        var change = service.SetEnabled(set, enabled: true);

        Assert.That(change.Enabled, Is.True);
        Assert.That(change.Changed, Is.True);
        Assert.That(change.UnavailableTools, Is.Empty, "with both tool assemblies loaded every catalog name must resolve");
        Assert.That(change.AddedTools, Is.EquivalentTo(AllSetToolNames(set)));
        Assert.That(collection.PrimitiveNames, Is.EquivalentTo(AllSetToolNames(set)));
        Assert.That(change.ActiveTools, Is.EquivalentTo(AllSetToolNames(set)));
        Assert.That(service.EnabledToolSets, Is.EqualTo(new[] { set }));
    }

    [Test]
    public void Enable_IsIdempotent_AndRaisesChangedOnce()
    {
        var (service, collection) = CreateService();
        int changed = 0;
        collection.Changed += (_, _) => changed++;

        service.SetEnabled(ToolSetName.declarations, true);
        int countAfterFirst = collection.Count;
        var second = service.SetEnabled(ToolSetName.declarations, true);

        Assert.That(second.Changed, Is.False);
        Assert.That(second.AddedTools, Is.Empty);
        Assert.That(collection.Count, Is.EqualTo(countAfterFirst));
        Assert.That(changed, Is.EqualTo(1), "a 9-tool enable must be one list_changed, not nine; the repeat must be none");
    }

    [Test]
    public void Disable_RemovesExactlyWhatWasAdded_AndIsIdempotent()
    {
        var (service, collection) = CreateService();
        service.SetEnabled(ToolSetName.declarations, true);
        service.SetEnabled(ToolSetName.projectAdmin, true);
        int changed = 0;
        collection.Changed += (_, _) => changed++;

        var off = service.SetEnabled(ToolSetName.declarations, false);
        var offAgain = service.SetEnabled(ToolSetName.declarations, false);

        Assert.That(off.Changed, Is.True);
        Assert.That(off.Enabled, Is.False);
        Assert.That(off.RemovedTools, Is.EquivalentTo(AllSetToolNames(ToolSetName.declarations)));
        Assert.That(collection.PrimitiveNames, Is.EquivalentTo(AllSetToolNames(ToolSetName.projectAdmin)));
        Assert.That(offAgain.Changed, Is.False);
        Assert.That(offAgain.RemovedTools, Is.Empty);
        Assert.That(changed, Is.EqualTo(1));
        Assert.That(service.EnabledToolSets, Is.EqualTo(new[] { ToolSetName.projectAdmin }));
    }

    [Test]
    public void Disable_WhenNeverEnabled_IsANoOp()
    {
        var (service, collection) = CreateService();
        int changed = 0;
        collection.Changed += (_, _) => changed++;

        var change = service.SetEnabled(ToolSetName.moveExtract, false);

        Assert.That(change.Changed, Is.False);
        Assert.That(change.Enabled, Is.False);
        Assert.That(changed, Is.Zero);
    }

    [Test]
    public void Enable_NeverReplacesAnExistingTool_AndDisableLeavesItAlone()
    {
        var (service, collection) = CreateService();
        var startupTool = McpServerTool.Create((string x) => x, new McpServerToolCreateOptions { Name = "ModifyEnum" });
        collection.Add(startupTool);

        var change = service.SetEnabled(ToolSetName.declarations, true);
        service.SetEnabled(ToolSetName.declarations, false);

        Assert.That(change.UnavailableTools, Does.Contain("ModifyEnum"));
        Assert.That(change.AddedTools, Does.Not.Contain("ModifyEnum"));
        Assert.That(collection.TryGetPrimitive("ModifyEnum", out var remaining), Is.True);
        Assert.That(remaining, Is.SameAs(startupTool));
    }

    [Test]
    public void SetEnabled_UnknownToolSet_Throws_AndChangesNothing()
    {
        var (service, collection) = CreateService();

        Assert.That(() => service.SetEnabled((ToolSetName)99, true), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(collection, Is.Empty);
        Assert.That(service.EnabledToolSets, Is.Empty);
    }

    [Test]
    public void SetEnabled_WithoutAToolCollection_ThrowsActionableError()
    {
        var (service, _) = CreateService(withCollection: false);

        Assert.That(() => service.SetEnabled(ToolSetName.declarations, true),
            Throws.InvalidOperationException.With.Message.Contains("tool collection"));
    }

    [Test]
    public void Enable_ToolFromAnUnsupportedClass_IsReportedUnavailable()
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        var service = new ToolsetService(
            Options.Create(new McpServerOptions { ToolCollection = collection }),
            new ServiceCollection().BuildServiceProvider(),
            new HashSet<string>(StringComparer.Ordinal),
            () => ToolAssemblies);

        var change = service.SetEnabled(ToolSetName.declarations, true);

        Assert.That(change.AddedTools, Is.Empty);
        Assert.That(change.UnavailableTools, Is.EquivalentTo(AllSetToolNames(ToolSetName.declarations)));
        Assert.That(collection, Is.Empty);
    }

    [Test]
    public void ConcurrentToggling_KeepsStateAndCollectionConsistent()
    {
        var (service, collection) = CreateService();
        var sets = Enum.GetValues<ToolSetName>();

        Parallel.For(0, 240, i =>
        {
            service.SetEnabled(sets[i % sets.Length], enabled: (i / sets.Length) % 2 == 0);
        });

        // Whatever interleaving happened, the collection holds exactly the tools of the sets reported on.
        var expected = service.EnabledToolSets.SelectMany(AllSetToolNames).ToArray();
        Assert.That(collection.PrimitiveNames, Is.EquivalentTo(expected));
        Assert.That(service.DynamicToolNames, Is.EquivalentTo(expected));

        foreach (var set in sets)
        {
            service.SetEnabled(set, false);
        }

        Assert.That(collection, Is.Empty);
    }

    [Test]
    public void Catalog_SetsAreDisjoint_AndExcludeCoreTools()
    {
        var all = Enum.GetValues<ToolSetName>().SelectMany(AllSetToolNames).ToArray();

        Assert.That(all, Is.Unique);
        Assert.That(all.Intersect(ToolClassRegistry.ClaudeLeanToolNames), Is.Empty);
        Assert.That(ToolsetCatalog.AllToolNames, Has.Count.EqualTo(all.Length));
        Assert.That(all, Has.Length.EqualTo(39));
    }

    [Test]
    public void Catalog_EveryToolIsDeclared_AndTheDescriptionNamesEverySetAndTool()
    {
        var declared = McpToolSchemaPatcher.DiscoverToolMethods(ToolAssemblies).Select(t => t.ToolName).ToHashSet(StringComparer.Ordinal);
        Assert.That(declared, Is.SupersetOf(ToolsetCatalog.AllToolNames));

        var tool = typeof(ToolsetControlTools).GetMethod(nameof(ToolsetControlTools.McpToolsetControl))!;
        string description = ((System.ComponentModel.DescriptionAttribute)Attribute.GetCustomAttribute(tool, typeof(System.ComponentModel.DescriptionAttribute))!).Description;
        foreach (var set in Enum.GetValues<ToolSetName>())
        {
            Assert.That(description, Does.Contain(set.ToString()));
        }

        // Extract*/Inline*/Introduce* are spelled as wildcards in the description; everything else by exact name.
        foreach (var name in ToolsetCatalog.AllToolNames.Where(n => !n.StartsWith("Extract") && !n.StartsWith("Inline") && !n.StartsWith("Introduce")))
        {
            Assert.That(description, Does.Contain(name), $"description should list {name}");
        }
    }

    // ---- schema parity with startup registration ----

    [Test]
    public void DynamicTools_AreIdenticalToTheSameToolsRegisteredAtStartup()
    {
        // --mode claude registers every set tool at startup through WithSentinelTools; the service must
        // produce byte-identical protocol tools (schema patching, x-tags, limits, lean profile).
        using var host = BuildHost(advanced: true, mode: "Claude", includeTools: null);
        var startup = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        var (service, collection) = CreateService();
        foreach (var set in Enum.GetValues<ToolSetName>())
        {
            service.SetEnabled(set, true);
        }

        // Declaration, ParameterEdit and SemanticFindReplace are the catalog tools no startup mode registers (they exist only as on-demand
        // claude-lean tools, see DeclarationToolTests, ParameterEditToolTests and SemanticFindReplaceToolTests), so there is no startup
        // instance to compare them with.
        foreach (var name in ToolsetCatalog.AllToolNames.Where(n => n is not ("Declaration" or "ParameterEdit" or "SemanticFindReplace")))
        {
            Assert.That(startup.TryGetPrimitive(name, out var expected), Is.True, $"{name} should be a startup tool in --mode claude");
            Assert.That(collection.TryGetPrimitive(name, out var actual), Is.True);
            Assert.That(Json(actual!), Is.EqualTo(Json(expected!)), $"{name} differs from its startup registration");
        }
    }

    private static string Json(McpServerTool tool) => JsonSerializer.Serialize(tool.ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions);

    // ---- in-process host ----

    private static IHost BuildHost(bool advanced, string mode, string? includeTools, Pipe? clientToServer = null, Pipe? serverToClient = null)
    {
        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mode };
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includeTools is not null)
        {
            include.Add(includeTools);
        }

        var services = new ServiceCollection();
        if (advanced)
        {
            services.AddRoslynSentinelEnginesAdvanced();
        }
        else
        {
            services.AddRoslynSentinelEnginesBasic();
        }

        var mcpBuilder = services.AddMcpServer();
        if (clientToServer is not null && serverToClient is not null)
        {
            mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        }

        if (advanced)
        {
            mcpBuilder.AddRoslynSentinelToolsAdvanced(services, activeModes, include);
        }
        else
        {
            mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, include);
        }

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        return hostBuilder.Build();
    }

    private sealed class LiveServer : IAsyncDisposable
    {
        private int _listChanged;
        private readonly SemaphoreSlim _signal = new(0);
        private IAsyncDisposable? _registration;

        public required IHost Host { get; init; }
        public required McpClient Client { get; init; }
        public int ListChangedCount => Volatile.Read(ref _listChanged);

        /// <summary>
        /// The initialize-handshake protocol revision. Clients on it get a session-wide list_changed broadcast;
        /// the SDK client's own default is 2026-07-28, where the server only notifies subscriptions/listen
        /// subscribers (see Lean_ClientOn20260728_GetsListChangedOnlyOverASubscription).
        /// </summary>
        public const string LegacyProtocol = "2025-11-25";

        public static async Task<LiveServer> StartAsync(bool advanced, string mode, string? includeTools = null, string? protocolVersion = LegacyProtocol)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var host = BuildHost(advanced, mode, includeTools, clientToServer, serverToClient);
            _ = host.RunAsync();

            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);
            var client = await McpClient.CreateAsync(
                transport,
                clientOptions: protocolVersion is null ? null : new McpClientOptions { ProtocolVersion = protocolVersion },
                cancellationToken: TestContext.CurrentContext.CancellationToken);

            var live = new LiveServer { Host = host, Client = client };
            live._registration = client.RegisterNotificationHandler(
                NotificationMethods.ToolListChangedNotification,
                (_, _) =>
                {
                    Interlocked.Increment(ref live._listChanged);
                    live._signal.Release();
                    return default;
                });
            return live;
        }

        public Task<bool> WaitForListChangedAsync(TimeSpan timeout) => _signal.WaitAsync(timeout);

        public async Task<string[]> ToolNamesAsync()
        {
            var tools = await Client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
            return tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        public async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> args) =>
            await Client.CallToolAsync(tool, args, cancellationToken: TestContext.CurrentContext.CancellationToken);

        public Task<CallToolResult> ToggleAsync(string toolSet, bool enabled) =>
            CallAsync("McpToolsetControl", new() { ["reason"] = Reason, ["toolSet"] = toolSet, ["enabled"] = enabled });

        public static string Text(CallToolResult result) =>
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

        public async ValueTask DisposeAsync()
        {
            if (_registration is not null)
            {
                await _registration.DisposeAsync();
            }

            await Client.DisposeAsync();
            await Host.StopAsync();
            Host.Dispose();
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Lean_AdvertisesListChanged_AndEnableThenDisableEachSendOneNotification(bool advanced)
    {
        await using var live = await LiveServer.StartAsync(advanced, "claude-lean");
        Assert.That(live.Client.ServerCapabilities.Tools?.ListChanged, Is.True, "tools capability must advertise listChanged");
        var before = await live.ToolNamesAsync();
        Assert.That(before, Does.Contain("McpToolsetControl"));

        var on = await live.ToggleAsync("projectAdmin", enabled: true);
        Assert.That(on.IsError, Is.Not.True, LiveServer.Text(on));
        Assert.That(await live.WaitForListChangedAsync(TimeSpan.FromSeconds(10)), Is.True, $"no tools/list_changed after enabling (negotiated protocol {live.Client.NegotiatedProtocolVersion})");
        await Task.Delay(300);
        Assert.That(live.ListChangedCount, Is.EqualTo(1), "one notification for the whole set");

        var during = await live.ToolNamesAsync();
        Assert.That(during, Is.SupersetOf(before));
        Assert.That(during, Does.Contain("IsSessionHalted"));
        Assert.That(during, Does.Contain("GetWorkspaceHealth"));

        var off = await live.ToggleAsync("projectAdmin", enabled: false);
        Assert.That(off.IsError, Is.Not.True, LiveServer.Text(off));
        Assert.That(await live.WaitForListChangedAsync(TimeSpan.FromSeconds(10)), Is.True, "no tools/list_changed after disabling");
        Assert.That(await live.ToolNamesAsync(), Is.EqualTo(before));

        var again = await live.ToggleAsync("projectAdmin", enabled: false);
        await Task.Delay(300);
        Assert.That(LiveServer.Text(again), Does.Contain("already off"));
        Assert.That(live.ListChangedCount, Is.EqualTo(2), "a no-op must not notify");
    }

    [Test]
    public async Task Lean_ClientOn20260728_GetsListChangedOnlyOverASubscription()
    {
        // A client on the 2026-07-28 revision is notified only through a subscriptions/listen stream it
        // opened; without one the server must not broadcast. So McpToolsetControl still works there (the tools are
        // callable and a re-list shows them), and a client that subscribes is told.
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean", protocolVersion: null);
        Assert.That(live.Client.NegotiatedProtocolVersion, Is.EqualTo("2026-07-28"));

        await live.ToggleAsync("declarations", enabled: true);
        await Task.Delay(500);
        Assert.That(live.ListChangedCount, Is.Zero, "no subscription yet, so no notification");
        Assert.That(await live.ToolNamesAsync(), Does.Contain("Declaration"), "a re-list sees the new tools");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        var listen = live.Client.SendRequestAsync(
            new JsonRpcRequest
            {
                Method = RequestMethods.SubscriptionsListen,
                Params = System.Text.Json.Nodes.JsonNode.Parse("""{"notifications":{"toolsListChanged":true}}"""),
            },
            cts.Token);
        await Task.Delay(500);

        await live.ToggleAsync("declarations", enabled: false);
        bool notified = await live.WaitForListChangedAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await listen;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ModelContextProtocol.McpException)
        {
            // The listen request is long-lived; cancelling it ends the test's subscription.
        }

        Assert.That(notified, Is.True, "a subscribed 2026-07-28 client must receive tools/list_changed");
    }

    [Test]
    public async Task EnabledTool_IsCallable_WithoutRelisting()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");

        // IsSessionHalted lives in AdminTools (a lean class, tool not allow-listed); GetWorkspaceHealth lives in a
        // class whose dependencies are registered for dynamic use only. Neither was listed before the enable.
        await live.ToggleAsync("projectAdmin", enabled: true);

        var halted = await live.CallAsync("IsSessionHalted", new() { ["reason"] = Reason });
        Assert.That(halted.IsError, Is.Not.True, LiveServer.Text(halted));

        var health = await live.CallAsync("GetWorkspaceHealth", new() { ["reason"] = Reason });
        Assert.That(LiveServer.Text(health), Does.Not.Contain("Unable to resolve"), "dependencies of on-demand classes must be registered");
        Assert.That(LiveServer.Text(health), Does.Not.Contain("No service for type"));
    }

    [Test]
    public async Task DisabledTool_IsNotCallable()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");
        await live.ToggleAsync("projectAdmin", enabled: true);
        await live.ToggleAsync("projectAdmin", enabled: false);

        var call = await live.CallAsync("IsSessionHalted", new() { ["reason"] = Reason });

        Assert.That(call.IsError, Is.True);
    }

    [Test]
    public async Task McpServerStatus_PointsInactiveToolsAtTheToolset_AndReportsEnabledOnesActive()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");

        var inactive = await live.CallAsync("McpServerStatus", new() { ["toolListing"] = "inactive", ["toolNameFilter"] = "Declaration", ["reason"] = Reason });
        Assert.That(LiveServer.Text(inactive), Does.Contain("McpToolsetControl(toolSet: declarations, enabled: true)"));

        await live.ToggleAsync("declarations", enabled: true);

        var after = await live.CallAsync("McpServerStatus", new() { ["toolListing"] = "inactive", ["toolNameFilter"] = "Declaration", ["reason"] = Reason });
        Assert.That(LiveServer.Text(after), Does.Not.Contain("McpToolsetControl(toolSet: declarations"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void OnDemandClasses_AreConstructibleFromTheLeanContainer(bool advanced)
    {
        using var host = BuildHost(advanced, "claude-lean", includeTools: null);
        var candidates = McpToolSchemaPatcher.DiscoverToolMethods(ToolAssemblies)
            .Where(t => ToolsetCatalog.AllToolNames.Contains(t.ToolName) && ToolClassRegistry.ClaudeLeanOnDemandToolClasses.Contains(t.ToolType.Name))
            .Where(t => advanced || t.ToolType != typeof(AdvancedRefactoringTools))
            .Select(t => t.ToolType)
            .Distinct()
            .ToArray();

        Assert.That(candidates, Is.Not.Empty);
        foreach (var type in candidates)
        {
            Assert.That(() => ActivatorUtilities.CreateInstance(host.Services, type), Throws.Nothing, $"{type.Name} must be constructible");
        }
    }

    [Test]
    public void LeanHost_StartsWithNoDynamicToolsAndTheExactCoreSurface()
    {
        using var host = BuildHost(advanced: true, "claude-lean", includeTools: null);

        var names = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!.PrimitiveNames;
        Assert.That(names, Has.Count.EqualTo(26));
        Assert.That(host.Services.GetRequiredService<ToolsetService>().DynamicToolNames, Is.Empty);
    }

    [TestCase(true, "Claude")]
    [TestCase(true, "Workspace")]
    [TestCase(true, "Refactor")]
    [TestCase(true, "Admin")]
    [TestCase(false, "Claude")]
    [TestCase(false, "Workspace")]
    public async Task OtherModes_DoNotExposeTheToolsetTool(bool advanced, string mode)
    {
        await using var live = await LiveServer.StartAsync(advanced, mode);

        Assert.That(await live.ToolNamesAsync(), Does.Not.Contain("McpToolsetControl"));
    }

    [Test]
    public async Task IncludeTools_CannotSmuggleTheToolsetToolIntoAnotherMode()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "Workspace", includeTools: "ToolsetControlTools");

        Assert.That(await live.ToolNamesAsync(), Does.Not.Contain("McpToolsetControl"));
    }

    [Test]
    public void OtherModes_DoNotRegisterAToolsetService()
    {
        using var host = BuildHost(advanced: true, "Claude", includeTools: null);

        Assert.That(host.Services.GetService<ToolsetService>(), Is.Null);
    }

    [Test]
    public async Task McpToolsetControl_RejectsAnUnknownToolSet()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");

        var call = await live.ToggleAsync("notASet", enabled: true);

        Assert.That(call.IsError, Is.True);
        Assert.That(live.ListChangedCount, Is.Zero);
    }

    // ---- Declaration (step 4a-1): merges ModifyModifier, ChangeAccessibility, ModifyAttribute and ModifyBaseType; on-demand in claude-lean only ----

    private static readonly string[] DeclarationOriginals = ["ModifyModifier", "ChangeAccessibility", "ModifyAttribute", "ModifyBaseType"];

    [Test]
    public async Task Declaration_IsAbsentFromLeanUntilTheDeclarationsToolsetIsEnabled_AndReplacesTheFourOriginalsThere()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");

        var before = await live.ToolNamesAsync();
        Assert.That(before, Does.Not.Contain("Declaration"));
        Assert.That(before, Does.Not.Contain("ModifyModifier"));

        await live.ToggleAsync("declarations", enabled: true);
        var on = await live.ToolNamesAsync();
        Assert.That(on, Does.Contain("Declaration"));
        foreach (var original in DeclarationOriginals)
        {
            Assert.That(on, Does.Not.Contain(original), $"Declaration replaces {original} in claude-lean");
        }

        await live.ToggleAsync("declarations", enabled: false);
        Assert.That(await live.ToolNamesAsync(), Does.Not.Contain("Declaration"));
    }

    [Test]
    public async Task Declaration_EnabledOnLean_IsCallable_AndRejectsAWrongParamSubsetByName()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");
        await live.ToggleAsync("declarations", enabled: true);

        var call = await live.CallAsync("Declaration", new() { ["reason"] = Reason, ["operation"] = "accessibility", ["filePath"] = "X.cs", ["targetName"] = "M" });

        string text = LiveServer.Text(call);
        Assert.That(text, Does.Contain("requires 'filePath', 'targetName' and 'accessibility'"), "the class must be constructible from the lean container and the tool callable");
        Assert.That(text, Does.Not.Contain("Unable to resolve"));
        Assert.That(text, Does.Not.Contain("No service for type"));
    }

    [TestCase(true, "Claude")]
    [TestCase(true, "Workspace")]
    [TestCase(true, "Refactor")]
    [TestCase(false, "Claude")]
    [TestCase(false, "Refactor")]
    public async Task OtherModes_DoNotExposeDeclaration_AndKeepTheOriginalTools(bool advanced, string mode)
    {
        await using var live = await LiveServer.StartAsync(advanced, mode);

        var names = await live.ToolNamesAsync();
        Assert.That(names, Does.Not.Contain("Declaration"));
        if (mode is "Claude" or "Refactor")
        {
            Assert.That(names, Is.SupersetOf(DeclarationOriginals));
        }
    }

    [Test]
    public async Task IncludeTools_CannotSmuggleDeclarationIntoAnotherMode()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "Claude", includeTools: "DeclarationTools");

        Assert.That(await live.ToolNamesAsync(), Does.Not.Contain("Declaration"));
    }

    [Test]
    public void Declaration_EmittedSchema_IsSmallerThanTheFourToolsItMerges()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        int Size(Type type, string method) =>
            Json(McpToolSchemaPatcher.CreateSentinelTool(type.GetMethod(method)!, type, services)).Length;

        int declaration = Size(typeof(DeclarationTools), nameof(DeclarationTools.Declaration));
        int modifier = Size(typeof(RefactoringStructuralTools), nameof(RefactoringStructuralTools.ModifyModifier));
        int accessibility = Size(typeof(RefactoringSignatureTools), nameof(RefactoringSignatureTools.ChangeAccessibility));
        int attribute = Size(typeof(RefactoringStructuralTools), nameof(RefactoringStructuralTools.ModifyAttribute));
        int baseType = Size(typeof(RefactoringStructuralTools), nameof(RefactoringStructuralTools.ModifyBaseType));
        int originals = modifier + accessibility + attribute + baseType;
        TestContext.Out.WriteLine($"Emitted schema chars: Declaration={declaration}, ModifyModifier={modifier}, ChangeAccessibility={accessibility}, ModifyAttribute={attribute}, ModifyBaseType={baseType}, originals={originals}");

        Assert.That(declaration, Is.LessThan(originals));
    }

    // ---- ParameterEdit (step 4a-2): merges MethodSignature and ConstructorParameter; on-demand in claude-lean only ----

    private static readonly string[] ParameterEditOriginals = ["MethodSignature", "ConstructorParameter"];

    [Test]
    public async Task ParameterEdit_IsAbsentFromLeanUntilTheDeclarationsToolsetIsEnabled_AndReplacesTheTwoOriginalsThere()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");

        var before = await live.ToolNamesAsync();
        Assert.That(before, Does.Not.Contain("ParameterEdit"));
        Assert.That(before, Does.Not.Contain("MethodSignature"));

        await live.ToggleAsync("declarations", enabled: true);
        var on = await live.ToolNamesAsync();
        Assert.That(on, Does.Contain("ParameterEdit"));
        foreach (var original in ParameterEditOriginals)
        {
            Assert.That(on, Does.Not.Contain(original), $"ParameterEdit replaces {original} in claude-lean");
        }

        await live.ToggleAsync("declarations", enabled: false);
        Assert.That(await live.ToolNamesAsync(), Does.Not.Contain("ParameterEdit"));
    }

    [Test]
    public async Task ParameterEdit_EnabledOnLean_IsCallable_AndRejectsAWrongParamSubsetByName()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "claude-lean");
        await live.ToggleAsync("declarations", enabled: true);

        var call = await live.CallAsync("ParameterEdit", new() { ["reason"] = Reason, ["operation"] = "method", ["filePath"] = "X.cs", ["action"] = "add", ["methodName"] = "M" });

        string text = LiveServer.Text(call);
        Assert.That(text, Does.Contain("operation 'method', action 'add' requires 'paramName' and 'paramType'"), "the class must be constructible from the lean container and the tool callable");
        Assert.That(text, Does.Not.Contain("Unable to resolve"));
        Assert.That(text, Does.Not.Contain("No service for type"));
    }

    [TestCase(true, "Claude")]
    [TestCase(true, "Workspace")]
    [TestCase(true, "Refactor")]
    [TestCase(false, "Claude")]
    [TestCase(false, "Refactor")]
    public async Task OtherModes_DoNotExposeParameterEdit_AndKeepTheOriginalTools(bool advanced, string mode)
    {
        await using var live = await LiveServer.StartAsync(advanced, mode);

        var names = await live.ToolNamesAsync();
        Assert.That(names, Does.Not.Contain("ParameterEdit"));
        if (mode is "Claude" or "Refactor")
        {
            Assert.That(names, Is.SupersetOf(ParameterEditOriginals));
        }
    }

    [Test]
    public async Task IncludeTools_CannotSmuggleParameterEditIntoAnotherMode()
    {
        await using var live = await LiveServer.StartAsync(advanced: true, "Claude", includeTools: "ParameterEditTools");

        Assert.That(await live.ToolNamesAsync(), Does.Not.Contain("ParameterEdit"));
    }

    [Test]
    public void ParameterEdit_EmittedSchema_IsSmallerThanTheTwoToolsItMerges()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        int Size(Type type, string method) =>
            Json(McpToolSchemaPatcher.CreateSentinelTool(type.GetMethod(method)!, type, services)).Length;

        int parameterEdit = Size(typeof(ParameterEditTools), nameof(ParameterEditTools.ParameterEdit));
        int methodSignature = Size(typeof(RefactoringSignatureTools), nameof(RefactoringSignatureTools.MethodSignature));
        int constructorParameter = Size(typeof(RefactoringSignatureTools), nameof(RefactoringSignatureTools.ConstructorParameter));
        int originals = methodSignature + constructorParameter;
        TestContext.Out.WriteLine($"Emitted schema chars: ParameterEdit={parameterEdit}, MethodSignature={methodSignature}, ConstructorParameter={constructorParameter}, originals={originals}");

        Assert.That(parameterEdit, Is.LessThan(originals));
    }
}

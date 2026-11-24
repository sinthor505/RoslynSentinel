// Coverage for ServerStartupHelpers.ApplyStartupToolsets with a per-tab toolset file: the saved toolsets
// replace the --toolsets argument at startup, and a later toolset change is written back to the file.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable]
public class StartupToolsetRestoreTests
{
    private static readonly System.Reflection.Assembly[] ToolAssemblies =
        [typeof(ServerStatusTools).Assembly, typeof(AdvancedRefactoringTools).Assembly];

    private string _dir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "startup-toolset-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Test]
    public void StateFile_ReplacesToolsetsArg()
    {
        new TabToolsetStateStore(_dir, null).Save(new[] { ToolSetName.git });
        var (service, store, provider) = Build();

        ServerStartupHelpers.ApplyStartupToolsets(provider, new[] { "--toolsets=declarations" });

        Assert.That(service.EnabledToolSets, Does.Contain(ToolSetName.git));
        Assert.That(service.EnabledToolSets, Does.Not.Contain(ToolSetName.declarations));
        Assert.That(store.RestoredAtStartup, Is.True);
        Assert.That(store.ConfiguredToolSets, Is.EqualTo(new[] { ToolSetName.declarations }));
    }

    [Test]
    public void NoStateFile_UsesToolsetsArg()
    {
        var (service, store, provider) = Build();

        ServerStartupHelpers.ApplyStartupToolsets(provider, new[] { "--toolsets=declarations" });

        Assert.That(service.EnabledToolSets, Is.EquivalentTo(new[] { ToolSetName.declarations }));
        Assert.That(store.RestoredAtStartup, Is.False);
    }

    [Test]
    public void EnableAfterStartup_WritesStateFile()
    {
        var (service, _, provider) = Build();

        ServerStartupHelpers.ApplyStartupToolsets(provider, Array.Empty<string>());
        service.SetEnabled(ToolSetName.git, enabled: true);

        var loaded = new TabToolsetStateStore(_dir, null).Load();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.EnabledToolSets, Does.Contain("git"));
    }

    [Test]
    public void UnknownSavedName_WarnsAndSkips()
    {
        File.WriteAllText(
            Path.Combine(_dir, TabToolsetStateStore.FileName),
            "{\"enabledToolSets\":[\"git\",\"bogusSet\"],\"sessionId\":null,\"savedUtc\":\"2026-11-15T00:00:00Z\"}");
        var (service, _, provider) = Build();

        var warnings = ServerStartupHelpers.ApplyStartupToolsets(provider, Array.Empty<string>());

        Assert.That(string.Join("\n", warnings), Does.Contain("bogusSet"));
        Assert.That(service.EnabledToolSets, Does.Contain(ToolSetName.git));
    }

    private (ToolsetService Service, TabToolsetStateStore Store, IServiceProvider Provider) Build()
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        var options = Options.Create(new McpServerOptions { ToolCollection = collection });
        var service = new ToolsetService(
            new ToolSurfaceController(options),
            new ServiceCollection().BuildServiceProvider(),
            ToolClassRegistry.ClaudeLeanOnDemandToolClasses,
            () => ToolAssemblies);
        var store = new TabToolsetStateStore(_dir, null);

        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton(store);
        return (service, store, services.BuildServiceProvider());
    }
}

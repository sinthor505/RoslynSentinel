using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Pins the startup tool population that the options system applies to every McpServerOptions
/// (stateless HTTP builds one per request): request and method order, allow-list filtering,
/// schema parity with CreateSentinelTool, shared tool instances across options, an existing
/// collection kept with its entries first, an empty collection created when there are no tools,
/// and building without a ToolSurfaceController registered.
/// </summary>
[TestFixture]
public class ToolSurfaceStartupPopulationTests
{
    private const BindingFlags AllMethods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static ToolSurfaceStartupRequest[] StartupRequests() =>
    [
        new(new[] { typeof(ServerStatusTools), typeof(SymbolRelationshipTools) }),
        new(new[] { typeof(WorkspaceTools) }),
    ];

    // Reflection mirror of the expected order: request by request, type by type, each type's
    // [McpServerTool] methods in GetMethods order, named by the attribute Name or the method name.
    private static List<(MethodInfo Method, Type ToolType, string Name)> ExpectedMethods(IEnumerable<ToolSurfaceStartupRequest> requests)
    {
        var expected = new List<(MethodInfo Method, Type ToolType, string Name)>();
        foreach (var request in requests)
        {
            foreach (var toolType in request.ToolTypes)
            {
                foreach (var method in toolType.GetMethods(AllMethods))
                {
                    var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (attribute is null)
                    {
                        continue;
                    }

                    expected.Add((method, toolType, attribute.Name ?? method.Name));
                }
            }
        }

        return expected;
    }

    [Test]
    public void BuildStartupTools_FollowsRequestTypeMethodOrder_AndTheAllowList()
    {
        var requests = StartupRequests();
        var expected = ExpectedMethods(requests);
        var services = new ServiceCollection().BuildServiceProvider();

        var tools = ToolSurfaceController.BuildStartupTools(requests, null, services);

        Assert.That(tools.Select(t => t.ProtocolTool.Name).ToList(), Is.EqualTo(expected.Select(e => e.Name).ToList()));
        for (var i = 0; i < expected.Count; i++)
        {
            var sentinel = McpToolSchemaPatcher.CreateSentinelTool(expected[i].Method, expected[i].ToolType, services);
            Assert.That(
                JsonSerializer.Serialize(tools[i].ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                Is.EqualTo(JsonSerializer.Serialize(sentinel.ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions)),
                $"canonical JSON of tool {expected[i].Name} differs from CreateSentinelTool");
        }

        // Keep every other name so the filter is non-trivial and the relative order is checked.
        var allowed = expected.Select(e => e.Name).Where((_, k) => k % 2 == 0).ToHashSet(StringComparer.Ordinal);
        var expectedFiltered = expected.Select(e => e.Name).Where(allowed.Contains).ToList();
        Assert.That(expectedFiltered.Count, Is.LessThan(expected.Count));

        var filtered = ToolSurfaceController.BuildStartupTools(requests, new ToolAllowList(allowed), services);

        Assert.That(filtered.Select(t => t.ProtocolTool.Name).ToList(), Is.EqualTo(expectedFiltered));
    }

    [Test]
    public void PostConfigure_TwoOptionsInstances_GetTheSameToolInstances()
    {
        var population = new ToolSurfaceStartupPopulation(new ServiceCollection().BuildServiceProvider(), StartupRequests());
        var first = new McpServerOptions();
        var second = new McpServerOptions();

        population.PostConfigure(null, first);
        population.PostConfigure(null, second);

        var firstTools = first.ToolCollection!.ToList();
        var secondTools = second.ToolCollection!.ToList();
        var built = population.Tools;
        Assert.That(firstTools.Count, Is.EqualTo(built.Count));
        Assert.That(secondTools.Count, Is.EqualTo(built.Count));
        for (var k = 0; k < built.Count; k++)
        {
            Assert.That(ReferenceEquals(firstTools[k], built[k]), Is.True, $"first options entry {k} is not the population tool");
            Assert.That(ReferenceEquals(secondTools[k], built[k]), Is.True, $"second options entry {k} is not the population tool");
        }

        Assert.That(ReferenceEquals(population.Tools, population.Tools), Is.True, "Tools must return the cached list instance");
    }

    [Test]
    public void PostConfigure_KeepsAnExistingCollection_AndItsEntriesFirst()
    {
        var population = new ToolSurfaceStartupPopulation(new ServiceCollection().BuildServiceProvider(), StartupRequests());
        var builtNames = population.Tools.Select(t => t.ProtocolTool.Name).ToList();
        var duplicateName = builtNames[0];
        const string uniqueName = "ToolSurfaceStartupPopulationProbe";

        var probeFirst = McpServerTool.Create((string x) => x, new McpServerToolCreateOptions { Name = duplicateName });
        var probeUnique = McpServerTool.Create((string x) => x, new McpServerToolCreateOptions { Name = uniqueName });
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        collection.TryAdd(probeFirst);
        collection.TryAdd(probeUnique);
        var options = new McpServerOptions { ToolCollection = collection };

        population.PostConfigure(null, options);

        Assert.That(ReferenceEquals(options.ToolCollection, collection), Is.True, "PostConfigure must keep the existing collection instance");
        var entries = options.ToolCollection!.ToList();
        Assert.That(ReferenceEquals(entries[0], probeFirst), Is.True, "the pre-added probe must stay first");
        Assert.That(ReferenceEquals(entries[1], probeUnique), Is.True, "the second pre-added probe must stay second");
        Assert.That(entries.Count(t => t.ProtocolTool.Name == duplicateName), Is.EqualTo(1));
        Assert.That(ReferenceEquals(entries.Single(t => t.ProtocolTool.Name == duplicateName), probeFirst), Is.True,
            "a duplicate name keeps the probe instance (first wins, as the SDK's TryAdd)");
        Assert.That(
            entries.Skip(2).Select(t => t.ProtocolTool.Name).ToList(),
            Is.EqualTo(builtNames.Where(n => n != duplicateName).Distinct().ToList()));
    }

    [Test]
    public void PostConfigure_NoTools_CreatesAnEmptyCollection()
    {
        // With no tools the population still creates an empty tool collection.
        var services = new ServiceCollection().BuildServiceProvider();
        var emptyList = new ToolSurfaceStartupPopulation(services, Array.Empty<ToolSurfaceStartupRequest>());
        var emptyTypes = new ToolSurfaceStartupPopulation(services, new[] { new ToolSurfaceStartupRequest(Array.Empty<Type>()) });

        var fromEmptyList = new McpServerOptions();
        emptyList.PostConfigure(null, fromEmptyList);
        Assert.That(fromEmptyList.ToolCollection, Is.Not.Null);
        Assert.That(fromEmptyList.ToolCollection!.Count, Is.EqualTo(0));

        var fromEmptyTypes = new McpServerOptions();
        emptyTypes.PostConfigure(null, fromEmptyTypes);
        Assert.That(fromEmptyTypes.ToolCollection, Is.Not.Null);
        Assert.That(fromEmptyTypes.ToolCollection!.Count, Is.EqualTo(0));
    }

    [Test]
    public void BuildStartupTools_WithAProviderWithoutAController_Builds()
    {
        // No ToolSurfaceController is registered: the static builder must not resolve one.
        var services = new ServiceCollection().BuildServiceProvider();

        var tools = ToolSurfaceController.BuildStartupTools(StartupRequests(), null, services);

        Assert.That(tools, Is.Not.Empty);
    }
}

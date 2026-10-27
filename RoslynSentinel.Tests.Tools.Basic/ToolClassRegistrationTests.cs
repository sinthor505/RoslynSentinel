// Resolves every Tools.Basic [McpServerToolType] class from a container built ONLY from this project's
// dependencies (Common / Engines.Basic / Tools.Basic) -- no Server project. This is the first path that
// exercises the registration moved down into the Engines/Tools projects (proposal_registration_move_down.md);
// a tool class missing from AddRoslynSentinelToolClassesBasic, or a dependency the Impls/engines methods
// do not provide, fails here loudly instead of leaving the class silently unresolvable.

using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("ToolClassRegistration")]
public class ToolClassRegistrationTests
{
    private ServiceCollection _services = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _services = new ServiceCollection();
        _services.AddLogging();
        _services.AddRoslynSentinelEnginesBasic();
        _services.AddRoslynSentinelToolImplsBasic();
        _services.AddRoslynSentinelToolClassesBasic();

        // Host state, not registered by the flat methods: ToolsetControlTools needs a ToolsetService, which a real
        // host builds from the live McpServerOptions and the claude-lean on-demand class set. A stand-in over empty
        // options is enough to prove the class itself is constructible (no allowlist entry needed).
        _services.AddSingleton(sp => new ToolsetService(Options.Create(new McpServerOptions()), sp, new HashSet<string>()));

        // ValidateOnBuild checks every registered constructor graph up front, the same check the server enables.
        _provider = _services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [TearDown]
    public void TearDown() => _provider?.Dispose();

    private static IReadOnlyList<Type> ToolClasses() =>
        typeof(WorkspaceTools).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

    [Test]
    public void ToolClassDiscovery_FindsTheKnownClasses()
    {
        // Guards the reflection itself: if discovery silently returned nothing, the other tests would pass vacuously.
        var names = ToolClasses().Select(t => t.Name).ToList();
        Assert.That(names, Does.Contain(nameof(WorkspaceTools)));
        Assert.That(names, Has.Count.GreaterThan(10));
    }

    [Test]
    public void EveryToolClass_IsRegisteredByAddRoslynSentinelToolClassesBasic()
    {
        var missing = ToolClasses()
            .Where(t => !_services.Any(d => d.ServiceType == t))
            .Select(t => t.Name)
            .ToList();

        Assert.That(missing, Is.Empty,
            "Tools.Basic tool class(es) not registered by AddRoslynSentinelToolClassesBasic (a class missing there is " +
            "unresolvable from any host that relies on the flat method): " + string.Join(", ", missing));
    }

    [Test]
    public void EveryToolClass_Resolves()
    {
        foreach (var type in ToolClasses())
        {
            object? instance = null;
            Assert.DoesNotThrow(() => instance = _provider.GetService(type),
                $"Resolving {type.Name} threw; check its constructor dependencies against the flat registration methods.");
            Assert.That(instance, Is.Not.Null, $"{type.Name} did not resolve.");
        }
    }

    [Test]
    public void WorkspaceTools_Resolves()
    {
        // WorkspaceTools has the widest constructor in Tools.Basic (engines, Impls, workspace manager, logger).
        var tools = _provider.GetRequiredService<WorkspaceTools>();
        Assert.That(tools, Is.Not.Null);
    }
}

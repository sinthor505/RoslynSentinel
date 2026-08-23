using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;

using System.Reflection;

using SentinelModernizationTools = RoslynSentinel.Server.Advanced.SentinelModernizationTools;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class DependencyInjectionTests
{
    private IServiceProvider _serviceProvider = null!;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();

        // 1. Mock Logger
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // 2. Register all engines via the real production registration path, not a hand-copied
        // list — a hand-copied list is exactly what silently drifted out of sync with the actual
        // engine set three times (see docs/TODO.md's "Registration duplication" entry / the
        // project_dependency_direction memory). This now exercises the same code path
        // Program.cs/the HTTP hosts actually run.
        services.AddRoslynSentinelEnginesAdvanced();

        // 3. Register all tool classes the same way — every class carrying [McpServerToolType],
        // via the real mode-conditional registration path, all modes enabled, so
        // DynamicDiscovery_AllClassesWithToolAttribute_ShouldBeResolvable exercises the full set
        // rather than whatever subset happened to be hand-copied here.
        var allModes = new HashSet<string> { "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation", "Asyncify" };
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, allModes);

        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    [Test]
    public void AllMcpTools_ShouldBeResolvable()
    {
        // Act & Assert
        var workspaceTools = _serviceProvider.GetService<SentinelWorkspaceTools>();
        Assert.That(workspaceTools, Is.Not.Null);

        var refactoringTools = _serviceProvider.GetService<SentinelRefactoringTools>();
        Assert.That(refactoringTools, Is.Not.Null, "Failed to resolve SentinelRefactoringTools. Check constructor dependencies.");
    }

    [Test]
    public void DynamicDiscovery_AllClassesWithToolAttribute_ShouldBeResolvable()
    {
        var assembly = typeof(SentinelWorkspaceTools).Assembly;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);

        foreach (var type in toolTypes)
        {
            var instance = _serviceProvider.GetService(type);
            Assert.That(instance, Is.Not.Null, $"Dynamically discovered tool {type.Name} is not registered in the DI container.");
        }
    }
}

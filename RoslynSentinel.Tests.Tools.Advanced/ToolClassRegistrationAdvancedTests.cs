// Resolves every Tools.Advanced [McpServerToolType] class from a container built ONLY from Common / Engines.* /
// Tools.* registration (no Server project). Advanced counterpart of ToolClassRegistrationTests in Tests.Tools.Basic;
// see proposal_registration_move_down.md. A class missing from AddRoslynSentinelToolClassesAdvanced fails here.

using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
[Category("ToolClassRegistration")]
public class ToolClassRegistrationAdvancedTests
{
    private ServiceCollection _services = null!;
    private ServiceProvider _provider = null!;

    /// <summary>Stand-in for the LM Studio client (registered by the Server project's AddRoslynSentinelLlmClient in a real host).</summary>
    private sealed class StubLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default) =>
            Task.FromResult("stub");
    }

    [SetUp]
    public void Setup()
    {
        _services = new ServiceCollection();
        _services.AddLogging();
        _services.AddRoslynSentinelEnginesAdvanced();
        _services.AddRoslynSentinelToolImplsBasic();
        _services.AddRoslynSentinelToolImplsAdvanced();
        _services.AddRoslynSentinelToolClassesAdvanced();

        // CommentingEngine depends on ILlmClient, whose real registration needs Microsoft.Extensions.Http (Server project).
        // TODO share one fake ILlmClient via the Tests project once a second Tests.Tools.* project needs it (proposal Q3).
        _services.AddSingleton<ILlmClient, StubLlmClient>();

        _provider = _services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [TearDown]
    public void TearDown() => _provider?.Dispose();

    private static IReadOnlyList<Type> ToolClasses() =>
        typeof(AsyncifyTools).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

    [Test]
    public void ToolClassDiscovery_FindsTheKnownClasses()
    {
        var names = ToolClasses().Select(t => t.Name).ToList();
        Assert.That(names, Does.Contain(nameof(AsyncifyTools)));
        Assert.That(names, Has.Count.GreaterThan(5));
    }

    [Test]
    public void EveryToolClass_IsRegisteredByAddRoslynSentinelToolClassesAdvanced()
    {
        var missing = ToolClasses()
            .Where(t => !_services.Any(d => d.ServiceType == t))
            .Select(t => t.Name)
            .ToList();

        Assert.That(missing, Is.Empty,
            "Tools.Advanced tool class(es) not registered by AddRoslynSentinelToolClassesAdvanced: " + string.Join(", ", missing));
    }

    [Test]
    public void EveryToolClass_Resolves()
    {
        // Includes the multi-constructor classes (AdvancedRefactoringTools, GenerationTools): DI must pick a constructor.
        foreach (var type in ToolClasses())
        {
            object? instance = null;
            Assert.DoesNotThrow(() => instance = _provider.GetService(type),
                $"Resolving {type.Name} threw; check its constructor dependencies against the flat registration methods.");
            Assert.That(instance, Is.Not.Null, $"{type.Name} did not resolve.");
        }
    }
}

using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Server;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.SubAgent;

/// <summary>
/// The two SubAgent tool classes against the real production registration path. Adding a tool needs a
/// class, a ToolClassRegistry entry and an explicit DI block; none alone makes it callable, and a
/// missing one fails silently, so each is checked here.
/// </summary>
[TestFixture]
public class SubAgentRegistrationTests
{
    private static ServiceProvider BuildProvider(params string[] modes)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddRoslynSentinelEnginesAdvanced();
        services.AddMcpServer().AddRoslynSentinelToolsAdvanced(
            services, new HashSet<string>(modes, StringComparer.OrdinalIgnoreCase));
        return services.BuildServiceProvider();
    }

    [Test]
    public void BothToolClasses_ConstructCleanly_UnderRealDi()
    {
        using var provider = BuildProvider("SubAgentEval", "SubAgent");

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetService<SubAgentEvalTools>(), Is.Not.Null, "SubAgentEvalTools: check its constructor dependencies and DI block");
            Assert.That(provider.GetService<SubAgentTools>(), Is.Not.Null, "SubAgentTools: check its constructor dependencies and DI block");
        });
    }

    [Test]
    public void EachToolClass_IsRegisteredOnlyByItsOwnMode()
    {
        using var evalOnly = BuildProvider("SubAgentEval");
        using var agentOnly = BuildProvider("SubAgent");

        Assert.Multiple(() =>
        {
            Assert.That(evalOnly.GetService<SubAgentEvalTools>(), Is.Not.Null);
            Assert.That(evalOnly.GetService<SubAgentTools>(), Is.Null);
            Assert.That(agentOnly.GetService<SubAgentTools>(), Is.Not.Null);
            Assert.That(agentOnly.GetService<SubAgentEvalTools>(), Is.Null);
        });
    }

    [Test]
    public void ClaudeMode_DoesNotExposeEitherTool()
    {
        // The Claude mode is what a child server is launched with, so it must never carry the tools.
        using var provider = BuildProvider("Claude");

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetService<SubAgentEvalTools>(), Is.Null);
            Assert.That(provider.GetService<SubAgentTools>(), Is.Null);
        });
    }

    [Test]
    public void ToolClassRegistry_MapsEachModeToItsClass()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ToolClassRegistry.AdvancedModeToToolClasses["SubAgentEval"], Is.EqualTo(new[] { "SubAgentEvalTools" }));
            Assert.That(ToolClassRegistry.AdvancedModeToToolClasses["SubAgent"], Is.EqualTo(new[] { "SubAgentTools" }));
        });
    }

    [TestCase(typeof(SubAgentEvalTools), "SubAgentEval")]
    [TestCase(typeof(SubAgentTools), "SubAgent")]
    public void MaxTokensPerTurn_IsARequiredParameter_WithNoDefault(Type toolClass, string toolName)
    {
        // A C# parameter with no default is "required" in the emitted MCP schema. Defaulting it silently
        // re-introduced the truncated-turn false-positive the explicit budget exists to prevent.
        var method = toolClass.GetMethods()
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var parameter = method.GetParameters().Single(p => p.Name == "maxTokensPerTurn");

        Assert.Multiple(() =>
        {
            Assert.That(parameter.HasDefaultValue, Is.False);
            Assert.That(parameter.ParameterType, Is.EqualTo(typeof(int)), "must not be nullable either");
        });
    }

    [TestCase(typeof(SubAgentEvalTools), "SubAgentEval")]
    [TestCase(typeof(SubAgentTools), "SubAgent")]
    public void CancellationToken_IsTheLastParameter(Type toolClass, string toolName)
    {
        var method = toolClass.GetMethods()
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        Assert.That(method.GetParameters().Last().ParameterType, Is.EqualTo(typeof(CancellationToken)));
    }
}

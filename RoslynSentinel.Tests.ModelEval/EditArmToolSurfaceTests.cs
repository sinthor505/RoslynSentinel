using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Covers <see cref="EditArmToolSurface"/> without a model: the value parser, the per-arm hide sets, and the tool list an
/// in-process MCP server offers for each arm. The in-process-server tests that go through the environment-reading
/// Register methods set the arm variable, so they are not parallelizable.
/// </summary>
[TestFixture]
public class EditArmToolSurfaceTests
{
    private static readonly string[] OtherLeanTools = ["Member", "ReadFile", "Build", "RunTest", "DeleteFile"];

    [Test]
    public void Parse_UnsetEmptyOrWhitespace_IsDefault()
    {
        foreach (var value in new string?[] { null, "", "   " })
        {
            Assert.That(EditArmToolSurface.Parse(value), Is.EqualTo(EditArm.Default), $"value '{value}'");
        }
    }

    [TestCase("ReplaceSnippet", EditArm.ReplaceSnippet)]
    [TestCase("ReplaceRange", EditArm.ReplaceRange)]
    [TestCase("EditSnippets", EditArm.EditSnippets)]
    [TestCase("replacerange", EditArm.ReplaceRange)]
    [TestCase("EDITSNIPPETS", EditArm.EditSnippets)]
    [TestCase("  ReplaceSnippet  ", EditArm.ReplaceSnippet)]
    public void Parse_ArmNames_AreTrimmedAndCaseInsensitive(string value, EditArm expected) =>
        Assert.That(EditArmToolSurface.Parse(value), Is.EqualTo(expected));

    [TestCase("Replace")]
    [TestCase("ReplaceRanges")]
    [TestCase("Default")]
    [TestCase("bogus")]
    public void Parse_UnknownValue_ThrowsNamingTheValidValues(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => EditArmToolSurface.Parse(value));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("ReplaceSnippet"));
            Assert.That(ex.Message, Does.Contain("ReplaceRange"));
            Assert.That(ex.Message, Does.Contain("EditSnippets"));
        });
    }

    [Test]
    public void ArmHiddenTools_AreTheOtherEditTools()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EditArmToolSurface.ArmHiddenTools(EditArm.Default), Is.Empty);
            Assert.That(EditArmToolSurface.ArmHiddenTools(EditArm.ReplaceSnippet), Is.EquivalentTo(new[] { "ReplaceRange" }));
            Assert.That(EditArmToolSurface.ArmHiddenTools(EditArm.ReplaceRange), Is.EquivalentTo(new[] { "ReplaceSnippet" }));
            Assert.That(EditArmToolSurface.ArmHiddenTools(EditArm.EditSnippets), Is.EquivalentTo(new[] { "ReplaceSnippet", "ReplaceRange" }));
        });
    }

    [Test]
    public void ArmHiddenTools_ReturnsAFreshCaseInsensitiveSetPerCall()
    {
        var first = EditArmToolSurface.ArmHiddenTools(EditArm.ReplaceSnippet);
        first.Add("Other");
        var second = EditArmToolSurface.ArmHiddenTools(EditArm.ReplaceSnippet);

        Assert.Multiple(() =>
        {
            Assert.That(second, Does.Not.Contain("Other"));
            // NUnit's Does.Contain compares with its own ordinal equality, not the set's comparer.
            Assert.That(second.Contains("replacerange"), Is.True);
        });
    }

    [Test]
    public void NewAdvancedModes_AddTheExperimentModeOnlyForEditSnippets()
    {
        foreach (var arm in Enum.GetValues<EditArm>())
        {
            var modes = EditArmToolSurface.NewAdvancedModes(arm);

            Assert.Multiple(() =>
            {
                Assert.That(modes, Is.SupersetOf(ChainEvalToolSurface.NewModes()), $"arm {arm}");
                Assert.That(modes.Contains("EditSnippetsExperiment"), Is.EqualTo(arm == EditArm.EditSnippets), $"arm {arm}");
            });
        }
    }

    [Test]
    public void Default_AdvancedHiddenToolsEqualTheUnchangedLeanHideSet()
    {
        Assert.That(
            EditArmToolSurface.NewAdvancedHiddenTools(EditArm.Default, "lean"),
            Is.EquivalentTo(ChainEvalToolSurface.NewHiddenTools("lean", null)));
    }

    [TestCase(EditArm.Default, new[] { "ReplaceSnippet", "ReplaceRange" }, new[] { "EditSnippets" })]
    [TestCase(EditArm.ReplaceSnippet, new[] { "ReplaceSnippet" }, new[] { "ReplaceRange", "EditSnippets" })]
    [TestCase(EditArm.ReplaceRange, new[] { "ReplaceRange" }, new[] { "ReplaceSnippet", "EditSnippets" })]
    [TestCase(EditArm.EditSnippets, new[] { "EditSnippets" }, new[] { "ReplaceSnippet", "ReplaceRange" })]
    public async Task AdvancedSurface_UnderLean_OffersExactlyTheArmsEditTools(EditArm arm, string[] offeredEdit, string[] absentEdit)
    {
        var names = await ListOfferedNamesAsync(
            (mcpBuilder, services) => mcpBuilder.AddRoslynSentinelToolsAdvanced(
                services,
                EditArmToolSurface.NewAdvancedModes(arm),
                hideTools: EditArmToolSurface.NewAdvancedHiddenTools(arm, "lean")));

        AssertEditTools(names, offeredEdit, absentEdit);
        Assert.That(OtherLeanTools.Where(n => !names.Contains(n)), Is.Empty, "other lean tools that are not offered");
    }

    [TestCase("", new[] { "ReplaceSnippet", "ReplaceRange" }, new[] { "EditSnippets" })]
    [TestCase("ReplaceSnippet", new[] { "ReplaceSnippet" }, new[] { "ReplaceRange", "EditSnippets" })]
    [TestCase("ReplaceRange", new[] { "ReplaceRange" }, new[] { "ReplaceSnippet", "EditSnippets" })]
    [TestCase("EditSnippets", new[] { "EditSnippets" }, new[] { "ReplaceSnippet", "ReplaceRange" })]
    [NonParallelizable]
    public async Task RegisterAdvanced_ReadsTheArmVariable(string armValue, string[] offeredEdit, string[] absentEdit)
    {
        var names = await WithEnvironmentAsync(
            armValue,
            "lean",
            () => ListOfferedNamesAsync(EditArmToolSurface.RegisterAdvanced));

        AssertEditTools(names, offeredEdit, absentEdit);
        Assert.That(OtherLeanTools.Where(n => !names.Contains(n)), Is.Empty, "other lean tools that are not offered");
    }

    [TestCase("", new[] { "ReplaceSnippet", "ReplaceRange" }, new[] { "EditSnippets" })]
    [TestCase("ReplaceSnippet", new[] { "ReplaceSnippet" }, new[] { "ReplaceRange", "EditSnippets" })]
    [TestCase("ReplaceRange", new[] { "ReplaceRange" }, new[] { "ReplaceSnippet", "EditSnippets" })]
    [TestCase("EditSnippets", new[] { "EditSnippets" }, new[] { "ReplaceSnippet", "ReplaceRange" })]
    [NonParallelizable]
    public async Task RegisterBasic_ReadsTheArmVariable(string armValue, string[] offeredEdit, string[] absentEdit)
    {
        var names = await WithEnvironmentAsync(
            armValue,
            null,
            () => ListOfferedNamesAsync(EditArmToolSurface.RegisterBasic));

        AssertEditTools(names, offeredEdit, absentEdit);
    }

    [Test]
    [NonParallelizable]
    public void RegisterAdvanced_UnknownArmValue_Throws()
    {
        var saved = Environment.GetEnvironmentVariable(EditArmToolSurface.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EditArmToolSurface.EnvVar, "bogus");
            var services = new ServiceCollection();

            Assert.Throws<ArgumentException>(() => EditArmToolSurface.RegisterAdvanced(services.AddMcpServer(), services));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EditArmToolSurface.EnvVar, saved);
        }
    }

    private static void AssertEditTools(HashSet<string> names, string[] offeredEdit, string[] absentEdit)
    {
        Assert.Multiple(() =>
        {
            foreach (var name in offeredEdit)
            {
                Assert.That(names, Does.Contain(name), $"{name} must be offered");
            }

            foreach (var name in absentEdit)
            {
                Assert.That(names, Does.Not.Contain(name), $"{name} must not be offered");
            }
        });
    }

    private static async Task<HashSet<string>> WithEnvironmentAsync(string armValue, string? toolsetValue, Func<Task<HashSet<string>>> action)
    {
        var savedArm = Environment.GetEnvironmentVariable(EditArmToolSurface.EnvVar);
        var savedToolset = Environment.GetEnvironmentVariable(ChainEvalToolSurface.ToolsetEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EditArmToolSurface.EnvVar, armValue);
            Environment.SetEnvironmentVariable(ChainEvalToolSurface.ToolsetEnvVar, toolsetValue);
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EditArmToolSurface.EnvVar, savedArm);
            Environment.SetEnvironmentVariable(ChainEvalToolSurface.ToolsetEnvVar, savedToolset);
        }
    }

    // Same in-process server wiring as ChainEvalToolSurfaceLeanTests.ListOfferedToolsAsync, with the registration supplied by the caller.
    private static async Task<HashSet<string>> ListOfferedNamesAsync(Action<IMcpServerBuilder, IServiceCollection> register)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        register(mcpBuilder, services);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        var host = hostBuilder.Build();
        _ = host.RunAsync();

        McpClient? client = null;
        try
        {
            var clientTransport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);

            client = await McpClient.CreateAsync(
                clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
            return tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await host.StopAsync();
            host.Dispose();
        }
    }
}

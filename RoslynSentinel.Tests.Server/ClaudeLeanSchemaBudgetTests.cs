// Absolute size budget for the claude-lean core tool surface (the tools/list payload an agent pays for every session).
// Existing relative checks (ParameterEdit_EmittedSchema_IsSmallerThanTheTwoToolsItMerges and its DeclarationTool twin) only
// guard one merged tool against its sources; nothing else stopped the whole surface growing. Sizes here are the
// serialized JSON character count of each emitted ProtocolTool, from a real claude-lean host (core tools only, no
// on-demand toolsets). Each budget is the measured value rounded UP to the next 500 characters, so a small description
// tweak passes and real bloat fails.
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable]
[Category("ToolClassRegistry")]
public class ClaudeLeanSchemaBudgetTests
{
    // Measured on 2026-10-18 (core claude-lean surface, after the trim in proposal_trim_claude_lean_core_surface.md):
    // Advanced and Basic both 19 tools, 27752 chars total on 2026-10-18, now about 28117 after the RunTest environment parameter (TODO #39); largest tool Git at 4157 chars. Raised to 29000 on 2026-10-24: measured 28868 after the optional GetMethodSource containerName parameter (~330 chars; plan_unknown_parameter_rejection_reduction.md Step 4) on top of other additions since 2026-10-18. Raised to 29500 on 2026-10-25: measured 29229 after the optional Search contextLines parameter (~361 chars, commit 36e51f0c). Each constant is the
    // measurement rounded up to the next 500. Raise a constant only when the growth is intended.
    private const int AdvancedTotalBudget = 29500;
    private const int BasicTotalBudget = 29500;
    private const int AdvancedMaxToolBudget = 4500;
    private const int BasicMaxToolBudget = 4500;

    private const string RaiseHint = "if this growth is intended, raise the constant in ClaudeLeanSchemaBudgetTests and note why in the commit message";

    private static List<(string Name, int Chars)> Measure(bool advanced)
    {
        var services = new ServiceCollection();
        if (advanced)
        {
            services.AddRoslynSentinelEnginesAdvanced();
        }
        else
        {
            services.AddRoslynSentinelEnginesBasic();
        }

        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-lean" };
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mcpBuilder = services.AddMcpServer();
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

        using var host = hostBuilder.Build();
        var collection = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        return collection
            .Select(tool => (
                tool.ProtocolTool.Name,
                JsonSerializer.Serialize(tool.ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length))
            .OrderByDescending(t => t.Length)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Failure(string what, bool advanced, int measured, int budget, List<(string Name, int Chars)> tools) =>
        $"claude-lean ({(advanced ? "Advanced" : "Basic")}) {what} is {measured} chars, budget {budget}. Five largest tools: "
        + string.Join(", ", tools.Take(5).Select(t => $"{t.Name}={t.Chars}"))
        + $". {RaiseHint}";

    [TestCase(true, AdvancedTotalBudget)]
    [TestCase(false, BasicTotalBudget)]
    public void CoreSurface_TotalSchemaSize_IsWithinBudget(bool advanced, int budget)
    {
        var tools = Measure(advanced);
        int total = tools.Sum(t => t.Chars);
        TestContext.Out.WriteLine($"claude-lean {(advanced ? "Advanced" : "Basic")}: {tools.Count} tools, total {total} chars, largest {tools[0].Name}={tools[0].Chars}");

        Assert.That(total, Is.LessThanOrEqualTo(budget), Failure("total schema size", advanced, total, budget, tools));
    }

    [TestCase(true, AdvancedMaxToolBudget)]
    [TestCase(false, BasicMaxToolBudget)]
    public void CoreSurface_LargestToolSchema_IsWithinBudget(bool advanced, int budget)
    {
        var tools = Measure(advanced);
        var largest = tools[0];
        TestContext.Out.WriteLine($"claude-lean {(advanced ? "Advanced" : "Basic")}: largest tool {largest.Name}={largest.Chars} chars");

        Assert.That(largest.Chars, Is.LessThanOrEqualTo(budget), Failure($"largest tool ({largest.Name}) schema size", advanced, largest.Chars, budget, tools));
    }
}

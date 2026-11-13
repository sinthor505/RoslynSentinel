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

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable]
[Category("ToolClassRegistry")]
public class ClaudeLeanSchemaBudgetTests
{
    // Measured on 2026-10-18 (core claude-lean surface, after the trim in proposal_trim_claude_lean_core_surface.md):
    // Advanced and Basic both 19 tools, 27752 chars total on 2026-10-18, now about 28117 after the RunTest environment parameter (TODO #39); largest tool Git at 4157 chars. Raised to 29000 on 2026-10-24: measured 28868 after the optional GetMethodSource containerName parameter (~330 chars; plan_unknown_parameter_rejection_reduction.md Step 4) on top of other additions since 2026-10-18. Raised to 29500 on 2026-10-25: measured 29229 after the optional Search contextLines parameter (~361 chars, commit 36e51f0c). Each constant is the
    // measurement rounded up to the next 500. Raise a constant only when the growth is intended. Lowered on 2026-11-05 (W8 Search schema trim): total 27996 chars, Search 3815 -> 2329 chars (inputSchema 3215 -> 1729); total budget now 28500 (28000 left 4 chars headroom), Search budget 2450 (5% headroom). Raised to 29000 on 2026-11-05: measured 28877 after the McpServerStatus operatingMode, operatingModeSource and callerAgentId result fields plus a description clause (plan_edit_attribution_ledger.md Step 6) and a ReadFile showLineEndings parameter added by another session. Raised to 29500 on 2026-11-06: measured 29291 chars after CreateFile gained the optional content parameter and a longer description (plan_claude_lean_core_write_tools step 1). Raised to 32000 (Advanced) and 30500 (Basic) on 2026-11-06: measured 31964 and 30075 chars after WriteFile (~782 chars) and ChangeSignature (~1868 chars) were promoted into the claude-lean core (plan_claude_lean_core_write_tools step 4). Advanced raised to 32500 on 2026-11-08: measured 32298 chars after the required parameters (reason, resultId, paths) were added to the Build, RunTest, ProjectName, DocCommentId and Git hunkIds descriptions (proposal_tool_hint_builder.md decision 2026-11-08).
    // 2026-11-08: +~440 for batch-item field descriptions/required (finding_batch_edit_item_schema_hides_required_fields)
    // Raised on 2026-11-10 (todo punchlist #9): measured 44948 chars (Advanced; Basic was 39703 until the core split moved ChangeSignature and MoveMember into it, then also 44948), largest tool SemanticFindReplace 5450, after SemanticFindReplace, InspectSymbol, MoveMember, UsingDirective and GetDiagnostics joined the claude-lean startup surface (~12k chars; SemanticFindReplace alone ~5.4k).
    // Trimmed 2026-11-10 (proposal_reduce_tool_schema_token_cost.md): SemanticFindReplace 5450 -> 3864, MoveMember 3356 -> 3112; both flavors now measure 43118 total, largest tool Git 4261. Budgets are measured + at least 300 rounded up to the next 500: total 43500, per tool 5000 (Git sets it).
    // Raised 43500 -> 44000 on 2026-11-12: measured 43519 after GitRemoteOperation.config and the GitAction get/set/unset
    // values (plan_libgit2sharp_followups.md Step 20: GitRemote +514 chars).
    private const int AdvancedTotalBudget = 44000;
    // Basic raised 40000 -> 45000 on 2026-11-10 (core split slice 1): ChangeSignature (~1.9k) and MoveMember (3356) moved from Tools.Advanced to Tools.Basic (CoreRefactoringTools), so the Basic flavor now serves them and measures 44948 chars, the same as Advanced.
    private const int BasicTotalBudget = 44000;
    private const int AdvancedMaxToolBudget = 5000;
    private const int BasicMaxToolBudget = 5000;
    // Search per-tool budget.
    private const int AdvancedSearchToolBudget = 2450;
    private const int BasicSearchToolBudget = 2450;

    private const string RaiseHint = "if this growth is intended, raise the constant in ClaudeLeanSchemaBudgetTests and note why in the commit message";

    private static List<(string Name, int Chars, int InputSchemaChars)> Measure(bool advanced)
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
                Name: tool.ProtocolTool.Name,
                Chars: JsonSerializer.Serialize(tool.ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length,
                InputSchemaChars: JsonSerializer.Serialize(tool.ProtocolTool.InputSchema, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length))
            .OrderByDescending(t => t.Chars)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    // RunTest keeps output only for failed tests, so this file is how headroom is read after a passing run.
    private static void WriteSizeReport(bool advanced, int total, int budget, List<(string Name, int Chars, int InputSchemaChars)> tools)
    {
        try
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
            {
                dir = dir.Parent;
            }

            if (dir == null)
            {
                return;
            }

            var outDir = System.IO.Path.Combine(dir.FullName, ".roslynsentinel", "schema-sizes");
            System.IO.Directory.CreateDirectory(outDir);
            var lines = new List<string> { $"total={total} budget={budget} headroom={budget - total} tools={tools.Count}" };
            lines.AddRange(tools.Select(t => $"{t.Name} {t.Chars} inputSchema={t.InputSchemaChars}"));
            System.IO.File.WriteAllLines(System.IO.Path.Combine(outDir, advanced ? "claude-lean-Advanced.txt" : "claude-lean-Basic.txt"), lines);
        }
        catch (System.IO.IOException)
        {
            // Best effort: a report problem must never fail the budget test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: a report problem must never fail the budget test.
        }
    }

    private static string Failure(string what, bool advanced, int measured, int budget, List<(string Name, int Chars, int InputSchemaChars)> tools) =>
        $"claude-lean ({(advanced ? "Advanced" : "Basic")}) {what} is {measured} chars, budget {budget}. Five largest tools: "
        + string.Join(", ", tools.Take(5).Select(t => $"{t.Name}={t.Chars}"))
        + $". {RaiseHint}";

    [TestCase(true, AdvancedTotalBudget)]
    [TestCase(false, BasicTotalBudget)]
    public void CoreSurface_TotalSchemaSize_IsWithinBudget(bool advanced, int budget)
    {
        var tools = Measure(advanced);
        int total = tools.Sum(t => t.Chars);
        WriteSizeReport(advanced, total, budget, tools);
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

    [TestCase(true, AdvancedSearchToolBudget)]
    [TestCase(false, BasicSearchToolBudget)]
    public void CoreSurface_SearchToolSchema_IsWithinBudget(bool advanced, int budget)
    {
        var tools = Measure(advanced);
        var search = tools.FirstOrDefault(t => t.Name == "Search");
        Assert.That(search.Name, Is.EqualTo("Search"), $"claude-lean ({(advanced ? "Advanced" : "Basic")}) surface has no Search tool. Tools: {string.Join(", ", tools.Select(t => t.Name))}");
        TestContext.Out.WriteLine($"claude-lean {(advanced ? "Advanced" : "Basic")}: Search entry {search.Chars} chars, inputSchema {search.InputSchemaChars} chars");

        Assert.That(search.Chars, Is.LessThanOrEqualTo(budget), Failure($"Search tool schema (entry {search.Chars} chars, inputSchema {search.InputSchemaChars} chars)", advanced, search.Chars, budget, tools));
    }
}

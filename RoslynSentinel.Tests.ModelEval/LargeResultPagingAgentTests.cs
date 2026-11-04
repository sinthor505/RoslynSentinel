using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tests.ModelEval.Fixtures;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Real-LM smoke test for the large-result marshaller (docs/current/plans/plan_wire_result_marshaller.md).
/// The fixture adds one generated 300-method file; ReadFile on it overflows the inline cap, so the server
/// returns a largeResult pointer and the model must page the rest with GetLargeResult to find the needle.
/// Read-only exercise: every mutating tool is blocked by a request filter. Same template and mode strings as
/// PlanOnlyAgentTests.
/// </summary>
[TestFixture]
public class LargeResultPagingAgentTests
{
    // Needle is method number 137 (1-based) of 300. Every other constant is a 3-decimal literal, so the
    // 4-decimal NeedleLiteral appears exactly once in the generated file.
    private const int RateMethodCount = 300;
    private const int NeedleMethodNumber = 137;
    private const string NeedleMethodName = "Rate_PlatinumAnnualContract";
    private const string NeedleLiteral = "0.1375m";
    private const string NeedleRate = "0.1375";

    // Same task framing as PlanOnlyAgentTests, but asks for a read-only answer about one generated file.
    // No paging hint: the model has to discover the largeResult pointer on its own.
    private const string LargeResultUserPromptTemplate = """
        The file `{0}/FixtureHelpers/TierRates.cs` has 300 rate methods. Read it with ReadFile (the whole file, no line range) and tell me the exact name of the method that applies the Platinum annual-contract rate, and the decimal rate it uses. Do not edit anything. Answer with the method name and the rate.
        """;

    // "Refactor" and "Workspace" are the exact mode strings AddRoslynSentinelToolsBasic checks. "Workspace"
    // gates WorkspaceReadNavigationTools (ReadFile, GetFileOutline, Search and GetLargeResult), so this set
    // exposes every read tool the paging path needs. The BlockedToolNames filter below does the no-edit enforcement.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace", "WorkspaceFileContent",
    };

    // Same mutating-tool block list as PlanOnlyAgentTests.
    private static readonly HashSet<string> BlockedToolNames = new(StringComparer.Ordinal)
    {
        "ApplyDiff", "ApplyUnifiedDiff", "ApplyDiffWithConfirmationCode", "ChangeAccessibility", "ModifyModifier",
        "WriteFile", "DeleteFile",
    };

    // Same template, but the objective is the ReadFile tool itself: the model must page the whole file through
    // GetLargeResult. Efficiency is explicitly irrelevant.
    private const string LargeResultReadFileOnlyPromptTemplate = """
        The objective of this session is to test the ReadFile tool. Read the whole file `{0}/FixtureHelpers/TierRates.cs` with ReadFile (no line range). If the result says it is INCOMPLETE, follow its GetLargeResult call and keep paging until you find the method that handles the Platinum annual contract tier, then report its method name and its rate value. Efficiency is irrelevant and no other tools are available. Do not edit anything.
        """;

    // Read-only find-by-name tools, blocked on top of BlockedToolNames for the ReadFile-only scenario. ReadFile stays
    // available, but a ReadFile call that names a member (memberName) is refused too (see blockReadFileMemberLookup),
    // so the whole-file read plus GetLargeResult paging is the only route to the needle.
    private static readonly HashSet<string> ReadFileOnlyBlockedToolNames = new(BlockedToolNames)
    {
        "Search", "GetFileOutline", "GetMethodSource", "Member", "ListAll", "FindReferences", "InspectSymbol",
        "LocateSymbol", "QuerySymbolRelationships",
    };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private LmStudioAgentClient _agentClient = null!;
    private string _runDirectory = null!;

    /// <summary>
    /// Deterministic TierRates.cs body: 300 one-expression rate methods. Method NeedleMethodNumber is the
    /// Platinum annual-contract needle; every other rate literal has the 3-decimal form, so NeedleLiteral
    /// occurs exactly once in the file.
    /// </summary>
    private static string BuildTierRatesSource()
    {
        var sb = new StringBuilder();
        sb.Append("namespace ContosoOrders.Core.FixtureHelpers;\r\n\r\n");
        sb.Append("public static class TierRates\r\n{\r\n");
        for (var i = 1; i <= RateMethodCount; i++)
        {
            var isNeedle = i == NeedleMethodNumber;
            var name = isNeedle ? NeedleMethodName : $"Rate_Tier{i:000}_Sku{i:0000}";
            var literal = isNeedle ? NeedleLiteral : $"0.{(i * 37) % 900 + 100:000}m";
            var summary = isNeedle
                ? "Platinum annual-contract rate: applies the platinum annual-contract percentage to the order amount."
                : $"Tier {i:000} SKU {i:0000} rate: applies a fixed percentage to the order amount.";
            sb.Append($"    /// <summary>{summary}</summary>\r\n");
            sb.Append($"    public static decimal {name}(decimal amount)\r\n");
            sb.Append("    {\r\n");
            sb.Append($"        var rate = {literal};\r\n");
            sb.Append("        return amount * rate;\r\n");
            sb.Append("    }\r\n\r\n");
        }
        sb.Append("}\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Walks a tool-result JSON tree (re-parsing string payloads that hold JSON) and collects every
    /// largeResult.omitted entry as a one-line description for the summary file.
    /// </summary>
    private static void CollectOmittedEntries(JsonElement element, List<string> lines, int depth = 0)
    {
        if (depth > 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("omitted") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in property.Value.EnumerateArray())
                        {
                            lines.Add($"path={ReadProperty(entry, "path")} returned={ReadProperty(entry, "returned")} " +
                                $"total={ReadProperty(entry, "total")} unit={ReadProperty(entry, "unit")}");
                        }
                    }
                    else
                    {
                        CollectOmittedEntries(property.Value, lines, depth + 1);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectOmittedEntries(item, lines, depth + 1);
                }
                break;
            case JsonValueKind.String:
                // Tool results often carry their JSON as an embedded text block, so re-parse string payloads.
                var text = element.GetString()?.TrimStart();
                if (text is not null && (text.StartsWith('{') || text.StartsWith('[')))
                {
                    try
                    {
                        using var nested = JsonDocument.Parse(text);
                        CollectOmittedEntries(nested.RootElement, lines, depth + 1);
                    }
                    catch (JsonException)
                    {
                        // Not JSON after all - nothing to collect.
                    }
                }
                break;
        }
    }

    private static string ReadProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "?";

    // True when a ReadFile call names a member (memberName), i.e. a find-by-name shortcut around GetLargeResult paging.
    private static bool NamesMember(CallToolRequestParams? parameters) =>
        parameters?.Arguments is { } arguments
        && arguments.TryGetValue("memberName", out var member)
        && member.ValueKind == JsonValueKind.String;

    [SetUp]
    public void SetUp()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }
    }

    /// <summary>
    /// Starts the in-memory MCP server and the agent host for one scenario. blockedToolNames are refused by the
    /// request filter; blockReadFileMemberLookup also refuses ReadFile calls that name a member.
    /// </summary>
    private async Task StartHarnessAsync(IReadOnlySet<string> blockedToolNames, bool blockReadFileMemberLookup)
    {
        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        // Blocks every mutating tool in BlockedToolNames with a readable error instead of a protocol exception,
        // so the model can answer from what it has already read.
        mcpBuilder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => new McpRequestHandler<CallToolRequestParams, CallToolResult>(
                async (context, cancellationToken) =>
                {
                    if (blockReadFileMemberLookup && context.Params?.Name == "ReadFile" && NamesMember(context.Params))
                    {
                        return new CallToolResult
                        {
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = "ReadFile with memberName is unavailable in this session - read the whole " +
                                        "file (no memberName, no line range) and page the INCOMPLETE result with GetLargeResult.",
                                },
                            ],
                            IsError = true,
                        };
                    }

                    if (context.Params?.Name is { } toolName && blockedToolNames.Contains(toolName))
                    {
                        return new CallToolResult
                        {
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = $"{toolName} is unavailable in this session - this is a " +
                                        "read-only exercise. Answer from the file contents you have read " +
                                        "instead of calling this tool.",
                                },
                            ],
                            IsError = true,
                        };
                    }

                    return await next(context, cancellationToken);
                }));
        });

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddLmStudioAgentClient(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(_runDirectory, "agent.log")));

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "TierRates.cs"),
            BuildTierRatesSource(),
            reloadSolution: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _mcpClient = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        _agentClient = _host.Services.GetRequiredService<LmStudioAgentClient>();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _fixture?.Dispose();

        if (_runDirectory is not null)
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory);
        }

        // Each scenario starts its own harness. Clear the handles so a test that ends in SetUp's Assert.Ignore
        // does not stop or archive the previous test's objects.
        _mcpClient = null!;
        _host = null!;
        _fixture = null!;
        _runDirectory = null!;
    }

    private static List<(int TurnNumber, AgentToolCallRecord Call)> AllToolCalls(AgentRunResult result) =>
        result.Transcript.Turns
            .SelectMany(t => t.ToolCalls.Select(c => (t.TurnNumber, c)))
            .ToList();

    private static bool CarriesLargeResultPointer(AgentToolCallRecord call) =>
        call.ResultJson.Contains("omitted") && call.ResultJson.Contains("largeResult");

    /// <summary>
    /// Writes largeresult-summary.txt next to agent.log: tool calls in order, every result that carried a
    /// largeResult pointer with its omitted entries, and the final answer.
    /// </summary>
    private void WriteSummary(AgentRunResult result, List<(int TurnNumber, AgentToolCallRecord Call)> calls, string finalMessage)
    {
        var lines = new List<string>
        {
            $"Stop reason: {result.StopReason}, turns: {result.TurnCount}",
            string.Empty,
            "Tool calls in order:",
        };

        var order = 0;
        foreach (var (turnNumber, call) in calls)
        {
            order++;
            lines.Add($"  {order}. turn {turnNumber}: {call.ToolName}{(call.IsError ? " (error)" : string.Empty)}");
        }

        lines.Add(string.Empty);
        lines.Add("Results carrying largeResult.omitted:");
        var pointerCount = 0;
        foreach (var (turnNumber, call) in calls.Where(c => CarriesLargeResultPointer(c.Call)))
        {
            pointerCount++;
            var entries = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(call.ResultJson);
                CollectOmittedEntries(doc.RootElement, entries);
            }
            catch (JsonException)
            {
                entries.Add("(result body is not JSON)");
            }

            if (entries.Count == 0)
            {
                entries.Add("(largeResult present, no omitted entries parsed)");
            }

            lines.Add($"  turn {turnNumber} {call.ToolName}:");
            lines.AddRange(entries.Select(e => "    " + e));
        }

        if (pointerCount == 0)
        {
            lines.Add("  (none)");
        }

        lines.Add(string.Empty);
        lines.Add("Final answer:");
        lines.Add(finalMessage);

        Directory.CreateDirectory(_runDirectory);
        File.WriteAllText(
            Path.Combine(_runDirectory, "largeresult-summary.txt"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    /// <summary>
    /// The last turn's answer. This model streams its final answer as ReasoningContent when a turn makes no tool
    /// calls, so fall back to it the same way PlanOnlyAgentTests does.
    /// </summary>
    private static string FinalMessageOf(AgentRunResult result)
    {
        var finalTurn = result.Transcript.Turns
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t.ModelMessage.Content) || !string.IsNullOrWhiteSpace(t.ModelMessage.ReasoningContent));
        return finalTurn is null
            ? string.Empty
            : (string.IsNullOrWhiteSpace(finalTurn.ModelMessage.Content) ? finalTurn.ModelMessage.ReasoningContent : finalTurn.ModelMessage.Content) ?? string.Empty;
    }

    /// <summary>
    /// Runs the agent on the paging prompt and writes largeresult-summary.txt before any assertion, so the summary
    /// exists even when an assertion fails. The harness must already be started by StartHarnessAsync.
    /// </summary>
    private async Task<(AgentRunResult Result, List<(int TurnNumber, AgentToolCallRecord Call)> Calls, string FinalMessage)> RunPagingScenarioAsync(
        string userPrompt)
    {
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: 15, wallClockCap: TimeSpan.FromMinutes(15),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var result = await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, _runDirectory, TestContext.CurrentContext.CancellationToken);

        var calls = AllToolCalls(result);
        var finalMessage = FinalMessageOf(result);
        WriteSummary(result, calls, finalMessage);
        return (result, calls, finalMessage);
    }

    /// <summary>
    /// Precondition (a): something must have overflowed, or the marshaller was not exercised at all. Reports
    /// Inconclusive otherwise and returns the number of pointer-carrying results.
    /// </summary>
    private static int AssertOverflowed(List<(int TurnNumber, AgentToolCallRecord Call)> calls)
    {
        var pointerCount = calls.Count(c => CarriesLargeResultPointer(c.Call));
        if (pointerCount == 0)
        {
            Assert.Inconclusive("no result overflowed - the marshaller was not exercised");
        }

        return pointerCount;
    }

    /// <summary>
    /// No stack-trace or generic-filter leakage in any tool result.
    /// </summary>
    private static void AssertNoLeakage(List<(int TurnNumber, AgentToolCallRecord Call)> calls, AgentRunResult result)
    {
        var leaked = calls
            .Where(c => System.Text.RegularExpressions.Regex.IsMatch(c.Call.ResultJson, @"\sat (?:RoslynSentinel|System)\.[\w.<>+,]*\(")
                || c.Call.ResultJson.Contains(".cs:line ")
                || c.Call.ResultJson.Contains("offloaded"))
            .ToList();
        Assert.That(leaked, Is.Empty,
            $"Tool results leaked a stack trace or the generic filter stub ({string.Join(", ", leaked.Select(c => c.Call.ToolName))}). " +
            $"Transcript: {result.TranscriptPath}");
    }

    /// <summary>
    /// Real-model smoke test of the large-result marshaller: ReadFile on the 300-method file is cut by the
    /// server, and the model must follow the largeResult pointer with GetLargeResult to find the needle.
    /// </summary>
    [Test]
    public async Task Model_PagesLargeResultToFindNeedle()
    {
        await StartHarnessAsync(BlockedToolNames, blockReadFileMemberLookup: false);
        var userPrompt = string.Format(LargeResultUserPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        var (result, calls, finalMessage) = await RunPagingScenarioAsync(userPrompt);

        var pointerCount = AssertOverflowed(calls);

        // (b) Main behaviour under test: the model must follow the pointer.
        Assert.That(calls.Any(c => c.Call.ToolName == "GetLargeResult"), Is.True,
            $"The model never followed the largeResult pointer (no GetLargeResult call) even though " +
            $"{pointerCount} result(s) overflowed. Transcript: {result.TranscriptPath}");

        // (c) The final answer names the needle and its rate.
        Assert.That(finalMessage, Does.Contain(NeedleMethodName),
            $"The final answer should name {NeedleMethodName}. Transcript: {result.TranscriptPath}");
        Assert.That(finalMessage, Does.Contain(NeedleRate),
            $"The final answer should state the rate {NeedleRate}. Transcript: {result.TranscriptPath}");

        // (d) No stack-trace or generic-filter leakage in any tool result.
        AssertNoLeakage(calls, result);
    }

    /// <summary>
    /// ReadFile-only variant: Search, GetFileOutline, GetMethodSource and Member are blocked, and ReadFile refuses
    /// memberName, so the model can only read the whole file, follow the largeResult pointer and page it with
    /// GetLargeResult. Efficiency is irrelevant; the assertions check that a GetLargeResult page itself carried the needle.
    /// </summary>
    [Test]
    public async Task Model_PagesLargeResultToFindNeedle_ReadFileOnly()
    {
        await StartHarnessAsync(ReadFileOnlyBlockedToolNames, blockReadFileMemberLookup: true);
        var userPrompt = string.Format(LargeResultReadFileOnlyPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        var (result, calls, finalMessage) = await RunPagingScenarioAsync(userPrompt);

        var pointerCount = AssertOverflowed(calls);

        // (b) The model must follow the pointer with GetLargeResult.
        Assert.That(calls.Any(c => c.Call.ToolName == "GetLargeResult"), Is.True,
            $"The model never followed the largeResult pointer (no GetLargeResult call) even though " +
            $"{pointerCount} result(s) overflowed. Transcript: {result.TranscriptPath}");

        // (c) A GetLargeResult page itself must carry the needle name and its rate, not only the model's answer.
        var pageCarriedNeedle = calls.Any(c => c.Call.ToolName == "GetLargeResult"
            && c.Call.ResultJson.Contains(NeedleMethodName, StringComparison.Ordinal)
            && c.Call.ResultJson.Contains(NeedleRate, StringComparison.Ordinal));
        Assert.That(pageCarriedNeedle, Is.True,
            $"No GetLargeResult result contained {NeedleMethodName} and {NeedleRate}, so the needle was never paged in. " +
            $"Transcript: {result.TranscriptPath}");

        // (d) The final answer names the needle and its rate.
        Assert.That(finalMessage, Does.Contain(NeedleMethodName),
            $"The final answer should name {NeedleMethodName}. Transcript: {result.TranscriptPath}");
        Assert.That(finalMessage, Does.Contain(NeedleRate),
            $"The final answer should state the rate {NeedleRate}. Transcript: {result.TranscriptPath}");

        // (e) No stack-trace or generic-filter leakage in any tool result.
        AssertNoLeakage(calls, result);
    }
}

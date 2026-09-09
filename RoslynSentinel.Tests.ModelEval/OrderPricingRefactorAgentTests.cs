using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Tests.ModelEval.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Drives a real LM Studio model through a real in-process MCP server against the
/// <see cref="OrderPricingRefactorReproducer"/> fixture — three independent, ordinary refactoring
/// steps chained on one small class (extract a duplicated expression, rename a method with a real
/// cross-file call site, change the new method's accessibility) rather than
/// <see cref="WholeFileRewriteAgentTests"/>/<see cref="SizeThresholdAgentTests"/>'s single bug-fix
/// scenario. The prompt states the three outcomes and constraints, not exact tool calls or exact
/// parameters, matching the flexibility <c>MinimalGuidance</c>/<c>Disambiguated</c> give elsewhere —
/// the model may use dedicated refactor tools (ExtractMethodSafe, RenameSymbol, ChangeAccessibility/
/// ModifyModifier) or ApplyDiff for any step, its choice.
///
/// Updated 2026-09-05 after 2/2 runs in a batch independently produced the same wrong-but-plausible
/// extraction: step 1's original wording ("extract that duplicated discount-amount calculation")
/// was ambiguous between "factor out the shared `amount * rate` expression" (the intended, narrower
/// reading — what AssertRefactorsApplied actually checks for) and "extract the whole per-branch
/// discount-amount computation, including the 1.1x branching, into one new method" (a broader,
/// equally reasonable reading of the same sentence). Both failing runs took the broader reading,
/// moved the if/else into the new method, and wrote `amount * rate` separately in each of ITS two
/// branches — so the literal duplication the fixture targets survived, just one level down, while
/// still fully satisfying the step's own stated goal ("share one calculation" read as "share the
/// discount logic"). Reworded step 1 to name the exact expression to factor out and explicitly say
/// the branching/scaling logic must stay in `CalcDisc`, closing off the broader reading.
/// </summary>
[TestFixture]
public class OrderPricingRefactorAgentTests
{
    private const string UserPromptTemplate = """
        # Task: Three small refactors in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below — use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `CalcDisc` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply all three)

        1. **Extract**: both branches of `CalcDisc` repeat the exact expression `amount * rate` —
           factor only that expression out into its own new private method on the same class (it
           should take `amount` and `rate` and return their product), and have both branches call
           your new method instead of repeating `amount * rate` inline — this includes the
           preferred-customer branch, which still applies its 1.1x scaling on top of the
           extracted call. The `* 1.1m` scaling factor is the only part of that branch that stays
           in `CalcDisc` and does not move into the new method. Preserve the existing behavior
           exactly (preferred customers still get the 1.1x scaling, standard customers don't).

        2. **Rename**: Rename `CalcDisc` to `CalculateDiscountedTotal`. This method is called from
           `OrderCheckout.cs` — that call site must also be updated to the new name; a rename that
           only changes the method's declaration and misses its caller is not complete.

        3. **Change accessibility**: Change the new method you extracted in step 1 from `private`
           to `internal`, so other classes in the same project could call it directly if needed.

        ## Constraints

        - Do not change the logic of `DescribeOrder` or `SummarizeShipping` in
          `OrderPricingCalculator.cs`, or anything in `OrderCheckout.cs` other than the one call
          site that must follow the rename — reformatting is fine, but their behavior must stay
          identical.
        - Do not change the observable behavior of `CalculateDiscountedTotal` (formerly
          `CalcDisc`) — same inputs must still produce the same outputs.
        - Verify your changes compile, using an MCP tool (you have no terminal access). Scope the
          build to just the `ContosoOrders.Core` project rather than the whole solution.

        ## Before you report done

        Re-read the current, actual contents of `CalculateDiscountedTotal` (formerly `CalcDisc`) —
        do not rely on your memory of the edit you intended to make. For each item below, check the
        real code and answer yourself honestly before writing your summary:

        1. Does the standard-customer branch call your new extracted method? Does the
           preferred-customer branch ALSO call it (with `* 1.1m` applied to the call's result), or
           did it get skipped and still compute `amount * rate` (or `amount * rate * 1.1m`) inline?
           Both branches must call the new method — re-read both branches, not just one.
        2. Is the method's declaration renamed to `CalculateDiscountedTotal`, AND is the call site in
           `OrderCheckout.cs` updated to the new name — not just one of the two?
        3. Is the extracted method's accessibility actually `internal` in the file on disk right now,
           not still `private`?

        If re-reading the code reveals any of the above isn't true, fix it now before reporting —
        do not report success based on what you intended to do.

        Report what you changed and the verification result.
        """;

    // "Refactor" and "Workspace" are the exact mode strings AddRoslynSentinelToolsBasic checks —
    // together they register ApplyDiff/Build/ReadFile/SearchSolutionText/ListSolutionItems plus
    // SentinelRefactoringTools (ExtractMethodSafe, RenameSymbol, ChangeAccessibility,
    // ModifyModifier), without pulling in Advanced's larger tool catalog.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace",
    };

    // See OrderPricingRefactorChainAgentTests' identical toggle for the rationale — an isolation
    // experiment comparing this rung with and without the WriteFile whole-file-rewrite escape
    // hatch available, keeping ApplyDiff/ApplyUnifiedDiff/RenameSymbol/ChangeSignature exposed.
    private static readonly bool BlockWriteFile = false;

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private LmStudioAgentClient _agentClient = null!;
    private string _runDirectory = null!;
    private DotnetTestResult _testBaseline = null!;

    [SetUp]
    public async Task SetUp()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set — model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        if (BlockWriteFile)
        {
            mcpBuilder.WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(next => new McpRequestHandler<CallToolRequestParams, CallToolResult>(
                    async (context, cancellationToken) =>
                    {
                        if (context.Params?.Name == "WriteFile")
                        {
                            return new CallToolResult
                            {
                                Content =
                                [
                                    new TextContentBlock
                                    {
                                        Text = "WriteFile is unavailable in this session. Use " +
                                            "ApplyDiff, ApplyUnifiedDiff, RenameSymbol, or " +
                                            "ChangeSignature instead.",
                                    },
                                ],
                                IsError = true,
                            };
                        }

                        return await next(context, cancellationToken);
                    }));
            });
        }

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddHttpClient<LmStudioAgentClient>(client =>
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
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "OrderPricingCalculator.cs"),
            OrderPricingRefactorReproducer.StartingCalculatorFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "OrderCheckout.cs"),
            OrderPricingRefactorReproducer.CheckoutCallerFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Tests", "ModelEvalGenerated", "OrderCheckoutTests.cs"),
            OrderPricingRefactorReproducer.CheckoutFrontDoorTestsFileContent,
            reloadSolution: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var testProjectPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Tests", "ContosoOrders.Tests.csproj");
        _testBaseline = await DotnetTestRunner.RunAsync(testProjectPath, TestContext.CurrentContext.CancellationToken);

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
    }

    [Test]
    public async Task Model_AppliesThreeChainedRefactors()
    {
        var result = await RunOnceAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertRefactorsApplied(result);
    }

    /// <summary>
    /// Runs the same prompt N times against a fresh fixture each time to check how consistently the
    /// model applies all three steps together, not just one or two of them.
    /// </summary>
    [Test]
    [Explicit("Slow (N real model runs); run manually via `dotnet test --filter ConsistencyCheck`.")]
    public async Task Model_AppliesThreeChainedRefactors_ConsistencyCheck()
    {
        const int runs = 5;
        var passCount = 0;
        var turnCounts = new List<int>();

        for (var i = 0; i < runs; i++)
        {
            if (i > 0)
            {
                await TearDown();
                await SetUp();
            }

            var result = await RunOnceAsync(TestContext.CurrentContext.CancellationToken);
            turnCounts.Add(result.TurnCount);

            if (result.Converged)
            {
                try
                {
                    await AssertRefactorsApplied(result);
                    passCount++;
                }
                catch (Exception ex) when (ex is AssertionException or InvalidOperationException or IOException)
                {
                }
            }
        }

        TestContext.Out.WriteLine($"Pass rate: {passCount}/{runs}. Turn counts: [{string.Join(", ", turnCounts)}]");
        Assert.That(passCount, Is.GreaterThan(0), $"Model never succeeded across {runs} runs — see per-run transcripts under {_runDirectory}/../");
    }

    private async Task<AgentRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, turnCap: 40, wallClockCap: TimeSpan.FromMinutes(30),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(UserPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        return await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, _runDirectory, cancellationToken);
    }

    private async Task AssertRefactorsApplied(AgentRunResult result)
    {
        var calculatorPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "OrderPricingCalculator.cs");
        var checkoutPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "OrderCheckout.cs");
        Assert.That(File.Exists(calculatorPath), Is.True, "OrderPricingCalculator.cs should still exist after the model's edits.");
        Assert.That(File.Exists(checkoutPath), Is.True, "OrderCheckout.cs should still exist after the model's edits.");

        var calculatorText = File.ReadAllText(calculatorPath);
        var checkoutText = File.ReadAllText(checkoutPath);

        // Step 2 (rename): old name gone everywhere, new name present in both the declaration and
        // the call site — a same-file-only rename (missing OrderCheckout.cs) is the specific
        // failure mode this fixture is built to catch.
        Assert.That(calculatorText, Does.Not.Match(@"\bCalcDisc\b"),
            $"CalcDisc should be fully renamed to CalculateDiscountedTotal in OrderPricingCalculator.cs. Transcript: {result.TranscriptPath}");
        Assert.That(calculatorText, Does.Match(@"\bCalculateDiscountedTotal\b"),
            $"CalculateDiscountedTotal should be defined in OrderPricingCalculator.cs. Transcript: {result.TranscriptPath}");
        Assert.That(checkoutText, Does.Not.Match(@"\bCalcDisc\b"),
            $"OrderCheckout.cs's call site should be updated to the new name, not left calling CalcDisc. Transcript: {result.TranscriptPath}");
        Assert.That(checkoutText, Does.Match(@"\bCalculateDiscountedTotal\b"),
            $"OrderCheckout.cs should call CalculateDiscountedTotal after the rename. Transcript: {result.TranscriptPath}");

        // Step 1 (extract): the discount-amount expression should no longer appear twice as CODE —
        // both branches should route through one shared method instead. Strip // and /// comment
        // lines first: the fixture's own doc comment on CalcDisc/CalculateDiscountedTotal
        // describes this exact calculation in prose, and a model isn't asked to touch comments, so
        // counting raw text here would false-positive on a correct extraction (confirmed live: a
        // qwen3.5-9b run extracted correctly but left the original doc comment mentioning the
        // expression, which alone satisfied a naive substring count).
        var calculatorCodeOnly = string.Join(
            "\n",
            calculatorText.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var inlineDiscountExpressionCount = System.Text.RegularExpressions.Regex.Matches(
            calculatorCodeOnly, @"amount\s*\*\s*rate").Count;
        Assert.That(inlineDiscountExpressionCount, Is.LessThanOrEqualTo(1),
            $"The 'amount * rate' discount calculation should be extracted into a shared method, not " +
            $"repeated inline in both branches (found {inlineDiscountExpressionCount} occurrences in code). " +
            $"Transcript: {result.TranscriptPath}");

        // Step 3 (accessibility): the extracted method must exist, and must NOT be private anymore.
        // Locating it robustly (name is the model's own choice) via: whatever new method/local the
        // rewritten CalculateDiscountedTotal calls that isn't itself. Matched via a broad
        // "internal ... decimal <Name>(" scan instead, since asserting an exact chosen name would
        // over-constrain a task that deliberately leaves the name to the model.
        Assert.That(calculatorText, Does.Match(@"internal\s+(?:static\s+)?decimal\s+\w+\s*\("),
            $"The method extracted in step 1 should have its accessibility raised to internal (found no " +
            $"'internal decimal SomeMethod(' in OrderPricingCalculator.cs). Transcript: {result.TranscriptPath}");
        Assert.That(calculatorText, Does.Not.Match(@"private\s+(?:static\s+)?decimal\s+(?!CalculateDiscountedTotal\b)\w+\s*\("),
            $"The extracted discount method should no longer be private. Transcript: {result.TranscriptPath}");

        // Unrelated code must still BEHAVE correctly — checked by running the fixture's own real
        // test project (ContosoOrders.Tests), whose OrderCheckoutTests front door exercises
        // GetFinalPrice -> the renamed/extracted calculator logic underneath it, rather than
        // scanning for byte-for-byte/whitespace-collapsed-unchanged text (see
        // docs/current/modeleval_fixture_test_suite_redesign.md). Asserting the total count is
        // unchanged (not just Failed == 0) closes the loophole where a model "fixes" a failing test
        // by deleting or [Fact(Skip=...)]-ing it instead of fixing the code.
        var testProjectPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Tests", "ContosoOrders.Tests.csproj");
        var postRunTestResult = await DotnetTestRunner.RunAsync(testProjectPath, TestContext.CurrentContext.CancellationToken);
        Assert.That(postRunTestResult.Failed, Is.EqualTo(0),
            $"ContosoOrders.Tests should have zero failures after the model's refactor (baseline: " +
            $"{_testBaseline.Passed}/{_testBaseline.Total} passed; after: {postRunTestResult.Passed}/{postRunTestResult.Total} passed). " +
            $"Transcript: {result.TranscriptPath}\n{postRunTestResult.RawOutput}");
        Assert.That(postRunTestResult.Total, Is.EqualTo(_testBaseline.Total),
            $"ContosoOrders.Tests' total test count should be unchanged (baseline: {_testBaseline.Total}, " +
            $"after: {postRunTestResult.Total}) — a dropped count means a test was deleted or disabled " +
            $"instead of the underlying code being fixed. Transcript: {result.TranscriptPath}");

        // DescribeOrder/SummarizeShipping have no front door (nothing calls them), so they fall back
        // to trivia-insensitive structural equivalence — legitimate reformatting (e.g. normalizing
        // the fixture's deliberately odd spacing) is fine, only a change to signature/behavior counts.
        UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(
            calculatorPath, "DescribeOrder", OrderPricingRefactorReproducer.StartingCalculatorFileContent);
        UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(
            calculatorPath, "SummarizeShipping", OrderPricingRefactorReproducer.StartingCalculatorFileContent);

        AgentToolErrorAssertions.AssertWithinBudget(result, maxTotal: 8, maxPerTool: 4);

        // Text-scan checks above can't catch code that compiles but is functionally broken (e.g. the
        // preferred-customer 1.1x scaling silently dropped during extraction) — build and
        // reflection-invoke the real method with both branches to confirm behavior is preserved.
        var coreProjectDirectory = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core");
        var (preferredResult, standardResult) = await FunctionalFixVerifier.InvokeCalculateDiscountedTotalAsync(
            coreProjectDirectory, amount: 200m, rate: 0.1m, TestContext.CurrentContext.CancellationToken);

        Assert.That(preferredResult, Is.EqualTo(200m - (200m * 0.1m * 1.1m)).Within(0.001m),
            $"CalculateDiscountedTotal(200, 0.1, isPreferredCustomer: true) should still apply the 1.1x " +
            $"scaling after refactoring. Transcript: {result.TranscriptPath}");
        Assert.That(standardResult, Is.EqualTo(200m - (200m * 0.1m)).Within(0.001m),
            $"CalculateDiscountedTotal(200, 0.1, isPreferredCustomer: false) should still compute the " +
            $"standard discount after refactoring. Transcript: {result.TranscriptPath}");
    }
}

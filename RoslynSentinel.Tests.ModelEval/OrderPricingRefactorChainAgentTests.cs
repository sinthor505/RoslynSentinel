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
/// Escalation ladder over <see cref="OrderPricingRefactorAgentTests"/>: each fixture in this file
/// takes that test's 3 refactor steps (extract/rename/accessibility) and adds exactly one more
/// ordinary, idiomatic refactor on top, one rung at a time, so pass rate can be plotted against
/// step count without also varying step "kind" (still no bug diagnosis, still no scale/entanglement
/// change — see the finding that idiomatic refactors succeed where symptom-driven bug fixes in
/// <see cref="WholeFileRewriteAgentTests"/> do not). Each rung reuses the prior rung's starting
/// fixture content and prompt, appending one step:
///
/// - Rung 1 (4 steps, <see cref="Model_AppliesFourChainedRefactors"/>): + inline a trivial,
///   single-use local variable directly into its return statement.
/// - Rung 2 (5 steps, <see cref="Model_AppliesFiveChainedRefactors"/>): + rename a parameter
///   (`rate` to `discountRate`) across its declaration and all uses within the method.
/// - Rung 3 (6 steps, <see cref="Model_AppliesSixChainedRefactors"/>): + add a guard clause
///   (reject a negative discount rate) at the top of the renamed method — the one rung where a new
///   *behavior* is introduced rather than a pure structural change, so the prompt explicitly scopes
///   the "preserve existing behavior" constraint to non-negative rates to avoid a repeat of
///   <see cref="OrderPricingRefactorAgentTests"/>'s step-1 wording ambiguity.
/// - Rung 4 (7 steps, <see cref="Model_AppliesSevenChainedRefactors"/>): + extract an interface
///   (`IOrderPricingCalculator`) and have `OrderCheckout` depend on the interface type instead of
///   the concrete class.
///
/// Each rung is its own prompt/fixture/assertion method rather than one parameterized test, since
/// the starting and expected-ending source text genuinely differs at each rung and a shared
/// approach would obscure exactly which step failed in a partial-credit result.
/// </summary>
[TestFixture]
public class OrderPricingRefactorChainAgentTests
{
    // "Refactor" and "Workspace" are the exact mode strings AddRoslynSentinelToolsBasic checks —
    // together they register ApplyDiff/Build/ReadFile/SearchSolutionText/ListSolutionItems plus
    // SentinelRefactoringTools/SentinelAugmentTools (ExtractMethodSafe, RenameSymbol,
    // ChangeAccessibility, ModifyModifier, SyncInterface), without pulling in Advanced's larger
    // tool catalog.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace",
    };

    // Toggle for an isolation experiment: with WriteFile blocked, the model must use ApplyDiff/
    // ApplyUnifiedDiff/RenameSymbol/ChangeSignature for every edit, so the rename-desync failure
    // mode found in the 2026-09-05 ladder batch (see
    // project_sequential_edit_habit_vs_compiler_checks_theory memory) can be compared with and
    // without the whole-file-rewrite escape hatch available, isolating whether it's WriteFile
    // specifically that invites the failure or whether the same pattern just resurfaces via
    // ApplyDiff regardless. Flip to true, rebuild, and rerun the same rungs for the comparison
    // batch; leave false for normal runs.
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
        //services.AddRoslynSentinelEnginesBasic(); // Temporarily commented out to give the model access to the full Advanced tool catalog as ExtractInterface is not in the Basic catalog and is required for rung 7.
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        if (BlockWriteFile)
        {
            // Mirrors PlanOnlyAgentTests' filter shape, blocking just WriteFile instead of every
            // mutating tool — the model still has ApplyDiff/ApplyUnifiedDiff/RenameSymbol/
            // ChangeSignature/etc., so this isolates the one specific tool rather than reverting to
            // a read-only exercise.
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

    // ============================================================================================
    // Rung 1 (4 steps): the 3 base steps + inline a trivial single-use local variable.
    // ============================================================================================

    private const string FourStepUserPromptTemplate = """
        # Task: Four small refactors in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below — use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `CalcDisc` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply all four)

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

        4. **Inline**: in the standard-customer branch of `CalculateDiscountedTotal` (formerly
           `CalcDisc`), the local variable `standardDiscount` is used exactly once, immediately
           after it's declared, purely to hold the result of your extracted method before
           subtracting it. Remove that local variable and subtract the extracted method's call
           result directly in the `return` statement instead — the preferred-customer branch's
           `discount` local should be left exactly as it is; only the standard-customer branch's
           local is being inlined.

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
        4. Is the `standardDiscount` local variable actually gone from the standard-customer branch,
           with the extracted method's call result subtracted directly in the `return` statement? Is
           the preferred-customer branch's `discount` local still present, untouched?

        If re-reading the code reveals any of the above isn't true, fix it now before reporting —
        do not report success based on what you intended to do.

        Report what you changed and the verification result.
        """;

    [Test]
    public async Task Model_AppliesFourChainedRefactors()
    {
        var result = await RunOnceAsync(FourStepUserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertThreeBaseStepsApplied(result);

        var calculatorText = File.ReadAllText(CalculatorPath);
        Assert.That(calculatorText, Does.Not.Match(@"\bstandardDiscount\b"),
            $"Step 4: the single-use 'standardDiscount' local should be inlined into its return statement, not left in place. Transcript: {result.TranscriptPath}");

        await AssertFunctionalBehaviorPreserved(result);
    }

    // ============================================================================================
    // Rung 2 (5 steps): rung 1's 4 steps + rename parameter `rate` to `discountRate`.
    // ============================================================================================

    private const string FiveStepUserPromptTemplate = """
        # Task: Five small refactors in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below — use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `CalcDisc` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply all five)

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

        4. **Inline**: in the standard-customer branch of `CalculateDiscountedTotal` (formerly
           `CalcDisc`), the local variable `standardDiscount` is used exactly once, immediately
           after it's declared, purely to hold the result of your extracted method before
           subtracting it. Remove that local variable and subtract the extracted method's call
           result directly in the `return` statement instead — the preferred-customer branch's
           `discount` local should be left exactly as it is; only the standard-customer branch's
           local is being inlined.

        5. **Rename a parameter**: rename the `rate` parameter of `CalculateDiscountedTotal` to
           `discountRate`. Update every use of it inside `CalculateDiscountedTotal` (including in
           the call to your extracted method from step 1) to the new name. Leave the extracted
           method's own parameter name(s) exactly as you already chose them in step 1 — this step
           only renames `CalculateDiscountedTotal`'s own parameter, not the extracted method's
           signature. `OrderCheckout.cs` calls this method positionally (not with named arguments),
           so its call site does not need to change for this step.

        ## Constraints

        - Do not change the logic of `DescribeOrder` or `SummarizeShipping` in
          `OrderPricingCalculator.cs`, or anything in `OrderCheckout.cs` other than the one call
          site that must follow the rename in step 2 — reformatting is fine, but their behavior
          must stay identical.
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
        4. Is the `standardDiscount` local variable actually gone from the standard-customer branch,
           with the extracted method's call result subtracted directly in the `return` statement? Is
           the preferred-customer branch's `discount` local still present, untouched?
        5. Is `CalculateDiscountedTotal`'s own parameter actually named `discountRate` now (not
           `rate`), at its declaration AND at every use inside the method, including inside the call
           to your extracted method from step 1? Did the extracted method's OWN parameter name(s)
           stay exactly as you chose them in step 1 — this rename only touches
           `CalculateDiscountedTotal`'s parameter, not the extracted method's.

        If re-reading the code reveals any of the above isn't true, fix it now before reporting —
        do not report success based on what you intended to do.

        Report what you changed and the verification result.
        """;

    [Test]
    public async Task Model_AppliesFiveChainedRefactors()
    {
        var result = await RunOnceAsync(FiveStepUserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertThreeBaseStepsApplied(result);

        var calculatorText = File.ReadAllText(CalculatorPath);
        Assert.That(calculatorText, Does.Not.Match(@"\bstandardDiscount\b"),
            $"Step 4: the single-use 'standardDiscount' local should be inlined into its return statement, not left in place. Transcript: {result.TranscriptPath}");

        // Step 5: CalculateDiscountedTotal's own parameter must be renamed. Anchored to the method
        // declaration specifically (not a solution-wide text search) so a leftover "rate" identifier
        // inside the extracted method from step 1 — which step 5 explicitly says NOT to rename —
        // isn't mistaken for an incomplete rename here.
        var calculateDiscountedTotalDeclaration = System.Text.RegularExpressions.Regex.Match(
            calculatorText, @"decimal\s+CalculateDiscountedTotal\s*\(([^)]*)\)");
        Assert.That(calculateDiscountedTotalDeclaration.Success, Is.True,
            $"Could not find CalculateDiscountedTotal's declaration to check its parameter names. Transcript: {result.TranscriptPath}");
        var parameterList = calculateDiscountedTotalDeclaration.Groups[1].Value;
        Assert.That(parameterList, Does.Match(@"\bdiscountRate\b"),
            $"Step 5: CalculateDiscountedTotal's 'rate' parameter should be renamed to 'discountRate'. Transcript: {result.TranscriptPath}");
        Assert.That(parameterList, Does.Not.Match(@"(?<!discount)\brate\b"),
            $"Step 5: CalculateDiscountedTotal should no longer have a parameter literally named 'rate' after the rename. Transcript: {result.TranscriptPath}");

        await AssertFunctionalBehaviorPreserved(result);
    }

    // ============================================================================================
    // Rung 3 (6 steps): rung 2's 5 steps + a guard clause rejecting a negative discount rate.
    //
    // This is the one rung that adds new *behavior* rather than a pure structural change, so the
    // wording explicitly scopes "preserve existing behavior" to non-negative rates — closing off
    // the exact ambiguity class that caused OrderPricingRefactorAgentTests's step-1 failure (see
    // its class doc comment): here the risk isn't "which reading of the sentence is intended," but
    // whether an unscoped "don't change behavior" instruction fights the also-present "add a guard
    // that changes behavior for negative input" instruction.
    // ============================================================================================

    private const string SixStepUserPromptTemplate = """
        # Task: Six small refactors in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below — use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `CalcDisc` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply all six)

        1. **Extract**: both branches of `CalcDisc` repeat the exact expression `amount * rate` —
           factor only that expression out into its own new private method on the same class (it
           should take `amount` and `rate` and return their product), and have both branches call
           your new method instead of repeating `amount * rate` inline — this includes the
           preferred-customer branch, which still applies its 1.1x scaling on top of the
           extracted call. The `* 1.1m` scaling factor is the only part of that branch that stays
           in `CalcDisc` and does not move into the new method.

        2. **Rename**: Rename `CalcDisc` to `CalculateDiscountedTotal`. This method is called from
           `OrderCheckout.cs` — that call site must also be updated to the new name; a rename that
           only changes the method's declaration and misses its caller is not complete.

        3. **Change accessibility**: Change the new method you extracted in step 1 from `private`
           to `internal`, so other classes in the same project could call it directly if needed.

        4. **Inline**: in the standard-customer branch of `CalculateDiscountedTotal` (formerly
           `CalcDisc`), the local variable `standardDiscount` is used exactly once, immediately
           after it's declared, purely to hold the result of your extracted method before
           subtracting it. Remove that local variable and subtract the extracted method's call
           result directly in the `return` statement instead — the preferred-customer branch's
           `discount` local should be left exactly as it is; only the standard-customer branch's
           local is being inlined.

        5. **Rename a parameter**: rename the `rate` parameter of `CalculateDiscountedTotal` to
           `discountRate`. Update every use of it inside `CalculateDiscountedTotal` (including in
           the call to your extracted method from step 1) to the new name. Leave the extracted
           method's own parameter name(s) exactly as you already chose them in step 1. `OrderCheckout.cs`
           calls this method positionally (not with named arguments), so its call site does not need
           to change for this step.

        6. **Add a guard clause**: at the very top of `CalculateDiscountedTotal`'s body, before any
           existing logic, add a check that throws `System.ArgumentOutOfRangeException` if
           `discountRate` is negative (i.e. `discountRate < 0`). This is a deliberate, intentional
           behavior change for negative rates only — do not try to preserve the old behavior for
           negative input, there was no meaningful old behavior for negative input to preserve.

        ## Constraints

        - Do not change the logic of `DescribeOrder` or `SummarizeShipping` in
          `OrderPricingCalculator.cs`, or anything in `OrderCheckout.cs` other than the one call
          site that must follow the rename in step 2 — reformatting is fine, but their behavior
          must stay identical.
        - For every NON-negative `discountRate`, `CalculateDiscountedTotal` (formerly `CalcDisc`)
          must keep producing exactly the same output it did before your changes. The only
          observable behavior change anywhere in this task is the new exception for negative
          `discountRate`, introduced solely by step 6.
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
        4. Is the `standardDiscount` local variable actually gone from the standard-customer branch,
           with the extracted method's call result subtracted directly in the `return` statement? Is
           the preferred-customer branch's `discount` local still present, untouched?
        5. Is `CalculateDiscountedTotal`'s own parameter actually named `discountRate` now (not
           `rate`), at its declaration AND at every use inside the method, including inside the call
           to your extracted method from step 1? Did the extracted method's OWN parameter name(s)
           stay exactly as you chose them in step 1 — this rename only touches
           `CalculateDiscountedTotal`'s parameter, not the extracted method's.
        6. Is the negative-`discountRate` guard clause actually the FIRST thing in the method body,
           before any of the other logic — not appended after, and not skipped entirely?

        If re-reading the code reveals any of the above isn't true, fix it now before reporting —
        do not report success based on what you intended to do.

        Report what you changed and the verification result.
        """;

    [Test]
    public async Task Model_AppliesSixChainedRefactors()
    {
        var result = await RunOnceAsync(SixStepUserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertThreeBaseStepsApplied(result);

        var calculatorText = File.ReadAllText(CalculatorPath);
        Assert.That(calculatorText, Does.Not.Match(@"\bstandardDiscount\b"),
            $"Step 4: the single-use 'standardDiscount' local should be inlined into its return statement, not left in place. Transcript: {result.TranscriptPath}");

        var calculateDiscountedTotalMatch = System.Text.RegularExpressions.Regex.Match(
            calculatorText, @"decimal\s+CalculateDiscountedTotal\s*\(([^)]*)\)\s*\{");
        Assert.That(calculateDiscountedTotalMatch.Success, Is.True,
            $"Could not find CalculateDiscountedTotal's declaration. Transcript: {result.TranscriptPath}");
        var parameterList = calculateDiscountedTotalMatch.Groups[1].Value;
        Assert.That(parameterList, Does.Match(@"\bdiscountRate\b"),
            $"Step 5: CalculateDiscountedTotal's 'rate' parameter should be renamed to 'discountRate'. Transcript: {result.TranscriptPath}");
        Assert.That(parameterList, Does.Not.Match(@"(?<!discount)\brate\b"),
            $"Step 5: CalculateDiscountedTotal should no longer have a parameter literally named 'rate'. Transcript: {result.TranscriptPath}");

        // Step 6: a guard clause throwing ArgumentOutOfRangeException must exist, textually anchored
        // inside CalculateDiscountedTotal's own body rather than solution-wide, since a model could
        // satisfy a bare text search by throwing from an unrelated method.
        var bodyStart = calculateDiscountedTotalMatch.Index + calculateDiscountedTotalMatch.Length;
        var bodyEnd = FindMatchingBrace(calculatorText, bodyStart - 1);
        var methodBody = calculatorText.Substring(bodyStart, bodyEnd - bodyStart);
        Assert.That(methodBody, Does.Match(@"discountRate\s*<\s*0"),
            $"Step 6: expected a guard checking 'discountRate < 0' inside CalculateDiscountedTotal. Transcript: {result.TranscriptPath}");
        Assert.That(methodBody, Does.Match(@"ArgumentOutOfRangeException"),
            $"Step 6: expected the guard to throw ArgumentOutOfRangeException. Transcript: {result.TranscriptPath}");

        AgentToolErrorAssertions.AssertWithinBudget(result, maxTotal: 8, maxPerTool: 4);

        var coreProjectDirectory = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core");
        var (preferredResult, standardResult) = await FunctionalFixVerifier.InvokeCalculateDiscountedTotalAsync(
            coreProjectDirectory, amount: 200m, rate: 0.1m, TestContext.CurrentContext.CancellationToken);
        Assert.That(preferredResult, Is.EqualTo(200m - (200m * 0.1m * 1.1m)).Within(0.001m),
            $"CalculateDiscountedTotal(200, 0.1, isPreferredCustomer: true) should still apply the 1.1x scaling for a valid rate. Transcript: {result.TranscriptPath}");
        Assert.That(standardResult, Is.EqualTo(200m - (200m * 0.1m)).Within(0.001m),
            $"CalculateDiscountedTotal(200, 0.1, isPreferredCustomer: false) should still compute the standard discount for a valid rate. Transcript: {result.TranscriptPath}");

        var threwForNegativeRate = await FunctionalFixVerifier.InvokeCalculateDiscountedTotalThrowsAsync(
            coreProjectDirectory, amount: 200m, rate: -0.1m, isPreferredCustomer: false,
            expectedExceptionTypeName: "ArgumentOutOfRangeException",
            TestContext.CurrentContext.CancellationToken);
        Assert.That(threwForNegativeRate, Is.True,
            $"Step 6: calling CalculateDiscountedTotal with a negative rate should throw ArgumentOutOfRangeException at runtime, not just textually in source. Transcript: {result.TranscriptPath}");
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        throw new InvalidOperationException("No matching closing brace found.");
    }

    // ============================================================================================
    // Rung 4 (7 steps): rung 3's 6 steps + extract an IOrderPricingCalculator interface, with
    // OrderCheckout depending on the interface type instead of the concrete class.
    // ============================================================================================

    private const string SevenStepUserPromptTemplate = """
        # Task: Seven small refactors in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below — use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `CalcDisc` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply all seven)

        1. **Extract**: both branches of `CalcDisc` repeat the exact expression `amount * rate` —
           factor only that expression out into its own new private method on the same class (it
           should take `amount` and `rate` and return their product), and have both branches call
           your new method instead of repeating `amount * rate` inline — this includes the
           preferred-customer branch, which still applies its 1.1x scaling on top of the
           extracted call. The `* 1.1m` scaling factor is the only part of that branch that stays
           in `CalcDisc` and does not move into the new method.

        2. **Rename**: Rename `CalcDisc` to `CalculateDiscountedTotal`. This method is called from
           `OrderCheckout.cs` — that call site must also be updated to the new name; a rename that
           only changes the method's declaration and misses its caller is not complete.

        3. **Change accessibility**: Change the new method you extracted in step 1 from `private`
           to `internal`, so other classes in the same project could call it directly if needed.

        4. **Inline**: in the standard-customer branch of `CalculateDiscountedTotal` (formerly
           `CalcDisc`), the local variable `standardDiscount` is used exactly once, immediately
           after it's declared, purely to hold the result of your extracted method before
           subtracting it. Remove that local variable and subtract the extracted method's call
           result directly in the `return` statement instead — the preferred-customer branch's
           `discount` local should be left exactly as it is; only the standard-customer branch's
           local is being inlined.

        5. **Rename a parameter**: rename the `rate` parameter of `CalculateDiscountedTotal` to
           `discountRate`. Update every use of it inside `CalculateDiscountedTotal` (including in
           the call to your extracted method from step 1) to the new name. Leave the extracted
           method's own parameter name(s) exactly as you already chose them in step 1.

        6. **Add a guard clause**: at the very top of `CalculateDiscountedTotal`'s body, before any
           existing logic, add a check that throws `System.ArgumentOutOfRangeException` if
           `discountRate` is negative (i.e. `discountRate < 0`). This is a deliberate, intentional
           behavior change for negative rates only — do not try to preserve the old behavior for
           negative input, there was no meaningful old behavior for negative input to preserve.

        7. **Extract an interface**: create a new interface `IOrderPricingCalculator` (in its own
           new file, `{0}/FixtureHelpers/IOrderPricingCalculator.cs`) containing exactly one member:
           `CalculateDiscountedTotal(decimal amount, decimal discountRate, bool isPreferredCustomer)`
           returning `decimal`. Make `OrderPricingCalculator` implement this interface (only the
           `CalculateDiscountedTotal` method needs to satisfy it — do not add `DescribeOrder` or
           `SummarizeShipping` to the interface). Then change `OrderCheckout`'s `_calculator` field
           in `OrderCheckout.cs` to be declared as `IOrderPricingCalculator` instead of the concrete
           `OrderPricingCalculator` type (the field can still be *constructed* with
           `new OrderPricingCalculator()` — only its declared/static type changes to the interface).

        ## Constraints

        - Do not change the logic of `DescribeOrder` or `SummarizeShipping` in
          `OrderPricingCalculator.cs` — reformatting is fine, but their behavior must stay identical.
        - For every NON-negative `discountRate`, `CalculateDiscountedTotal` must keep producing
          exactly the same output it did before your changes. The only observable behavior change
          anywhere in this task is the new exception for negative `discountRate` from step 6.
        - `OrderCheckout.GetFinalPrice` must still return the same value it did before, for the same
          non-negative inputs — only the declared type of the private field backing it changes.
        - Verify your changes compile, using an MCP tool (you have no terminal access). Scope the
          build to just the `ContosoOrders.Core` project rather than the whole solution.

        ## Before you report done

        Re-read the current, actual contents of `CalculateDiscountedTotal` (formerly `CalcDisc`),
        `OrderCheckout.cs`, and `IOrderPricingCalculator.cs` — do not rely on your memory of the
        edit you intended to make. For each item below, check the real code and answer yourself
        honestly before writing your summary:

        1. Does the standard-customer branch call your new extracted method? Does the
           preferred-customer branch ALSO call it (with `* 1.1m` applied to the call's result), or
           did it get skipped and still compute `amount * rate` (or `amount * rate * 1.1m`) inline?
           Both branches must call the new method — re-read both branches, not just one.
        2. Is the method's declaration renamed to `CalculateDiscountedTotal`, AND is the call site in
           `OrderCheckout.cs` updated to the new name — not just one of the two?
        3. Is the extracted method's accessibility actually `internal` in the file on disk right now,
           not still `private`?
        4. Is the `standardDiscount` local variable actually gone from the standard-customer branch,
           with the extracted method's call result subtracted directly in the `return` statement? Is
           the preferred-customer branch's `discount` local still present, untouched?
        5. Is `CalculateDiscountedTotal`'s own parameter actually named `discountRate` now (not
           `rate`), at its declaration AND at every use inside the method, including inside the call
           to your extracted method from step 1? Did the extracted method's OWN parameter name(s)
           stay exactly as you chose them in step 1 — this rename only touches
           `CalculateDiscountedTotal`'s parameter, not the extracted method's.
        6. Is the negative-`discountRate` guard clause actually the FIRST thing in the method body,
           before any of the other logic — not appended after, and not skipped entirely?
        7. Does `IOrderPricingCalculator.cs` exist with exactly the one member described? Does
           `OrderPricingCalculator` actually implement that interface? Is `OrderCheckout`'s
           `_calculator` field's DECLARED type actually `IOrderPricingCalculator` now, not still the
           concrete `OrderPricingCalculator` type (construction can still use `new
           OrderPricingCalculator()` — only the declared/static type changes)?

        If re-reading the code reveals any of the above isn't true, fix it now before reporting —
        do not report success based on what you intended to do.

        Report what you changed and the verification result.
        """;

    [Test]
    public async Task Model_AppliesSevenChainedRefactors()
    {
        var result = await RunOnceAsync(SevenStepUserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertThreeBaseStepsApplied(result);

        var calculatorText = File.ReadAllText(CalculatorPath);
        var checkoutText = File.ReadAllText(CheckoutPath);

        Assert.That(calculatorText, Does.Not.Match(@"\bstandardDiscount\b"),
            $"Step 4: the single-use 'standardDiscount' local should be inlined. Transcript: {result.TranscriptPath}");

        var calculateDiscountedTotalMatch = System.Text.RegularExpressions.Regex.Match(
            calculatorText, @"decimal\s+CalculateDiscountedTotal\s*\(([^)]*)\)\s*\{");
        Assert.That(calculateDiscountedTotalMatch.Success, Is.True,
            $"Could not find CalculateDiscountedTotal's declaration. Transcript: {result.TranscriptPath}");
        var parameterList = calculateDiscountedTotalMatch.Groups[1].Value;
        Assert.That(parameterList, Does.Match(@"\bdiscountRate\b"),
            $"Step 5: CalculateDiscountedTotal's 'rate' parameter should be renamed to 'discountRate'. Transcript: {result.TranscriptPath}");

        var bodyStart = calculateDiscountedTotalMatch.Index + calculateDiscountedTotalMatch.Length;
        var bodyEnd = FindMatchingBrace(calculatorText, bodyStart - 1);
        var methodBody = calculatorText.Substring(bodyStart, bodyEnd - bodyStart);
        Assert.That(methodBody, Does.Match(@"discountRate\s*<\s*0"),
            $"Step 6: expected a guard checking 'discountRate < 0'. Transcript: {result.TranscriptPath}");
        Assert.That(methodBody, Does.Match(@"ArgumentOutOfRangeException"),
            $"Step 6: expected the guard to throw ArgumentOutOfRangeException. Transcript: {result.TranscriptPath}");

        // Step 7: interface file, class implements it, and OrderCheckout's field is declared as
        // the interface type rather than the concrete class.
        var interfacePath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "IOrderPricingCalculator.cs");
        Assert.That(File.Exists(interfacePath), Is.True,
            $"Step 7: expected a new IOrderPricingCalculator.cs file. Transcript: {result.TranscriptPath}");
        var interfaceText = File.ReadAllText(interfacePath);
        Assert.That(interfaceText, Does.Match(@"interface\s+IOrderPricingCalculator"),
            $"Step 7: expected an IOrderPricingCalculator interface declaration. Transcript: {result.TranscriptPath}");
        Assert.That(interfaceText, Does.Match(@"decimal\s+CalculateDiscountedTotal\s*\("),
            $"Step 7: IOrderPricingCalculator should declare CalculateDiscountedTotal. Transcript: {result.TranscriptPath}");

        Assert.That(calculatorText, Does.Match(@"class\s+OrderPricingCalculator\s*:\s*IOrderPricingCalculator"),
            $"Step 7: OrderPricingCalculator should implement IOrderPricingCalculator. Transcript: {result.TranscriptPath}");

        var fieldMatch = System.Text.RegularExpressions.Regex.Match(
            checkoutText, @"(?:private|internal|protected|public)\s+(?:readonly\s+)?(\w+)\s+_calculator\b");
        Assert.That(fieldMatch.Success, Is.True,
            $"Could not find OrderCheckout's _calculator field declaration. Transcript: {result.TranscriptPath}");
        Assert.That(fieldMatch.Groups[1].Value, Is.EqualTo("IOrderPricingCalculator"),
            $"Step 7: OrderCheckout's _calculator field should be declared as IOrderPricingCalculator, not the concrete class. Transcript: {result.TranscriptPath}");

        AgentToolErrorAssertions.AssertWithinBudget(result, maxTotal: 10, maxPerTool: 4);

        var coreProjectDirectory = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core");
        var (preferredResult, standardResult) = await FunctionalFixVerifier.InvokeCalculateDiscountedTotalAsync(
            coreProjectDirectory, amount: 200m, rate: 0.1m, TestContext.CurrentContext.CancellationToken);
        Assert.That(preferredResult, Is.EqualTo(200m - (200m * 0.1m * 1.1m)).Within(0.001m),
            $"CalculateDiscountedTotal should still apply the 1.1x scaling for a valid rate. Transcript: {result.TranscriptPath}");
        Assert.That(standardResult, Is.EqualTo(200m - (200m * 0.1m)).Within(0.001m),
            $"CalculateDiscountedTotal should still compute the standard discount for a valid rate. Transcript: {result.TranscriptPath}");

        var threwForNegativeRate = await FunctionalFixVerifier.InvokeCalculateDiscountedTotalThrowsAsync(
            coreProjectDirectory, amount: 200m, rate: -0.1m, isPreferredCustomer: false,
            expectedExceptionTypeName: "ArgumentOutOfRangeException",
            TestContext.CurrentContext.CancellationToken);
        Assert.That(threwForNegativeRate, Is.True,
            $"Step 6: negative rate should still throw ArgumentOutOfRangeException through the interface-typed field. Transcript: {result.TranscriptPath}");
    }

    // ============================================================================================
    // Shared helpers
    // ============================================================================================

    private string CalculatorPath =>
        Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "OrderPricingCalculator.cs");

    private string CheckoutPath =>
        Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "OrderCheckout.cs");

    private async Task<AgentRunResult> RunOnceAsync(string userPromptTemplate, CancellationToken cancellationToken)
    {
        // 45min/60 turns, not the base 3-step test's 30min: every added rung is strictly more work
        // on top of the same 3 base steps, and the first Chain5 (5-step) batch run hit the old
        // 30-minute cap after correctly completing 4 of 5 steps, stalling only on a tool-misuse
        // retry for the last one — a step-scaled cap gives real headroom to recover from that kind
        // of retry instead of cutting the run off right as it's converging.
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, turnCap: 60, wallClockCap: TimeSpan.FromMinutes(45),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(userPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        return await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, _runDirectory, cancellationToken);
    }

    /// <summary>
    /// Checks the 3 base steps (extract/rename/accessibility) shared by every rung — identical to
    /// <see cref="OrderPricingRefactorAgentTests.AssertRefactorsApplied"/>'s corresponding checks,
    /// duplicated here (rather than shared) because each rung's assertion method also needs to
    /// check its own additional step(s) inline against the same file reads, and the two test
    /// classes are allowed to diverge independently as each rung's wording gets tuned.
    /// </summary>
    private async Task AssertThreeBaseStepsApplied(AgentRunResult result)
    {
        Assert.That(File.Exists(CalculatorPath), Is.True, "OrderPricingCalculator.cs should still exist after the model's edits.");
        Assert.That(File.Exists(CheckoutPath), Is.True, "OrderCheckout.cs should still exist after the model's edits.");

        var calculatorText = File.ReadAllText(CalculatorPath);
        var checkoutText = File.ReadAllText(CheckoutPath);

        Assert.That(calculatorText, Does.Not.Match(@"\bCalcDisc\b"),
            $"CalcDisc should be fully renamed to CalculateDiscountedTotal in OrderPricingCalculator.cs. Transcript: {result.TranscriptPath}");
        Assert.That(calculatorText, Does.Match(@"\bCalculateDiscountedTotal\b"),
            $"CalculateDiscountedTotal should be defined in OrderPricingCalculator.cs. Transcript: {result.TranscriptPath}");
        Assert.That(checkoutText, Does.Not.Match(@"\bCalcDisc\b"),
            $"OrderCheckout.cs's call site should be updated to the new name, not left calling CalcDisc. Transcript: {result.TranscriptPath}");
        Assert.That(checkoutText, Does.Match(@"\bCalculateDiscountedTotal\b"),
            $"OrderCheckout.cs should call CalculateDiscountedTotal after the rename. Transcript: {result.TranscriptPath}");

        var calculatorCodeOnly = string.Join(
            "\n",
            calculatorText.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var inlineDiscountExpressionCount = System.Text.RegularExpressions.Regex.Matches(
            calculatorCodeOnly, @"amount\s*\*\s*(?:rate|discountRate)").Count;
        Assert.That(inlineDiscountExpressionCount, Is.LessThanOrEqualTo(1),
            $"The 'amount * rate' discount calculation should be extracted into a shared method, not " +
            $"repeated inline in both branches (found {inlineDiscountExpressionCount} occurrences in code). " +
            $"Transcript: {result.TranscriptPath}");

        Assert.That(calculatorText, Does.Match(@"internal\s+(?:static\s+)?decimal\s+\w+\s*\("),
            $"The method extracted in step 1 should have its accessibility raised to internal (found no " +
            $"'internal decimal SomeMethod(' in OrderPricingCalculator.cs). Transcript: {result.TranscriptPath}");
        Assert.That(calculatorText, Does.Not.Match(@"private\s+(?:static\s+)?decimal\s+(?!CalculateDiscountedTotal\b)\w+\s*\("),
            $"The extracted discount method should no longer be private. Transcript: {result.TranscriptPath}");

        // Unrelated code must still BEHAVE correctly — run the fixture's own real test project
        // (ContosoOrders.Tests), whose OrderCheckoutTests front door exercises GetFinalPrice -> the
        // renamed/extracted/interface-wrapped calculator logic underneath it across every rung
        // (OrderCheckout's own constructor/public shape is never touched by any rung's prompt — see
        // docs/current/modeleval_fixture_test_suite_redesign.md), rather than scanning for
        // byte-for-byte/whitespace-collapsed-unchanged text. Asserting the total count is unchanged
        // (not just Failed == 0) closes the loophole where a model "fixes" a failing test by
        // deleting or [Fact(Skip=...)]-ing it instead of fixing the code.
        var postRunTestResult = await DotnetTestRunner.RunAsync(
            Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Tests", "ContosoOrders.Tests.csproj"),
            TestContext.CurrentContext.CancellationToken);
        Assert.That(postRunTestResult.Failed, Is.EqualTo(0),
            $"ContosoOrders.Tests should have zero failures after the model's refactor (baseline: " +
            $"{_testBaseline.Passed}/{_testBaseline.Total} passed; after: {postRunTestResult.Passed}/{postRunTestResult.Total} passed). " +
            $"Transcript: {result.TranscriptPath}\n{postRunTestResult.RawOutput}");
        Assert.That(postRunTestResult.Total, Is.EqualTo(_testBaseline.Total),
            $"ContosoOrders.Tests' total test count should be unchanged (baseline: {_testBaseline.Total}, " +
            $"after: {postRunTestResult.Total}) — a dropped count means a test was deleted or disabled " +
            $"instead of the underlying code being fixed. Transcript: {result.TranscriptPath}");

        // DescribeOrder/SummarizeShipping have no front door (nothing calls them), so they fall back
        // to trivia-insensitive structural equivalence — legitimate reformatting is fine, only a
        // change to signature/behavior counts.
        UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(
            CalculatorPath, "DescribeOrder", OrderPricingRefactorReproducer.StartingCalculatorFileContent);
        UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(
            CalculatorPath, "SummarizeShipping", OrderPricingRefactorReproducer.StartingCalculatorFileContent);

        AgentToolErrorAssertions.AssertWithinBudget(result, maxTotal: 8, maxPerTool: 4);
    }

    private async Task AssertFunctionalBehaviorPreserved(AgentRunResult result)
    {
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

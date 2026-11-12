using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// The method-name miss of MsToolAugmentEngine.ExtractMethodSafeAsync with extractEntireBody, reached through
/// AsyncifyTools.ExtractEventHandlersTool (the only tool that passes extractEntireBody: HandlerExtractTarget.ExtractEntireBody) -
/// plan_not_found_error_orientation, Step 5e slice 4. With extractEntireBody the target's ContextSnippet carries the method name, so the text
/// names that method (not "the snippet"), offers the ranked retry for contextSnippet, and says how the parameter is read. A dry run writes nothing;
/// the fixture file text is compared before and after anyway.
/// </summary>
[TestFixture]
[Category("AsyncifyTools")] // sentinel:auto-category
public class MsToolAugmentExtractMethodMissTests
{
    private const string FileName = "Pricing.cs";

    private const string Source = """
        public class Pricing
        {
            public int ComputeTotal(int rate)
            {
                var doubled = rate * 2;
                return doubled;
            }
        }
        """;

    private IWorkspaceManager _workspaceManager;
    private AsyncifyTools _asyncifyTools;
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "ExtractMissTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var diffEngine = Battery.Basic.TestEngines.DiffEngine();
        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var antiPatternEngine = Battery.Advanced.TestEnginesAdvanced.AntiPatternEngine(_workspaceManager);
        var asyncOptEngine = Battery.Advanced.TestEnginesAdvanced.AsyncOptimizationEngine(_workspaceManager);
        var asyncBatchEngine = Battery.Advanced.TestEnginesAdvanced.AsyncBatchEngine(
            _workspaceManager,
            asyncOptEngine,
            validationEngine,
            antiPatternEngine,
            new MigrationLedger(),
            NullLogger<AsyncBatchEngine>.Instance);

        _asyncifyTools = new AsyncifyTools(
            antiPatternEngine,
            asyncOptEngine,
            asyncBatchEngine,
            Battery.Basic.TestEngines.MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            validationEngine,
            new FailureRouter(ToolGraph.Empty),
            new MigrationLedger(),
            NullLogger<AsyncifyTools>.Instance);

        var path = Path.Combine(_tempDir, FileName);
        File.WriteAllText(path, Source);
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", Path.Combine(_tempDir, "TestProj.csproj"), [(FileName, Source, path)]));
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // Runs one extractEntireBody call whose method name does not exist: one failed item, the file untouched. Returns the item's reason.
    private async Task<string> RunMissAsync(string methodName)
    {
        var path = Path.Combine(_tempDir, FileName);
        var result = await _asyncifyTools.ExtractEventHandlersTool(
            reason: "test message",
            targets:
            [
                new HandlerExtractTarget
                {
                    FilePath = path,
                    NewMethodName = "DoubleIt",
                    ContextSnippet = methodName,
                    ExtractEntireBody = true,
                }
            ],
            dryRun: true);

        Assert.That(result.SuccessData, Is.Not.Null, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.Failed, Is.EqualTo(1));
        Assert.That(File.ReadAllText(path), Is.EqualTo(Source), "A refused lookup must not write the file.");
        return result.SuccessData.Failures[0].Reason;
    }

    [Test, CancelAfter(10000)]
    public async Task ExtractEntireBody_NearMethodName_OffersTheRankedRetryForContextSnippet()
    {
        var reason = await RunMissAsync("ComputeTotl");

        Assert.That(reason, Does.Contain("Method 'ComputeTotl' was not found in"));
        Assert.That(reason, Does.Contain("Did you mean 'ComputeTotal'? Retry with contextSnippet: 'ComputeTotal'."));
        Assert.That(reason, Does.Contain("With extractEntireBody: true, contextSnippet is read as the name of the method whose body is extracted."));
        AssertFactsAndOptionsOnly(reason);
    }

    [Test, CancelAfter(10000)]
    public async Task ExtractEntireBody_NoSimilarMethodName_StatesTheFactAndOffersTheOutline()
    {
        var reason = await RunMissAsync("Qzxvbnm");

        Assert.That(reason, Does.Contain("No method declared in"));
        Assert.That(reason, Does.Contain("has a similar name to 'Qzxvbnm'"));
        Assert.That(reason, Does.Contain("GetFileOutline("));
        Assert.That(reason, Does.Contain("With extractEntireBody: true, contextSnippet is read as the name of the method whose body is extracted."));
        AssertFactsAndOptionsOnly(reason);
    }
}

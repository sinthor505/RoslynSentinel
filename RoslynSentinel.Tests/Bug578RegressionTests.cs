using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Regression tests for Bug 5 (BlockingTaskWait enum false positive),
/// Bug 6 (unused interfaces NuGet false positives), Bug 7 (AdvanceToLastIdentifier),
/// and Bug 8 (generate_fluent_builder record support).
/// </summary>
[TestFixture]
public class Bug578RegressionTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private CodeGenerationEngine _codeGenerationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 5 (enum) / Bug 8 (Task.Wait): BoundedChannelFullMode.Wait false positive
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DetectAntiPatterns_BoundedChannelFullModeWait_IsNotFlaggedAsBlockingCall()
    {
        // BoundedChannelFullMode.Wait is an enum value, NOT a Task.Wait() blocking call.
        // Before the fix, the semantic guard only applied to ".Result", so ".Wait" on
        // any expression (including enums) would be flagged incorrectly.
        const string source = """
            using System.Threading.Channels;
            using System.Threading.Tasks;

            namespace App;
            public class MyWorker
            {
                private readonly Channel<string> _channel = Channel.CreateBounded<string>(
                    new BoundedChannelOptions(100)
                    {
                        FullMode = BoundedChannelFullMode.Wait
                    });

                public async Task DoWorkAsync()
                {
                    await _channel.Writer.WriteAsync("msg");
                }
            }
            """;

        SetSource(source, "MyWorker.cs");
        var antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        var findings = await antiPatternEngine.DetectAntiPatternsAsync("MyWorker.cs");

        var blockingFindings = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();
        Assert.That(blockingFindings, Is.Empty,
            $"BoundedChannelFullMode.Wait (enum) must NOT be flagged as a blocking Task.Wait() call. " +
            $"Found: {string.Join(", ", blockingFindings.Select(f => f.Description))}");
    }

    [Test]
    public async Task DetectAntiPatterns_TaskDotWait_IsFlaggedAsBlockingCall()
    {
        // Confirm real Task.Wait() is still correctly flagged.
        const string source = """
            using System.Threading.Tasks;

            namespace App;
            public class BlockingService
            {
                public void SyncMethod()
                {
                    Task.Delay(100).Wait();
                }
            }
            """;

        SetSource(source, "BlockingService.cs");
        var antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        var findings = await antiPatternEngine.DetectAntiPatternsAsync("BlockingService.cs");

        var blockingFindings = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();
        Assert.That(blockingFindings, Is.Not.Empty,
            "Task.Wait() on a real Task must still be flagged as a blocking anti-pattern");
    }

    [Test]
    public async Task DetectAntiPatterns_TaskResultAccess_IsFlaggedAsBlockingCall()
    {
        // Confirm .Result is still flagged.
        const string source = """
            using System.Threading.Tasks;

            namespace App;
            public class BlockingService
            {
                public string GetValue()
                {
                    return Task.FromResult("x").Result;
                }
            }
            """;

        SetSource(source, "ResultService.cs");
        var antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        var findings = await antiPatternEngine.DetectAntiPatternsAsync("ResultService.cs");

        var blockingFindings = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();
        Assert.That(blockingFindings, Is.Not.Empty,
            ".Result on a Task must still be flagged as a blocking anti-pattern");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 8: GenerateFluentBuilder record support
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GenerateFluentBuilder_ForRecordWithPrimaryConstructor_GeneratesBuilder()
    {
        const string source = """
            namespace App;
            public record CreateOrderRequest(string ProductName, int Quantity, decimal Price);
            """;

        SetSource(source, "CreateOrderRequest.cs");
        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync(
            "CreateOrderRequest.cs", "CreateOrderRequest");

        Assert.That(result.BuilderCode, Does.Contain("CreateOrderRequestBuilder"),
            "Should generate a builder class");
        Assert.That(result.BuilderCode, Does.Contain("WithProductName"),
            "Should generate WithProductName setter from primary constructor parameter");
        Assert.That(result.BuilderCode, Does.Contain("WithQuantity"),
            "Should generate WithQuantity setter");
        Assert.That(result.BuilderCode, Does.Contain("WithPrice"),
            "Should generate WithPrice setter");
        Assert.That(result.BuilderCode, Does.Contain("Build()"),
            "Should generate Build() method");
    }

    [Test]
    public async Task GenerateFluentBuilder_ForRegularClass_StillWorks()
    {
        const string source = """
            namespace App;
            public class UserProfile
            {
                public string Name { get; set; } = "";
                public string Email { get; set; } = "";
                public int Age { get; set; }
            }
            """;

        SetSource(source, "UserProfile.cs");
        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync(
            "UserProfile.cs", "UserProfile");

        Assert.That(result.BuilderCode, Does.Contain("UserProfileBuilder"),
            "Should still generate builder class for regular class");
        Assert.That(result.BuilderCode, Does.Contain("WithName"), "Should have WithName");
        Assert.That(result.BuilderCode, Does.Contain("WithEmail"), "Should have WithEmail");
        Assert.That(result.BuilderCode, Does.Contain("Build()"), "Should have Build()");
    }
}

// Battery #33 — Regression tests for:
//   Bug #1: CheckForUnusedEventSubscriptions false positives on sql += / numeric +=
//   Bug #2: DetectMismatchedAwait false positives on new ValueTask<T>(asyncMethod(...)) patterns
//
// Bug #1 root cause: += on any identifier was flagged as an event subscription because the tool
//   lacked an IEventSymbol semantic check. Fix: acquire semantic model, skip if left-hand side
//   symbol is not IEventSymbol.
//
// Bug #2 root cause: the skip condition for "invocation is the lambda body" only matched when
//   the invocation's direct parent was the lambda expression. For the pattern
//     ct => new ValueTask<T>(FetchAsync(id, ct))
//   the invocation's parent chain is InvocationExpression → ArgumentSyntax →
//   ArgumentListSyntax → ObjectCreationExpressionSyntax → Lambda, so the direct parent is
//   ArgumentSyntax, not the lambda. Fix: also skip when invocation.Parent is ArgumentSyntax
//   inside an ObjectCreationExpressionSyntax whose type contains "ValueTask".

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryThirtyThreeTests
{
    // ── engines for SentinelIntelligenceTools (Bug #1) ─────────────────────────
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ImpactAnalyzer _impactAnalyzer;
    private SemanticSearchEngine _semanticSearchEngine;
    private MetricsEngine _metricsEngine;
    private InventoryEngine _inventoryEngine;
    private DeadCodeEngine _deadCodeEngine;
    private AnalysisEngine _analysisEngine;
    private DocumentationEngine _documentationEngine;
    private DependencyEngine _dependencyEngine;
    private ProjectStructureEngine _projectStructureEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private HealthOrchestrationEngine _healthOrchestrationEngine;
    private ArchitecturalEngine _architecturalEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private DependencyInjectionEngine _dependencyInjectionEngine;
    private DiscoveryEngine _discoveryEngine;
    private SentinelIntelligenceTools _intelligenceTools;

    // ── additional engines for SentinelQualityTools (Bug #2) ───────────────────
    private PerformanceEngine _performanceEngine;
    private SecurityEngine _securityEngine;
    private TestingEngine _testingEngine;
    private ControlFlowEngine _controlFlowEngine;
    private LogicOptimizationEngine _logicOptimizationEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private AsyncBatchEngine _asyncBatchEngine;
    private DiagnosticEngine _diagnosticEngine;
    private SentinelQualityTools _qualityTools;
    private DiffEngine _diffEngine;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();

        _impactAnalyzer = new ImpactAnalyzer(NullLogger<ImpactAnalyzer>.Instance, _workspaceManager);
        _semanticSearchEngine = new SemanticSearchEngine(_workspaceManager);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
        _documentationEngine = new DocumentationEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager, _config);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _healthOrchestrationEngine = new HealthOrchestrationEngine(
            _workspaceManager, _projectStructureEngine, _analysisEngine, _asyncSafetyEngine, _config);
        _architecturalEngine = new ArchitecturalEngine(_workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager, _symbolNavigationEngine);

        _intelligenceTools = new SentinelIntelligenceTools(
            _impactAnalyzer, _semanticSearchEngine, _metricsEngine, _inventoryEngine,
            _deadCodeEngine, _analysisEngine, _documentationEngine, _dependencyEngine,
            _projectStructureEngine, _asyncSafetyEngine, _healthOrchestrationEngine,
            _architecturalEngine, _symbolNavigationEngine, _dependencyInjectionEngine,
            _discoveryEngine, new ProjectConsistencyEngine(_workspaceManager), new BreakingChangeEngine(_workspaceManager),
            new CloneDetectionEngine(_workspaceManager),
            _workspaceManager,
            _config, NullLogger<SentinelIntelligenceTools>.Instance);

        _performanceEngine = new PerformanceEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _testingEngine = new TestingEngine(_workspaceManager);
        _controlFlowEngine = new ControlFlowEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _diffEngine = new DiffEngine(_workspaceManager);
        _asyncBatchEngine = new AsyncBatchEngine(_workspaceManager, _asyncOptimizationEngine, new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, _diffEngine), new AntiPatternEngine(_workspaceManager), new MigrationLedger(), NullLogger<AsyncBatchEngine>.Instance);

        _qualityTools = new SentinelQualityTools(
            _performanceEngine, _securityEngine, _testingEngine, _controlFlowEngine,
            _logicOptimizationEngine, _analysisEngine, _asyncSafetyEngine,
            new AntiPatternEngine(_workspaceManager), _asyncOptimizationEngine,
            new ThreadSafetyEngine(_workspaceManager), _diagnosticEngine,
            new CodeStyleAnalysisEngine(_workspaceManager),
            new PathDrivenTestEngine(_workspaceManager),
            new StackOverflowEngine(_workspaceManager),
            _asyncBatchEngine, _workspaceManager, NullLogger<SentinelQualityTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BUG #1 — CheckForUnusedEventSubscriptions false positives
    // ═══════════════════════════════════════════════════════════════════════════

    // Mirrors the real ProductRepository.cs pattern that triggered the bug:
    //   sql += " AND Name LIKE @Name"
    private const string SqlConcatSource = @"
using System.Threading;
using System.Threading.Tasks;
namespace TestProj;

public class QueryBuilder
{
    public async Task<string> BuildQueryAsync(string name, CancellationToken ct = default)
    {
        var sql = ""SELECT * FROM Product WHERE IsDeleted = 0"";
        if (name != null)
            sql += "" AND Name LIKE @Name"";
        sql += "" ORDER BY Name"";
        return await Task.FromResult(sql);
    }
}";

    private const string NumericPlusEqualsSource = @"
namespace TestProj;

public class Counter
{
    public int GetSum(int[] values)
    {
        int total = 0;
        foreach (var v in values)
            total += v;
        return total;
    }
}";

    private const string RealEventSource = @"
using System;
namespace TestProj;

public class Button
{
    public event EventHandler Click;
    protected virtual void OnClick(EventArgs e) => Click?.Invoke(this, e);
}

public class SubscriberService
{
    private readonly Button _button = new Button();

    public void Subscribe()
    {
        _button.Click += HandleClick;   // subscribed but never unsubscribed
    }

    private void HandleClick(object sender, EventArgs e) { }
}";

    [Test]
    public async Task CheckForUnusedEventSubscriptions_SqlStringConcatenation_NoFalsePositives()
    {
        SetSource(SqlConcatSource, "QueryBuilder.cs");
        var results = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("QueryBuilder.cs");

        Assert.That(results, Is.Not.Null);
        Assert.That(
            results.Any(r => r.Type == "EventSubscriptionWithoutUnsubscription"),
            Is.False,
            "sql += string concatenation must NOT be flagged as EventSubscriptionWithoutUnsubscription");
    }

    [Test]
    public async Task CheckForUnusedEventSubscriptions_NumericPlusEquals_NoFalsePositives()
    {
        SetSource(NumericPlusEqualsSource, "Counter.cs");
        var results = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Counter.cs");

        Assert.That(results, Is.Not.Null);
        Assert.That(
            results.Any(r => r.Type == "EventSubscriptionWithoutUnsubscription"),
            Is.False,
            "Numeric += accumulation must NOT be flagged as EventSubscriptionWithoutUnsubscription");
    }

    [Test]
    public async Task CheckForUnusedEventSubscriptions_RealEventSubscription_IsDetected()
    {
        SetSource(RealEventSource, "Subscriber.cs");
        var results = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Subscriber.cs");

        Assert.That(results, Is.Not.Null);
        Assert.That(
            results.Any(r => r.Type == "EventSubscriptionWithoutUnsubscription"),
            Is.True,
            "A real += event subscription with no matching -= MUST be reported");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BUG #2 — DetectMismatchedAwait false positives
    // ═══════════════════════════════════════════════════════════════════════════

    // Mirrors the real ProductRepository.cs pattern that triggered the bug:
    //   _cache.GetOrSetAsync(key, ct => new ValueTask<ProductDto?>(GetByIdFromDbAsync(id, ct)), ...)
    private const string ValueTaskWrapperSource = @"
using System;
using System.Threading;
using System.Threading.Tasks;
namespace TestProj;

public class CacheService
{
    public Task<string?> GetOrSetAsync(
        string key,
        Func<CancellationToken, ValueTask<string?>> factory,
        CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public async Task<string?> GetItemAsync(string id, CancellationToken ct = default)
    {
        return await GetOrSetAsync(
            id,
            ct2 => new ValueTask<string?>(FetchFromDbAsync(id, ct2)),
            ct);
    }

    private async Task<string?> FetchFromDbAsync(string id, CancellationToken ct)
        => await Task.FromResult(id);
}";

    private const string TrulyUnawaitedSource = @"
using System.Threading.Tasks;
namespace TestProj;

public class FireForgetService
{
    public async Task PerformWorkAsync()
    {
        DoBackgroundWorkAsync();   // deliberately not awaited
        await Task.CompletedTask;
    }

    private Task DoBackgroundWorkAsync() => Task.CompletedTask;
}";

    // Three separate ValueTask<T> wrapper calls — all must be clean after the fix
    private const string MultipleValueTaskWrappersSource = @"
using System;
using System.Threading;
using System.Threading.Tasks;
namespace TestProj;

public class MultiCacheService
{
    public Task<string?> GetOrSetAsync(
        string key,
        Func<CancellationToken, ValueTask<string?>> factory,
        CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public async Task<string?> GetUserAsync(string id, CancellationToken ct = default)
        => await GetOrSetAsync(id, ct2 => new ValueTask<string?>(FetchUserAsync(id, ct2)), ct);

    public async Task<string?> GetProductAsync(string id, CancellationToken ct = default)
        => await GetOrSetAsync(id, ct2 => new ValueTask<string?>(FetchProductAsync(id, ct2)), ct);

    public async Task<string?> GetCategoryAsync(string id, CancellationToken ct = default)
        => await GetOrSetAsync(id, ct2 => new ValueTask<string?>(FetchCategoryAsync(id, ct2)), ct);

    private async Task<string?> FetchUserAsync(string id, CancellationToken ct) => await Task.FromResult(id);
    private async Task<string?> FetchProductAsync(string id, CancellationToken ct) => await Task.FromResult(id);
    private async Task<string?> FetchCategoryAsync(string id, CancellationToken ct) => await Task.FromResult(id);
}";

    [Test]
    public async Task DetectMismatchedAwait_ValueTaskWrapper_NoFalsePositives()
    {
        SetSource(ValueTaskWrapperSource, "CacheService.cs");
        var results = await _analysisEngine.DetectMismatchedAwaitAsync("CacheService.cs");

        Assert.That(results, Is.Not.Null);
        Assert.That(
            results.Any(r => r.Contains("FetchFromDbAsync")),
            Is.False,
            "FetchFromDbAsync inside new ValueTask<T>(...) must NOT be flagged as unawaited");
    }

    [Test]
    public async Task DetectMismatchedAwait_TrulyUnawaitedTask_IsDetected()
    {
        SetSource(TrulyUnawaitedSource, "FireForget.cs");
        var results = await _analysisEngine.DetectMismatchedAwaitAsync("FireForget.cs");

        Assert.That(results, Is.Not.Null);
        Assert.That(
            results.Any(r => r.Contains("DoBackgroundWorkAsync")),
            Is.True,
            "A truly fire-and-forget Task call MUST be flagged as a mismatched await");
    }

    [Test]
    public async Task DetectMismatchedAwait_MultipleValueTaskWrappers_AllClean()
    {
        SetSource(MultipleValueTaskWrappersSource, "MultiCacheService.cs");
        var results = await _analysisEngine.DetectMismatchedAwaitAsync("MultiCacheService.cs");

        Assert.That(results, Is.Not.Null);
        var wrappedMethods = new[] { "FetchUserAsync", "FetchProductAsync", "FetchCategoryAsync" };
        foreach (var method in wrappedMethods)
        {
            Assert.That(
                results.Any(r => r.Contains(method)),
                Is.False,
                $"{method} inside new ValueTask<T>(...) must NOT be flagged as unawaited");
        }
    }
}

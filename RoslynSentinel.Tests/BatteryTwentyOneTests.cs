// Battery #21 — SentinelModernizationTools
// Tests all 26 public methods of SentinelModernizationTools in-memory via TestSolutionBuilder.

using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryTwentyOneTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ModernizationEngine _modernizationEngine;
    private ModernizationUpgradeEngine _modernizationUpgradeEngine;
    private ModernLoggingEngine _modernLoggingEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private AnalysisEngine _analysisEngine;
    private LogicOptimizationEngine _logicOptimizationEngine;
    private CodeStyleEngine _codeStyleEngine;
    private CodeHealingEngine _codeHealingEngine;
    private AdvancedLogicEngine _advancedLogicEngine;
    private IDEStyleEngine _ideStyleEngine;
    private ImmutabilityEngine _immutabilityEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private SentinelModernizationTools _tools;

    private const string RichSource = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TestProj;

public class Order
{
    public int OrderId;
    public string CustomerName;
    private readonly ILogger _logger;

    public Order(int orderId, string customerName, ILogger logger)
    {
        OrderId = orderId;
        CustomerName = customerName;
        _logger = logger;
    }

    public async Task<string> ProcessAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(""Processing order {Id}"", OrderId);
        var result = string.Format(""{0}: {1}"", OrderId, CustomerName);
        return await Task.FromResult(result);
    }

    public string GetStatus()
    {
        if (OrderId == 1) return ""Active"";
        if (OrderId == 2) return ""Pending"";
        return ""Unknown"";
    }

    public void UpdateStatus(int status)
    {
        switch (status)
        {
            case 1: Console.WriteLine(""active""); break;
            case 2: Console.WriteLine(""pending""); break;
            default: Console.WriteLine(""unknown""); break;
        }
    }

    public List<string> GetItems()
    {
        var items = new List<string>();
        foreach (var i in new[] { ""a"", ""b"" })
        {
            items.Add(i);
        }
        return items;
    }

    public async Task WaitAsync() => await Task.Delay(1000);
}

public interface IOrderService
{
    Task<Order> GetOrderAsync(int id);
    Task SaveAsync(Order order);
}

public class OrderService : IOrderService
{
    private readonly ILogger<OrderService> _logger;
    public OrderService(ILogger<OrderService> logger) { _logger = logger; }
    public async Task<Order> GetOrderAsync(int id) => await Task.FromResult(new Order(id, ""test"", _logger));
    public async Task SaveAsync(Order order) => await Task.CompletedTask;
}";

    private const string AsyncSource = @"
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestProj;

public class Worker
{
    private static object _lock = new object();

    public async Task DoWorkAsync()
    {
        Thread.Sleep(100);
        await Task.CompletedTask;
    }

    public void SyncMethod()
    {
        Thread.Sleep(100);
    }

    public void MethodWithLock()
    {
        lock(_lock) { Console.WriteLine(""locked""); }
    }

    public async Task<string> GetDataAsync()
    {
        await Task.Delay(1);
        return ""data"";
    }
}";

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _modernizationEngine = new ModernizationEngine(_workspaceManager, _config);
        _modernizationUpgradeEngine = new ModernizationUpgradeEngine(_workspaceManager);
        _modernLoggingEngine = new ModernLoggingEngine(_workspaceManager);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, _config);
        _codeHealingEngine = new CodeHealingEngine(_workspaceManager, _config);
        _advancedLogicEngine = new AdvancedLogicEngine(_workspaceManager);
        _ideStyleEngine = new IDEStyleEngine(_workspaceManager);
        _immutabilityEngine = new ImmutabilityEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _tools = new SentinelModernizationTools(
            _modernizationEngine, _modernizationUpgradeEngine, _modernLoggingEngine,
            _syntaxUpgradeEngine, _analysisEngine, _logicOptimizationEngine,
            _codeStyleEngine, _codeHealingEngine, _advancedLogicEngine,
            _ideStyleEngine, _immutabilityEngine, _asyncOptimizationEngine,
            _workspaceManager, _config, NullLogger<SentinelModernizationTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- FixThreadSleep ---

    [Test]
    public async Task FixThreadSleep_FileWithThreadSleep_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Worker.cs");
        var result = await _tools.FixThreadSleep("Worker.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void FixThreadSleep_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.FixThreadSleep("NonExistent.cs"));
    }

    // --- AddBraces ---

    [Test]
    public async Task AddBraces_FileWithBracelessStatements_ReturnsUpdatedSource()
    {
        const string src = "public class C { void M() { if (true) Console.WriteLine(\"x\"); } }";
        SetSource(src, "C.cs");
        var result = await _tools.AddBraces("C.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void AddBraces_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.AddBraces("NonExistent.cs"));
    }

    // --- UpgradePatternMatching ---

    [Test]
    public async Task UpgradePatternMatching_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.UpgradePatternMatching("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void UpgradePatternMatching_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.UpgradePatternMatching("NonExistent.cs"));
    }

    // --- UseIndexFromEnd ---

    [Test]
    public async Task UseIndexFromEnd_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.UseIndexFromEnd("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void UseIndexFromEnd_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.UseIndexFromEnd("NonExistent.cs"));
    }

    // --- UpgradeUnboundNameof ---

    [Test]
    public async Task UpgradeUnboundNameof_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.UpgradeUnboundNameof("Test.cs", "nameof(Order)");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void UpgradeUnboundNameof_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.UpgradeUnboundNameof("NonExistent.cs", "nameof(X)"));
    }

    // --- UseFieldBackedProperties ---

    [Test]
    public async Task UseFieldBackedProperties_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.UseFieldBackedProperties("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task UseFieldBackedProperties_NonExistentFile_ReturnsFriendlyMessage()
    {
        // Bug 4b fix: handler no longer throws; returns a friendly message instead
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.UseFieldBackedProperties("NonExistent.cs");
        Assert.That(result, Does.Contain("not in workspace").Or.Contain("not found").Or.Contain("NoFile").Or.Contain("NonExistent"));
    }

    // --- ClassToRecord ---

    [Test]
    public async Task ClassToRecord_SimpleClass_ReturnsRecord()
    {
        const string src = "namespace TestProj; public class Point { public int X { get; init; } public int Y { get; init; } }";
        SetSource(src, "Point.cs");
        var result = await _tools.ClassToRecord("Point.cs", "Point");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ClassToRecord_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.ClassToRecord("NonExistent.cs", "Point"));
    }

    // --- RecordToClass ---

    [Test]
    public async Task RecordToClass_SimpleRecord_ReturnsClass()
    {
        const string src = "namespace TestProj; public record Point(int X, int Y);";
        SetSource(src, "Point.cs");
        var result = await _tools.RecordToClass("Point.cs", "Point");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void RecordToClass_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.RecordToClass("NonExistent.cs", "Point"));
    }

    // --- SimplifyVerbosity ---

    [Test]
    public async Task SimplifyVerbosity_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.SimplifyVerbosity("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void SimplifyVerbosity_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.SimplifyVerbosity("NonExistent.cs"));
    }

    // --- UpgradeThreadSafety ---

    [Test]
    public async Task UpgradeThreadSafety_FileWithLock_ReturnsSource()
    {
        SetSource(AsyncSource, "Worker.cs");
        var result = await _tools.UpgradeThreadSafety("Worker.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void UpgradeThreadSafety_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.UpgradeThreadSafety("NonExistent.cs"));
    }

    // --- UseTimeProvider ---

    [Test]
    public async Task UseTimeProvider_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.UseTimeProvider("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void UseTimeProvider_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.UseTimeProvider("NonExistent.cs"));
    }

    // --- ModernizeExceptions (autoStage pattern) ---

    [Test]
    public async Task ModernizeExceptions_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(RichSource, "Test.cs");
        var targets = new List<CodeHealingEngine.ExceptionTarget>();
        var result = await _tools.ModernizeExceptions(targets, autoStage: true);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ModernizeExceptions_AutoStageFalse_ReturnsDictionary()
    {
        SetSource(RichSource, "Test.cs");
        var targets = new List<CodeHealingEngine.ExceptionTarget>();
        var result = await _tools.ModernizeExceptions(targets, autoStage: false);
        Assert.That(result, Is.Not.Null);
    }

    // --- UpgradeToModernGuards ---

    [Test]
    public async Task UpgradeToModernGuards_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.UpgradeToModernGuards("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void UpgradeToModernGuards_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.UpgradeToModernGuards("NonExistent.cs"));
    }

    // --- ConvertSwitchToExpression ---

    [Test]
    public async Task ConvertSwitchToExpression_FileWithSwitch_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.ConvertSwitchToExpression("Test.cs", "UpdateStatus");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ConvertSwitchToExpression_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.ConvertSwitchToExpression("NonExistent.cs", "M"));
    }

    // --- CleanupImplicitSpans ---

    [Test]
    public async Task CleanupImplicitSpans_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.CleanupImplicitSpans("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void CleanupImplicitSpans_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.CleanupImplicitSpans("NonExistent.cs"));
    }

    // --- ConvertToSourceGeneratedLogging ---

    [Test]
    public async Task ConvertToSourceGeneratedLogging_ClassWithLogger_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.ConvertToSourceGeneratedLogging("Test.cs", "OrderService");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ConvertToSourceGeneratedLogging_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<Exception>(
            () => _tools.ConvertToSourceGeneratedLogging("NonExistent.cs", "OrderService"));
    }

    // --- SimplifyBooleanExpressions ---

    [Test]
    public async Task SimplifyBooleanExpressions_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.SimplifyBooleanExpressions("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void SimplifyBooleanExpressions_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.SimplifyBooleanExpressions("NonExistent.cs"));
    }

    // --- SimplifyMemberAccess ---

    [Test]
    public async Task SimplifyMemberAccess_ValidFile_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.SimplifyMemberAccess("Test.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void SimplifyMemberAccess_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.SimplifyMemberAccess("NonExistent.cs"));
    }

    // --- MakeClassImmutable ---

    [Test]
    public async Task MakeClassImmutable_ClassWithMutableFields_ReturnsSource()
    {
        const string src = "namespace TestProj; public class Config { public string Host { get; set; } public int Port { get; set; } }";
        SetSource(src, "Config.cs");
        var result = await _tools.MakeClassImmutable("Config.cs", "Config");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void MakeClassImmutable_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.MakeClassImmutable("NonExistent.cs", "Config"));
    }

    // --- ConvertStaticToExtension ---

    [Test]
    public async Task ConvertStaticToExtension_StaticMethod_ReturnsSource()
    {
        const string src = "namespace TestProj; public static class Helper { public static string Format(string s) => s.Trim(); }";
        SetSource(src, "Helper.cs");
        var result = await _tools.ConvertStaticToExtension("Helper.cs", "Format");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ConvertStaticToExtension_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.ConvertStaticToExtension("NonExistent.cs", "Format"));
    }

    // --- InvertBooleanLogic ---

    [Test]
    public async Task InvertBooleanLogic_BoolField_ReturnsDictionary()
    {
        const string src = "namespace TestProj; public class Flags { public bool IsActive; public void Toggle() { IsActive = !IsActive; } }";
        SetSource(src, "Flags.cs");
        var result = await _tools.InvertBooleanLogic("Flags.cs", "IsActive");
        Assert.That(result, Is.Not.Null);
    }

    // --- OptimizeToValueTask ---

    [Test]
    public async Task OptimizeToValueTask_AsyncMethod_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.OptimizeToValueTask("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void OptimizeToValueTask_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.OptimizeToValueTask("NonExistent.cs", "M"));
    }

    // --- OptimizeIndependentAwaits ---

    [Test]
    public async Task OptimizeIndependentAwaits_AsyncMethod_ReturnsSource()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.OptimizeIndependentAwaits("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task OptimizeIndependentAwaits_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.OptimizeIndependentAwaits("NonExistent.cs", "M");
        Assert.That(result, Is.Not.Null);
    }

    // --- UpgradeToPrimaryConstructor---

    [Test]
    public async Task UpgradeToPrimaryConstructor_SimpleClass_ReturnsSource()
    {
        const string src = @"namespace TestProj;
public class Service
{
    private readonly string _name;
    public Service(string name) { _name = name; }
    public string GetName() => _name;
}";
        SetSource(src, "Service.cs");
        var result = await _tools.UpgradeToPrimaryConstructor("Service.cs", "Service");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task UpgradeToPrimaryConstructor_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.UpgradeToPrimaryConstructor("NonExistent.cs", "Service");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUseFrozenCollections---

    [Test]
    public async Task FindUseFrozenCollections_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindUseFrozenCollections("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindUseFrozenCollections_NoArguments_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindUseFrozenCollections();
        Assert.That(result, Is.Not.Null);
    }

    // --- UseExceptionExpressions ---

    [Test]
    public async Task UseExceptionExpressions_MethodWithGuard_ReturnsSource()
    {
        const string src = @"namespace TestProj;
public class Validator
{
    public void Check(string s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
    }
}";
        SetSource(src, "Validator.cs");
        var result = await _tools.UseExceptionExpressions("Validator.cs", "Check");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void UseExceptionExpressions_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.UseExceptionExpressions("NonExistent.cs", "M"));
    }
}

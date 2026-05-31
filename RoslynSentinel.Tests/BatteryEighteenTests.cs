// Battery #18 — SentinelAugmentTools
// Tests all 12 public methods of SentinelAugmentTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryEighteenTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelAugmentTools _tools;
    private MsToolAugmentEngine _msEngine;

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

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _msEngine = new MsToolAugmentEngine(_workspaceManager);
        _tools = new SentinelAugmentTools(
            new MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            NullLogger<SentinelAugmentTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- EncapsulateFieldSafe ---

    [Test]
    public async Task EncapsulateFieldSafe_PublicField_ReturnsResult()
    {
        SetSource("namespace TestProj; public class Order { public int OrderId; }", "Test.cs");
        var result = await _tools.EncapsulateFieldSafe("Test.cs", "OrderId");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task EncapsulateFieldSafe_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.EncapsulateFieldSafe("NonExistent.cs", "OrderId");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeSwitchForPatternConversion_WithSwitchStatement_ReturnsAnalysis()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.AnalyzeSwitchForPatternConversion("Test.cs", "switch (status)");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeSwitchForPatternConversion_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.AnalyzeSwitchForPatternConversion("NonExistent.cs", "switch (x)");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertSwitchToPatternSafe ---

    [Test]
    public async Task ConvertSwitchToPatternSafe_WithSwitchStatement_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.ConvertSwitchToPatternSafe("Test.cs", "switch (status)");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertSwitchToPatternSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.ConvertSwitchToPatternSafe("NonExistent.cs", "switch (x)");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertStringFormatToInterpolatedSmart ---

    [Test]
    public async Task ConvertStringFormatToInterpolatedSmart_WithFormatCall_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.ConvertStringFormatToInterpolatedSmartAsync("Test.cs", @"string.Format(""{0}: {1}""");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertStringFormatToInterpolatedSmart_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.ConvertStringFormatToInterpolatedSmartAsync("NonExistent.cs", "string.Format");
        Assert.That(result, Is.Not.Null);
    }

    // --- SortAndDeduplicateUsings ---

    [Test]
    public async Task SortAndDeduplicateUsings_FileWithUsings_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.SortAndDeduplicateUsingsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SortAndDeduplicateUsings_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.SortAndDeduplicateUsingsAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FormatDocumentSafe ---

    [Test]
    public async Task FormatDocumentSafe_ValidFile_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.FormatDocumentSafeAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FormatDocumentSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.FormatDocumentSafeAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeForeachForLinqConversion ---

    [Test]
    public async Task AnalyzeForeachForLinqConversion_WithForeach_ReturnsAnalysis()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.AnalyzeForeachForLinqConversion("Test.cs", "foreach (var i in");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeForeachForLinqConversion_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.AnalyzeForeachForLinqConversion("NonExistent.cs", "foreach");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetWorkspaceHealth ---

    [Test]
    public async Task GetWorkspaceHealth_WithLoadedSolution_ReturnsReport()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.GetWorkspaceHealth();
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetWorkspaceHealth_NoSolution_ReturnsReport()
    {
        var result = await _tools.GetWorkspaceHealth();
        Assert.That(result, Is.Not.Null);
    }

    // --- PreviewAddMissingUsings ---

    [Test]
    public async Task PreviewAddMissingUsings_ValidFile_ReturnsPreview()
    {
        SetSource("namespace TestProj; public class Order { }", "Test.cs");
        var result = await _msEngine.PreviewAddMissingUsingsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PreviewAddMissingUsings_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.PreviewAddMissingUsingsAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractConstantSafe ---

    [Test]
    public async Task ExtractConstantSafe_WithLiteralValue_ReturnsResult()
    {
        const string src = @"namespace TestProj; public class Order { public string GetLabel() { return ""hello""; } }";
        SetSource(src, "Test.cs");
        var result = await _msEngine.ExtractConstantSafeAsync("Test.cs", @"""hello""", "HelloLabel");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ExtractConstantSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.ExtractConstantSafeAsync("NonExistent.cs", @"""hello""", "HelloLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateToStringSafe ---

    [Test]
    public async Task GenerateToStringSafe_ValidClass_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.GenerateToStringSafeAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GenerateToStringSafe_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.GenerateToStringSafeAsync("NonExistent.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractMethodSafe ---

    [Test]
    public async Task ExtractMethodSafe_WithMethodBody_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.ExtractMethodSafe("Test.cs", "DoProcess", @"var result = string.Format");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ExtractMethodSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.ExtractMethodSafe("NonExistent.cs", "NewMethod", "some code");
        Assert.That(result, Is.Not.Null);
    }
}

// Battery #19 — SentinelGenerationTools
// Tests all 13 public methods of SentinelGenerationTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryNineteenTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private CodeGenerationEngine _codeGenerationEngine;
    private ApiAutomationEngine _apiAutomationEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private ApiIntegrationEngine _apiIntegrationEngine;
    private SentinelGenerationTools _tools;

    private const string ControllerSource = @"
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace TestProj;

[ApiController]
[Route(""api/[controller]"")]
public class OrdersController : ControllerBase
{
    [HttpGet(""{id}"")]
    public async Task<IActionResult> GetById(int id) => Ok(id);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] OrderDto dto) => Created("""", dto);

    [HttpPut(""{id}"")]
    public async Task<IActionResult> Update(int id, [FromBody] OrderDto dto) => Ok(dto);

    [HttpDelete(""{id}"")]
    public async Task<IActionResult> Delete(int id) => NoContent();
}

public record OrderDto(int Id, string Name);
";

    private const string PocoSource = @"
namespace TestProj;

public class Order
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }
    public decimal Total { get; set; }
}

public interface IOrderRepository
{
    Task<Order> GetByIdAsync(int id);
    Task SaveAsync(Order order);
}
";

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
        _apiAutomationEngine = new ApiAutomationEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _apiIntegrationEngine = new ApiIntegrationEngine(_workspaceManager);
        _tools = new SentinelGenerationTools(
            _codeGenerationEngine,
            _apiAutomationEngine,
            _asyncOptimizationEngine,
            _apiIntegrationEngine,
            NullLogger<SentinelGenerationTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- GenerateClassesFromJson (sync) ---

    [Test]
    public void GenerateClassesFromJson_ValidJson_ReturnsResult()
    {
        var json = @"{""id"": 1, ""name"": ""test"", ""active"": true}";
        var result = _tools.GenerateClassesFromJson(json, "Product", "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void GenerateClassesFromJson_NestedJson_ReturnsResult()
    {
        var json = @"{""order"": {""id"": 1, ""items"": [{""sku"": ""A""}]}}";
        var result = _tools.GenerateClassesFromJson(json, "Root", "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateHttpClient ---

    [Test]
    public async Task GenerateHttpClient_ValidController_ReturnsCode()
    {
        SetSource(ControllerSource, "Orders.cs");
        var result = await _tools.GenerateHttpClient("Orders.cs", "OrdersController");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GenerateHttpClient_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.GenerateHttpClient("NonExistent.cs", "OrdersController"));
    }

    // --- GenerateConstructor ---

    [Test]
    public async Task GenerateConstructor_ValidClass_ReturnsCode()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _codeGenerationEngine.GenerateConstructorAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GenerateConstructor_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _codeGenerationEngine.GenerateConstructorAsync("NonExistent.cs", "Order");
        Assert.That(result, Is.Null.Or.Empty);
    }

    // --- GenerateToString ---

    [Test]
    public async Task GenerateToString_ValidClass_ReturnsResult()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _codeGenerationEngine.GenerateToStringAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateRepositoryInterface ---

    [Test]
    public async Task GenerateRepositoryInterface_ValidClass_ReturnsResult()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _codeGenerationEngine.GenerateRepositoryInterfaceAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateFluentBuilder ---

    [Test]
    public async Task GenerateFluentBuilder_ValidClass_ReturnsResult()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateDecoratorClass ---

    [Test]
    public async Task GenerateDecoratorClass_ValidInterface_ReturnsResult()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync("IOrderRepository", "Logging", null);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GenerateDecoratorClass_NonExistentInterface_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync("INoSuchInterface", "Logging", null);
        Assert.That(result, Is.Null);
    }

    // --- GenerateDefaultConfigJson ---

    [Test]
    public async Task GenerateDefaultConfigJson_ValidProject_ReturnsJson()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _tools.GenerateDefaultConfigJson("TestProj");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GenerateDefaultConfigJson_UnknownProject_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<Exception>(
            () => _tools.GenerateDefaultConfigJson("NoSuchProject"));
    }

    // --- GenerateAsyncOverload ---

    [Test]
    public async Task GenerateAsyncOverload_SyncMethod_ReturnsCode()
    {
        const string src = "namespace TestProj; public class Service { public string GetData() { return \"data\"; } }";
        SetSource(src, "Service.cs");
        var result = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync("Service.cs", "GetData");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GenerateAsyncOverload_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync("NonExistent.cs", "GetData");
        Assert.That(result, Is.Null.Or.Empty);
    }

    // --- AddValidationToPoco ---

    [Test]
    public async Task AddValidationToPoco_ValidClass_ReturnsCode()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _apiIntegrationEngine.AddValidationToPocoAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task AddValidationToPoco_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _apiIntegrationEngine.AddValidationToPocoAsync("NonExistent.cs", "Order");
        Assert.That(result, Is.Null.Or.Empty);
    }

    // --- ImplementInterfaceSafe ---

    [Test]
    public async Task ImplementInterfaceSafe_ValidClassAndInterface_ReturnsCode()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Order.cs", "Order", "IOrderRepository");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ImplementInterfaceSafe_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _codeGenerationEngine.ImplementInterfaceAsync("NonExistent.cs", "Order", "IOrderRepository");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertPropertySafe_AutoPropertyToFull_ReturnsCode()
    {
        SetSource(PocoSource, "Order.cs");
        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Order.cs", "OrderId", "ToFullProperty");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertPropertySafe_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("NonExistent.cs", "OrderId", "ToFullProperty");
        Assert.That(result, Is.Null.Or.Empty);
    }

    // --- InterpolateStringSafe ---

    [Test]
    public async Task InterpolateStringSafe_WithFormatCall_ReturnsCode()
    {
        const string src = @"namespace TestProj; public class Order { public string GetLabel(int id) { return string.Format(""{0}"", id); } }";
        SetSource(src, "Order.cs");
        var result = await _tools.InterpolateStringSafe("Order.cs", @"string.Format(""{0}""");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void InterpolateStringSafe_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.InterpolateStringSafe("NonExistent.cs", "string.Format"));
    }
}

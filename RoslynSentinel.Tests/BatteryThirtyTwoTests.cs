// Battery #32 — Regression tests for GenerateFluentBuilder: error result (not exception) on DI/POCO-less classes
// Bug fixed: GenerateFluentBuilderAsync used to throw InvalidOperationException on classes with no
// settable public properties. The fix returns a FluentBuilderResult with a non-empty Error field.
//
// Tests in this battery:
//   1. DI service class (no settable props) → returns error result, does NOT throw
//   2. API controller class (no settable props) → returns error result, does NOT throw
//   3. Abstract class with no props → returns error result, does NOT throw
//   4. Empty class → returns error result with class name in the error
//   5. Error message contains expected guidance (class name, "DI-injected", "settable public properties")
//   6. POCO class WITH settable properties → no error, valid builder generated
//   7. Record with primary constructor → no error, valid builder generated
//   8. Class with init-only properties → no error, valid builder generated
//   9. Multiple DI classes in file — per-class call still returns error
//  10. Previously correct class (POCO) still generates correct builder after fix

using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryThirtyTwoTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private CodeGenerationEngine _codeGenerationEngine;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // =========================================================================
    // BUG REGRESSION: DI/no-property classes must return error, not throw
    // =========================================================================

    [Test]
    public async Task GenerateFluentBuilder_DIServiceClass_DoesNotThrow_ReturnsErrorResult()
    {
        // Regression: was throwing InvalidOperationException before fix
        const string src = @"public class OrderService
{
    private readonly IOrderRepository _repo;
    private readonly IEmailService _email;
    public OrderService(IOrderRepository repo, IEmailService email)
    {
        _repo = repo;
        _email = email;
    }
    public Task ProcessOrderAsync(int id) => Task.CompletedTask;
}";
        SetSource(src, "OrderService.cs");

        FluentBuilderResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
        {
            result = await _codeGenerationEngine.GenerateFluentBuilderAsync("OrderService.cs", "OrderService");
        }, "GenerateFluentBuilderAsync must NOT throw — should return an error result instead");

        Assert.That(result, Is.Not.Null, "Result must not be null");
        Assert.That(result!.Error, Is.Not.Null.And.Not.Empty,
            "DI service class with no settable properties should produce a non-empty Error field");
    }

    [Test]
    public async Task GenerateFluentBuilder_APIController_DoesNotThrow_ReturnsErrorResult()
    {
        // Regression: API controllers are pure DI classes with no settable props
        const string src = @"public class ProductsController
{
    private readonly IProductService _svc;
    private readonly ILogger _logger;
    public ProductsController(IProductService svc, ILogger logger)
    {
        _svc = svc;
        _logger = logger;
    }
}";
        SetSource(src, "ProductsController.cs");

        FluentBuilderResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
        {
            result = await _codeGenerationEngine.GenerateFluentBuilderAsync("ProductsController.cs", "ProductsController");
        }, "API controller must NOT cause exception — returns error result");

        Assert.That(result!.Error, Is.Not.Null.And.Not.Empty,
            "Controller class (no settable props) must have non-empty Error");
    }

    [Test]
    public async Task GenerateFluentBuilder_AbstractClassNoProperies_DoesNotThrow_ReturnsError()
    {
        const string src = @"public abstract class BaseValidator
{
    private readonly ILogger _log;
    protected BaseValidator(ILogger log) { _log = log; }
    public abstract bool Validate(object item);
}";
        SetSource(src, "BaseValidator.cs");

        FluentBuilderResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
        {
            result = await _codeGenerationEngine.GenerateFluentBuilderAsync("BaseValidator.cs", "BaseValidator");
        });

        Assert.That(result!.Error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GenerateFluentBuilder_EmptyClass_DoesNotThrow_ReturnsError()
    {
        // Edge case: completely empty class
        const string src = @"public class EmptyClass { }";
        SetSource(src, "EmptyClass.cs");

        FluentBuilderResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
        {
            result = await _codeGenerationEngine.GenerateFluentBuilderAsync("EmptyClass.cs", "EmptyClass");
        });

        Assert.That(result!.Error, Is.Not.Null.And.Not.Empty,
            "Empty class must produce an error result");
    }

    // =========================================================================
    // Error message content validation
    // =========================================================================

    [Test]
    public async Task GenerateFluentBuilder_DIClass_ErrorContainsClassName()
    {
        const string src = @"public class MySpecialService
{
    private readonly IDep _dep;
    public MySpecialService(IDep dep) { _dep = dep; }
}";
        SetSource(src, "MySpecialService.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("MySpecialService.cs", "MySpecialService");

        Assert.That(result.Error, Does.Contain("MySpecialService"),
            "Error message should include the target class name for context");
    }

    [Test]
    public async Task GenerateFluentBuilder_DIClass_ErrorContainsDIInjectedGuidance()
    {
        const string src = @"public class RepoService
{
    private readonly IDb _db;
    public RepoService(IDb db) { _db = db; }
}";
        SetSource(src, "RepoService.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("RepoService.cs", "RepoService");

        Assert.That(result.Error, Does.Contain("DI-injected").Or.Contain("DI injected").Or.Contain("dependency inject"),
            "Error should mention DI injection to guide the user");
    }

    [Test]
    public async Task GenerateFluentBuilder_DIClass_ErrorMentionsSettableProperties()
    {
        const string src = @"public class SomeHandler
{
    private readonly ISomeDep _dep;
    public SomeHandler(ISomeDep dep) { _dep = dep; }
}";
        SetSource(src, "SomeHandler.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("SomeHandler.cs", "SomeHandler");

        Assert.That(result.Error, Does.Contain("settable public properties").Or.Contain("public properties"),
            "Error should explain what is needed (settable public properties)");
    }

    // =========================================================================
    // Happy path: classes WITH properties still work after fix
    // =========================================================================

    [Test]
    public async Task GenerateFluentBuilder_POCOClass_NoError_GeneratesBuilder()
    {
        const string src = @"public class CreateOrderRequest
{
    public string CustomerName { get; set; }
    public decimal TotalAmount { get; set; }
    public int Quantity { get; set; }
}";
        SetSource(src, "CreateOrderRequest.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("CreateOrderRequest.cs", "CreateOrderRequest");

        Assert.That(result.Error, Is.Null.Or.Empty,
            "POCO class with settable properties should NOT produce an error");
        Assert.That(result.BuilderCode, Does.Contain("WithCustomerName"),
            "Builder should contain With-method for CustomerName");
        Assert.That(result.BuilderCode, Does.Contain("WithTotalAmount"),
            "Builder should contain With-method for TotalAmount");
        Assert.That(result.BuilderCode, Does.Contain("WithQuantity"),
            "Builder should contain With-method for Quantity");
    }

    [Test]
    public async Task GenerateFluentBuilder_Record_NoError_GeneratesBuilder()
    {
        const string src = @"public record ProductDto(string Name, decimal Price, int StockLevel);";
        SetSource(src, "ProductDto.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("ProductDto.cs", "ProductDto");

        Assert.That(result.Error, Is.Null.Or.Empty,
            "Record with primary constructor should NOT produce an error");
        Assert.That(result.BuilderCode, Is.Not.Null.And.Not.Empty,
            "Builder code should be generated for record type");
    }

    [Test]
    public async Task GenerateFluentBuilder_InitOnlyProperties_NoError_GeneratesBuilder()
    {
        const string src = @"public class ImmutableConfig
{
    public string Host { get; init; }
    public int Port { get; init; }
    public bool UseSsl { get; init; }
}";
        SetSource(src, "ImmutableConfig.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("ImmutableConfig.cs", "ImmutableConfig");

        Assert.That(result.Error, Is.Null.Or.Empty,
            "Class with init-only properties should NOT produce an error");
        Assert.That(result.BuilderCode, Does.Contain("WithHost"),
            "Builder should contain With-method for Host");
    }

    [Test]
    public async Task GenerateFluentBuilder_MixedClassAndDIClass_IndependentResults()
    {
        // Both classes in the same file; each call is independent
        const string src = @"
public class DIService
{
    private readonly IDep _dep;
    public DIService(IDep dep) { _dep = dep; }
}
public class ValueObject
{
    public string Name { get; set; }
    public int Score { get; set; }
}";
        SetSource(src, "Mixed.cs");

        var diResult = await _codeGenerationEngine.GenerateFluentBuilderAsync("Mixed.cs", "DIService");
        var pocoResult = await _codeGenerationEngine.GenerateFluentBuilderAsync("Mixed.cs", "ValueObject");

        Assert.That(diResult.Error, Is.Not.Null.And.Not.Empty,
            "DIService should return error result (no settable props)");
        Assert.That(pocoResult.Error, Is.Null.Or.Empty,
            "ValueObject should NOT return error (has settable props)");
        Assert.That(pocoResult.BuilderCode, Does.Contain("WithName"),
            "ValueObject builder should contain WithName");
    }

    [Test]
    public async Task GenerateFluentBuilder_DIClass_BuilderCodeIsEmpty()
    {
        // When error is returned, builder code should be empty (not garbage)
        const string src = @"public class EmptyDIClass
{
    private readonly IFoo _foo;
    public EmptyDIClass(IFoo foo) { _foo = foo; }
}";
        SetSource(src, "EmptyDIClass.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("EmptyDIClass.cs", "EmptyDIClass");

        Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
            "Should have error");
        Assert.That(result.BuilderCode, Is.Null.Or.Empty,
            "When error is returned, BuilderCode should be empty (not partial/garbage)");
    }
}

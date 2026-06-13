#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #7 — Tests for three engines at 6-mention coverage:
///   A. StandardRefactoringEngine (4 tests) — ConvertMethodToProperty, MakeMethodStatic, InvertBoolean stub
///   B. SemanticSearchEngine      (4 tests) — FindMethodsByReturnType, FindTypesByAttribute
///   C. AdvancedTypeEngine        (5 tests) — ConvertTupleToClass, ChangePropertyType, ConvertAnonymousToNamed
///
/// Total: 13 tests. All workspace-based (SetSource / SetMultipleFiles).
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// A. StandardRefactoringEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class StandardRefactoringEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private StandardRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new StandardRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ConvertMethodToProperty_SingleReturnNoParams_ConvertsToExpressionProperty()
    {
        SetSource(@"
public class Counter
{
    private int _count;
    public int GetCount() { return _count; }
}");
        var result = await _engine.ConvertMethodToPropertyAsync("Test.cs", "GetCount");

        // Method becomes an expression-bodied property — no parameter list
        Assert.That(result, Does.Contain("GetCount"), "Property name should be preserved");
        Assert.That(result, Does.Contain("=>"), "Should produce expression-bodied property");
        Assert.That(result, Does.Not.Contain("GetCount()"), "Should not have method parameter parens");
    }

    [Test]
    public async Task ConvertMethodToProperty_MethodWithParameters_ReturnsUnchanged()
    {
        const string source = @"
public class Calculator
{
    public int Add(int a, int b) { return a + b; }
}";
        SetSource(source);

        var result = await _engine.ConvertMethodToPropertyAsync("Test.cs", "Add");

        // Methods with parameters cannot be converted — source returned unchanged
        Assert.That(result, Does.Contain("Add(int a, int b)"), "Parameterized method should remain unchanged");
        Assert.That(result, Does.Not.Contain("Add =>"), "Should not produce arrow property for parameterized method");
    }

    [Test]
    public async Task MakeMethodStatic_MethodWithNoInstanceAccess_AddsStaticKeyword()
    {
        SetSource(@"
public class MathHelper
{
    public int Multiply(int a, int b) { return a * b; }
}");
        var result = await _engine.MakeMethodStaticAsync("Test.cs", "Multiply");

        Assert.That(result, Does.Contain("static"), "Method with no instance access should receive static keyword");
        Assert.That(result, Does.Contain("Multiply"), "Method name should be preserved");
    }

    [Test]
    public async Task InvertBoolean_AnyInput_ReturnsEmpty()
    {
        // InvertBoolean is a documented stub — requires solution-wide reference tracking
        SetSource("public class C { public bool IsEnabled { get; set; } }");

        var result = await _engine.InvertBooleanAsync("Test.cs", "IsEnabled");

        Assert.That(result, Is.Empty, "InvertBoolean stub should return empty string");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. SemanticSearchEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SemanticSearchEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SemanticSearchEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SemanticSearchEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task FindMethodsByReturnType_MatchingMethods_ReturnsCorrectResults()
    {
        SetSource(@"
public class InventoryService
{
    public Task<List<Product>> GetAllAsync() => null!;
    public Task<Product> GetByIdAsync(int id) => null!;
    public string GetName() => ""name"";
}");
        var results = await _engine.FindMethodsByReturnTypeAsync("Task");

        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Count, Is.EqualTo(2), "Should find exactly 2 Task-returning methods");
        Assert.That(results.All(r => r.MemberName is "GetAllAsync" or "GetByIdAsync"), Is.True);
    }

    [Test]
    public async Task FindMethodsByReturnType_NoMatch_ReturnsEmpty()
    {
        SetSource(@"
public class OrderService
{
    public int GetCount() => 0;
    public string GetName() => ""name"";
}");
        var results = await _engine.FindMethodsByReturnTypeAsync("XmlDocument");

        Assert.That(results, Is.Empty, "No methods return XmlDocument — should yield empty list");
    }

    [Test]
    public async Task FindTypesByAttribute_WithMatchingType_ReturnsResult()
    {
        SetSource(@"
[ApiController]
[Route(""api/[controller]"")]
public class ProductsController { }

public class RegularClass { }
");
        var results = await _engine.FindTypesByAttributeAsync("ApiController");

        Assert.That(results.Count, Is.EqualTo(1), "Only one class has ApiController attribute");
        Assert.That(results[0].MemberName, Is.EqualTo("ProductsController"));
    }

    [Test]
    public async Task FindTypesByAttribute_NoMatchingAttribute_ReturnsEmpty()
    {
        SetSource(@"
public class PlainDto { public int Id { get; set; } }
");
        var results = await _engine.FindTypesByAttributeAsync("Obsolete");

        Assert.That(results, Is.Empty, "No types have Obsolete attribute");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. AdvancedTypeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AdvancedTypeEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AdvancedTypeEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AdvancedTypeEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ConvertTupleToClass_MethodReturnsTuple_GeneratesClassAndUpdatesMethodReturn()
    {
        SetSource(@"
public class DataService
{
    public (int Id, string Name) GetData() { return (1, ""test""); }
}");
        var result = await _engine.ConvertTupleToClassAsync("Test.cs", "GetData", "DataResult");

        Assert.That(result, Does.ContainKey("Test.cs"), "Should return updated original file");
        Assert.That(result["Test.cs"], Does.Contain("DataResult"), "Original file should reference new class name");
        Assert.That(result["Test.cs"], Does.Not.Contain("(int Id, string Name)"), "Tuple return type should be replaced");

        var newClassKey = result.Keys.FirstOrDefault(k => k.Contains("DataResult.cs"));
        Assert.That(newClassKey.Absolute, Is.Not.Null.And.Not.Empty, "Should generate DataResult.cs");
        Assert.That(result[newClassKey!], Does.Contain("public int Id"), "Generated class should have Id property");
        Assert.That(result[newClassKey!], Does.Contain("public string Name"), "Generated class should have Name property");
    }

    [Test]
    public async Task ConvertTupleToClass_MethodDoesNotReturnTuple_Throws()
    {
        SetSource(@"
public class Processor
{
    public int Calculate(int x) { return x * 2; }
}");
        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ConvertTupleToClassAsync("Test.cs", "Calculate", "Result"));
    }

    [Test]
    public async Task ChangePropertyType_ValidProperty_UpdatesPropertyType()
    {
        SetSource(@"
public class Product
{
    public int Price { get; set; }
    public string Name { get; set; }
}");
        var result = await _engine.ChangePropertyTypeAsync("Test.cs", "Product", "Price", "decimal");

        Assert.That(result, Does.ContainKey("Test.cs"), "Should return changes for the modified file");
        Assert.That(result["Test.cs"], Does.Contain("decimal Price"), "Property type should be updated to decimal");
        Assert.That(result["Test.cs"], Does.Contain("string Name"), "Other properties should be unchanged");
    }

    [Test]
    public async Task ChangePropertyType_PropertyNotFound_Throws()
    {
        SetSource(@"public class Foo { public int Bar { get; set; } }");

        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ChangePropertyTypeAsync("Test.cs", "Foo", "NonExistent", "string"));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_WithAnonymousObjectInitializer_GeneratesNamedClass()
    {
        SetSource(@"
public class Factory
{
    public void Build()
    {
        var item = new { Name = ""widget"", Quantity = 10 };
    }
}");
        var result = await _engine.ConvertAnonymousToNamedAsync("Test.cs", "ItemDto");

        var newClassKey = result.Keys.FirstOrDefault(k => k.Contains("ItemDto.cs"));
        Assert.That(newClassKey.Absolute, Is.Not.Null.And.Not.Empty, "Should generate ItemDto.cs");
        Assert.That(result[newClassKey!], Does.Contain("ItemDto"), "Generated class should be named ItemDto");
        Assert.That(result[newClassKey!], Does.Contain("Name"), "Should extract Name property from anonymous type");
        Assert.That(result[newClassKey!], Does.Contain("Quantity"), "Should extract Quantity property from anonymous type");
    }
}

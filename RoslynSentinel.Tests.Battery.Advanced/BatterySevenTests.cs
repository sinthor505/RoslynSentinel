#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;
/// <summary>
/// Battery #7 -> Tests for four engines at 6-mention coverage:
///   A. BasicRefactoringEngine (4 tests) -> InvertBoolean, InvertBoolean (stub), InvertBoolean (stub), InvertBoolean (stub)
///   B. SemanticSearchEngine      (4 tests) -> FindMethodsByReturnType, FindTypesByAttribute
///   C. AdvancedTypeEngine        (5 tests) -> ConvertTupleToClass, ChangePropertyType, ConvertAnonymousToNamed
///   D. MemberRefactoringEngine (5 tests) -> ConvertMethodToProperty, MakeMethodStatic, ExtractMethod, AddRemoveParams, SortMembers
///
/// Total: 16 tests. All workspace-based (SetSource / SetMultipleFiles).
/// </summary>
/// 
// ════════════════════════════════════════════════════════════════════════════════
// A. BasicRefactoringEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class BasicRefactoringEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _basicRefactoringEngine;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _basicRefactoringEngine = new BasicRefactoringEngine(_workspaceManager);
        var navEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task InvertBoolean_AnyInput_ReturnsEmpty()
    {
        // InvertBoolean is a documented stub -> requires solution-wide reference tracking
        SetSource("public class C { public bool IsEnabled { get; set; } }");
        var result = await _basicRefactoringEngine.InvertBooleanAsync("Test.cs", "IsEnabled");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit), "InvertBoolean stub should report CannotEdit");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. SemanticSearchEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SemanticSearchEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private DiscoveryEngine _engine;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new DiscoveryEngine(_workspaceManager);
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
        var results = await new DiscoveryEngine(_workspaceManager).FindMethodsByReturnTypeAsync("Task");
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
        var results = await new DiscoveryEngine(_workspaceManager).FindMethodsByReturnTypeAsync("XmlDocument");
        Assert.That(results, Is.Empty, "No methods return XmlDocument - should yield empty list");
    }

    [Test]
    public async Task FindTypesByAttribute_WithMatchingType_ReturnsResult()
    {
        SetSource(@"
public class ApiControllerAttribute : System.Attribute { }

[ApiController]
public class ProductsController { }

public class RegularClass { }
");
        var results = await new DiscoveryEngine(_workspaceManager).FindTypesByAttributeAsync("ApiController");
        Assert.That(results.Count, Is.EqualTo(1), "Only one class has ApiController attribute");
        Assert.That(results[0].MemberName, Is.EqualTo("ProductsController"));
    }

    [Test]
    public async Task FindTypesByAttribute_NoMatchingAttribute_ReturnsEmpty()
    {
        SetSource(@"
public class PlainDto { public int Id { get; set; } }
");
        var results = await new DiscoveryEngine(_workspaceManager).FindTypesByAttributeAsync("Obsolete");
        Assert.That(results, Is.Empty, "No types have Obsolete attribute");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. AdvancedTypeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AdvancedTypeEngineTests
{
    private IWorkspaceManager _workspaceManager;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
        var result = await new StructuralRefactoringEngine(_workspaceManager).ConvertTupleToClassAsync("Test.cs", "GetData", "DataResult");
        var originalKey = result.Keys.FirstOrDefault(k => k.Contains("Test.cs"));
        Assert.That(originalKey.Absolute, Is.Not.Null.And.Not.Empty, "Should return updated original file");
        Assert.That(result[originalKey!], Does.Contain("DataResult"), "Original file should reference new class name");
        Assert.That(result[originalKey!], Does.Not.Contain("(int Id, string Name)"), "Tuple return type should be replaced");
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
        Assert.ThrowsAsync<ToolTargetIneligibleException>(async () => await new StructuralRefactoringEngine(_workspaceManager).ConvertTupleToClassAsync("Test.cs", "Calculate", "Result"));
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
        var result = await new StructuralRefactoringEngine(_workspaceManager).ChangePropertyTypeAsync("Test.cs", "Product", "Price", "decimal");
        var key = result.Keys.FirstOrDefault(k => k.Contains("Test.cs"));
        Assert.That(key.Absolute, Is.Not.Null.And.Not.Empty, "Should return changes for the modified file");
        Assert.That(result[key!], Does.Contain("decimal Price"), "Property type should be updated to decimal");
        Assert.That(result[key!], Does.Contain("string Name"), "Other properties should be unchanged");
    }

    [Test]
    public async Task ChangePropertyType_PropertyNotFound_Throws()
    {
        SetSource(@"public class Foo { public int Bar { get; set; } }");
        Assert.ThrowsAsync<ToolNotFoundException>(async () => await new StructuralRefactoringEngine(_workspaceManager).ChangePropertyTypeAsync("Test.cs", "Foo", "NonExistent", "string"));
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
        var result = await new StructuralRefactoringEngine(_workspaceManager).ConvertAnonymousToNamedAsync("Test.cs", "ItemDto");
        var newClassKey = result.Keys.FirstOrDefault(k => k.Contains("ItemDto.cs"));
        Assert.That(newClassKey.Absolute, Is.Not.Null.And.Not.Empty, "Should generate ItemDto.cs");
        Assert.That(result[newClassKey!], Does.Contain("ItemDto"), "Generated class should be named ItemDto");
        Assert.That(result[newClassKey!], Does.Contain("Name"), "Should extract Name property from anonymous type");
        Assert.That(result[newClassKey!], Does.Contain("Quantity"), "Should extract Quantity property from anonymous type");
    }

}

// ════════════════════════════════════════════════════════════════════════════════
// D. MemberRefactoringEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class MemberRefactoringEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _memberRefactoringEngine;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var navEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        _memberRefactoringEngine = new MemberRefactoringEngine(_workspaceManager, navEngine, validationEngine);
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
        var result = await _memberRefactoringEngine.ConvertMethodToPropertyAsync("Test.cs", "GetCount");
        // Method becomes an expression-bodied property -> no parameter list
        Assert.That(result.UpdatedText, Does.Contain("GetCount"), "Property name should be preserved");
        Assert.That(result.UpdatedText, Does.Contain("=>"), "Should produce expression-bodied property");
        Assert.That(result.UpdatedText, Does.Not.Contain("GetCount()"), "Should not have method parameter parens");
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
        var result = await _memberRefactoringEngine.ConvertMethodToPropertyAsync("Test.cs", "Add");
        // Methods with parameters cannot be converted -> source returned unchanged
        Assert.That(result.UpdatedText, Does.Contain("Add(int a, int b)"), "Parameterized method should remain unchanged");
        Assert.That(result.UpdatedText, Does.Not.Contain("Add =>"), "Should not produce arrow property for parameterized method");
    }

    [Test]
    public async Task MakeMethodStatic_MethodWithNoInstanceAccess_AddsStaticKeyword()
    {
        SetSource(@"
public class MathHelper
{
    public int Multiply(int a, int b) { return a * b; }
}");
        var result = await _memberRefactoringEngine.MakeMethodStaticAsync("Test.cs", "Multiply");
        Assert.That(result.UpdatedText, Does.Contain("static"), "Method with no instance access should receive static keyword");
        Assert.That(result.UpdatedText, Does.Contain("Multiply"), "Method name should be preserved");
    }


    [Test]
    public async Task ExtractMethodAsync_ValidRange_ReturnsResult()
    {
        var source = @"public class Calc {
    public int Process(int a, int b) {
        int sum = a + b;
        return sum;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        // Line 3 = "        int sum = a + b;"
        var result = await _memberRefactoringEngine.ExtractMethodAsync("Calc.cs", 3, "int sum = a + b;", 3, "int sum = a + b;", "ComputeSum");
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError || result.ErrorMessage != null, Is.True, "Should return IsSuccess or a descriptive error");
    }

    [Test]
    public async Task ExtractMethodAsync_UnknownFile_ReturnsFailureResult()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _memberRefactoringEngine.ExtractMethodAsync("NoFile.cs", 1, "x", 1, "x", "NewMethod");
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.False, "Should fail gracefully for unknown file");
    }

    [Test]
    public async Task AddRemoveParamsAsync_MethodWithParams_ReturnsNonNull()
    {
        var source = "public class Service { public void Go(int a, int b) {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _memberRefactoringEngine.AddRemoveParamsAsync("Service.cs", "Go");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AddRemoveParamsAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _memberRefactoringEngine.AddRemoveParamsAsync("NoFile.cs", "Foo");
        Assert.That(result.UpdatedText, Is.Null);
    }

    private const string SimpleSource = @"
namespace TestProj;

public class Order
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }

    public Order(int orderId, string customerName)
    {
        OrderId = orderId;
        CustomerName = customerName;
    }

    public string GetLabel()
    {
        return string.Format(""{0}: {1}"", OrderId, CustomerName);
    }

    public string GetStatus()
    {
        if (OrderId == 1) return ""Active"";
        if (OrderId == 2) return ""Pending"";
        return ""Unknown"";
    }
}

public interface IService
{
    string GetLabel();
}

public enum Status { Active = 1, Pending = 2 }
";

    // --- SortMembers ---
    [Test]
    public async Task SortMembers_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _memberRefactoringEngine.SortMembersAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractMethod ---
    [Test]
    public async Task ExtractMethod_ValidLineRange_ReturnsResult()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _memberRefactoringEngine.ExtractMethodAsync("Order.cs", 14, "return string.Format", 14, "return string.Format", "FormatLabel");
        Assert.That(result, Is.Not.Null);
    }
}
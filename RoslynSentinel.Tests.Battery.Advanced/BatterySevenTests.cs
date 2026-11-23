#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Support;

namespace RoslynSentinel.Tests.Battery.Advanced;

// ════════════════════════════════════════════════════════════════════════════════
// C. AdvancedTypeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
[Category("StructuralRefactoringEngine")] // sentinel:auto-category
public class AdvancedTypeEngineTests
{
    private IWorkspaceManager _workspaceManager;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
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
        var result = await TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager).ConvertTupleToClassAsync("Test.cs", "GetData", "DataResult");
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
        await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () => await TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager).ConvertTupleToClassAsync("Test.cs", "Calculate", "Result"));
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
        var result = await TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager).ChangePropertyTypeAsync("Test.cs", "Product", "Price", "decimal");
        var key = result.Keys.FirstOrDefault(k => k.Contains("Test.cs"));
        Assert.That(key.Absolute, Is.Not.Null.And.Not.Empty, "Should return changes for the modified file");
        Assert.That(result[key!], Does.Contain("decimal Price"), "Property type should be updated to decimal");
        Assert.That(result[key!], Does.Contain("string Name"), "Other properties should be unchanged");
    }

    [Test]
    public async Task ChangePropertyType_PropertyNotFound_Throws()
    {
        SetSource(@"public class Foo { public int Bar { get; set; } }");
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager).ChangePropertyTypeAsync("Test.cs", "Foo", "NonExistent", "string"));
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
        var result = await TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager).ConvertAnonymousToNamedAsync("Test.cs", "ItemDto");
        var newClassKey = result.Keys.FirstOrDefault(k => k.Contains("ItemDto.cs"));
        Assert.That(newClassKey.Absolute, Is.Not.Null.And.Not.Empty, "Should generate ItemDto.cs");
        Assert.That(result[newClassKey!], Does.Contain("ItemDto"), "Generated class should be named ItemDto");
        Assert.That(result[newClassKey!], Does.Contain("Name"), "Should extract Name property from anonymous type");
        Assert.That(result[newClassKey!], Does.Contain("Quantity"), "Should extract Quantity property from anonymous type");
    }

}

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class ApiGenerationEngineTests
{
    private IWorkspaceManager _workspaceManager;


    private ApiGenerationEngine _engine;


    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];


    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
        _engine = new ApiGenerationEngine(_workspaceManager);
    }


    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();


    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }


    [Test]
    public async Task AddValidationToPoco_UnknownFile_ReportsWithoutThrowing()
    {
        var result = await _engine.AddValidationToPocoAsync("NoSuchFile.cs", "PersonDto");
        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified), "Engines report not-found through Outcome instead of throwing.");
    }


    [Test]
    public async Task AddValidationToPoco_StringProperty_AddsRequiredAttribute()
    {
        const string source = @"
public class PersonDto
{
    public string Name { get; set; }
}";
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("PersonDto.cs", source)]));
        var result = await _engine.AddValidationToPocoAsync("PersonDto.cs", "PersonDto");
        Assert.That(result.UpdatedText, Does.Contain("Required"), "string property should get [Required] attribute");
        Assert.That(result.UpdatedText, Does.Contain("StringLength"), "string property should get [StringLength] attribute");
    }


    [Test]
    public async Task AddValidationToPoco_IntProperty_AddsRangeAttribute()
    {
        const string source = @"
public class ProductDto
{
    public int Quantity { get; set; }
}";
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("ProductDto.cs", source)]));
        var result = await _engine.AddValidationToPocoAsync("ProductDto.cs", "ProductDto");
        Assert.That(result.UpdatedText, Does.Contain("Range"), "int property should get [Range] attribute");
    }


    [Test]
    public async Task GenerateHttpClient_WithTypedActionResult_ProducesClientWithTypedReturn()
    {
        SetSource(@"
public class ProductsController
{
    public Task<ActionResult<List<Product>>> GetAll() => Task.FromResult(null!);
    public Task<ActionResult<Product>> GetById(int id) => Task.FromResult(null!);
}");
        var result = await _engine.GenerateHttpClientForControllerAsync("Test.cs", "ProductsController");

        Assert.That(result.UpdatedText, Does.Contain("ProductsClient"), "Client class name should strip 'Controller'");
        Assert.That(result.UpdatedText, Does.Contain("GetAll"), "Should generate method for GetAll");
        Assert.That(result.UpdatedText, Does.Contain("GetById"), "Should generate method for GetById");
        Assert.That(result.UpdatedText, Does.Contain("using System.Net.Http.Json;"), "Should include HttpClient using");
        // Task<ActionResult<T>> should be simplified to Task<T>
        Assert.That(result.UpdatedText, Does.Contain("Task<List<Product>>").Or.Contain("Task<Product>"),
            "Should strip ActionResult wrapper from return types");
    }


    [Test]
    public async Task GenerateHttpClient_VoidAndActionResultReturns_GeneratesTaskMethods()
    {
        SetSource(@"
public class OrdersController
{
    public void Delete(int id) { }
    public ActionResult Create(string name) => null!;
    public IActionResult Update(int id) => null!;
}");
        var result = await _engine.GenerateHttpClientForControllerAsync("Test.cs", "OrdersController");

        // void, ActionResult, IActionResult -> all become Task
        Assert.That(result.UpdatedText, Does.Contain("async Task Delete"), "void return -> Task");
        Assert.That(result.UpdatedText, Does.Contain("async Task Create"), "ActionResult -> Task");
        Assert.That(result.UpdatedText, Does.Contain("async Task Update"), "IActionResult -> Task");
    }


    [Test]
    public async Task GenerateHttpClient_FileNotFound_ReturnsEmpty()
    {
        SetSource("public class C { }", "Test.cs");

        var result = await _engine.GenerateHttpClientForControllerAsync("Missing.cs", "MyController");

        Assert.That(result.UpdatedText, Is.Null, "Should return null when file not found");
    }


    [Test]
    public async Task GenerateHttpClient_ControllerNotFound_ReturnsEmpty()
    {
        SetSource(@"public class SomeOtherClass { public void Method() { } }");

        var result = await _engine.GenerateHttpClientForControllerAsync("Test.cs", "MissingController");

        Assert.That(result.UpdatedText, Is.Null, "Should return null when controller class not found");
    }
}

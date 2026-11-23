#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;
/// <summary>
/// Battery #6 -> Functional tests for three engines with 4–5 test-mentions but no real coverage:
///   A. DocumentationEngine   (4 tests) -> GenerateXmlDocStubs, DocumentPocoFields
///   B. SolutionStructureEngine   (5 tests) -> ConvertToBackgroundService, FindCircularDependencies
///   C. ApiGenerationEngine   (4 tests) -> GenerateHttpClientForController, return-type mapping
///
/// Solution-management operations (spawn real powershell.exe processes) are excluded -> integration only.
///
/// Total: 13 tests. All workspace-based (SetSource / SetMultipleFiles).
/// </summary>
 // ════════════════════════════════════════════════════════════════════════════════
// A. DocumentationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("DocumentationEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class DocumentationEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private DocumentationEngine _engine;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.DocumentationEngine(_workspaceManager);
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
    public async Task GenerateXmlDocStubs_PublicMethodsWithoutDocs_AddsXmlComments()
    {
        SetSource(@"
public class OrderService
{
    public int GetOrderCount(int userId) { return 0; }
    public void DeleteOrder(int id, string reason) { }
    private void AuditLog() { }
}");
        var result = await _engine.GenerateXmlDocumentationStubsAsync("Test.cs");
        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"), "Should add XML summary tags");
        Assert.That(result.UpdatedText, Does.Contain("GetOrderCount"), "Public method name should appear in TODO comment");
        Assert.That(result.UpdatedText, Does.Contain("DeleteOrder"), "Second public method should also get docs");
        Assert.That(result.UpdatedText, Does.Contain("<param name=\"userId\">"), "Should add <param> for each parameter");
        Assert.That(result.UpdatedText, Does.Contain("<returns>"), "Non-void return should get <returns> tag");
    }

    [Test]
    public async Task GenerateXmlDocStubs_PrivateMethods_NotDocumented()
    {
        SetSource(@"
public class Auditor
{
    private void LogInternal(string msg) { }
}");
        var result = await _engine.GenerateXmlDocumentationStubsAsync("Test.cs");
        // Private methods are filtered out -> no summary should appear
        Assert.That(result.UpdatedText, Does.Not.Contain("/// <summary>"), "Private methods should not receive XML doc stubs");
    }

    [Test]
    public async Task GenerateXmlDocStubs_FileNotFound_ThrowsFileNotFound()
    {
        SetSource("public class C { }", "Test.cs");
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.GenerateXmlDocumentationStubsAsync("Missing.cs"));
    }

    [Test]
    public async Task DocumentPocoFields_ClassWithProperties_AddsDescriptionAttributes()
    {
        SetSource(@"
public class UserDto
{
    public int UserId { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
}");
        var result = await _engine.DocumentPocoFieldsAsync("Test.cs", "UserDto");
        Assert.That(result.UpdatedText, Does.Contain("[Description("), "Should add Description attributes");
        Assert.That(result.UpdatedText, Does.Contain("UserId"), "Property names should be preserved");
        Assert.That(result.UpdatedText, Does.Contain("System.ComponentModel"), "Should add using for ComponentModel");
    }
}
// C. ApiGenerationEngine
// ════════════════════════════════════════════════════════════════════════════════

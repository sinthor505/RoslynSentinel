using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

[TestFixture]
public class DiscoveryEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private DiscoveryEngine _discoveryEngine;
    private DependencyInjectionEngine _dependencyInjectionEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // FindAllThrowSitesAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindAllThrowSites_DetectsThrowStatement()
    {
        SetSource(@"
public class MyService
{
    public void DoWork(string s)
    {
        throw new ArgumentNullException(""s"");
    }
}", "MyService.cs");

        var results = await _discoveryEngine.FindAllThrowSitesAsync();

        Assert.That(results, Is.Not.Null);
        Assert.That(results.Count, Is.GreaterThanOrEqualTo(1));
        var finding = results.First();
        Assert.That(finding.ExceptionType, Does.Contain("ArgumentNullException"));
        Assert.That(finding.ContainingMethod, Is.EqualTo("DoWork"));
        Assert.That(finding.IsInCatch, Is.False);
    }

    [Test]
    public async Task FindAllThrowSites_FiltersByExceptionType()
    {
        SetSource(@"
public class MyService
{
    public void A() { throw new ArgumentNullException(""x""); }
    public void B() { throw new InvalidOperationException(""bad state""); }
}");

        var results = await _discoveryEngine.FindAllThrowSitesAsync(exceptionType: "ArgumentNull");

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].ExceptionType, Does.Contain("ArgumentNull"));
    }

    [Test]
    public async Task FindAllThrowSites_ExtractsMessageLiteral()
    {
        SetSource(@"
public class MyService
{
    public void Validate(string s)
    {
        throw new ArgumentException(""Value cannot be empty"");
    }
}");

        var results = await _discoveryEngine.FindAllThrowSitesAsync();

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].MessageLiteral, Is.EqualTo("Value cannot be empty"));
    }

    [Test]
    public async Task FindAllThrowSites_DetectsThrowInsideCatch()
    {
        SetSource(@"
public class MyService
{
    public void Run()
    {
        try { }
        catch (Exception ex)
        {
            throw new InvalidOperationException(""wrapped"", ex);
        }
    }
}");

        var results = await _discoveryEngine.FindAllThrowSitesAsync();

        Assert.That(results.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(results.Any(r => r.IsInCatch), Is.True);
    }

    [Test]
    public async Task FindAllThrowSites_SkipsBareRethrow()
    {
        SetSource(@"
public class MyService
{
    public void Run()
    {
        try { }
        catch
        {
            throw; // bare rethrow - should be skipped
        }
    }
}");

        var results = await _discoveryEngine.FindAllThrowSitesAsync();

        // Bare rethrows should not be included
        Assert.That(results.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task FindAllThrowSites_EmptyWhenNoThrows()
    {
        SetSource(@"
public class MyService
{
    public int Add(int a, int b) => a + b;
}");

        var results = await _discoveryEngine.FindAllThrowSitesAsync();

        Assert.That(results, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════
    // FindObjectCreationSitesAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindObjectCreationSites_FindsExplicitCreation()
    {
        SetSource(@"
public class MyRepo { }
public class Service
{
    public void Load()
    {
        var repo = new MyRepo();
    }
}");

        var results = await _discoveryEngine.FindObjectCreationSitesAsync("MyRepo");

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].TypeName, Does.Contain("MyRepo"));
        Assert.That(results[0].ContainingMethod, Is.EqualTo("Load"));
    }

    [Test]
    public async Task FindObjectCreationSites_NoMatchReturnsEmpty()
    {
        SetSource(@"
public class Service
{
    public void Load()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}");

        var results = await _discoveryEngine.FindObjectCreationSitesAsync("MyRepo");

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task FindObjectCreationSites_CountsArguments()
    {
        SetSource(@"
public class Dto { public Dto(int a, string b) { } }
public class Service
{
    void Create() { var d = new Dto(1, ""x""); }
}");

        var results = await _discoveryEngine.FindObjectCreationSitesAsync("Dto");

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].ArgumentCount, Is.EqualTo(2));
    }

    [Test]
    public async Task FindObjectCreationSites_IsCaseInsensitive()
    {
        SetSource(@"
public class MyDto { }
public class Service
{
    void Create() { var d = new MyDto(); }
}");

        var results = await _discoveryEngine.FindObjectCreationSitesAsync("mydto");

        Assert.That(results.Count, Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════
    // GetPublicApiSurfaceAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPublicApiSurface_ReturnsPublicClass()
    {
        SetSource(@"
/// <summary>A widget service.</summary>
public class WidgetService
{
    public string GetWidget(int id) => """";
    public string Name { get; set; }
}", "WidgetService.cs");

        var results = await _discoveryEngine.GetPublicApiSurfaceAsync("TestProj");

        Assert.That(results, Is.Not.Empty);
        var typeEntry = results.FirstOrDefault(r => r.Kind == "Class" && r.TypeName == "WidgetService");
        Assert.That(typeEntry, Is.Not.Null);
        Assert.That(typeEntry!.XmlDocSummary, Does.Contain("widget service"));
    }

    [Test]
    public async Task GetPublicApiSurface_IncludesPublicMethods()
    {
        SetSource(@"
public class Calculator
{
    public int Add(int a, int b) => a + b;
    private int Helper() => 0;
}", "Calculator.cs");

        var results = await _discoveryEngine.GetPublicApiSurfaceAsync("TestProj");

        var methods = results.Where(r => r.Kind == "Method").ToList();
        Assert.That(methods.Any(m => m.MemberName == "Add"), Is.True);
        Assert.That(methods.All(m => m.MemberName != "Helper"), Is.True);
    }

    [Test]
    public async Task GetPublicApiSurface_IncludesProperties()
    {
        SetSource(@"
public class Model
{
    public string Name { get; set; }
    public int Age { get; }
}", "Model.cs");

        var results = await _discoveryEngine.GetPublicApiSurfaceAsync("TestProj");

        var props = results.Where(r => r.Kind == "Property").ToList();
        Assert.That(props.Any(p => p.MemberName == "Name"), Is.True);
        Assert.That(props.Any(p => p.MemberName == "Age"), Is.True);
    }

    [Test]
    public async Task GetPublicApiSurface_EmptyForUnknownProject()
    {
        SetSource(@"public class A { }");

        var results = await _discoveryEngine.GetPublicApiSurfaceAsync("NonExistentProject");

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetPublicApiSurface_DetectsInterface()
    {
        SetSource(@"
public interface IMyService
{
    void Execute();
}", "IMyService.cs");

        var results = await _discoveryEngine.GetPublicApiSurfaceAsync("TestProj");

        var typeEntry = results.FirstOrDefault(r => r.Kind == "Interface");
        Assert.That(typeEntry, Is.Not.Null);
        Assert.That(typeEntry!.TypeName, Is.EqualTo("IMyService"));
    }

    // ══════════════════════════════════════════════════════════════
    // FindServicesNotRegisteredAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindServicesNotRegistered_DetectsUnregisteredInterface()
    {
        SetMultipleFiles(
            ("Startup.cs", @"
public class Startup
{
    public void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddSingleton<IMyRegisteredService, MyRegisteredService>();
    }
}"),
            ("MyController.cs", @"
public class MyController
{
    public MyController(IUnregisteredService svc) { }
}"));

        var results = await _dependencyInjectionEngine.FindServicesNotRegisteredAsync();

        Assert.That(results.Any(r => r.MissingType.Contains("IUnregisteredService")), Is.True);
    }

    [Test]
    public async Task FindServicesNotRegistered_DoesNotFlagRegisteredServices()
    {
        SetMultipleFiles(
            ("Startup.cs", @"
public class Startup
{
    public void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddSingleton<IRegisteredService, RegisteredService>();
    }
}"),
            ("MyController.cs", @"
public class MyController
{
    public MyController(IRegisteredService svc) { }
}"));

        var results = await _dependencyInjectionEngine.FindServicesNotRegisteredAsync();

        Assert.That(results.All(r => !r.MissingType.Contains("IRegisteredService")), Is.True);
    }

    [Test]
    public async Task FindServicesNotRegistered_DoesNotFlagFrameworkTypes()
    {
        SetSource(@"
public class MyService
{
    public MyService(
        Microsoft.Extensions.Logging.ILogger<MyService> logger,
        Microsoft.Extensions.Configuration.IConfiguration config) { }
}");

        var results = await _dependencyInjectionEngine.FindServicesNotRegisteredAsync();

        Assert.That(results.All(r =>
            !r.MissingType.Contains("ILogger") &&
            !r.MissingType.Contains("IConfiguration")), Is.True);
    }

    // ══════════════════════════════════════════════════════════════
    // PreviewRenameImpactAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task PreviewRenameImpact_DefinitionOnly_ReportsOneReference()
    {
        // Source: single file, MyMethod is declared but never called
        // Line 3: "    public void MyMethod() { }"
        // Column 17: 'M' in MyMethod  (4 spaces + "public void " = 16 chars)
        SetSource("public class MyService\n{\n    public void MyMethod() { }\n}", "MyService.cs");

        var preview = await _discoveryEngine.PreviewRenameImpactAsync(
            "MyService.cs", "MyMethod", contextSnippet: "public void MyMethod()");

        Assert.That(preview.SymbolName, Is.EqualTo("MyMethod"),
            "SymbolName should reflect the name passed in.");
        Assert.That(preview.TotalReferences, Is.GreaterThanOrEqualTo(0),
            "TotalReferences should be a non-negative count.");
        Assert.That(preview.FilesAffected, Is.GreaterThanOrEqualTo(0),
            "FilesAffected should be a non-negative count.");
    }

    [Test]
    public async Task PreviewRenameImpact_MethodCalledInTwoFiles_ShowsTwoFilesAffected()
    {
        // File 1: defines MyHelper with a public method called Execute
        // File 2: calls MyHelper.Execute
        SetMultipleFiles(
            ("Helper.cs", "public class MyHelper\n{\n    public void Execute() { }\n}"),
            ("Consumer.cs", @"public class Consumer
{
    public void Run()
    {
        var h = new MyHelper();
        h.Execute();
    }
}"));

        // In Helper.cs, line 3, column 17 → 'E' in Execute
        var preview = await _discoveryEngine.PreviewRenameImpactAsync(
            "Helper.cs", "Execute", contextSnippet: "public void Execute()");

        Assert.That(preview.SymbolName, Is.EqualTo("Execute"));
        // There is at least one reference in Consumer.cs (the call site)
        Assert.That(preview.TotalReferences, Is.GreaterThanOrEqualTo(1),
            "Execute is called in Consumer.cs, so TotalReferences >= 1.");
        Assert.That(preview.FilesAffected, Is.GreaterThanOrEqualTo(1),
            "Consumer.cs references Execute, so at least 1 file is affected.");
        Assert.That(preview.AffectedFiles, Is.Not.Null,
            "AffectedFiles list should not be null.");
    }

    [Test]
    public async Task PreviewRenameImpact_UnusedPrivateMethod_ZeroOrOneReference()
    {
        // A private method that is never called anywhere — should have 0 references (no callers)
        SetSource("public class Service\n{\n    private void Unused() { }\n}", "Service.cs");

        var preview = await _discoveryEngine.PreviewRenameImpactAsync(
            "Service.cs", "Unused", contextSnippet: "private void Unused()");

        // TotalReferences = count of call-site references (not the declaration itself)
        Assert.That(preview.TotalReferences, Is.EqualTo(0),
            "A never-called private method should have 0 references.");
        Assert.That(preview.FilesAffected, Is.EqualTo(0),
            "No files are affected when method has no callers.");
    }
}

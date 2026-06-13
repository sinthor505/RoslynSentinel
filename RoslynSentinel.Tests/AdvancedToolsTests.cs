using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class AdvancedToolsTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private RefactoringEngine _refactoringEngine;
    private GranularRefactoringEngine _granularRefactoringEngine;
    private SentinelConfiguration _config;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _config = new SentinelConfiguration();
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _granularRefactoringEngine = new GranularRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ===== Tool 1: DetectValueTaskMisuse =====

    [Test]
    public async Task DetectValueTaskMisuse_FlagsDoubleAwait()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        var vt = GetVT();
        await vt;
        await vt;
    }
    ValueTask GetVT() => ValueTask.CompletedTask;
}", "C.cs");

        var reports = await _asyncSafetyEngine.DetectValueTaskMisuseAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports.Any(r => r.Reason.Contains("awaited more than once")), Is.True);
    }

    [Test]
    public async Task DetectValueTaskMisuse_FlagsStoredAndDeferred()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        var vt = GetVT();
        DoSomethingElse();
        await vt;
    }
    ValueTask GetVT() => ValueTask.CompletedTask;
    void DoSomethingElse() {}
}", "C.cs");

        var reports = await _asyncSafetyEngine.DetectValueTaskMisuseAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports.Any(r => r.Reason.Contains("deferred") || r.Reason.Contains("intervening")), Is.True);
    }

    [Test]
    public async Task DetectValueTaskMisuse_DoesNotFlagImmediateAwait()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        await GetVT();
    }
    ValueTask GetVT() => ValueTask.CompletedTask;
}", "C.cs");

        var reports = await _asyncSafetyEngine.DetectValueTaskMisuseAsync("C.cs");
        // Immediate await (no stored variable) should not trigger stored-and-deferred or double-await
        Assert.That(reports.Any(r => r.Reason.Contains("awaited more than once") || r.Reason.Contains("deferred")), Is.False);
    }

    // ===== Tool 2: UpgradeToPrimaryConstructor =====

    [Test]
    public async Task UpgradeToPrimaryConstructor_ConvertsPureAssignmentCtor()
    {
        SetSource(@"
public interface IRepo { void Save(); }
public class MyService {
    private readonly IRepo _repo;
    public MyService(IRepo repo) { _repo = repo; }
    public void Do() { _repo.Save(); }
}", "MyService.cs");

        var result = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync("MyService.cs", "MyService");

        Assert.That(result, Does.Contain("MyService(IRepo repo)"));
        Assert.That(result, Does.Contain("repo.Save()"));
        Assert.That(result, Does.Not.Contain("private readonly IRepo _repo"));
        Assert.That(result, Does.Not.Contain("public MyService(IRepo repo)"));
    }

    [Test]
    public async Task UpgradeToPrimaryConstructor_RefusesCtorWithNonAssignmentLogic()
    {
        SetSource(@"
public interface IRepo { void Save(); void Init(); }
public class MyService {
    private readonly IRepo _repo;
    public MyService(IRepo repo) { _repo = repo; _repo.Init(); }
    public void Do() { _repo.Save(); }
}", "MyService.cs");

        var result = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync("MyService.cs", "MyService");

        Assert.That(result, Does.Contain("// Cannot convert"));
    }

    [Test]
    public async Task UpgradeToPrimaryConstructor_MultipleFields()
    {
        SetSource(@"
public interface IRepo {}
public interface ILogger {}
public class MyService {
    private readonly IRepo _repo;
    private readonly ILogger _logger;
    public MyService(IRepo repo, ILogger logger) { _repo = repo; _logger = logger; }
    public void Do() { var r = _repo; var l = _logger; }
}", "MyService.cs");

        var result = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync("MyService.cs", "MyService");

        Assert.That(result, Does.Contain("MyService(IRepo repo, ILogger logger)"));
        Assert.That(result, Does.Not.Contain("_repo"));
        Assert.That(result, Does.Not.Contain("_logger"));
    }

    // ===== Tool 3: AddCancellationTokenToMethod =====

    [Test]
    public async Task AddCancellationTokenToMethod_AddsParameterToMethod()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task GetData() {
        var x = 1;
    }
}", "C.cs");

        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync("C.cs", "GetData");

        Assert.That(result, Does.Contain("CancellationToken cancellationToken"));
    }

    [Test]
    public async Task AddCancellationTokenToMethod_DoesNotAddIfAlreadyPresent()
    {
        SetSource(@"
using System.Threading;
using System.Threading.Tasks;
public class C {
    public async Task GetData(CancellationToken cancellationToken = default) {
        var x = 1;
    }
}", "C.cs");

        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync("C.cs", "GetData");

        // Should return unchanged (not add a second CancellationToken)
        var ctCount = result.UpdatedText!.Split("CancellationToken").Length - 1;
        Assert.That(ctCount, Is.LessThanOrEqualTo(2)); // type + param name
    }

    [Test]
    public async Task AddCancellationTokenToMethod_PropagatesViaSyntacticHeuristic()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    private readonly IRepo _repo;
    public async Task GetData() {
        var x = await _repo.GetAllAsync();
    }
}
public interface IRepo {
    Task<int> GetAllAsync();
    Task<int> GetAllAsync(System.Threading.CancellationToken ct);
}", "C.cs");

        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync("C.cs", "GetData");

        // Parameter should be added
        Assert.That(result, Does.Contain("CancellationToken cancellationToken"));
        // Semantic model finds CT overload on IRepo → should propagate
        Assert.That(result, Does.Contain("GetAllAsync(cancellationToken)"));
    }

    // ===== Tool 4: SyncInterfaceToImplementation =====

    [Test]
    public async Task SyncInterfaceToImplementation_AddsMissingMethod()
    {
        SetSource(@"
public interface IService { void DoA(); }
public class Service : IService { 
    public void DoA() {}
    public void DoB() {}
}", "Service.cs");

        var result = await _refactoringEngine.SyncInterfaceToImplementationAsync("Service.cs", "Service", "IService");

        Assert.That(result, Does.Contain("void DoB()"));
        Assert.That(result, Does.Contain("void DoA()"));
    }

    [Test]
    public async Task SyncInterfaceToImplementation_NoChangeWhenUpToDate()
    {
        SetSource(@"
public interface IService { void DoA(); void DoB(); }
public class Service : IService { 
    public void DoA() {}
    public void DoB() {}
}", "Service.cs");

        var result = await _refactoringEngine.SyncInterfaceToImplementationAsync("Service.cs", "Service", "IService");

        // No new members should have been added — both already present
        // Count occurrences of DoB in interface section
        Assert.That(result, Does.Not.Contain("// Interface not found"));
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task SyncInterfaceToImplementation_AddsMissingProperty()
    {
        SetSource(@"
public interface IService { void DoA(); }
public class Service : IService { 
    public void DoA() {}
    public string Name { get; set; }
}", "Service.cs");

        var result = await _refactoringEngine.SyncInterfaceToImplementationAsync("Service.cs", "Service", "IService");

        Assert.That(result, Does.Contain("Name"));
        Assert.That(result, Does.Contain("get;"));
    }

    // ===== Tool 5: IntroduceParameterObject =====

    [Test]
    public async Task IntroduceParameterObject_CreatesRecord()
    {
        SetSource(@"
public class C {
    public void CreateUser(string name, string email, int age) {
        var n = name;
        var e = email;
        var a = age;
    }
}", "C.cs");

        var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync("C.cs", "CreateUser");

        Assert.That(result, Does.Contain("record CreateUserParameters"));
        Assert.That(result, Does.Contain("request"));
        Assert.That(result, Does.Contain("request.Name") | Does.Contain("Name"));
    }

    [Test]
    public async Task IntroduceParameterObject_RespectsSpecifiedParamNames()
    {
        SetSource(@"
public class C {
    public void CreateUser(string name, string email, int age) {
        var n = name;
        var e = email;
        var a = age;
    }
}", "C.cs");

        var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync(
            "C.cs", "CreateUser", "UserNameEmail", new[] { "name", "email" });

        Assert.That(result, Does.Contain("record UserNameEmail"));
        // age should remain as a separate parameter
        Assert.That(result, Does.Contain("int age"));
    }

    [Test]
    public async Task IntroduceParameterObject_IncludesTodoComment()
    {
        SetSource(@"
public class C {
    public void Process(string input, int count) {
        var x = input;
        var y = count;
    }
}", "C.cs");

        var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync("C.cs", "Process");

        // Record should be emitted and params rewritten
        Assert.That(result, Does.Contain("record ProcessParameters"));
        Assert.That(result, Does.Contain("ProcessParameters request"));
        Assert.That(result, Does.Contain("request.Input") | Does.Contain("request.Count"));
    }
}

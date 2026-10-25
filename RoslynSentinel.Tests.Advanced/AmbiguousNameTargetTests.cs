using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

// Name-keyed lookups refuse an ambiguous name with ToolAmbiguousMatchException instead of taking the first match.
[TestFixture]
[Category("AmbiguousNameTarget")]
public class AmbiguousNameTargetTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private AsyncOptimizationEngine _asyncOptimization;
    private CodeGenerationEngine _codeGeneration;
    private CodeStyleEngine _codeStyle;
    private DeadCodeEngine _deadCode;
    private DependencyInjectionEngine _dependencyInjection;
    private CloneDetectionEngine _cloneDetection;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _asyncOptimization = new AsyncOptimizationEngine(_workspaceManager);
        _codeGeneration = new CodeGenerationEngine(_workspaceManager);
        _codeStyle = new CodeStyleEngine(_workspaceManager, _config);
        _deadCode = new DeadCodeEngine(_workspaceManager);
        _dependencyInjection = new DependencyInjectionEngine(_workspaceManager);
        _cloneDetection = new CloneDetectionEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task OptimizeToValueTask_TwoOverloadsWithSameName_ThrowsAmbiguousMatch()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task<int> GetDataAsync()
    {
        await Task.Delay(1);
        return 1;
    }

    public async Task<int> GetDataAsync(int id)
    {
        await Task.Delay(1);
        return id;
    }
}", "Service.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _asyncOptimization.OptimizeToValueTaskAsync("Service.cs", "GetDataAsync"));

        Assert.That(ex!.Message, Does.Contain("GetDataAsync"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task FlagMultipleMigrationCandidates_AmbiguousItem_ReportedPerItemWhileUniqueItemFlagged()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task<int> GetDataAsync() { await Task.Delay(1); return 1; }
    public async Task<int> GetDataAsync(int id) { await Task.Delay(1); return id; }
    public async Task SaveAsync() { await Task.Delay(1); }
}", "Service.cs");

        var items = new List<(FilePathWrapper FilePath, string MethodName, string Pattern, int Score, string? Reason)>
        {
            ("Service.cs", "GetDataAsync", "AsyncOverload", 90, null),
            ("Service.cs", "SaveAsync", "AsyncOverload", 90, null),
        };

        var (results, errors) = await _asyncOptimization.FlagMultipleMigrationCandidatesAsync(items);

        var ambiguous = errors.Single(e => e.Index == 0);
        Assert.That(ambiguous.Error, Does.Contain("GetDataAsync"));
        Assert.That(ambiguous.Error, Does.Contain("matches 2 declarations"));
        Assert.That(errors.Select(e => e.Index), Does.Not.Contain(1),
            "The unique SaveAsync item should not be reported as an error.");
        Assert.That(results[1].Changes, Is.Not.Empty,
            "The unique SaveAsync item should still be flagged.");
    }

    [Test]
    public async Task GenerateConstructor_TwoClassesNamedFooInDifferentNamespaces_ThrowsAmbiguousMatch()
    {
        SetSource(@"
namespace A
{
    public class Foo
    {
        private readonly int _x;
    }
}

namespace B
{
    public class Foo
    {
        private readonly int _y;
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _codeGeneration.GenerateConstructorAsync("Test.cs", "Foo"));

        Assert.That(ex!.Message, Does.Contain("Foo"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task ConvertPropertySafe_SameNamedPropertyInBaseAndDerived_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Base
{
    public int Size { get; set; }
}

public class Derived : Base
{
    public new int Size { get; set; }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _codeGeneration.ConvertPropertySafeAsync("Test.cs", "Size", "ToFullProperty"));

        Assert.That(ex!.Message, Does.Contain("Size"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task ConvertPropertyToMethods_SameNamedPropertyInBaseAndDerived_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Base
{
    public int Size { get; set; }
}

public class Derived : Base
{
    public new int Size { get; set; }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _codeStyle.ConvertPropertyToMethodsAsync("Test.cs", "Size"));

        Assert.That(ex!.Message, Does.Contain("Size"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task FindUnusedPrivateMembers_TwoClassesNamedWorkerInDifferentNamespaces_ThrowsAmbiguousMatch()
    {
        SetSource(@"
namespace A
{
    public class Worker
    {
        private int _unusedA;
    }
}

namespace B
{
    public class Worker
    {
        private int _unusedB;
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _deadCode.FindUnusedPrivateMembersAsync("Test.cs", "Worker"));

        Assert.That(ex!.Message, Does.Contain("Worker"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task AnalyzeDependencies_TwoClassesNamedServiceInDifferentNamespaces_ThrowsAmbiguousMatch()
    {
        SetSource(@"
namespace A
{
    public class Service
    {
        private readonly int _a;
    }
}

namespace B
{
    public class Service
    {
        private readonly int _b;
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _dependencyInjection.AnalyzeDependenciesAsync("Test.cs", "Service"));

        Assert.That(ex!.Message, Does.Contain("Service"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task FindDuplicateBlocksInClass_TwoClassesNamedWorkerInDifferentNamespaces_ThrowsAmbiguousMatch()
    {
        SetSource(@"
namespace A
{
    public class Worker
    {
        public void Run() { var x = 1; var y = 2; var z = 3; var w = 4; }
    }
}

namespace B
{
    public class Worker
    {
        public void Run() { var x = 1; var y = 2; var z = 3; var w = 4; }
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _cloneDetection.FindDuplicateBlocksInClassAsync("Test.cs", "Worker"));

        Assert.That(ex!.Message, Does.Contain("Worker"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task OptimizeToValueTask_InterfaceMethodWithSingleImplementation_DoesNotThrow()
    {
        SetSource(@"
using System.Threading.Tasks;
public interface IRepo
{
    Task<int> GetAsync();
}

public class Repo : IRepo
{
    public async Task<int> GetAsync()
    {
        await Task.Delay(1);
        return 1;
    }
}", "Repo.cs");

        await Assert.DoesNotThrowAsync(
            async () => await _asyncOptimization.OptimizeToValueTaskAsync("Repo.cs", "GetAsync"));
    }
}

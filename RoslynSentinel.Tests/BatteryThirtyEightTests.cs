using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the path-driven test generation capability (Battery 38):
///  1. Missing method returns error gracefully (no exception).
///  2. Simple method with no branches → exactly one happy-path test.
///  3. If-with-null-check → happy path + true branch (null param).
///  4. If-with-else → happy path + both branches reported.
///  5. ForEach loop → happy path + empty-collection + with-items cases.
///  6. Switch statement → happy path + one case per switch label.
///  7. Async method → generated test stubs are async Task.
///  8. Method with interface dependency → mock setup comment in arrange.
///  9. Framework=xunit → constructor setup, [Fact] attribute, correct class name.
/// 10. GeneratedTestCode is non-empty and contains the test class name.
/// 11. For loop over param bound → zero / one / multiple iteration stubs.
/// 12. While loop writing to returned variable → never-enters / terminates stubs.
/// 13. Do-while over param condition → single-pass / multi-pass stubs.
/// 14. For loop with constant bound (no param) → not reported (irrelevant loop filter).
/// 15. While loop that doesn't touch params or return value → not reported.
/// </summary>
[TestFixture]
public class BatteryThirtyEightTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private PathDrivenTestEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new PathDrivenTestEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetProject(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("MyApp.Service", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private async Task<string> GetDocPath()
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        return solution.Projects.First().Documents.First().FilePath ?? "Test.cs";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1 — Missing method → graceful error result (no exception)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_MethodNotFound_ReturnsErrorInCode()
    {
        SetProject("public class Foo { public void Exists() { } }");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "DoesNotExist");

        Assert.That(report.PathCount, Is.EqualTo(0), "No paths for a missing method");
        Assert.That(report.GeneratedTestCode, Does.Contain("Error"),
            "Error message must appear in GeneratedTestCode when method is not found");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2 — Simple method with no branches → exactly one happy-path test
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_MethodWithNoBranches_ProducesOnlyHappyPath()
    {
        SetProject(@"
public class OrderService {
    public string GetName(string id) { return id; }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "GetName");

        Assert.That(report.PathCount, Is.EqualTo(1), "A branchless method has exactly one path — the happy path");
        Assert.That(report.TestCases[0].TestMethodName, Does.Contain("HappyPath"),
            "First test must be the happy-path stub");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3 — If with null check → happy path + true (null) branch
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_NullCheck_ProducesNullAndNonNullPaths()
    {
        SetProject(@"
public class OrderService {
    public string Process(string id) {
        if (id == null) { return ""empty""; }
        return id;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Process");

        Assert.That(report.PathCount, Is.GreaterThanOrEqualTo(2),
            "Null-check if must produce at least happy path + null branch");

        var trueBranchCase = report.TestCases.FirstOrDefault(t =>
            t.InputConstraints.Any(c => c.SuggestedValue == "null"));
        Assert.That(trueBranchCase, Is.Not.Null,
            "True branch (id == null) must have a test case with SuggestedValue = null");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4 — If-with-else → both branches in the report
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_IfWithElse_ProducesBothBranchCases()
    {
        SetProject(@"
public class OrderService {
    public string Classify(int score) {
        if (score > 0) { return ""positive""; }
        else { return ""non-positive""; }
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Classify");

        // Expect: happy path + true branch + false/else branch = at least 3
        Assert.That(report.PathCount, Is.GreaterThanOrEqualTo(3),
            "if-with-else should produce happy path + true branch + false/else branch");

        bool hasTrue = report.TestCases.Any(t => t.ScenarioDescription.Contains("true"));
        bool hasFalse = report.TestCases.Any(t => t.ScenarioDescription.Contains("false"));
        Assert.That(hasTrue, Is.True, "Must have a test case for the true branch");
        Assert.That(hasFalse, Is.True, "Must have a test case for the false/else branch");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5 — ForEach loop → empty and with-items cases
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_ForEachLoop_ProducesEmptyAndWithItemsCases()
    {
        SetProject(@"
using System.Collections.Generic;
public class OrderService {
    public int Sum(List<int> items) {
        int total = 0;
        foreach (var item in items) { total += item; }
        return total;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Sum");

        bool hasEmpty = report.TestCases.Any(t => t.ScenarioDescription.Contains("empty collection"));
        bool hasItems = report.TestCases.Any(t => t.ScenarioDescription.Contains("has items"));
        Assert.That(hasEmpty, Is.True, "ForEach must generate an empty-collection test case");
        Assert.That(hasItems, Is.True, "ForEach must generate a with-items test case");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6 — Switch statement → one case per switch label
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_SwitchStatement_ProducesPerCasePaths()
    {
        SetProject(@"
public class OrderService {
    public string Describe(string status) {
        switch (status) {
            case ""active"": return ""Active order"";
            case ""cancelled"": return ""Cancelled"";
            case ""pending"": return ""Pending"";
            default: return ""Unknown"";
        }
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Describe");

        var switchCases = report.TestCases
            .Where(t => t.ScenarioDescription.Contains("Switch on"))
            .ToList();

        Assert.That(switchCases.Count, Is.GreaterThanOrEqualTo(2),
            "Switch with 3 non-default labels must produce at least 2 switch-case test stubs");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7 — Async method → generated test stubs are async Task
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_AsyncMethod_GeneratesAsyncTestStubs()
    {
        SetProject(@"
using System.Threading.Tasks;
public class OrderService {
    public async Task<string> LoadAsync(string id) {
        if (id == null) return """";
        return id;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "LoadAsync");

        Assert.That(report.GeneratedTestCode, Does.Contain("async Task"),
            "Generated test methods must be async Task for an async method-under-test");
        Assert.That(report.GeneratedTestCode, Does.Contain("await"),
            "Act line must use await for an async method");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8 — Method with interface dependency → mock comment in happy-path arrange
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_InterfaceDependency_IncludesMockSetupComment()
    {
        SetProject(@"
using System.Threading.Tasks;
public interface IOrderRepo {
    Task<string> GetAsync(string id);
}
public class OrderService {
    private readonly IOrderRepo _orderRepo;
    public OrderService(IOrderRepo orderRepo) { _orderRepo = orderRepo; }
    public async Task<string> FindAsync(string id) {
        var result = await _orderRepo.GetAsync(id);
        return result;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "FindAsync");

        var happyPath = report.TestCases.First();
        Assert.That(happyPath.ArrangeCode, Does.Contain("_mockOrderRepo"),
            "Happy path arrange must reference the mock field for the injected IOrderRepo");
        Assert.That(happyPath.ArrangeCode, Does.Contain("GetAsync"),
            "Happy path arrange must mention the callee method for mock setup");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9 — Framework=xunit → [Fact] attribute, constructor setup
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_XunitFramework_UsesFactAndConstructorSetup()
    {
        SetProject(@"
public class OrderService {
    public string Get(string id) {
        if (id == null) return """";
        return id;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Get", framework: "xunit");

        Assert.That(report.GeneratedTestCode, Does.Contain("[Fact]"),
            "xUnit framework must emit [Fact] test attribute");
        Assert.That(report.GeneratedTestCode, Does.Not.Contain("[Test]"),
            "[Test] is NUnit — must not appear in xunit output");
        Assert.That(report.GeneratedTestCode, Does.Not.Contain("[SetUp]"),
            "[SetUp] is NUnit — constructor-based setup must be used for xunit");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 10 — GeneratedTestCode is non-empty and contains expected class name
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_GeneratedCode_ContainsExpectedClassName()
    {
        SetProject(@"
public class PaymentProcessor {
    public void Run(int amount) {
        if (amount > 0) { }
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Run");

        Assert.That(report.GeneratedTestCode, Is.Not.Empty,
            "GeneratedTestCode must be non-empty");
        Assert.That(report.GeneratedTestCode, Does.Contain("PaymentProcessor"),
            "GeneratedTestCode must reference the class under test");
        Assert.That(report.GeneratedTestCode, Does.Contain("Run"),
            "GeneratedTestCode must reference the method under test");
        Assert.That(report.ClassName, Is.EqualTo("PaymentProcessor"),
            "Report ClassName must be correctly set from the containing class");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 11 — For loop with param-controlled bound → zero / one / multiple stubs
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_ForLoopWithParamBound_ProducesIterationCases()
    {
        SetProject(@"
public class BatchService {
    public int Sum(int count) {
        int total = 0;
        for (int i = 0; i < count; i++) { total += i; }
        return total;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Sum");

        bool hasZero    = report.TestCases.Any(t => t.ScenarioDescription.Contains("never executes") ||
                                                    t.ScenarioDescription.Contains("ZeroIterations") ||
                                                    t.TestMethodName.Contains("Zero"));
        bool hasOne     = report.TestCases.Any(t => t.TestMethodName.Contains("One") ||
                                                    t.ScenarioDescription.Contains("exactly once"));
        bool hasMulti   = report.TestCases.Any(t => t.TestMethodName.Contains("Multiple") ||
                                                    t.ScenarioDescription.Contains("N > 1"));

        Assert.That(hasZero,  Is.True, "For loop must produce a zero-iterations test case");
        Assert.That(hasOne,   Is.True, "For loop must produce a one-iteration boundary test case");
        Assert.That(hasMulti, Is.True, "For loop must produce a multiple-iterations test case");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 12 — While loop writing to returned variable → never-enters / terminates
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_WhileLoopOnOutgoingVar_ProducesEnterAndNeverEnterCases()
    {
        SetProject(@"
using System.Collections.Generic;
public class QueueService {
    public int Drain(Queue<int> queue) {
        int total = 0;
        while (queue.Count > 0) { total += queue.Dequeue(); }
        return total;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Drain");

        bool hasNeverEnters = report.TestCases.Any(t => t.TestMethodName.Contains("NeverEnters") ||
                                                        t.ScenarioDescription.Contains("never"));
        bool hasTerminates  = report.TestCases.Any(t => t.TestMethodName.Contains("Terminates") ||
                                                        t.ScenarioDescription.Contains("terminates") ||
                                                        t.ScenarioDescription.Contains("executes then"));

        Assert.That(hasNeverEnters, Is.True,
            "While loop must produce a never-enters case (condition false from start)");
        Assert.That(hasTerminates, Is.True,
            "While loop must produce a terminates case (executes then exits)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 13 — Do-while over param condition → single-pass / multi-pass stubs
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_DoWhileLoop_ProducesSingleAndMultiPassCases()
    {
        SetProject(@"
public class RetryService {
    public int Retry(int maxRetries) {
        int attempts = 0;
        do { attempts++; } while (attempts < maxRetries);
        return attempts;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "Retry");

        bool hasSinglePass = report.TestCases.Any(t => t.TestMethodName.Contains("SinglePass") ||
                                                       t.ScenarioDescription.Contains("once"));
        bool hasMultiPass  = report.TestCases.Any(t => t.TestMethodName.Contains("MultiplePasses") ||
                                                       t.ScenarioDescription.Contains("multiple times"));

        Assert.That(hasSinglePass, Is.True,
            "Do-while must produce a single-pass case (condition false after first body execution)");
        Assert.That(hasMultiPass, Is.True,
            "Do-while must produce a multi-pass case (body runs N > 1 times)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 14 — For loop with constant bound (no param ref) → not reported
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_ForLoopConstantBound_NotReported()
    {
        SetProject(@"
public class FixedService {
    public int FixedSum() {
        int total = 0;
        for (int i = 0; i < 10; i++) { total += i; }
        return total;
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "FixedSum");

        bool hasForLoop = report.TestCases.Any(t =>
            t.TestMethodName.Contains("ZeroIterations") ||
            t.TestMethodName.Contains("OneIteration") ||
            t.TestMethodName.Contains("MultipleIterations"));

        Assert.That(hasForLoop, Is.False,
            "A for loop with a constant bound (no param, no outgoing var) must not produce loop test cases — it is not in the path of any variable of interest");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 15 — While loop not touching params or return value → not reported
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GeneratePathDrivenTests_WhileLoopIrrelevant_NotReported()
    {
        SetProject(@"
using System.Threading;
public class SpinService {
    public void WaitForFlag() {
        bool flag = false;
        // spins on a local — no param reference, nothing in return path
        while (!flag) { flag = true; }
    }
}");
        var docPath = await GetDocPath();

        var report = await _engine.GeneratePathDrivenTestsAsync(docPath, "WaitForFlag");

        bool hasWhileLoop = report.TestCases.Any(t =>
            t.TestMethodName.Contains("NeverEnters") ||
            t.TestMethodName.Contains("Terminates"));

        Assert.That(hasWhileLoop, Is.False,
            "A while loop that neither references a parameter nor writes to a returned variable must not generate test cases");
    }
}

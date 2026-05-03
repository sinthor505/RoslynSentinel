using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the 11 new engine methods added to fill tool gaps,
/// plus the 5 bug-fixed engine methods.
/// </summary>
[TestFixture]
public class ToolGapsTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private ThreadSafetyEngine _threadSafetyEngine;
    private DiscoveryEngine _discoveryEngine;
    private AntiPatternEngine _antiPatternEngine;
    private CodeStyleEngine _codeStyleEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private RefactoringEngine _refactoringEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _threadSafetyEngine = new ThreadSafetyEngine(_workspaceManager);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager);
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        var config = new SentinelConfiguration();
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, config);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, config);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 1: MakeMethodThreadSafeAsync — lockFieldName parameter
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task MakeMethodThreadSafe_UsesCustomLockFieldName()
    {
        SetSource(
            "public class C { public void DoWork() { int x = 1; } }",
            "C.cs");
        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync("C.cs", "DoWork", "_myLock");
        Assert.That(result, Does.Contain("_myLock"));
        Assert.That(result, Does.Not.Contain("_lock"));
    }

    [Test]
    public async Task MakeMethodThreadSafe_DefaultLockFieldName_IsUsed()
    {
        SetSource(
            "public class C { public void DoWork() { int x = 1; } }",
            "C.cs");
        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync("C.cs", "DoWork");
        Assert.That(result, Does.Contain("_lock"));
    }

    [Test]
    public async Task MakeMethodThreadSafe_ThrowsIfExistingFieldNotObject()
    {
        SetSource(
            "public class C { private readonly System.Threading.SemaphoreSlim _lock = new(1,1); public void DoWork() { int x = 1; } }",
            "C.cs");
        string result = null!;
        Assert.DoesNotThrowAsync(async () =>
            result = await _threadSafetyEngine.MakeMethodThreadSafeAsync("C.cs", "DoWork", "_lock"));
        Assert.That(result, Does.StartWith("// Error:"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 2: FindTaskWhenAllUsageAsync — dependency-aware sequential detection
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindTaskWhenAll_Flags_SequentialIndependentAwaits()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        var a = await System.Threading.Tasks.Task.FromResult(1);
        var b = await System.Threading.Tasks.Task.FromResult(2);
    }
}", "C.cs");
        var reports = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("Task.WhenAll"));
    }

    [Test]
    public async Task FindTaskWhenAll_DoesNotFlag_DependentAwaits()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        var a = await System.Threading.Tasks.Task.FromResult(1);
        var b = await System.Threading.Tasks.Task.FromResult(a);
    }
}", "C.cs");
        var reports = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindAsyncOverSync
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindAsyncOverSync_Flags_AsyncMethodWithNoAwait()
    {
        SetSource(
            "public class C { public async System.Threading.Tasks.Task M() { int x = 1; } }",
            "C.cs");
        var reports = await _asyncSafetyEngine.FindAsyncOverSyncAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("no await"));
    }

    [Test]
    public async Task FindAsyncOverSync_Flags_AsyncMethodOnlyAwaitingFromResult()
    {
        SetSource(
            "using System.Threading.Tasks; public class C { public async Task<int> M() { return await Task.FromResult(42); } }",
            "C.cs");
        var reports = await _asyncSafetyEngine.FindAsyncOverSyncAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("Task.FromResult"));
    }

    [Test]
    public async Task FindAsyncOverSync_DoesNotFlag_RealAsync()
    {
        SetSource(
            "public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(10); } }",
            "C.cs");
        var reports = await _asyncSafetyEngine.FindAsyncOverSyncAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindUnawaitedFireAndForget
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindUnawaitedFireAndForget_Flags_UnawastedAsyncCall()
    {
        SetSource(@"
public class C {
    public void M() { DoWorkAsync(); }
    public System.Threading.Tasks.Task DoWorkAsync() => System.Threading.Tasks.Task.CompletedTask;
}", "C.cs");
        var reports = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("DoWorkAsync"));
    }

    [Test]
    public async Task FindUnawaitedFireAndForget_DoesNotFlag_AwaitedCall()
    {
        SetSource(@"
public class C {
    public async System.Threading.Tasks.Task M() { await DoWorkAsync(); }
    public System.Threading.Tasks.Task DoWorkAsync() => System.Threading.Tasks.Task.CompletedTask;
}", "C.cs");
        var reports = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    [Test]
    public async Task FindUnawaitedFireAndForget_DoesNotFlag_NonAsyncSuffix()
    {
        SetSource(
            "public class C { public void M() { DoWork(); } public void DoWork() {} }",
            "C.cs");
        var reports = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindLongParameterList
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindLongParameterList_Flags_MethodWithFourParams()
    {
        SetSource(
            "public class C { public void M(int a, int b, int c, int d) {} }",
            "C.cs");
        var findings = await _antiPatternEngine.FindLongParameterListAsync("C.cs");
        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Pattern, Is.EqualTo("LongParameterList"));
    }

    [Test]
    public async Task FindLongParameterList_DoesNotFlag_TwoParamMethod()
    {
        SetSource(
            "public class C { public void M(int a, int b) {} }",
            "C.cs");
        var findings = await _antiPatternEngine.FindLongParameterListAsync("C.cs");
        Assert.That(findings, Is.Empty);
    }

    [Test]
    public async Task FindLongParameterList_RespectsCustomMinParameters()
    {
        SetSource(
            "public class C { public void M(int a, int b, int c) {} }",
            "C.cs");
        var findings = await _antiPatternEngine.FindLongParameterListAsync("C.cs", minParameters: 3);
        Assert.That(findings, Is.Not.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindPrimitiveObsession
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindPrimitiveObsession_Flags_ThreeStringsInMethod()
    {
        SetSource(
            "public class C { public void M(string a, string b, string c) {} }",
            "C.cs");
        var findings = await _antiPatternEngine.FindPrimitiveObsessionAsync("C.cs");
        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Pattern, Is.EqualTo("PrimitiveObsession"));
    }

    [Test]
    public async Task FindPrimitiveObsession_DoesNotFlag_TwoStringsInMethod()
    {
        SetSource(
            "public class C { public void M(string a, string b, int c) {} }",
            "C.cs");
        var findings = await _antiPatternEngine.FindPrimitiveObsessionAsync("C.cs");
        Assert.That(findings, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindInconsistentAsyncSuffix
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindInconsistentAsyncSuffix_Flags_AsyncMethodWithoutSuffix()
    {
        SetSource(
            "public class C { public async System.Threading.Tasks.Task DoWork() { await System.Threading.Tasks.Task.Delay(1); } }",
            "C.cs");
        var findings = await _antiPatternEngine.FindInconsistentAsyncSuffixAsync("C.cs");
        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Pattern, Is.EqualTo("InconsistentAsyncSuffix"));
        Assert.That(findings[0].Description, Does.Contain("DoWorkAsync"));
    }

    [Test]
    public async Task FindInconsistentAsyncSuffix_Flags_SyncMethodWithAsyncSuffix()
    {
        SetSource(
            "public class C { public int GetValueAsync() { return 42; } }",
            "C.cs");
        var findings = await _antiPatternEngine.FindInconsistentAsyncSuffixAsync("C.cs");
        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Description, Does.Contain("Remove 'Async' suffix"));
    }

    [Test]
    public async Task FindInconsistentAsyncSuffix_DoesNotFlag_ProperlyNamedMethod()
    {
        SetSource(
            "public class C { public async System.Threading.Tasks.Task DoWorkAsync() { await System.Threading.Tasks.Task.Delay(1); } }",
            "C.cs");
        var findings = await _antiPatternEngine.FindInconsistentAsyncSuffixAsync("C.cs");
        Assert.That(findings, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindBestInsertionPoint
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindBestInsertionPoint_ReturnsLineForMethod_InEmptyClass()
    {
        SetSource(
            "public class MyClass { }",
            "MyClass.cs");
        var result = await _discoveryEngine.FindBestInsertionPointAsync("MyClass.cs", "MyClass", "method");
        Assert.That(result.InsertBeforeLine, Is.GreaterThan(0));
        Assert.That(result.ContainerName, Is.EqualTo("MyClass"));
    }

    [Test]
    public async Task FindBestInsertionPoint_ReturnsAfterLastField_WhenAddingField()
    {
        SetSource(
            "public class MyClass { private int _a; private int _b; public void M() {} }",
            "MyClass.cs");
        var result = await _discoveryEngine.FindBestInsertionPointAsync("MyClass.cs", "MyClass", "field");
        Assert.That(result.Reason, Does.Contain("field"));
        Assert.That(result.InsertBeforeLine, Is.GreaterThan(0));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindTodoFixmeComments
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindTodoFixmeComments_FindsTodoComment()
    {
        SetSource(
            "public class C { // TODO: fix this\n public void M() {} }",
            "C.cs");
        var findings = await _discoveryEngine.FindTodoFixmeCommentsAsync("C.cs");
        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings.Any(f => f.Kind == "TODO"), Is.True);
    }

    [Test]
    public async Task FindTodoFixmeComments_FindsFixmeComment()
    {
        SetSource(
            "public class C { // FIXME: broken\n public void M() {} }",
            "C.cs");
        var findings = await _discoveryEngine.FindTodoFixmeCommentsAsync("C.cs");
        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings.Any(f => f.Kind == "FIXME"), Is.True);
    }

    [Test]
    public async Task FindTodoFixmeComments_ReturnsEmpty_WhenNoKeywords()
    {
        SetSource(
            "public class C { // regular comment\n public void M() {} }",
            "C.cs");
        var findings = await _discoveryEngine.FindTodoFixmeCommentsAsync("C.cs");
        Assert.That(findings, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: FindUseFrozenCollections
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindUseFrozenCollections_Flags_PrivateStaticReadonlyDictionary()
    {
        SetSource(
            "public class C { private static readonly System.Collections.Generic.Dictionary<string, int> _map = new(); }",
            "C.cs");
        var findings = await _codeStyleEngine.FindUseFrozenCollectionsAsync("C.cs");
        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Pattern, Is.EqualTo("UseFrozenCollection"));
        Assert.That(findings[0].Description, Does.Contain("FrozenDictionary"));
    }

    [Test]
    public async Task FindUseFrozenCollections_DoesNotFlag_InstanceDictionary()
    {
        // Not static — should not be flagged
        SetSource(
            "public class C { private readonly System.Collections.Generic.Dictionary<string, int> _map = new(); }",
            "C.cs");
        var findings = await _codeStyleEngine.FindUseFrozenCollectionsAsync("C.cs");
        Assert.That(findings, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: UseExceptionExpressions
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task UseExceptionExpressions_ReplacesArgumentNullException()
    {
        SetSource(@"
public class C {
    public void M(string s) {
        if (s == null) throw new ArgumentNullException(nameof(s));
    }
}", "C.cs");
        var result = await _syntaxUpgradeEngine.UseExceptionExpressionsAsync("C.cs", "M");
        Assert.That(result, Does.Contain("ThrowIfNull"));
        Assert.That(result, Does.Not.Contain("new ArgumentNullException"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // New: UpdateXmlDocsFromSignature
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateXmlDocsFromSignature_AddsMissingParamTag()
    {
        SetSource(@"
public class C {
    /// <summary>Does something.</summary>
    public void M(string name) {}
}", "C.cs");
        var result = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync("C.cs", "M");
        Assert.That(result, Does.Contain("<param name=\"name\""));
    }

    [Test]
    public async Task UpdateXmlDocsFromSignature_RemovesStaleParamTag()
    {
        SetSource(@"
public class C {
    /// <summary>Does something.</summary>
    /// <param name=""oldParam"">An old param.</param>
    public void M(string name) {}
}", "C.cs");
        var result = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync("C.cs", "M");
        Assert.That(result, Does.Not.Contain("oldParam"));
        Assert.That(result, Does.Contain("name"));
    }

    [Test]
    public async Task UpdateXmlDocsFromSignature_NoOp_WhenNoXmlDocs()
    {
        SetSource(
            "public class C { public void M(string name) {} }",
            "C.cs");
        var result = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync("C.cs", "M");
        // Should now GENERATE XML docs when they're missing (BUG-59 fix)
        Assert.That(result, Does.Contain("public void M"));
        Assert.That(result, Does.Contain("<summary>"), "Should generate XML docs when missing");
        Assert.That(result, Does.Contain("<param name=\"name\">"), "Should generate param tags");
    }
}

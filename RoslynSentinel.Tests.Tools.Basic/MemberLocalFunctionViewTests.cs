// Tool-layer coverage for Member(view) on a local function: a unique local function is returned with its
// note, a same-named method or class wins over it (with a retry hint), and two local functions need
// containerName to pick one.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("RefactoringStructuralImpl")]
public class MemberLocalFunctionViewTests
{
    private const string FileName = "MemberLocalFunctionView.cs";

    private const string UniqueSource = "public class Holder\n{\n    public void Run()\n    {\n        void Helper(int x)\n        {\n        }\n        Helper(1);\n    }\n}";

    private const string MethodAndLocalSource = "public class Holder\n{\n    public void Helper() { }\n    public void Run()\n    {\n        void Helper() { }\n        Helper();\n    }\n}";

    private const string ClassAndLocalSource = "public class Helper\n{\n}\npublic class Holder\n{\n    public void Run()\n    {\n        void Helper() { }\n    }\n}";

    private const string TwoLocalsSource = "public class Holder\n{\n    public void A()\n    {\n        void Helper() { }\n    }\n    public void B()\n    {\n        void Helper() { }\n    }\n}";

    private IWorkspaceManager _workspaceManager = null!;
    private RefactoringStructuralImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var symbolNavigationEngine = TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = TestEngines.ValidationEngine(_workspaceManager);
        _impl = TestTools.RefactoringStructuralImpl(
            TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, TestEngines.SentinelConfiguration()),
            TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNavigationEngine, validationEngine),
            TestEngines.StructuralRefinementEngine(_workspaceManager),
            symbolNavigationEngine,
            _workspaceManager,
            validationEngine,
            NullLogger<RefactoringStructuralImpl>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    [Test]
    public async Task Member_View_UniqueLocalFunction_ReturnsSourceWithNote()
    {
        LoadSource(UniqueSource);

        var result = await _impl.Member(
            reason: "local function view test: unique local function Helper",
            filePath: FileName,
            operation: MemberAction.view,
            memberName: "Helper");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var member = ((MemberSourceViewResult)result.SuccessData!).Member;
        Assert.That(member.Kind, Is.EqualTo("localFunction"));
        Assert.That(member.Source, Does.Contain("void Helper(int x)"));
        Assert.That(result.Findings.Any(f => f.Source == "MemberSource"
            && f.Message.Contains("is a local function inside Holder.Run")), Is.True);
    }

    [Test]
    public async Task Member_View_MethodAndLocalFunctionSameName_MethodWinsWithRetryHint()
    {
        LoadSource(MethodAndLocalSource);

        var result = await _impl.Member(
            reason: "local function view test: method Helper shadows local function Helper",
            filePath: FileName,
            operation: MemberAction.view,
            memberName: "Helper");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var member = ((MemberSourceViewResult)result.SuccessData!).Member;
        Assert.That(member.Kind, Is.EqualTo("method"));
        Assert.That(member.Source, Does.Contain("public void Helper()"));
        Assert.That(result.Findings.Any(f => f.Source == "MemberSource"
            && f.Message.Contains("retry with containerName")), Is.True);
    }

    [Test]
    public async Task Member_View_ClassAndLocalFunctionSameName_ClassWins()
    {
        LoadSource(ClassAndLocalSource);

        var result = await _impl.Member(
            reason: "local function view test: class Helper shadows local function Helper",
            filePath: FileName,
            operation: MemberAction.view,
            memberName: "Helper");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var member = ((MemberSourceViewResult)result.SuccessData!).Member;
        Assert.That(member.Kind, Is.EqualTo("class"));
        Assert.That(member.Source, Does.Contain("public class Helper"));
    }

    [Test]
    public async Task Member_View_TwoLocalFunctionsSameName_AmbiguousNamesBothEnclosingMembers()
    {
        LoadSource(TwoLocalsSource);

        var ambiguous = await _impl.Member(
            reason: "local function view test: two local functions named Helper without containerName",
            filePath: FileName,
            operation: MemberAction.view,
            memberName: "Helper");

        Assert.That(ambiguous.IsError, Is.True);
        Assert.That(ambiguous.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(ambiguous.ErrorData.Message, Does.Contain("inside Holder.A"));
        Assert.That(ambiguous.ErrorData.Message, Does.Contain("inside Holder.B"));
    }

    [Test]
    public async Task Member_View_TwoLocalFunctionsSameName_ContainerNamePicksEnclosingMethod()
    {
        LoadSource(TwoLocalsSource);

        var result = await _impl.Member(
            reason: "local function view test: containerName selects the local function inside method A",
            filePath: FileName,
            operation: MemberAction.view,
            memberName: "Helper",
            containerName: "A");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var member = ((MemberSourceViewResult)result.SuccessData!).Member;
        Assert.That(member.Kind, Is.EqualTo("localFunction"));
        Assert.That(member.StartLine, Is.EqualTo(5));
        Assert.That(member.Source, Does.Contain("void Helper()"));
    }

    private void LoadSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(FileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }
}

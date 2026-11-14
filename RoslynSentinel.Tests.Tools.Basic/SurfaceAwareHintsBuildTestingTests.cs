using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// Pins the zero-project BuildNotRun messages of BuildEngine to HEAD text on the full tool surface.
// Expected hints are rendered with ToolCallHint.Call so the goldens follow the schema catalog the
// production code uses; the surrounding sentence is copied verbatim from HEAD.
[TestFixture]
[Category("BuildEngine")]
public class SurfaceAwareHintsBuildTestingTests
{
    [Test]
    public async Task RunQuickBuildAsync_ZeroProjects_FullSurfaceMessageMatchesHead()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunQuickBuildAsync(ToolScope.solution, null, maxDetails: 50, CancellationToken.None);

        var listAll = ToolCallHint.Call("ListAll", ("kind", "all"));
        var expected = "Quick build compiled zero projects. Scope 'solution' with scopeName '' resolved to nothing -- no compile verdict is available. Call "
            + listAll + " to see valid project/file names.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunFullBuildAsync_NoProjects_FullSurfaceMessageMatchesHead()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        workspaceManager.SolutionPath = "C:/fake/EmptySolution.slnx";
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50);

        var loadSolution = ToolCallHint.Call("LoadSolution");
        var listAll = ToolCallHint.Call("ListAll", ("kind", "all"));
        var expected = "Full build compiled zero projects. The loaded solution contains no projects -- no compile verdict is available. Call "
            + loadSolution + " with a populated .slnx/.sln, or " + listAll + " to inspect the current workspace.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunFullBuildAsync_UnknownProjectScope_FullSurfaceMessageMatchesHead()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "Real", new[] { ("C:/fake/Real/RealValue.cs", "namespace Fake; public static class RealValue { }") }));
        workspaceManager.SolutionPath = "C:/fake/Real.slnx";
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
            level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: "Nope");

        var listAll = ToolCallHint.Call("ListAll", ("kind", "all"));
        var expected = "Full build compiled zero projects. Scope 'project' with scopeName 'Nope' resolved to no project in the loaded solution -- no compile verdict is available. Call "
            + listAll + " to see valid project/file names.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunQuickBuildAsync_ZeroProjects_AllCallablePredicateMatchesHead()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunQuickBuildAsync(ToolScope.solution, null, maxDetails: 50, CancellationToken.None, isCallable: _ => true);

        var listAll = ToolCallHint.Call("ListAll", ("kind", "all"));
        var expected = "Quick build compiled zero projects. Scope 'solution' with scopeName '' resolved to nothing -- no compile verdict is available. Call "
            + listAll + " to see valid project/file names.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunFullBuildAsync_NoProjects_AllCallablePredicateMatchesHead()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        workspaceManager.SolutionPath = "C:/fake/EmptySolution.slnx";
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50, isCallable: _ => true);

        var loadSolution = ToolCallHint.Call("LoadSolution");
        var listAll = ToolCallHint.Call("ListAll", ("kind", "all"));
        var expected = "Full build compiled zero projects. The loaded solution contains no projects -- no compile verdict is available. Call "
            + loadSolution + " with a populated .slnx/.sln, or " + listAll + " to inspect the current workspace.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunFullBuildAsync_UnknownProjectScope_AllCallablePredicateMatchesHead()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "Real", new[] { ("C:/fake/Real/RealValue.cs", "namespace Fake; public static class RealValue { }") }));
        workspaceManager.SolutionPath = "C:/fake/Real.slnx";
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
            level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: "Nope", isCallable: _ => true);

        var listAll = ToolCallHint.Call("ListAll", ("kind", "all"));
        var expected = "Full build compiled zero projects. Scope 'project' with scopeName 'Nope' resolved to no project in the loaded solution -- no compile verdict is available. Call "
            + listAll + " to see valid project/file names.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));

        workspaceManager.Dispose();
    }

    private static readonly HashSet<string> LeanSurface = new(StringComparer.Ordinal)
    {
        "ReadFile", "GetFileOutline", "Search", "ListSolutionItems", "GetLargeResult", "ReplaceSnippet",
        "Member", "RenameSymbol", "CreateFile", "DeleteFile", "Build", "RunTest", "GetMethodSource",
        "UndoLastApply", "ChangeSignature", "ChangeAccessibility", "ModifyModifier", "UsingDirective",
    };

    private static bool LeanPredicate(string toolName) => LeanSurface.Contains(toolName);

    private static void AssertOnlyLeanCalls(string message)
    {
        Assert.That(message, Does.Not.Contain("ListAll"));
        Assert.That(message, Does.Not.Contain("LoadSolution"));
        foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(message, @"\b([A-Z][A-Za-z]+)\(reason:"))
        {
            Assert.That(LeanSurface.Contains(call.Groups[1].Value), Is.True, message);
        }
    }

    private static void AssertNoCallableHint(string message)
    {
        Assert.That(message, Does.EndWith("is available."));
        Assert.That(message, Does.Not.Contain("(reason:"));
    }

    [Test]
    public async Task RunQuickBuildAsync_ZeroProjects_LeanSurfaceNamesOnlyCallableListing()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunQuickBuildAsync(ToolScope.solution, null, maxDetails: 50, CancellationToken.None, isCallable: LeanPredicate);

        var listing = ToolCallHint.Call("ListSolutionItems", ("kind", "all"));
        var expected = "Quick build compiled zero projects. Scope 'solution' with scopeName '' resolved to nothing -- no compile verdict is available. Call "
            + listing + " to see valid project/file names.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));
        AssertOnlyLeanCalls(result.Error.Message);

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunFullBuildAsync_NoProjects_LeanSurfaceNamesOnlyCallableListing()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        workspaceManager.SolutionPath = "C:/fake/EmptySolution.slnx";
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50, isCallable: LeanPredicate);

        var listing = ToolCallHint.Call("ListSolutionItems", ("kind", "all"));
        var expected = "Full build compiled zero projects. The loaded solution contains no projects -- no compile verdict is available. Call "
            + listing + " to inspect the current workspace.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));
        AssertOnlyLeanCalls(result.Error.Message);

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunFullBuildAsync_UnknownProjectScope_LeanSurfaceNamesOnlyCallableListing()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "Real", new[] { ("C:/fake/Real/RealValue.cs", "namespace Fake; public static class RealValue { }") }));
        workspaceManager.SolutionPath = "C:/fake/Real.slnx";
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
            level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: "Nope", isCallable: LeanPredicate);

        var listing = ToolCallHint.Call("ListSolutionItems", ("kind", "all"));
        var expected = "Full build compiled zero projects. Scope 'project' with scopeName 'Nope' resolved to no project in the loaded solution -- no compile verdict is available. Call "
            + listing + " to see valid project/file names.";
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Is.EqualTo(expected));
        AssertOnlyLeanCalls(result.Error.Message);

        workspaceManager.Dispose();
    }

    [Test]
    public async Task RunBuildAsync_ZeroProjects_NothingCallableEndsWithoutListingCall()
    {
        var quickManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        quickManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        var quickEngine = TestEngines.BuildEngine(quickManager, TestEngines.DiagnosticEngine(quickManager));
        var quick = await quickEngine.RunQuickBuildAsync(ToolScope.solution, null, maxDetails: 50, CancellationToken.None, isCallable: _ => false);
        AssertNoCallableHint(quick.Error!.Message);
        quickManager.Dispose();

        var emptyManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        emptyManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        emptyManager.SolutionPath = "C:/fake/EmptySolution.slnx";
        var emptyEngine = TestEngines.BuildEngine(emptyManager, TestEngines.DiagnosticEngine(emptyManager));
        var noProjects = await emptyEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50, isCallable: _ => false);
        AssertNoCallableHint(noProjects.Error!.Message);
        emptyManager.Dispose();

        var projectManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        projectManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "Real", new[] { ("C:/fake/Real/RealValue.cs", "namespace Fake; public static class RealValue { }") }));
        projectManager.SolutionPath = "C:/fake/Real.slnx";
        var projectEngine = TestEngines.BuildEngine(projectManager, TestEngines.DiagnosticEngine(projectManager));
        var unknown = await projectEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
            level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: "Nope", isCallable: _ => false);
        AssertNoCallableHint(unknown.Error!.Message);
        projectManager.Dispose();
    }

    [Test]
    public async Task Build_QuickBuildZeroProjectsNoSurface_NamesListAll()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        var impl = new WorkspaceBuildTestImpl(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager),
            TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager)),
            TestEngines.TestRunEngine(workspaceManager), NullLogger<WorkspaceBuildTestImpl>.Instance);

        var result = await impl.Build("zero-project build hint check", BuildVerifyLevel.quickBuild, ToolScope.solution);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain(ToolCallHint.Call("ListAll", ("kind", "all"))));

        workspaceManager.Dispose();
    }

    [Test]
    public async Task Build_QuickBuildZeroProjectsLeanSurface_NamesOnlyCallableListing()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());

        var none = new HashSet<string>();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel", StringComparison.Ordinal) == true)
            .ToArray();
        var activeClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, className) in McpToolSchemaPatcher.DiscoverAllDeclaredTools(assemblies))
        {
            activeClasses.Add(className);
        }

        var surface = new ActiveToolSurface("test", none, none, none, activeClasses, classModes: null, allowedToolNames: LeanSurface);
        var impl = new WorkspaceBuildTestImpl(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager),
            TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager)),
            TestEngines.TestRunEngine(workspaceManager), NullLogger<WorkspaceBuildTestImpl>.Instance, surface);

        var result = await impl.Build("zero-project build hint check", BuildVerifyLevel.quickBuild, ToolScope.solution);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain(ToolCallHint.Call("ListSolutionItems", ("kind", "all"))));
        Assert.That(result.ErrorData!.Message, Does.Not.Contain("ListAll"));

        workspaceManager.Dispose();
    }
}

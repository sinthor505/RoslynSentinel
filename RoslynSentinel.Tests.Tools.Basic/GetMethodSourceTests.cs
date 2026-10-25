// GetMethodSourceTool -> WorkspaceTools. Zero coverage before this file (GetTestCoverageMap
// flagged branches: document == null, method == null, methodBytes > threshold, plus happy path).
// root == null is not exercised here -> GetSyntaxRootAsync only returns null for non-source
// documents, which TestSolutionBuilder never produces, so that branch is effectively unreachable
// from these fixtures.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("MethodSourceResult")] // sentinel:auto-category
[Category("WorkspaceTools")] // sentinel:auto-category
public class GetMethodSourceTests
{
    private FakeWorkspaceManager _workspaceManager;
    private WorkspaceTools _tools;
    private string _documentPath;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = FakeWorkspaceManager.ReadOnly();
        _documentPath = Path.Combine(Path.GetTempPath(), "GetMethodSourceTests_" + Guid.NewGuid().ToString("N"), "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Foo\n{\n    public int Bar(int x)\n    {\n        return x + 1;\n    }\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(_workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, config);
        var dependencyEngine = new DependencyEngine(_workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(_workspaceManager);
        _tools = new WorkspaceTools(
            _workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(_workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(_workspaceManager),
            new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task GetMethodSource_ExistingMethod_ReturnsSourceAndSignatureAsync()
    {
        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Bar");

        Assert.That(!result.IsError, Is.True);
        var data = (MethodSourceResult)result.SuccessData!;
        Assert.That(data.Source, Does.Contain("return x + 1;"));
        Assert.That(data.Signature, Does.Contain("Bar"));
        Assert.That(data.Envelope, Is.Not.Null);
        Assert.That(data.Envelope.LineCount, Is.EqualTo(4));
        Assert.That(data.Envelope.TotalLinesInFile, Is.EqualTo(8));
        Assert.That(data.Envelope.IsComplete, Is.True);
        Assert.That(data.Envelope.ContinuationOffset, Is.Null);
        Assert.That(data.Envelope.ReturnedFromLine, Is.EqualTo(3));
        Assert.That(data.Envelope.ReturnedToLine, Is.EqualTo(6));
    }

    [Test]
    public async Task GetMethodSource_ConstructorRequestedByClassName_ReturnsConstructorSourceAsync()
    {
        var ctorDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "WithCtor.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("WithCtor.cs", "public class WithCtor\n{\n    public WithCtor(int x)\n    {\n    }\n}\n", ctorDocPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", ctorDocPath, "WithCtor");

        Assert.That(!result.IsError, Is.True);
        var data = (MethodSourceResult)result.SuccessData!;
        Assert.That(data.Source, Does.Contain("public WithCtor(int x)"));
    }

    [Test]
    public async Task GetMethodSource_FileNotInSolution_ReturnsFileNotFoundAsync()
    {
        var missingPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "DoesNotExist.cs");

        var result = await _tools.GetMethodSourceTool(reason: "test message", missingPath, "Bar");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("FileNotFound"));
    }

    [Test]
    public async Task GetMethodSource_MethodNameNotInFile_ReturnsMethodNotFoundAsync()
    {
        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "NoSuchMethod");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("MethodNotFound"));
    }

    [Test]
    public async Task GetMethodSource_MethodDeclaredInAnotherFile_MessageNamesThatFileAndRetryAsync()
    {
        var otherDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Other.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Foo\n{\n    public int Bar(int x)\n    {\n        return x + 1;\n    }\n}\n", _documentPath),
                ("Other.cs", "public class Other\n{\n    public void Qux()\n    {\n    }\n}\n", otherDocPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Qux");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Other.cs"));
        Assert.That(message, Does.Contain("Retry GetMethodSource"));
    }

    [Test]
    public async Task GetMethodSource_NameIsPropertyInFile_MessagePointsToMemberViewAsync()
    {
        var propDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Props.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("Props.cs", "public class Props\n{\n    public int Size { get; set; }\n}\n", propDocPath) });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", propDocPath, "Size");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Member(operation: view"));
    }

    [Test]
    public async Task GetMethodSource_QualifiedName_MessageAsksForBareNameAsync()
    {
        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Foo.Bar");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("pass the bare method name"));
    }

    [Test]
    public async Task GetMethodSource_NameExistsNowhere_MessageListsFileMethodsAsync()
    {
        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "NoSuchMethod");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("No method or constructor named"));
        Assert.That(message, Does.Contain("Methods and constructors in this file:"));
    }

    [Test]
    public async Task GetMethodSource_ClassNameWhoseConstructorIsInAnotherFile_MessageNamesThatFileAsync()
    {
        var svcDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Svc.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Foo\n{\n    public int Bar(int x)\n    {\n        return x + 1;\n    }\n}\n", _documentPath),
                ("Svc.cs", "public class SomeClass\n{\n    public SomeClass(int x)\n    {\n    }\n}\n", svcDocPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "SomeClass");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Svc.cs"));
        Assert.That(message, Does.Contain("(constructor)"));
    }

    [Test]
    public async Task GetMethodSource_MethodNameCaseMismatch_FallsBackToCaseInsensitiveMatchAsync()
    {
        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "bar");

        Assert.That(!result.IsError, Is.True);
        var data = (MethodSourceResult)result.SuccessData!;
        Assert.That(data.Signature, Does.Contain("Bar"));
    }

    [Test]
    public async Task GetMethodSource_MethodLargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync()
    {
        var bigDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Big.cs");
        var bigBody = string.Concat(Enumerable.Range(0, 2000).Select(i => $"        var line{i} = {i};\n"));
        var bigSource = $"public class Big\n{{\n    public void Huge()\n    {{\n{bigBody}    }}\n}}\n";
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("Big.cs", bigSource, bigDocPath) });
        _workspaceManager.SetTestSolution(solution);
        // GetMethodSourceTool only offloads when GetSolutionRoot() is non-empty; the fake derives that
        // from SolutionPath since the AdhocWorkspace solution here has no FilePathWrapper of its own.
        _workspaceManager.SolutionPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Test.sln");

        var result = await _tools.GetMethodSourceTool(reason: "test message", bigDocPath, "Huge");

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.LargeResult, Is.Not.Null);
        Assert.That(result.LargeResult!.ResultType, Is.EqualTo("MethodSource"));
    }

    [Test]
    public async Task GetMethodSource_SingleMatch_IsUnchangedAsync()
    {
        var otherDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Other.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Foo\n{\n    public int Bar(int x)\n    {\n        return x + 1;\n    }\n}\n", _documentPath),
                ("Other.cs", "public class Other\n{\n    public int Bar(string s)\n    {\n        return 2;\n    }\n}\n", otherDocPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Bar");

        Assert.That(!result.IsError, Is.True);
        var data = (MethodSourceResult)result.SuccessData!;
        Assert.That(data.Source, Does.Contain("return x + 1;"));
        Assert.That(data.Source, Does.Not.Contain("return 2;"));
    }

    [Test]
    public async Task GetMethodSource_SameNameInTwoTypes_WithoutContainerName_ReturnsAmbiguousListingBothAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public int Run(int x) => 1;\n}\n\npublic class B\n{\n    public int Run(string s) => 2;\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("Ambiguous"));
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("A."));
        Assert.That(message, Does.Contain("B."));
        Assert.That(message, Does.Contain("Run(int x)"));
        Assert.That(message, Does.Contain("Run(string s)"));
        Assert.That(message, Does.Contain("lines"));
        Assert.That(message, Does.Contain("containerName: 'B'"));
        Assert.That(message, Does.Contain("ReadFile("));
        var candidates = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(candidates, Has.Count.EqualTo(2));
        Assert.That(candidates.Select(c => c.ContainingType), Is.EquivalentTo(new[] { "A", "B" }));
        Assert.That(candidates[0].Line, Is.Not.EqualTo(candidates[1].Line));
    }

    [Test]
    public async Task GetMethodSource_SameNameInTwoTypes_WithContainerName_ReturnsTheRightOneAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public int Run(int x) => 1;\n}\n\npublic class B\n{\n    public int Run(string s) => 2;\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var resultB = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "B");
        var resultA = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "A");

        Assert.That(!resultB.IsError, Is.True);
        Assert.That(((MethodSourceResult)resultB.SuccessData!).Source, Does.Contain("=> 2"));
        Assert.That(!resultA.IsError, Is.True);
        Assert.That(((MethodSourceResult)resultA.SuccessData!).Source, Does.Contain("=> 1"));
    }

    [Test]
    public async Task GetMethodSource_OverloadsInOneType_ReturnsAmbiguousListingEverySignatureAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public void Run(int x)\n    {\n    }\n\n    public void Run(string s)\n    {\n    }\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("Ambiguous"));
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Run(int x)"));
        Assert.That(message, Does.Contain("Run(string s)"));
        Assert.That(message, Does.Not.Contain("containerName:"));
        var candidates = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(candidates, Has.Count.EqualTo(2));

        var narrowed = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "A");

        Assert.That(narrowed.IsError, Is.True);
        Assert.That(narrowed.ErrorData!.ErrorCode, Is.EqualTo("Ambiguous"));
        Assert.That(narrowed.ErrorData!.Message, Does.Contain("narrowed to type 'A'"));
        Assert.That(narrowed.ErrorData!.Message, Does.Contain("Run(string s)"));
    }

    [Test]
    public async Task GetMethodSource_AmbiguityRetryHint_ReadFileRange_ReturnsTheNamedOverloadAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public void Run(int x)\n    {\n    }\n\n    public void Run(string s)\n    {\n    }\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var ambiguous = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run");

        Assert.That(ambiguous.IsError, Is.True);
        var hint = System.Text.RegularExpressions.Regex.Match(ambiguous.ErrorData!.Message, @"startLine: (\d+), endLine: (\d+)");
        Assert.That(hint.Success, Is.True);
        var startLine = int.Parse(hint.Groups[1].Value);
        var endLine = int.Parse(hint.Groups[2].Value);
        var candidates = ambiguous.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(startLine, Is.EqualTo(candidates[0].Line));

        var read = await _tools.ReadFileTool(reason: "test message", _documentPath, startLine: startLine, endLine: endLine);

        Assert.That(!read.IsError, Is.True);
        var source = (string)read.SuccessData!.GetType().GetProperty("source")!.GetValue(read.SuccessData)!;
        Assert.That(source, Does.Contain("Run(int x)"));
        Assert.That(source, Does.Not.Contain("Run(string s)"));
    }

    [Test]
    public async Task GetMethodSource_TwoConstructors_ByClassName_ReturnsAmbiguousAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public A(int x)\n    {\n    }\n\n    public A(string s)\n    {\n    }\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "A");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("Ambiguous"));
        Assert.That(result.ErrorData!.Message, Does.Contain("(int x)"));
        Assert.That(result.ErrorData!.Message, Does.Contain("(string s)"));
        var candidates = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(candidates, Has.Count.EqualTo(2));
        Assert.That(candidates.Select(c => c.Kind), Is.All.EqualTo("constructor"));
    }

    [Test]
    public async Task GetMethodSource_InterfaceDeclarationAndImplementationInOneFile_ReturnsTheImplementationAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public interface I\n{\n    int Get();\n}\n\npublic class C : I\n{\n    public int Get() => 3;\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Get");

        Assert.That(!result.IsError, Is.True);
        var data = (MethodSourceResult)result.SuccessData!;
        Assert.That(data.Source, Does.Contain("=> 3"));
    }

    [Test]
    public async Task GetMethodSource_CaseVariantNames_OrdinalMatchWinsWithoutAmbiguityAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public int Run() => 1;\n\n    public int run() => 2;\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run");

        Assert.That(!result.IsError, Is.True);
        var data = (MethodSourceResult)result.SuccessData!;
        Assert.That(data.Source, Does.Contain("=> 1"));
        Assert.That(data.Source, Does.Not.Contain("=> 2"));
    }

    [Test]
    public async Task GetMethodSource_GenericContainerSpellings_AllResolveAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Box<T>\n{\n    public T Get() => default!;\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var bare = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Get", containerName: "Box");
        var withParameter = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Get", containerName: "Box<T>");

        Assert.That(!bare.IsError, Is.True);
        Assert.That(((MethodSourceResult)bare.SuccessData!).Source, Does.Contain("default!"));
        Assert.That(!withParameter.IsError, Is.True);
        Assert.That(((MethodSourceResult)withParameter.SuccessData!).Source, Does.Contain("default!"));
    }

    [Test]
    public async Task GetMethodSource_ContainerDeclaresNoSuchMethod_ListsTheTypesThatDoAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public void Run()\n    {\n    }\n}\n\npublic class B\n{\n    public void Other()\n    {\n    }\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "B");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("MethodNotFound"));
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("declares no method"));
        Assert.That(message, Does.Contain("'Run'"));
        Assert.That(message, Does.Contain("A."));
        Assert.That(message, Does.Contain("lines"));
        Assert.That(message, Does.Contain("containerName: 'A'"));
        var candidates = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].ContainingType, Is.EqualTo("A"));
    }

    [Test]
    public async Task GetMethodSource_ContainerNotInFile_ListsDeclaredTypesAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public void Run()\n    {\n    }\n}\n\npublic class B\n{\n    public void Other()\n    {\n    }\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "Nope");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("No type named 'Nope'"));
        Assert.That(message, Does.Contain("Types declared here"));
        Assert.That(message, Does.Contain("is declared in"));
    }

    [Test]
    public async Task GetMethodSource_ConstructorWithContainerName_ReturnsTheConstructorAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class A\n{\n    public A(int x)\n    {\n    }\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "A", containerName: "A");

        Assert.That(!result.IsError, Is.True);
        var data = (MethodSourceResult)result.SuccessData!;
        Assert.That(data.Source, Does.Contain("public A(int x)"));
    }

    [Test]
    public async Task GetMethodSource_DottedContainer_ResolvesByLastSegmentAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Outer\n{\n    public class Inner\n    {\n        public int Run() => 1;\n    }\n}\n\npublic class Other\n{\n    public int Run() => 2;\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var dotted = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "Outer.Inner");
        var bare = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "Inner");

        Assert.That(!dotted.IsError, Is.True);
        Assert.That(((MethodSourceResult)dotted.SuccessData!).Source, Does.Contain("=> 1"));
        Assert.That(((MethodSourceResult)dotted.SuccessData!).Source, Does.Not.Contain("=> 2"));
        Assert.That(!bare.IsError, Is.True);
        Assert.That(((MethodSourceResult)bare.SuccessData!).Source, Does.Contain("=> 1"));
    }

    [Test]
    public async Task GetMethodSource_WrongDottedContainer_RefusesAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Outer\n{\n    public class Inner\n    {\n        public int Run() => 1;\n    }\n}\n\npublic class Other\n{\n    public int Run() => 2;\n}\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSourceTool(reason: "test message", _documentPath, "Run", containerName: "Outer.Missing");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("MethodNotFound"));
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Types declared here"));
        Assert.That(message, Does.Contain("Inner"));
        var candidates = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(candidates, Has.Count.EqualTo(2));
        Assert.That(candidates.Select(c => c.ContainingType), Is.EquivalentTo(new[] { "Inner", "Other" }));
    }
}

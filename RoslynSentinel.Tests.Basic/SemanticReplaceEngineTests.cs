using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

public class SemanticReplaceEngineTests
{
    private SemanticReplaceEngine CreateEngine(Solution solution)
    {
        var fakeManager = new FakeWorkspaceManager();
        fakeManager.SetTestSolution(solution);
        return new SemanticReplaceEngine(fakeManager);
    }

    private Solution CreateTestSolution(string sourceCode)
    {
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var documentId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "Test.cs", sourceCode);
        return solution;
    }

    private async Task<string?> GetDocCommentId(Solution solution, string symbolName)
    {
        var project = solution.Projects.First();
        var compilation = await project.GetCompilationAsync();
        var symbol = compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == "C")
            .GetMembers(symbolName).First();
        return symbol.GetDocumentationCommentId();
    }

    [Test]
    public async Task ResolveBoolMemberAsync_WithAutoProperty_ReturnsSymbol()
    {
        var code = "namespace Test; public class C { public bool IsSuccess { get; set; } }";
        var solution = CreateTestSolution(code);
        var engine = CreateEngine(solution);
        var docCommentId = await GetDocCommentId(solution, "IsSuccess");

        var (resolvedSymbol, error) = await engine.ResolveBoolMemberAsync(docCommentId!);

        Assert.That(resolvedSymbol, Is.Not.Null);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ResolveBoolMemberAsync_WithField_ReturnsSymbol()
    {
        var code = "namespace Test; public class C { public bool isSuccess; }";
        var solution = CreateTestSolution(code);
        var engine = CreateEngine(solution);
        var docCommentId = await GetDocCommentId(solution, "isSuccess");

        var (resolvedSymbol, error) = await engine.ResolveBoolMemberAsync(docCommentId!);

        Assert.That(resolvedSymbol, Is.Not.Null);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ResolveBoolMemberAsync_WithUnknownId_ReturnsNotFound()
    {
        var code = "namespace Test; public class C { public bool IsSuccess { get; set; } }";
        var solution = CreateTestSolution(code);
        var engine = CreateEngine(solution);

        var (resolvedSymbol, error) = await engine.ResolveBoolMemberAsync("T:UnknownType");

        Assert.That(resolvedSymbol, Is.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ResolveBoolMemberAsync_WithNonBoolProperty_ReturnsInvalidArgument()
    {
        var code = "namespace Test; public class C { public int IsSuccess { get; set; } }";
        var solution = CreateTestSolution(code);
        var engine = CreateEngine(solution);
        var docCommentId = await GetDocCommentId(solution, "IsSuccess");

        var (resolvedSymbol, error) = await engine.ResolveBoolMemberAsync(docCommentId!);

        Assert.That(resolvedSymbol, Is.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ResolveBoolMemberAsync_WithCustomGetter_ReturnsTargetIneligible()
    {
        var code = "namespace Test; public class C { private bool _x; public bool IsSuccess { get => !_x; set => _x = !value; } }";
        var solution = CreateTestSolution(code);
        var engine = CreateEngine(solution);
        var docCommentId = await GetDocCommentId(solution, "IsSuccess");

        var (resolvedSymbol, error) = await engine.ResolveBoolMemberAsync(docCommentId!);

        Assert.That(resolvedSymbol, Is.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }

    [Test]
    public async Task ResolveBoolMemberAsync_WithVirtualProperty_ReturnsTargetIneligible()
    {
        var code = "namespace Test; public class C { public virtual bool IsSuccess { get; set; } }";
        var solution = CreateTestSolution(code);
        var engine = CreateEngine(solution);
        var docCommentId = await GetDocCommentId(solution, "IsSuccess");

        var (resolvedSymbol, error) = await engine.ResolveBoolMemberAsync(docCommentId!);

        Assert.That(resolvedSymbol, Is.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }

    [Test]
    public async Task ResolveBoolMemberAsync_WithExpressionBodiedProperty_ReturnsTargetIneligible()
    {
        var code = "namespace Test; public class C { private bool _y; public bool IsX => !_y; }";
        var solution = CreateTestSolution(code);
        var engine = CreateEngine(solution);
        var docCommentId = await GetDocCommentId(solution, "IsX");

        var (resolvedSymbol, error) = await engine.ResolveBoolMemberAsync(docCommentId!);

        Assert.That(resolvedSymbol, Is.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }

    [Test]
    public void DescribeDeclaration_WithAutoPropertyNoInitializer_ReturnsCorrectInfo()
    {
        var code = "namespace Test; public class C { public bool IsSuccess { get; set; } }";
        var solution = CreateTestSolution(code);
        var project = solution.Projects.First();
        var compilation = project.GetCompilationAsync().Result;
        var symbol = compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == "C")
            .GetMembers("IsSuccess").First();

        var info = SemanticReplaceEngine.DescribeDeclaration(symbol);

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.FilePath, Is.Not.Null);
        Assert.That(info.IdentifierSpan.Length, Is.GreaterThan(0));
        Assert.That(info.InitializerValueSpan, Is.Null);
        Assert.That(info.InitializerText, Is.Null);
    }

    [Test]
    public void DescribeDeclaration_WithAutoPropertyWithInitializer_ReturnsCorrectInfo()
    {
        var code = "namespace Test; public class C { public bool IsSuccess { get; set; } = true; }";
        var solution = CreateTestSolution(code);
        var project = solution.Projects.First();
        var compilation = project.GetCompilationAsync().Result;
        var symbol = compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == "C")
            .GetMembers("IsSuccess").First();

        var info = SemanticReplaceEngine.DescribeDeclaration(symbol);

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.FilePath, Is.Not.Null);
        Assert.That(info.IdentifierSpan.Length, Is.GreaterThan(0));
        Assert.That(info.InitializerValueSpan, Is.Not.Null);
        Assert.That(info.InitializerText, Is.EqualTo("true"));
    }

    [Test]
    public void DescribeDeclaration_WithFieldNoInitializer_ReturnsCorrectInfo()
    {
        var code = "namespace Test; public class C { public bool isSuccess; }";
        var solution = CreateTestSolution(code);
        var project = solution.Projects.First();
        var compilation = project.GetCompilationAsync().Result;
        var symbol = compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == "C")
            .GetMembers("isSuccess").First();

        var info = SemanticReplaceEngine.DescribeDeclaration(symbol);

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.FilePath, Is.Not.Null);
        Assert.That(info.IdentifierSpan.Length, Is.GreaterThan(0));
        Assert.That(info.InitializerValueSpan, Is.Null);
        Assert.That(info.InitializerText, Is.Null);
    }

    [TestCase("public bool IsSuccess { get; set; } = true;", "IsSuccess", "true")]
    [TestCase("public bool IsSuccess { get; set; }", "IsSuccess", null)]
    [TestCase("public bool isOk = false;", "isOk", "false")]
    [TestCase("public bool a = true, isOk = false;", "isOk", "false")]
    [TestCase("public bool isOk;", "isOk", null)]
    public void DescribeDeclaration_SpansPointAtIdentifierAndInitializerText(string member, string name, string? expectedInitializer)
    {
        var solution = CreateTestSolution($"namespace Test; public class C {{ {member} }}");
        var compilation = solution.Projects.First().GetCompilationAsync().Result;
        var symbol = compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == "C")
            .GetMembers(name).First();

        var info = SemanticReplaceEngine.DescribeDeclaration(symbol);

        Assert.That(info, Is.Not.Null);
        var text = symbol.DeclaringSyntaxReferences[0].SyntaxTree.GetText();
        Assert.That(text.ToString(info!.IdentifierSpan), Is.EqualTo(name));
        Assert.That(info.InitializerText, Is.EqualTo(expectedInitializer));
        if (expectedInitializer is null)
        {
            Assert.That(info.InitializerValueSpan, Is.Null);
        }
        else
        {
            Assert.That(text.ToString(info.InitializerValueSpan!.Value), Is.EqualTo(expectedInitializer));
        }
    }

    [Test]
    public void DescribeDeclaration_WithFieldWithInitializer_ReturnsCorrectInfo()
    {
        var code = "namespace Test; public class C { public bool isSuccess = true; }";
        var solution = CreateTestSolution(code);
        var project = solution.Projects.First();
        var compilation = project.GetCompilationAsync().Result;
        var symbol = compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == "C")
            .GetMembers("isSuccess").First();

        var info = SemanticReplaceEngine.DescribeDeclaration(symbol);

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.FilePath, Is.Not.Null);
        Assert.That(info.IdentifierSpan.Length, Is.GreaterThan(0));
        Assert.That(info.InitializerValueSpan, Is.Not.Null);
        Assert.That(info.InitializerText, Is.EqualTo("true"));
    }
}

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

    [TestCase("IsError", null)]
    [TestCase("", "must be a valid")]
    [TestCase("1abc", "must be a valid")]
    [TestCase("class", "must be a valid")]
    [TestCase("IsSuccess", "identical")]
    [TestCase("IsError2", "IsError2")]
    public void ValidateNewName_WithVariousInputs_ReturnsExpectedError(string newName, string? expectedErrorContent)
    {
        var code = "namespace Test; public class C { public bool IsSuccess { get; set; } public bool IsError2; }";
        var solution = CreateTestSolution(code);
        var project = solution.Projects.First();
        var compilation = project.GetCompilationAsync().Result;
        var symbol = compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == "C")
            .GetMembers("IsSuccess").First();

        var error = SemanticReplaceEngine.ValidateNewName(symbol, newName);

        if (expectedErrorContent == null)
        {
            Assert.That(error, Is.Null);
        }
        else
        {
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(error.Message, Does.Contain(expectedErrorContent));
        }
    }

    // ---- CollectSitesAsync -------------------------------------------------------------------------------------

    private static string Source(params string[] lines) => string.Join("\n", lines);

    private static Solution CreateMultiDocumentSolution(params string[] sources)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        for (var i = 0; i < sources.Length; i++)
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), $"Test{i}.cs", sources[i]);
        }

        return solution;
    }

    private static async Task<ISymbol> GetMemberSymbolAsync(Solution solution, string typeName, string memberName)
    {
        var compilation = await solution.Projects.First().GetCompilationAsync();
        return compilation!.GlobalNamespace
            .GetNamespaceMembers().First(n => n.Name == "Test")
            .GetTypeMembers().First(t => t.Name == typeName)
            .GetMembers(memberName).First();
    }

    /// <summary>Applies one file's edits to its original text, start descending then end descending (the documented application order).</summary>
    private static string ApplyEdits(string source, string filePath, IEnumerable<ReferenceEdit> edits)
    {
        var result = source;
        foreach (var edit in edits.Where(e => e.FilePath == filePath).OrderByDescending(e => e.Span.Start).ThenByDescending(e => e.Span.End))
        {
            result = result.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        }

        return result;
    }

    [Test]
    public async Task CollectSitesAsync_OnlyTargetedClassIsTouched_AcrossTwoDocuments()
    {
        var types = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } }",
            "public class B { public bool IsSuccess { get; set; } }");
        var usage = Source(
            "namespace Test;",
            "public class U",
            "{",
            "    public void M(A a, B b)",
            "    {",
            "        var x = a.IsSuccess;",
            "        var y = b.IsSuccess;",
            "        if (!b.IsSuccess) { }",
            "        b.IsSuccess = true;",
            "    }",
            "}");
        var solution = CreateMultiDocumentSolution(types, usage);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (sites, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(error, Is.Null);
        Assert.That(sites.Select(s => s.Role), Is.EqualTo(new[] { SemanticReplaceRole.DeclarationInitializer, SemanticReplaceRole.Read }));
        Assert.That(ApplyEdits(types, "Test0.cs", edits), Is.EqualTo(types.Replace("A { public bool IsSuccess", "A { public bool IsError")));
        Assert.That(ApplyEdits(usage, "Test1.cs", edits), Is.EqualTo(usage.Replace("var x = a.IsSuccess;", "var x = !a.IsError;")));
    }

    [Test]
    public async Task CollectSitesAsync_AllRoles_ProduceExactEditsAndSites()
    {
        var code = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } = true; }",
            "public class U",
            "{",
            "    public void M(A r, bool a, bool b)",
            "    {",
            "        if (r.IsSuccess) { }",
            "        if (!r.IsSuccess) { }",
            "        r.IsSuccess = a && b;",
            "        r.IsSuccess = true;",
            "        var n = nameof(A.IsSuccess);",
            "        var o = new A { IsSuccess = true };",
            "    }",
            "}");
        var solution = CreateMultiDocumentSolution(code);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (sites, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(error, Is.Null);
        var actual = edits.OrderBy(e => e.Span.Start).Select(e => (code.Substring(e.Span.Start, e.Span.Length), e.NewText)).ToArray();
        var expected = new[]
        {
            ("IsSuccess", "IsError"), ("true", "false"),
            ("r.IsSuccess", "!r.IsError"),
            ("!r.IsSuccess", "r.IsError"),
            ("IsSuccess", "IsError"), ("a && b", "!(a && b)"),
            ("IsSuccess", "IsError"), ("true", "false"),
            ("IsSuccess", "IsError"),
            ("IsSuccess", "IsError"), ("true", "false"),
        };
        Assert.That(actual, Is.EqualTo(expected));

        Assert.That(sites.Select(s => s.Role), Is.EqualTo(new[]
        {
            SemanticReplaceRole.DeclarationInitializer,
            SemanticReplaceRole.Read,
            SemanticReplaceRole.NegatedRead,
            SemanticReplaceRole.WriteExpression,
            SemanticReplaceRole.WriteLiteral,
            SemanticReplaceRole.NameOf,
            SemanticReplaceRole.WriteLiteral,
        }));
        Assert.That(sites[0].Before, Is.EqualTo("IsSuccess { get; set; } = true"));
        Assert.That(sites[0].After, Is.EqualTo("IsError { get; set; } = false"));
        Assert.That(sites[3].Before, Is.EqualTo("r.IsSuccess = a && b"));
        Assert.That(sites[3].After, Is.EqualTo("r.IsError = !(a && b)"));
        Assert.That(sites.Select(s => s.Line), Is.EqualTo(new[] { 2, 7, 8, 9, 10, 11, 12 }));

        var expectedCode = Source(
            "namespace Test;",
            "public class A { public bool IsError { get; set; } = false; }",
            "public class U",
            "{",
            "    public void M(A r, bool a, bool b)",
            "    {",
            "        if (!r.IsError) { }",
            "        if (r.IsError) { }",
            "        r.IsError = !(a && b);",
            "        r.IsError = false;",
            "        var n = nameof(A.IsError);",
            "        var o = new A { IsError = false };",
            "    }",
            "}");
        Assert.That(ApplyEdits(code, "Test0.cs", edits), Is.EqualTo(expectedCode));
    }

    [Test]
    public async Task CollectSitesAsync_NestedReferencesInsideWriteRhs_WrapInsteadOfReplace()
    {
        var code = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } }",
            "public class U",
            "{",
            "    public void M(A x, A y, bool c)",
            "    {",
            "        x.IsSuccess = !y.IsSuccess;",
            "        x.IsSuccess = y.IsSuccess && c;",
            "    }",
            "}");
        var solution = CreateMultiDocumentSolution(code);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (sites, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(error, Is.Null);
        var expectedCode = Source(
            "namespace Test;",
            "public class A { public bool IsError { get; set; } }",
            "public class U",
            "{",
            "    public void M(A x, A y, bool c)",
            "    {",
            "        x.IsError = !(y.IsError);",
            "        x.IsError = !(!y.IsError && c);",
            "    }",
            "}");
        Assert.That(ApplyEdits(code, "Test0.cs", edits), Is.EqualTo(expectedCode));
        Assert.That(edits.Any(e => e.Span.Length == 0 && e.NewText == "!("), Is.True, "outer flip must be an insertion, never a replacement of the original RHS");
        var outerWrite = sites.First(s => s.Role == SemanticReplaceRole.WriteExpression);
        Assert.That(outerWrite.Before, Is.EqualTo("x.IsSuccess = !y.IsSuccess"));
        Assert.That(outerWrite.After, Is.EqualTo("x.IsError = !(y.IsError)"));
    }

    [Test]
    public async Task CollectSitesAsync_UnsupportedSites_RefuseAtomicallyAndListEveryFileAndLine()
    {
        var code = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } }",
            "public class U",
            "{",
            "    public bool M(A x, bool y)",
            "    {",
            "        x.IsSuccess |= y;",
            "        return x.IsSuccess = true;",
            "    }",
            "}");
        var solution = CreateMultiDocumentSolution(code);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (sites, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(edits, Is.Empty);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(error.Message, Does.Contain("Test0.cs:7 - "));
        Assert.That(error.Message, Does.Contain("Test0.cs:8 - assignment used as a value is not supported"));
        Assert.That(sites.Count(s => s.UnsupportedReason is not null), Is.EqualTo(2));
    }

    [Test]
    public async Task CollectSitesAsync_NullConditionalAccess_IsUnsupported()
    {
        var code = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } }",
            "public class U { public bool? M(A? x) => x?.IsSuccess; }");
        var solution = CreateMultiDocumentSolution(code);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (_, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(edits, Is.Empty);
        Assert.That(error!.Message, Does.Contain("Test0.cs:3 - null-conditional access"));
    }

    [Test]
    public async Task CollectSitesAsync_ReceiverOfMemberAccess_IsParenthesized()
    {
        var code = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } }",
            "public class U { public bool M(A x, bool y) { return x.IsSuccess.Equals(y); } }");
        var solution = CreateMultiDocumentSolution(code);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (_, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(error, Is.Null);
        Assert.That(ApplyEdits(code, "Test0.cs", edits), Does.Contain("return (!x.IsError).Equals(y);"));
    }

    [Test]
    public async Task CollectSitesAsync_NonLiteralInitializer_IsFlippedAsExpression()
    {
        var code = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } = Compute(); static bool Compute() => true; }");
        var solution = CreateMultiDocumentSolution(code);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (sites, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(error, Is.Null);
        Assert.That(ApplyEdits(code, "Test0.cs", edits), Does.Contain("IsError { get; set; } = !Compute();"));
        Assert.That(sites.Single().Role, Is.EqualTo(SemanticReplaceRole.DeclarationInitializer));
    }

    [Test]
    public async Task CollectSitesAsync_CollidingNewName_ReturnsValidateNewNameError()
    {
        var code = Source(
            "namespace Test;",
            "public class A { public bool IsSuccess { get; set; } public bool IsError { get; set; } }",
            "public class U { public bool M(A x) { return x.IsSuccess; } }");
        var solution = CreateMultiDocumentSolution(code);
        var engine = CreateEngine(solution);
        var symbol = await GetMemberSymbolAsync(solution, "A", "IsSuccess");

        var (sites, edits, error) = await engine.CollectSitesAsync(symbol, "IsError");

        Assert.That(sites, Is.Empty);
        Assert.That(edits, Is.Empty);
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(error.Message, Does.Contain("collides"));
    }
}

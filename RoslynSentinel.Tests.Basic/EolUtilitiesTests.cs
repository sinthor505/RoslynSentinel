using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class EolUtilitiesTests
{
    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public void DetectDominantEol_ReturnsLf_ForLfDominantText()
    {
        var content = "namespace Scratch;\n\npublic class ScratchCrlf\n{\n    public void DoWork()\n    {\n    }\n}\n";
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(content);
        var dominant = EolUtilities.DetectDominantEol(sourceText);
        Assert.That(dominant, Is.EqualTo("\n"));
    }
    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task ReplaceNodeFormattedAsync_PreservesLfDominant_ForLfFixture()
    {
        var lfContent = "namespace Scratch;\n\npublic class ScratchCrlf2\n{\n    public void DoWork()\n    {\n    }\n}\n";
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .AddMetadataReference(projectId, Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var documentId = Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "ScratchCrlf2.cs", lfContent);
        var document = solution.GetDocument(documentId)!;
        var root = await document.GetSyntaxRootAsync();
        var classDecl = root!.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First();
        var method = classDecl.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First();
        var newMethod = method.WithModifiers(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TokenList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword).WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
        var result = await FormattingHelper.ReplaceNodeFormattedAsync(document, root, method, newMethod);
        var crlfCount = System.Text.RegularExpressions.Regex.Matches(result, "\r\n").Count;
        var bareLfCount = System.Text.RegularExpressions.Regex.Matches(result, "(?<!\r)\n").Count;
        Assert.That(crlfCount, Is.EqualTo(0), $"Expected 0 CRLF but found {crlfCount}. Bare LF count: {bareLfCount}. Result: {result}");
    }
    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task ReplaceNodeFormattedAsync_WithEditorConfigCrlf_StillPreservesLfDominant()
    {
        var lfContent = "namespace Scratch;\n\npublic class ScratchCrlf3\n{\n    public void DoWork()\n    {\n    }\n}\n";
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .AddMetadataReference(projectId, Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddAnalyzerConfigDocument(Microsoft.CodeAnalysis.DocumentId.CreateNewId(Microsoft.CodeAnalysis.ProjectId.CreateNewId()), ".editorconfig", Microsoft.CodeAnalysis.Text.SourceText.From("root = true\n[*.cs]\nend_of_line = crlf\n"), filePath: "C:/fake/.editorconfig");
        var documentId = Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "ScratchCrlf3.cs", lfContent, filePath: "C:/fake/ScratchCrlf3.cs");
        var document = solution.GetDocument(documentId)!;
        var root = await document.GetSyntaxRootAsync();
        var classDecl = root!.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First();
        var method = classDecl.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First();
        var newMethod = method.WithModifiers(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TokenList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword).WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
        var result = await FormattingHelper.ReplaceNodeFormattedAsync(document, root, method, newMethod);
        var crlfCount = System.Text.RegularExpressions.Regex.Matches(result, "\r\n").Count;
        var bareLfCount = System.Text.RegularExpressions.Regex.Matches(result, "(?<!\r)\n").Count;
        Assert.That(crlfCount, Is.EqualTo(0), $"Expected 0 CRLF but found {crlfCount}. Bare LF count: {bareLfCount}. Result: {result}");
    }
    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task ReplaceNodeFormattedAsync_PreservesCrlfDominant_ForCrlfFixture()
    {
        var crlfContent = "namespace Scratch;\r\n\r\npublic class ScratchCrlf4\r\n{\r\n    public void DoWork()\r\n    {\r\n    }\r\n}\r\n";
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .AddMetadataReference(projectId, Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var documentId = Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "ScratchCrlf4.cs", crlfContent);
        var document = solution.GetDocument(documentId)!;
        var root = await document.GetSyntaxRootAsync();
        var classDecl = root!.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First();
        var method = classDecl.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First();
        var newMethod = method.WithModifiers(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TokenList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword).WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
        var result = await FormattingHelper.ReplaceNodeFormattedAsync(document, root, method, newMethod);
        var crlfCount = System.Text.RegularExpressions.Regex.Matches(result, "\r\n").Count;
        var bareLfCount = System.Text.RegularExpressions.Regex.Matches(result, "(?<!\r)\n").Count;
        Assert.That(bareLfCount, Is.EqualTo(0), $"Expected 0 bare LF but found {bareLfCount}. CRLF count: {crlfCount}. Result: {result}");
    }
}

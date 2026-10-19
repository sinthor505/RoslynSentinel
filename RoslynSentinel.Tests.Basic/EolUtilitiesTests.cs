using Microsoft.CodeAnalysis.CSharp;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class EolUtilitiesTests
{
       [Test]
    public void DetectDominantEol_ReturnsLf_ForLfDominantText()
    {
        var content = "namespace Scratch;\n\npublic class ScratchCrlf\n{\n    public void DoWork()\n    {\n    }\n}\n";
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(content);
        var dominant = EolUtilities.DetectDominantEol(sourceText);
        Assert.That(dominant, Is.EqualTo("\n"));
    }

    [TestCase("a\rb\rc\r", "\n", TestName = "DetectNormalizableEol_CrOnly_ReturnsLf")]
    [TestCase("a\nb\nc\n", "\n", TestName = "DetectNormalizableEol_Lf_ReturnsLf")]
    [TestCase("a\r\nb\r\nc\r\n", "\r\n", TestName = "DetectNormalizableEol_Crlf_ReturnsCrlf")]
    [TestCase("a\r\nb\r\nc\n", "\r\n", TestName = "DetectNormalizableEol_MixedCrlfDominant_ReturnsCrlf")]
    [TestCase("a\nb\nc\r\n", "\n", TestName = "DetectNormalizableEol_MixedLfDominant_ReturnsLf")]
    public void DetectNormalizableEol_ReturnsOnlyLfOrCrlf(string content, string expected)
    {
        var result = EolUtilities.DetectNormalizableEol(Microsoft.CodeAnalysis.Text.SourceText.From(content));

        Assert.That(result, Is.EqualTo(expected));
        Assert.That(result, Is.AnyOf("\n", "\r\n"));
    }

    [TestCase(0x2028, TestName = "DetectNormalizableEol_LineSeparatorU2028_ReturnsLf")]
    [TestCase(0x0085, TestName = "DetectNormalizableEol_NextLineU0085_ReturnsLf")]
    public void DetectNormalizableEol_UnicodeLineTerminator_ReturnsLf(int terminatorCodePoint)
    {
        // Built from a code point so the terminator cannot be altered by an encoding round trip of this file.
        var terminator = ((char)terminatorCodePoint).ToString();
        var text = Microsoft.CodeAnalysis.Text.SourceText.From("a" + terminator + "b" + terminator + "c" + terminator);

        Assert.That(EolUtilities.DetectDominantEol(text), Is.EqualTo(terminator), "Precondition: the dominant EOL is the unusual terminator.");
        Assert.That(EolUtilities.DetectNormalizableEol(text), Is.EqualTo("\n"));
    }

    [Test]
    public void DetectNormalizableEol_EmptyText_ReturnsAnAcceptedStyle()
    {
        var result = EolUtilities.DetectNormalizableEol(Microsoft.CodeAnalysis.Text.SourceText.From(string.Empty));

        Assert.That(result, Is.EqualTo(Environment.NewLine == "\r\n" ? "\r\n" : "\n"));
    }

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
        var result = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root, method, newMethod);
        var crlfCount = System.Text.RegularExpressions.Regex.Matches(result, "\r\n").Count;
        var bareLfCount = System.Text.RegularExpressions.Regex.Matches(result, "(?<!\r)\n").Count;
        Assert.That(crlfCount, Is.Zero, $"Expected 0 CRLF but found {crlfCount}. Bare LF count: {bareLfCount}. Result: {result}");
    }
       [Test]
    public async Task ReplaceNodeFormattedAsync_WithEditorConfigCrlf_StillPreservesLfDominant()
    {
        var lfContent = "namespace Scratch;\n\npublic class ScratchCrlf3\n{\n    public void DoWork()\n    {\n    }\n}\n";
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .AddMetadataReference(projectId, Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddAnalyzerConfigDocument(Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId), ".editorconfig", Microsoft.CodeAnalysis.Text.SourceText.From("root = true\n[*.cs]\nend_of_line = crlf\n"), filePath: "C:/fake/.editorconfig");
        var documentId = Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "ScratchCrlf3.cs", lfContent, filePath: "C:/fake/ScratchCrlf3.cs");
        var document = solution.GetDocument(documentId)!;
        var root = await document.GetSyntaxRootAsync();
        var classDecl = root!.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First();
        var method = classDecl.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First();
        var newMethod = method.WithModifiers(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TokenList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword).WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
        var result = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root, method, newMethod);
        var crlfCount = System.Text.RegularExpressions.Regex.Matches(result, "\r\n").Count;
        var bareLfCount = System.Text.RegularExpressions.Regex.Matches(result, "(?<!\r)\n").Count;
        Assert.That(crlfCount, Is.Zero, $"Expected 0 CRLF but found {crlfCount}. Bare LF count: {bareLfCount}. Result: {result}");
    }
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
        var result = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root, method, newMethod);
        var crlfCount = System.Text.RegularExpressions.Regex.Matches(result, "\r\n").Count;
        var bareLfCount = System.Text.RegularExpressions.Regex.Matches(result, "(?<!\r)\n").Count;
        Assert.That(bareLfCount, Is.Zero, $"Expected 0 bare LF but found {bareLfCount}. CRLF count: {crlfCount}. Result: {result}");
    }
}

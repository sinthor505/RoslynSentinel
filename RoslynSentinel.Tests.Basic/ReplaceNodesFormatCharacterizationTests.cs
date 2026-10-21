using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Support.Sources;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// C5c-6 characterization tests for <see cref="RoslynFormattingHelper.ReplaceNodesAndFormatReplacementsAsync"/>.
/// Byte-exact pins for the batch replace-and-format helper, called directly on an in-memory document.
/// The helper is migrated from a whole-text format plus whole-text NormalizeEol onto annotated-span formatting;
/// the pins that change with that migration are marked where they are defined.
/// </summary>
[TestFixture]
public class ReplaceNodesFormatCharacterizationTests
{
    private const string LfText = "namespace Scratch;\n\npublic class C\n{\n    public void A()\n    {\n    }\n\n    public void B()\n    {\n    }\n}\n";

    private static string PrivateA(string lfText) => lfText.Replace("    public void A()", "    private void A()");

    private static string PrivateB(string lfText) => lfText.Replace("    public void B()", "    private void B()");

    private static async Task<(Document Document, SyntaxNode Root)> BuildAsync(string text)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var documentId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "Scratch.cs", text);
        var document = solution.GetDocument(documentId)!;
        return (document, (await document.GetSyntaxRootAsync())!);
    }

    private static MethodDeclarationSyntax Method(SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);

    private static MethodDeclarationSyntax Privatized(MethodDeclarationSyntax method) =>
        method.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space)));

    [Test]
    public async Task SingleReplacement_ByteExact([Values(false, true)] bool crlf)
    {
        var (document, root) = await BuildAsync(crlf ? Crlf(LfText) : LfText);
        var methodA = Method(root, "A");
        var replacements = new Dictionary<SyntaxNode, SyntaxNode> { [methodA] = Privatized(methodA) };

        var result = await RoslynFormattingHelper.ReplaceNodesAndFormatReplacementsAsync(document, root, replacements);

        var expected = PrivateA(LfText);
        Assert.That(result.Text, Is.EqualTo(crlf ? Crlf(expected) : expected));
        Assert.That(result.UnlocatedNodes, Is.Empty);
    }

    [Test]
    public async Task MultipleIndependentReplacements_ByteExact([Values(false, true)] bool crlf)
    {
        var (document, root) = await BuildAsync(crlf ? Crlf(LfText) : LfText);
        var methodA = Method(root, "A");
        var methodB = Method(root, "B");
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>
        {
            [methodA] = Privatized(methodA),
            [methodB] = Privatized(methodB),
        };

        var result = await RoslynFormattingHelper.ReplaceNodesAndFormatReplacementsAsync(document, root, replacements);

        var expected = PrivateB(PrivateA(LfText));
        Assert.That(result.Text, Is.EqualTo(crlf ? Crlf(expected) : expected));
        Assert.That(result.UnlocatedNodes, Is.Empty);
    }

    // The class replacement takes the pending method replacement with it: the method is reported, not dropped.
    [Test]
    public async Task UnlocatedDescendant_IsReportedAndTextStillFormatted()
    {
        var (document, root) = await BuildAsync(LfText);
        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var methodA = Method(root, "A");
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>
        {
            [classDecl] = SyntaxFactory.ClassDeclaration("Other"),
            [methodA] = Privatized(methodA),
        };

        var result = await RoslynFormattingHelper.ReplaceNodesAndFormatReplacementsAsync(document, root, replacements);

        Assert.That(result.UnlocatedNodes, Has.Count.EqualTo(1));
        Assert.That(result.UnlocatedNodes[0], Is.SameAs(methodA));
        Assert.That(result.Text, Is.EqualTo("namespace Scratch;\n\nclass Other\n{\n}\n"));
    }

    // Stray CRLF on an untouched line keeps its own terminator (migrated behaviour; the pre-migration whole-text
    // NormalizeEol flattened it); the replaced method's lines take the dominant LF.
    [Test]
    public async Task UntouchedStrayCrlf_IsKept()
    {
        var mixed = LfText.Replace("public class C\n", "public class C\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        var (document, root) = await BuildAsync(mixed);
        var methodA = Method(root, "A");
        var replacements = new Dictionary<SyntaxNode, SyntaxNode> { [methodA] = Privatized(methodA) };

        var result = await RoslynFormattingHelper.ReplaceNodesAndFormatReplacementsAsync(document, root, replacements);

        Assert.That(result.Text, Is.EqualTo(PrivateA(mixed)));
        Assert.That(result.UnlocatedNodes, Is.Empty);
    }
}

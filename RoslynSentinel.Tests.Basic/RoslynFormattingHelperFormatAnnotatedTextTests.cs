using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Whitespace slice D2: the shared text-returning wrappers of <see cref="RoslynFormattingHelper.FormatAnnotatedSpansAsync"/>
/// that replaced the engines' private copies (FormatAnnotatedTextAsync for a Document or for plain text, and
/// FormatNewSnippetAsync). Each case runs for an LF, a CRLF and a mixed document (LF-dominant, CRLF on the lines marked
/// with a trailing "~"): an untouched line keeps its own terminator and a line the formatter changed takes the dominant
/// ending. The Document fixture pins the formatter's own NewLine option to the opposite ending so a leak shows.
/// </summary>
public class RoslynFormattingHelperFormatAnnotatedTextTests
{
    // The annotated declaration (line 5) is badly spaced, so the formatter changes it; line 6 is not annotated.
    private static readonly string[] SourceLines =
    {
        "class C",
        "{",
        "    void M()",
        "    {",
        "        int  x  =  1;~",
        "        int y = 2;~",
        "    }",
        "}",
    };

    // Line 5 changed, so it takes the dominant ending even where it had the odd one; line 6 keeps its own.
    private static readonly string[] ExpectedLines =
    {
        "class C",
        "{",
        "    void M()",
        "    {",
        "        int x = 1;",
        "        int y = 2;~",
        "    }",
        "}",
    };

    private AdhocWorkspace? _workspace;

    [TearDown]
    public void TearDown() => _workspace?.Dispose();

    private static IEnumerable<TestCaseData> EolCases()
    {
        yield return new TestCaseData("\n", null).SetName("{m}(LF)");
        yield return new TestCaseData("\r\n", null).SetName("{m}(CRLF)");
        yield return new TestCaseData("\n", "\r\n").SetName("{m}(Mixed)");
    }

    // eol: the dominant ending. oddEol: when set, every line marked with a trailing "~" ends with it instead.
    private static string Text(string eol, string? oddEol, IEnumerable<string> lines)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (line.EndsWith('~'))
            {
                builder.Append(line, 0, line.Length - 1).Append(oddEol ?? eol);
            }
            else
            {
                builder.Append(line).Append(eol);
            }
        }

        return builder.ToString();
    }

    // The .globalconfig sets the formatter's NewLine to the OPPOSITE of the dominant ending, so a leak shows.
    private Document MakeDocument(string content, string eol)
    {
        _workspace?.Dispose();
        _workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = _workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddDocument(documentId, "Test.cs", content, filePath: "/Test.cs")
            .AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(projectId),
                ".globalconfig",
                SourceText.From($"is_global = true\nend_of_line = {(eol == "\r\n" ? "lf" : "crlf")}\n"),
                filePath: "/.globalconfig");
        return solution.GetDocument(documentId)!;
    }

    private static SyntaxNode AnnotateFirstDeclaration(SyntaxNode root)
    {
        var declaration = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().First();
        return root.ReplaceNode(declaration, declaration.WithAdditionalAnnotations(Formatter.Annotation));
    }

    [TestCaseSource(nameof(EolCases))]
    public async Task FormatAnnotatedTextAsync_Document_FormatsOnlyAnnotatedLines_ChangedLinesTakeDominantEol(string eol, string? oddEol)
    {
        var document = MakeDocument(Text(eol, oddEol, SourceLines), eol);
        var root = AnnotateFirstDeclaration((await document.GetSyntaxRootAsync())!);

        var result = await RoslynFormattingHelper.FormatAnnotatedTextAsync(document, root, EolUtilities.DetectDominantEol(await document.GetTextAsync()));

        Assert.That(result, Is.EqualTo(Text(eol, oddEol, ExpectedLines)));
    }

    [TestCaseSource(nameof(EolCases))]
    public async Task FormatAnnotatedTextAsync_SourceText_FormatsOnlyAnnotatedLines_ChangedLinesTakeDominantEol(string eol, string? oddEol)
    {
        var originalText = SourceText.From(Text(eol, oddEol, SourceLines));
        var root = AnnotateFirstDeclaration(CSharpSyntaxTree.ParseText(originalText).GetRoot());

        var result = await RoslynFormattingHelper.FormatAnnotatedTextAsync(originalText, root, EolUtilities.DetectDominantEol(originalText));

        Assert.That(result, Is.EqualTo(Text(eol, oddEol, ExpectedLines)));
    }

    [TestCaseSource(nameof(EolCases))]
    public async Task FormatNewSnippetAsync_EveryLineTakesDominantEol_AndResultIsTrimmed(string eol, string? oddEol)
    {
        var document = MakeDocument(Text(eol, oddEol, SourceLines), eol);
        var member = SyntaxFactory.ParseMemberDeclaration("void B()\n{\nint  z=3;\n}")!;

        var result = await RoslynFormattingHelper.FormatNewSnippetAsync(document, member, EolUtilities.DetectDominantEol(await document.GetTextAsync()));

        Assert.That(result, Is.EqualTo(string.Join(eol, "void B()", "{", "    int z = 3;", "}")));
    }
}

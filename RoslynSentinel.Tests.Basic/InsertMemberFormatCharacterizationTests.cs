using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// C5c-7 characterization tests for <see cref="RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync"/>.
/// Byte-exact pins for the single-member insert-and-format helper, called directly on an in-memory document.
/// The helper is migrated from a whole-text format plus whole-text NormalizeEol onto annotated-span formatting;
/// the pins that change with that migration are marked where they are defined.
/// </summary>
[TestFixture]
public class InsertMemberFormatCharacterizationTests
{
    private const string Head = "namespace Scratch;\n\npublic class C\n{\n";
    private const string MethodA = "    public void A()\n    {\n    }\n";
    private const string MethodB = "    public void B()\n    {\n    }\n";
    private const string MethodZ = "    public void Z()\n    {\n    }\n";
    private const string Tail = "}\n";

    private const string LfText = Head + MethodA + "\n" + MethodB + Tail;

    private static string ToCrlf(string lfText) => lfText.Replace("\n", "\r\n");

    private static string Doc(bool crlf, string lfText) => crlf ? ToCrlf(lfText) : lfText;

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

    private static ClassDeclarationSyntax ClassC(SyntaxNode root) =>
        root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

    // The new member as a freshly parsed declaration: no leading trivia, the caller's own line breaks.
    private static MemberDeclarationSyntax NewMember(bool crlf) =>
        SyntaxFactory.ParseMemberDeclaration(Doc(crlf, "public void Z()\n    {\n    }"))!;

    private static string Expected(int insertIndex) => insertIndex switch
    {
        0 => Head + MethodZ + "\n" + MethodA + "\n" + MethodB + Tail,
        1 => Head + MethodA + "\n" + MethodZ + "\n" + MethodB + Tail,
        2 => Head + MethodA + "\n" + MethodB + "\n" + MethodZ + Tail,
        _ => throw new System.ArgumentOutOfRangeException(nameof(insertIndex)),
    };

    [Test]
    public async Task InsertAtPosition_ByteExact([Values(0, 1, 2)] int insertIndex, [Values(false, true)] bool crlf)
    {
        var (document, root) = await BuildAsync(Doc(crlf, LfText));
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync(
            document, root, container, insertIndex, NewMember(crlf));

        Assert.That(result, Is.EqualTo(Doc(crlf, Expected(insertIndex))));
    }

    // The untouched stray CRLF on "public class C" keeps its own terminator; the inserted lines take the dominant LF.
    [Test]
    public async Task LfDocument_UntouchedStrayCrlf_InsertInMiddle()
    {
        var mixed = LfText.Replace("public class C\n", "public class C\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        var (document, root) = await BuildAsync(mixed);
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync(
            document, root, container, 1, NewMember(crlf: false));

        Assert.That(result, Is.EqualTo(Expected(1).Replace("public class C\n", "public class C\r\n")));
    }

    // The untouched stray LF on "public class C" keeps its own terminator; the inserted lines take the dominant CRLF.
    [Test]
    public async Task CrlfDocument_UntouchedStrayLf_InsertInMiddle()
    {
        var mixed = ToCrlf(LfText).Replace("public class C\r\n", "public class C\n");
        Assert.That(mixed.Count(c => c == '\n') - mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray LF.");
        var (document, root) = await BuildAsync(mixed);
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync(
            document, root, container, 1, NewMember(crlf: true));

        Assert.That(result, Is.EqualTo(ToCrlf(Expected(1)).Replace("public class C\r\n", "public class C\n")));
    }
}

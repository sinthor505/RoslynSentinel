using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Support.Sources;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Byte-exact characterization tests for <see cref="RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync"/>,
/// the N-member insert-and-format helper, called directly on an in-memory document. Covers append, insert at the
/// front, insert in the middle, empty container, explicit eol on mixed-ending sources, and the one-element
/// equivalence with the single-member helper.
/// </summary>
[TestFixture]
public class InsertMembersFormatCharacterizationTests
{
    private const string Head = "namespace Scratch;\n\npublic class C\n{\n";
    private const string MethodA = "    public void A()\n    {\n    }\n";
    private const string MethodB = "    public void B()\n    {\n    }\n";
    private const string MethodX = "    public void X()\n    {\n    }\n";
    private const string MethodY = "    public void Y()\n    {\n    }\n";
    private const string MethodZ = "    public void Z()\n    {\n    }\n";
    private const string Tail = "}\n";
    private const string EmptyText = "namespace Scratch;\n\npublic class C\n{\n}\n";

    private const string LfText = Head + MethodA + "\n" + MethodB + Tail;

    private static string Doc(bool crlf, string lfText) => crlf ? Crlf(lfText) : lfText;

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

    // A freshly parsed declaration: no leading trivia, the caller's own line breaks.
    private static MemberDeclarationSyntax NewMember(string name, bool crlf) =>
        SyntaxFactory.ParseMemberDeclaration(Doc(crlf, "public void " + name + "()\n    {\n    }"))!;

    // Head + A + blank + B + blank + Y + blank + Z: the two new members appended after the siblings.
    private static string AppendTwoExpected() => Head + MethodA + "\n" + MethodB + "\n" + MethodY + "\n" + MethodZ + Tail;

    [Test]
    public async Task AppendTwoAtEnd_ByteExact()
    {
        var (document, root) = await BuildAsync(LfText);
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(
            document, root, container, container.Members.Count,
            new MemberDeclarationSyntax[] { NewMember("Y", crlf: false), NewMember("Z", crlf: false) }, "\n");

        Assert.That(result, Is.EqualTo(AppendTwoExpected()));
    }

    // The existing first sibling A has no blank line before it; inserting at index 0 must give it one.
    [Test]
    public async Task InsertThreeAtFront_FollowingSiblingGainsBlankLine()
    {
        var (document, root) = await BuildAsync(LfText);
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(
            document, root, container, 0,
            new MemberDeclarationSyntax[] { NewMember("X", crlf: false), NewMember("Y", crlf: false), NewMember("Z", crlf: false) }, "\n");

        Assert.That(result, Is.EqualTo(Head + MethodX + "\n" + MethodY + "\n" + MethodZ + "\n" + MethodA + "\n" + MethodB + Tail));
    }

    // Sibling A keeps its interior spacing; B keeps its single blank line before it.
    [Test]
    public async Task InsertTwoInMiddle_ContiguousAndSiblingsUntouched()
    {
        var (document, root) = await BuildAsync(LfText);
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(
            document, root, container, 1,
            new MemberDeclarationSyntax[] { NewMember("Y", crlf: false), NewMember("Z", crlf: false) }, "\n");

        Assert.That(result, Is.EqualTo(Head + MethodA + "\n" + MethodY + "\n" + MethodZ + "\n" + MethodB + Tail));
    }

    // No siblings: no blank line after the opening brace, one blank line between the two new members.
    [Test]
    public async Task InsertTwoIntoEmptyContainer_OneBlankLineBetween()
    {
        var (document, root) = await BuildAsync(EmptyText);
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(
            document, root, container, 0,
            new MemberDeclarationSyntax[] { NewMember("X", crlf: false), NewMember("Y", crlf: false) }, "\n");

        Assert.That(result, Is.EqualTo(Head + MethodX + "\n" + MethodY + Tail));
    }

    // LF-written source members into a CRLF document: the eol argument decides every line of the inserted text.
    [Test]
    public async Task CrlfDocument_LfSource_EolArgumentUsedForAllLines()
    {
        var (document, root) = await BuildAsync(Crlf(LfText));
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(
            document, root, container, container.Members.Count,
            new MemberDeclarationSyntax[] { NewMember("Y", crlf: false), NewMember("Z", crlf: false) }, "\r\n");

        Assert.That(result, Is.EqualTo(Crlf(AppendTwoExpected())));
    }

    // CRLF-written source members into an LF document: the eol argument decides every line of the inserted text.
    [Test]
    public async Task LfDocument_CrlfSource_EolArgumentUsedForAllLines()
    {
        var (document, root) = await BuildAsync(LfText);
        var container = ClassC(root);

        var result = await RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(
            document, root, container, container.Members.Count,
            new MemberDeclarationSyntax[] { NewMember("Y", crlf: true), NewMember("Z", crlf: true) }, "\n");

        Assert.That(result, Is.EqualTo(AppendTwoExpected()));
    }

    // A one-element list must produce the same text as the single-member helper on the same inputs.
    [Test]
    public async Task OneElementList_ByteIdenticalToSingleMemberHelper([Values(0, 1, 2)] int insertIndex, [Values(false, true)] bool crlf)
    {
        var (document, root) = await BuildAsync(Doc(crlf, LfText));
        var container = ClassC(root);
        var eol = EolUtilities.DetectDominantEol(await document.GetTextAsync());

        var single = await RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync(
            document, root, container, insertIndex, NewMember("Y", crlf), eol);
        var list = await RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(
            document, root, container, insertIndex, new MemberDeclarationSyntax[] { NewMember("Y", crlf) }, eol);

        Assert.That(list, Is.EqualTo(single));
    }
}

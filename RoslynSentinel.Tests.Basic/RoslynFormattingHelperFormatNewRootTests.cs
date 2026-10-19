using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice C6 of docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md:
/// <see cref="RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync"/>, the comment-keeping way to format a newly built
/// file root that embeds existing code. The three fixtures mirror how AdvancedStructuralEngine builds its roots
/// (ExtractSuperclass, ExtractMembersToPartial, ConvertTupleToClass), which use this helper since slices C7a/C7b
/// (engine pins: AdvancedStructuralNormalizeCharacterizationTests). Every test compares the whole result byte for byte
/// for LF, CRLF and mixed documents, and pins the formatter's own NewLine option to the OPPOSITE ending so a leak shows.
/// A fixture line with a trailing "~" takes the odd ending of a mixed document.
/// </summary>
public class RoslynFormattingHelperFormatNewRootTests
{
    private AdhocWorkspace _workspace = null!;

    [TearDown]
    public void TearDown() => _workspace?.Dispose();

    // eol: the dominant ending. oddEol: when set, every line marked with a trailing "~" ends with it instead.
    private static string Text(string eol, string? oddEol, params string[] lines)
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

    private Document MakeDocument(string content, string eol)
    {
        _workspace?.Dispose();
        _workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = _workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(documentId, "Test.cs", content, filePath: "/Test.cs")
            .AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(projectId),
                ".globalconfig",
                SourceText.From($"is_global = true\nend_of_line = {(eol == "\r\n" ? "lf" : "crlf")}\n"),
                filePath: "/.globalconfig");
        return solution.GetDocument(documentId)!;
    }

    private static async Task<CompilationUnitSyntax> RootOf(Document document)
        => (CompilationUnitSyntax)(await document.GetSyntaxRootAsync())!;

    private static BaseNamespaceDeclarationSyntax NewNamespaceLike(BaseNamespaceDeclarationSyntax ns, MemberDeclarationSyntax member)
        => ns is FileScopedNamespaceDeclarationSyntax
            ? FileScopedNamespaceDeclaration(ns.Name).AddMembers(member)
            : NamespaceDeclaration(ns.Name).AddMembers(member);

    // ExtractSuperclass: the public properties of the class are embedded in a new abstract class in a new file.
    private static async Task<string> ExtractSuperclassAsync(Document document)
    {
        var root = await RootOf(document);
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))).ToArray();
        var baseClassNode = ClassDeclaration("PersonBase").AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AbstractKeyword)).AddMembers(properties);
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().First();
        var baseUnit = CompilationUnit().WithUsings(root.Usings).AddMembers(NewNamespaceLike(ns, baseClassNode));

        return await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, baseUnit, n => n is not (UsingDirectiveSyntax or PropertyDeclarationSyntax));
    }

    // ExtractMembersToPartial: the named members are embedded in a new partial class (block namespace) in a new file.
    private static async Task<string> ExtractMembersToPartialAsync(Document document)
    {
        var root = await RootOf(document);
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var membersToMove = classNode.Members.Where(m => (m is MethodDeclarationSyntax meth && meth.Identifier.Text == "Total") || (m is PropertyDeclarationSyntax prop && prop.Identifier.Text == "Name")).ToList();
        var newClassNode = ClassDeclaration("Customer").WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.PartialKeyword))).WithMembers(List(membersToMove));
        var namespaceDecl = NamespaceDeclaration(ParseName("Demo")).WithMembers(SingletonList<MemberDeclarationSyntax>(newClassNode));
        var newCompilationUnit = CompilationUnit().WithUsings(root.Usings).WithMembers(SingletonList<MemberDeclarationSyntax>(namespaceDecl));

        return await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, newCompilationUnit, n => n is not (UsingDirectiveSyntax or MemberDeclarationSyntax { Parent: ClassDeclarationSyntax }));
    }

    // ConvertTupleToClass: the tuple elements' types and the file's usings are embedded in a new class in a new file.
    private static async Task<string> ConvertTupleToClassAsync(Document document)
    {
        var root = await RootOf(document);
        var tupleType = root.DescendantNodes().OfType<TupleTypeSyntax>().First();
        var properties = new List<MemberDeclarationSyntax>();
        foreach (var element in tupleType.Elements)
        {
            var name = element.Identifier.Text;
            properties.Add(PropertyDeclaration(element.Type, char.ToUpper(name[0]) + name.Substring(1))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddAccessorListAccessors(
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken))));
        }

        var newClass = ClassDeclaration("DataResult").AddModifiers(Token(SyntaxKind.PublicKeyword)).AddMembers(properties.ToArray());
        var ns = tupleType.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().First();
        var classRoot = CompilationUnit().WithUsings(root.Usings).AddMembers(NewNamespaceLike(ns, newClass));

        return await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, classRoot, n => n is not (UsingDirectiveSyntax or TypeSyntax { Parent: PropertyDeclarationSyntax }));
    }

    // The four endings under test: (dominant, odd). The odd ending applies to each "~"-marked embedded line.
    private static readonly object?[][] Endings =
    [
        ["\n", null],
        ["\r\n", null],
        ["\n", "\r\n"],
        ["\r\n", "\n"],
    ];

    [TestCaseSource(nameof(Endings))]
    public async Task ExtractSuperclass_KeepsEveryComment_AndLeavesEmbeddedLinesByteExact(string eol, string? oddEol)
    {
        var source = Text(eol, oddEol,
            "// Copyright Acme",
            "using System; // for Console",
            "",
            "namespace Demo;",
            "",
            "public class Customer",
            "{",
            "    // the name~",
            "    public string Name { get; set; }",
            "    public   int   Age { get;  set; } // odd spacing",
            "}");
        var document = MakeDocument(source, eol);

        var result = await ExtractSuperclassAsync(document);

        var expected = Text(eol, oddEol,
            "// Copyright Acme",
            "using System; // for Console",
            "",
            "namespace Demo;",
            "",
            "public abstract class PersonBase",
            "{",
            "    // the name~",
            "    public string Name { get; set; }",
            "    public   int   Age { get;  set; } // odd spacing",
            "}");
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(Endings))]
    public async Task ExtractMembersToPartial_KeepsEveryComment_AndLeavesEmbeddedLinesByteExact(string eol, string? oddEol)
    {
        var source = Text(eol, oddEol,
            "using System; // for Console",
            "using System.Linq;",
            "",
            "namespace Demo",
            "{",
            "    public partial class Customer",
            "    {",
            "        // the name~",
            "        public string Name { get; set; }",
            "",
            "        /// <summary>Total.</summary>",
            "        public   int Total(int a)",
            "        {",
            "            return  a; // keep",
            "        }",
            "",
            "        public void Other() { }",
            "    }",
            "}");
        var document = MakeDocument(source, eol);

        var result = await ExtractMembersToPartialAsync(document);

        var expected = Text(eol, oddEol,
            "using System; // for Console",
            "using System.Linq;",
            "",
            "namespace Demo",
            "{",
            "    public partial class Customer",
            "    {",
            "        // the name~",
            "        public string Name { get; set; }",
            "",
            "        /// <summary>Total.</summary>",
            "        public   int Total(int a)",
            "        {",
            "            return  a; // keep",
            "        }",
            "    }",
            "}");
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(Endings))]
    public async Task ConvertTupleToClass_KeepsEveryComment_AndLeavesEmbeddedLinesByteExact(string eol, string? oddEol)
    {
        var source = Text(eol, oddEol,
            "using System; // for Console~",
            "",
            "namespace Demo;",
            "",
            "public class Svc",
            "{",
            "    public (int /* id */ Id, string Name) GetData() => default;",
            "}");
        var document = MakeDocument(source, eol);

        var result = await ConvertTupleToClassAsync(document);

        var expected = Text(eol, oddEol,
            "using System; // for Console~",
            "",
            "namespace Demo;",
            "",
            "public class DataResult",
            "{",
            "    public int /* id */ Id { get; set; }",
            "    public string Name { get; set; }",
            "}");
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task FormatNewRoot_NullArguments_Throw()
    {
        var document = MakeDocument("public class C\n{\n}\n", "\n");
        var root = await RootOf(document);

        await Assert.ThatAsync(async () => await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(null!, root, _ => true), Throws.TypeOf<ArgumentNullException>());
        await Assert.ThatAsync(async () => await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, null!, _ => true), Throws.TypeOf<ArgumentNullException>());
        await Assert.ThatAsync(async () => await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, root, null!), Throws.TypeOf<ArgumentNullException>());
    }
}

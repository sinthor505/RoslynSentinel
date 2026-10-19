using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice C9 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md):
/// byte-exact pins for how <see cref="AttributeTextEditBuilder"/> renders caller-supplied attribute text (inner spacing,
/// the passed end-of-line, eol validation) and rejects comments (boundary, inner, sibling), pinned on the code that still
/// went through a whole-subtree whitespace guard (since deleted), so the self-contained rendering must reproduce them exactly.
/// </summary>
[TestFixture]
[Category("AttributeTextEditBuilder")]
public class AttributeTextEditBuilderRenderTests
{
    private const string CommentsRejectedMessage = "attribute text must not contain comments; remove the comment and retry";

    private static (SourceText Text, CompilationUnitSyntax Root) ParseSource(string source)
    {
        var text = SourceText.From(source);
        return (text, CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot());
    }

    /// <summary>Adds the parsed list to a class declared at column 0, so the edit text is the rendered list plus the eol.</summary>
    private static string RenderViaAdd(string attributeSource, string eol)
    {
        var (text, root) = ParseSource("public class C\n{\n}\n");
        var target = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var list = AttributeTextEditBuilder.ParseAttributeList(attributeSource);
        Assert.That(list, Is.Not.Null);
        var edit = AttributeTextEditBuilder.BuildAddEdit(0, target, list!, text, eol);
        TestContext.Out.WriteLine(edit.NewText.Replace("\r", "\\r").Replace("\n", "\\n"));
        return edit.NewText;
    }

    private static AttributeSyntax OldAttribute() => ParseSource("[Obsolete]\npublic class C\n{\n}\n").Root.DescendantNodes().OfType<AttributeSyntax>().First();

    private static void AssertCommentsRejected(Action act)
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => act());
        Assert.That(ex!.Message, Is.EqualTo(CommentsRejectedMessage));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [TestCase("Obsolete", "[Obsolete]")]
    [TestCase("  Obsolete (  \"use X\" ,\r\n      true )  ", "[Obsolete(\"use X\", true)]")]
    [TestCase("[return:  NotNull]", "[return: NotNull]")]
    [TestCase("A,B( 1 ,2 )", "[A, B(1, 2)]")]
    [TestCase("AttributeUsage( AttributeTargets.All ,AllowMultiple=true )", "[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]")]
    [TestCase("Foo(typeof( List<int> ), nameof( X ))", "[Foo(typeof(List<int>), nameof(X))]")]
    [TestCase("Foo(new[]{1,2})", "[Foo(new[] { 1, 2 })]")]
    [TestCase("Foo(@\"a\r\nb\")", "[Foo(@\"a\r\nb\")]")]
    public void BuildAddEdit_RendersInnerSpacing_AndAppendsPassedEol(string attributeSource, string expectedList)
    {
        Assert.That(RenderViaAdd(attributeSource, "\n"), Is.EqualTo(expectedList + "\n"));
        Assert.That(RenderViaAdd(attributeSource, "\r\n"), Is.EqualTo(expectedList + "\r\n"));
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void BuildAddEdit_OwnLineMember_InsertsAtLineStartWithIndentation(string eol)
    {
        var (text, root) = ParseSource("public class C\n{\n    [Serializable]\n    public void M() { }\n}\n");
        var target = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var list = AttributeTextEditBuilder.ParseAttributeList("Conditional( \"X\" )")!;

        var edit = AttributeTextEditBuilder.BuildAddEdit(2, target, list, text, eol);

        var lineStart = text.Lines[3].Start;
        Assert.That(edit, Is.EqualTo(new AttributeTextEditBuilder.TextEdit(lineStart, lineStart, "    [Conditional(\"X\")]" + eol, 2)));
    }

    [Test]
    public void BuildAddEdit_InlineMember_InsertsBeforeKeywordWithSpace()
    {
        var (text, root) = ParseSource("public class C { [Serializable] public void M() { } }\n");
        var target = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var list = AttributeTextEditBuilder.ParseAttributeList("Conditional( \"X\" )")!;

        var edit = AttributeTextEditBuilder.BuildAddEdit(0, target, list, text, "\r\n");

        var anchor = target.Modifiers[0].SpanStart;
        Assert.That(edit, Is.EqualTo(new AttributeTextEditBuilder.TextEdit(anchor, anchor, "[Conditional(\"X\")] ", 0)));
    }

    [Test]
    public void BuildReplaceEdit_AttachedAttribute_RendersSpacingAndKeepsSpan()
    {
        var oldAttribute = OldAttribute();
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("[Obsolete,  Foo( 1 ,named:2 )]")!.Attributes[1];

        var edit = AttributeTextEditBuilder.BuildReplaceEdit(5, oldAttribute, newAttribute, "\r\n");

        Assert.That(edit, Is.EqualTo(new AttributeTextEditBuilder.TextEdit(oldAttribute.SpanStart, oldAttribute.Span.End, "Foo(1, named: 2)", 5)));
    }

    [TestCase("\r")]
    [TestCase("")]
    [TestCase("\n\n")]
    public void BuildAddEdit_And_BuildReplaceEdit_RejectNonNormalizableEol_AsArgumentException(string eol)
    {
        var (text, root) = ParseSource("public class C\n{\n}\n");
        var target = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete")!;

        var addEx = Assert.Throws<ArgumentException>(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list, text, eol));
        var replaceEx = Assert.Throws<ArgumentException>(() => AttributeTextEditBuilder.BuildReplaceEdit(0, OldAttribute(), list.Attributes[0], eol));

        Assert.That(addEx!.ParamName, Is.EqualTo("eol"));
        Assert.That(replaceEx!.ParamName, Is.EqualTo("eol"));
    }

    [Test]
    public void BuildAddEdit_NullEol_ThrowsArgumentNullException()
    {
        var (text, root) = ParseSource("public class C\n{\n}\n");
        var target = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete")!;

        var ex = Assert.Throws<ArgumentNullException>(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list, text, null!));

        Assert.That(ex!.ParamName, Is.EqualTo("eol"));
    }

    [Test]
    public void BuildAddEdit_CrEolWithComment_ReportsTheEolFirst()
    {
        var (text, root) = ParseSource("public class C\n{\n}\n");
        var target = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete(/* why */)")!;

        var ex = Assert.Throws<ArgumentException>(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list, text, "\r"));

        Assert.That(ex!.ParamName, Is.EqualTo("eol"));
    }

    [TestCase("[/* lead */ Obsolete]")]
    [TestCase("[Obsolete] // trailing")]
    [TestCase("[Obsolete] /* trailing */")]
    [TestCase("Obsolete(\"x\" /* why */)")]
    [TestCase("Obsolete(\"x\", // why\n true)")]
    [TestCase("[Obsolete /* inner */]")]
    [TestCase("[A /* sibling */, Obsolete]")]
    [TestCase("Obsolete(\n#if DEBUG\n\"x\"\n#endif\n)")]
    public void BuildAddEdit_AnyCommentOrDirective_IsRejected(string attributeSource)
    {
        var (text, root) = ParseSource("public class C\n{\n}\n");
        var target = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var list = AttributeTextEditBuilder.ParseAttributeList(attributeSource)!;

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list, text, "\n"));
    }

    [TestCase("[/* lead */ Obsolete]", 0)]
    [TestCase("[Obsolete] // trailing", 0)]
    [TestCase("Obsolete(\"y\" /* why */)", 0)]
    [TestCase("[A /* sibling before */, Obsolete]", 1)]
    [TestCase("[Obsolete, B /* sibling after */]", 0)]
    [TestCase("[Obsolete, B(/* inside sibling */)]", 0)]
    public void BuildReplaceEdit_CommentAnywhereInTheParsedList_IsRejected(string attributeSource, int attributeIndex)
    {
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList(attributeSource)!.Attributes[attributeIndex];

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildReplaceEdit(0, OldAttribute(), newAttribute, "\n"));
    }

    [Test]
    public void BuildReplaceEdit_DetachedAttribute_OnlyItsOwnTriviaIsChecked()
    {
        // Once detached from its list, a sibling's comment is no longer reachable: only the attribute itself is checked.
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("[A /* sibling */, Obsolete( 1 )]")!.Attributes[1].WithoutTrivia();

        var edit = AttributeTextEditBuilder.BuildReplaceEdit(0, OldAttribute(), newAttribute, "\n");

        Assert.That(edit.NewText, Is.EqualTo("Obsolete(1)"));
    }

    [Test]
    public void BuildReplaceEdit_DetachedAttributeWithInnerComment_IsRejected()
    {
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("Obsolete(1 /* why */)")!.Attributes[0].WithoutTrivia();

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildReplaceEdit(0, OldAttribute(), newAttribute, "\n"));
    }
}

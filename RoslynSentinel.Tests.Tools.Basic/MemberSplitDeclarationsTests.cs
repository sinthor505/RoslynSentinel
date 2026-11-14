using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Pure unit tests (no workspace) for the helpers behind Member addMember with several declarations in one source:
// MemberRefactoringEngine.TrySplitMemberDeclarations, ParseNewMemberDeclarations and DescribeAddedMembers, and
// MemberEditEffectFacts.DescribeAdded. Error texts are asserted on their stable prefixes or key phrases.
[TestFixture]
public class MemberSplitDeclarationsTests
{
    [Test]
    [Category("RefactoringStructuralTools")]
    public void TrySplit_TwoMethods_ReturnsBothInSourceOrder()
    {
        var members = MemberRefactoringEngine.TrySplitMemberDeclarations("void A() { }\r\nvoid B() { }");

        Assert.That(members, Is.Not.Null);
        Assert.That(members!.Select(NameOf), Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void TrySplit_FieldThenMethod_ReturnsBoth()
    {
        var members = MemberRefactoringEngine.TrySplitMemberDeclarations("private int _a;\r\nvoid M() { }");

        Assert.That(members, Is.Not.Null);
        Assert.That(members!.Select(NameOf), Is.EqualTo(new[] { "_a", "M" }));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void TrySplit_MethodThenHelper_ReturnsBoth()
    {
        var members = MemberRefactoringEngine.TrySplitMemberDeclarations("public void Run() { Helper(); }\r\nprivate void Helper() { }");

        Assert.That(members, Is.Not.Null);
        Assert.That(members!.Select(NameOf), Is.EqualTo(new[] { "Run", "Helper" }));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void TrySplit_ThreeDeclarations_ReturnsAllThreeInOrder()
    {
        var members = MemberRefactoringEngine.TrySplitMemberDeclarations("void A() { }\r\nvoid B() { }\r\nvoid C() { }");

        Assert.That(members, Is.Not.Null);
        Assert.That(members!.Select(NameOf), Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    [Category("RefactoringStructuralTools")]
    public void TrySplit_BlankLinesBetweenDeclarations_AreNotKeptAsLeadingTrivia(int blankLines)
    {
        var separator = string.Concat(Enumerable.Repeat("\r\n", blankLines + 1));

        var members = MemberRefactoringEngine.TrySplitMemberDeclarations("void A() { }" + separator + "void B() { }");

        Assert.That(members, Is.Not.Null);
        Assert.That(members!.Select(NameOf), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(members.Any(StartsWithBlankRun), Is.False);
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void TrySplit_DocCommentOnSecondMember_IsKeptInItsLeadingTrivia()
    {
        var members = MemberRefactoringEngine.TrySplitMemberDeclarations("void A() { }\r\n/// <summary>Adds B.</summary>\r\nvoid B() { }");

        Assert.That(members, Is.Not.Null);
        Assert.That(members!.Count, Is.EqualTo(2));
        Assert.That(members[1].GetLeadingTrivia().ToFullString(), Does.Contain("/// <summary>Adds B.</summary>"));
    }

    [TestCase("void A() { }")]
    [TestCase("   ")]
    [TestCase("void A() { }\r\nvoid B() { }\r\n}")]
    [TestCase("void A() { }\r\nvoid B() { ")]
    [TestCase("void A() { }\r\nvoid B( { }")]
    [TestCase("void A() { }\r\nreturn 1;")]
    [Category("RefactoringStructuralTools")]
    public void TrySplit_NotACleanMemberList_ReturnsNull(string source)
    {
        Assert.That(MemberRefactoringEngine.TrySplitMemberDeclarations(source), Is.Null);
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_OneValidMember_ReturnsOneElement()
    {
        var members = MemberRefactoringEngine.ParseNewMemberDeclarations("void A() { }");

        Assert.That(members, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_OneMalformedMember_ThrowsInvalidArgumentWithSingleDeclarationText()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.ParseNewMemberDeclarations("public int Broken("));

        Assert.That(ex!.Message, Does.StartWith("newMemberSource is invalid: The source does not parse as a single valid declaration"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_ThreeDeclarationsSecondMalformed_NamesDeclarationTwoAndItsName()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.ParseNewMemberDeclarations("void A() { }\r\nvoid B( { }\r\nvoid C() { }"));

        Assert.That(ex!.Message, Does.Contain("declaration 2"));
        Assert.That(ex.Message, Does.Contain("Method 'B'"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_StrayClosingBraceAfterTwoDeclarations_NamesExtraBrace()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.ParseNewMemberDeclarations("void A() { }\r\nvoid B() { }\r\n}"));

        Assert.That(ex!.Message, Does.Contain("extra or unbalanced '}'"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_ValidMethodThenBodyFragment_ThrowsTrailingDeclarationMessage()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.ParseNewMemberDeclarations("void A() { }\r\nreturn 1;"));

        Assert.That(ex!.Message, Does.StartWith("newMemberSource is invalid: "));
        Assert.That(ex.Message, Does.Not.Contain("declaration 2"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_UnparsableSingleSource_ThrowsDefaultMessage()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.ParseNewMemberDeclarations("   "));

        Assert.That(ex!.Message, Is.EqualTo("Failed to parse new member."));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_UnparsableSingleSource_ThrowsCallerSuppliedMessage()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.ParseNewMemberDeclarations("   ", "Cannot edit: invalid member source."));

        Assert.That(ex!.Message, Is.EqualTo("Cannot edit: invalid member source."));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void ParseNew_ListMissingClosingBrace_ThrowsNotAListMessage()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.ParseNewMemberDeclarations("void A() { }\r\nvoid B() { "));

        Assert.That(ex!.Message, Does.Contain("member declarations but does not parse as a list"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void DescribeAddedMembers_OneMember_ReturnsKindAndName()
    {
        var members = MemberRefactoringEngine.ParseNewMemberDeclarations("void A() { }");

        Assert.That(MemberRefactoringEngine.DescribeAddedMembers(members), Is.EqualTo("Method 'A'"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void DescribeAddedMembers_TwoMembers_ReturnsCountAndEachDescription()
    {
        var members = MemberRefactoringEngine.TrySplitMemberDeclarations("void A() { }\r\nprivate int _b;");

        Assert.That(members, Is.Not.Null);
        Assert.That(MemberRefactoringEngine.DescribeAddedMembers(members!), Is.EqualTo("2 members: Method 'A', Field '_b'"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void DescribeAdded_TwoMethods_GivesTwoRowsInSourceOrder()
    {
        var rows = MemberEditEffectFacts.DescribeAdded("void A() { }\r\nvoid B() { }", "Widget", isEnumMember: false);

        Assert.That(rows.Select(r => r.SimpleName), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(rows.Select(r => r.Display), Is.EqualTo(new[] { "Widget.A", "Widget.B" }));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void DescribeAdded_FieldWithTwoNamesThenMethod_GivesThreeRows()
    {
        var rows = MemberEditEffectFacts.DescribeAdded("private int _a, _b;\r\nvoid M() { }", "Widget", isEnumMember: false);

        Assert.That(rows.Select(r => r.SimpleName), Is.EqualTo(new[] { "_a", "_b", "M" }));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void DescribeAdded_OneMember_GivesSingleRow()
    {
        var rows = MemberEditEffectFacts.DescribeAdded("void A() { }", "Widget", isEnumMember: false);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Display, Is.EqualTo("Widget.A"));
        Assert.That(rows[0].SimpleName, Is.EqualTo("A"));
        Assert.That(rows[0].Container, Is.EqualTo("Widget"));
    }

    private static string NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text,
        FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.Text)),
        BaseTypeDeclarationSyntax type => type.Identifier.Text,
        _ => member.Kind().ToString(),
    };

    private static bool StartsWithBlankRun(MemberDeclarationSyntax member)
    {
        var leading = member.GetLeadingTrivia();
        return leading.Count > 0 && (leading[0].Kind() == SyntaxKind.EndOfLineTrivia || leading[0].Kind() == SyntaxKind.WhitespaceTrivia);
    }
}

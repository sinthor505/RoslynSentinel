using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// BuildInsertion into an EMPTY target class. A one-line body ("{ }") used to keep the open brace's trailing space
// and leave "{ " dangling at the end of its line.
[TestFixture]
public class MoveMemberInsertionTextTests
{
    private const string Block = "    public static int Bar() => 1;";

    private static string InsertIntoTarget(string source)
    {
        var text = SourceText.From(source);
        var target = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == "T");
        var insertion = MoveMemberTextEdits.BuildInsertion(text, target, Block, "\n");
        return MoveMemberTextEdits.ApplyChanges(text, new[] { insertion });
    }

    [Test]
    public void OneLineEmptyBodyWithSpace_HasNoTrailingWhitespaceAfterTheInsertion()
    {
        var result = InsertIntoTarget("public static class T { }\n");

        Assert.That(result, Is.EqualTo("public static class T {\n    public static int Bar() => 1;\n}\n"));
    }

    [Test]
    public void OneLineEmptyBodyWithoutSpace_InsertsOnItsOwnLines()
    {
        var result = InsertIntoTarget("public static class T {}\n");

        Assert.That(result, Is.EqualTo("public static class T {\n    public static int Bar() => 1;\n}\n"));
    }

    [Test]
    public void OneLineEmptyBody_KeepsTheClassIndentationOnTheClosingBrace()
    {
        var result = InsertIntoTarget("namespace N\n{\n    public class T { }\n}\n");

        Assert.That(result, Is.EqualTo("namespace N\n{\n    public class T {\n    public static int Bar() => 1;\n    }\n}\n"));
    }

    [Test]
    public void EmptyBodyAcrossLines_StillInsertsAfterTheOpenBrace()
    {
        var result = InsertIntoTarget("public class T\n{\n}\n");

        Assert.That(result, Is.EqualTo("public class T\n{\n    public static int Bar() => 1;\n}\n"));
    }

    [Test]
    public void OneLineEmptyBodyWithComment_KeepsTheComment()
    {
        var result = InsertIntoTarget("public class T { /* keep */ }\n");

        Assert.That(result, Does.Contain("/* keep */"));
    }
}

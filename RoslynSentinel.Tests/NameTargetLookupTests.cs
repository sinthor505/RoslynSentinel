using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

public class NameTargetLookupTests
{
    [Test]
    public void RequireSingle_EmptyList_ReturnsNull()
    {
        var matches = new System.Collections.Generic.List<MethodDeclarationSyntax>();

        Assert.That(NameTargetLookup.RequireSingle(matches, "Method", "M"), Is.Null);
    }

    [Test]
    public void RequireSingle_SingleMatch_ReturnsIt()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { } }");
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        var result = NameTargetLookup.RequireSingle(methods, "Method", "M");

        Assert.That(result, Is.SameAs(methods[0]));
    }

    [Test]
    public void RequireSingle_TwoOverloads_ThrowsNamingBothLines()
    {
        var tree = CSharpSyntaxTree.ParseText("class C\n{\n    void M() { }\n    void M(int x) { }\n}");
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => NameTargetLookup.RequireSingle(methods, "Method", "M"));

        Assert.That(ex!.Message, Does.Contain("matches 2 declarations").And.Contain(":3 `").And.Contain(":4 `"));
    }

    [Test]
    public void RequireSingle_InterfaceMethodPlusOneImplementation_ReturnsFirstMatch()
    {
        var tree = CSharpSyntaxTree.ParseText("interface I { void M(); }\nclass C : I { public void M() { } }");
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        var result = NameTargetLookup.RequireSingle(methods, "Method", "M");

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.SameAs(methods[0]));
    }

    [Test]
    public void RequireSingle_TwoOverloadsPlusInterfaceDeclaration_Throws()
    {
        var tree = CSharpSyntaxTree.ParseText("interface I { void M(); }\nclass C\n{\n    void M() { }\n    void M(int x) { }\n}");
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        Assert.Throws<ToolAmbiguousMatchException>(() => NameTargetLookup.RequireSingle(methods, "Method", "M"));
    }
}

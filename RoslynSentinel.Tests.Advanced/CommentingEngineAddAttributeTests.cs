namespace RoslynSentinel.Tests.Advanced;

public class CommentingEngineAddAttributeTests
{
    // AddAttribute is a private static helper; its only public callers need a project directory to inject the
    // attribute class, so the helper is invoked by reflection to pin its trivia placement directly.
    [Test]
    [Category("CommentingEngine")]
    [TestCase("    /// <summary>Does M.</summary>\n")]
    [TestCase("    // plain comment\n")]
    [TestCase("    // first\n    /// <summary>Does M.</summary>\n")]
    public void AddAttribute_MemberWithLeadingComment_PlacesAttributeBetweenCommentAndDeclaration(string leadingComment)
    {
        var classSource = "class C\n{\n" + leadingComment + "    public void M()\n    {\n        return;\n    }\n}\n";
        var member = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(classSource).GetRoot()
            .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single();
        var attribute = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Attribute(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseName("ContentHash"));

        var addAttribute = typeof(RoslynSentinel.Engines.Advanced.CommentingEngine).GetMethod("AddAttribute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.That(addAttribute, Is.Not.Null, "private helper AddAttribute was renamed or removed");
        var tagged = (Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax)addAttribute!.Invoke(null, [member, attribute])!;

        Assert.That(tagged.ToFullString(), Is.EqualTo(leadingComment + "    [ContentHash]\n    public void M()\n    {\n        return;\n    }\n"),
            "comment first, then the attribute on its own indented line, then the declaration");

        var resultSource = "class C\n{\n" + tagged.ToFullString() + "}\n";
        var syntaxErrors = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(resultSource).GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();
        Assert.That(syntaxErrors, Is.Empty);
    }
}

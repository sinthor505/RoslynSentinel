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

    // Parses a class holding one method, invokes the private AddAttribute with [ContentHash], returns the member's full text.
    private static string TagFirstMethod(string classSource)
    {
        var member = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(classSource).GetRoot()
            .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single();
        var attribute = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Attribute(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseName("ContentHash"));
        var addAttribute = typeof(RoslynSentinel.Engines.Advanced.CommentingEngine).GetMethod("AddAttribute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.That(addAttribute, Is.Not.Null, "private helper AddAttribute was renamed or removed");
        return ((Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax)addAttribute!.Invoke(null, [member, attribute])!).ToFullString();
    }

    // A CRLF member must come back CRLF throughout: the attribute line's own terminator is taken from the member,
    // and the moved comment keeps its terminators. The write path refuses a bare LF in a CRLF file (EolChangeGuard).
    [Test]
    [Category("CommentingEngine")]
    [TestCase("    /// <summary>Does M.</summary>\r\n")]
    [TestCase("    // first\r\n    /// <summary>Does M.</summary>\r\n")]
    public void AddAttribute_CrlfMemberWithLeadingComment_KeepsCrlfThroughoutAndPlacesAttributeBelowComment(string leadingComment)
    {
        var classSource = "class C\r\n{\r\n" + leadingComment + "    public void M()\r\n    {\r\n        return;\r\n    }\r\n}\r\n";

        var tagged = TagFirstMethod(classSource);

        Assert.That(tagged, Is.EqualTo(leadingComment + "    [ContentHash]\r\n    public void M()\r\n    {\r\n        return;\r\n    }\r\n"));
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch(tagged, "(?<!\r)\n"), Is.False, "no bare LF in a CRLF member");
    }

    // Existing-attribute branch: the comment sits above the first attribute list and must stay there untouched; the
    // new list goes after the existing one, indented like the whitespace directly before the declaration (not like
    // the oddly indented first comment line), with the member's own line ending.
    [Test]
    [Category("CommentingEngine")]
    [TestCase("\n")]
    [TestCase("\r\n")]
    public void AddAttribute_MemberWithExistingAttributeAndLeadingComment_AppendsNewListAfterExistingAndLeavesCommentUntouched(string eol)
    {
        var comment = "        // odd indent\n    /// <summary>Does M.</summary>\n".Replace("\n", eol);
        var classSource = "class C\n{\n".Replace("\n", eol) + comment + "    [Obsolete]\n    public void M()\n    {\n        return;\n    }\n}\n".Replace("\n", eol);

        var tagged = TagFirstMethod(classSource);

        Assert.That(tagged, Is.EqualTo(comment + "    [Obsolete]\n    [ContentHash]\n    public void M()\n    {\n        return;\n    }\n".Replace("\n", eol)));
        if (eol == "\r\n")
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch(tagged, "(?<!\r)\n"), Is.False, "no bare LF in a CRLF member");
    }
}

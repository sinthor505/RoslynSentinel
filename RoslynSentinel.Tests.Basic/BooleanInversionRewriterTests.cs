using NUnit.Framework;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
[Category("BooleanInversionRewriter")] // sentinel:auto-category
[Category("SemanticReplaceRole")] // sentinel:auto-category
public class BooleanInversionRewriterTests
{
    [TestCase(SemanticReplaceRole.WriteLiteral, "true", "IsError", false, "false")]
    [TestCase(SemanticReplaceRole.WriteLiteral, "false", "IsError", false, "true")]
    [TestCase(SemanticReplaceRole.WriteExpression, "a && b", "IsError", false, "!(a && b)")]
    [TestCase(SemanticReplaceRole.WriteExpression, "Foo()", "IsError", false, "!Foo()")]
    [TestCase(SemanticReplaceRole.WriteExpression, "!ok", "IsError", false, "ok")]
    [TestCase(SemanticReplaceRole.WriteExpression, "Foo(a) && Bar(b)", "IsError", false, "!(Foo(a) && Bar(b))")]
    [TestCase(SemanticReplaceRole.WriteExpression, "!a && b", "IsError", false, "!(!a && b)")]
    [TestCase(SemanticReplaceRole.WriteExpression, "a.B(c).D(e)", "IsError", false, "!(a.B(c).D(e))")]
    [TestCase(SemanticReplaceRole.WriteExpression, "x.Ok", "IsError", false, "!x.Ok")]
    [TestCase(SemanticReplaceRole.Read, "x?.IsSuccess", "IsError", false, "!x?.IsError")]
    [TestCase(SemanticReplaceRole.Read, "IsSuccess", "IsError", false, "!IsError")]
    [TestCase(SemanticReplaceRole.Read, "x.IsSuccess", "IsError", false, "!x.IsError")]
    [TestCase(SemanticReplaceRole.Read, "IsSuccess", "IsError", true, "(!IsError)")]
    [TestCase(SemanticReplaceRole.Read, "x.IsSuccess", "IsError", true, "(!x.IsError)")]
    [TestCase(SemanticReplaceRole.NegatedRead, "!x.IsSuccess", "IsError", false, "x.IsError")]
    [TestCase(SemanticReplaceRole.NegatedRead, "!IsSuccess", "IsError", false, "IsError")]
    [TestCase(SemanticReplaceRole.NameOf, "IsSuccess", "IsError", false, "IsError")]
    [TestCase(SemanticReplaceRole.DeclarationInitializer, "true", "IsError", false, "false")]
    [TestCase(SemanticReplaceRole.DeclarationInitializer, "false", "IsError", false, "true")]
    public void Rewrite_WithValidInputs_ReturnsExpectedOutput(SemanticReplaceRole role, string originalText, string newName, bool parenthesize, string expected)
    {
        var result = BooleanInversionRewriter.Rewrite(role, originalText, newName, parenthesize);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase(SemanticReplaceRole.WriteLiteral, "invalid", "IsError", false)]
    [TestCase(SemanticReplaceRole.Unsupported, "x.IsSuccess", "IsError", false)]
    public void Rewrite_WithInvalidInputs_ThrowsArgumentException(SemanticReplaceRole role, string originalText, string newName, bool parenthesize)
    {
        Assert.Throws<ArgumentException>(() => BooleanInversionRewriter.Rewrite(role, originalText, newName, parenthesize));
    }
}

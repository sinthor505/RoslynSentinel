using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[Category("ReferenceRoleClassifier")] // sentinel:auto-category
[Category("SemanticReplaceRole")] // sentinel:auto-category
public class ReferenceRoleClassifierTests
{
    private IdentifierNameSyntax GetIdentifier(string code, string targetName)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var identifiers = root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(id => id.Identifier.Text == targetName).ToList();
        if (identifiers.Count == 0)
        {
            throw new InvalidOperationException($"No identifier named '{targetName}' found in code");
        }
        return identifiers.First();
    }

    [Test]
    public void Classify_MemberAccessWriteLiteral_ReturnsWriteLiteral()
    {
        var code = "class C { void M() { var x = new object(); x.IsSuccess = true; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.WriteLiteral));
    }

    [Test]
    public void Classify_MemberAccessWriteExpression_ReturnsWriteExpression()
    {
        var code = "class C { void M() { var x = new object(); x.IsSuccess = a && b; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.WriteExpression));
    }

    [Test]
    public void Classify_ObjectInitializerWriteLiteral_ReturnsWriteLiteral()
    {
        var code = "class C { void M() { var obj = new T { IsSuccess = false }; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.WriteLiteral));
    }

    [Test]
    public void Classify_BareAssignmentWriteLiteral_ReturnsWriteLiteral()
    {
        var code = "class C { void M() { IsSuccess = true; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.WriteLiteral));
    }

    [Test]
    public void Classify_ReadContextOnRight_ReturnsRead()
    {
        var code = "class C { void M() { var z = x.IsSuccess; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Read));
    }

    [Test]
    public void Classify_UnrelatedShape_ReturnsRead()
    {
        var code = "class C { void M() { var y = x.IsSuccess.ToString(); } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Read));
    }

    [Test]
    public void Classify_CompoundAssignmentBitwiseOr_ReturnsUnsupported()
    {
        var code = "class C { void M() { x.IsSuccess |= y; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
    }

    [Test]
    public void Classify_CompoundAssignmentBitwiseAnd_ReturnsUnsupported()
    {
        var code = "class C { void M() { x.IsSuccess &= y; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
    }

    [Test]
    public void Classify_ObjectInitializerRightSideMemberAccess_ReturnsRead()
    {
        var code = "class C { void M() { var obj = new T { A = x.IsSuccess }; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Read));
    }

    [Test]
    public void Classify_ObjectInitializerRightSideMethodCall_ReturnsRead()
    {
        var code = "class C { void M() { var obj = new T { Foo = Bar(IsSuccess) }; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Read));
    }

    [Test]
    public void Classify_BareIsPattern_ReturnsUnsupported()
    {
        var code = "class C { void M() { if (IsSuccess is true) { } } }";
        var identifier = GetIdentifier(code, "IsSuccess");

        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);

        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [TestCase("class C { void M() { (x.IsSuccess, y) = t; } }")]
    [TestCase("class C { void M() { (IsSuccess, y) = t; } }")]
    public void Classify_DeconstructionTarget_ReturnsUnsupported(string code)
    {
        var identifier = GetIdentifier(code, "IsSuccess");

        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);

        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_TupleExpressionOnRightSide_ReturnsRead()
    {
        var code = "class C { void M() { t = (x.IsSuccess, y); } }";
        var identifier = GetIdentifier(code, "IsSuccess");

        Assert.That(ReferenceRoleClassifier.Classify(identifier), Is.EqualTo(SemanticReplaceRole.Read));
    }

    [Test]
    public void Classify_Read_ReturnsRead()
    {
        var code = "class C { void M() { if (x.IsSuccess) { } } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Read));
    }

    [Test]
    public void Classify_NegatedRead_ReturnsNegatedRead()
    {
        var code = "class C { void M() { if (!x.IsSuccess) { } } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.NegatedRead));
    }

    [Test]
    public void Classify_NameOf_ReturnsNameOf()
    {
        var code = "class C { void M() { var s = nameof(IsSuccess); } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.NameOf));
    }

    [Test]
    public void Classify_CompoundAssignmentWithReason_ReturnsUnsupportedWithReason()
    {
        var code = "class C { void M() { x.IsSuccess |= y; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_IncrementUnsupported_ReturnsUnsupportedWithReason()
    {
        var code = "class C { void M() { x.IsSuccess++; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_EqualityComparisonUnsupported_ReturnsUnsupportedWithReason()
    {
        var code = "class C { void M() { var b = x.IsSuccess == true; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_RefArgumentOnMemberAccess_ReturnsUnsupported()
    {
        var code = "class C { void M() { Foo(ref x.IsSuccess); } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_OutArgumentOnMemberAccess_ReturnsUnsupported()
    {
        var code = "class C { void M() { Foo(out x.IsSuccess); } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_PropertyPattern_ReturnsUnsupported()
    {
        var code = "class C { void M() { if (x is { IsSuccess: true }) { } } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_IsPatternTrue_ReturnsUnsupported()
    {
        var code = "class C { void M() { if (x.IsSuccess is true) { } } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_IsPatternFalse_ReturnsUnsupported()
    {
        var code = "class C { void M() { if (x.IsSuccess is false) { } } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Classify_IsPatternNotTrue_ReturnsUnsupported()
    {
        var code = "class C { void M() { if (x.IsSuccess is not true) { } } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier, out var reason);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }
}

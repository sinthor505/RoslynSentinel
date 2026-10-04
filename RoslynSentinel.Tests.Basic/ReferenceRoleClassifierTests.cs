using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

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
    public void Classify_ReadContextOnRight_ReturnsUnsupported()
    {
        var code = "class C { void M() { var z = x.IsSuccess; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
    }

    [Test]
    public void Classify_UnrelatedShape_ReturnsUnsupported()
    {
        var code = "class C { void M() { var y = x.IsSuccess.ToString(); } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
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
    public void Classify_ObjectInitializerRightSideMemberAccess_ReturnsUnsupported()
    {
        var code = "class C { void M() { var obj = new T { A = x.IsSuccess }; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
    }

    [Test]
    public void Classify_ObjectInitializerRightSideMethodCall_ReturnsUnsupported()
    {
        var code = "class C { void M() { var obj = new T { Foo = Bar(IsSuccess) }; } }";
        var identifier = GetIdentifier(code, "IsSuccess");
        
        var role = ReferenceRoleClassifier.Classify(identifier);
        
        Assert.That(role, Is.EqualTo(SemanticReplaceRole.Unsupported));
    }
}

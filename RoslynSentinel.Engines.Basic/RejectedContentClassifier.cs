using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Classifies rejected write content into a <see cref="RejectedContentShape"/> so oversize advice
/// can name the Member call that fits it.
/// </summary>
public static class RejectedContentClassifier
{
    /// <summary>
    /// Returns the shape of <paramref name="content"/>: Unspecified when it is blank or does not parse
    /// as member declarations; FileLevel when it has usings or type/namespace/delegate declarations;
    /// otherwise SingleMember or MultipleMembers by declaration count.
    /// </summary>
    /// <param name="content">The rejected newMemberSource or file body.</param>
    public static RejectedContentShape Classify(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return RejectedContentShape.Unspecified;
        }

        var unit = SyntaxFactory.ParseCompilationUnit(content);
        if (unit.Usings.Count > 0 ||
            unit.Members.Any(m => m is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            return RejectedContentShape.FileLevel;
        }

        // Intentionally mirrors MemberRefactoringEngine.DetectMultipleMemberDeclarations: that method is
        // internal and returns a message rather than a count, so the same probe is repeated here.
        var probe = SyntaxFactory.ParseCompilationUnit("class __SentinelMemberCountProbe__\n{\n" + content + "\n}\n");
        if (probe.Members.Count != 1 || probe.Members[0] is not ClassDeclarationSyntax probeClass)
        {
            return RejectedContentShape.Unspecified;
        }

        var members = probeClass.Members;
        if (members.Any(m => m is IncompleteMemberSyntax))
        {
            return RejectedContentShape.Unspecified;
        }

        return members.Count switch
        {
            0 => RejectedContentShape.Unspecified,
            1 => RejectedContentShape.SingleMember,
            _ => RejectedContentShape.MultipleMembers,
        };
    }
}

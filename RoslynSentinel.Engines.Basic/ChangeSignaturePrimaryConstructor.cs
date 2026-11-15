using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

// Change-signature support for primary constructors of records and classes (staged contract; bodies pending).
internal static class ChangeSignaturePrimaryConstructor
{
    internal static TypeDeclarationSyntax? FindTypeWithPrimaryConstructor(CompilationUnitSyntax root, string typeName)
    {
        throw new NotImplementedException(); // TODO implement (Step 2)
    }

    internal static string? RecordPolicyRefusal(TypeDeclarationSyntax type, IReadOnlyList<SignatureParameterSpec> parameters)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    internal static Task<ChangeSignatureResult> ApplyAsync(Document document, TypeDeclarationSyntax type, IReadOnlyList<SignatureParameterSpec> parameters, Solution solution, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 2)
    }
}

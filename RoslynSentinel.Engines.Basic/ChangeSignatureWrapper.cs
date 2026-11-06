using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Support for ChangeSignatureAsync with oldSignature keepAsDeprecatedWrapper: refusals decided before any edit, the wrapper text
/// (built by <see cref="MoveMemberTextEdits.BuildForwarder"/>), and the post-edit check that the wrapper's call binds to the new member.
/// </summary>
internal static class ChangeSignatureWrapper
{
    private static string RemoveHint => ToolCallHint.Call("ChangeSignature", ("oldSignature", "remove"));

    /// <summary>
    /// Returns an error (nothing may be written) when wrapper mode cannot be honoured for this member and parameter list, otherwise the
    /// old and new signature display text ("Save(int, string)"). Every refusal names the reason and the next step.
    /// </summary>
    internal static (string? Error, string OldSignature, string NewSignature) Prepare(SemanticModel semanticModel, BaseMethodDeclarationSyntax methodDecl, IMethodSymbol? symbol, IReadOnlyList<SignatureParameterSpec> parameters, IReadOnlyList<ParameterSyntax> originalParams, string methodName, bool autoMode = false)
    {
        // clause: what is wrong, starting with the verb ("cannot wrap 'M': ..."); advice: the next step for the explicit-wrapper caller.
        (string?, string, string) Refuse(string clause, string advice) => (autoMode ? "a deprecated wrapper " + clause : $"oldSignature keepAsDeprecatedWrapper {clause} Nothing was changed. {advice}", string.Empty, string.Empty);

        if (methodDecl is ConstructorDeclarationSyntax)
        {
            // TODO ChangeSignature: wrapper mode for constructors
            return Refuse($"does not support constructors yet: '{methodName}' is a constructor.", $"Retry with {RemoveHint}, or add the new constructor beside the old one and migrate its callers by hand.");
        }

        if (methodDecl is not MethodDeclarationSyntax method || symbol == null)
        {
            return Refuse($"could not resolve '{methodName}' to a method symbol.", $"Retry with {RemoveHint}.");
        }

        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride || symbol.ContainingType.TypeKind == TypeKind.Interface)
        {
            return Refuse($"cannot wrap '{methodName}': it is virtual, abstract, override or an interface member, and a wrapper beside it would change dispatch for overriding or implementing types.", $"Retry with {RemoveHint}.");
        }

        if (symbol.IsExtensionMethod || method.ExplicitInterfaceSpecifier != null || method.Modifiers.Any(SyntaxKind.PartialKeyword) || symbol.IsExtern || symbol.ReturnsByRef || symbol.ReturnsByRefReadonly)
        {
            return Refuse($"cannot wrap '{methodName}': an extension, explicit-interface, partial, extern or ref-returning method has no safe generated wrapper.", $"Retry with {RemoveHint}.");
        }

        foreach (var spec in parameters.OfType<NewParameterSpec>())
        {
            if (string.IsNullOrWhiteSpace(spec.DefaultValueExpression))
            {
                return Refuse($"needs a defaultValue for every new parameter, and '{spec.Name}' has none: the wrapper passes it to the new member and migrated callers receive it as their fill-in argument.", $"Pass defaultValue for '{spec.Name}', or retry with {RemoveHint}.");
            }
        }

        var oldKeys = new List<(ITypeSymbol? Type, string Text, bool ByRef)>();
        for (var i = 0; i < originalParams.Count; i++)
        {
            oldKeys.Add((symbol.Parameters[i].Type, NormalizeType(originalParams[i].Type), symbol.Parameters[i].RefKind != RefKind.None));
        }

        var newKeys = new List<(ITypeSymbol? Type, string Text, bool ByRef)>();
        var newTypeTexts = new List<string>();
        foreach (var spec in parameters)
        {
            if (spec is ExistingParameterSpec existing)
            {
                newKeys.Add(oldKeys[existing.OriginalIndex]);
                newTypeTexts.Add(DescribeParameterType(originalParams[existing.OriginalIndex]));
            }
            else if (spec is NewParameterSpec added)
            {
                var resolved = semanticModel.GetSpeculativeTypeInfo(method.ParameterList.SpanStart, SyntaxFactory.ParseTypeName(added.Type), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
                newKeys.Add((resolved is { TypeKind: not TypeKind.Error } ? resolved : null, StripWhitespace(added.Type), false));
                newTypeTexts.Add(added.Type.Trim());
            }
        }

        var oldTypeTexts = originalParams.Select(DescribeParameterType).ToList();
        var oldSignature = $"{methodName}({string.Join(", ", oldTypeTexts)})";
        var newSignature = $"{methodName}({string.Join(", ", newTypeTexts)})";
        if (oldKeys.Count == newKeys.Count && oldKeys.Zip(newKeys, SameParameter).All(same => same))
        {
            return Refuse($"cannot keep '{methodName}': the old and the new signature both take ({string.Join(", ", oldTypeTexts)}), so the wrapper and the member would be the same overload (renaming a parameter or changing a default value does not change the signature; ref, out and in count as one mode).", "Change a parameter type, the order or the count so the overloads differ, or retry with " + RemoveHint + ".");
        }

        return (null, oldSignature, newSignature);
    }

    // Overload identity: same type and same by-reference-ness. Falls back to the whitespace-free type text when either side did not bind.
    private static bool SameParameter((ITypeSymbol? Type, string Text, bool ByRef) a, (ITypeSymbol? Type, string Text, bool ByRef) b)
    {
        if (a.ByRef != b.ByRef)
        {
            return false;
        }

        return a.Type != null && b.Type != null ? SymbolEqualityComparer.Default.Equals(a.Type, b.Type) : a.Text == b.Text;
    }

    private static string StripWhitespace(string text) => Regex.Replace(text, @"\s+", string.Empty);

    private static string NormalizeType(TypeSyntax? type) => StripWhitespace(type?.ToString() ?? string.Empty);

    // "ref int" / "string": the passing mode (when not by value) plus the declared type, as shown in the [Obsolete] message and errors.
    private static string DescribeParameterType(ParameterSyntax parameter)
    {
        var mode = parameter.Modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword));
        var type = (parameter.Type?.ToString() ?? string.Empty).Trim();
        return mode.IsKind(SyntaxKind.None) ? type : mode.ValueText + " " + type;
    }

    /// <summary>
    /// The wrapper's source: the OLD parameter list, the same return type, generics and constraints, marked [Obsolete("Use <new signature>")],
    /// with an expression body that passes EVERY argument of the new signature explicitly, positionally and in the new order (a new parameter
    /// gets its default value expression, never an omitted argument). The call is receiver-qualified (this. for an instance method, the
    /// containing type for a static one) so a parameter or local that shares the method's name cannot capture it; a generic method passes
    /// explicit type arguments (BuildForwarder). <see cref="VerifyBinding"/> then proves the call binds to the new member.
    /// </summary>
    internal static string BuildWrapperText(MethodDeclarationSyntax method, IMethodSymbol symbol, IReadOnlyList<SignatureParameterSpec> parameters, IReadOnlyList<ParameterSyntax> originalParams, string newSignature, string eol)
    {
        var arguments = new List<string>();
        foreach (var spec in parameters)
        {
            arguments.Add(spec is ExistingParameterSpec existing
                ? MoveMemberTextEdits.FormatForwarderArgument(originalParams[existing.OriginalIndex])
                : ((NewParameterSpec)spec).DefaultValueExpression.Trim());
        }

        var receiver = symbol.IsStatic ? symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) : "this";
        return MoveMemberTextEdits.BuildForwarder(method, receiver, true, symbol.ContainingType.Name, eol, arguments, "Use " + newSignature);
    }

    /// <summary>
    /// Proves the wrapper cannot recurse: in the rewritten declaration file, the call inside the annotated wrapper must bind to the annotated
    /// new member (not to the wrapper, another overload, or nothing/ambiguously). Returns an error to refuse with, or null when it binds correctly.
    /// </summary>
    internal static string? VerifyBinding(SemanticModel semanticModel, SyntaxNode root, SyntaxAnnotation wrapperAnnotation, SyntaxAnnotation memberAnnotation, string newSignature)
    {
        var wrapper = root.GetAnnotatedNodes(wrapperAnnotation).OfType<MethodDeclarationSyntax>().SingleOrDefault();
        var member = root.GetAnnotatedNodes(memberAnnotation).Select(n => n.Parent).OfType<MethodDeclarationSyntax>().SingleOrDefault();
        var call = wrapper?.ExpressionBody?.Expression as InvocationExpressionSyntax;
        if (wrapper == null || member == null || call == null)
        {
            return $"oldSignature keepAsDeprecatedWrapper could not locate the generated wrapper to verify it. Nothing was changed. Retry with {RemoveHint}.";
        }

        var expected = semanticModel.GetDeclaredSymbol(member);
        var bound = semanticModel.GetSymbolInfo(call).Symbol as IMethodSymbol;
        if (expected != null && bound != null && SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, expected.OriginalDefinition))
        {
            return null;
        }

        var boundText = bound == null ? "nothing unambiguous" : bound.ToDisplayString();
        return $"oldSignature keepAsDeprecatedWrapper refused: the generated wrapper's call would bind to {boundText} instead of the new member {newSignature}, which could make the wrapper call itself or the wrong overload. Nothing was changed. Retry with {RemoveHint}, or choose new parameter types that are distinct from every other overload.";
    }

    /// <summary>One migrated call site as reported to the caller: the call before and after, on a single line, with the 1-based line in the ORIGINAL text.</summary>
    internal static RewrittenCallSite DescribeMigratedCall(FilePathWrapper filePath, int line, InvocationExpressionSyntax call, SeparatedSyntaxList<ArgumentSyntax> arguments, IReadOnlyList<(int SrcIndex, ExpressionSyntax? Literal)> plan)
    {
        static string ArgumentText(ArgumentSyntax a) => (a.RefKindKeyword.IsKind(SyntaxKind.None) ? string.Empty : a.RefKindKeyword.ValueText + " ") + a.Expression;

        var newArguments = plan.Select(p => p.Literal != null ? p.Literal.ToString() : ArgumentText(arguments[p.SrcIndex]));
        return new RewrittenCallSite(filePath.ToString(), line, OneLine(call.ToString()), OneLine(call.Expression + "(" + string.Join(", ", newArguments) + ")"));
    }

    private static string OneLine(string text)
    {
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= 160 ? collapsed : collapsed.Substring(0, 157) + "...";
    }
}

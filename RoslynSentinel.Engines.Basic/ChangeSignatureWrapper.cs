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

        if (symbol == null || methodDecl is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax))
        {
            // TODO the declared method names are not in scope here (the declaration is already located, only its symbol is missing); when ChangeSignature's lookup passes the file's method names, add the ranked candidates or the no-similar-name fact.
            return Refuse($"could not resolve '{methodName}' to a method or constructor symbol.", $"Retry with {RemoveHint}.");
        }

        // A constructor: the declaration is rewritten in place (its initializer stays with it) and the wrapper is a fresh `: this(...)` constructor beside it.
        // A static constructor never gets here (it has no parameters; the engine refuses it earlier) and a primary constructor has no ConstructorDeclarationSyntax.
        var isConstructor = methodDecl is ConstructorDeclarationSyntax;
        if (isConstructor)
        {
            if (methodDecl.Modifiers.Any(SyntaxKind.PartialKeyword) || methodDecl.Modifiers.Any(SyntaxKind.StaticKeyword) || symbol.IsExtern)
            {
                return Refuse($"cannot wrap '{methodName}': a partial, static or extern constructor has no safe generated wrapper.", $"Retry with {RemoveHint}.");
            }
        }
        else
        {
            var method = (MethodDeclarationSyntax)methodDecl;
            if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride || symbol.ContainingType.TypeKind == TypeKind.Interface)
            {
                return Refuse($"cannot wrap '{methodName}': it is virtual, abstract, override or an interface member, and a wrapper beside it would change dispatch for overriding or implementing types.", $"Retry with {RemoveHint}.");
            }

            if (symbol.IsExtensionMethod || method.ExplicitInterfaceSpecifier != null || method.Modifiers.Any(SyntaxKind.PartialKeyword) || symbol.IsExtern || symbol.ReturnsByRef || symbol.ReturnsByRefReadonly)
            {
                return Refuse($"cannot wrap '{methodName}': an extension, explicit-interface, partial, extern or ref-returning method has no safe generated wrapper.", $"Retry with {RemoveHint}.");
            }
        }

        foreach (var spec in parameters.OfType<NewParameterSpec>())
        {
            if (string.IsNullOrWhiteSpace(spec.DefaultValueExpression))
            {
                return Refuse($"needs a defaultValue for every new parameter, and '{spec.Name}' has none: the wrapper passes it to the new member and migrated callers receive it as their fill-in argument.", $"Pass defaultValue for '{spec.Name}', or retry with {RemoveHint}.");
            }
        }

        // The wrapper body (in the declaring type, same static-ness as the member, old parameters in scope) passes each default value, so a name-bearing one must bind there.
        foreach (var added in AnalyzeNewDefaults(semanticModel, methodDecl, parameters).Where(d => d.NeedsScopeCheck))
        {
            if (DefaultBindingProblem(semanticModel, ScopePosition(methodDecl), added.Expression, added.ParameterType, !isConstructor && !symbol.IsStatic) is { } problem)
            {
                return Refuse($"cannot wrap '{methodName}': the defaultValue '{added.Text}' of new parameter '{added.Name}' does not bind inside '{symbol.ContainingType.Name}', where the wrapper passes it ({problem}).", $"Pass a defaultValue that binds in '{symbol.ContainingType.Name}', or retry with {RemoveHint}.");
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
                var resolved = semanticModel.GetSpeculativeTypeInfo(methodDecl.ParameterList.SpanStart, SyntaxFactory.ParseTypeName(added.Type), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
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
    internal static string BuildWrapperText(BaseMethodDeclarationSyntax declaration, IMethodSymbol symbol, IReadOnlyList<SignatureParameterSpec> parameters, IReadOnlyList<ParameterSyntax> originalParams, string newSignature, string eol)
    {
        var arguments = new List<string>();
        foreach (var spec in parameters)
        {
            arguments.Add(spec is ExistingParameterSpec existing
                ? MoveMemberTextEdits.FormatForwarderArgument(originalParams[existing.OriginalIndex])
                : ((NewParameterSpec)spec).DefaultValueExpression.Trim());
        }

        if (declaration is ConstructorDeclarationSyntax constructor)
        {
            // [Obsolete("Use T(newparams)")] public T(old params) : this(new args) { }: the constructor keeps its accessibility (and unsafe) and the OLD parameter list verbatim, defaults included.
            var modifiers = constructor.Modifiers.Where(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.InternalKeyword) || m.IsKind(SyntaxKind.UnsafeKeyword))
                .Select(m => m.ValueText + " ");
            return "[global::System.Obsolete(\"" + ("Use " + newSignature).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\")]" + eol
                + string.Concat(modifiers) + constructor.Identifier.ValueText + constructor.ParameterList.WithoutTrivia() + " : this(" + string.Join(", ", arguments) + ")" + eol
                + "{" + eol + "}";
        }

        var method = (MethodDeclarationSyntax)declaration;
        var receiver = symbol.IsStatic ? symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) : "this";
        return MoveMemberTextEdits.BuildForwarder(method, receiver, true, symbol.ContainingType.Name, eol, arguments, "Use " + newSignature);
    }

    /// <summary>
    /// Proves the wrapper cannot recurse: in the rewritten declaration file, the call inside the annotated wrapper must bind to the annotated
    /// new member (not to the wrapper, another overload, or nothing/ambiguously). Returns an error to refuse with, or null when it binds correctly.
    /// </summary>
    internal static string? VerifyBinding(SemanticModel semanticModel, SyntaxNode root, SyntaxAnnotation wrapperAnnotation, SyntaxAnnotation memberAnnotation, string newSignature)
    {
        var wrapper = root.GetAnnotatedNodes(wrapperAnnotation).OfType<BaseMethodDeclarationSyntax>().SingleOrDefault();
        var member = root.GetAnnotatedNodes(memberAnnotation).Select(n => n.Parent).OfType<BaseMethodDeclarationSyntax>().SingleOrDefault();
        // A method wrapper forwards with an expression-bodied invocation; a constructor wrapper with its `: this(...)` initializer.
        SyntaxNode? call = wrapper switch
        {
            MethodDeclarationSyntax method => method.ExpressionBody?.Expression as InvocationExpressionSyntax,
            ConstructorDeclarationSyntax constructor => constructor.Initializer,
            _ => null,
        };
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
    internal static RewrittenCallSite DescribeMigratedCall(FilePathWrapper filePath, int line, SyntaxNode call, SeparatedSyntaxList<ArgumentSyntax> arguments, IReadOnlyList<(int SrcIndex, ExpressionSyntax? Literal)> plan)
    {
        static string ArgumentText(ArgumentSyntax a) => (a.RefKindKeyword.IsKind(SyntaxKind.None) ? string.Empty : a.RefKindKeyword.ValueText + " ") + a.Expression;

        // What precedes the argument list, and what follows it (a constructor call's object initializer).
        var (callee, tail) = call switch
        {
            InvocationExpressionSyntax invocation => (invocation.Expression.ToString(), string.Empty),
            ObjectCreationExpressionSyntax creation => ("new " + creation.Type, creation.Initializer == null ? string.Empty : " " + creation.Initializer),
            ImplicitObjectCreationExpressionSyntax implicitCreation => ("new", implicitCreation.Initializer == null ? string.Empty : " " + implicitCreation.Initializer),
            ConstructorInitializerSyntax initializer => (": " + initializer.ThisOrBaseKeyword.ValueText, string.Empty),
            PrimaryConstructorBaseTypeSyntax primaryBase => (primaryBase.Type.ToString(), string.Empty),
            _ => (call.ToString(), string.Empty),
        };
        var newArguments = plan.Select(p => p.Literal != null ? p.Literal.ToString() : ArgumentText(arguments[p.SrcIndex]));
        return new RewrittenCallSite(filePath.ToString(), line, OneLine(call.ToString()), OneLine(callee + "(" + string.Join(", ", newArguments) + ")" + tail));
    }

    /// <summary>A position inside the member's body (where a wrapper's call would sit), else the end of its parameter list: names bind there as they would in the wrapper.</summary>
    internal static int ScopePosition(BaseMethodDeclarationSyntax methodDecl) =>
        methodDecl.Body?.OpenBraceToken.Span.End ?? methodDecl.ExpressionBody?.Expression.SpanStart ?? methodDecl.ParameterList.Span.End;

    /// <summary>
    /// Every new parameter's defaultValue, parsed once: its slot and text, the expression, the type it must convert to (null when that type does not resolve),
    /// whether it names anything and so must bind at each call site (NeedsScopeCheck), and whether it is a compile-time constant a declaration may carry as a
    /// default (IsConstant; a non-constant one such as _logger makes the new parameter required). A new parameter without a defaultValue is not listed.
    /// </summary>
    // DTO-EXEMPT: internal tuple list local to ChangeSignature planning, never serialized.
    internal static List<(int Slot, string Name, string Text, ExpressionSyntax Expression, ITypeSymbol? ParameterType, bool NeedsScopeCheck, bool IsConstant)> AnalyzeNewDefaults(SemanticModel semanticModel, BaseMethodDeclarationSyntax methodDecl, IReadOnlyList<SignatureParameterSpec> parameters)
    {
        var scope = ScopePosition(methodDecl);
        var found = new List<(int Slot, string Name, string Text, ExpressionSyntax Expression, ITypeSymbol? ParameterType, bool NeedsScopeCheck, bool IsConstant)>();
        for (var slot = 0; slot < parameters.Count; slot++)
        {
            if (parameters[slot] is not NewParameterSpec added || string.IsNullOrWhiteSpace(added.DefaultValueExpression))
            {
                continue;
            }

            var text = added.DefaultValueExpression.Trim();
            var expression = SyntaxFactory.ParseExpression(text);
            var resolved = semanticModel.GetSpeculativeTypeInfo(methodDecl.ParameterList.SpanStart, SyntaxFactory.ParseTypeName(added.Type), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            var parameterType = resolved is { TypeKind: not (TypeKind.Error or TypeKind.TypeParameter) } ? resolved : null;
            var invalid = expression.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            var needsScopeCheck = invalid || expression.DescendantNodesAndSelf().Any(n => n is SimpleNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax);
            found.Add((slot, added.Name, text, expression, parameterType, needsScopeCheck, !invalid && IsConstantDefault(semanticModel, scope, expression)));
        }

        return found;
    }

    // Conservative: false only costs a required (instead of optional) new parameter, which every call site fills anyway.
    private static bool IsConstantDefault(SemanticModel model, int position, ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax:
            case DefaultExpressionSyntax:
                return true;
            case ParenthesizedExpressionSyntax parenthesized:
                return IsConstantDefault(model, position, parenthesized.Expression);
            case CastExpressionSyntax cast:
                return IsConstantDefault(model, position, cast.Expression);
            case PrefixUnaryExpressionSyntax unary:
                return unary.Kind() is SyntaxKind.UnaryPlusExpression or SyntaxKind.UnaryMinusExpression or SyntaxKind.BitwiseNotExpression or SyntaxKind.LogicalNotExpression
                    && IsConstantDefault(model, position, unary.Operand);
            case BinaryExpressionSyntax binary:
                return binary.Kind() is not (SyntaxKind.IsExpression or SyntaxKind.AsExpression or SyntaxKind.CoalesceExpression)
                    && IsConstantDefault(model, position, binary.Left) && IsConstantDefault(model, position, binary.Right);
            case ConditionalExpressionSyntax conditional:
                return IsConstantDefault(model, position, conditional.Condition) && IsConstantDefault(model, position, conditional.WhenTrue) && IsConstantDefault(model, position, conditional.WhenFalse);
            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                return model.GetSpeculativeSymbolInfo(position, expression, SpeculativeBindingOption.BindAsExpression).Symbol is IFieldSymbol { IsConst: true };
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } }:
                return true;
            case ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 0, Initializer: null }:
                return model.GetSpeculativeTypeInfo(position, expression, SpeculativeBindingOption.BindAsExpression).Type is { IsValueType: true };
            default:
                return false;
        }
    }

    /// <summary>
    /// Why <paramref name="expression"/> (a new parameter's defaultValue) does not bind at <paramref name="position"/> of <paramref name="model"/>, or null when it does:
    /// every name in it must resolve there (speculative binding), an instance member needs an instance context, and its type must convert implicitly to the parameter type.
    /// </summary>
    internal static string? DefaultBindingProblem(SemanticModel model, int position, ExpressionSyntax expression, ITypeSymbol? parameterType, bool instanceContext)
    {
        if (expression.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return "it is not a valid C# expression";
        }

        if (expression.DescendantNodesAndSelf().Any(n => n is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax))
        {
            return "a lambda or query cannot be verified per call site";
        }

        if (!instanceContext && expression.DescendantNodesAndSelf().Any(n => n is ThisExpressionSyntax or BaseExpressionSyntax))
        {
            return "it uses 'this' or 'base' and there is no instance in this scope";
        }

        foreach (var name in expression.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Where(IsRootName))
        {
            // The invoked name binds as the call (a method group alone has no single symbol).
            ExpressionSyntax target = name.Parent is InvocationExpressionSyntax invocation && invocation.Expression == name ? invocation : name;
            var symbol = model.GetSpeculativeSymbolInfo(position, target, SpeculativeBindingOption.BindAsExpression).Symbol
                ?? model.GetSpeculativeSymbolInfo(position, target, SpeculativeBindingOption.BindAsTypeOrNamespace).Symbol;
            if (symbol == null)
            {
                return $"no accessible '{name.Identifier.ValueText}' is in scope here";
            }

            var isInstanceMember = !symbol.IsStatic && (symbol is IFieldSymbol or IPropertySymbol or IEventSymbol || symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary });
            if (isInstanceMember && !instanceContext)
            {
                return $"'{name.Identifier.ValueText}' is an instance member and there is no instance in this scope (static context)";
            }
        }

        var type = model.GetSpeculativeTypeInfo(position, expression, SpeculativeBindingOption.BindAsExpression).Type;
        if (type == null || type.TypeKind == TypeKind.Error)
        {
            return "its type cannot be determined here";
        }

        if (parameterType != null && !model.ClassifyConversion(position, expression, parameterType).IsImplicit)
        {
            return $"its type '{type.ToDisplayString()}' does not convert implicitly to '{parameterType.ToDisplayString()}'";
        }

        return null;
    }

    // A name that starts a lookup (not the right side of a dot, a named-argument label or an initializer member): only those bind on their own.
    private static bool IsRootName(SimpleNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax access => access.Name != name,
        QualifiedNameSyntax qualified => qualified.Right != name,
        AliasQualifiedNameSyntax => false,
        MemberBindingExpressionSyntax => false,
        NameColonSyntax => false,
        NameEqualsSyntax => false,
        AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment => assignment.Left != name,
        _ => true,
    };

    /// <summary>
    /// Whether instance members (an implicit this) are reachable from <paramref name="site"/>: false in a static member or static lambda/local function, a field or
    /// property initializer, a constructor initializer or primary-constructor base call, an operator, or top-level statements. Conservative: uncertain is false.
    /// </summary>
    internal static bool IsInstanceContext(SyntaxNode site)
    {
        for (var node = site; node != null; node = node.Parent)
        {
            switch (node)
            {
                case ConstructorInitializerSyntax:
                case PrimaryConstructorBaseTypeSyntax:
                case AttributeSyntax:
                case GlobalStatementSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                case TypeDeclarationSyntax:
                    return false;
                case AnonymousFunctionExpressionSyntax lambda when lambda.Modifiers.Any(SyntaxKind.StaticKeyword):
                case LocalFunctionStatementSyntax localFunction when localFunction.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return false;
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax } } }:
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax or ParameterSyntax }:
                    return false;
                case MethodDeclarationSyntax method:
                    return !method.Modifiers.Any(SyntaxKind.StaticKeyword);
                case ConstructorDeclarationSyntax constructor:
                    return !constructor.Modifiers.Any(SyntaxKind.StaticKeyword);
                case PropertyDeclarationSyntax property:
                    return !property.Modifiers.Any(SyntaxKind.StaticKeyword);
                case EventDeclarationSyntax eventDeclaration:
                    return !eventDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword);
                case IndexerDeclarationSyntax:
                case DestructorDeclarationSyntax:
                    return true;
            }
        }

        return false;
    }

    private static string OneLine(string text)
    {
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= 160 ? collapsed : collapsed.Substring(0, 157) + "...";
    }
}

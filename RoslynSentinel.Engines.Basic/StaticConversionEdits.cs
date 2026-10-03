using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>One member to turn into a static member (one entry of ModifyModifier add static).</summary>
public sealed record StaticConversionRequest(int Index, FilePathWrapper FilePath, string TargetName, string? ContextSnippet, string? LineBefore, string? LineAfter);

/// <summary>
/// Outcome of a static conversion: the request indexes the conversion took over (any other index still needs the
/// plain modifier path), the new text of every touched file, and non-fatal notes for the caller.
/// </summary>
public sealed record StaticConversionResult(HashSet<int> HandledIndexes, Dictionary<FilePathWrapper, string> Changes, List<string> Notes);

/// <summary>One use of instance state inside a member that was asked to become static.</summary>
public sealed record InstanceStateUse(string Description, string FileName, int Line)
{
    public override string ToString() => $"{Description} at {FileName}:{Line}";
}

/// <summary>
/// Building blocks for turning an instance method/property that uses no instance state into a static member and
/// rewriting its instance-qualified callers (ModifyModifier add static, MoveMember into a static class).
/// Like <see cref="MoveMemberTextEdits"/>, every edit is a minimal TextChange against a document's ORIGINAL text, so
/// everything outside the rewritten receiver spans stays byte-identical.
/// </summary>
public static class StaticConversionEdits
{
    /// <summary>
    /// Why a declaration cannot become static at all (null when it can). Covers the kinds of member and the modifiers
    /// and relationships that make <c>static</c> invalid or would silently change behavior.
    /// </summary>
    public static string? GetIneligibilityReason(MemberDeclarationSyntax declaration, ISymbol symbol)
    {
        SyntaxTokenList modifiers;
        switch (declaration)
        {
            case MethodDeclarationSyntax method:
                modifiers = method.Modifiers;
                if (method.ExplicitInterfaceSpecifier != null)
                {
                    return "it is an explicit interface implementation, which cannot be static";
                }

                break;
            case PropertyDeclarationSyntax property:
                modifiers = property.Modifiers;
                if (property.ExplicitInterfaceSpecifier != null)
                {
                    return "it is an explicit interface implementation, which cannot be static";
                }

                var accessors = property.AccessorList?.Accessors ?? default;
                if (property.ExpressionBody == null && (accessors.Count == 0 || accessors.Any(a => a.Body == null && a.ExpressionBody == null)))
                {
                    return "it is an auto-property: its compiler-generated backing field is instance state";
                }

                if (accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)))
                {
                    return "an init accessor cannot be static";
                }

                break;
            default:
                return "only methods and properties can be converted to static (fields, events, indexers, operators and types are not supported)";
        }

        foreach (var token in modifiers)
        {
            if (token.IsKind(SyntaxKind.AbstractKeyword) || token.IsKind(SyntaxKind.VirtualKeyword) || token.IsKind(SyntaxKind.OverrideKeyword) || token.IsKind(SyntaxKind.ExternKeyword) || token.IsKind(SyntaxKind.PartialKeyword) || token.IsKind(SyntaxKind.ReadOnlyKeyword) || token.IsKind(SyntaxKind.SealedKeyword) || token.IsKind(SyntaxKind.RequiredKeyword))
            {
                return $"it is declared '{token.Text}', which cannot be combined with static";
            }
        }

        var containingType = symbol.ContainingType;
        if (containingType == null)
        {
            return "its containing type could not be resolved";
        }

        if (containingType.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return $"it is declared in {containingType.TypeKind.ToString().ToLowerInvariant()} '{containingType.Name}', whose members cannot be converted";
        }

        for (var type = containingType; type != null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                return $"its containing type '{type.Name}' is generic, and callers would need the type arguments spelled out";
            }
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, symbol))
                {
                    return $"it implements interface member '{iface.Name}.{interfaceMember.Name}', which requires an instance member";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every use of instance state inside <paramref name="declaration"/>: <c>this</c>/<c>base</c>, instance fields,
    /// properties, events and methods of the containing type (or its bases) reached through an implicit <c>this</c>,
    /// and captured primary-constructor parameters. Uses of members listed in <paramref name="convertedSymbols"/> are
    /// not instance state because those members become static in the same change.
    /// </summary>
    public static List<InstanceStateUse> FindInstanceStateUses(MemberDeclarationSyntax declaration, SemanticModel model, INamedTypeSymbol containingType, ISet<ISymbol> convertedSymbols, CancellationToken cancellationToken)
    {
        var uses = new List<InstanceStateUse>();
        var tree = declaration.SyntaxTree;
        foreach (var node in declaration.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
            {
                if (node is ThisExpressionSyntax && node.Parent is MemberAccessExpressionSyntax convertedAccess && convertedAccess.Expression == node && IsConverted(model.GetSymbolInfo(convertedAccess.Name, cancellationToken).Symbol, convertedSymbols))
                {
                    continue;
                }

                var text = node.Parent is MemberAccessExpressionSyntax parentAccess && parentAccess.Expression == node ? $"'{node}.{parentAccess.Name.Identifier.Text}'" : $"'{node}'";
                uses.Add(new InstanceStateUse(text, Path.GetFileName(tree.FilePath), LineOf(tree, node.Span)));
                continue;
            }

            if (node is not SimpleNameSyntax name || IsIgnorableName(name, model, cancellationToken))
            {
                continue;
            }

            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol == null || IsConverted(symbol, convertedSymbols))
            {
                continue;
            }

            string? label = symbol switch
            {
                IFieldSymbol { IsStatic: false, IsConst: false } => "instance field",
                IPropertySymbol { IsStatic: false } => "instance property",
                IEventSymbol { IsStatic: false } => "instance event",
                IMethodSymbol { IsStatic: false, MethodKind: MethodKind.Ordinary } => "instance method (implicit this)",
                _ => null
            };
            if (label != null && symbol.ContainingType != null && IsSameOrBaseOf(symbol.ContainingType, containingType))
            {
                uses.Add(new InstanceStateUse($"{label} '{symbol.Name}'", Path.GetFileName(tree.FilePath), LineOf(tree, name.Span)));
            }
            else if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } })
            {
                uses.Add(new InstanceStateUse($"captured primary-constructor parameter '{symbol.Name}'", Path.GetFileName(tree.FilePath), LineOf(tree, name.Span)));
            }
        }

        return uses;
    }

    /// <summary>
    /// The edit that adds <c>static</c> to a method or property declaration: inserted right after the accessibility
    /// keyword(s) (<c>public static ...</c>), or before the first modifier/type when there is no accessibility keyword.
    /// </summary>
    public static TextChange BuildStaticModifierEdit(MemberDeclarationSyntax declaration)
    {
        SyntaxTokenList modifiers;
        SyntaxNode typeNode;
        switch (declaration)
        {
            case MethodDeclarationSyntax method:
                modifiers = method.Modifiers;
                typeNode = method.ReturnType;
                break;
            case PropertyDeclarationSyntax property:
                modifiers = property.Modifiers;
                typeNode = property.Type;
                break;
            default:
                throw new ArgumentException("Only methods and properties can be converted to static.", nameof(declaration));
        }

        SyntaxToken lastAccessibility = default;
        foreach (var token in modifiers)
        {
            if (token.IsKind(SyntaxKind.PublicKeyword) || token.IsKind(SyntaxKind.PrivateKeyword) || token.IsKind(SyntaxKind.ProtectedKeyword) || token.IsKind(SyntaxKind.InternalKeyword))
            {
                lastAccessibility = token;
            }
        }

        if (lastAccessibility.RawKind != 0)
        {
            return new TextChange(new TextSpan(lastAccessibility.Span.End, 0), " static");
        }

        return modifiers.Count > 0 ? new TextChange(new TextSpan(modifiers[0].SpanStart, 0), "static ") : new TextChange(new TextSpan(typeNode.SpanStart, 0), "static ");
    }

    /// <summary>
    /// Drops every edit that lies entirely inside a larger replacement (for example the receiver <c>y.N()</c> that is
    /// being replaced wholesale also contains a reference to <c>N</c> that would be edited on its own) - the larger edit
    /// already decides that text.
    /// </summary>
    public static List<TextChange> DropNestedEdits(IEnumerable<TextChange> edits)
    {
        var kept = new List<TextChange>();
        foreach (var edit in edits.OrderBy(e => e.Span.Start).ThenByDescending(e => e.Span.Length))
        {
            if (kept.Any(k => k.Span.Length > 0 && k.Span.Start <= edit.Span.Start && edit.Span.End <= k.Span.End))
            {
                continue;
            }

            kept.Add(edit);
        }

        return kept;
    }

    internal static int LineOf(SyntaxTree tree, TextSpan span) => tree.GetLineSpan(span).StartLinePosition.Line + 1;

    internal static string Shorten(SyntaxNode node)
    {
        var text = string.Join(" ", node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 60 ? text.Substring(0, 57) + "..." : text;
    }

    private static bool IsConverted(ISymbol? symbol, ISet<ISymbol> convertedSymbols) => symbol != null && convertedSymbols.Contains(symbol.OriginalDefinition);

    private static bool IsSameOrBaseOf(INamedTypeSymbol owner, INamedTypeSymbol containingType)
    {
        for (var type = containingType; type != null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, owner.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIgnorableName(SimpleNameSyntax name, SemanticModel model, CancellationToken cancellationToken)
    {
        switch (name.Parent)
        {
            case MemberAccessExpressionSyntax access when access.Name == name:
            case MemberBindingExpressionSyntax:
            case NameColonSyntax:
            case NameEqualsSyntax:
                return true;
            case QualifiedNameSyntax qualified when qualified.Right == name:
                return true;
            case AssignmentExpressionSyntax assignment when assignment.Left == name && assignment.Parent is InitializerExpressionSyntax initializer && initializer.IsKind(SyntaxKind.ObjectInitializerExpression):
                return true;
        }

        for (var ancestor = name.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor is InvocationExpressionSyntax invocation && invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" } && model.GetConstantValue(invocation, cancellationToken).HasValue)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Collects the caller-rewrite edits for members that become static: each instance-qualified reference
/// (<c>receiver.M(...)</c>, <c>this.M(...)</c>, <c>receiver?.M(...)</c>) gets its receiver replaced by the type name,
/// as one minimal TextChange per site against the document's original text. It also gathers notes: receiver fields
/// that are no longer read, and receivers whose side effects no longer run.
/// </summary>
public sealed class StaticConversionReferenceCollector
{
    private readonly Solution _solution;
    private readonly Dictionary<DocumentId, List<TextSpan>> _replacedSpans = new();
    private readonly Dictionary<ISymbol, IFieldSymbol> _receiverFields = new(SymbolEqualityComparer.Default);

    public StaticConversionReferenceCollector(Solution solution)
    {
        _solution = solution;
    }

    /// <summary>Edits per document, with spans in each document's ORIGINAL text.</summary>
    public Dictionary<DocumentId, List<TextChange>> EditsByDocument { get; } = new();

    /// <summary>Sites that cannot be rewritten safely. A non-empty list means the whole change must be refused.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Non-fatal notes for the caller (unused receiver fields, dropped side effects, dropped null checks).</summary>
    public List<string> Notes { get; } = new();

    public void AddEdit(DocumentId documentId, TextChange edit)
    {
        if (!EditsByDocument.TryGetValue(documentId, out var edits))
        {
            edits = new List<TextChange>();
            EditsByDocument[documentId] = edits;
        }

        if (!edits.Any(e => e.Span == edit.Span))
        {
            edits.Add(edit);
        }
    }

    /// <summary>
    /// Adds the rewrite for every reference to <paramref name="symbol"/>. <paramref name="qualifierType"/> is the type
    /// that must now qualify the member; its name is spelled minimally for each call site. A bare (unqualified)
    /// reference needs no edit when the member only becomes static in place (<paramref name="skipAllBareReferences"/>),
    /// and when it moves, only bare references inside the moved members themselves are skipped
    /// (<paramref name="isInsideMovedMember"/>) because those members travel together.
    /// </summary>
    public async Task AddReferencesAsync(ISymbol symbol, INamedTypeSymbol qualifierType, bool skipAllBareReferences, Func<DocumentId, TextSpan, bool>? isInsideMovedMember, CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(symbol, _solution, cancellationToken);
        foreach (var location in references.SelectMany(r => r.Locations).Where(l => !l.IsCandidateLocation && l.Document.FilePath != null))
        {
            var document = location.Document;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
            {
                continue;
            }

            var span = location.Location.SourceSpan;
            var node = root.FindNode(span, findInsideTrivia: true, getInnermostNodeForTie: true);
            var name = node.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault(n => n.Identifier.Span == span);
            if (name == null)
            {
                continue;
            }

            var insideMoved = isInsideMovedMember?.Invoke(document.Id, span) ?? false;
            var qualifier = insideMoved ? qualifierType.Name : qualifierType.ToMinimalDisplayString(model, span.Start);
            var site = $"{Path.GetFileName(root.SyntaxTree.FilePath)}:{StaticConversionEdits.LineOf(root.SyntaxTree, span)}";
            if (name.Parent is MemberBindingExpressionSyntax binding)
            {
                AddConditionalAccessEdit(document, model, binding, name, qualifier, site);
                continue;
            }

            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name && HasDanglingConditionalBinding(access.Expression))
            {
                Problems.Add($"{site}: '{StaticConversionEdits.Shorten(access)}' is reached through a null-conditional chain whose receiver is not a simple field, local or parameter; rewrite that call by hand or assign the receiver to a local first");
                continue;
            }

            var edit = MoveMemberTextEdits.BuildReferenceEdit(root, span, qualifier);
            if (edit == null || (edit.Value.Span.Length == 0 && (skipAllBareReferences || insideMoved)))
            {
                continue;
            }

            AddEdit(document.Id, edit.Value);
            if (edit.Value.Span.Length > 0 && name.Parent is MemberAccessExpressionSyntax receiverAccess && receiverAccess.Name == name)
            {
                TrackReplacedReceiver(document.Id, model, receiverAccess.Expression, edit.Value.Span, site);
            }
        }
    }

    /// <summary>
    /// Call once after every reference was added: removes edits nested inside larger replacements, then reports each
    /// receiver field that no code reads any more (assignments alone, such as the constructor's, do not count as use).
    /// </summary>
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        foreach (var documentId in EditsByDocument.Keys.ToList())
        {
            EditsByDocument[documentId] = StaticConversionEdits.DropNestedEdits(EditsByDocument[documentId]);
        }

        foreach (var field in _receiverFields.Values)
        {
            var reads = 0;
            var writes = 0;
            var references = await SymbolFinder.FindReferencesAsync(field, _solution, cancellationToken);
            foreach (var location in references.SelectMany(r => r.Locations).Where(l => !l.IsCandidateLocation))
            {
                var span = location.Location.SourceSpan;
                if (_replacedSpans.TryGetValue(location.Document.Id, out var replaced) && replaced.Any(s => s.Contains(span)))
                {
                    continue;
                }

                var root = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var name = root?.FindNode(span, findInsideTrivia: true, getInnermostNodeForTie: true).AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault(n => n.Identifier.Span == span);
                SyntaxNode? use = name;
                if (name?.Parent is MemberAccessExpressionSyntax qualified && qualified.Name == name)
                {
                    use = qualified;
                }

                if (use?.Parent is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Left == use)
                {
                    writes++;
                }
                else
                {
                    reads++;
                }
            }

            if (reads == 0)
            {
                var declaration = field.Locations.FirstOrDefault(l => l.IsInSource);
                var where = declaration?.SourceTree == null ? string.Empty : $" ({Path.GetFileName(declaration.SourceTree.FilePath)}:{declaration.GetLineSpan().StartLinePosition.Line + 1})";
                Notes.Add($"Field '{field.ContainingType.Name}.{field.Name}'{where} is no longer read anywhere after this change ({writes} assignment(s) remain). It was NOT removed - delete it, and whatever fills it, if nothing else needs it.");
            }
        }
    }

    private void AddConditionalAccessEdit(Document document, SemanticModel model, MemberBindingExpressionSyntax binding, SimpleNameSyntax name, string qualifier, string site)
    {
        var conditional = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(c => c.WhenNotNull.SpanStart == binding.SpanStart);
        if (conditional == null)
        {
            Problems.Add($"{site}: '{name.Identifier.Text}' is used inside a null-conditional chain whose shape cannot be rewritten to a static call; rewrite that call by hand");
            return;
        }

        var receiver = conditional.Expression;
        var receiverSymbol = model.GetSymbolInfo(receiver).Symbol;
        var isSimpleReceiver = receiverSymbol is IFieldSymbol or ILocalSymbol or IParameterSymbol && (receiver is IdentifierNameSyntax || receiver is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax });
        if (!isSimpleReceiver)
        {
            Problems.Add($"{site}: the null-conditional receiver in '{StaticConversionEdits.Shorten(conditional)}' is not a simple field, local or parameter, so '?.' cannot be rewritten to a static call safely; assign the receiver to a local first or rewrite that call by hand");
            return;
        }

        var replaced = TextSpan.FromBounds(receiver.SpanStart, conditional.OperatorToken.Span.End);
        AddEdit(document.Id, new TextChange(replaced, qualifier));
        TrackReplacedReceiver(document.Id, model, receiver, replaced, site);
        Notes.Add($"{site}: null-conditional call '{StaticConversionEdits.Shorten(receiver)}?.{name.Identifier.Text}' became a plain static call; the null check on the receiver is gone, and a value-type result is no longer wrapped in Nullable.");
    }

    private void TrackReplacedReceiver(DocumentId documentId, SemanticModel model, ExpressionSyntax receiver, TextSpan replacedSpan, string site)
    {
        if (!_replacedSpans.TryGetValue(documentId, out var spans))
        {
            spans = new List<TextSpan>();
            _replacedSpans[documentId] = spans;
        }

        spans.Add(replacedSpan);
        if (HasSideEffects(receiver))
        {
            Notes.Add($"{site}: the receiver expression '{StaticConversionEdits.Shorten(receiver)}' was replaced by the type name; the call, creation or assignment inside it no longer runs.");
        }

        if (model.GetSymbolInfo(receiver).Symbol is IFieldSymbol { IsConst: false } field)
        {
            _receiverFields[field.OriginalDefinition] = field.OriginalDefinition;
        }
    }

    private static bool HasSideEffects(SyntaxNode receiver)
    {
        return receiver.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or AssignmentExpressionSyntax or AwaitExpressionSyntax || (n is PrefixUnaryExpressionSyntax prefix && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))) || (n is PostfixUnaryExpressionSyntax postfix && (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression))));
    }

    /// <summary>
    /// True when the receiver contains a <c>?.</c> binding whose conditional access starts outside the receiver, i.e.
    /// replacing the receiver would cut a null-conditional chain in half.
    /// </summary>
    private static bool HasDanglingConditionalBinding(SyntaxNode receiver)
    {
        foreach (var binding in receiver.DescendantNodesAndSelf().Where(n => n is MemberBindingExpressionSyntax or ElementBindingExpressionSyntax))
        {
            var owner = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(c => c.WhenNotNull.Span.Contains(binding.Span));
            if (owner == null || !receiver.Span.Contains(owner.Span))
            {
                return true;
            }
        }

        return false;
    }
}

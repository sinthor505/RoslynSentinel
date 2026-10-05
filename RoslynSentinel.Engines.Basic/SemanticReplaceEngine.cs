using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Information about a symbol's declaration, including identifier and initializer spans.</summary>
public sealed record DeclarationInfo(string FilePath, TextSpan IdentifierSpan, TextSpan? InitializerValueSpan, string? InitializerText);

/// <summary>Engine for semantic find-replace operations on symbols.</summary>
public class SemanticReplaceEngine
{
    private readonly IWorkspaceReader _workspaceManager;

    /// <summary>Initializes a new instance of the SemanticReplaceEngine.</summary>
    public SemanticReplaceEngine(IWorkspaceReader workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>Resolves a docCommentId to a bool property or field symbol.</summary>
    /// <returns>A tuple of (ISymbol, ResultError); exactly one is non-null. Error cases: NotFound if the id does not resolve, InvalidArgument if the symbol is not a bool property/field, TargetIneligible if it is virtual/override/abstract or has a custom accessor.</returns>
    public Task<(ISymbol? Symbol, ResultError? Error)> ResolveBoolMemberAsync(string docCommentId, CancellationToken ct = default)
        => ResolveBoolMemberAsync(docCommentId, null, ct);

    /// <summary>
    /// Like <see cref="ResolveBoolMemberAsync(string, CancellationToken)"/>, but newName lets a computed inverse alias through:
    /// a bool property whose getter is exactly <c>!X</c> (and whose setter/init, if any, is <c>X = !value</c>) is accepted when newName
    /// is an existing sibling that holds the same state (X itself, or a property that passes straight through to X). That is the
    /// "alias retarget" form: callers are migrated to the sibling and the alias declaration is left untouched.
    /// </summary>
    /// <returns>A tuple of (ISymbol, ResultError); exactly one is non-null. A computed inverse alias with any other newName is refused with the names that would work.</returns>
    public async Task<(ISymbol? Symbol, ResultError? Error)> ResolveBoolMemberAsync(string docCommentId, string? newName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(docCommentId))
        {
            return (null, new ResultError(ToolErrorCode.InvalidArgument, "docCommentId cannot be empty."));
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, ct);
        ISymbol? symbol = null;

        // Try to resolve the symbol across all projects
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(docCommentId, compilation);
            if (symbol is not null)
            {
                break;
            }
        }

        if (symbol is null)
        {
            return (null, new ResultError(ToolErrorCode.NotFound, $"No symbol found for docCommentId: {docCommentId}"));
        }

        // Check if symbol is a property or field
        if (symbol is not IPropertySymbol and not IFieldSymbol)
        {
            var kindName = symbol.Kind switch
            {
                SymbolKind.Method => "method",
                SymbolKind.NamedType => "type",
                SymbolKind.Namespace => "namespace",
                _ => symbol.Kind.ToString().ToLowerInvariant()
            };
            return (null, new ResultError(ToolErrorCode.InvalidArgument, $"Symbol is a {kindName}, not a property or field."));
        }

        // Check if it is a bool property or field
        var type = symbol switch
        {
            IPropertySymbol prop => prop.Type,
            IFieldSymbol field => field.Type,
            _ => null
        };

        if (type is null || type.SpecialType != SpecialType.System_Boolean)
        {
            var typeName = type?.Name ?? "unknown";
            return (null, new ResultError(ToolErrorCode.InvalidArgument, $"Symbol is not a bool type; it is {typeName}."));
        }

        // Check for virtual/override/abstract
        if (symbol is IPropertySymbol propSymbol)
        {
            if (propSymbol.IsVirtual || propSymbol.IsOverride || propSymbol.IsAbstract)
            {
                return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property is virtual, override, or abstract; coordinated edits would be needed."));
            }

            // A computed inverse alias (get => !X; set => X = !value) is only eligible as an alias retarget: newName must be the sibling.
            var aliasTargets = GetInverseAliasTargets(propSymbol);
            if (aliasTargets is not null)
            {
                if (newName is not null && aliasTargets.Contains(newName))
                {
                    return (symbol, null);
                }

                var names = string.Join(" or ", aliasTargets.Select(n => $"'{n}'"));
                return (null, new ResultError(
                    ToolErrorCode.TargetIneligible,
                    $"Property '{propSymbol.Name}' is a computed inverse alias of an existing member; renaming it in place is not meaningful. " +
                    $"To migrate its callers instead (alias retarget), pass newName = {names}; the alias declaration itself is left untouched."));
            }

            // Check for expression-bodied properties (public bool X => ...)
            if (propSymbol.DeclaringSyntaxReferences.Length > 0)
            {
                var propSyntax = await propSymbol.DeclaringSyntaxReferences[0].GetSyntaxAsync(ct);
                if (propSyntax is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax propDecl && propDecl.ExpressionBody != null)
                {
                    return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property is expression-bodied; inverting a computed property is not meaningful. " + AliasRetargetHint));
                }
            }

            // Check for custom accessor bodies (includes expression-bodied accessors)
            if (propSymbol.GetMethod != null && propSymbol.GetMethod.DeclaringSyntaxReferences.Length > 0)
            {
                var getSyntax = await propSymbol.GetMethod.DeclaringSyntaxReferences[0].GetSyntaxAsync(ct);
                if (getSyntax is Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax accessor &&
                    (accessor.Body != null || accessor.ExpressionBody != null))
                {
                    return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property has a custom getter body; inverting a computed property is not meaningful. " + AliasRetargetHint));
                }
            }

            if (propSymbol.SetMethod != null && propSymbol.SetMethod.DeclaringSyntaxReferences.Length > 0)
            {
                var setSyntax = await propSymbol.SetMethod.DeclaringSyntaxReferences[0].GetSyntaxAsync(ct);
                if (setSyntax is Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax accessor &&
                    (accessor.Body != null || accessor.ExpressionBody != null))
                {
                    return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property has a custom setter body; inverting a computed property is not meaningful. " + AliasRetargetHint));
                }
            }
        }

        return (symbol, null);
    }

    private const string AliasRetargetHint =
        "If it is a computed inverse alias (get => !X; set => X = !value), pass newName = the existing sibling member X to migrate its callers (alias retarget).";

    /// <summary>True when property is an exact inverse alias and newName is one of the sibling names an alias retarget accepts.</summary>
    private static bool IsAliasRetarget(IPropertySymbol property, string newName)
    {
        return GetInverseAliasTargets(property)?.Contains(newName) == true;
    }

    /// <summary>
    /// When property is an exact inverse alias - getter <c>!X</c> (expression body or a single return) and setter/init, if present,
    /// <c>X = !value</c>, with X a bool field/property of the same type - returns the names an alias retarget accepts: X itself, then any
    /// sibling property that passes straight through to X (getter <c>X</c>, setter <c>X = value</c>), because callers should be migrated to the
    /// public member rather than to a private backing field. Returns null for anything else.
    /// </summary>
    private static IReadOnlyList<string>? GetInverseAliasTargets(IPropertySymbol property)
    {
        if (property.IsIndexer
            || property.ContainingType is null
            || property.DeclaringSyntaxReferences.Length != 1
            || property.DeclaringSyntaxReferences[0].GetSyntax() is not PropertyDeclarationSyntax declaration)
        {
            return null;
        }

        if (GetAccessorExpression(declaration, SyntaxKind.GetAccessorDeclaration) is not PrefixUnaryExpressionSyntax negation
            || !negation.IsKind(SyntaxKind.LogicalNotExpression))
        {
            return null;
        }

        var negated = FindBoolSibling(property, SiblingName(negation.Operand));
        if (negated is null || !SetterAssigns(declaration, negated.Name, negated: true))
        {
            return null;
        }

        var targets = new List<string> { negated.Name };
        foreach (var candidate in property.ContainingType.GetMembers().OfType<IPropertySymbol>())
        {
            if (!SymbolEqualityComparer.Default.Equals(candidate, property)
                && !SymbolEqualityComparer.Default.Equals(candidate, negated)
                && IsPassThrough(candidate, negated.Name))
            {
                targets.Add(candidate.Name);
            }
        }

        return targets;
    }

    /// <summary>True when candidate is a bool property whose getter is exactly <c>target</c> and whose setter/init, if present, is <c>target = value</c>.</summary>
    private static bool IsPassThrough(IPropertySymbol candidate, string targetName)
    {
        if (candidate.IsIndexer
            || candidate.Type.SpecialType != SpecialType.System_Boolean
            || candidate.DeclaringSyntaxReferences.Length != 1
            || candidate.DeclaringSyntaxReferences[0].GetSyntax() is not PropertyDeclarationSyntax declaration)
        {
            return false;
        }

        var getter = GetAccessorExpression(declaration, SyntaxKind.GetAccessorDeclaration);
        return getter is not null && SiblingName(getter) == targetName && SetterAssigns(declaration, targetName, negated: false);
    }

    /// <summary>True when the property has no setter/init, or its setter/init is <c>target = value</c> (or <c>target = !value</c> when negated).</summary>
    private static bool SetterAssigns(PropertyDeclarationSyntax declaration, string targetName, bool negated)
    {
        var hasSetter = declaration.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;
        if (!hasSetter)
        {
            return true;
        }

        if (GetAccessorExpression(declaration, SyntaxKind.SetAccessorDeclaration, SyntaxKind.InitAccessorDeclaration) is not AssignmentExpressionSyntax assignment
            || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            || SiblingName(assignment.Left) != targetName)
        {
            return false;
        }

        var right = assignment.Right;
        if (negated)
        {
            if (right is not PrefixUnaryExpressionSyntax inverted || !inverted.IsKind(SyntaxKind.LogicalNotExpression))
            {
                return false;
            }

            right = inverted.Operand;
        }

        return right is IdentifierNameSyntax { Identifier.ValueText: "value" };
    }

    /// <summary>The expression of an accessor (or of an expression-bodied property, for a getter): an expression body, or the sole statement of a block body.</summary>
    private static ExpressionSyntax? GetAccessorExpression(PropertyDeclarationSyntax declaration, params SyntaxKind[] accessorKinds)
    {
        if (declaration.ExpressionBody is { } propertyBody && accessorKinds.Contains(SyntaxKind.GetAccessorDeclaration))
        {
            return propertyBody.Expression;
        }

        var accessor = declaration.AccessorList?.Accessors.FirstOrDefault(a => accessorKinds.Contains(a.Kind()));
        if (accessor is null)
        {
            return null;
        }

        if (accessor.ExpressionBody is { } body)
        {
            return body.Expression;
        }

        if (accessor.Body is { Statements: { Count: 1 } statements })
        {
            return statements[0] switch
            {
                ReturnStatementSyntax returned => returned.Expression,
                ExpressionStatementSyntax statement => statement.Expression,
                _ => null
            };
        }

        return null;
    }

    /// <summary>The member name for <c>X</c> or <c>this.X</c>, else null.</summary>
    private static string? SiblingName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
            _ => null
        };
    }

    /// <summary>Finds a bool field or (non-indexer) property named name in the property's containing type, other than the property itself.</summary>
    private static ISymbol? FindBoolSibling(IPropertySymbol property, string? name)
    {
        if (name is null || name == property.Name)
        {
            return null;
        }

        foreach (var member in property.ContainingType.GetMembers(name))
        {
            if (property.IsStatic && !member.IsStatic)
            {
                continue;
            }

            switch (member)
            {
                case IFieldSymbol { Type.SpecialType: SpecialType.System_Boolean }:
                case IPropertySymbol { IsIndexer: false, Type.SpecialType: SpecialType.System_Boolean }:
                    return member;
            }
        }

        return null;
    }

    /// <summary>Describes a symbol's declaration, including identifier and initializer spans.</summary>
    /// <returns>DeclarationInfo if symbol has a single source declaration, null otherwise.</returns>
    public static DeclarationInfo? DescribeDeclaration(ISymbol symbol, CancellationToken cancellationToken = default)
    {
        if (symbol == null)
        {
            return null;
        }

        // Handle property symbols
        if (symbol is IPropertySymbol propSymbol)
        {
            if (propSymbol.DeclaringSyntaxReferences.Length != 1)
            {
                return null;
            }

            var syntaxRef = propSymbol.DeclaringSyntaxReferences[0];
            var syntax = syntaxRef.GetSyntax(cancellationToken);

            if (syntax is PropertyDeclarationSyntax propDecl)
            {
                var filePath = syntaxRef.SyntaxTree?.FilePath ?? string.Empty;
                var identifierSpan = propDecl.Identifier.Span;

                // Check for initializer (e.g., { get; set; } = true;)
                TextSpan? initializerValueSpan = null;
                string? initializerText = null;

                if (propDecl.Initializer?.Value != null)
                {
                    initializerValueSpan = propDecl.Initializer.Value.Span;
                    initializerText = propDecl.Initializer.Value.ToString();
                }

                return new DeclarationInfo(filePath, identifierSpan, initializerValueSpan, initializerText);
            }

            // A record positional parameter (record R(bool Success = true)) declares the property; its default value plays the initializer.
            if (syntax is ParameterSyntax { Parent: ParameterListSyntax { Parent: RecordDeclarationSyntax } } recordParameter)
            {
                var defaultValue = recordParameter.Default?.Value;
                return new DeclarationInfo(syntaxRef.SyntaxTree?.FilePath ?? string.Empty, recordParameter.Identifier.Span, defaultValue?.Span, defaultValue?.ToString());
            }
        }

        // Handle field symbols
        if (symbol is IFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.DeclaringSyntaxReferences.Length != 1)
            {
                return null;
            }

            var syntaxRef = fieldSymbol.DeclaringSyntaxReferences[0];
            var syntax = syntaxRef.GetSyntax(cancellationToken);

            if (syntax is VariableDeclaratorSyntax varDecl)
            {
                var filePath = syntaxRef.SyntaxTree?.FilePath ?? string.Empty;
                var identifierSpan = varDecl.Identifier.Span;

                // Check for initializer (e.g., = true;)
                TextSpan? initializerValueSpan = null;
                string? initializerText = null;

                if (varDecl.Initializer?.Value != null)
                {
                    initializerValueSpan = varDecl.Initializer.Value.Span;
                    initializerText = varDecl.Initializer.Value.ToString();
                }

                return new DeclarationInfo(filePath, identifierSpan, initializerValueSpan, initializerText);
            }
        }

        return null;
    }

    /// <summary>Validates that newName is a valid C# identifier and not already in use.</summary>
    /// <returns>null if valid; ResultError with InvalidArgument code if invalid.</returns>
    public static ResultError? ValidateNewName(ISymbol symbol, string newName)
    {
        // Check if newName is null, empty, or whitespace
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new ResultError(ToolErrorCode.InvalidArgument, "newName must be a valid C# identifier. Example: 'IsError'.");
        }

        // Check if it's a valid identifier
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            return new ResultError(ToolErrorCode.InvalidArgument, "newName must be a valid C# identifier. Example: 'IsError'.");
        }

        // Check if it's a keyword
        if (SyntaxFacts.GetKeywordKind(newName) != SyntaxKind.None)
        {
            return new ResultError(ToolErrorCode.InvalidArgument, "newName must be a valid C# identifier. Example: 'IsError'.");
        }

        // Check if it's identical to the current name
        if (newName == symbol.Name)
        {
            return new ResultError(ToolErrorCode.InvalidArgument, $"newName '{newName}' is identical to the current name; nothing to do.");
        }

        // Check if it collides with another member in the containing type
        if (symbol.ContainingType != null)
        {
            var collidingMembers = symbol.ContainingType.GetMembers(newName);
            // The one sanctioned collision: an inverse alias retargeted at the sibling that holds its state.
            var isAliasRetarget = symbol is IPropertySymbol aliasProperty && IsAliasRetarget(aliasProperty, newName);
            if (collidingMembers.Length > 0 && !isAliasRetarget)
            {
                var collidingMember = collidingMembers[0];
                return new ResultError(ToolErrorCode.InvalidArgument, 
                    $"newName '{newName}' collides with existing member '{collidingMember.Name}' in type '{symbol.ContainingType.Name}'.");
            }
        }

        return null;
    }

    /// <summary>
    /// Finds every reference to a bool property/field, classifies each by role, and returns the sites plus the text edits that
    /// rename the symbol and flip its polarity. Nothing is applied here: the caller applies Edits per file.
    /// </summary>
    /// <remarks>
    /// Edits are returned in application order (see <see cref="ReferenceEdit.ApplicationOrder"/>): per file, start descending, then end
    /// descending. Tie-break: a non-empty edit that starts at the same offset as a zero-length insertion is applied first, so the
    /// insertion lands in front of its text; an insertion at the END of another edit has the larger start and lands after that
    /// edit's text. This is what lets "!(" and ")" wrap a right-hand side whose inner references are edited too.
    /// Errors (all with empty Edits): ValidateNewName's error; TargetIneligible when the declaration is not a plain property/field
    /// declaration, when any site is Unsupported (message lists every file:line - reason), or when two edits conflict.
    /// </remarks>
    /// <returns>Sites ordered by file then line (Unsupported sites included, with UnsupportedReason), the edits, and an error or null.</returns>
    public async Task<(List<SemanticReplaceSite> Sites, List<ReferenceEdit> Edits, ResultError? Error)> CollectSitesAsync(ISymbol symbol, string newName, CancellationToken cancellationToken = default)
    {
        var nameError = ValidateNewName(symbol, newName);
        if (nameError is not null)
        {
            return (new List<SemanticReplaceSite>(), new List<ReferenceEdit>(), nameError);
        }

        var declaration = DescribeDeclaration(symbol, cancellationToken);
        if (declaration is null)
        {
            var notPlain = new ResultError(
                ToolErrorCode.TargetIneligible,
                $"'{symbol.Name}' is not declared by exactly one plain property, field or record positional parameter declaration (for example a partial member); pick a different symbol.");
            return (new List<SemanticReplaceSite>(), new List<ReferenceEdit>(), notPlain);
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var collection = new SiteCollection();

        // Alias retarget: a computed inverse alias (get => !X) whose newName is the sibling X. Its references are migrated to X, but the
        // alias declaration is left untouched (a later step deletes it), so the declaration site and anything inside its accessors are skipped.
        var aliasRetarget = symbol is IPropertySymbol aliasProperty && IsAliasRetarget(aliasProperty, newName);
        var aliasDeclaration = symbol.DeclaringSyntaxReferences[0];

        // The declaration: rename the identifier, and flip the initializer if there is one.
        if (!aliasRetarget)
        {
            var declarationTree = aliasDeclaration.SyntaxTree;
            var declarationText = await declarationTree.GetTextAsync(cancellationToken);
            var declarationDocument = solution.GetDocument(declarationTree);
            var declarationPath = declarationDocument is null ? declaration.FilePath : declarationDocument.FilePath ?? declarationDocument.Name;
            var declarationLine = declarationText.Lines.GetLinePosition(declaration.IdentifierSpan.Start).Line + 1;
            var declarationSpan = declaration.InitializerValueSpan is { } initializerSpan
                ? TextSpan.FromBounds(declaration.IdentifierSpan.Start, initializerSpan.End)
                : declaration.IdentifierSpan;
            var declarationSite = collection.AddSite(declarationPath, declarationLine, declarationSpan, SemanticReplaceRole.DeclarationInitializer, declarationText);
            collection.AddEdit(declarationSite, declarationPath, declaration.IdentifierSpan, newName, declarationLine);
            if (declaration.InitializerValueSpan is { } valueSpan)
            {
                collection.Writes.Add(new PendingWrite(declarationSite, valueSpan, declarationText.ToString(valueSpan), declarationLine));
            }
        }

        var parsedDocuments = new Dictionary<DocumentId, (SyntaxNode Root, SourceText Text)>();
        var seen = new HashSet<(string FilePath, int Start)>();

        // A record positional property is also a primary constructor parameter. Constructor calls bind their arguments to the
        // parameter (named or positional), initializers inside the record read the parameter, and deconstruction reads it by position:
        // all three are invisible to the property's own references, so they are collected here.
        var targets = new List<ISymbol> { symbol };
        if (symbol is IPropertySymbol recordProperty && aliasDeclaration.GetSyntax(cancellationToken) is ParameterSyntax)
        {
            var recordParameter = GetRecordPrimaryParameter(recordProperty, cancellationToken);
            if (recordParameter is null)
            {
                var noConstructor = new ResultError(
                    ToolErrorCode.TargetIneligible,
                    $"'{symbol.Name}' is a record positional parameter but its primary constructor could not be resolved; nothing was changed.");
                return (new List<SemanticReplaceSite>(), new List<ReferenceEdit>(), noConstructor);
            }

            await CollectRecordPositionalSitesAsync(collection, seen, parsedDocuments, solution, recordParameter, aliasDeclaration.SyntaxTree, newName, cancellationToken);
            targets.Add(recordParameter);
        }

        // The references. Only the ReferencedSymbol for each target itself is used: FindReferences also cascades to
        // interface/override relatives, and rewriting those is out of scope (the compile gate catches the fallout).
        var referencedSymbols = new List<(ISymbol Target, ReferencedSymbol Referenced)>();
        foreach (var target in targets)
        {
            foreach (var found in await SymbolFinder.FindReferencesAsync(target, solution, cancellationToken))
            {
                referencedSymbols.Add((target, found));
            }
        }

        foreach (var (target, referenced) in referencedSymbols)
        {
            var targetId = target.GetDocumentationCommentId();
            var definition = referenced.Definition;
            var isTarget = SymbolEqualityComparer.Default.Equals(definition, target)
                || (targetId is not null && definition.GetDocumentationCommentId() == targetId);
            if (!isTarget)
            {
                continue;
            }

            foreach (var location in referenced.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (location.IsImplicit || location.IsCandidateLocation || !location.Location.IsInSource)
                {
                    continue;
                }

                if (aliasRetarget
                    && location.Location.SourceTree == aliasDeclaration.SyntaxTree
                    && aliasDeclaration.Span.Contains(location.Location.SourceSpan))
                {
                    continue; // inside the alias's own declaration: it stays as written
                }

                var document = location.Document;
                if (await GetParsedAsync(parsedDocuments, document, cancellationToken) is not { } parsed)
                {
                    continue;
                }

                var filePath = document.FilePath ?? document.Name;
                var span = location.Location.SourceSpan;
                if (!seen.Add((filePath, span.Start)))
                {
                    continue; // same file reached through a second project (multi-targeting)
                }

                var node = parsed.Root.FindNode(span, findInsideTrivia: true, getInnermostNodeForTie: true);
                var identifier = node as IdentifierNameSyntax
                    ?? node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault(i => i.Span == span);
                if (identifier is null)
                {
                    var line = parsed.Text.Lines.GetLinePosition(span.Start).Line + 1;
                    collection.AddSite(filePath, line, span, SemanticReplaceRole.Unsupported, parsed.Text, "reference is not a simple identifier");
                    continue;
                }

                if (identifier.Parent is NameColonSyntax { Parent: ArgumentSyntax })
                {
                    // Named arguments of primary constructor calls were handled above; any other one is a shape this pass cannot rewrite.
                    var namedLine = parsed.Text.Lines.GetLinePosition(span.Start).Line + 1;
                    collection.AddSite(filePath, namedLine, span, SemanticReplaceRole.Unsupported, parsed.Text, UnattributedNamedArgumentReason(identifier, symbol));
                    continue;
                }

                CollectReference(collection, filePath, parsed.Text, identifier, newName);
            }
        }

        ComposeWrites(collection, newName);

        var sites = collection.Sites
            .OrderBy(s => s.FilePath, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Span.Start)
            .ToList();
        var siteRecords = sites.Select(s => ToSite(s, collection.Edits)).ToList();

        // Check accessibility of newName when retargeting to an alias: if newName is a private member,
        // it must not be referenced from outside its containing type (would cause CS0122).
        if (symbol is IPropertySymbol propSymbol && IsAliasRetarget(propSymbol, newName))
        {
            var containingType = propSymbol.ContainingType!;
            var newNameSymbol = containingType.GetMembers(newName).FirstOrDefault();
            if (newNameSymbol is not null && newNameSymbol.DeclaredAccessibility == Accessibility.Private)
            {
                // Use SemanticModel.IsAccessible to check if newNameSymbol is accessible from each site location.
                PendingSite? inaccessibleSite = null;
                foreach (var site in sites)
                {
                    if (site.UnsupportedReason is not null)
                    {
                        continue; // skip unsupported sites
                    }

                    // Find the document for this site
                    var doc = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.FilePath == site.FilePath || d.Name == site.FilePath);
                    if (doc is null)
                    {
                        inaccessibleSite = site; // conservative: if we can't find the document, treat as inaccessible
                        break;
                    }

                    var semanticModel = await doc.GetSemanticModelAsync(cancellationToken);
                    if (semanticModel is null)
                    {
                        inaccessibleSite = site; // conservative: if we can't get the semantic model, treat as inaccessible
                        break;
                    }

                    // Check accessibility at the site's position
                    if (!semanticModel.IsAccessible(site.Span.Start, newNameSymbol))
                    {
                        inaccessibleSite = site;
                        break;
                    }
                }

                if (inaccessibleSite is not null)
                {
                    var refusal = new ResultError(
                        ToolErrorCode.TargetIneligible,
                        $"Cannot retarget to '{newName}': it is not accessible from {inaccessibleSite.FilePath}:{inaccessibleSite.Line}. Choose a public or internal sibling property as newName instead.");
                    return (siteRecords, new List<ReferenceEdit>(), refusal);
                }
            }
        }

        var unsupported = sites.Where(s => s.UnsupportedReason is not null).ToList();
        if (unsupported.Count > 0)
        {
            var lines = string.Join("\n", unsupported.Select(s => $"  {s.FilePath}:{s.Line} - {s.UnsupportedReason}"));
            var refusal = new ResultError(
                ToolErrorCode.TargetIneligible,
                $"{unsupported.Count} unsupported site(s) refuse the whole operation; nothing was changed. Fix these by hand, then re-run:\n{lines}");
            return (siteRecords, new List<ReferenceEdit>(), refusal);
        }

        var conflict = FindConflict(collection.Edits);
        if (conflict is not null)
        {
            var overlap = new ResultError(
                ToolErrorCode.TargetIneligible,
                $"Conflicting edits at {conflict.Edit.FilePath}:{conflict.Line}: a reference nested inside another reference's expression cannot be rewritten in one pass. Edit that site by hand, then re-run.");
            return (siteRecords, new List<ReferenceEdit>(), overlap);
        }

        var edits = collection.Edits.Select(e => e.Edit).OrderBy(e => e, Comparer<ReferenceEdit>.Create(ReferenceEdit.ApplicationOrder)).ToList();
        return (siteRecords, edits, null);
    }

    /// <summary>An edit plus the 1-based line it belongs to (used only for error messages).</summary>
    private sealed class EditEntry
    {
        public EditEntry(ReferenceEdit edit, int line)
        {
            Edit = edit;
            Line = line;
        }

        public ReferenceEdit Edit
        {
            get;
        }

        public int Line
        {
            get;
        }
    }

    /// <summary>A site under construction: its original span/text and the edits it owns (After is computed once all edits exist).</summary>
    private sealed class PendingSite
    {
        public PendingSite(string filePath, int line, TextSpan span, SemanticReplaceRole role, string before)
        {
            FilePath = filePath;
            Line = line;
            Span = span;
            Role = role;
            Before = before;
        }

        public string FilePath
        {
            get;
        }

        public int Line
        {
            get;
        }

        public TextSpan Span
        {
            get;
        }

        public SemanticReplaceRole Role
        {
            get;
        }

        public string Before
        {
            get;
        }

        public string? UnsupportedReason
        {
            get; set;
        }

        public List<EditEntry> Own { get; } = new();
    }

    /// <summary>A write whose right-hand side still has to be flipped (replace or wrap, decided once every inner edit is known).</summary>
    private sealed class PendingWrite
    {
        public PendingWrite(PendingSite site, TextSpan rhs, string rhsText, int line)
        {
            Site = site;
            Rhs = rhs;
            RhsText = rhsText;
            Line = line;
        }

        public PendingSite Site
        {
            get;
        }

        public TextSpan Rhs
        {
            get;
        }

        public string RhsText
        {
            get;
        }

        public int Line
        {
            get;
        }

        public bool IsLiteral => RhsText.Trim() is "true" or "false";
    }

    /// <summary>Mutable accumulator for one CollectSitesAsync run.</summary>
    private sealed class SiteCollection
    {
        public List<PendingSite> Sites { get; } = new();

        public List<EditEntry> Edits { get; } = new();

        public List<PendingWrite> Writes { get; } = new();

        public PendingSite AddSite(string filePath, int line, TextSpan span, SemanticReplaceRole role, SourceText text, string? unsupportedReason = null)
        {
            var site = new PendingSite(filePath, line, span, role, text.ToString(span)) { UnsupportedReason = unsupportedReason };
            Sites.Add(site);
            return site;
        }

        public void AddEdit(PendingSite? owner, string filePath, TextSpan span, string newText, int line)
        {
            var entry = new EditEntry(new ReferenceEdit(filePath, span, newText), line);
            Edits.Add(entry);
            owner?.Own.Add(entry);
        }
    }

    /// <summary>True when the rewritten read (which starts with '!') must be wrapped in parentheses: the reference is a receiver or a postfix operand.</summary>
    private static bool NeedsParentheses(ExpressionSyntax reference)
    {
        return reference.Parent switch
        {
            MemberAccessExpressionSyntax member => member.Expression == reference,
            ElementAccessExpressionSyntax element => element.Expression == reference,
            ConditionalAccessExpressionSyntax conditional => conditional.Expression == reference,
            PostfixUnaryExpressionSyntax => true,
            _ => false
        };
    }

    /// <summary>
    /// Turns every pending write into edits. A write whose right-hand side contains another edit (a nested reference or a nested write)
    /// is wrapped with zero-length insertions "!(" and ")" so the inner edits, which preserve their own value, stay in place; otherwise
    /// the whole right-hand side is replaced with the flipped text.
    /// </summary>
    private static void ComposeWrites(SiteCollection collection, string newName)
    {
        // Decide for every write before emitting anything, so one write's new edits never influence another's decision.
        var wrap = new List<bool>(collection.Writes.Count);
        foreach (var write in collection.Writes)
        {
            var file = write.Site.FilePath;
            var hasInnerEdit = collection.Edits.Any(e => e.Edit.FilePath == file && e.Edit.Span.Start < write.Rhs.End && write.Rhs.Start < e.Edit.Span.End);
            var hasInnerWrite = collection.Writes.Any(o => !ReferenceEquals(o, write) && o.Site.FilePath == file && write.Rhs.Contains(o.Rhs));
            wrap.Add(hasInnerEdit || hasInnerWrite);
        }

        for (var i = 0; i < collection.Writes.Count; i++)
        {
            var write = collection.Writes[i];
            var file = write.Site.FilePath;
            if (wrap[i])
            {
                collection.AddEdit(write.Site, file, new TextSpan(write.Rhs.Start, 0), "!(", write.Line);
                collection.AddEdit(write.Site, file, new TextSpan(write.Rhs.End, 0), ")", write.Line);
                continue;
            }

            var role = write.IsLiteral
                ? (write.Site.Role == SemanticReplaceRole.DeclarationInitializer ? SemanticReplaceRole.DeclarationInitializer : SemanticReplaceRole.WriteLiteral)
                : SemanticReplaceRole.WriteExpression;
            collection.AddEdit(write.Site, file, write.Rhs, BooleanInversionRewriter.Rewrite(role, write.RhsText, newName), write.Line);
        }
    }

    /// <summary>Classifies one identifier reference and records its site and edits (write right-hand sides are deferred to ComposeWrites).</summary>
    private static void CollectReference(SiteCollection collection, string filePath, SourceText text, IdentifierNameSyntax identifier, string newName)
    {
        var line = text.Lines.GetLinePosition(identifier.SpanStart).Line + 1;
        var reference = identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier
            ? (ExpressionSyntax)access
            : identifier;

        // A reference inside a doc-comment cref is a plain rename: no polarity to flip.
        if (identifier.Ancestors().OfType<CrefSyntax>().Any())
        {
            var crefSite = collection.AddSite(filePath, line, identifier.Span, SemanticReplaceRole.NameOf, text);
            collection.AddEdit(crefSite, filePath, identifier.Span, newName, line);
            return;
        }

        SemanticReplaceRole role;
        string? reason = null;
        if (identifier.Parent is NameEqualsSyntax { Parent: AttributeArgumentSyntax })
        {
            role = SemanticReplaceRole.Unsupported;
            reason = "attribute named arguments are not supported";
        }
        else if (identifier.Parent is MemberBindingExpressionSyntax)
        {
            role = SemanticReplaceRole.Unsupported;
            reason = "null-conditional access (x?.Member) is not supported";
        }
        else
        {
            role = ReferenceRoleClassifier.Classify(identifier, out reason);
        }

        switch (role)
        {
            case SemanticReplaceRole.WriteLiteral:
            case SemanticReplaceRole.WriteExpression:
                var assignment = reference.Parent as AssignmentExpressionSyntax;
                var usableTarget = assignment is not null
                    && assignment.Left == reference
                    && (assignment.Parent is ExpressionStatementSyntax
                        || (assignment.Parent is InitializerExpressionSyntax initializer
                            && (initializer.IsKind(SyntaxKind.ObjectInitializerExpression) || initializer.IsKind(SyntaxKind.WithInitializerExpression))));
                if (!usableTarget)
                {
                    collection.AddSite(filePath, line, reference.Span, SemanticReplaceRole.Unsupported, text, "assignment used as a value is not supported");
                    return;
                }

                var writeSite = collection.AddSite(filePath, line, assignment!.Span, role, text);
                collection.AddEdit(writeSite, filePath, identifier.Span, newName, line);
                collection.Writes.Add(new PendingWrite(writeSite, assignment.Right.Span, text.ToString(assignment.Right.Span), line));
                return;

            case SemanticReplaceRole.Read:
                var readSite = collection.AddSite(filePath, line, reference.Span, role, text);
                collection.AddEdit(readSite, filePath, reference.Span, BooleanInversionRewriter.Rewrite(role, readSite.Before, newName, NeedsParentheses(reference)), line);
                return;

            case SemanticReplaceRole.NegatedRead:
                if (reference.Parent is not PrefixUnaryExpressionSyntax negation)
                {
                    collection.AddSite(filePath, line, reference.Span, SemanticReplaceRole.Unsupported, text, "negated read shape is not recognised");
                    return;
                }

                var negatedSite = collection.AddSite(filePath, line, negation.Span, role, text);
                collection.AddEdit(negatedSite, filePath, negation.Span, BooleanInversionRewriter.Rewrite(role, negatedSite.Before, newName), line);
                return;

            case SemanticReplaceRole.NameOf:
                var nameOfSite = collection.AddSite(filePath, line, identifier.Span, role, text);
                collection.AddEdit(nameOfSite, filePath, identifier.Span, newName, line);
                return;

            default:
                collection.AddSite(filePath, line, reference.Span, SemanticReplaceRole.Unsupported, text, reason ?? "unsupported reference shape");
                return;
        }
    }

    /// <summary>Parses a document once per CollectSitesAsync run; null when it has no syntax root.</summary>
    private static async Task<(SyntaxNode Root, SourceText Text)?> GetParsedAsync(Dictionary<DocumentId, (SyntaxNode Root, SourceText Text)> parsedDocuments, Document document, CancellationToken cancellationToken)
    {
        if (parsedDocuments.TryGetValue(document.Id, out var parsed))
        {
            return parsed;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
        {
            return null;
        }

        parsed = (root, await document.GetTextAsync(cancellationToken));
        parsedDocuments[document.Id] = parsed;
        return parsed;
    }

    /// <summary>For a record positional property, the primary constructor parameter declared by the same ParameterSyntax; else null.</summary>
    private static IParameterSymbol? GetRecordPrimaryParameter(IPropertySymbol property, CancellationToken cancellationToken)
    {
        if (property.DeclaringSyntaxReferences.Length != 1
            || property.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not ParameterSyntax { Parent: ParameterListSyntax { Parent: RecordDeclarationSyntax } } parameterSyntax)
        {
            return null;
        }

        foreach (var constructor in property.ContainingType.InstanceConstructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                if (parameter.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == parameterSyntax.SyntaxTree && r.Span == parameterSyntax.Span))
                {
                    return parameter;
                }
            }
        }

        return null;
    }

    /// <summary>The argument bound to parameter: the one named after it, else the positional argument at its ordinal (C# binds a positional argument to its own index).</summary>
    private static ArgumentSyntax? FindArgumentFor(ArgumentListSyntax argumentList, IParameterSymbol parameter)
    {
        var arguments = argumentList.Arguments;
        var named = arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter.Name);
        if (named is not null)
        {
            return named;
        }

        return parameter.Ordinal < arguments.Count && arguments[parameter.Ordinal].NameColon is null ? arguments[parameter.Ordinal] : null;
    }

    private static bool IsRecordDeconstruct(IMethodSymbol method, INamedTypeSymbol recordType)
    {
        return method.Name == WellKnownMemberNames.DeconstructMethodName && IsSameDeclaration(method.ContainingType, recordType);
    }

    private static bool UsesRecordDeconstruct(DeconstructionInfo info, INamedTypeSymbol recordType)
    {
        return (info.Method is { } method && IsRecordDeconstruct(method, recordType)) || info.Nested.Any(n => UsesRecordDeconstruct(n, recordType));
    }

    /// <summary>Cheap syntax prefilter: deconstructing assignment/declaration, foreach deconstruction, or a positional pattern.</summary>
    private static bool IsDeconstructionCandidate(SyntaxNode node)
    {
        return node switch
        {
            AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Left is TupleExpressionSyntax or DeclarationExpressionSyntax,
            ForEachVariableStatementSyntax => true,
            PositionalPatternClauseSyntax => true,
            _ => false
        };
    }

    /// <summary>True when node (a deconstruction candidate) calls the record's Deconstruct, at any nesting depth.</summary>
    private static bool DeconstructsRecord(SemanticModel model, SyntaxNode node, INamedTypeSymbol recordType, CancellationToken cancellationToken)
    {
        return node switch
        {
            AssignmentExpressionSyntax assignment => UsesRecordDeconstruct(model.GetDeconstructionInfo(assignment), recordType),
            ForEachVariableStatementSyntax loop => UsesRecordDeconstruct(model.GetDeconstructionInfo(loop), recordType),
            PositionalPatternClauseSyntax { Parent: RecursivePatternSyntax pattern } =>
                model.GetOperation(pattern, cancellationToken) is Microsoft.CodeAnalysis.Operations.IRecursivePatternOperation { DeconstructSymbol: IMethodSymbol method }
                && IsRecordDeconstruct(method, recordType),
            _ => false
        };
    }

    /// <summary>The argument list of a node that can call a constructor (new R(...), target-typed new(...), this(...)/base(...), a record base list entry); else null.</summary>
    private static ArgumentListSyntax? ConstructorArgumentList(SyntaxNode node)
    {
        return node switch
        {
            ObjectCreationExpressionSyntax creation => creation.ArgumentList,
            ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation.ArgumentList,
            ConstructorInitializerSyntax initializer => initializer.ArgumentList,
            PrimaryConstructorBaseTypeSyntax baseType => baseType.ArgumentList,
            _ => null
        };
    }

    /// <summary>Records one primary constructor argument: a named argument's name is renamed; named or positional, its value is flipped.</summary>
    private static void CollectConstructorArgument(SiteCollection collection, HashSet<(string FilePath, int Start)> seen, string filePath, SourceText text, ArgumentSyntax argument, string newName)
    {
        var line = text.Lines.GetLinePosition(argument.SpanStart).Line + 1;
        if (!argument.RefKindKeyword.IsKind(SyntaxKind.None))
        {
            collection.AddSite(filePath, line, argument.Span, SemanticReplaceRole.Unsupported, text, "ref/out/in constructor argument is not supported");
            return;
        }

        var role = argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression) || argument.Expression.IsKind(SyntaxKind.FalseLiteralExpression)
            ? SemanticReplaceRole.WriteLiteral
            : SemanticReplaceRole.WriteExpression;
        var site = collection.AddSite(filePath, line, argument.Span, role, text);
        if (argument.NameColon is { } nameColon)
        {
            seen.Add((filePath, nameColon.Name.SpanStart)); // the parameter's own reference pass must not see it again
            collection.AddEdit(site, filePath, nameColon.Name.Identifier.Span, newName, line);
        }

        collection.Writes.Add(new PendingWrite(site, argument.Expression.Span, text.ToString(argument.Expression.Span), line));
    }

    /// <summary>
    /// One pass over the declaring project and every project that depends on it, for the record-only sites the property's references miss:
    /// - every call of the primary constructor (new R(...), target-typed new(...), this(...), a derived record's base list): the argument
    ///   bound to the parameter is flipped, and renamed when named. A call that omits it uses the default, flipped with the declaration.
    /// - every deconstruction (var (a, b) = r; foreach (var (a, b) in rs); r is R(true, _)): Unsupported, because Deconstruct hands out the
    ///   inverted value positionally under the caller's own variable name, so nothing at that site would reveal the polarity change.
    /// Calls are found by syntax and then bound, not via SymbolFinder: constructor references do not reliably surface target-typed new(...).
    /// </summary>
    private static async Task CollectRecordPositionalSitesAsync(SiteCollection collection, HashSet<(string FilePath, int Start)> seen, Dictionary<DocumentId, (SyntaxNode Root, SourceText Text)> parsedDocuments, Solution solution, IParameterSymbol parameter, SyntaxTree declarationTree, string newName, CancellationToken cancellationToken)
    {
        var constructor = (IMethodSymbol)parameter.ContainingSymbol;
        var recordType = constructor.ContainingType;
        var declaringProject = solution.GetDocument(declarationTree)?.Project.Id;
        var projectIds = declaringProject is null
            ? solution.ProjectIds.ToList()
            : new[] { declaringProject }.Concat(solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(declaringProject)).ToList();
        var seenDeconstructions = new HashSet<(string FilePath, int Start)>();
        foreach (var document in projectIds.SelectMany(id => solution.GetProject(id)?.Documents ?? Enumerable.Empty<Document>()))
        {
            if (await GetParsedAsync(parsedDocuments, document, cancellationToken) is not { } parsed)
            {
                continue;
            }

            // Syntax prefilter: only calls that pass an argument for this parameter, and deconstruction shapes, are bound.
            var calls = new List<(SyntaxNode Call, ArgumentListSyntax List, ArgumentSyntax Argument)>();
            var deconstructions = new List<SyntaxNode>();
            foreach (var node in parsed.Root.DescendantNodes())
            {
                if (IsDeconstructionCandidate(node))
                {
                    deconstructions.Add(node);
                }
                else if (ConstructorArgumentList(node) is { } list && FindArgumentFor(list, parameter) is { } argument)
                {
                    calls.Add((node, list, argument));
                }
            }

            if ((calls.Count == 0 && deconstructions.Count == 0) || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            var filePath = document.FilePath ?? document.Name;
            foreach (var (call, list, argument) in calls)
            {
                // A dependent project can see the record through a retargeting wrapper (its own view of the referenced assembly), so the bound
                // constructor is a different instance than the target's: compare by declaration, and accept an overload-resolution candidate.
                var info = model.GetSymbolInfo(call, cancellationToken);
                var isPrimaryConstructorCall = IsSameDeclaration(info.Symbol, constructor)
                    || (info.Symbol is null && info.CandidateSymbols.Any(candidate => IsSameDeclaration(candidate, constructor)));
                if (isPrimaryConstructorCall && seen.Add((filePath, list.SpanStart))) // the same file reached through a second project is skipped
                {
                    CollectConstructorArgument(collection, seen, filePath, parsed.Text, argument, newName);
                }
            }

            foreach (var node in deconstructions)
            {
                if (DeconstructsRecord(model, node, recordType, cancellationToken) && seenDeconstructions.Add((filePath, node.SpanStart)))
                {
                    var line = parsed.Text.Lines.GetLinePosition(node.SpanStart).Line + 1;
                    collection.AddSite(filePath, line, node.Span, SemanticReplaceRole.Unsupported, parsed.Text,
                        $"deconstruction of '{recordType.Name}' reads the inverted value positionally; replace it with property access first");
                }
            }
        }
    }

    /// <summary>
    /// True when a and b are the same declaration, even when they are different symbol instances: the target symbol can come from a
    /// different compilation of the record's project than the one a dependent project's semantic model binds against.
    /// </summary>
    private static bool IsSameDeclaration(ISymbol? a, ISymbol? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        a = a.OriginalDefinition;
        b = b.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(a, b))
        {
            return true;
        }

        var id = a.GetDocumentationCommentId();
        return id is not null && id == b.GetDocumentationCommentId() && a.ContainingAssembly?.Name == b.ContainingAssembly?.Name;
    }

    /// <summary>Unsupported reason for a named argument the primary constructor scan did not attribute, naming the call it sits in.</summary>
    private static string UnattributedNamedArgumentReason(IdentifierNameSyntax identifier, ISymbol target)
    {
        var call = identifier.Parent?.Parent?.Parent?.Parent; // NameColon -> Argument -> ArgumentList -> call
        var callee = call switch
        {
            ObjectCreationExpressionSyntax creation => $"new {creation.Type}(...)",
            ImplicitObjectCreationExpressionSyntax => "target-typed new(...)",
            InvocationExpressionSyntax invocation => $"{invocation.Expression}(...)",
            ConstructorInitializerSyntax initializer => $"{initializer.ThisOrBaseKeyword.Text}(...)",
            PrimaryConstructorBaseTypeSyntax baseType => $"{baseType.Type}(...)",
            _ => call?.Kind().ToString() ?? "an unrecognised call"
        };
        return $"named argument '{identifier.Identifier.ValueText}:' in {callee} could not be matched to the primary constructor of '{target.ContainingType?.Name}'; rewrite it by hand";
    }

    /// <summary>Returns the first edit that conflicts with another in the same file: overlapping non-empty spans, or an insertion strictly inside another edit's span.</summary>
    private static EditEntry? FindConflict(IEnumerable<EditEntry> edits)
    {
        foreach (var group in edits.GroupBy(e => e.Edit.FilePath).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var nonEmpty = group.Where(e => e.Edit.Span.Length > 0).OrderBy(e => e.Edit.Span.Start).ThenBy(e => e.Edit.Span.End).ToList();
            var maxEnd = -1;
            foreach (var entry in nonEmpty)
            {
                if (entry.Edit.Span.Start < maxEnd)
                {
                    return entry;
                }

                maxEnd = Math.Max(maxEnd, entry.Edit.Span.End);
            }

            foreach (var insert in group.Where(e => e.Edit.Span.Length == 0))
            {
                var position = insert.Edit.Span.Start;
                if (nonEmpty.Any(e => e.Edit.Span.Start < position && position < e.Edit.Span.End))
                {
                    return insert;
                }
            }
        }

        return null;
    }

    /// <summary>Applies edits (offsets in the original file text) to a fragment that starts at fragmentStart, in application order.</summary>
    private static string ApplyToFragment(string fragment, int fragmentStart, IEnumerable<ReferenceEdit> edits)
    {
        var result = fragment;
        foreach (var edit in edits.OrderByDescending(e => e.Span.Start).ThenByDescending(e => e.Span.End))
        {
            var start = edit.Span.Start - fragmentStart;
            result = result.Remove(start, edit.Span.Length).Insert(start, edit.NewText);
        }

        return result;
    }

    /// <summary>Builds the public site record, computing After from the edits that fall inside the site (plus the site's own edits).</summary>
    private static SemanticReplaceSite ToSite(PendingSite site, IReadOnlyList<EditEntry> edits)
    {
        if (site.UnsupportedReason is not null)
        {
            return new SemanticReplaceSite(site.FilePath, site.Line, site.Role, site.Before, site.Before, site.UnsupportedReason);
        }

        var relevant = edits
            .Where(e => e.Edit.FilePath == site.FilePath
                && (site.Own.Contains(e)
                    || (e.Edit.Span.Length > 0
                        ? site.Span.Contains(e.Edit.Span)
                        : site.Span.Start < e.Edit.Span.Start && e.Edit.Span.Start < site.Span.End)))
            .Select(e => e.Edit);
        return new SemanticReplaceSite(site.FilePath, site.Line, site.Role, site.Before, ApplyToFragment(site.Before, site.Span.Start, relevant), null);
    }

    /// <summary>
    /// Orchestrates the planning phase: calls CollectSitesAsync, groups edits by file, retrieves original document text,
    /// applies edits in correct order (descending by span), and returns a dictionary of file changes.
    /// </summary>
    /// <remarks>
    /// Errors from CollectSitesAsync are returned unchanged. If a document for an edit's FilePath cannot be found,
    /// returns a TargetIneligible error.
    /// </remarks>
    /// <returns>(Sites, Changes dictionary mapping each changed file to its full new text, error or null)</returns>
    public async Task<(List<SemanticReplaceSite> Sites, Dictionary<FilePathWrapper, string> Changes, ResultError? Error)> PlanInvertBooleanAsync(ISymbol symbol, string newName, CancellationToken cancellationToken = default)
    {
        // Step 1: Call CollectSitesAsync
        var (sites, edits, error) = await CollectSitesAsync(symbol, newName, cancellationToken);
        if (error is not null)
        {
            return (sites, new Dictionary<FilePathWrapper, string>(), error);
        }

        // Step 2: Group edits by FilePath and build changes dictionary
        var changes = new Dictionary<FilePathWrapper, string>();
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);

        foreach (var fileGroup in edits.GroupBy(e => e.FilePath))
        {
            var filePath = fileGroup.Key;

            // Find the document for this file path
            Document? document = null;
            foreach (var project in solution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath == filePath || doc.Name == filePath)
                    {
                        document = doc;
                        break;
                    }
                }
                if (document is not null)
                {
                    break;
                }
            }

            if (document is null)
            {
                return (sites, new Dictionary<FilePathWrapper, string>(), new ResultError(
                    ToolErrorCode.TargetIneligible,
                    $"Document not found for file '{filePath}'."));
            }

            // Get the original text
            var originalText = await document.GetTextAsync(cancellationToken);
            var resultText = originalText.ToString();

            // Apply all edits for this file in one pass, in the order they already come (sorted by ReferenceEdit.ApplicationOrder)
            foreach (var edit in fileGroup.OrderByDescending(e => e.Span.Start).ThenByDescending(e => e.Span.End))
            {
                resultText = resultText.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
            }

            changes[filePath] = resultText;
        }

        return (sites, changes, null);
    }

    /// <summary>
    /// Orchestrates the full semantic find-replace operation: resolves a docCommentId to a symbol,
    /// then plans the inversion and rename. Returns both sites and file changes.
    /// </summary>
    /// <remarks>
    /// Errors from ResolveBoolMemberAsync or PlanInvertBooleanAsync are returned immediately.
    /// </remarks>
    /// <returns>SemanticReplaceOutcome with sites, changes, and optional error.</returns>
    public async Task<SemanticReplaceOutcome> InvertBooleanAndRenameAsync(string docCommentId, string newName, CancellationToken cancellationToken = default)
    {
        // Step 1: Resolve the docCommentId to a symbol
        var (symbol, resolveError) = await ResolveBoolMemberAsync(docCommentId, newName, cancellationToken);
        if (resolveError is not null)
        {
            return new SemanticReplaceOutcome(new List<SemanticReplaceSite>(), new Dictionary<FilePathWrapper, string>(), resolveError);
        }

        // Step 2: Plan the operation
        var (sites, changes, planError) = await PlanInvertBooleanAsync(symbol!, newName, cancellationToken);
        return new SemanticReplaceOutcome(sites, changes, planError);
    }
}

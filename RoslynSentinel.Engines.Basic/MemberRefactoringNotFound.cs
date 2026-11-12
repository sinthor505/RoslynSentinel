using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The not-found errors of <c>MemberRefactoringEngine</c> in one place (plan_not_found_error_orientation, Step 5c). A call site hands
/// over what it has in hand (the wanted name, the syntax root or scope node, the file path) and gets back the
/// <see cref="ToolNotFoundException"/> to throw (or, for a site that returns a result message or a <see cref="BatchEditFailure"/>,
/// the message text). The text states facts and offers prefilled options only: ranked "Did you mean 'X'? Retry with param: 'X'."
/// hints, or the no-similar-name fact plus a prefilled GetFileOutline(filePath) call. Ranking is <see cref="NameCandidates.Rank{T}"/>
/// (through <see cref="DeclarationNotFoundDetail"/> for a file-wide lookup); nothing here ranks names itself. Each exception keeps the
/// missing-target kind and name the engine always passed, so the error code and catch blocks are unchanged.
/// A call site is one line: <c>throw MemberRefactoringNotFound.Method(methodName, root, filePath);</c>
/// </summary>
public static class MemberRefactoringNotFound
{
    private const int MaxListed = 15;

    // ---- Collectors: the declared names a site passes as the candidate pool (all syntax only, null-tolerant, distinct) ----

    /// <summary>Names of the class, struct, interface, record and enum declarations under <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> TypeNames(SyntaxNode? root)
        => Distinct(root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text));

    /// <summary>Names of the class declarations under <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> ClassNames(SyntaxNode? root)
        => Distinct(root?.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => c.Identifier.Text));

    /// <summary>Names of the enum declarations under <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> EnumNames(SyntaxNode? root)
        => Distinct(root?.DescendantNodes().OfType<EnumDeclarationSyntax>().Select(e => e.Identifier.Text));

    /// <summary>Names of the methods declared under <paramref name="scope"/> (a compilation unit, or one type).</summary>
    public static IReadOnlyList<string> MethodNames(SyntaxNode? scope)
        => Distinct(scope?.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text));

    /// <summary>Names of every member, enum member and type declared under <paramref name="scope"/>: the pool for a "target" that may be any declaration.</summary>
    public static IReadOnlyList<string> MemberNames(SyntaxNode? scope)
        => Distinct(scope?.DescendantNodes()
            .Select(n => n is EnumMemberDeclarationSyntax em ? em.Identifier.Text : n is MemberDeclarationSyntax md ? SyntaxTargetResolver.GetMemberName(md) : null)
            .OfType<string>());

    /// <summary>Names of the namespaces declared under <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> NamespaceNames(SyntaxNode? root)
        => Distinct(root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()));

    /// <summary>Names of the members of <paramref name="enumDecl"/>.</summary>
    public static IReadOnlyList<string> EnumMemberNames(EnumDeclarationSyntax? enumDecl)
        => Distinct(enumDecl?.Members.Select(m => m.Identifier.Text));

    /// <summary>Parameter names across every constructor of <paramref name="classDecl"/>.</summary>
    public static IReadOnlyList<string> ConstructorParameterNames(ClassDeclarationSyntax? classDecl)
        => Distinct(classDecl?.Members.OfType<ConstructorDeclarationSyntax>().SelectMany(c => c.ParameterList.Parameters).Select(p => p.Identifier.Text));

    // TODO MemberRefactoringEngine.GetAttributeName applies the same rule (private); the attribute-sweep slice can delegate to this one.
    /// <summary>Attribute names (last name segment) on <paramref name="target"/>, a member or type declaration.</summary>
    public static IReadOnlyList<string> AttributeNames(SyntaxNode? target)
        => Distinct((target as MemberDeclarationSyntax)?.AttributeLists.SelectMany(l => l.Attributes).Select(a => a.Name switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            _ => a.Name.ToString(),
        }));

    /// <summary>Base types and interfaces named in the base list of <paramref name="container"/> (empty without a base list).</summary>
    public static IReadOnlyList<string> BaseTypeNames(BaseTypeDeclarationSyntax? container)
        => Distinct(container?.BaseList?.Types.Select(t => t.Type.ToString()));

    /// <summary>Modifier keywords on <paramref name="target"/>.</summary>
    public static IReadOnlyList<string> ModifierNames(MemberDeclarationSyntax? target)
        => Distinct(target?.Modifiers.Select(m => m.Text));

    /// <summary>1-based start lines of <paramref name="nodes"/> in <paramref name="text"/>.</summary>
    public static IReadOnlyList<int> StartLines(SourceText text, IEnumerable<SyntaxNode> nodes)
        => nodes.Select(n => text.Lines.GetLineFromPosition(n.SpanStart).LineNumber + 1).Distinct().ToList();

    // ---- Exception factories: file-wide declaration lookups ----

    /// <summary>
    /// General form: a declaration of kind <paramref name="kindLabel"/> named <paramref name="wanted"/> is not among
    /// <paramref name="declaredNames"/> of the file. <paramref name="paramName"/> is the tool parameter a retry sets.
    /// </summary>
    public static ToolNotFoundException Declaration(string kindLabel, string wanted, string paramName, IEnumerable<string> declaredNames, string filePath, string? missingTargetKind = null)
        => new(DeclarationMessage(kindLabel, wanted, paramName, declaredNames, filePath), missingTargetKind ?? kindLabel, wanted);

    /// <summary>
    /// Class lookup miss (kind "class"): ConstructorParameter class lookups, GetConstructorParameters, MoveMember and
    /// PreviewInstanceMoveCallSites class lookups, the base-class lookup. States when the name is declared in the file as a struct,
    /// interface, enum or record instead.
    /// </summary>
    public static ToolNotFoundException Class(string className, SyntaxNode? root, string filePath, string paramName = "className")
    {
        var message = DeclarationMessage("class", className, paramName, ClassNames(root), filePath);
        var otherKind = OtherKindFact(root, className, filePath, n => n is ClassDeclarationSyntax, "a class");
        return new ToolNotFoundException(otherKind is null ? message : message + " " + otherKind, "class", className);
    }

    /// <summary>Enum lookup miss (kind "enum"): ModifyEnum. States when the name is declared as another kind of type.</summary>
    public static ToolNotFoundException Enum(string enumName, SyntaxNode? root, string filePath, string paramName = "enumName")
    {
        var message = DeclarationMessage("enum", enumName, paramName, EnumNames(root), filePath);
        var otherKind = OtherKindFact(root, enumName, filePath, n => n is EnumDeclarationSyntax, "an enum");
        return new ToolNotFoundException(otherKind is null ? message : message + " " + otherKind, "enum", enumName);
    }

    /// <summary>Method lookup miss (kind "method"): MethodSignature add/remove/view, AddRemoveParams, ConvertMethodToProperty, MakeMethodStatic.</summary>
    public static ToolNotFoundException Method(string methodName, SyntaxNode? root, string filePath, string paramName = "methodName")
        => Declaration("method", methodName, paramName, MethodNames(root), filePath);

    /// <summary>Type lookup miss (kind "type"): AddBaseType, RemoveBaseType, and SortMembers' container (pass <c>paramName: "containerName"</c>).</summary>
    public static ToolNotFoundException Type(string typeName, SyntaxNode? root, string filePath, string paramName = "typeName")
        => Declaration("type", typeName, paramName, TypeNames(root), filePath);

    /// <summary>
    /// Member-or-type target miss (kind "member"): ChangeAccessibility, AddModifier, RemoveModifier, AddAttribute, ReplaceAttribute,
    /// RemoveAttribute (the six identical former "Cannot edit: target ..." sites).
    /// </summary>
    public static ToolNotFoundException Target(string targetName, SyntaxNode? root, string filePath, string paramName = "targetName")
        => Declaration("declaration", targetName, paramName, MemberNames(root), filePath, "member");

    /// <summary>Namespace miss (kind "namespace"): AddTopLevelType. Also lists the namespaces the file declares.</summary>
    public static ToolNotFoundException Namespace(string namespaceName, SyntaxNode? root, string filePath, string paramName = "namespaceName")
    {
        var names = NamespaceNames(root);
        var message = DeclarationMessage("namespace", namespaceName, paramName, names, filePath);
        if (names.Count > 0)
        {
            message += $" Namespaces declared in '{filePath}': {JoinNames(names)}.";
        }

        return new ToolNotFoundException(message, "namespace", namespaceName);
    }

    // ---- Exception factories: lookups inside one declaration ----

    /// <summary>Enum member miss (kind "enum member"): RemoveEnumMember, ReplaceEnumMember. Lists the members of the named enum.</summary>
    public static ToolNotFoundException EnumMember(string memberName, string enumName, EnumDeclarationSyntax? enumDecl, string filePath, string paramName = "memberName")
        => new(ScopedMessage("enum member", memberName, paramName, EnumMemberNames(enumDecl), $"enum '{enumName}'", filePath), "enum member", memberName);

    /// <summary>Enum member miss from a caller that holds the enum's member names rather than its syntax (RemoveEnumMember, ReplaceEnumMember read them through GetContainerMembersAsync).</summary>
    public static ToolNotFoundException EnumMember(string memberName, string enumName, IEnumerable<string> enumMemberNames, string filePath, string paramName = "memberName")
        => new(ScopedMessage("enum member", memberName, paramName, enumMemberNames, $"enum '{enumName}'", filePath), "enum member", memberName);

    /// <summary>Constructor parameter miss (kind "parameter"): ConstructorParameter remove. Lists the parameters of the class's constructors.</summary>
    public static ToolNotFoundException ConstructorParameter(string paramName, string className, ClassDeclarationSyntax? classDecl, string filePath, string retryParamName = "paramName")
        => new(ScopedMessage("constructor parameter", paramName, retryParamName, ConstructorParameterNames(classDecl), $"the constructors of '{className}'", filePath), "parameter", paramName);

    /// <summary>
    /// The contextSnippet matched outside every constructor that declares <paramref name="paramName"/> (kind "parameter"): states the
    /// start lines of those constructors and offers a ReadFile of the first one.
    /// </summary>
    public static ToolNotFoundException SnippetOutsideConstructors(string paramName, string className, IReadOnlyList<int> constructorStartLines, string filePath)
    {
        var message = $"contextSnippet is not inside any constructor of '{className}' that declares parameter '{paramName}'.";
        if (constructorStartLines.Count > 0)
        {
            var first = constructorStartLines.Min();
            message += $" Constructors declaring '{paramName}' start at line {string.Join(", ", constructorStartLines)}. {ToolCallHint.Call("ReadFile", ("filePath", filePath), ("startLine", first.ToString(System.Globalization.CultureInfo.InvariantCulture)))} reads the first one.";
        }

        return new ToolNotFoundException(message, "parameter", paramName);
    }

    /// <summary>
    /// Attribute miss on a located target (kind "attribute"): ReplaceAttribute (pass <c>paramName: "oldAttributeName"</c>) and
    /// RemoveAttribute. Lists the attributes the target carries.
    /// </summary>
    public static ToolNotFoundException Attribute(string attributeName, string targetName, SyntaxNode? target, string filePath, string paramName = "attributeName")
        => new(ScopedMessage("attribute", attributeName, paramName, AttributeNames(target), $"'{targetName}'", filePath), "attribute", attributeName);

    // ---- Exception factories: documents and syntax that are present in the solution but unusable ----

    /// <summary>
    /// A document of the solution that gave no <paramref name="what"/> (for example "syntax root", "semantic model"): MoveMember,
    /// PreviewInstanceMoveCallSites and MoveMembersToBaseType sites. Offers ReadFile for the text the server holds.
    /// </summary>
    public static ToolNotFoundException Unavailable(string what, string filePath)
        => new($"The {what} of '{filePath}' is not available: the file is a document of the loaded solution but did not produce one. {ToolCallHint.Call("ReadFile", ("filePath", filePath))} returns the text the server holds for it.", "document", filePath);

    /// <summary>
    /// A lookup that failed on server state rather than on a name the caller typed (for example a base class that lives in an external
    /// assembly): <paramref name="fact"/> is the stated cause; a known <paramref name="filePath"/> adds a prefilled GetFileOutline option.
    /// </summary>
    public static ToolNotFoundException Unresolved(string missingTargetKind, string name, string fact, string? filePath = null)
        => new(string.IsNullOrEmpty(filePath) ? fact : $"{fact} To see what the file declares, call {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))}.", missingTargetKind, name);

    // ---- Message factories: sites that build a BatchEditFailure or a result message instead of throwing ----

    /// <summary>The text of a file-wide declaration miss (see <see cref="Declaration"/>).</summary>
    public static string DeclarationMessage(string kindLabel, string wanted, string paramName, IEnumerable<string> declaredNames, string filePath)
    {
        var names = Distinct(declaredNames);
        var detail = names.Count == 0
            ? DeclarationNotFoundDetail.BuildNoneDeclared(kindLabel, filePath)
            : DeclarationNotFoundDetail.Build(kindLabel, paramName, wanted, names, filePath);
        return $"{Capitalize(kindLabel)} '{wanted}' was not found in '{filePath}'. {detail}";
    }

    /// <summary>
    /// The message of a <see cref="BatchEditFailure"/> for a target that did not resolve: the item index and name, then the declaration miss
    /// with the retry parameter rendered as <c>batchEdits[i].paramName</c>. Sites: ApplyModifierBatch / ApplyAttributeBatch (use
    /// <c>kindLabel: "declaration"</c>, names <see cref="MemberNames"/>, <c>paramName: "targetName"</c>) and ApplyBaseTypeBatch
    /// (<c>kindLabel: "type"</c>, names <see cref="TypeNames"/>, <c>paramName: "typeName"</c>).
    /// </summary>
    public static string BatchMessage(int index, string wanted, string kindLabel, string paramName, IEnumerable<string> declaredNames, string filePath)
        => $"batchEdits[{index}] ({wanted}): " + DeclarationMessage(kindLabel, wanted, $"batchEdits[{index}].{paramName}", declaredNames, filePath);

    /// <summary>
    /// Result message for RemoveModifier when the target does not carry <paramref name="modifier"/>: the modifiers it does carry, with a
    /// prefilled retry for a near name. Not a throw: the edit outcome stays AlreadyInTargetState.
    /// </summary>
    public static string ModifierMessage(string modifier, string targetName, MemberDeclarationSyntax? target, string paramName = "modifier")
        => ScopedMessage("modifier", modifier, paramName, ModifierNames(target), $"'{targetName}'", null);

    /// <summary>
    /// Result message for RemoveBaseType when the type's base list does not name <paramref name="baseTypeName"/>: the base types it does
    /// name (or that it has none), with a prefilled retry for a near name. Not a throw: the edit outcome stays AlreadyInTargetState.
    /// </summary>
    public static string BaseTypeMessage(string baseTypeName, string typeName, BaseTypeDeclarationSyntax? container, string paramName = "baseTypeName")
        => ScopedMessage("base type", baseTypeName, paramName, BaseTypeNames(container), $"the base list of '{typeName}'", null);

    // ---- Rendering ----

    // A lookup inside one named declaration: candidates (retry prefilled) or the no-similar-name fact, and what the scope declares.
    private static string ScopedMessage(string kindLabel, string wanted, string paramName, IEnumerable<string> declaredNames, string scope, string? filePath)
    {
        var names = Distinct(declaredNames);
        var candidates = NameCandidates.Rank(wanted, names, name => name);
        var parts = new List<string> { $"{Capitalize(kindLabel)} '{wanted}' was not found in {scope}." };
        if (candidates.Count > 0)
        {
            parts.AddRange(candidates.Select(candidate => $"Did you mean '{candidate.Item}'? Retry with {paramName}: '{candidate.Item}'."));
        }
        else if (names.Count == 0)
        {
            parts.Add($"No {kindLabel}s are declared in {scope}.");
        }
        else
        {
            parts.Add($"No {kindLabel} in {scope} has a similar name to '{wanted}'.");
        }

        if (names.Count > 0)
        {
            parts.Add($"Declared in {scope}: {JoinNames(names)}.");
            if (candidates.Count == 0)
            {
                parts.Add($"'{wanted}' is not among them, so it does not exist in {scope}.");
            }
        }

        if (candidates.Count == 0 && !string.IsNullOrEmpty(filePath))
        {
            parts.Add($"To see what the file declares, call {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))}.");
        }

        return string.Join(" ", parts);
    }

    // The file declares a type of this name, but of another kind than the lookup wanted (a kind mistake, not a name mistake).
    private static string? OtherKindFact(SyntaxNode? root, string wanted, string filePath, Func<BaseTypeDeclarationSyntax, bool> isExpectedKind, string expectedKindWithArticle)
    {
        // Every same-named declaration of another kind is described (nested types in different containers can share a name),
        // so no first match is picked among several.
        var actualKinds = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == wanted && !isExpectedKind(t))
            .Select(other => other switch
            {
                ClassDeclarationSyntax => "a class",
                StructDeclarationSyntax => "a struct",
                InterfaceDeclarationSyntax => "an interface",
                EnumDeclarationSyntax => "an enum",
                RecordDeclarationSyntax => "a record",
                _ => "another kind of type",
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (actualKinds is null || actualKinds.Count == 0)
        {
            return null;
        }

        return $"'{wanted}' is declared in '{filePath}' as {string.Join(" and ", actualKinds)}, not as {expectedKindWithArticle}.";
    }

    private static string JoinNames(IReadOnlyList<string> names)
        => string.Join(", ", names.Take(MaxListed)) + (names.Count > MaxListed ? $" (+{names.Count - MaxListed} more)" : string.Empty);

    private static string Capitalize(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static IReadOnlyList<string> Distinct(IEnumerable<string>? names)
        => names?.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
}

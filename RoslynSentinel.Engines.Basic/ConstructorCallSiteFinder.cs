using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Engines.Basic;

/// <summary>The syntactic shape of a call site of a constructor.</summary>
internal enum ConstructorCallSiteKind
{
    /// <summary><c>new X(...)</c> or target-typed <c>new(...)</c>.</summary>
    ObjectCreation,

    /// <summary><c>: base(...)</c> or <c>: this(...)</c> on a constructor.</summary>
    ConstructorInitializer,

    /// <summary>A derived-class constructor with no initializer, which implicitly calls <c>base()</c>.</summary>
    ImplicitBaseInitializer,

    /// <summary>A derived class with no declared constructor at all; its implicit constructor calls <c>base()</c>.</summary>
    ImplicitDerivedConstructor,

    /// <summary>A reference to the constructor that is not one of the shapes above (e.g. an attribute usage).</summary>
    Unsupported,
}

/// <summary>
/// One place that invokes a constructor. <see cref="Node"/> is the node to edit (creation expression,
/// constructor initializer, or - for an implicit base call - the derived constructor declaration).
/// <see cref="Line"/> is 1-based and <see cref="FilePath"/> is the document's full path, so
/// <c>"{FilePath}:{Line}"</c> is directly usable as a callSiteFixups key.
/// </summary>
internal sealed record ConstructorCallSite(Document Document, SyntaxNode Node, ConstructorCallSiteKind Kind, string FilePath, int Line, string CallText, bool UseNamedArgument)
{
    public bool IsEditable => Kind is ConstructorCallSiteKind.ObjectCreation or ConstructorCallSiteKind.ConstructorInitializer or ConstructorCallSiteKind.ImplicitBaseInitializer;

    /// <summary>Why a callSiteFixups entry cannot repair this site, or null when it can.</summary>
    public string? UnfixableReason => Kind switch
    {
        ConstructorCallSiteKind.ImplicitDerivedConstructor => "derived class has no explicit constructor, so there is no call to add an argument to - add an explicit constructor that calls base(...) first, or pass defaultValue/nullDefault",
        ConstructorCallSiteKind.Unsupported => "reference is not an object creation or constructor initializer (e.g. an attribute usage) - fix it by hand, or pass defaultValue/nullDefault",
        _ => null,
    };
}

/// <summary>
/// A constructor call site that ConstructorParameter(add, callSiteFixups) could not resolve. <see cref="FilePath"/>
/// and <see cref="Line"/> form the exact callSiteFixups key (<c>"{FilePath}:{Line}"</c>); <see cref="Note"/> is
/// non-null when no callSiteFixups entry can repair the site and says why.
/// </summary>
public sealed record UnresolvedConstructorCallSite(string FilePath, int Line, string CallText, string? Note);

/// <summary>
/// Outcome of <c>AddConstructorParameterWithCallSitesAsync</c>. Exactly one of these holds: <see cref="ClassEdit"/>
/// is not Modified (pass its message through); <see cref="InvalidArgumentMessage"/> is set; <see cref="UnresolvedSites"/>
/// is non-empty; or <see cref="Changes"/> holds the class edit plus every call-site edit as one change set.
/// </summary>
public sealed record AddConstructorParameterCascadeResult(DocumentEditResult ClassEdit, Dictionary<FilePathWrapper, string> Changes, IReadOnlyList<UnresolvedConstructorCallSite> UnresolvedSites, string? InvalidArgumentMessage, int CallSitesUpdated, int CallSitesLeftToDefault);

/// <summary>
/// Finds every syntactic call site of one constructor and appends an argument at each. Unlike the
/// ChangeSignature call-site pass, the node chosen for a reference is the innermost creation expression or
/// constructor initializer whose BOUND symbol is the target constructor, so <c>Foo(new Bar(x))</c> targeting
/// Bar's constructor edits Bar's argument list, not Foo's.
/// </summary>
internal static class ConstructorCallSiteFinder
{
    public static async Task<List<ConstructorCallSite>> FindAsync(Solution solution, IMethodSymbol targetCtor, CancellationToken cancellationToken)
    {
        var sites = new List<ConstructorCallSite>();
        var seen = new HashSet<(DocumentId, int)>();
        var original = targetCtor.OriginalDefinition;

        var references = await SymbolFinder.FindReferencesAsync(targetCtor, solution, cancellationToken);
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                if (location.IsImplicit || location.Document == null || !location.Location.IsInSource)
                {
                    continue;
                }

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                {
                    continue;
                }

                var span = location.Location.SourceSpan;
                var start = root.FindToken(span.Start, findInsideTrivia: true).Parent;
                if (start == null || start.AncestorsAndSelf().Any(a => a is DocumentationCommentTriviaSyntax or CrefSyntax))
                {
                    continue; // an XML-doc cref is not a call
                }

                IMethodSymbol? bound = null;
                SyntaxNode? siteNode = null;
                foreach (var node in start.AncestorsAndSelf())
                {
                    if (node is BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax)
                    {
                        var candidate = node switch
                        {
                            BaseObjectCreationExpressionSyntax creation => model.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol,
                            ConstructorInitializerSyntax initializer => model.GetSymbolInfo(initializer, cancellationToken).Symbol as IMethodSymbol,
                            _ => null,
                        };
                        if (candidate != null && SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, original))
                        {
                            siteNode = node;
                            bound = candidate;
                            break;
                        }
                    }
                    else if (node is AttributeSyntax or StatementSyntax or MemberDeclarationSyntax)
                    {
                        break;
                    }
                }

                if (siteNode == null || bound == null)
                {
                    if (seen.Add((document.Id, span.Start)))
                    {
                        var lineNumber = root.SyntaxTree.GetLineSpan(span, cancellationToken).StartLinePosition.Line;
                        var lineText = root.SyntaxTree.GetText(cancellationToken).Lines[lineNumber].ToString().Trim();
                        sites.Add(new ConstructorCallSite(document, start, ConstructorCallSiteKind.Unsupported, document.FilePath ?? document.Name, lineNumber + 1, Truncate(lineText), false));
                    }

                    continue;
                }

                if (!seen.Add((document.Id, siteNode.SpanStart)))
                {
                    continue;
                }

                var arguments = siteNode switch
                {
                    BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments,
                    ConstructorInitializerSyntax initializer => initializer.ArgumentList.Arguments,
                    _ => null,
                };
                var argumentCount = arguments?.Count ?? 0;
                var usesNamed = arguments?.Any(a => a.NameColon != null) ?? false;
                // Named when the site already uses named arguments, or leaves trailing optional parameters
                // out (a positional argument appended at the end would bind to the wrong parameter).
                var useNamedArgument = usesNamed || argumentCount < bound.Parameters.Length;
                var kind = siteNode is ConstructorInitializerSyntax ? ConstructorCallSiteKind.ConstructorInitializer : ConstructorCallSiteKind.ObjectCreation;
                sites.Add(new ConstructorCallSite(document, siteNode, kind, document.FilePath ?? document.Name, LineOf(siteNode, cancellationToken), Truncate(siteNode.ToString()), useNamedArgument));
            }
        }

        await AddTargetTypedCreationSitesAsync(solution, targetCtor, sites, seen, cancellationToken);
        await AddImplicitBaseCallSitesAsync(solution, targetCtor, sites, seen, cancellationToken);
        return sites;
    }

    /// <summary>
    /// SymbolFinder.FindReferencesAsync does not report the target-typed <c>new(...)</c> form as a reference to the
    /// constructor it binds to, so those sites are found by binding every <c>ImplicitObjectCreationExpression</c>
    /// directly. Only documents that contain one are given a semantic model. Sites already found are skipped.
    /// </summary>
    private static async Task AddTargetTypedCreationSitesAsync(Solution solution, IMethodSymbol targetCtor, List<ConstructorCallSite> sites, HashSet<(DocumentId, int)> seen, CancellationToken cancellationToken)
    {
        var original = targetCtor.OriginalDefinition;
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var creations = root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>().ToList();
            if (creations.Count == 0)
            {
                continue;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null)
            {
                continue;
            }

            foreach (var creation in creations)
            {
                if (model.GetSymbolInfo(creation, cancellationToken).Symbol is not IMethodSymbol bound
                    || !SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, original)
                    || !seen.Add((document.Id, creation.SpanStart)))
                {
                    continue;
                }

                var arguments = creation.ArgumentList.Arguments;
                var useNamedArgument = arguments.Any(a => a.NameColon != null) || arguments.Count < bound.Parameters.Length;
                sites.Add(new ConstructorCallSite(document, creation, ConstructorCallSiteKind.ObjectCreation, document.FilePath ?? document.Name, LineOf(creation, cancellationToken), Truncate(creation.ToString()), useNamedArgument));
            }
        }
    }

    /// <summary>
    /// A parameterless constructor is also called implicitly, as <c>base()</c>, by every directly derived class
    /// constructor that has no initializer. There is no syntax for the reference finder to report, so those are found
    /// from the derived types.
    /// </summary>
    private static async Task AddImplicitBaseCallSitesAsync(Solution solution, IMethodSymbol targetCtor, List<ConstructorCallSite> sites, HashSet<(DocumentId, int)> seen, CancellationToken cancellationToken)
    {
        if (targetCtor.Parameters.Length != 0 || targetCtor.IsStatic)
        {
            return;
        }

        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(targetCtor.ContainingType, solution, transitive: false, projects: null, cancellationToken: cancellationToken);
        foreach (var derived in derivedTypes)
        {
            var declarations = new List<(Document Document, ClassDeclarationSyntax Declaration)>();
            foreach (var syntaxReference in derived.DeclaringSyntaxReferences)
            {
                if (await syntaxReference.GetSyntaxAsync(cancellationToken) is ClassDeclarationSyntax declaration
                    && declaration.ParameterList == null
                    && solution.GetDocument(declaration.SyntaxTree) is { } document)
                {
                    declarations.Add((document, declaration));
                }
            }

            if (declarations.Count == 0)
            {
                continue;
            }

            var constructors = declarations
                .SelectMany(d => d.Declaration.Members.OfType<ConstructorDeclarationSyntax>()
                    .Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword))
                    .Select(c => (d.Document, Constructor: c)))
                .ToList();
            if (constructors.Count == 0)
            {
                var (document, declaration) = declarations[0];
                if (seen.Add((document.Id, declaration.SpanStart)))
                {
                    sites.Add(new ConstructorCallSite(document, declaration, ConstructorCallSiteKind.ImplicitDerivedConstructor, document.FilePath ?? document.Name,
                        declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1, $"{derived.Name} [no explicit constructor; implicitly calls base()]", false));
                }

                continue;
            }

            foreach (var (document, constructor) in constructors)
            {
                if (constructor.Initializer == null && seen.Add((document.Id, constructor.SpanStart)))
                {
                    sites.Add(new ConstructorCallSite(document, constructor, ConstructorCallSiteKind.ImplicitBaseInitializer, document.FilePath ?? document.Name,
                        constructor.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1, $"{constructor.Identifier.Text}(...) [implicit base()]", false));
                }
            }
        }
    }

    /// <summary>
    /// Appends <c>expression</c> (as <c>paramName: expression</c> where the site needs a named argument) at every
    /// given site. All edits to one document are applied in a single pass over the ORIGINAL tree, so nested sites
    /// (<c>new X(new X())</c>) and several sites in one file cannot invalidate each other. Returns the edited,
    /// formatted documents keyed by id.
    /// </summary>
    public static async Task<Dictionary<DocumentId, Document>> ApplyAsync(IReadOnlyList<(ConstructorCallSite Site, string Expression)> edits, string paramName, CancellationToken cancellationToken)
    {
        var result = new Dictionary<DocumentId, Document>();
        foreach (var group in edits.GroupBy(e => e.Site.Document.Id))
        {
            var document = group.First().Site.Document;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var byNode = group.ToDictionary(e => e.Site.Node, e => e);
            var newRoot = root.ReplaceNodes(byNode.Keys, (originalNode, rewritten) => Rewrite(rewritten, byNode[originalNode].Site, byNode[originalNode].Expression, paramName));
            var edited = document.WithSyntaxRoot(newRoot);
            result[document.Id] = await Formatter.FormatAsync(edited, Formatter.Annotation, cancellationToken: cancellationToken);
        }

        return result;
    }

    private static SyntaxNode Rewrite(SyntaxNode rewritten, ConstructorCallSite site, string expression, string paramName)
    {
        var argument = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(expression));
        if (site.UseNamedArgument)
        {
            argument = argument.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(paramName)));
        }

        switch (rewritten)
        {
            case BaseObjectCreationExpressionSyntax creation:
                var creationArguments = (creation.ArgumentList ?? SyntaxFactory.ArgumentList()).AddArguments(argument).WithAdditionalAnnotations(Formatter.Annotation);
                return creation.WithArgumentList(creationArguments);
            case ConstructorInitializerSyntax initializer:
                return initializer.WithArgumentList(initializer.ArgumentList.AddArguments(argument).WithAdditionalAnnotations(Formatter.Annotation));
            case ConstructorDeclarationSyntax constructor:
                // Implicit base(): add an explicit ": base(arg)" initializer, moving the parameter list's trailing
                // trivia (the line break before the body) onto the initializer so the body stays where it was.
                var trailing = constructor.ParameterList.GetTrailingTrivia();
                var baseInitializer = SyntaxFactory.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer, SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(argument)))
                    .WithLeadingTrivia(SyntaxFactory.Space)
                    .WithTrailingTrivia(trailing)
                    .WithAdditionalAnnotations(Formatter.Annotation);
                return constructor.WithParameterList(constructor.ParameterList.WithTrailingTrivia(SyntaxFactory.TriviaList())).WithInitializer(baseInitializer);
            default:
                return rewritten;
        }
    }

    private static int LineOf(SyntaxNode node, CancellationToken cancellationToken) =>
        node.SyntaxTree.GetLineSpan(node.Span, cancellationToken).StartLinePosition.Line + 1;

    private static string Truncate(string text)
    {
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= 100 ? collapsed : collapsed[..100] + "...";
    }
}

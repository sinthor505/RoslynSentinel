using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

public record ExtractMethodResult(bool Success, string? ErrorMessage, string? BeforeSnippet, string? CallSiteReplacement, string? ExtractedMethodText, string? UpdatedSourceContent);
public record UsingDirectiveInfo(string Name, bool IsStatic, string? Alias);
public record ResidualMention(FilePathWrapper FilePath, int LineNumber, string LineText);
public record ChangeSignatureResult(Dictionary<FilePathWrapper, string> Changes, List<SkippedCallSite> SkippedCallSites, string? Error = null);
public record RenameSymbolResult(string OldName, string NewName, Dictionary<FilePathWrapper, string> PendingChanges, string? Error = null, SymbolHandle? UpdatedHandle = null, List<ResidualMention>? ResidualMentions = null)
{
    public string ToToolResponse()
    {
        return System.Text.Json.JsonSerializer.Serialize(new { success = Error is null, oldName = OldName, newName = NewName, filesChanged = PendingChanges.Count, updatedHandle = UpdatedHandle is SymbolHandle h ? new { h.ProjectName, h.DocCommentId } : null, note = UpdatedHandle is null ? "updatedHandle is null - re-run LocateSymbol before further operations on this symbol." : null });
    }
}

public record ControlFlowSummary(string MethodName, bool AlwaysReturns, bool SometimesReturns, bool NeverReturns, List<string> ReturnPoints, List<string> ThrowPoints, int ExitPathCount);
public record DataFlowSummary(string MethodName, List<string> ReadBeforeAssignment, List<string> WrittenInside, List<string> ReadInside, List<string> WrittenOutside, List<string> CapturedVariables, List<string> DataFlowWarnings);
public record FormatHunk(int StartLine, int EndLine, List<string> ContextBefore, List<string> RemovedLines, List<string> AddedLines, List<string> ContextAfter);
public record FormatPreviewResult(bool Changed, int TotalHunks, List<FormatHunk> Hunks);
public class BasicRefactoringEngine
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ILogger<BasicRefactoringEngine> _logger;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
    private static readonly string[] separator = new[]
    {
        "\r\n",
        "\r",
        "\n"
    };

    public BasicRefactoringEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = new NullLogger<BasicRefactoringEngine>();
        _config = new SentinelConfiguration();
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public BasicRefactoringEngine(IWorkspaceManager workspaceManager, ILogger<BasicRefactoringEngine> logger, SentinelConfiguration config)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _config = config;
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public BasicRefactoringEngine(IWorkspaceManager workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ILogger<BasicRefactoringEngine> logger, SentinelConfiguration config)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _config = config;
        _symbolNavigationEngine = symbolNavigationEngine;
    }

    public async Task<DocumentEditResult> FormatDocumentAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: _workspaceManager is ISolutionProvider-typed (40 construction sites
        // across the solution as of 2026-09-25, including production callers in
        // AdvancedRefactoringTools.cs and RefactoringSignatureTools.cs/RefactoringStructuralTools.cs --
        // widening the constructor to IWorkspaceManager would force touching all of them). Every real
        // ISolutionProvider implementation (PersistentWorkspaceManager, FakeWorkspaceManager) also
        // implements IWorkspaceManager, so this cast is safe today. Cleanup target: widen the
        // constructor and drop this cast once BasicRefactoringEngine's caller list has been consolidated.
        // See docs/current/design_read_chokepoint.md.
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var formatted = await Formatter.FormatAsync(document, null, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formatted.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<ChangeSignatureResult> ChangeSignatureAsync(FilePathWrapper filePath, string methodName, IReadOnlyList<SignatureParameterSpec> parameters, CancellationToken cancellationToken = default)
    {
        var emptyResult = new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), new List<SkippedCallSite>());

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return emptyResult;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return emptyResult;
        }

        // BaseMethodDeclarationSyntax covers both ordinary methods and constructors, so a
        // constructor target (matched by its class name, same as GetMethodSource/LocateSymbol
        // already do for ctors) is found here too instead of silently falling through to
        // emptyResult - see blocking_error_changesignature_silent_noop_on_valid_constructor.md.
        var methodDecl = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(m => m switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text == methodName,
                ConstructorDeclarationSyntax ctor => ctor.Identifier.Text == methodName,
                _ => false
            });
        if (methodDecl == null)
        {
            return emptyResult;
        }

        // ChangeSignature only edits the single symbol it's invoked on - it never walks to/from
        // the other side of an interface boundary. Refuse early with a clear error naming the
        // interface/implementers instead of a half-applied change surfacing CS0535 later. See
        // blocking_error_changesignature_interface_implementation_no_cascade.md.
        var declaredSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (declaredSymbol != null)
        {
            if (declaredSymbol.ContainingType.TypeKind == TypeKind.Interface)
            {
                var implementations = await SymbolFinder.FindImplementationsAsync(declaredSymbol, solution, null, cancellationToken);
                var implementers = implementations.OfType<IMethodSymbol>()
                    .Select(m => m.ContainingType.ToDisplayString())
                    .Distinct()
                    .ToList();
                if (implementers.Count > 0)
                {
                    return new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), new List<SkippedCallSite>(),
                        $"'{methodName}' is declared on interface '{declaredSymbol.ContainingType.ToDisplayString()}', implemented by: " +
                        $"{string.Join(", ", implementers)}. ChangeSignature refuses this outright - it cannot cascade across the " +
                        "interface/implementer boundary, and calling it again on the interface or on any implementer hits this same " +
                        "refusal. Edit the interface's and every implementer's parameter list directly instead (ApplyDiff/ReplaceSnippet " +
                        "with validateOnApply:false on each), then run one terminal Build to converge.");
                }
            }
            else
            {
                var implementedInterfaceMembers = declaredSymbol.ContainingType.AllInterfaces
                    .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
                    .Where(im => SymbolEqualityComparer.Default.Equals(declaredSymbol.ContainingType.FindImplementationForInterfaceMember(im), declaredSymbol)
                              || declaredSymbol.ExplicitInterfaceImplementations.Contains(im, SymbolEqualityComparer.Default))
                    .Select(im => $"{im.ContainingType.ToDisplayString()}.{im.Name}")
                    .Distinct()
                    .ToList();
                if (implementedInterfaceMembers.Count > 0)
                {
                    return new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), new List<SkippedCallSite>(),
                        $"'{methodName}' implements {string.Join(", ", implementedInterfaceMembers)}. ChangeSignature refuses this " +
                        "outright - it cannot cascade across the interface/implementer boundary, and calling it again on the interface " +
                        "or on any implementer hits this same refusal. Edit the interface's and every implementer's parameter list " +
                        "directly instead (ApplyDiff/ReplaceSnippet with validateOnApply:false on each), then run one terminal Build " +
                        "to converge.");
                }
            }
        }

        var originalParams = methodDecl.ParameterList.Parameters.ToList();
        if (originalParams.Count == 0 || parameters.Count == 0)
        {
            return emptyResult;
        }

        // Validate: every ExistingParameterSpec.OriginalIndex must be in range and referenced at most once.
        var existingIndexes = parameters.OfType<ExistingParameterSpec>().Select(e => e.OriginalIndex).ToList();
        if (existingIndexes.Any(i => i < 0 || i >= originalParams.Count) || existingIndexes.Distinct().Count() != existingIndexes.Count)
        {
            return emptyResult;
        }

        var generator = SyntaxGenerator.GetGenerator(document);

        // Build the new parameter list (declaration side) and, in parallel, a per-call-site rewrite plan:
        // for each new-position slot, either "carry over the bound argument for original index N" or
        // "insert this literal default value expression" (for a NewParameterSpec).
        var newParameterSyntaxes = new List<ParameterSyntax>();
        foreach (var spec in parameters)
        {
            switch (spec)
            {
                case ExistingParameterSpec existing:
                    newParameterSyntaxes.Add(originalParams[existing.OriginalIndex].WithoutTrivia());
                    break;
                case NewParameterSpec added:
                    var defaultExpr = SyntaxFactory.ParseExpression(added.DefaultValueExpression);
                    var generated = (ParameterSyntax)generator.ParameterDeclaration(added.Name, SyntaxFactory.ParseTypeName(added.Type), defaultExpr);
                    newParameterSyntaxes.Add(generated);
                    break;
            }
        }

        var newParameterList = methodDecl.ParameterList.WithParameters(SyntaxFactory.SeparatedList(newParameterSyntaxes));

        var declarationEditor = await DocumentEditor.CreateAsync(document, cancellationToken);
        declarationEditor.ReplaceNode(methodDecl.ParameterList, newParameterList);
        var updatedDeclarationDoc = declarationEditor.GetChangedDocument();

        var pendingDocs = new Dictionary<FilePathWrapper, Document> { [filePath] = updatedDeclarationDoc };
        var skippedCallSites = new List<SkippedCallSite>();

        if (declaredSymbol != null)
        {
            var references = await SymbolFinder.FindReferencesAsync(declaredSymbol, solution, cancellationToken);
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    if (location.IsImplicit || location.Document?.FilePath == null)
                    {
                        continue;
                    }

                    var refDoc = location.Document;
                    var refDocPath = (FilePathWrapper)refDoc.FilePath!;
                    var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                    var refSemanticModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                    if (refRoot == null || refSemanticModel == null)
                    {
                        continue;
                    }

                    var span = location.Location.SourceSpan;
                    var refLineNumber = refRoot.SyntaxTree.GetLineSpan(span, cancellationToken: cancellationToken).StartLinePosition.Line + 1;
                    var token = refRoot.FindToken(span.Start);
                    // A constructor reference has no InvocationExpressionSyntax - its call sites are
                    // `new Foo(...)` (ObjectCreationExpressionSyntax) or `Foo x = new(...)`
                    // (ImplicitObjectCreationExpressionSyntax). BaseObjectCreationExpressionSyntax
                    // covers both; ArgumentList is nullable there (e.g. `new Foo { X = 1 }`), unlike
                    // InvocationExpressionSyntax's non-nullable one.
                    ExpressionSyntax? invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    invocation ??= token.Parent?.AncestorsAndSelf().OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault(o => o.ArgumentList != null);
                    if (invocation == null)
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Reference is not a simple invocation or object-creation expression with an argument list (e.g. method group, delegate conversion, or object-initializer-only construction)."));
                        continue;
                    }

                    var boundMethod = refSemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                    if (boundMethod == null)
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Could not _symbolNavigationEngine. Resolve the bound overload for this call site via the semantic model."));
                        continue;
                    }

                    // Map original parameter ordinal -> the argument syntax actually supplied at this call
                    // site (null if the argument was omitted, relying on the parameter's own default).
                    // The null-forgiving ArgumentList access is safe: the object-creation branch above
                    // only matches nodes whose ArgumentList is non-null.
                    var argsByOriginalIndex = new ArgumentSyntax?[originalParams.Count];
                    var arguments = invocation switch
                    {
                        InvocationExpressionSyntax inv => inv.ArgumentList.Arguments,
                        BaseObjectCreationExpressionSyntax obj => obj.ArgumentList!.Arguments,
                        _ => throw new NotSupportedException($"ChangeSignatureAsync: unhandled call-site expression type {invocation.GetType().Name}.")
                    };
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        var arg = arguments[i];
                        int originalIndex;
                        if (arg.NameColon != null)
                        {
                            var namedParam = boundMethod.Parameters.FirstOrDefault(p => p.Name == arg.NameColon.Name.Identifier.Text);
                            if (namedParam == null || namedParam.Ordinal >= originalParams.Count)
                            {
                                continue;
                            }
                            originalIndex = namedParam.Ordinal;
                        }
                        else
                        {
                            // Positional argument: bound parameter ordinal handles params-array expansion too
                            // (each expanded element binds to the same trailing params parameter).
                            if (i >= boundMethod.Parameters.Length)
                            {
                                continue;
                            }
                            var boundParam = boundMethod.Parameters[i];
                            if (boundParam.Ordinal >= originalParams.Count)
                            {
                                continue;
                            }
                            originalIndex = boundParam.Ordinal;
                        }
                        argsByOriginalIndex[originalIndex] = arg;
                    }

                    // Build the new argument list in new-parameter order. A kept parameter whose argument
                    // was omitted at this call site only stays omitted if every slot after it is also
                    // omitted-or-newly-added-with-default (i.e. it's still a valid trailing-optional gap);
                    // otherwise its original default value expression is materialized explicitly so the
                    // call keeps compiling positionally.
                    var slotHasExplicitArg = new bool[parameters.Count];
                    var slotArgExpr = new ArgumentSyntax?[parameters.Count];
                    for (int slot = 0; slot < parameters.Count; slot++)
                    {
                        if (parameters[slot] is ExistingParameterSpec existing)
                        {
                            var boundArg = argsByOriginalIndex[existing.OriginalIndex];
                            if (boundArg != null)
                            {
                                slotHasExplicitArg[slot] = true;
                                slotArgExpr[slot] = SyntaxFactory.Argument(boundArg.Expression);
                            }
                        }
                        else if (parameters[slot] is NewParameterSpec added)
                        {
                            // Always fill in the literal default value at existing call sites when a
                            // parameter is added (decided: no per-call flag).
                            slotHasExplicitArg[slot] = true;
                            slotArgExpr[slot] = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(added.DefaultValueExpression));
                        }
                    }

                    // Walk from the end: once we've seen an explicit arg, every earlier omitted slot must
                    // also be materialized (can't leave a positional gap before a later real argument).
                    bool sawExplicitFromEnd = false;
                    bool unresolvableGap = false;
                    for (int slot = parameters.Count - 1; slot >= 0; slot--)
                    {
                        if (slotHasExplicitArg[slot])
                        {
                            sawExplicitFromEnd = true;
                            continue;
                        }
                        if (!sawExplicitFromEnd)
                        {
                            continue; // trailing omitted slot: still fine to omit
                        }
                        if (parameters[slot] is ExistingParameterSpec existing)
                        {
                            var originalDefault = originalParams[existing.OriginalIndex].Default?.Value;
                            if (originalDefault == null)
                            {
                                skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Call site omits a required argument for a parameter that must be materialized in its new position, and the original parameter has no default value to fall back on."));
                                unresolvableGap = true;
                                break;
                            }
                            slotArgExpr[slot] = SyntaxFactory.Argument(originalDefault);
                        }
                    }
                    if (unresolvableGap)
                    {
                        continue;
                    }

                    var newArguments = SyntaxFactory.SeparatedList(slotArgExpr.Where(a => a != null).Select(a => a!));

                    if (!pendingDocs.TryGetValue(refDocPath, out var pendingRefDoc))
                    {
                        pendingRefDoc = refDoc;
                    }

                    var pendingRoot = await pendingRefDoc.GetSyntaxRootAsync(cancellationToken);
                    // Re-locate by span in whichever node kind the original call site was; an earlier
                    // edit to this same document may have shifted spans, so this can legitimately miss.
                    ExpressionSyntax? targetInvocation = invocation switch
                    {
                        InvocationExpressionSyntax => pendingRoot?.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(inv => inv.Span == invocation.Span),
                        BaseObjectCreationExpressionSyntax => pendingRoot?.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault(obj => obj.Span == invocation.Span),
                        _ => null
                    };
                    if (targetInvocation == null)
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Could not re-locate this call site in the pending document after an earlier edit."));
                        continue;
                    }

                    var callSiteEditor = await DocumentEditor.CreateAsync(pendingRefDoc, cancellationToken);
                    ExpressionSyntax rewrittenSite = targetInvocation switch
                    {
                        InvocationExpressionSyntax inv => inv.WithArgumentList(inv.ArgumentList.WithArguments(newArguments)),
                        BaseObjectCreationExpressionSyntax obj => obj.WithArgumentList(obj.ArgumentList!.WithArguments(newArguments)),
                        _ => throw new NotSupportedException($"ChangeSignatureAsync: unhandled call-site expression type {targetInvocation.GetType().Name}.")
                    };
                    callSiteEditor.ReplaceNode(targetInvocation, rewrittenSite);
                    pendingDocs[refDocPath] = callSiteEditor.GetChangedDocument();
                }
            }
        }

        // Format all changed documents.
        var result = new Dictionary<FilePathWrapper, string>();
        foreach (var kvp in pendingDocs)
        {
            var formatted = await Formatter.FormatAsync(kvp.Value, null, cancellationToken);
            result[kvp.Key] = (await formatted.GetTextAsync(cancellationToken)).ToString();
        }

        return new ChangeSignatureResult(result, skippedCallSites);
    }

    public async Task<Dictionary<FilePathWrapper, string>> MoveTypeToFileAsync(FilePathWrapper filePath, string typeName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var typeNode = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == typeName);
        if (typeNode == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var (newRoot, cleanTypeNode) = BuildSplitFileRoot(root!, typeNode);
        var ns = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns != null)
        {
            var newNs = ns is FileScopedNamespaceDeclarationSyntax ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name) : SyntaxFactory.NamespaceDeclaration(ns.Name);
            newRoot = newRoot.AddMembers(newNs.AddMembers(cleanTypeNode));
        }
        else
        {
            newRoot = newRoot.AddMembers(cleanTypeNode);
        }

        var sourceDirectory = Path.GetDirectoryName(document.FilePath ?? filePath);
        var newPath = string.IsNullOrEmpty(sourceDirectory) ? $"{typeName}.cs" : Path.Combine(sourceDirectory, $"{typeName}.cs");
        // Guard: if the type's name already matches the source file name, it's already in its own file -> nothing to move
        if (string.Equals(typeName, Path.GetFileNameWithoutExtension(document.Name), StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var updatedOrig = RemoveOrphanedRegionDirectives(root!.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia)!);
        var newDoc = document.Project.AddDocument($"{typeName}.cs", newRoot);
        var formattedNewDoc = await Formatter.FormatAsync(newDoc, null, cancellationToken);
        var newContent = (await formattedNewDoc.GetTextAsync(cancellationToken)).ToString();
        var updatedOrigDoc = document.WithSyntaxRoot(updatedOrig);
        var formattedOrigDoc = await Formatter.FormatAsync(updatedOrigDoc, null, cancellationToken);
        var updatedOrigContent = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();
        return new Dictionary<FilePathWrapper, string>
        {
            {
                filePath,
                updatedOrigContent
            },
            {
                newPath,
                newContent
            }
        };
    }

    private async Task<Dictionary<FilePathWrapper, string>> MoveAllTypesToFilesForDocumentAsync(Document document, CancellationToken cancellationToken = default)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var allTypes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Parent is CompilationUnitSyntax || t.Parent is BaseNamespaceDeclarationSyntax).ToList();
        if (allTypes.Count <= 1)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var fileBaseName = Path.GetFileNameWithoutExtension(document.FilePath ?? document.Name);
        var primaryType = allTypes.FirstOrDefault(t => t.Identifier.Text == fileBaseName) ?? allTypes[0];
        var typesToMove = allTypes.Where(t => t != primaryType).ToList();
        if (typesToMove.Count == 0)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var changes = new Dictionary<FilePathWrapper, string>();
        var sourceDirectory = Path.GetDirectoryName(document.FilePath) ?? "";
        foreach (var typeNode in typesToMove)
        {
            var ns = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var (newRoot, cleanTypeNode) = BuildSplitFileRoot(root, typeNode);
            if (ns != null)
            {
                var cleanNsName = SyntaxFactory.ParseName(ns.Name.ToString());
                var newNs = ns is FileScopedNamespaceDeclarationSyntax ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(cleanNsName) : SyntaxFactory.NamespaceDeclaration(cleanNsName);
                newRoot = newRoot.AddMembers(newNs.AddMembers(cleanTypeNode));
            }
            else
            {
                newRoot = newRoot.AddMembers(cleanTypeNode);
            }

            var typeName = typeNode.Identifier.Text;
            var newPath = string.IsNullOrEmpty(sourceDirectory) ? $"{typeName}.cs" : Path.Combine(sourceDirectory, $"{typeName}.cs");
            var newDoc = document.Project.AddDocument($"{typeName}.cs", newRoot);
            var formattedNewDoc = await Formatter.FormatAsync(newDoc, null, cancellationToken);
            changes[newPath] = (await formattedNewDoc.GetTextAsync(cancellationToken)).ToString();
        }

        var updatedRoot = RemoveOrphanedRegionDirectives(root.RemoveNodes(typesToMove, SyntaxRemoveOptions.KeepNoTrivia)!);
        var updatedOrigDoc = document.WithSyntaxRoot(updatedRoot);
        var formattedOrigDoc = await Formatter.FormatAsync(updatedOrigDoc, null, cancellationToken);
        changes[document.FilePath ?? document.Name] = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();
        return changes;
    }

    // Builds the compilation unit for a type being split into its own file, handling:
    // - extern alias declarations (not in root.Usings -> must be copied separately)
    // - global using aliases filtered out (project-scoped; duplicating them causes CS1537)
    // - file-scoped types promoted to internal (file modifier = visible only in declaring file)
    private static (CompilationUnitSyntax newRoot, BaseTypeDeclarationSyntax cleanNode) BuildSplitFileRoot(CompilationUnitSyntax root, BaseTypeDeclarationSyntax typeNode)
    {
        var cleanNode = typeNode.WithoutLeadingTrivia().WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        // Promote `file` modifier to `internal` -> the type is now in its own file and must be accessible
        if (cleanNode.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)))
        {
            var fileToken = cleanNode.Modifiers.First(m => m.IsKind(SyntaxKind.FileKeyword));
            var internalToken = SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithLeadingTrivia(fileToken.LeadingTrivia).WithTrailingTrivia(fileToken.TrailingTrivia);
            var newModifiers = cleanNode.Modifiers.Replace(fileToken, internalToken);
            cleanNode = (BaseTypeDeclarationSyntax)cleanNode.WithModifiers(newModifiers);
        }

        var cleanExterns = SyntaxFactory.List(root.Externs.Select(e => e.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
        // Exclude global using aliases -> they are project-scoped; duplicating them across split files causes CS1537
        var cleanUsings = SyntaxFactory.List(root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)).Select(u => u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
        var newRoot = SyntaxFactory.CompilationUnit().WithExterns(cleanExterns).WithUsings(cleanUsings);
        return (newRoot, cleanNode);
    }

    // Removes #endregion directives that have no matching #region (orphaned when types are removed from a file).
    private static CompilationUnitSyntax RemoveOrphanedRegionDirectives(CompilationUnitSyntax root)
    {
        var toRemove = new HashSet<SyntaxTrivia>();
        int depth = 0;
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia))
            {
                depth++;
            }
            else if (trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
            {
                if (depth == 0)
                {
                    toRemove.Add(trivia);
                }
                else
                {
                    depth--;
                }
            }
        }

        return toRemove.Count == 0 ? root : (CompilationUnitSyntax)root.ReplaceTrivia(toRemove, (_, _) => SyntaxFactory.Whitespace(""));
    }

    public async Task<Dictionary<FilePathWrapper, string>> MoveAllTypesToFilesAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        return await MoveAllTypesToFilesForDocumentAsync(document, cancellationToken);
    }

    public async Task<Dictionary<FilePathWrapper, string>> MoveAllTypesToFilesInProjectAsync(string projectName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolNotFoundException($"Project '{projectName}' not found.");
        var allChanges = new Dictionary<FilePathWrapper, string>();
        foreach (var document in project.Documents.Where(d => d.FilePath?.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, cancellationToken))
            {
                allChanges[kvp.Key] = kvp.Value;
            }
        }

        return allChanges;
    }

    public async Task<Dictionary<FilePathWrapper, string>> MoveAllTypesToFilesInSolutionAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var allChanges = new Dictionary<FilePathWrapper, string>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents).Where(d => d.FilePath?.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, cancellationToken))
            {
                allChanges[kvp.Key] = kvp.Value;
            }
        }

        return allChanges;
    }

    public async Task<Dictionary<FilePathWrapper, string>> ExtractInterfaceAsync(FilePathWrapper filePath, string className, string interfaceName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractInterface"))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath) ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // Extract public instance methods (exclude static, constructors)
        var methods = classNode.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));
        // Extract public non-static properties with at least a getter
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) && p.AccessorList != null);
        static SyntaxTriviaList MemberTrivia() => SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace("    "));
        var ifaceMethods = methods.Select(m => (MemberDeclarationSyntax)SyntaxFactory.MethodDeclaration(m.ReturnType.WithoutTrivia(), m.Identifier).WithTypeParameterList(m.TypeParameterList).WithParameterList(m.ParameterList).WithConstraintClauses(m.ConstraintClauses).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithLeadingTrivia(MemberTrivia()).WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed)));
        var ifaceProperties = properties.Select(p =>
        {
            // Build interface accessor list: only keep get/set/init that existed in source
            var accessors = p.AccessorList!.Accessors.Select(acc => SyntaxFactory.AccessorDeclaration(acc.Kind()).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            return (MemberDeclarationSyntax)SyntaxFactory.PropertyDeclaration(p.Type.WithoutTrivia(), p.Identifier).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors))).WithLeadingTrivia(MemberTrivia()).WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed));
        });
        var ifaceMembers = ifaceProperties.Concat(ifaceMethods).ToArray();
        var ifaceNode = SyntaxFactory.InterfaceDeclaration(interfaceName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddMembers(ifaceMembers);
        // Wrap in namespace + usings to produce a compilable file
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var cleanUsings = SyntaxFactory.List(root!.Usings.Select(u => u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
        CompilationUnitSyntax ifaceCompUnit;
        if (ns != null)
        {
            BaseNamespaceDeclarationSyntax newNs = ns is FileScopedNamespaceDeclarationSyntax ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name).AddMembers(ifaceNode) : SyntaxFactory.NamespaceDeclaration(ns.Name).AddMembers(ifaceNode);
            ifaceCompUnit = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newNs);
        }
        else
        {
            ifaceCompUnit = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(ifaceNode);
        }

        // Add interface to class's base list (only if not already present)
        var alreadyImplements = classNode.BaseList?.Types.Any(t => t.Type.ToString() == interfaceName) == true;
        var newClass = alreadyImplements ? classNode : classNode.AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName)));
        var updatedOrig = root.ReplaceNode(classNode, newClass);
        var ifacePath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", $"{interfaceName}.cs");
        // Format the interface file using NormalizeWhitespace for reliable member separation.
        // Formatter.FormatAsync with null workspace options can flatten all members onto one line.
        var ifaceContent = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(ifaceCompUnit, elasticTrivia: false).ToFullString();
        // Format the original file
        var origDoc = document.WithSyntaxRoot(updatedOrig);
        var formattedOrigDoc = await Formatter.FormatAsync(origDoc, null, cancellationToken);
        var origContent = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();
        return new Dictionary<FilePathWrapper, string>
        {
            {
                filePath,
                origContent
            },
            {
                ifacePath,
                ifaceContent
            }
        };
    }

    public async Task<RenameSymbolResult> RenameSymbolAsync(SymbolHandle handle, ISymbol symbol, string newName, CancellationToken cancellationToken = default)
    {
        static RenameSymbolResult Err(string msg, string n) => new("", n, new Dictionary<FilePathWrapper, string>(), msg);
        if (!_config.IsFeatureEnabled("Rename"))
        {
            return Err("Feature 'Rename' is disabled.", newName);
        }

        if (!symbol.Locations.Any(l => l.IsInSource))
        {
            return Err("Symbol is not defined in editable source. Rename is not available.", newName);
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var oldName = symbol.Name;
        // RenameInComments/RenameInStrings are deliberately left false: Roslyn's renamer would
        // otherwise rewrite any comment/string-literal token containing the old identifier as a
        // substring, even when it is not a genuine symbol reference (see
        // docs/current/blockers/blocking_error_renamesymbol_corrupts_unrelated_string_literals.md).
        var renameOptions = new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions
        {
            RenameInComments = false,
            RenameInStrings = false,
        };
        var updated = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(solution, symbol, renameOptions, newName, cancellationToken);
        var pendingChanges = new Dictionary<FilePathWrapper, string>();
        foreach (var pc in updated.GetChanges(solution).GetProjectChanges())
        {
            foreach (var docId in pc.GetChangedDocuments())
            {
                var newDoc = updated.GetDocument(docId)!;
                var filePth = new FilePathWrapper(newDoc.FilePath ?? newDoc.Name, _workspaceManager.GetSolutionRoot());
                pendingChanges[filePth] = (await newDoc.GetTextAsync(cancellationToken)).ToString();
            }
        }

        // Per-file diffs are not computed here -> the caller (RefactoringTools.RenameSymbol)
        // gets them from ValidateAndApplyAsync's returnDiff option, which builds them via the
        // canonical DiffEngine.CreateDiff. This used to duplicate that with a second, weaker
        // lockstep diff (ComputeRenameHunks); removed as part of the diff-logic consistency fix
        // (docs/current/codebase-consistency-audit-v1.md #3).
        var updatedHandle = await TryResolveUpdatedHandleAsync(handle, symbol, updated, newName, cancellationToken);
        var residualMentions = await FindResidualMentionsAsync(updated, oldName, cancellationToken);
        return new RenameSymbolResult(oldName, newName, pendingChanges, null, updatedHandle, residualMentions);
    }

    /// <summary>
    /// Whole-word scan of the post-rename solution for leftover mentions of <paramref name="oldName"/>
    /// that Roslyn's rename couldn't reach -> e.g. the old name embedded as a substring of an unrelated
    /// identifier (like a test method named ...OldNameDoesNotBlock), or occurrences in non-source files
    /// (docs, config) that aren't part of any project's compilation and so were never visited by the
    /// rename engine. RenameInComments/RenameInStrings are off (see RenameSymbolAsync), so comment and
    /// string-literal text is never rewritten by the rename itself and can still surface here.
    /// </summary>
    private async Task<List<ResidualMention>> FindResidualMentionsAsync(Solution updated, string oldName, CancellationToken cancellationToken)
    {
        var mentions = new List<ResidualMention>();
        var wordPattern = new Regex($@"\b{Regex.Escape(oldName)}\b", RegexOptions.Compiled);
        foreach (var project in updated.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null)
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken);
                var sourceText = text.ToString();
                if (!sourceText.Contains(oldName, StringComparison.Ordinal))
                {
                    continue;
                }

                var filePath = new FilePathWrapper(document.FilePath, _workspaceManager.GetSolutionRoot());
                var lines = sourceText.Split(separator, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (wordPattern.IsMatch(lines[i]))
                    {
                        mentions.Add(new ResidualMention(filePath, i + 1, lines[i].Trim()));
                    }
                }
            }
        }

        return mentions;
    }

    private static async Task<SymbolHandle?> TryResolveUpdatedHandleAsync(SymbolHandle handle, ISymbol originalSymbol, Solution updatedSolution, string newName, CancellationToken cancellationToken = default)
    {
        try
        {
            var originalLocation = originalSymbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (originalLocation is null)
            {
                return null;
            }

            var docId = updatedSolution.GetDocumentId(originalLocation.SourceTree!);
            if (docId is null)
            {
                return null;
            }

            var updatedDoc = updatedSolution.GetDocument(docId);
            if (updatedDoc is null)
            {
                return null;
            }

            var updatedRoot = await updatedDoc.GetSyntaxRootAsync(cancellationToken);
            var updatedModel = await updatedDoc.GetSemanticModelAsync(cancellationToken);
            if (updatedRoot is null || updatedModel is null)
            {
                return null;
            }

            var originalSpanStart = originalLocation.SourceSpan.Start;
            var candidates = updatedRoot.DescendantTokens().Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken) && t.Text == newName && Math.Abs(t.SpanStart - originalSpanStart) < 500);
            foreach (var token in candidates)
            {
                var parentNode = token.Parent;
                if (parentNode is null)
                {
                    continue;
                }

                var declaredSymbol = updatedModel.GetDeclaredSymbol(parentNode, cancellationToken);
                if (declaredSymbol is null)
                {
                    continue;
                }

                var newDocCommentId = declaredSymbol.GetDocumentationCommentId();
                if (newDocCommentId is not null)
                {
                    return new SymbolHandle(handle.ProjectName, newDocCommentId);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<DocumentEditResult> ConvertIndexerToMethodAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ConvertIndexerToMethod"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature is disabled."
            };
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var indexer = root?.DescendantNodes().OfType<IndexerDeclarationSyntax>().FirstOrDefault();
        if (indexer == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Indexer not found."
            };
        }

        var blockBody = indexer.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?.Body;
        var arrowExpr = indexer.ExpressionBody?.Expression ?? indexer.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?.ExpressionBody?.Expression;
        MethodDeclarationSyntax getter;
        if (blockBody != null)
        {
            getter = SyntaxFactory.MethodDeclaration(indexer.Type, "Get").WithModifiers(indexer.Modifiers).WithParameterList(SyntaxFactory.ParameterList(indexer.ParameterList.Parameters)).WithBody(blockBody);
        }
        else if (arrowExpr != null)
        {
            getter = SyntaxFactory.MethodDeclaration(indexer.Type, "Get").WithModifiers(indexer.Modifiers).WithParameterList(SyntaxFactory.ParameterList(indexer.ParameterList.Parameters)).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(arrowExpr)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        else
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Indexer not found."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Indexer converted to method.",
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, indexer, getter, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ExtractConstantAsync(FilePathWrapper filePath, string contextSnippet, string constantName, string visibility = "private", string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractConstant"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// ExtractConstant feature is disabled."
            };
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// Error: {snippetError}"
            };
        }

        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length));
        var literal = node.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault() ?? node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
        if (literal == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: literal not found."
            };
        }

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing type not found."
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax constType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(literal, cancellationToken);
            constType = typeInfo.Type != null ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString()) : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            constType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        var accessMod = visibility switch
        {
            "public" => SyntaxKind.PublicKeyword,
            "protected" => SyntaxKind.ProtectedKeyword,
            "internal" => SyntaxKind.InternalKeyword,
            _ => SyntaxKind.PrivateKeyword
        };
        var constDecl = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(constType).WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(constantName).WithInitializer(SyntaxFactory.EqualsValueClause(literal.WithoutTrivia()))))).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(accessMod), SyntaxFactory.Token(SyntaxKind.ConstKeyword)));
        var literalValue = literal.Token.Text;
        var allLiterals = containingType.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.Token.Text == literalValue).ToList();
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { containingType }.Concat(allLiterals));
        foreach (var lit in allLiterals)
        {
            var current = trackedRoot.GetCurrentNode(lit)!;
            trackedRoot = trackedRoot.ReplaceNode(current, SyntaxFactory.IdentifierName(constantName).WithTriviaFrom(current));
        }

        var currentType = trackedRoot.GetCurrentNode(containingType)!;
        var newType = currentType.WithMembers(((TypeDeclarationSyntax)currentType).Members.Insert(0, constDecl));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, trackedRoot, currentType, newType, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ExtractLocalVariableAsync(FilePathWrapper filePath, string contextSnippet, string? newVariableName = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractLocalVariable"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// ExtractLocalVariable feature is disabled."
            };
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            // ContextHelper's message ("contextSnippet not found"/"ambiguous (N matches)") is
            // necessarily generic -> ContextHelper only sees raw text offsets, it has no symbolName
            // or declaration list to enumerate the way _symbolNavigationEngine. ResolveMemberByNameOrSnippet's NearMissList
            // hint does, and this tool has no name argument at all (it targets an expression by its
            // literal text, not a named declaration) -> so there is no candidate set to report here
            // the way there is for the member/type _symbolNavigationEngine. Resolvers. Point the caller at the tools that
            // would show it real file content instead of leaving a bare message with nothing to act on.
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// Error: {snippetError} Re-check the snippet against GetMethodSource/GetFileOutline " + "output, or add lineBefore/lineAfter (the single verbatim line immediately before/after) to disambiguate."
            };
        }

        // Find the expression that matches the context snippet - use same logic as IntroduceVariableAsync.
        // Comparison is whitespace-collapsed (not raw-trimmed) so a caller who reproduces the exact
        // expression but with different internal spacing (e.g. around operators) still hits this exact
        // path instead of silently falling through to the ambiguous nearest-enclosing-expression guess
        // below -> that fallback exists for a genuinely partial contextSnippet, not a whitespace variant
        // of a complete one.
        var normalizedSnippet = System.Text.RegularExpressions.Regex.Replace(contextSnippet.Trim(), @"\s+", " ");
        var exactMatch = root.DescendantNodes().OfType<ExpressionSyntax>().Where(e => e.SpanStart == pos && System.Text.RegularExpressions.Regex.Replace(e.ToString().Trim(), @"\s+", " ") == normalizedSnippet).FirstOrDefault();
        // Fallback: contextSnippet didn't match a whole expression's text at this position -> walk from
        // the token at the position up to the nearest enclosing expression instead. This is inherently
        // ambiguous (a partial/short contextSnippet can _symbolNavigationEngine. Resolve to a larger expression than the caller
        // intended), so it only ever kicks in when the exact match above fails, and never overrides it.
        var expression = exactMatch ?? root.FindToken(pos).Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null)
        {
            // The snippet DID _symbolNavigationEngine. Resolve to a text position (pos, above) -> the failure is that no
            // ExpressionSyntax boundary aligns with it (e.g. the snippet spans a statement, a
            // keyword, or crosses an expression boundary). Report where it landed instead of a
            // bare "not found", since that position is real, already-available information -> a
            // caller reading only "expression not found" has no way to tell its snippet was even
            // located at all versus silently mismatched.
            var landedLine = text.Lines.GetLineFromPosition(pos).LineNumber + 1;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = $"// Cannot convert: contextSnippet was found at line {landedLine}, but it does not " + "align to a single, whole extractable expression there (it may span a statement, a keyword, " + "or cross an expression boundary). Narrow the snippet to exactly one expression's text."
            };
        }

        // Find the containing method
        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null || containingMethod.Body == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing method not found."
            };
        }

        // Find the containing statement and block
        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing statement not found."
            };
        }

        var containingBlock = containingStatement.Parent as BlockSyntax;
        if (containingBlock == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing block not found."
            };
        }

        // Skip if expression is already a standalone variable declaration
        if (containingStatement is LocalDeclarationStatementSyntax existingDecl && existingDecl.Declaration.Variables.Count == 1 && existingDecl.Declaration.Variables[0].Initializer?.Value?.IsEquivalentTo(expression) == true)
        {
            var existingName = existingDecl.Declaration.Variables[0].Identifier.Text;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = $"// '{existingName}' is already a local variable - nothing to extract."
            };
        }

        // Skip if expression has potential side effects (method calls, assignments)
        if (HasSideEffects(expression))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: expression has potential side effects."
            };
        }

        // Generate or validate variable name
        var varName = newVariableName;
        if (string.IsNullOrWhiteSpace(varName))
        {
            var baseName = InferVariableName(expression);
            varName = ContextHelper.GetUniqueVariableName(containingMethod.Body, baseName);
        }
        else
        {
            // Check if provided name conflicts
            varName = ContextHelper.GetUniqueVariableName(containingMethod.Body, varName);
        }

        // Infer type from semantic analysis if possible
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax? inferredType = null;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            if (typeInfo.Type != null)
            {
                inferredType = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
            }
        }

        // Create variable declaration with 'var' type
        var varDecl = SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")).WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(varName).WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())))));
        // Handle parenthesized expressions - replace outer parens too if the expression is the sole content
        SyntaxNode nodeToReplace = expression;
        if (expression.Parent is ParenthesizedExpressionSyntax parenParent && parenParent.Expression == expression)
        {
            nodeToReplace = parenParent;
        }

        var varRef = SyntaxFactory.IdentifierName(varName).WithTriviaFrom(nodeToReplace);
        // Track all nodes that need to be replaced
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { nodeToReplace, containingStatement, containingBlock });
        // Replace the expression with variable reference
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(nodeToReplace)!, varRef);
        // Get updated statement and block
        var currentStatement = newRoot.GetCurrentNode(containingStatement)!;
        var currentBlock = newRoot.GetCurrentNode(containingBlock)!;
        // Find the index where we insert the variable declaration
        var idx = currentBlock.Statements.IndexOf(currentStatement);
        if (idx < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: statement index not found."
            };
        }

        // Insert variable declaration before the statement
        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, newRoot, currentBlock, newBlock, cancellationToken)
        };
    }

    private static bool HasSideEffects(ExpressionSyntax expression)
    {
        // Check for method calls, assignments, and other side-effect operations
        var descendants = expression.DescendantNodesAndSelf();
        // Method invocations are risky unless they're property getters
        if (descendants.OfType<InvocationExpressionSyntax>().Any())
        {
            return true;
        }

        // Assignment expressions always have side effects
        if (descendants.OfType<AssignmentExpressionSyntax>().Any())
        {
            return true;
        }

        // Pre/post increment/decrement
        if (descendants.OfType<PostfixUnaryExpressionSyntax>().Any())
        {
            return true;
        }

        if (descendants.OfType<PrefixUnaryExpressionSyntax>().Any(p => p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return true;
        }

        return false;
    }

    private static string InferVariableName(ExpressionSyntax expression)
    {
        // Try to infer a meaningful variable name from the expression
        return expression switch
        {
            // Binary operations: "x + y" -> "sum", "x * y" -> "product"
            BinaryExpressionSyntax binary => binary.OperatorToken.Kind() switch
            {
                SyntaxKind.PlusToken => "sum",
                SyntaxKind.MinusToken => "difference",
                SyntaxKind.AsteriskToken => "product",
                SyntaxKind.SlashToken => "quotient",
                SyntaxKind.PercentToken => "remainder",
                SyntaxKind.GreaterThanToken or SyntaxKind.LessThanToken or SyntaxKind.GreaterThanEqualsToken or SyntaxKind.LessThanEqualsToken or SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken => "comparison",
                SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken => "condition",
                _ => "result"
            },
            // Member access: "obj.Property" -> "property"
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text.ToLowerInvariant(),
            // Identifier: "x" -> "x"
            IdentifierNameSyntax ident => ident.Identifier.Text,
            // String literals: "..." -> "text" or "str"
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) => "text",
            // Numeric literals
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.NumericLiteralExpression) => "value",
            // Default fallback
            _ => "extracted"
        };
    }

    public async Task<ControlFlowSummary> AnalyzeControlFlowAsync(FilePathWrapper filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        MethodDeclarationSyntax? method = null;
        if (contextSnippet != null)
        {
            var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out _, lineBefore, lineAfter);
            if (pos >= 0)
            {
                method = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0)).AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            }
        }

        method ??= root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method?.Body == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var flow = model.AnalyzeControlFlow(method.Body);
        if (flow == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var returnPoints = flow.ReturnStatements.Select(r => r.ToString().Trim()).ToList();
        var throwPoints = method.Body.DescendantNodes().OfType<ThrowStatementSyntax>().Select(t => t.ToString().Trim()).ToList();
        return new ControlFlowSummary(methodName, flow.EndPointIsReachable == false, flow.ReturnStatements.Length > 0, flow.ReturnStatements.Length == 0, returnPoints, throwPoints, flow.ExitPoints.Length);
    }

    public async Task<DataFlowSummary> AnalyzeDataFlowAsync(FilePathWrapper filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        MethodDeclarationSyntax? method = null;
        if (contextSnippet != null)
        {
            var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out _, lineBefore, lineAfter);
            if (pos >= 0)
            {
                method = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0)).AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            }
        }

        method ??= root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method?.Body == null)
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        }

        DataFlowAnalysis flow;
        try
        {
            flow = model.AnalyzeDataFlow(method.Body)!;
        }
        catch
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string> { "AnalyzeDataFlow failed: body may contain unsupported constructs." });
        }

        var warnings = new List<string>();
        var writtenOnly = flow.WrittenInside.Except(flow.ReadInside).ToList();
        foreach (var v in writtenOnly)
        {
            warnings.Add($"'{v.Name}' is written but never read - possible dead assignment.");
        }

        return new DataFlowSummary(methodName, flow.ReadOutside.Select(s => s.Name).ToList(), flow.WrittenInside.Select(s => s.Name).ToList(), flow.ReadInside.Select(s => s.Name).ToList(), flow.WrittenOutside.Select(s => s.Name).ToList(), flow.Captured.Select(s => s.Name).ToList(), warnings);
    }

    public async Task<DocumentEditResult> AddUsingDirectiveAsync(FilePathWrapper filePath, string namespaceName, bool simplifyExisting = false, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        // Idempotency check
        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        if (root.Usings.Any(u => u.Name?.ToString() == targetName))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// Using directive already exists.",
                UpdatedText = root.ToFullString()
            };
        }

        UsingDirectiveSyntax newUsing;
        if (namespaceName.StartsWith("static "))
        {
            newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space), null, SyntaxFactory.ParseName(namespaceName[7..])).WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }
        else
        {
            newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName)).WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }

        var annotation = new SyntaxAnnotation();
        var newRoot = root.AddUsings(newUsing.WithAdditionalAnnotations(annotation));
        var editedDocument = document.WithSyntaxRoot(newRoot);
        var formattedDoc = await Formatter.FormatAsync(editedDocument, annotation, cancellationToken: cancellationToken);
        if (simplifyExisting)
        {
            // Semantic-safe shortening of now-redundant fully-qualified names, via Roslyn's own
            // Simplifier (not text find/replace) -> it consults the semantic model per-node, so it
            // only reduces a qualified name when doing so introduces no ambiguity in this file.
            formattedDoc = await Simplifier.ReduceAsync(formattedDoc, Simplifier.Annotation, cancellationToken: cancellationToken);
            var simplifiedRoot = await formattedDoc.GetSyntaxRootAsync(cancellationToken);
            formattedDoc = await Formatter.FormatAsync(formattedDoc.WithSyntaxRoot(simplifiedRoot!.WithAdditionalAnnotations(Simplifier.Annotation)), cancellationToken: cancellationToken);
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<DocumentEditResult> RemoveUsingDirectiveAsync(FilePathWrapper filePath, string namespaceName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        var isStaticTarget = namespaceName.StartsWith("static ");
        var existing = root.Usings.FirstOrDefault(u => u.Name?.ToString() == targetName && u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) == isStaticTarget);
        if (existing == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Using directive '{namespaceName}' not found."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.RemoveNodeFormattedAsync(document, root, existing, cancellationToken)
        };
    }

    public async Task<List<UsingDirectiveInfo>> GetUsingDirectivesAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return [];
        }

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return [];
        }

        return root.Usings.Select(u => new UsingDirectiveInfo(Name: u.Name?.ToString() ?? "", IsStatic: u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword), Alias: u.Alias?.Name.ToString())).ToList();
    }

    public async Task<DocumentEditResult> AddSummaryCommentAsync(FilePathWrapper filePath, string targetName, string summaryText, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containingTypeName = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        return await AddSummaryCommentCoreAsync(document, filePath, targetName, summaryText, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken);
    }

    /// <summary>
    /// Core of <see cref="AddSummaryCommentAsync"/>, factored out to accept a <see cref="Document"/>
    /// directly instead of always resolving one from <c>_workspaceManager.GetSolutionAsync(ReadSource.Committed, ...)</c>.
    /// Lets a caller that's evolving its own local <see cref="Solution"/> fork across multiple edits
    /// (e.g. <c>CommentingEngine</c> commenting several members in the same file before a single
    /// disk write) reuse this logic without each call reading back the workspace's committed state.
    /// </summary>
    public async Task<DocumentEditResult> AddSummaryCommentCoreAsync(Document document, FilePathWrapper filePath, string targetName, string summaryText, string? contextSnippet, string? lineBefore, string? lineAfter, string? containingTypeName, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        SyntaxNode? target = null;
        try
        {
            var candidates = _symbolNavigationEngine.PreferNonInterfaceMember(_symbolNavigationEngine.PreferConstructorOverType(_symbolNavigationEngine.ResolveCandidates(root, sourceText, targetName, cancellationToken)));
            candidates = _symbolNavigationEngine.FilterByContainingType(candidates, containingTypeName);
            target = _symbolNavigationEngine.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (c, matches, failureMode) => _symbolNavigationEngine.BuildMemberHintForCandidates(c, matches, failureMode))?.Node;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        // baseIndent is derived first because docText bakes it into every line of the synthetic doc
        // comment (ParseMemberDeclaration has no notion of "the target's real indent" -> whatever
        // whitespace is in the string is exactly what ends up in the parsed trivia).
        var baseIndent = target.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
        var indentText = baseIndent != default ? baseIndent.ToFullString() : "";
        var normalizedSummary = NormalizeSummaryText(summaryText);
        var docText = BuildDocCommentText(target, indentText, normalizedSummary);
        var parsedMember = SyntaxFactory.ParseMemberDeclaration(docText);
        var docTrivia = parsedMember!.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)).ToList();

        // Keep the target's own real leading trivia that isn't the doc comment itself -> blank-line
        // separators from the previous member and any pre-existing "// planted scenario" line
        // comments -> in their original relative order and indentation, then splice the freshly
        // indented doc comment in immediately before the target's own base indent. A pre-existing
        // XML doc comment is dropped here deliberately: this is an Add operation and replaces
        // whatever summary was already present.
        var keptLeading = target.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();
        var rebuiltTrivia = new List<SyntaxTrivia>(keptLeading);
        rebuiltTrivia.AddRange(docTrivia);
        if (baseIndent != default)
        {
            rebuiltTrivia.Add(baseIndent);
        }

        // newTrivia's indentation is already correct by construction (derived from the target's own
        // base indent) -> do NOT run NormalizeWhitespace() on the result: it re-indents the whole tree
        // and, when a SingleLineDocumentationCommentTrivia is immediately followed by a plain
        // SingleLineCommentTrivia at the same level, inserts a spurious extra leading space before
        // the plain comment. Confirmed via a debug dump comparing pre/post-normalize output -> the
        // trivia list built above renders correctly before that call and only gets corrupted by it.
        var newTrivia = SyntaxFactory.TriviaList(rebuiltTrivia);
        var newRoot = root.ReplaceNode(target, target.WithLeadingTrivia(newTrivia));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    // Mirrors VS/Roslyn's native "///" auto-generate scaffold: <param>/<typeparam> per declared
    // parameter/type-parameter and <returns> when the member has a non-void return type, same as
    // typing "///" above the member would produce -> just with the tag bodies left empty rather than
    // filled in by hand, exactly like the native feature does (see docs history: GitHub Copilot,
    // not base VS, is what fills tag bodies with real prose -> base VS only emits the empty shape).
    // Only MethodDeclarationSyntax/ConstructorDeclarationSyntax carry a ParameterList; other taggable
    // member kinds (property, enum, enum member) fall through to a bare <summary>, same as before.
    private static string BuildDocCommentText(SyntaxNode target, string indentText, string normalizedSummary)
    {
        SeparatedSyntaxList<ParameterSyntax>? parameters = target switch
        {
            MethodDeclarationSyntax m => m.ParameterList.Parameters,
            ConstructorDeclarationSyntax c => c.ParameterList.Parameters,
            _ => null,
        };
        SeparatedSyntaxList<TypeParameterSyntax>? typeParameters = target is MethodDeclarationSyntax { TypeParameterList: { } tpl } ? tpl.Parameters : null;
        TypeSyntax? returnType = target is MethodDeclarationSyntax methodForReturn ? methodForReturn.ReturnType : null;

        var lines = new List<string> { $"{indentText}/// <summary>", $"{indentText}/// {normalizedSummary}", $"{indentText}/// </summary>" };

        if (typeParameters != null)
        {
            foreach (var tp in typeParameters.Value)
            {
                lines.Add($"{indentText}/// <typeparam name=\"{tp.Identifier.Text}\"></typeparam>");
            }
        }

        if (parameters != null)
        {
            foreach (var p in parameters.Value)
            {
                lines.Add($"{indentText}/// <param name=\"{p.Identifier.Text}\"></param>");
            }
        }

        // "void"/"Task" (no result) get no <returns> -> matches VS's own native behavior, which only
        // emits <returns> for a genuinely non-void, non-plain-Task return type.
        if (returnType != null && returnType is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }
            && returnType is not IdentifierNameSyntax { Identifier.Text: "Task" })
        {
            lines.Add($"{indentText}/// <returns></returns>");
        }

        lines.Add($"{indentText}void __Dummy__() {{}}");
        return string.Join("\n", lines);
    }

    // Callers sometimes pass summaryText already shaped as a doc comment (e.g. "/// <summary>...</summary>"
    // or "<summary>...</summary>") instead of plain prose, which would otherwise get wrapped a second time
    // into malformed nested <summary> tags. Strip any such wrapping so the caller's text is always
    // re-wrapped exactly once, regardless of the shape they supplied it in.
    private static string NormalizeSummaryText(string summaryText)
    {
        var lines = summaryText.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("///"))
            {
                trimmed = trimmed[3..].TrimStart();
            }

            return trimmed;
        }).Where(line => !line.Equals("<summary>", StringComparison.OrdinalIgnoreCase) && !line.Equals("</summary>", StringComparison.OrdinalIgnoreCase) && line.Length > 0);
        var joined = string.Join(" ", lines).Trim();
        if (joined.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
        {
            joined = joined[9..];
        }

        if (joined.EndsWith("</summary>", StringComparison.OrdinalIgnoreCase))
        {
            joined = joined[..^10];
        }

        return joined.Trim();
    }

    public async Task<DocumentEditResult> RemoveSummaryCommentAsync(FilePathWrapper filePath, string targetName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containingTypeName = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        SyntaxNode? target;
        try
        {
            var candidates = _symbolNavigationEngine.PreferNonInterfaceMember(_symbolNavigationEngine.PreferConstructorOverType(_symbolNavigationEngine.ResolveCandidates(root, sourceText, targetName, cancellationToken)));
            candidates = _symbolNavigationEngine.FilterByContainingType(candidates, containingTypeName);
            target = _symbolNavigationEngine.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (c, matches, failureMode) => _symbolNavigationEngine.BuildMemberHintForCandidates(c, matches, failureMode))?.Node;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        if (!target.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// No summary comment present.",
                UpdatedText = root.ToFullString()
            };
        }

        var stripped = target.GetLeadingTrivia().Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)).ToList();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, target, target.WithLeadingTrivia(SyntaxFactory.TriviaList(stripped)), cancellationToken, RoslynFormattingHelper.TriviaEditIntent.ReplaceLeading)
        };
    }

    public async Task<(EditOutcome Outcome, string? Message, string? SummaryText)> GetSummaryCommentAsync(FilePathWrapper filePath, string targetName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containingTypeName = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return (EditOutcome.DocumentNotFound, "// Document not found.", null);
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return (EditOutcome.CannotEdit, "// Cannot edit: syntax root not found.", null);
        }

        SyntaxNode? target;
        try
        {
            var candidates = _symbolNavigationEngine.PreferNonInterfaceMember(_symbolNavigationEngine.PreferConstructorOverType(_symbolNavigationEngine.ResolveCandidates(root, sourceText, targetName, cancellationToken)));
            candidates = _symbolNavigationEngine.FilterByContainingType(candidates, containingTypeName);
            target = _symbolNavigationEngine.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (c, matches, failureMode) => _symbolNavigationEngine.BuildMemberHintForCandidates(c, matches, failureMode))?.Node;
        }
        catch (InvalidOperationException ex)
        {
            return (EditOutcome.CannotEdit, ex.Message, null);
        }

        if (target == null)
        {
            return (EditOutcome.CannotEdit, "// Cannot edit: target not found.", null);
        }

        var docTrivia = target.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        if (docTrivia == default)
        {
            return (EditOutcome.NoChange, "// No summary comment present.", null);
        }

        var lines = docTrivia.ToFullString().Split('\n').Select(l => l.Trim().TrimStart('/').Trim()).Where(l => l.Length > 0 && !l.StartsWith("<summary>") && !l.StartsWith("</summary>")).ToList();
        return (EditOutcome.Modified, null, string.Join(" ", lines));
    }

    public async Task<DocumentEditResult> WrapInTryCatchAsync(FilePathWrapper filePath, int startLine, int endLine, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var tree = root.SyntaxTree;
        int StatementStartLine(StatementSyntax s) => tree.GetLineSpan(s.FullSpan, cancellationToken).StartLinePosition.Line + 1;
        int StatementEndLine(StatementSyntax s) => tree.GetLineSpan(s.FullSpan, cancellationToken).EndLinePosition.Line + 1;
        // Find the smallest block that fully contains the line range
        var block = root.DescendantNodes().OfType<BlockSyntax>().Where(b =>
        {
            var ls = tree.GetLineSpan(b.Span, cancellationToken);
            return ls.StartLinePosition.Line + 1 <= startLine && ls.EndLinePosition.Line + 1 >= endLine;
        }).OrderBy(b => b.Span.Length).FirstOrDefault();
        if (block == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not find a suitable block."
            };
        }

        var targeted = block.Statements.Where(s => StatementStartLine(s) <= endLine && StatementEndLine(s) >= startLine).ToList();
        if (targeted.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not find any statements in the specified range."
            };
        }

        var tryBlock = SyntaxFactory.Block(SyntaxFactory.List(targeted));
        var catchDecl = SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName(exceptionType), SyntaxFactory.Identifier(catchVariableName));
        StatementSyntax? catchStmt = null;
        if (catchBody != null)
        {
            catchStmt = SyntaxFactory.ParseStatement(catchBody);
        }

        var catchBlock = catchStmt != null ? SyntaxFactory.Block(catchStmt) : SyntaxFactory.Block();
        var catchClause = SyntaxFactory.CatchClause(catchDecl, null, catchBlock);
        var tryStatement = SyntaxFactory.TryStatement(tryBlock, SyntaxFactory.List([catchClause]), null);
        var newStatements = block.Statements.Select((s, i) =>
        {
            if (s == targeted[0])
            {
                return (StatementSyntax)tryStatement;
            }

            if (targeted.Contains(s))
            {
                return null;
            }

            return s;
        }).Where(s => s != null).Select(s => s!).ToList();
        var newBlock = block.WithStatements(SyntaxFactory.List(newStatements));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, block, newBlock, cancellationToken)
        };
    }

    /// <summary>
    /// Wraps a code snippet (identified via contextSnippet, lineBefore/lineAfter) in a try/catch block.
    /// Uses ContextHelper.FindSnippetPosition to locate the snippet, then wraps the enclosing statements.
    /// </summary>
    public async Task<DocumentEditResult> WrapInTryCatchAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore, string? lineAfter, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        try
        {
            // Find the snippet position
            var snippetPos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
            var tree = root.SyntaxTree;
            // Convert position to line numbers
            var linePos = sourceText.Lines.GetLinePosition(snippetPos);
            var startLine = linePos.Line + 1;
            var endLine = startLine; // Start with the snippet's line
            // Find the enclosing block and targeted statements
            var block = root.DescendantNodes().OfType<BlockSyntax>().Where(b =>
            {
                var ls = tree.GetLineSpan(b.Span, cancellationToken);
                return ls.StartLinePosition.Line + 1 <= startLine && ls.EndLinePosition.Line + 1 >= endLine;
            }).OrderBy(b => b.Span.Length).FirstOrDefault();
            if (block == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = "// Could not find a suitable block for the snippet."
                };
            }

            // StatementStartLine and StatementEndLine are unused
            // int StatementStartLine(StatementSyntax s) => tree.GetLineSpan(s.FullSpan, cancellationToken).StartLinePosition.Line + 1;
            // int StatementEndLine(StatementSyntax s) => tree.GetLineSpan(s.FullSpan, cancellationToken).EndLinePosition.Line + 1;

            // Find statements that contain or overlap the snippet
            var targeted = block.Statements.Where(s => s.Span.Contains(snippetPos)).ToList();
            if (targeted.Count == 0)
            {
                // Fallback: try to find statement that contains the snippet position
                targeted = block.Statements.Where(s => s.Span.Start <= snippetPos && s.Span.End >= snippetPos).ToList();
            }

            if (targeted.Count == 0)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = "// Could not find any statements containing the snippet."
                };
            }

            // Build the try/catch as in the original
            var tryBlock = SyntaxFactory.Block(SyntaxFactory.List(targeted));
            var catchDecl = SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName(exceptionType), SyntaxFactory.Identifier(catchVariableName));
            StatementSyntax? catchStmt = null;
            if (catchBody != null)
            {
                catchStmt = SyntaxFactory.ParseStatement(catchBody);
            }

            var catchBlockStmt = catchStmt != null ? SyntaxFactory.Block(catchStmt) : SyntaxFactory.Block();
            var catchClause = SyntaxFactory.CatchClause(catchDecl, null, catchBlockStmt);
            var tryStatement = SyntaxFactory.TryStatement(tryBlock, SyntaxFactory.List([catchClause]), null);
            var newStatements = block.Statements.Select((s, i) =>
            {
                if (s == targeted[0])
                {
                    return (StatementSyntax)tryStatement;
                }

                if (targeted.Contains(s))
                {
                    return null;
                }

                return s;
            }).Where(s => s != null).Select(s => s!).ToList();
            var newBlock = block.WithStatements(SyntaxFactory.List(newStatements));
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, block, newBlock, cancellationToken)
            };
        }
        catch (ToolException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ContextSnippet error: {ex.Message}"
            };
        }
    }

    public async Task<DocumentEditResult> WrapInRegionAsync(FilePathWrapper filePath, int startLine, int endLine, string regionName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var text = await document.GetTextAsync(cancellationToken);
        var lines = text.Lines;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            int lineNumber = i + 1; // 1-based
            if (lineNumber == startLine)
            {
                sb.AppendLine($"#region {regionName}");
            }

            sb.AppendLine(lines[i].ToString());
            if (lineNumber == endLine)
            {
                sb.AppendLine("#endregion");
            }
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = sb.ToString()
        };
    }

    /// <summary>
    /// Wraps a code snippet (identified via contextSnippet, lineBefore/lineAfter) in a #region block.
    /// Uses ContextHelper.FindSnippetPosition to locate the snippet, then derives the line number.
    /// </summary>
    public async Task<DocumentEditResult> WrapInRegionAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore, string? lineAfter, string regionName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var text = await document.GetTextAsync(cancellationToken);
        try
        {
            // Find the snippet position
            var snippetPos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
            // Convert position to line number
            var linePos = text.Lines.GetLinePosition(snippetPos);
            var startLine = linePos.Line + 1;
            var endLine = startLine; // Start and end at the snippet's line
            var lines = text.Lines;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                int lineNumber = i + 1; // 1-based
                if (lineNumber == startLine)
                {
                    sb.AppendLine($"#region {regionName}");
                }

                sb.AppendLine(lines[i].ToString());
                if (lineNumber == endLine)
                {
                    sb.AppendLine("#endregion");
                }
            }

            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = sb.ToString()
            };
        }
        catch (ToolException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ContextSnippet error: {ex.Message}"
            };
        }
    }

    public async Task<DocumentEditResult> UpdateXmlDocsFromSignatureAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse document."
            };
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Method not found."
            };
        }

        var currentParams = method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToList();
        // Find the XML doc comment trivia preceding the method
        var xmlTrivia = method.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        // If no XML doc exists, generate one
        if (xmlTrivia == default)
        {
            // Generate new XML doc
            var lines = new List<XmlNodeSyntax>();
            // Add <summary> tag
            lines.Add(SyntaxFactory.XmlElement(SyntaxFactory.XmlElementStartTag(SyntaxFactory.XmlName("summary")), SyntaxFactory.SingletonList<XmlNodeSyntax>(SyntaxFactory.XmlText("Description of " + methodName)), SyntaxFactory.XmlElementEndTag(SyntaxFactory.XmlName("summary"))));
            // Add <param> tags
            foreach (var param in currentParams)
            {
                lines.Add(SyntaxFactory.XmlElement(SyntaxFactory.XmlElementStartTag(SyntaxFactory.XmlName("param")).AddAttributes(SyntaxFactory.XmlNameAttribute(param)), SyntaxFactory.SingletonList<XmlNodeSyntax>(SyntaxFactory.XmlText($"The {param} parameter.")), SyntaxFactory.XmlElementEndTag(SyntaxFactory.XmlName("param"))));
            }

            // Create the documentation comment
            var newXmlDoc = SyntaxFactory.DocumentationCommentTrivia(SyntaxKind.MultiLineDocumentationCommentTrivia, SyntaxFactory.List(lines.Cast<XmlNodeSyntax>()));
            var newTrivia = SyntaxFactory.Trivia(newXmlDoc);
            var newLeadingTrivia = method.GetLeadingTrivia().Insert(0, newTrivia);
            var newMethod = method.WithLeadingTrivia(newLeadingTrivia);
            var newRoot = root.ReplaceNode(method, newMethod);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = newRoot.ToFullString()
            };
        }

        // XML doc exists -> update it
        var xmlDoc = xmlTrivia.GetStructure() as Microsoft.CodeAnalysis.CSharp.Syntax.DocumentationCommentTriviaSyntax;
        if (xmlDoc == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse XML documentation."
            };
        }

        // Find existing param tags
        var existingParamTags = xmlDoc.Content.OfType<XmlElementSyntax>().Where(e => e.StartTag.Name.LocalName.Text == "param").ToList();
        var existingParamNames = existingParamTags.Select(e => e.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.Text ?? "").Where(n => !string.IsNullOrEmpty(n)).ToHashSet();
        // Params to add (in current signature but not in XML)
        var toAdd = currentParams.Except(existingParamNames).ToList();
        // Param tags to remove (in XML but not in current signature)
        var toRemove = existingParamTags.Where(e =>
        {
            var name = e.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.Text;
            return name != null && !currentParams.Contains(name);
        }).ToList();
        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// No changes needed."
            };
        }

        // Build updated XML doc content
        var updatedContent = xmlDoc.Content.ToList();
        // Remove stale param tags
        foreach (var staleTag in toRemove)
        {
            updatedContent.Remove(staleTag);
        }

        // Add missing param tags
        foreach (var paramName in toAdd)
        {
            var newTag = SyntaxFactory.XmlElement(SyntaxFactory.XmlElementStartTag(SyntaxFactory.XmlName("param")).AddAttributes(SyntaxFactory.XmlNameAttribute(paramName)), SyntaxFactory.SingletonList<XmlNodeSyntax>(SyntaxFactory.XmlText($"The {paramName} parameter.")), SyntaxFactory.XmlElementEndTag(SyntaxFactory.XmlName("param")));
            updatedContent.Add(newTag);
        }

        var updatedXmlDoc = xmlDoc.WithContent(SyntaxFactory.List(updatedContent));
        var updatedTrivia = SyntaxFactory.Trivia(updatedXmlDoc);
        var updatedLeadingTrivia = method.GetLeadingTrivia().Replace(xmlTrivia, updatedTrivia);
        var updatedMethod = method.WithLeadingTrivia(updatedLeadingTrivia);
        var updatedRoot = root.ReplaceNode(method, updatedMethod);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedRoot.ToFullString()
        };
    }

    /// <summary>
    /// Returns a preview of what FormatDocument would change without applying changes.
    /// Shows changed line ranges with ±3 lines of context (like a unified diff).
    /// Returns Changed=false and an empty hunks list if the file is already formatted correctly.
    /// </summary>
    public async Task<FormatPreviewResult> FormatDocumentPreviewAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new FormatPreviewResult(false, 0, new List<FormatHunk>());
        }

        var originalText = (await document.GetTextAsync(cancellationToken)).ToString();
        var formattedDoc = await Formatter.FormatAsync(document, null, cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        if (originalText == formattedText)
        {
            return new FormatPreviewResult(false, 0, new List<FormatHunk>());
        }

        // Split on the same 3-way line-ending set as DiffEngine/ComputeRenameHunks (not just '\n')
        // so a trailing '\r' on Windows-style line endings isn't folded into line content.
        var originalLines = originalText.Split(separator, StringSplitOptions.None);
        var formattedLines = formattedText.Split(separator, StringSplitOptions.None);
        var hunks = ComputeFormatHunks(originalLines, formattedLines, contextLines: 3);
        return new FormatPreviewResult(true, hunks.Count, hunks);
    }

    // Intentionally NOT delegated to DiffEngine.CreateDiff: FormatHunk groups adjacent changed
    // lines into merged ranges with leading/trailing context on each side, a shape CreateDiff's
    // flat per-line unified-diff string doesn't produce and existing tests assert on directly
    // (RegressionTests/NewToolTests/BugFixTests). See docs/current/codebase-consistency-audit-v1.md
    // #3 -> consolidating this would need a real reshape of FormatHunk/FormatPreviewResult and its
    // consumers, not just a call-site swap, so it's left as its own diff implementation for now.
    private static List<FormatHunk> ComputeFormatHunks(string[] original, string[] formatted, int contextLines)
    {
        var changedLines = new List<int>();
        var minLen = Math.Min(original.Length, formatted.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (original[i] != formatted[i])
            {
                changedLines.Add(i);
            }
        }

        for (int i = minLen; i < Math.Max(original.Length, formatted.Length); i++)
        {
            changedLines.Add(i);
        }

        if (changedLines.Count == 0)
        {
            return new List<FormatHunk>();
        }

        // Group nearby changed lines into hunks
        var groups = new List<(int start, int end)>();
        int gStart = changedLines[0], gEnd = changedLines[0];
        for (int k = 1; k < changedLines.Count; k++)
        {
            if (changedLines[k] - gEnd <= (contextLines * 2) + 1)
            {
                gEnd = changedLines[k];
            }
            else
            {
                groups.Add((gStart, gEnd));
                gStart = gEnd = changedLines[k];
            }
        }

        groups.Add((gStart, gEnd));
        var hunks = new List<FormatHunk>();
        foreach (var (start, end) in groups)
        {
            var ctxBeforeStart = Math.Max(0, start - contextLines);
            var ctxBefore = Enumerable.Range(ctxBeforeStart, start - ctxBeforeStart).Select(l => original[l]).ToList();
            var removed = Enumerable.Range(start, Math.Min(end + 1, original.Length) - start).Select(l => original[l]).ToList();
            var added = Enumerable.Range(start, Math.Min(end + 1, formatted.Length) - start).Select(l => formatted[l]).ToList();
            var ctxAfter = Enumerable.Range(end + 1, contextLines).Where(l => l < original.Length).Select(l => original[l]).ToList();
            hunks.Add(new FormatHunk(StartLine: start + 1, EndLine: end + 1, ContextBefore: ctxBefore, RemovedLines: removed, AddedLines: added, ContextAfter: ctxAfter));
        }

        return hunks;
    }

    /// <summary>
    /// Inverts a boolean variable or parameter name and its usages.
    /// </summary>
    public async Task<DocumentEditResult> InvertBooleanAsync(FilePathWrapper filePath, string boolName, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        // Requires solution-wide reference tracking, logic implemented in AdvancedLogicEngine.
        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// InvertBooleanAsync is not implemented."
        };
    }
}
/// <summary>
/// One entry in the desired end-state parameter list for <see cref="BasicRefactoringEngine.ChangeSignatureAsync"/>.
/// The list's position expresses the new order; an original parameter simply omitted from the list is a removal.
/// </summary>
public abstract record SignatureParameterSpec;

/// <summary>Keep the parameter that was originally at <paramref name="OriginalIndex"/> (0-based), at this new position.</summary>
public sealed record ExistingParameterSpec(int OriginalIndex) : SignatureParameterSpec;

/// <summary>
/// Insert a brand-new parameter at this position. <paramref name="DefaultValueExpression"/> is literal C# source
/// text (e.g. "TimeSpan.FromSeconds(30)"), parsed via SyntaxFactory.ParseExpression, used both as the
/// declaration's default value and as the literal fill-in argument inserted at every existing call site.
/// </summary>
public sealed record NewParameterSpec(string Name, string Type, string DefaultValueExpression) : SignatureParameterSpec;// Added by AddTopLevelType (expected - used for diagnostics)

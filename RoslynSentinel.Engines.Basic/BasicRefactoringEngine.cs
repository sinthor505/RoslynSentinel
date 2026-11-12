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
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Basic;

public record ExtractMethodResult(bool IsError, string? ErrorMessage, string? BeforeSnippet, string? CallSiteReplacement, string? ExtractedMethodText, string? UpdatedSourceContent) : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.Exception : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => ErrorMessage;
}
public record UsingDirectiveInfo(string Name, bool IsStatic, string? Alias);
/// <summary>Where a residual mention of a renamed symbol sits; precedence is PathLike, then NonSource, then (for .cs) Comment/String/Code.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ResidualBucket
{
    /// <summary>In a .cs file, outside comments and string-like literals.</summary>
    Code,
    /// <summary>In a .cs comment, including structured doc comments.</summary>
    Comment,
    /// <summary>In a .cs string, char, interpolated-string-text, raw-string or utf8 literal token.</summary>
    String,
    /// <summary>Immediately followed by '/' or '\' (a folder or file path); any file type.</summary>
    PathLike,
    /// <summary>In a non-.cs file (project, props, json, md, ps1, sln).</summary>
    NonSource
}
/// <summary>MatchColumn is the 0-based index of the match inside the trimmed <paramref name="LineText"/> (0 when unknown).</summary>
public record ResidualMention(FilePathWrapper FilePath, int LineNumber, string LineText, ResidualBucket Bucket = ResidualBucket.Code, int MatchColumn = 0);
/// <summary>
/// Outcome of ChangeSignatureAsync. In oldSignature keepAsDeprecatedWrapper mode, <paramref name="SkippedCallSites"/> are the call sites LEFT on the
/// wrapper (not an error), <paramref name="MigratedCallSites"/> are the ones rewritten to the new signature (Line is 1-based in the ORIGINAL text),
/// <paramref name="WrapperSignature"/> is the kept old signature and <paramref name="ReplacementSignature"/> the new one. All three are null when the old signature was removed.
/// <paramref name="OldSignatureOutcome"/> is what actually happened to the old signature (remove or keepAsDeprecatedWrapper, never auto): oldSignature auto resolves to one
/// of them from the planned call sites before anything is written, so a dry run and the real apply report the same outcome. Null on an error.
/// <paramref name="ReflectiveConstructionSites"/> (constructor changes only; null otherwise) lists calls such as ActivatorUtilities.CreateInstance that name the changed
/// constructor's type and pass its arguments for runtime resolution: they are neither rewritten nor skipped call sites (they compile and fail at runtime), so they are
/// reported for a manual check. LineNumber is 1-based in the ORIGINAL text; Reason names the API.
/// </summary>
public record ChangeSignatureResult(Dictionary<FilePathWrapper, string> Changes, List<SkippedCallSite> SkippedCallSites, string? Error = null, List<RewrittenCallSite>? MigratedCallSites = null, string? WrapperSignature = null, string? ReplacementSignature = null, ChangeSignatureOldSignature? OldSignatureOutcome = null, List<SkippedCallSite>? ReflectiveConstructionSites = null) : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    // Every Error this engine sets is a refusal (interface member, unrewritable call sites), never an internal fault.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.TargetIneligible : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;
}
public record RenameSymbolResult(string OldName, string NewName, Dictionary<FilePathWrapper, string> PendingChanges, string? Error = null, SymbolHandle? UpdatedHandle = null, List<ResidualMention>? ResidualMentions = null) : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.Exception : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;

    public string ToToolResponse()
    {
        return System.Text.Json.JsonSerializer.Serialize(new { success = Error is null, oldName = OldName, newName = NewName, filesChanged = PendingChanges.Count, updatedHandle = UpdatedHandle is SymbolHandle h ? new { h.ProjectName, h.DocCommentId } : null, note = UpdatedHandle is null ? $"updatedHandle is null - re-run {ToolCallHint.Call("Search", ("mode", "symbol"))} before further operations on this symbol." : null });
    }
}

public record ControlFlowSummary(string MethodName, bool AlwaysReturns, bool SometimesReturns, bool NeverReturns, List<string> ReturnPoints, List<string> ThrowPoints, int ExitPathCount);
public record DataFlowSummary(string MethodName, List<string> ReadBeforeAssignment, List<string> WrittenInside, List<string> ReadInside, List<string> WrittenOutside, List<string> CapturedVariables, List<string> DataFlowWarnings);
public record FormatHunk(int StartLine, int EndLine, List<string> ContextBefore, List<string> RemovedLines, List<string> AddedLines, List<string> ContextAfter);
public record FormatPreviewResult(bool Changed, int TotalHunks, List<FormatHunk> Hunks) : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => false;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => Changed ? ResultStatus.Success : ResultStatus.AlreadyInState;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => null;
}
public class BasicRefactoringEngine
{
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
    }

    public BasicRefactoringEngine(IWorkspaceManager workspaceManager, ILogger<BasicRefactoringEngine> logger, SentinelConfiguration config)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _config = config;
    }

    // DEAD CODE (owner decision 2026-10-22): FormatDocumentAsync has no production caller; it is kept only for tests.
    // Do not re-investigate, re-raise in reviews, or delete it without the owner's go-ahead.
    public async Task<DocumentEditResult> FormatDocumentAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: _workspaceManager is ISolutionProvider-typed (40 construction sites
        // across the solution as of 2026-09-25; the only production construction site is
        // AdvancedRefactoringTools.cs:42. FormatDocumentAsync itself has no production caller (tests only).
        // widening the constructor to IWorkspaceManager would force touching every construction site). Every real
        // ISolutionProvider implementation (PersistentWorkspaceManager, FakeWorkspaceManager) also
        // implements IWorkspaceManager, so this cast is safe today. Cleanup target: widen the
        // constructor and drop this cast once BasicRefactoringEngine's caller list has been consolidated.
        // See docs/current/design/design_read_chokepoint.md.
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var formatted = await Formatter.FormatAsync(document, null, cancellationToken);
        var formattedText = (await formatted.GetTextAsync(cancellationToken)).ToString();
        var currentText = (await document.GetTextAsync(cancellationToken)).ToString();
        if (string.Equals(formattedText, currentText, StringComparison.Ordinal))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.AlreadyInTargetState,
                UpdatedText = currentText,
                FilePath = filePath
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = formattedText
        };
    }

    /// <summary>
    /// The method or constructor ChangeSignatureAsync would edit for these arguments (null when none matches), resolved exactly as ChangeSignatureAsync does:
    /// an ambiguous name or an unmatched snippet throws the usual typed error.
    /// </summary>
    private static async Task<BaseMethodDeclarationSyntax?> FindChangeSignatureTargetAsync(Document document, CompilationUnitSyntax root, FilePathWrapper filePath, string methodName, string? contextSnippet, string? lineBefore, string? lineAfter, CancellationToken cancellationToken)
    {
        // BaseMethodDeclarationSyntax covers both ordinary methods and constructors, so a
        // constructor target (matched by its class name, same as GetMethodSource/LocateSymbol
        // already do for ctors) is found here too instead of silently falling through to
        // emptyResult - see blocking_error_changesignature_silent_noop_on_valid_constructor.md.
        var methodMatches = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
            .Where(m => m switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text == methodName,
                ConstructorDeclarationSyntax ctor => ctor.Identifier.Text == methodName,
                _ => false
            })
            .ToList();
        if (string.IsNullOrWhiteSpace(contextSnippet) && string.IsNullOrWhiteSpace(lineBefore) && string.IsNullOrWhiteSpace(lineAfter))
        {
            // No snippet: an ambiguous name stays Ambiguous, and the retry hint names the real narrowing parameter.
            return RoslynSentinel.Common.NameTargetLookup.RequireSingle(methodMatches, "Method", methodName,
                retryCall: firstLine => ToolCallHint.Call("ChangeSignature", ("filePath", filePath.ToString()), ("methodName", methodName), ("contextSnippet", firstLine)));
        }

        // A snippet selects the overload/constructor whose declaration contains it; none or several is the usual snippet error, never a first pick.
        var sourceText = await document.GetTextAsync(cancellationToken);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, methodName, cancellationToken)
            .Where(c => c.Kind is CandidateKind.Method or CandidateKind.Constructor).ToList();
        return SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
            (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode))?.Node as BaseMethodDeclarationSyntax;
    }

    /// <summary>
    /// How many parameters the method or constructor ChangeSignatureAsync would edit declares today (null when the target is not found), read from the committed
    /// solution. Call it before the change is applied; the tool uses it to say whether a change removed a parameter.
    /// </summary>
    public async Task<(int ParameterCount, bool IsConstructor)?> GetChangeSignatureTargetShapeAsync(FilePathWrapper filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);
        if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
        {
            return null;
        }

        var target = await FindChangeSignatureTargetAsync(document, root, filePath, methodName, contextSnippet, lineBefore, lineAfter, cancellationToken);
        return target is null ? null : (target.ParameterList.Parameters.Count, target is ConstructorDeclarationSyntax);
    }

    public async Task<ChangeSignatureResult> ChangeSignatureAsync(FilePathWrapper filePath, string methodName, IReadOnlyList<SignatureParameterSpec> parameters, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default, ChangeSignatureOldSignature oldSignature = ChangeSignatureOldSignature.auto)
    {
        // oldSignature auto is resolved to remove or keepAsDeprecatedWrapper (wrapperMode, below) once the call sites are planned and BEFORE anything is
        // written, so a dry run and the real apply (same planning pass) report the same outcome.
        var keepRequested = oldSignature == ChangeSignatureOldSignature.keepAsDeprecatedWrapper;
        var autoRequested = oldSignature == ChangeSignatureOldSignature.auto;
        // Every early exit names its reason: an empty result with no Error reads to the caller as a successful no-op.
        static ChangeSignatureResult Refuse(string error) => new(new Dictionary<FilePathWrapper, string>(), new List<SkippedCallSite>(), error);

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return Refuse($"Could not load the syntax tree or semantic model for '{filePath}'. Nothing was changed.");
        }

        var methodDecl = await FindChangeSignatureTargetAsync(document, root, filePath, methodName, contextSnippet, lineBefore, lineAfter, cancellationToken);

        if (methodDecl == null)
        {
            if (root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.Text == methodName && type.ParameterList != null))
            {
                return Refuse($"'{methodName}' is a type with a primary constructor, and ChangeSignature does not edit primary constructors (there is no constructor declaration to rewrite or to put a wrapper beside). Nothing was changed. Edit the type's parameter list and its construction sites directly, or add an ordinary constructor and change that one."); // hint-ok: names the tool in prose, not a call to make
            }

            var fileMethodNames = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(methodNode => methodNode.Identifier.Text);
            var fileCtorNames = root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Select(ctorNode => ctorNode.Identifier.Text);
            return Refuse($"Method or constructor '{methodName}' was not found in '{filePath}' (a constructor is matched by its class name). Nothing was changed. " + DeclarationNotFoundDetail.Build("method or constructor", "methodName", methodName, fileMethodNames.Concat(fileCtorNames), filePath.ToString()));
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
        if (methodDecl is ConstructorDeclarationSyntax staticConstructor && staticConstructor.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return Refuse($"'{methodName}' is a static constructor: it takes no parameters, so there is no signature to change. Nothing was changed. Narrow the target to an instance constructor with contextSnippet.");
        }

        if (originalParams.Count == 0)
        {
            // Thrown, not returned: a deliberate refusal of this target maps to TargetIneligible (nothing has been planned or written yet).
            throw new ToolTargetIneligibleException($"'{methodName}' declares no parameters, and ChangeSignature cannot add a parameter to a parameterless method. Nothing was changed. Edit the declaration and its call sites directly."); // hint-ok: names the tool in prose, not a call to make
        }

        if (parameters.Count == 0)
        {
            throw new ToolTargetIneligibleException("The parameters list is empty, and ChangeSignature does not remove every parameter in one call. Nothing was changed. Edit the declaration and its call sites directly."); // hint-ok: names the tool in prose, not a call to make
        }

        // Validate: every ExistingParameterSpec.OriginalIndex must be in range and referenced at most once.
        var existingIndexes = parameters.OfType<ExistingParameterSpec>().Select(e => e.OriginalIndex).ToList();
        if (existingIndexes.Any(i => i < 0 || i >= originalParams.Count) || existingIndexes.Distinct().Count() != existingIndexes.Count)
        {
            return Refuse($"parameters contains an originalIndex that is out of range or used more than once. '{methodName}' has {originalParams.Count} parameter(s), so valid originalIndex values are 0..{originalParams.Count - 1}, each used at most once. Nothing was changed.");
        }

        // A removed parameter (an existing one absent from the new list) the body still uses would leave CS0103 behind: refuse before anything is planned, in every oldSignature mode.
        if (RefuseWhenRemovedParameterIsStillUsed(semanticModel, methodDecl, originalParams, existingIndexes, methodName, cancellationToken) is { } removedParameterStillUsed)
        {
            throw new ToolTargetIneligibleException(removedParameterStillUsed);
        }

        // remove and auto hand every call site the new parameter's defaultValue as its fill-in argument, so a new parameter without one is refused up front
        // (keepAsDeprecatedWrapper refuses it in Prepare with the wrapper-specific text).
        if (!keepRequested && parameters.OfType<NewParameterSpec>().FirstOrDefault(s => string.IsNullOrWhiteSpace(s.DefaultValueExpression)) is { } noDefault)
        {
            return Refuse($"New parameter '{noDefault.Name}' has no defaultValue, and every migrated call site receives it as its fill-in argument. Nothing was changed. Pass defaultValue for '{noDefault.Name}'.");
        }

        // keepAsDeprecatedWrapper and auto: every wrapper refusal (partial/extern constructor, virtual/override/abstract/interface member, a new parameter without a default
        // value, old and new signature colliding) is decided here, before any call site is planned or anything is written. An explicit keep refuses on it;
        // auto only remembers it (wrapperRefusal): it matters only if some call site cannot be migrated, and then the change is refused like remove.
        var oldSignatureText = string.Empty;
        var newSignatureText = string.Empty;
        string? wrapperRefusal = null;
        if (oldSignature != ChangeSignatureOldSignature.remove)
        {
            var prepared = ChangeSignatureWrapper.Prepare(semanticModel, methodDecl, declaredSymbol, parameters, originalParams, methodName, autoMode: autoRequested);
            if (prepared.Error is not null)
            {
                if (keepRequested)
                {
                    return Refuse(prepared.Error);
                }

                wrapperRefusal = prepared.Error;
            }
            else
            {
                (oldSignatureText, newSignatureText) = (prepared.OldSignature, prepared.NewSignature);
            }
        }

        // A new parameter's defaultValue that names something (_logger, Instance, DateTime.UtcNow) is copied into every call site as its fill-in argument, so it must bind in that
        // site's scope; a site where it does not is unmigratable for this change (skipped below). One that is not a compile-time constant cannot be a declaration default either.
        var newDefaults = ChangeSignatureWrapper.AnalyzeNewDefaults(semanticModel, methodDecl, parameters);
        var scopeCheckedDefaults = newDefaults.Where(d => d.NeedsScopeCheck).ToList();
        var nonConstantDefaultSlots = newDefaults.Where(d => !d.IsConstant).Select(d => d.Slot).ToHashSet();

        // Single pass: every edit in a file (the declaration's parameter list and all call sites) is planned against
        // ONE original root per file path and applied with one ReplaceNodes, so no edit can shift the span of another
        // (re-locating a call site by span after an earlier edit missed the second site in the same document).
        // The declaration's file is keyed by the document's own FilePath (not the caller-supplied filePath, which may be
        // a bare file name) so call sites in the same file land on the same entry.
        FilePathWrapper declPath = document.FilePath ?? filePath;
        var rootByPath = new Dictionary<FilePathWrapper, SyntaxNode> { [declPath] = root };
        var documentByPath = new Dictionary<FilePathWrapper, Document> { [declPath] = document };
        var callSitePlansByPath = new Dictionary<FilePathWrapper, Dictionary<SyntaxNode, List<(int SrcIndex, ExpressionSyntax? Literal)>>> { [declPath] = new() };
        var skippedCallSites = new List<SkippedCallSite>();
        var migratedCallSites = new List<RewrittenCallSite>();
        // File and line of every call site this change plans to rewrite; with skippedCallSites (the ones it leaves alone) it is the plan the call-binding check holds the result to.
        var plannedCallSiteLines = new List<(string FilePath, int Line)>();

        if (declaredSymbol != null)
        {
            var references = await SymbolFinder.FindReferencesAsync(declaredSymbol, solution, cancellationToken);
            var referenceGroups = new List<List<(Document Document, Microsoft.CodeAnalysis.Text.TextSpan Span)>>();
            foreach (var reference in references)
            {
                referenceGroups.Add(reference.Locations
                    .Where(location => !location.IsImplicit && location.Document?.FilePath != null)
                    .Select(location => (location.Document, location.Location.SourceSpan))
                    .ToList());
            }

            if (declaredSymbol.MethodKind == MethodKind.Constructor)
            {
                // SymbolFinder reports no location for a target-typed `new(...)` call of the constructor, so those calls are found by binding them.
                referenceGroups.Add(await FindTargetTypedConstructorCallsAsync(solution, declaredSymbol, referenceGroups.SelectMany(group => group), cancellationToken));
            }

            foreach (var referenceGroup in referenceGroups)
            {
                foreach (var (refDoc, span) in referenceGroup)
                {
                    var refDocPath = (FilePathWrapper)refDoc.FilePath!;
                    if (!rootByPath.TryGetValue(refDocPath, out var refRoot))
                    {
                        var loadedRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                        if (loadedRoot == null)
                        {
                            continue;
                        }

                        refRoot = loadedRoot;
                        rootByPath[refDocPath] = refRoot;
                        documentByPath[refDocPath] = refDoc;
                        callSitePlansByPath[refDocPath] = new();
                    }

                    var refSemanticModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                    if (refSemanticModel == null)
                    {
                        continue;
                    }

                    var refLineNumber = refRoot.SyntaxTree.GetLineSpan(span, cancellationToken: cancellationToken).StartLinePosition.Line + 1;
                    var token = refRoot.FindToken(span.Start);
                    // A constructor reference has no InvocationExpressionSyntax - its call sites are
                    // `new Foo(...)` (ObjectCreationExpressionSyntax) or `Foo x = new(...)`
                    // (ImplicitObjectCreationExpressionSyntax). BaseObjectCreationExpressionSyntax
                    // covers both; ArgumentList is nullable there (e.g. `new Foo { X = 1 }`), unlike
                    // InvocationExpressionSyntax's non-nullable one.
                    // The nearest call-like ancestor decides: a constructor initializer (`: base(...)` / `: this(...)`) and a primary-constructor base call
                    // (`class D(int x) : B(x)`) are call sites too. The reference must be that call's callee (or its `new` / type), otherwise it is an
                    // argument such as a method group (`Run(Save)`) and the nearest call is some other member's.
                    var nearestCall = FindNearestCall(token);
                    if (nearestCall is InvocationExpressionSyntax possibleNameof && IsNameofInvocation(possibleNameof, refSemanticModel))
                    {
                        // nameof(Save) is a compile-time string, not a call: there is no argument list to migrate and nothing to skip, so the reference is left as written.
                        continue;
                    }

                    SyntaxNode? invocation = nearestCall != null && IsCalleeToken(nearestCall, token) ? nearestCall : null;
                    if (invocation == null)
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Reference is not a simple invocation or object-creation expression with an argument list (e.g. method group, delegate conversion, or object-initializer-only construction)."));
                        continue;
                    }

                    var boundMethod = refSemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                    if (boundMethod == null)
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Could not resolve the bound overload for this call site via the semantic model."));
                        continue;
                    }

                    // Map original parameter ordinal -> the argument syntax actually supplied at this call
                    // site (null if the argument was omitted, relying on the parameter's own default).
                    // The null-forgiving ArgumentList access is safe: the object-creation branch above
                    // only matches nodes whose ArgumentList is non-null.
                    // A reduced extension call (receiver.M(a)) has no argument for the `this` receiver and numbers boundMethod.Parameters without it,
                    // so every bound ordinal is shifted by one to land on the declaration's own parameter index (the receiver is parameter 0).
                    // The receiver stays first and unchanged in a reduced call; any other treatment of it cannot be written in that form, so the site is skipped.
                    var receiverOffset = boundMethod.ReducedFrom != null ? 1 : 0;
                    if (receiverOffset == 1 && !(parameters.Count > 0 && parameters[0] is ExistingParameterSpec { OriginalIndex: 0 }))
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Call is an extension method called on its receiver, and the change moves or removes the receiver (the 'this' parameter), so the call cannot keep that form."));
                        continue;
                    }

                    var argsByOriginalIndex = new ArgumentSyntax?[originalParams.Count];
                    // Expanded params call: the positional arguments past the last declared parameter all belong to the params parameter
                    // (the first one binds to it by ordinal below). They are carried right after that parameter's slot, in source order.
                    var paramsExtraIndexes = new List<int>();
                    var unbindableArgument = false;
                    var arguments = invocation switch
                    {
                        InvocationExpressionSyntax inv => inv.ArgumentList.Arguments,
                        BaseObjectCreationExpressionSyntax obj => obj.ArgumentList!.Arguments,
                        ConstructorInitializerSyntax init => init.ArgumentList.Arguments,
                        PrimaryConstructorBaseTypeSyntax primaryBase => primaryBase.ArgumentList.Arguments,
                        _ => throw new NotSupportedException($"ChangeSignatureAsync: unhandled call-site expression type {invocation.GetType().Name}.")
                    };
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        var arg = arguments[i];
                        int originalIndex;
                        if (arg.NameColon != null)
                        {
                            var namedParam = boundMethod.Parameters.FirstOrDefault(p => p.Name == arg.NameColon.Name.Identifier.Text);
                            if (namedParam == null || namedParam.Ordinal + receiverOffset >= originalParams.Count)
                            {
                                continue;
                            }
                            originalIndex = namedParam.Ordinal + receiverOffset;
                        }
                        else
                        {
                            // Positional argument: bound parameter ordinal handles params-array expansion too
                            // (each expanded element binds to the same trailing params parameter).
                            if (i >= boundMethod.Parameters.Length)
                            {
                                // More positional arguments than declared parameters: the trailing arguments of an expanded params call.
                                if (boundMethod.Parameters.Length > 0 && boundMethod.Parameters[boundMethod.Parameters.Length - 1].IsParams
                                    && boundMethod.Parameters[boundMethod.Parameters.Length - 1].Ordinal + receiverOffset < originalParams.Count)
                                {
                                    paramsExtraIndexes.Add(i);
                                    continue;
                                }

                                skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Call passes more positional arguments than the bound method declares parameters and is not a plain expanded params call, so its arguments cannot be re-ordered safely."));
                                unbindableArgument = true;
                                break;
                            }
                            var boundParam = boundMethod.Parameters[i];
                            if (boundParam.Ordinal + receiverOffset >= originalParams.Count)
                            {
                                continue;
                            }
                            originalIndex = boundParam.Ordinal + receiverOffset;
                        }
                        argsByOriginalIndex[originalIndex] = arg;
                    }

                    if (unbindableArgument)
                    {
                        continue;
                    }

                    // Extra params arguments need the params parameter's own first argument to follow; without it (a named argument claimed the slot
                    // or the call is malformed) their place in the new list is not known, so the call is left alone instead of guessed at.
                    var paramsOrdinal = originalParams.Count - 1;
                    if (paramsExtraIndexes.Count > 0 && argsByOriginalIndex[paramsOrdinal] == null)
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, "Call passes extra positional arguments to a params parameter whose own argument could not be identified, so its arguments cannot be re-ordered safely."));
                        continue;
                    }

                    var unboundDefault = scopeCheckedDefaults
                        .Select(d => (Default: d, Problem: ChangeSignatureWrapper.DefaultBindingProblem(refSemanticModel, invocation.SpanStart, d.Expression, d.ParameterType, ChangeSignatureWrapper.IsInstanceContext(invocation))))
                        .FirstOrDefault(check => check.Problem is not null);
                    if (unboundDefault.Problem is not null)
                    {
                        skippedCallSites.Add(new SkippedCallSite(refDocPath, refLineNumber, $"defaultValue '{unboundDefault.Default.Text}' of new parameter '{unboundDefault.Default.Name}' does not bind in this scope: {unboundDefault.Problem}."));
                        continue;
                    }

                    // Build the new argument list in new-parameter order. A kept parameter whose argument
                    // was omitted at this call site only stays omitted if every slot after it is also
                    // omitted-or-newly-added-with-default (i.e. it's still a valid trailing-optional gap);
                    // otherwise its original default value expression is materialized explicitly so the
                    // call keeps compiling positionally.
                    var slotHasExplicitArg = new bool[parameters.Count];
                    var slotArgExpr = new ArgumentSyntax?[parameters.Count];
                    // A reduced extension call has no argument slot for the receiver (checked above to stay parameter 0).
                    for (int slot = receiverOffset; slot < parameters.Count; slot++)
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
                            // (A defaultValue that names something was checked to bind at this site above.)
                            slotHasExplicitArg[slot] = true;
                            slotArgExpr[slot] = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(added.DefaultValueExpression));
                        }
                    }

                    // Walk from the end: once we've seen an explicit arg, every earlier omitted slot must
                    // also be materialized (can't leave a positional gap before a later real argument).
                    bool sawExplicitFromEnd = false;
                    bool unresolvableGap = false;
                    for (int slot = parameters.Count - 1; slot >= receiverOffset; slot--)
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

                    // Record the new argument list as a plan over the ORIGINAL node: each slot either carries over the
                    // argument at a given index of the (possibly already-rewritten) current argument list, or inserts a
                    // literal expression. Resolving carried arguments from the rewritten node at apply time keeps edits to
                    // call sites nested inside an argument (e.g. Process(Process(...), ...)).
                    var slotPlan = new List<(int SrcIndex, ExpressionSyntax? Literal)>();
                    for (int slot = receiverOffset; slot < parameters.Count; slot++)
                    {
                        var slotArg = slotArgExpr[slot];
                        if (slotArg == null)
                        {
                            continue;
                        }

                        if (parameters[slot] is ExistingParameterSpec boundSpec && argsByOriginalIndex[boundSpec.OriginalIndex] is { } boundSrc)
                        {
                            slotPlan.Add((arguments.IndexOf(boundSrc), null));
                            if (boundSpec.OriginalIndex == paramsOrdinal)
                            {
                                // The rest of an expanded params call follows its slot; when the params parameter is removed this branch is never
                                // reached, so all of its arguments go with it.
                                slotPlan.AddRange(paramsExtraIndexes.Select(index => (index, (ExpressionSyntax?)null)));
                            }
                        }
                        else
                        {
                            slotPlan.Add((-1, slotArg.Expression));
                        }
                    }

                    if (callSitePlansByPath[refDocPath].TryAdd(invocation, slotPlan))
                    {
                        plannedCallSiteLines.Add((refDocPath.ToString(), refLineNumber));
                        if (oldSignature != ChangeSignatureOldSignature.remove)
                        {
                            migratedCallSites.Add(ChangeSignatureWrapper.DescribeMigratedCall(refDocPath, refLineNumber, invocation, arguments, slotPlan));
                        }
                    }
                }
            }
        }

        // Refuse if any call sites could not be rewritten: report them and do not apply a partial
        // change that would leave the code not compiling. The caller must fix or remove the skipped
        // call sites first, or split the change into smaller steps. Wrapper mode keeps them on the deprecated wrapper instead.
        // The old-signature decision, made here from the planned call sites and before any write (so a dry run decides identically): keepAsDeprecatedWrapper always
        // keeps the wrapper; auto keeps it only when some call site cannot be migrated AND a wrapper is possible; otherwise the old signature is removed.
        var wrapperMode = keepRequested || (autoRequested && skippedCallSites.Count > 0 && wrapperRefusal is null);
        if (skippedCallSites.Count > 0 && !wrapperMode)
        {
            var lines = new List<string> { $"Nothing was changed. Could not rewrite {skippedCallSites.Count} call site(s)" };
            var count = 0;
            foreach (var site in skippedCallSites)
            {
                if (count >= 5)
                {
                    break;
                }
                var fileName = Path.GetFileName(site.FilePath);
                lines.Add($"{fileName}:{site.LineNumber} ({site.Reason})");
                count++;
            }
            if (skippedCallSites.Count > 5)
            {
                lines.Add($"... and {skippedCallSites.Count - 5} more.");
            }
            if (wrapperRefusal is not null)
            {
                lines.Add("oldSignature auto cannot keep the old signature for them because " + wrapperRefusal);
            }

            lines.Add("Fix or remove those call sites first, or split the change.");
            var errorMessage = string.Join("; ", lines);
            return new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), skippedCallSites, errorMessage);
        }

        // The declaration side. A kept wrapper declares every new parameter REQUIRED (defaultValue only fills call sites and the wrapper body): an optional one
        // would make the new signature compete with the wrapper for old-shaped calls. Without a wrapper a new parameter stays optional unless a later parameter has
        // no default (an optional parameter before a required one is CS1737, and every call site receives the value anyway). Existing defaults in front of a
        // required parameter are dropped on the new member in wrapper mode (migrated call sites pass them explicitly; the wrapper keeps the old list and its defaults).
        var newParameterSyntaxes = new ParameterSyntax[parameters.Count];
        var laterParameterRequired = false;
        for (var slot = parameters.Count - 1; slot >= 0; slot--)
        {
            if (parameters[slot] is ExistingParameterSpec keptSpec)
            {
                var kept = originalParams[keptSpec.OriginalIndex].WithoutTrivia();
                if (kept.Default == null)
                {
                    laterParameterRequired |= !kept.Modifiers.Any(SyntaxKind.ParamsKeyword);
                }
                else if ((wrapperMode || nonConstantDefaultSlots.Count > 0) && laterParameterRequired)
                {
                    kept = kept.WithDefault(null);
                }

                newParameterSyntaxes[slot] = kept;
            }
            else if (parameters[slot] is NewParameterSpec addedSpec)
            {
                var addedRequired = wrapperMode || laterParameterRequired || nonConstantDefaultSlots.Contains(slot);
                // Built with its own whitespace (type, space, name, " = ", default), so a list that keeps its layout needs no formatter pass over it.
                var addedParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(addedSpec.Name))
                    .WithType(SyntaxFactory.ParseTypeName(addedSpec.Type.Trim()).WithTrailingTrivia(SyntaxFactory.Space));
                newParameterSyntaxes[slot] = addedRequired
                    ? addedParameter
                    : addedParameter.WithDefault(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.Token(SyntaxKind.EqualsToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.ParseExpression(addedSpec.DefaultValueExpression.Trim())));
                laterParameterRequired |= addedRequired;
            }
        }

        // A multi-line parameter list keeps its layout (RebuildListKeepingLayout); a single-line one is rebuilt as before.
        var newParameterList = methodDecl.ParameterList.WithParameters(RebuildListKeepingLayout(
            methodDecl.ParameterList.Parameters,
            newParameterSyntaxes.Select((syntax, slot) => (syntax, parameters[slot] is ExistingParameterSpec existingSpec ? existingSpec.OriginalIndex : -1)).ToList()));

        static SeparatedSyntaxList<ArgumentSyntax> RebuildArguments(SeparatedSyntaxList<ArgumentSyntax> current, List<(int SrcIndex, ExpressionSyntax? Literal)> plan)
        {
            // A carried argument keeps its ref/out/in keyword; dropping it would not compile. A multi-line call keeps its layout (RebuildListKeepingLayout).
            return RebuildListKeepingLayout(current, plan.Select(p => p.Literal != null
                ? (SyntaxFactory.Argument(p.Literal), -1)
                : (SyntaxFactory.Argument(null, current[p.SrcIndex].RefKindKeyword, current[p.SrcIndex].Expression), p.SrcIndex)).ToList());
        }

        // A multi-line list (the declaration's parameter list, a `: this(...)` / `: base(...)` argument list) keeps its layout from RebuildListKeepingLayout and every token it
        // adds carries its own whitespace, so it is not annotated for formatting: formatting a list that spans lines would also move a body brace on its closing line onto its
        // own line. A single-line list still takes the formatter (its separators need it).
        var parameterListKeepsLayout = ListKeepsLayout(methodDecl.ParameterList.Parameters);
        static ConstructorInitializerSyntax RebuildInitializer(ConstructorInitializerSyntax initializer, List<(int SrcIndex, ExpressionSyntax? Literal)> plan)
        {
            var keepsLayout = ListKeepsLayout(initializer.ArgumentList.Arguments);
            var rebuilt = initializer.WithArgumentList(initializer.ArgumentList.WithArguments(RebuildArguments(initializer.ArgumentList.Arguments, plan)));
            return keepsLayout ? rebuilt : rebuilt.WithAdditionalAnnotations(Formatter.Annotation);
        }

        // Apply every planned edit per document in one ReplaceNodes pass, then format.
        var result = new Dictionary<FilePathWrapper, string>();
        // The same texts keyed by the documents' own paths (result is keyed by the caller-supplied path for the declaration file), for the trial solution.
        var trialChanges = new Dictionary<FilePathWrapper, string>();
        var wrapperAnnotation = new SyntaxAnnotation();
        var memberAnnotation = new SyntaxAnnotation();
        foreach (var (path, plans) in callSitePlansByPath)
        {
            var nodes = new List<SyntaxNode>(plans.Keys);
            if (path.Equals(declPath))
            {
                nodes.Add(methodDecl.ParameterList);
            }

            var newRoot = rootByPath[path].ReplaceNodes(nodes, (original, rewritten) => rewritten switch
            {
                ParameterListSyntax => parameterListKeepsLayout ? newParameterList.WithAdditionalAnnotations(memberAnnotation) : newParameterList.WithAdditionalAnnotations(Formatter.Annotation, memberAnnotation),
                InvocationExpressionSyntax inv => inv.WithArgumentList(inv.ArgumentList.WithArguments(RebuildArguments(inv.ArgumentList.Arguments, plans[original]))).WithAdditionalAnnotations(Formatter.Annotation),
                BaseObjectCreationExpressionSyntax obj => obj.WithArgumentList(obj.ArgumentList!.WithArguments(RebuildArguments(obj.ArgumentList.Arguments, plans[original]))).WithAdditionalAnnotations(Formatter.Annotation),
                ConstructorInitializerSyntax init => RebuildInitializer(init, plans[original]),
                PrimaryConstructorBaseTypeSyntax primaryBase => primaryBase.WithArgumentList(primaryBase.ArgumentList.WithArguments(RebuildArguments(primaryBase.ArgumentList.Arguments, plans[original]))).WithAdditionalAnnotations(Formatter.Annotation),
                _ => rewritten
            });
            var eol = await _workspaceManager.ResolveEolAsync(documentByPath[path], cancellationToken);
            if (wrapperMode && path.Equals(declPath))
            {
                // The wrapper (old signature, [Obsolete], forwards to the new member) lands right after the member in the SAME edit set, so it is
                // applied, validated and rolled back together with the call-site edits. A blank line before it and a line end after it come from the text.
                var newMember = newRoot.GetAnnotatedNodes(memberAnnotation).Select(n => n.Parent).OfType<BaseMethodDeclarationSyntax>().SingleOrDefault();
                var wrapperText = ChangeSignatureWrapper.BuildWrapperText(methodDecl, declaredSymbol!, parameters, originalParams, newSignatureText, eol);
                if (newMember == null || SyntaxFactory.ParseMemberDeclaration(eol + wrapperText + eol) is not BaseMethodDeclarationSyntax wrapperNode)
                {
                    return Refuse($"Could not generate the deprecated wrapper for '{methodName}'. Nothing was changed. Retry with {ToolCallHint.Call("ChangeSignature", ("oldSignature", "remove"))}.");
                }

                newRoot = newRoot.InsertNodesAfter(newMember, new[] { wrapperNode.WithAdditionalAnnotations(Formatter.Annotation, wrapperAnnotation) });
                // Prove the wrapper's call binds to the new member (never to itself) before anything leaves the engine.
                var verifyDocument = documentByPath[path].WithSyntaxRoot(newRoot);
                var bindingError = ChangeSignatureWrapper.VerifyBinding((await verifyDocument.GetSemanticModelAsync(cancellationToken))!, (await verifyDocument.GetSyntaxRootAsync(cancellationToken))!, wrapperAnnotation, memberAnnotation, newSignatureText);
                if (bindingError is not null)
                {
                    return Refuse(bindingError);
                }
            }

            // Only the rewritten parameter list and call sites are formatted (C5c-3); the rest of the file keeps its text and
            // line endings, and every line the rewrite changed takes the resolved line ending (IWorkspaceMutator.ResolveEol).
            var formatted = await RoslynFormattingHelper.FormatAnnotatedSpansAsync(documentByPath[path], newRoot, eol, cancellationToken);
            var formattedText = (await formatted.GetTextAsync(cancellationToken)).ToString();
            // A file whose every call site was left alone (skipped, or kept on the wrapper) has nothing to write: it is not a change.
            if (formattedText != (await documentByPath[path].GetTextAsync(cancellationToken)).ToString())
            {
                result[path.Equals(declPath) ? filePath : path] = formattedText;
            }

            trialChanges[path] = formattedText;
        }

        // The final gate, before anything leaves the engine (so a dry run and the real apply refuse identically): no call may silently bind to a different method than the plan accounts for.
        await RequireSignatureChangeKeepsCallBindingsAsync(solution, declaredSymbol, methodName, newParameterList, declPath.ToString(), trialChanges, wrapperMode, plannedCallSiteLines, skippedCallSites, cancellationToken);

        // ActivatorUtilities.CreateInstance(sp, args) and similar bind to no constructor, so they are not call sites above: they compile after the change and fail at runtime. Reported, never rewritten.
        var reflectiveSites = declaredSymbol is { MethodKind: MethodKind.Constructor }
            ? await FindReflectiveConstructionSitesAsync(solution, declaredSymbol.ContainingType, cancellationToken)
            : null;

        return wrapperMode
            ? new ChangeSignatureResult(result, skippedCallSites, null, migratedCallSites, oldSignatureText, newSignatureText, ChangeSignatureOldSignature.keepAsDeprecatedWrapper, reflectiveSites)
            : new ChangeSignatureResult(result, skippedCallSites, OldSignatureOutcome: ChangeSignatureOldSignature.remove, ReflectiveConstructionSites: reflectiveSites);
    }

    /// <summary>
    /// Calls of Microsoft.Extensions.DependencyInjection.ActivatorUtilities (CreateInstance, CreateFactory, GetServiceOrCreateInstance, generic or not) and of
    /// System.Activator.CreateInstance(Type, params object[] args) that name <paramref name="type"/> as a type argument or a typeof argument, matched by symbol. Line is 1-based.
    /// </summary>
    private static async Task<List<SkippedCallSite>> FindReflectiveConstructionSitesAsync(Solution solution, INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var sites = new List<SkippedCallSite>();
        foreach (var document in solution.Projects.Where(project => project.Language == LanguageNames.CSharp).SelectMany(project => project.Documents))
        {
            if (document.FilePath == null || await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            {
                continue;
            }

            // Cheap syntactic pre-filter on the invoked name; the semantic model is only built for files that pass it.
            var candidates = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.Expression switch
                {
                    MemberAccessExpressionSyntax access => IsReflectiveConstructionName(access.Name.Identifier.ValueText),
                    SimpleNameSyntax name => IsReflectiveConstructionName(name.Identifier.ValueText),
                    _ => false
                })
                .ToList();
            if (candidates.Count == 0 || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            foreach (var invocation in candidates)
            {
                if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                var api = method.ContainingType;
                var isActivatorUtilities = api.Name == "ActivatorUtilities" && api.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.DependencyInjection";
                var isActivatorWithArgs = api.Name == "Activator" && api.ContainingNamespace.ToDisplayString() == "System" && method.Name == "CreateInstance"
                    && method.Parameters.Any(p => p.Name == "args");
                if (!isActivatorUtilities && !isActivatorWithArgs)
                {
                    continue;
                }

                var namesType = method.TypeArguments.Any(t => SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, type.OriginalDefinition))
                    || invocation.ArgumentList.DescendantNodes().OfType<TypeOfExpressionSyntax>()
                        .Any(typeOf => model.GetTypeInfo(typeOf.Type, cancellationToken).Type is { } named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, type.OriginalDefinition));
                if (namesType)
                {
                    var line = root.SyntaxTree.GetLineSpan(invocation.Span, cancellationToken: cancellationToken).StartLinePosition.Line + 1;
                    sites.Add(new SkippedCallSite(document.FilePath, line, $"{api.Name}.{method.Name} names '{type.Name}'; its arguments are not rewritten and are resolved at runtime"));
                }
            }
        }

        return sites;
    }

    private static bool IsReflectiveConstructionName(string name) => name is "CreateInstance" or "CreateFactory" or "GetServiceOrCreateInstance";

    private static bool IsCommentTrivia(SyntaxTrivia trivia) =>
    trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
    || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    // True when a line end sits in an item's own trivia or in a separator's trivia, i.e. the list is laid out over several lines.
    private static bool HasLineBreakBetweenItems<TNode>(SeparatedSyntaxList<TNode> list) where TNode : SyntaxNode
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index].GetLeadingTrivia().Any(SyntaxKind.EndOfLineTrivia) || list[index].GetTrailingTrivia().Any(SyntaxKind.EndOfLineTrivia))
            {
                return true;
            }

            if (index < list.Count - 1)
            {
                var separator = list.GetSeparator(index);
                if (separator.LeadingTrivia.Any(SyntaxKind.EndOfLineTrivia) || separator.TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // True when RebuildListKeepingLayout must carry the list's own trivia over: two or more items with a line break between them or after the open parenthesis, or one
    // item after such a break (`f(\n    a)`). Anything else is a single-line list, rebuilt with the default separators and formatted.
    private static bool ListKeepsLayout<TNode>(SeparatedSyntaxList<TNode> list) where TNode : SyntaxNode
    {
        var breaksAfterOpen = list.Count > 0 && list[0].GetFirstToken().GetPreviousToken().TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia);
        return list.Count >= 2 ? breaksAfterOpen || HasLineBreakBetweenItems(list) : breaksAfterOpen;
    }

    // The layout part of a trivia list: comments and the whitespace in front of them are removed; with dropFollowingLineEnd the line end that closed a
    // single-line comment goes too (leading trivia, where that line end belongs to the comment's line); trailing trivia keeps its line end.
    private static List<SyntaxTrivia> WithoutComments(SyntaxTriviaList trivia, bool dropFollowingLineEnd)
    {
        var kept = new List<SyntaxTrivia>();
        for (var index = 0; index < trivia.Count; index++)
        {
            if (!IsCommentTrivia(trivia[index]))
            {
                kept.Add(trivia[index]);
                continue;
            }

            while (kept.Count > 0 && kept[^1].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                kept.RemoveAt(kept.Count - 1);
            }

            if (dropFollowingLineEnd && trivia[index].IsKind(SyntaxKind.SingleLineCommentTrivia) && index + 1 < trivia.Count && trivia[index + 1].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                index++;
            }
        }

        return kept;
    }

    // Collects each comment of a trailing trivia list together with the whitespace in front of it, so a comment can be re-attached after a comma.
    private static void AddCommentsWithIndent(SyntaxTriviaList trivia, List<SyntaxTrivia> into)
    {
        for (var index = 0; index < trivia.Count; index++)
        {
            if (!IsCommentTrivia(trivia[index]))
            {
                continue;
            }

            if (index > 0 && trivia[index - 1].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                into.Add(trivia[index - 1]);
            }

            into.Add(trivia[index]);
        }
    }

    /// <summary>
    /// Rebuilds an argument or parameter list for ChangeSignatureAsync. A list that fits on one line (or has fewer than two items) is rebuilt with the default
    /// ", " separators exactly as before. A multi-line list keeps its layout by position: the leading/trailing trivia and the separator layout of slot i
    /// (indentation, line ends) go to the item now at position i, an unchanged slot keeps its trivia verbatim, a new item copies its neighbours' layout, and an
    /// item's own comments travel with it. The line ends come from the existing trivia, never from a literal. <paramref name="items"/> pairs each new node with
    /// the index it had in <paramref name="current"/>, or -1 for an added one.
    /// </summary>
    private static SeparatedSyntaxList<TNode> RebuildListKeepingLayout<TNode>(SeparatedSyntaxList<TNode> current, IReadOnlyList<(TNode Node, int SourceIndex)> items) where TNode : SyntaxNode
    {
        var oldCount = current.Count;
        var newCount = items.Count;
        if (!ListKeepsLayout(current))
        {
            return SyntaxFactory.SeparatedList(items.Select(item => item.Node));
        }

        var rebuilt = new List<SyntaxNodeOrToken>();
        for (var position = 0; position < newCount; position++)
        {
            var (node, sourceIndex) = items[position];
            var isLast = position == newCount - 1;
            var bare = node.WithoutTrivia();
            if (sourceIndex == position && isLast == (sourceIndex == oldCount - 1))
            {
                // The slot is unchanged: keep the original trivia and separator byte for byte.
                rebuilt.Add(bare.WithLeadingTrivia(current[sourceIndex].GetLeadingTrivia()).WithTrailingTrivia(current[sourceIndex].GetTrailingTrivia()));
                if (!isLast)
                {
                    rebuilt.Add(current.GetSeparator(sourceIndex));
                }

                continue;
            }

            // A list with one original item has no separator to copy: an added item follows on the same line (", " and no indentation).
            var singleOld = oldCount == 1;
            var ownLeading = sourceIndex >= 0 ? current[sourceIndex].GetLeadingTrivia() : default;
            var leading = ownLeading.Any(IsCommentTrivia) ? ownLeading.ToList()
                : singleOld && position > 0 ? new List<SyntaxTrivia>()
                : WithoutComments(current[Math.Min(position, oldCount - 1)].GetLeadingTrivia(), dropFollowingLineEnd: true);
            var tailComments = new List<SyntaxTrivia>();
            if (sourceIndex >= 0)
            {
                AddCommentsWithIndent(current[sourceIndex].GetTrailingTrivia(), tailComments);
                if (sourceIndex < oldCount - 1)
                {
                    AddCommentsWithIndent(current.GetSeparator(sourceIndex).TrailingTrivia, tailComments);
                }
            }

            if (isLast)
            {
                // The last item carries the line end of the old last slot (the list's closing line), after any comment that used to follow its comma.
                var trailing = tailComments.Concat(WithoutComments(current[oldCount - 1].GetTrailingTrivia(), dropFollowingLineEnd: false));
                rebuilt.Add(bare.WithLeadingTrivia(leading).WithTrailingTrivia(trailing));
            }
            else
            {
                var separator = singleOld ? SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space) : current.GetSeparator(Math.Min(position, oldCount - 2));
                rebuilt.Add(bare.WithLeadingTrivia(leading));
                rebuilt.Add(SyntaxFactory.Token(SyntaxKind.CommaToken)
                    .WithLeadingTrivia(WithoutComments(separator.LeadingTrivia, dropFollowingLineEnd: false))
                    .WithTrailingTrivia(tailComments.Concat(WithoutComments(separator.TrailingTrivia, dropFollowingLineEnd: false))));
            }
        }

        return SyntaxFactory.SeparatedList<TNode>(rebuilt);
    }

    /// <summary>
    /// Preflight for ChangeSignatureAsync: an existing parameter that is absent from the new list is removed, and a removed parameter the body still uses would
    /// leave CS0103 behind after the apply. Finds the uses by symbol identity (a same-named local or member does not count; nameof, lambdas and local functions
    /// capturing it do) in the body, expression body and constructor initializer, and returns a refusal naming the parameter and the 1-based lines, or null when
    /// no removed parameter is used. A declaration without a body (abstract, interface, extern, partial) has no uses. Never rewrites anything.
    /// </summary>
    private static string? RefuseWhenRemovedParameterIsStillUsed(SemanticModel semanticModel, BaseMethodDeclarationSyntax methodDecl, IReadOnlyList<ParameterSyntax> originalParams, IReadOnlyCollection<int> keptIndexes, string methodName, CancellationToken cancellationToken)
    {
        var scopes = new List<SyntaxNode>();
        if (methodDecl.Body is not null)
        {
            scopes.Add(methodDecl.Body);
        }

        if (methodDecl.ExpressionBody is not null)
        {
            scopes.Add(methodDecl.ExpressionBody);
        }

        if (methodDecl is ConstructorDeclarationSyntax { Initializer: { } initializer })
        {
            scopes.Add(initializer);
        }

        if (scopes.Count == 0)
        {
            return null;
        }

        var messages = new List<string>();
        for (var index = 0; index < originalParams.Count; index++)
        {
            if (keptIndexes.Contains(index))
            {
                continue;
            }

            var parameterSymbol = semanticModel.GetDeclaredSymbol(originalParams[index], cancellationToken);
            if (parameterSymbol is null)
            {
                continue;
            }

            var name = originalParams[index].Identifier.ValueText;
            var lines = new SortedSet<int>();
            var fileName = System.IO.Path.GetFileName(methodDecl.SyntaxTree.FilePath);
            foreach (var scope in scopes)
            {
                foreach (var identifier in scope.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Identifier.ValueText != name)
                    {
                        continue;
                    }

                    if (SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol, parameterSymbol))
                    {
                        lines.Add(identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    }
                }
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var shown = string.Join(", ", lines.Take(10).Select(line => $"{fileName}:{line}"));
            var more = lines.Count > 10 ? $" and {lines.Count - 10} more" : string.Empty;
            messages.Add($"Cannot remove parameter '{name}' of '{methodName}': it is still used in the body at {shown}{more}. Make the body stop using it first, then retry the removal.");
        }

        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    /// <summary>
    /// The final gate of ChangeSignatureAsync: applies the planned file texts to a trial solution, captures every call of the changed
    /// member's name there and in the original, and refuses the whole change (before anything is written) when a call would bind to a
    /// different method than the plan accounts for, or when the check itself cannot run. Skipped only when the trial has compile errors of
    /// its own: the write's compile gate reports those with their real cause, and unbound calls would only add noise.
    /// </summary>
    private static async Task RequireSignatureChangeKeepsCallBindingsAsync(Solution solution, IMethodSymbol? changedMethod, string methodName, ParameterListSyntax newParameterList, string declarationPath, Dictionary<FilePathWrapper, string> trialChanges, bool wrapperMode, IReadOnlyCollection<(string FilePath, int Line)> plannedSites, IReadOnlyCollection<SkippedCallSite> skippedSites, CancellationToken cancellationToken)
    {
        CallBindingCheckResult check;
        if (changedMethod is null)
        {
            check = CallBindingCheckResult.Failed("the changed member could not be resolved to a symbol");
        }
        else
        {
            try
            {
                var trial = await ValidationEngine.ValidateChangesWithCandidateAsync(solution, trialChanges, null, cancellationToken);
                if (trial.Report.IsError)
                {
                    return;
                }

                check = await ChangeSignatureCallBindingCheck.CheckAsync(solution, trial.Candidate, changedMethod, methodName, newParameterList, declarationPath, wrapperMode, plannedSites, skippedSites.Select(s => (s.FilePath.ToString(), s.LineNumber)).ToList(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fails closed. Only the exception type is echoed: its text can carry file paths.
                check = CallBindingCheckResult.Failed($"the trial solution could not be built ({ex.GetType().Name})");
            }
        }

        if (check.IsRefusal)
        {
            var fix = check.Status == CallBindingCheckStatus.Discrepancies
                ? " Fix for this change: pick a parameter list that does not make those calls applicable to another overload, rename or remove the competing overload first, or update those calls by hand before changing the signature."
                : string.Empty;
            // TODO rebind refusals show file paths with backslashes, unlike other results (paths come from declPath.ToString() and skippedSites FilePath.ToString())
            throw new ToolInvalidArgumentException(CallBindingCheck.FormatRefusal(check, $"change the signature of '{methodName}'") + fix);
        }
    }

    /// <summary>True for the nameof(...) operator (not a method that happens to be called nameof): the name is not bound to a symbol and the whole expression is a constant.</summary>
    private static bool IsNameofInvocation(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        return invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } name
            && semanticModel.GetSymbolInfo(name).Symbol == null
            && semanticModel.GetConstantValue(invocation).HasValue;
    }

    /// <summary>The nearest enclosing node that carries an argument list a ChangeSignature call site could be: an invocation, an object creation (`new T(...)` or target-typed `new(...)`) with an argument list, a constructor initializer, or a primary-constructor base call.</summary>
    private static SyntaxNode? FindNearestCall(SyntaxToken token) =>
        token.Parent?.AncestorsAndSelf().FirstOrDefault(n => n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax { ArgumentList: not null } or ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax);

    /// <summary>True when <paramref name="token"/> names the thing <paramref name="call"/> calls (the callee of an invocation, the `new` keyword or type of a creation, the `this`/`base` keyword of an initializer, the base type of a primary-constructor base call), not one of its arguments.</summary>
    private static bool IsCalleeToken(SyntaxNode call, SyntaxToken token) => call switch
    {
        InvocationExpressionSyntax invocation => invocation.Expression.Span.Contains(token.Span),
        ObjectCreationExpressionSyntax creation => token == creation.NewKeyword || creation.Type.Span.Contains(token.Span),
        ImplicitObjectCreationExpressionSyntax implicitCreation => token == implicitCreation.NewKeyword,
        ConstructorInitializerSyntax initializer => token == initializer.ThisOrBaseKeyword,
        PrimaryConstructorBaseTypeSyntax primaryBase => primaryBase.Type.Span.Contains(token.Span),
        _ => false,
    };

    /// <summary>
    /// The target-typed `new(...)` calls (their `new` keyword) that bind to <paramref name="constructor"/>, in C# documents where
    /// SymbolFinder did not already report a location (<paramref name="alreadyFound"/>).
    /// </summary>
    private static async Task<List<(Document Document, Microsoft.CodeAnalysis.Text.TextSpan Span)>> FindTargetTypedConstructorCallsAsync(Solution solution, IMethodSymbol constructor, IEnumerable<(Document Document, Microsoft.CodeAnalysis.Text.TextSpan Span)> alreadyFound, CancellationToken cancellationToken)
    {
        var seen = new HashSet<(string Path, int Start)>(alreadyFound.Select(found => (found.Document.FilePath!, found.Span.Start)));
        var calls = new List<(Document Document, Microsoft.CodeAnalysis.Text.TextSpan Span)>();
        foreach (var document in solution.Projects.Where(project => project.Language == LanguageNames.CSharp).SelectMany(project => project.Documents))
        {
            if (document.FilePath == null || await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            {
                continue;
            }

            var creations = root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>().ToList();
            if (creations.Count == 0 || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            foreach (var creation in creations)
            {
                if (model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol bound
                    && SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, constructor.OriginalDefinition)
                    && seen.Add((document.FilePath, creation.NewKeyword.SpanStart)))
                {
                    calls.Add((document, creation.NewKeyword.Span));
                }
            }
        }

        return calls;
    }

    public async Task<Dictionary<FilePathWrapper, string>> MoveTypeToFileAsync(FilePathWrapper filePath, string typeName, string? targetFolder = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var typeNode = RoslynSentinel.Common.NameTargetLookup.RequireSingle(root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Identifier.Text == typeName).ToList() ?? new List<BaseTypeDeclarationSyntax>(), "Type", typeName);
        if (typeNode == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // Guard: if the type's name already matches the source file name, it's already in its own file -> nothing to move
        if (string.Equals(typeName, Path.GetFileNameWithoutExtension(document.Name), StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // A nested type becomes a top-level type: qualified Outer.Nested references solution-wide (and in this file) are rewritten first,
        // so the declaration is taken from the rewritten root.
        var referenceEdits = new Dictionary<FilePathWrapper, string>();
        if (typeNode.Parent is TypeDeclarationSyntax)
        {
            (root, typeNode) = await LiftNestedTypeReferencesAsync(solution, document, root!, typeNode, referenceEdits, cancellationToken);
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
        var targetDirectory = string.IsNullOrEmpty(targetFolder) ? sourceDirectory : targetFolder;
        var newPath = string.IsNullOrEmpty(targetDirectory) ? $"{typeName}.cs" : Path.Combine(targetDirectory, $"{typeName}.cs");

        var updatedOrig = RemoveOrphanedRegionDirectives(root!.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia)!);
        var newDoc = await RemoveUnusedUsingsAsync(document.Project.AddDocument($"{typeName}.cs", newRoot), cancellationToken);
        // The new file and the trimmed original are both fully owned by this tool, so both are formatted whole; both take the
        // resolved line ending (IWorkspaceMutator.ResolveEol).
        var sourceText = await document.GetTextAsync(cancellationToken);
        var eol = await _workspaceManager.ResolveEolAsync(document, cancellationToken);
        var formattedNewDoc = await RoslynFormattingHelper.FormatWholeDocumentAsync(newDoc, eol, sourceText, cancellationToken);
        var newContent = (await formattedNewDoc.GetTextAsync(cancellationToken)).ToString();
        // The scaffolded file must end with exactly one EOL, in the resolved line ending (the formatter drops it).
        if (!newContent.EndsWith('\n'))
        {
            newContent += eol;
        }
        var updatedOrigDoc = document.WithSyntaxRoot(updatedOrig);
        var formattedOrigDoc = await RoslynFormattingHelper.FormatWholeDocumentAsync(updatedOrigDoc, eol, sourceText, cancellationToken);
        var updatedOrigContent = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();
        var moveResult = new Dictionary<FilePathWrapper, string>
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
        foreach (var referenceEdit in referenceEdits)
        {
            moveResult.TryAdd(referenceEdit.Key, referenceEdit.Value);
        }

        return moveResult;
    }

    // Rewrites qualified Outer.Nested references to Nested (adding the using where the file cannot see the namespace) for a nested type that is being lifted to top level.
    // The declaring document's rewritten root and type node are returned (the caller removes the declaration from that root); other documents' new text goes into otherEdits.
    // Limitation: references through a derived type, to a generic containing type, or alias-qualified are left as is (the compile gate then reports them).
    private async Task<(CompilationUnitSyntax Root, BaseTypeDeclarationSyntax TypeNode)> LiftNestedTypeReferencesAsync(Solution solution, Document document, CompilationUnitSyntax root, BaseTypeDeclarationSyntax typeNode, Dictionary<FilePathWrapper, string> otherEdits, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model?.GetDeclaredSymbol(typeNode, cancellationToken) is not INamedTypeSymbol nested)
        {
            return (root, typeNode);
        }

        var sites = await NestedTypeReferenceLifter.FindSitesAsync(solution, nested, cancellationToken);
        var namespaceName = NestedTypeReferenceLifter.NamespaceNameOrNull(nested);
        foreach (var documentSites in sites.GroupBy(s => s.DocumentId))
        {
            var siteDocument = solution.GetDocument(documentSites.Key);
            if (siteDocument == null)
            {
                continue;
            }

            var documentEol = await _workspaceManager.ResolveEolAsync(siteDocument, cancellationToken);
            var siteList = documentSites.Select(s => (s.Span, s.Kind, s.NeedsUsing, s.QualifyFully, s.KeepQualifier)).ToList();
            if (documentSites.Key == document.Id)
            {
                var (rewrittenRoot, rewrittenType) = NestedTypeReferenceLifter.ApplyRewrites(root, siteList, typeNode, namespaceName, documentEol);
                root = rewrittenRoot;
                typeNode = (BaseTypeDeclarationSyntax)rewrittenType!;
            }
            else if (await siteDocument.GetSyntaxRootAsync(cancellationToken) is CompilationUnitSyntax siteRoot)
            {
                var (rewrittenRoot, _) = NestedTypeReferenceLifter.ApplyRewrites(siteRoot, siteList, null, namespaceName, documentEol);
                otherEdits[siteDocument.FilePath ?? siteDocument.Name] = rewrittenRoot.ToFullString();
            }
        }

        return (root, typeNode);
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
        var sourceText = await document.GetTextAsync(cancellationToken);
        var eol = await _workspaceManager.ResolveEolAsync(document, cancellationToken);
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
            var newDoc = await RemoveUnusedUsingsAsync(document.Project.AddDocument($"{typeName}.cs", newRoot), cancellationToken);
            var formattedNewDoc = await RoslynFormattingHelper.FormatWholeDocumentAsync(newDoc, eol, sourceText, cancellationToken);
            var newText = (await formattedNewDoc.GetTextAsync(cancellationToken)).ToString();
            // The scaffolded file must end with exactly one EOL, in the resolved line ending (the formatter drops it).
            changes[newPath] = newText.EndsWith('\n') ? newText : newText + eol;
        }

        var updatedRoot = RemoveOrphanedRegionDirectives(root.RemoveNodes(typesToMove, SyntaxRemoveOptions.KeepNoTrivia)!);
        var updatedOrigDoc = document.WithSyntaxRoot(updatedRoot);
        var formattedOrigDoc = await RoslynFormattingHelper.FormatWholeDocumentAsync(updatedOrigDoc, eol, sourceText, cancellationToken);
        changes[document.FilePath ?? document.Name] = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();
        return changes;
    }

    // Builds the compilation unit for a type being split into its own file, handling:
    // - extern alias declarations (not in root.Usings -> must be copied separately)
    // - global using aliases filtered out (project-scoped; duplicating them causes CS1537)
    // - file-scoped types promoted to internal (file modifier = visible only in declaring file)
    private static (CompilationUnitSyntax newRoot, BaseTypeDeclarationSyntax cleanNode) BuildSplitFileRoot(CompilationUnitSyntax root, BaseTypeDeclarationSyntax typeNode)
    {
        // Keep the type's doc comment (/// and /** */) so the split file retains its documentation; #region and plain comments are still dropped.
        var docTrivia = typeNode.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        var cleanNode = typeNode.WithoutLeadingTrivia().WithLeadingTrivia(SyntaxFactory.TriviaList(docTrivia.Prepend(SyntaxFactory.ElasticCarriageReturnLineFeed)));
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
            throw new ToolFeatureDisabledException("MoveTypeToFile");
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        return await MoveAllTypesToFilesForDocumentAsync(document, cancellationToken);
    }

    public async Task<Dictionary<FilePathWrapper, string>> MoveAllTypesToFilesInProjectAsync(string projectName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            throw new ToolFeatureDisabledException("MoveTypeToFile");
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var project = ProjectLookup.FindProject(solution, projectName, ignoreCase: true) ?? throw new ToolNotFoundException($"Project '{projectName}' not found.");
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
            throw new ToolFeatureDisabledException("MoveTypeToFile");
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

    public async Task<Dictionary<FilePathWrapper, string>> ExtractInterfaceAsync(FilePathWrapper filePath, string className, string interfaceName, CancellationToken cancellationToken = default, List<string>? warnings = null)
    {
        if (!_config.IsFeatureEnabled("ExtractInterface"))
        {
            throw new ToolFeatureDisabledException("ExtractInterface");
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = RoslynSentinel.Common.NameTargetLookup.RequireSingle(root?.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == className).ToList() ?? new List<ClassDeclarationSyntax>(), "Class", className);
        if (classNode == null)
        {
            var fileClassNames = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(classNode2 => classNode2.Identifier.Text) ?? Enumerable.Empty<string>();
            throw new ToolNotFoundException($"Class '{className}' not found in '{filePath}'. " + DeclarationNotFoundDetail.Build("class", "className", className, fileClassNames, filePath.ToString()), "class", className);
        }

        // Extract public instance methods (exclude static, constructors)
        var methods = classNode.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));
        // Extract public non-static properties with at least a getter
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) && p.AccessorList != null);
        // The interface file root is built from scratch and formatted by FormatNewRootKeepingTriviaAsync: the source's using
        // directives are embedded as existing nodes (header comment, trailing comments and line endings kept byte-exact) and
        // everything else is new structure the formatter lays out. Signature parts reused from the source (types, names,
        // parameter and constraint lists) are stripped of ALL trivia first, so a comment inside a signature is not carried to
        // the interface (as SyncInterfaceToImplementation does).
        static T Strip<T>(T node) where T : SyntaxNode => RoslynFormattingHelper.StripAllTrivia(node);
        static T? StripOrNull<T>(T? node) where T : SyntaxNode => node is null ? null : Strip(node);
        // Only the source member's documentation comments survive onto the interface member; other trivia is dropped.
        static SyntaxTriviaList DocComments(SyntaxNode node) => SyntaxFactory.TriviaList(node.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)));
        // Strip drops every comment inside a signature part, so one warning is recorded per member that lost one.
        // Doc comments and the member's own leading trivia are not signature parts.
        static bool IsCommentTrivia(SyntaxTrivia t) => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia);
        // A return or property type's leading trivia can hold the member's own leading comments, so it is skipped.
        static bool HasSignatureComment(SyntaxNode? node, bool skipLeadingTrivia = false)
        {
            if (node is null) return false;
            var skipped = skipLeadingTrivia ? node.GetLeadingTrivia() : default;
            return node.DescendantTrivia(descendIntoTrivia: true).Any(t => IsCommentTrivia(t) && !skipped.Contains(t));
        }
        if (warnings is not null)
        {
            foreach (var method in methods)
            {
                if (HasSignatureComment(method.ReturnType, skipLeadingTrivia: true) || HasSignatureComment(method.TypeParameterList) || HasSignatureComment(method.ParameterList) || method.ConstraintClauses.Any(c => HasSignatureComment(c)))
                {
                    warnings.Add($"Comments inside the signature of '{method.Identifier.Text}' were not copied to the interface.");
                }
            }
            foreach (var prop in properties)
            {
                if (HasSignatureComment(prop.Type, skipLeadingTrivia: true))
                {
                    warnings.Add($"Comments inside the signature of '{prop.Identifier.Text}' were not copied to the interface.");
                }
            }
        }
        var ifaceMethods = methods.Select(m => (MemberDeclarationSyntax)SyntaxFactory.MethodDeclaration(Strip(m.ReturnType), m.Identifier.WithoutTrivia()).WithTypeParameterList(StripOrNull(m.TypeParameterList)).WithParameterList(Strip(m.ParameterList)).WithConstraintClauses(SyntaxFactory.List(m.ConstraintClauses.Select(c => Strip(c)))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithLeadingTrivia(DocComments(m)));
        var ifaceProperties = properties.Select(p =>
        {
            // Build interface accessor list: only keep get/set/init that existed in source
            var accessors = p.AccessorList!.Accessors.Select(acc => SyntaxFactory.AccessorDeclaration(acc.Kind()).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            return (MemberDeclarationSyntax)SyntaxFactory.PropertyDeclaration(Strip(p.Type), p.Identifier.WithoutTrivia()).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors))).WithLeadingTrivia(DocComments(p));
        });
        var ifaceMembers = ifaceProperties.Concat(ifaceMethods).ToArray();
        var ifaceNode = SyntaxFactory.InterfaceDeclaration(interfaceName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddMembers(ifaceMembers);
        // Wrap in namespace + usings to produce a compilable file
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var sourceUsings = root!.Usings;
        CompilationUnitSyntax ifaceCompUnit;
        if (ns != null)
        {
            BaseNamespaceDeclarationSyntax newNs = ns is FileScopedNamespaceDeclarationSyntax ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(Strip(ns.Name)).AddMembers(ifaceNode) : SyntaxFactory.NamespaceDeclaration(Strip(ns.Name)).AddMembers(ifaceNode);
            ifaceCompUnit = SyntaxFactory.CompilationUnit().WithUsings(sourceUsings).AddMembers(newNs);
        }
        else
        {
            ifaceCompUnit = SyntaxFactory.CompilationUnit().WithUsings(sourceUsings).AddMembers(ifaceNode);
        }

        // Add interface to class's base list (only if not already present)
        var alreadyImplements = classNode.BaseList?.Types.Any(t => t.Type.ToString() == interfaceName) == true;
        var newClass = classNode;
        if (!alreadyImplements)
        {
            var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));
            if (classNode.BaseList == null && classNode.Identifier.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
            {
                // The line break after the class name moves onto the new base list so the colon stays on the class line.
                newClass = classNode.WithIdentifier(classNode.Identifier.WithTrailingTrivia(SyntaxFactory.TriviaList())).AddBaseListTypes(baseType.WithTrailingTrivia(classNode.Identifier.TrailingTrivia));
            }
            else
            {
                newClass = classNode.AddBaseListTypes(baseType);
            }
        }
        var updatedOrig = root.ReplaceNode(classNode, newClass);
        var ifacePath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", $"{interfaceName}.cs");
        var eol = await _workspaceManager.ResolveEolAsync(document, cancellationToken);
        // Only the synthesized interface structure is formatted; the embedded using directives stay byte-exact.
        var ifaceContent = await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, ifaceCompUnit, static n => n is not UsingDirectiveSyntax, eol, cancellationToken);
        // Format the original file
        var origDoc = document.WithSyntaxRoot(updatedOrig);
        var formattedOrigDoc = await RoslynFormattingHelper.FormatWholeDocumentAsync(origDoc, eol, await document.GetTextAsync(cancellationToken), cancellationToken);
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
        // A namespace is scanned by its full old qualified name (computed before the rename); everything else by simple name.
        var isNamespaceRename = symbol is INamespaceSymbol;
        var residualScanName = isNamespaceRename ? symbol.ToDisplayString() : oldName;
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

        // Per-file diffs are not computed here -> the caller (RefactoringStructuralTools.RenameSymbol)
        // gets them from ValidateAndApplyAsync's returnDiff option, which builds them via the
        // canonical DiffEngine.CreateDiff. This used to duplicate that with a second, weaker
        // lockstep diff (ComputeRenameHunks); removed as part of the diff-logic consistency fix
        // (docs/current/codebase-consistency-audit-v1.md #3).
        // A namespace has no declared-symbol node whose token equals newName (its name is a QualifiedName, which
        // GetDeclaredSymbol does not map), so TryResolveUpdatedHandleAsync would always return null; the new id is
        // simply the parent namespace plus the new last segment.
        var updatedHandle = symbol is INamespaceSymbol renamedNamespace
            ? new SymbolHandle(handle.ProjectName, "N:" + (renamedNamespace.ContainingNamespace is { IsGlobalNamespace: false } parentNamespace ? parentNamespace.ToDisplayString() + "." : "") + newName)
            : await TryResolveUpdatedHandleAsync(handle, symbol, updated, newName, cancellationToken);
        var residualMentions = await FindResidualMentionsAsync(updated, residualScanName, isNamespaceRename, cancellationToken);
        return new RenameSymbolResult(oldName, newName, pendingChanges, null, updatedHandle, residualMentions);
    }

    /// <summary>
    /// Post-rename scan for leftover mentions Roslyn's rename did not rewrite, delegated to
    /// <see cref="ResidualMentionScanner"/>. Scanned: the project .cs documents of <paramref name="updated"/> minus
    /// generated ones (obj/bin/Worktree paths, .g.cs/.g.i.cs/.designer.cs/.AssemblyInfo.cs/.AssemblyAttributes.cs
    /// names, "auto-generated" headers), plus non-source files under the solution root with extension
    /// .csproj, .props, .targets, .json, .md, .ps1, .slnx or .sln (skipping bin, obj, .git, .roslynsentinel,
    /// Worktree and node_modules directories). <paramref name="name"/> is the simple name for a non-namespace
    /// (whole-word match) or the full old qualified name for a namespace (dotted-token match: not the tail of a longer
    /// qualified name, not followed by an identifier char, a following ".Sub" matches). Each mention carries a
    /// <see cref="ResidualBucket"/> (PathLike, NonSource, Comment, String, Code). RenameInComments/RenameInStrings stay
    /// off (see RenameSymbolAsync), so comment and string-literal text is never rewritten by the rename itself.
    /// </summary>
    private async Task<List<ResidualMention>> FindResidualMentionsAsync(Solution updated, string name, bool isNamespace, CancellationToken cancellationToken)
    {
        return await ResidualMentionScanner.ScanAsync(updated, _workspaceManager.GetSolutionRoot(), name, isNamespace, cancellationToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<DocumentEditResult> ConvertIndexerToMethodAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ConvertIndexerToMethod"))
        {
            throw new ToolFeatureDisabledException("ConvertIndexerToMethod");
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var indexer = root?.DescendantNodes().OfType<IndexerDeclarationSyntax>().FirstOrDefault();
        if (indexer == null)
        {
            throw new ToolNotFoundException("Indexer not found. " + DeclarationNotFoundDetail.BuildNoneDeclared("indexer", filePath.ToString()));
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
            throw new ToolTargetIneligibleException("Indexer has no get accessor body or expression body to convert.");
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Indexer converted to method.",
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root!, indexer, getter, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ExtractConstantAsync(FilePathWrapper filePath, string contextSnippet, string constantName, string visibility = "private", string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractConstant"))
        {
            throw new ToolFeatureDisabledException("ExtractConstant");
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        var pos = SnippetLookup.TryResolve(text, contextSnippet, lineBefore, lineAfter, out var snippetMatch, out var snippetError) ? snippetMatch.Start : -1;
        if (pos < 0)
        {
            throw new ToolInvalidArgumentException(snippetError ?? "Context snippet not found.");
        }

        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, snippetMatch!.Length));
        var literal = node.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault() ?? node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
        if (literal == null)
        {
            throw new ToolTargetIneligibleException("Cannot convert: literal not found.");
        }

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            throw new ToolTargetIneligibleException("Cannot convert: containing type not found.");
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
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, trackedRoot, currentType, newType, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ExtractLocalVariableAsync(FilePathWrapper filePath, string contextSnippet, string? newVariableName = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractLocalVariable"))
        {
            throw new ToolFeatureDisabledException("ExtractLocalVariable");
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        var pos = SnippetLookup.TryResolve(text, contextSnippet, lineBefore, lineAfter, out var snippetMatch, out var snippetError) ? snippetMatch.Start : -1;
        if (pos < 0)
        {
            // ContextHelper's message ("contextSnippet not found"/"ambiguous (N matches)") is
            // necessarily generic -> ContextHelper only sees raw text offsets, it has no symbolName
            // or declaration list to enumerate the way SyntaxTargetResolver's candidate
            // hint does, and this tool has no name argument at all (it targets an expression by its
            // literal text, not a named declaration) -> so there is no candidate set to report here
            // the way there is for the member/type lookups in SyntaxTargetResolver. Point the caller at the tools that
            // would show it real file content instead of leaving a bare message with nothing to act on.
            throw new ToolInvalidArgumentException((snippetError ?? "Context snippet not found.") + " Re-check the snippet against GetMethodSource/GetFileOutline " + "output, or add lineBefore/lineAfter (the single verbatim line immediately before/after) to disambiguate.");
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
        // ambiguous (a partial/short contextSnippet can resolve to a larger expression than the caller
        // intended), so it only ever kicks in when the exact match above fails, and never overrides it.
        var expression = exactMatch ?? root.FindToken(pos).Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null)
        {
            // The snippet DID resolve to a text position (pos, above) -> the failure is that no
            // ExpressionSyntax boundary aligns with it (e.g. the snippet spans a statement, a
            // keyword, or crosses an expression boundary). Report where it landed instead of a
            // bare "not found", since that position is real, already-available information -> a
            // caller reading only "expression not found" has no way to tell its snippet was even
            // located at all versus silently mismatched.
            var landedLine = text.Lines.GetLineFromPosition(pos).LineNumber + 1;
            throw new ToolTargetIneligibleException($"Cannot convert: contextSnippet was found at line {landedLine}, but it does not " + "align to a single, whole extractable expression there (it may span a statement, a keyword, " + "or cross an expression boundary). Narrow the snippet to exactly one expression's text.");
        }

        // Find the containing method
        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null || containingMethod.Body == null)
        {
            throw new ToolTargetIneligibleException("Cannot convert: containing method not found.");
        }

        // Find the containing statement and block
        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            throw new ToolTargetIneligibleException("Cannot convert: containing statement not found.");
        }

        var containingBlock = containingStatement.Parent as BlockSyntax;
        if (containingBlock == null)
        {
            throw new ToolTargetIneligibleException("Cannot convert: containing block not found.");
        }

        // Skip if expression is already a standalone variable declaration
        if (containingStatement is LocalDeclarationStatementSyntax existingDecl && existingDecl.Declaration.Variables.Count == 1 && existingDecl.Declaration.Variables[0].Initializer?.Value?.IsEquivalentTo(expression) == true)
        {
            var existingName = existingDecl.Declaration.Variables[0].Identifier.Text;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.AlreadyInTargetState,
                FilePath = filePath,
                Message = $"'{existingName}' is already a local variable - nothing to extract.",
                UpdatedText = text.ToString()
            };
        }

        // Skip if expression has potential side effects (method calls, assignments)
        if (HasSideEffects(expression))
        {
            throw new ToolTargetIneligibleException("Cannot convert: expression has potential side effects.");
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
            throw new ToolTargetIneligibleException("Cannot convert: statement index not found.");
        }

        // Insert variable declaration before the statement
        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, newRoot, currentBlock, newBlock, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken)
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
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        MethodDeclarationSyntax? method = null;
        if (contextSnippet != null)
        {
            var pos = SnippetLookup.TryResolve(text, contextSnippet, lineBefore, lineAfter, out var lookup) ? lookup.Matches.Single().Start : -1;
            if (pos >= 0)
            {
                method = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0)).AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            }
        }

        method ??= RoslynSentinel.Common.NameTargetLookup.RequireSingle(root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == methodName).ToList(), "Method", methodName);
        if (method == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' not found in '{filePath}'. Searched: method declarations in the file by name. Next: {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))} to list its methods, or {ToolCallHint.Call("Search", ("mode", "method"), ("query", methodName))}. {DeclarationNotFoundDetail.Build("method", "methodName", methodName, root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text), filePath, includeOutlineCall: false)}", "method", methodName);
        }

        if (method.Body == null)
        {
            throw new ToolTargetIneligibleException($"Method '{methodName}' in '{filePath}' has no body (abstract, extern or interface member), so control-flow analysis does not apply. Next: pick a method with a body from GetFileOutline.");
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            throw new ToolInvalidArgumentException($"Semantic model for '{filePath}' is unavailable. Next: confirm the project compiles with {ToolCallHint.Call("Build")}.");
        }

        var flow = model.AnalyzeControlFlow(method.Body)!;

        var returnPoints = flow.ReturnStatements.Select(r => r.ToString().Trim()).ToList();
        var throwPoints = method.Body.DescendantNodes().OfType<ThrowStatementSyntax>().Select(t => t.ToString().Trim()).ToList();
        return new ControlFlowSummary(methodName, flow.EndPointIsReachable == false, flow.ReturnStatements.Length > 0, flow.ReturnStatements.Length == 0, returnPoints, throwPoints, flow.ExitPoints.Length);
    }

    public async Task<DataFlowSummary> AnalyzeDataFlowAsync(FilePathWrapper filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        MethodDeclarationSyntax? method = null;
        if (contextSnippet != null)
        {
            var pos = SnippetLookup.TryResolve(text, contextSnippet, lineBefore, lineAfter, out var lookup) ? lookup.Matches.Single().Start : -1;
            if (pos >= 0)
            {
                method = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0)).AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            }
        }

        method ??= RoslynSentinel.Common.NameTargetLookup.RequireSingle(root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == methodName).ToList(), "Method", methodName);
        if (method == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' not found in '{filePath}'. Searched: method declarations in the file by name. Next: {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))} to list its methods, or {ToolCallHint.Call("Search", ("mode", "method"), ("query", methodName))}. {DeclarationNotFoundDetail.Build("method", "methodName", methodName, root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text), filePath, includeOutlineCall: false)}", "method", methodName);
        }

        if (method.Body == null)
        {
            throw new ToolTargetIneligibleException($"Method '{methodName}' in '{filePath}' has no body (abstract, extern or interface member), so data-flow analysis does not apply. Next: pick a method with a body from GetFileOutline.");
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            throw new ToolInvalidArgumentException($"Semantic model for '{filePath}' is unavailable. Next: confirm the project compiles with {ToolCallHint.Call("Build")}.");
        }

        DataFlowAnalysis flow;
        try
        {
            flow = model.AnalyzeDataFlow(method.Body)!;
        }
        // TODO bare catch swallows Roslyn failures into a warning string; throw ToolTargetIneligibleException instead
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

    /// <summary>
    /// Annotates the outermost qualified names and member accesses whose symbol lives in <paramref name="namespaceName"/>,
    /// so the Simplifier pass that follows has something to shorten. Names inside using directives are skipped.
    /// </summary>
    private static async Task<Document> AnnotateNamesInAddedNamespaceAsync(Document document, string namespaceName, CancellationToken cancellationToken)
    {
        if (namespaceName.StartsWith("static "))
        {
            return document;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
        {
            return document;
        }

        var names = root.DescendantNodes()
            .Where(n => n is QualifiedNameSyntax or MemberAccessExpressionSyntax)
            .Where(n => n.Parent is not (QualifiedNameSyntax or MemberAccessExpressionSyntax))
            .Where(n => !n.Ancestors().OfType<UsingDirectiveSyntax>().Any())
            .Where(n => model.GetSymbolInfo(n, cancellationToken).Symbol?.ContainingNamespace?.ToDisplayString() == namespaceName)
            .ToList();
        if (names.Count == 0)
        {
            return document;
        }

        var annotatedRoot = root.ReplaceNodes(names, (original, _) => original.WithAdditionalAnnotations(Simplifier.Annotation));
        return document.WithSyntaxRoot(annotatedRoot);
    }

    public async Task<DocumentEditResult> AddUsingDirectiveAsync(FilePathWrapper filePath, string namespaceName, bool simplifyExisting = false, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException("Cannot edit: syntax root not found.");
        }

        // Idempotency check
        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        if (root.Usings.Any(u => u.Name?.ToString() == targetName))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.AlreadyInTargetState,
                FilePath = filePath,
                Message = "Using directive already exists.",
                UpdatedText = root.ToFullString()
            };
        }

        // The resolved line ending (IWorkspaceMutator.ResolveEol), not a hard-coded CRLF.
        var originalText = await document.GetTextAsync(cancellationToken);
        var eol = await _workspaceManager.ResolveEolAsync(document, cancellationToken);
        UsingDirectiveSyntax newUsing;
        if (namespaceName.StartsWith("static "))
        {
            newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space), null, SyntaxFactory.ParseName(namespaceName[7..])).WithTrailingTrivia(SyntaxFactory.EndOfLine(eol));
        }
        else
        {
            newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName)).WithTrailingTrivia(SyntaxFactory.EndOfLine(eol));
        }

        var newRoot = root.AddUsings(newUsing.WithAdditionalAnnotations(Formatter.Annotation));
        var formattedDoc = await RoslynSentinel.Common.RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, eol, cancellationToken);
        if (simplifyExisting)
        {
            // Semantic-safe shortening of now-redundant fully-qualified names. Names resolving into the added
            // namespace are annotated first, because Simplifier.ReduceAsync only touches annotated nodes. It then
            // consults the semantic model per-node, so it only reduces a qualified name when doing so introduces no
            // ambiguity in this file.
            formattedDoc = await AnnotateNamesInAddedNamespaceAsync(formattedDoc, namespaceName, cancellationToken);
            formattedDoc = await Simplifier.ReduceAsync(formattedDoc, Simplifier.Annotation, cancellationToken: cancellationToken);
            var simplifiedRoot = await formattedDoc.GetSyntaxRootAsync(cancellationToken);
            formattedDoc = await RoslynFormattingHelper.FormatWholeDocumentAsync(formattedDoc.WithSyntaxRoot(simplifiedRoot!.WithAdditionalAnnotations(Simplifier.Annotation)), eol, originalText, cancellationToken);
        }

        // Roslyn's Formatter emits the platform newline for any line break it creates or re-flows, so
        // a file that used one consistent style (e.g. LF on Windows) would come back mixed. When the
        // original was single-style, hold the result to that style; an already-mixed file is left as
        // the formatter produced it rather than rewriting its minority lines.
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        if (EolChangeGuard.Count(originalText.ToString()).IsSingleStyle)
        {
            formattedText = EolUtilities.NormalizeEol(formattedText, eol);
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = formattedText
        };
    }

    public async Task<DocumentEditResult> RemoveUsingDirectiveAsync(FilePathWrapper filePath, string namespaceName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException("Cannot edit: syntax root not found.");
        }

        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        var isStaticTarget = namespaceName.StartsWith("static ");
        var existing = root.Usings.FirstOrDefault(u => u.Name?.ToString() == targetName && u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) == isStaticTarget);
        if (existing == null)
        {
            var usingNames = root.Usings.Select(u => (u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? "static " : "") + u.Name?.ToString()).OfType<string>().ToList();
            var detail = usingNames.Count == 0
                ? DeclarationNotFoundDetail.BuildNoneDeclared("using directive", filePath)
                : DeclarationNotFoundDetail.Build("using directive", "namespaceName", namespaceName, usingNames, filePath);
            throw new ToolNotFoundException($"Using directive '{namespaceName}' not found. {detail}", "using directive", namespaceName);
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.RemoveNodePreservingTriviaAsync(document, root, existing, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken: cancellationToken)
        };
    }

    public async Task<List<UsingDirectiveInfo>> GetUsingDirectivesAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return [];
        }

        return root.Usings.Select(u => new UsingDirectiveInfo(Name: u.Name?.ToString() ?? "", IsStatic: u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword), Alias: u.Alias?.Name.ToString())).ToList();
    }

    public async Task<DocumentEditResult> AddSummaryCommentAsync(FilePathWrapper filePath, string targetName, string summaryText, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containingTypeName = null, CancellationToken cancellationToken = default, PreferredTarget preferredTarget = PreferredTarget.constructor)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        return await AddSummaryCommentCoreAsync(document, filePath, targetName, summaryText, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken, preferredTarget);
    }

    /// <summary>
    /// Core of <see cref="AddSummaryCommentAsync"/>, factored out to accept a <see cref="Document"/>
    /// directly instead of always resolving one from <c>_workspaceManager.GetSolutionAsync(ReadSource.Committed, ...)</c>.
    /// Lets a caller that's evolving its own local <see cref="Solution"/> fork across multiple edits
    /// (e.g. <c>CommentingEngine</c> commenting several members in the same file before a single
    /// disk write) reuse this logic without each call reading back the workspace's committed state.
    /// </summary>
    public async Task<DocumentEditResult> AddSummaryCommentCoreAsync(Document document, FilePathWrapper filePath, string targetName, string summaryText, string? contextSnippet, string? lineBefore, string? lineAfter, string? containingTypeName, CancellationToken cancellationToken, PreferredTarget preferredTarget = PreferredTarget.constructor)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            throw new ToolInvalidArgumentException("Cannot edit: syntax root not found.");
        }

        SyntaxNode? target = null;
        var candidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.PreferKind(SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken), preferredTarget));
        candidates = SyntaxTargetResolver.FilterByContainingType(candidates, containingTypeName);
        target = SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
            (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode))?.Node;

        if (target == null)
        {
            throw new ToolNotFoundException($"Cannot edit: target not found. {DeclarationNotFoundDetail.Build("declaration", "targetName", targetName, DeclarationNamesIn(root), filePath)}", "declaration", targetName);
        }

        // baseIndent is derived first because docText bakes it into every line of the synthetic doc
        // comment (ParseMemberDeclaration has no notion of "the target's real indent" -> whatever
        // whitespace is in the string is exactly what ends up in the parsed trivia).
        var baseIndent = target.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
        var indentText = baseIndent != default ? baseIndent.ToFullString() : "";
        var normalizedSummary = NormalizeSummaryText(summaryText);
        var docText = BuildDocCommentText(target, indentText, normalizedSummary, await _workspaceManager.ResolveEolAsync(document, cancellationToken));
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
    private static string BuildDocCommentText(SyntaxNode target, string indentText, string normalizedSummary, string eol)
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
        // eol is the resolved line ending (IWorkspaceMutator.ResolveEol), not a hard-coded "\n".
        return string.Join(eol, lines);
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

    public async Task<DocumentEditResult> RemoveSummaryCommentAsync(FilePathWrapper filePath, string targetName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containingTypeName = null, CancellationToken cancellationToken = default, PreferredTarget preferredTarget = PreferredTarget.constructor)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            throw new ToolInvalidArgumentException("Cannot edit: syntax root not found.");
        }

        SyntaxNode? target;
        var candidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.PreferKind(SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken), preferredTarget));
        candidates = SyntaxTargetResolver.FilterByContainingType(candidates, containingTypeName);
        target = SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
            (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode))?.Node;

        if (target == null)
        {
            throw new ToolNotFoundException($"Cannot edit: target not found. {DeclarationNotFoundDetail.Build("declaration", "targetName", targetName, DeclarationNamesIn(root), filePath)}", "declaration", targetName);
        }

        if (!target.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoCandidatesFound,
                FilePath = filePath,
                Message = "No summary comment present.",
                UpdatedText = root.ToFullString()
            };
        }

        var stripped = target.GetLeadingTrivia().Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)).ToList();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root, target, target.WithLeadingTrivia(SyntaxFactory.TriviaList(stripped)), await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken, RoslynFormattingHelper.TriviaEditIntent.ReplaceLeading)
        };
    }

    public async Task<(EditOutcome Outcome, string? Message, string? SummaryText)> GetSummaryCommentAsync(FilePathWrapper filePath, string targetName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containingTypeName = null, CancellationToken cancellationToken = default, PreferredTarget preferredTarget = PreferredTarget.constructor)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            throw new ToolInvalidArgumentException($"Syntax root of '{filePath}' is unavailable. Next: confirm the file is a plain .cs source file with {ToolCallHint.Call("GetFileOutline")}.");
        }

        SyntaxNode? target;
        var candidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.PreferKind(SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken), preferredTarget));
        candidates = SyntaxTargetResolver.FilterByContainingType(candidates, containingTypeName);
        target = SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
            (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode))?.Node;

        if (target == null)
        {
            throw new ToolNotFoundException($"Target '{targetName}' not found in '{filePath}'. Searched: symbols named '{targetName}' in the file. Next: {ToolCallHint.Call("Search", ("mode", "symbol"), ("query", targetName))} or {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))}. {DeclarationNotFoundDetail.Build("declaration", "targetName", targetName, DeclarationNamesIn(root), filePath, includeOutlineCall: false)}", "symbol", targetName);
        }

        var docTrivia = target.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        if (docTrivia == default)
        {
            return (EditOutcome.NoCandidatesFound, $"No summary comment present on '{targetName}' in '{filePath}'. Next: {ToolCallHint.Call("SummaryComment", ("operation", "add"), ("targetName", targetName), ("filePath", filePath))} to add one.", null);
        }

        var lines = docTrivia.ToFullString().Split('\n')
            .Select(l => l.Trim().TrimStart('/').Trim())
            .Select(l => l.StartsWith("<summary>", StringComparison.Ordinal) ? l.Substring("<summary>".Length) : l)
            .Select(l => l.EndsWith("</summary>", StringComparison.Ordinal) ? l.Substring(0, l.Length - "</summary>".Length) : l)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return (EditOutcome.Modified, null, string.Join(" ", lines));
    }

    /// <summary>
    /// Names of the members and enum members declared under <paramref name="root"/>, the candidate pool for a
    /// declaration not-found message (see <see cref="DeclarationNotFoundDetail"/>).
    /// </summary>
    private static IEnumerable<string> DeclarationNamesIn(SyntaxNode root)
    {
        return root.DescendantNodes().Select(n => n is EnumMemberDeclarationSyntax em ? em.Identifier.Text : n is MemberDeclarationSyntax md ? SyntaxTargetResolver.GetMemberName(md) : null).OfType<string>();
    }

    public async Task<DocumentEditResult> WrapInTryCatchAsync(FilePathWrapper filePath, int startLine, int endLine, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException("Cannot edit: syntax root not found.");
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
            throw new ToolNotFoundException($"Could not find a suitable block. Choose a range that covers complete statements inside a method body (not a constructor header, field or attribute list); use {ToolCallHint.Call("GetFileOutline")} to find the lines.", "block");
        }

        var targeted = block.Statements.Where(s => StatementStartLine(s) <= endLine && StatementEndLine(s) >= startLine).ToList();
        if (targeted.Count == 0)
        {
            throw new ToolNotFoundException($"Could not find any statements in the specified range. Choose a range that covers complete statements inside a method body (not a constructor header, field or attribute list); use {ToolCallHint.Call("GetFileOutline")} to find the lines.", "statement");
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
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root, block, newBlock, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken)
        };
    }

    /// <summary>
    /// Wraps a code snippet (identified via contextSnippet, lineBefore/lineAfter) in a try/catch block.
    /// Uses SnippetLookup.ResolveOrThrow to locate the snippet, then wraps the enclosing statements.
    /// </summary>
    public async Task<DocumentEditResult> WrapInTryCatchAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore, string? lineAfter, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            throw new ToolInvalidArgumentException("Cannot edit: syntax root not found.");
        }

        // Find the snippet position
        var snippetPos = SnippetLookup.ResolveOrThrow(sourceText, contextSnippet, lineBefore, lineAfter).Start;
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
            throw new ToolNotFoundException($"Could not find a suitable block for the snippet. Choose a range that covers complete statements inside a method body (not a constructor header, field or attribute list); use {ToolCallHint.Call("GetFileOutline")} to find the lines.", "block");
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
            throw new ToolNotFoundException($"Could not find any statements containing the snippet. Choose a range that covers complete statements inside a method body (not a constructor header, field or attribute list); use {ToolCallHint.Call("GetFileOutline")} to find the lines.", "statement");
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
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root, block, newBlock, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken)
        };
    }

    public async Task<DocumentEditResult> WrapInRegionAsync(FilePathWrapper filePath, int startLine, int endLine, string regionName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

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
    /// Uses SnippetLookup.ResolveOrThrow to locate the snippet, then derives the line number.
    /// </summary>
    public async Task<DocumentEditResult> WrapInRegionAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore, string? lineAfter, string regionName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var text = await document.GetTextAsync(cancellationToken);
        // Find the snippet position
        var snippetPos = SnippetLookup.ResolveOrThrow(text, contextSnippet, lineBefore, lineAfter).Start;
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

    public async Task<DocumentEditResult> UpdateXmlDocsFromSignatureAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException("Could not parse document.");
        }

        var method = RoslynSentinel.Common.NameTargetLookup.RequireSingle(root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == methodName).ToList(), "Method", methodName);
        if (method == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' not found in '{filePath}'. {DeclarationNotFoundDetail.Build("method", "methodName", methodName, root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text), filePath)}", "method", methodName);
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
            throw new ToolTargetIneligibleException("Could not parse XML documentation.");
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
                Outcome = EditOutcome.AlreadyInTargetState,
                FilePath = filePath,
                Message = "No changes needed.",
                UpdatedText = root.ToFullString()
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
        var document = DocumentLookup.RequireDocument(solution, filePath);

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
        throw new ToolNotImplementedException("InvertBooleanAsync is not implemented.");
    }

    // Removes the compiler's unused-using diagnostics (CS8019) from a freshly scaffolded split file. The original
    // document still declares the type, so the new document has a duplicate declaration (CS0101); that does not stop
    // usings from binding, so only the genuinely unused directives are reported.
    private static async Task<Document> RemoveUnusedUsingsAsync(Document newDoc, CancellationToken cancellationToken)
    {
        var semanticModel = await newDoc.GetSemanticModelAsync(cancellationToken);
        var root = await newDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (semanticModel == null || root == null)
        {
            return newDoc;
        }

        var unusedUsings = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == "CS8019")
            .Select(d => root.FindNode(d.Location.SourceSpan).AncestorsAndSelf().OfType<UsingDirectiveSyntax>().FirstOrDefault())
            .OfType<UsingDirectiveSyntax>()
            .Distinct()
            .ToList();
        if (unusedUsings.Count == 0)
        {
            return newDoc;
        }

        return newDoc.WithSyntaxRoot(root.RemoveNodes(unusedUsings, SyntaxRemoveOptions.KeepNoTrivia)!);
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

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>Return payload for <c>GetMethodSourceTool</c>.</summary>
public record MethodSourceResult
{
    /// <summary>Scope/truncation metadata for the containing file. See <see cref="ReadEnvelope"/>.</summary>
    public ReadEnvelope Envelope { get; init; } = null!;
    /// <summary>Condensed method declaration: modifiers, return type, name, and parameter list -> no body.</summary>
    public string Signature { get; init; } = "";
    /// <summary>Attributes declared on the method, in declaration order.</summary>
    public List<MethodAttributeInfo> Attributes { get; init; } = new();
    /// <summary>Complete source text of the method including attributes and body.</summary>
    public string Source { get; init; } = "";
}

/// <summary>One attribute applied to a method.</summary>
public record MethodAttributeInfo
{
    /// <summary>Attribute name as written in source, e.g. "MigrationCandidate" or "Obsolete".</summary>
    public string Name { get; init; } = "";
    /// <summary>Argument list contents (no outer parentheses), e.g. "\"AsyncBridgeCandidate\", Score = 80". Empty string when no arguments.</summary>
    public string Arguments { get; init; } = "";
}

/// <summary>
/// Plain implementation class for the read/navigation slice of workspace tools: GetMethodSourceTool,
/// GetFileOutlineTool, ListAllTool, SearchSolutionTextTool, GetOperationDetailTool, GetLargeResultTool. Method bodies
/// are verbatim moves from WorkspaceTools -> see docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md
/// (Decision 1-Amendment). Not an [McpServerToolType]; the MCP surface lives in WorkspaceReadNavigationTools.
///
/// WorkspaceTools.cs still holds the six live/registered copies of these tools (each with a
/// short comment pointing back here); this class is the shared implementation both
/// WorkspaceReadNavigationTools and WorkspaceTools delegate to.
/// </summary>
public class WorkspaceReadNavigationImpl
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger<WorkspaceReadNavigationImpl> _logger;
    // Points at the solution-wide shared instance (RoslynSentinel.Common.SharedJsonOptions.Default)
    // rather than constructing its own, per docs/current/plans/plan_shared_json_serializer_options.md.
    // Kept as a same-named field rather than replacing all 28 call sites with the fully-qualified
    // name, since this is an internal serialize/deserialize round-trip within this class either way.
    private static readonly JsonSerializerOptions _jsonOptions = RoslynSentinel.Common.SharedJsonOptions.Default;
    public WorkspaceReadNavigationImpl(IWorkspaceManager workspaceManager, ILogger<WorkspaceReadNavigationImpl> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // Candidate-suggestion behavior confirmed live: an agent asked to read a file at a slightly
    // wrong path (e.g. missing a subfolder) retried the identical wrong path 2-3 times before
    // giving up, even when ListSolutionItemsTool's own earlier output already showed the real path ->
    // the plain "file not found" message gave them nothing to act on. Searching the solution for
    // files sharing the requested filename turns most of these into a one-shot redirect; when
    // nothing matches by filename either, the message issues an explicit, unhedged directive
    // rather than a suggestion, since softer phrasing ("consider calling X") was observed not
    // changing the model's next action.
    internal static ResultError BuildFileNotFoundError(Solution solution, string normalizedPath)
    {
        var requestedFileName = Path.GetFileName(normalizedPath);
        var candidates = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFileName(d.FilePath), requestedFileName, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.FilePath!)
            .Distinct()
            .Take(5)
            .ToList();

        if (candidates.Count > 0)
        {
            return new ResultError("FileNotFound",
                $"'{requestedFileName}' does not exist at '{normalizedPath}'. A file with this name exists at a different path. " +
                $"You MUST retry with the correct path:\n" +
                string.Join("\n", candidates.Select(c => $"  - {c}")));
        }

        return new ResultError("FileNotFound",
            $"'{requestedFileName}' does not exist anywhere in the solution (searched {solution.Projects.Count()} project(s), no filename match). " +
            "You MUST call ListSolutionItems(kind: all) next to see every file actually in the solution before trying another path.");
    }

    /// <summary>
    /// Builds the MethodNotFound message for GetMethodSource. Parts, each only when it applies: the original sentence,
    /// a qualified-name hint, same-name non-method members in this file, same-name methods elsewhere in the solution,
    /// and the list of methods this file declares. Solution-wide lookup is best-effort and skipped on failure.
    /// </summary>
    internal static async Task<ResultError> BuildMethodNotFoundErrorAsync(Solution solution, SyntaxNode root, string methodName, string requestedFilePath, string? solutionRoot, CancellationToken cancellationToken)
    {
        var parts = new List<string>
        {
            $"Method or constructor '{methodName}' not found in '{requestedFilePath}'."
        };

        var name = methodName;
        var lastDot = methodName.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var bare = methodName[(lastDot + 1)..];
            var bareIsMethodInFile = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
                .Any(m => GetMethodOrCtorName(m).Equals(bare, StringComparison.OrdinalIgnoreCase));
            parts.Add($"'{methodName}' looks qualified; pass the bare method name '{bare}' (and the file that declares it) instead. " +
                (bareIsMethodInFile ? $"'{bare}' does exist as a method in this file." : $"'{bare}' is not a method in this file."));
            name = bare;
        }

        if (name.Length > 0)
        {
            var cmp = StringComparison.OrdinalIgnoreCase;
            string? otherKind = null;
            if (root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text.Equals(name, cmp)))
                otherKind = "property";
            else if (root.DescendantNodes().OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text.Equals(name, cmp))))
                otherKind = "field";
            else if (root.DescendantNodes().OfType<EventDeclarationSyntax>().Any(e => e.Identifier.Text.Equals(name, cmp)))
                otherKind = "event";
            else if (root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any(l => l.Identifier.Text.Equals(name, cmp)))
                otherKind = "local function";
            if (otherKind != null)
            {
                parts.Add($"'{name}' exists in this file as a {otherKind}, not a method; read it with Member(operation: view, memberName: '{name}').");
            }
        }

        try
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in solution.Projects)
            {
                var memberDeclarations = await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: true, SymbolFilter.Member, cancellationToken);
                var candidates = memberDeclarations.OfType<IMethodSymbol>().ToList();
                // Constructor symbols are named ".ctor", so a class-name query only reaches them through the type declaration.
                var typeDeclarations = await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: true, SymbolFilter.Type, cancellationToken);
                candidates.AddRange(typeDeclarations.OfType<INamedTypeSymbol>()
                    .SelectMany(t => t.Constructors)
                    .Where(c => c.MethodKind == MethodKind.Constructor && !c.IsImplicitlyDeclared));
                foreach (var symbol in candidates)
                {
                    var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location == null || location.SourceTree == null)
                    {
                        continue;
                    }
                    var fullPath = location.SourceTree.FilePath;
                    var displayPath = !string.IsNullOrEmpty(solutionRoot) ? Path.GetRelativePath(solutionRoot, fullPath) : fullPath;
                    var line = location.GetLineSpan().StartLinePosition.Line + 1;
                    var label = symbol.MethodKind == MethodKind.Constructor
                        ? $"{symbol.ContainingType?.Name} (constructor)"
                        : $"{symbol.ContainingType?.Name}.{symbol.Name}";
                    var entry = $"  - {displayPath}:{line} ({label})";
                    if (seen.Add(entry))
                    {
                        found.Add(entry);
                    }
                }
            }

            if (found.Count > 0)
            {
                var lines = string.Join("\n", found.Take(5));
                if (found.Count > 5)
                {
                    lines += $"\n  (+{found.Count - 5} more)";
                }
                parts.Add("A method or constructor with this name is declared in:\n" + lines + "\nRetry GetMethodSource with filePath set to the file that holds the one you mean.");
            }
            else
            {
                parts.Add($"No method or constructor named '{name}' is declared in any project of the loaded solution (tests included). If the name may be a substring or differently spelled, call Search(mode: method, query: '{name}', exactMatch: false); a file outside the loaded solution cannot be searched until it is loaded.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort lookup: skip the solution-wide step rather than leak exception text into the result.
        }

        var fileMethodNames = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
            .Select(GetMethodOrCtorName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        parts.Add(fileMethodNames.Count == 0
            ? "This file declares no methods or constructors."
            : "Methods and constructors in this file: " + string.Join(", ", fileMethodNames.Take(12)) + (fileMethodNames.Count > 12 ? $" (+{fileMethodNames.Count - 12} more)" : ""));

        return new ResultError("MethodNotFound", string.Join(" ", parts));
    }

    /// <summary>Name of the nearest enclosing class, struct, record or interface of <paramref name="member"/>, or null when there is none.</summary>
    private static string? GetNearestTypeName(SyntaxNode member) => member.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;

    /// <summary>
    /// Builds the Ambiguous error for GetMethodSource when a name is still declared more than once after the container
    /// filter, name match and interface rule. Lists each candidate (containing type, signature, line range, capped at 10)
    /// and names retries that work: <c>containerName</c> only for types with exactly one candidate (at most 3), and
    /// <c>ReadFile</c> with the first candidate's real line range.
    /// </summary>
    internal static ResultError BuildAmbiguousMethodError(IReadOnlyList<BaseMethodDeclarationSyntax> matches, string methodName, string? containerName, string requestedFilePath)
    {
        const int candidateCap = 10;
        var entries = matches.Select(m =>
        {
            var span = m.GetLocation().GetLineSpan();
            return (Method: m, Type: GetNearestTypeName(m), Signature: BuildSignature(m), Start: span.StartLinePosition.Line + 1, End: span.EndLinePosition.Line + 1);
        }).ToList();

        var candidates = entries.Take(candidateCap).Select(e => new AmbiguousCandidate(
            FilePath: requestedFilePath,
            Line: e.Start,
            Kind: e.Method is ConstructorDeclarationSyntax ? "constructor" : "method",
            Signature: e.Signature,
            ContainingType: e.Type ?? string.Empty,
            ContextSnippet: e.Method.ToString().Split('\n')[0].Trim(),
            LineBefore: null,
            Counted: true)).ToList();

        var listed = entries.Take(candidateCap).Select((e, i) =>
            $"{i + 1}. {(e.Type is null ? "" : e.Type + ".")}{Regex.Replace(e.Signature, @"\s+", " ")} - lines {e.Start}-{e.End}");
        var more = entries.Count > candidateCap ? $" (+{entries.Count - candidateCap} more)" : "";
        var narrowed = string.IsNullOrWhiteSpace(containerName) ? "" : $", narrowed to type '{containerName}'";
        var parts = new List<string>
        {
            $"Method or constructor '{methodName}' is declared {entries.Count} times in '{requestedFilePath}' (searched the whole file{narrowed}). " +
            $"GetMethodSource never picks one for you. Candidates: {string.Join("; ", listed)}{more}."
        };

        var singleCandidateTypes = entries.Where(e => e.Type is not null)
            .GroupBy(e => e.Type!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Key)
            .Take(3)
            .ToList();
        if (singleCandidateTypes.Count > 0)
        {
            var retries = singleCandidateTypes.Select(t => $"GetMethodSource(filePath: '{requestedFilePath}', methodName: '{methodName}', containerName: '{t}')");
            parts.Add($"To choose a type, retry {string.Join(" or ", retries)}.");
        }

        parts.Add($"To choose between overloads, read the exact lines: ReadFile(filePath: '{requestedFilePath}', startLine: {entries[0].Start}, endLine: {entries[0].End}).");

        return new ResultError(ToolErrorCode.Ambiguous, string.Join(" ", parts), null, ToolErrorMapper.ToStructuredDetail(candidates));
    }

    public async Task<SentinelCallToolResult<MethodSourceResult, ResultError>> GetMethodSource(
        ToolCallReason reason,
        string filePath, string methodName,
        string? containerName = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var normalizedPath = Path.GetFullPath(filePathResolved);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return new SentinelCallToolResult<MethodSourceResult, ResultError>()
                {
                    IsError = true,
                    ErrorData = BuildFileNotFoundError(solution, normalizedPath)
                };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken: cancellationToken);
            if (root == null)
            {
                return new SentinelCallToolResult<MethodSourceResult, ResultError>()
                {
                    IsError = true,
                    ErrorData = new ResultError("SyntaxRootNotFound", "Syntax root not found.")
                };
            }

            // Constructors are ConstructorDeclarationSyntax, not MethodDeclarationSyntax, but callers
            // naturally pass the class name for "give me the source of its constructor" -> resolve
            // both node kinds under the shared BaseMethodDeclarationSyntax base.
            // containerName is optional on purpose: every existing call names a method that is unique in its file and
            // passes no container, so a mandatory parameter would break them. The first match is never returned when
            // several remain; the Ambiguous error lists every candidate and the retry that picks one.
            var hasContainer = !string.IsNullOrWhiteSpace(containerName);
            var declared = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().ToList();
            var inScope = hasContainer
                ? declared.Where(m => SyntaxTargetResolver.ContainingTypeMatches(GetNearestTypeName(m), containerName!)).ToList()
                : declared;
            var matches = inScope.Where(m => GetMethodOrCtorName(m).Equals(methodName, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
            {
                matches = inScope.Where(m => GetMethodOrCtorName(m).Equals(methodName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Interface rule: a declaration in an interface yields to implementations (mirrors SyntaxTargetResolver.PreferNonInterfaceMember).
            if (matches.Count > 1 && matches.Any(m => m.Parent is InterfaceDeclarationSyntax) && matches.Any(m => m.Parent is not InterfaceDeclarationSyntax))
            {
                matches = matches.Where(m => m.Parent is not InterfaceDeclarationSyntax).ToList();
            }

            if (matches.Count == 0)
            {
                if (!hasContainer)
                {
                    return new SentinelCallToolResult<MethodSourceResult, ResultError>()
                    {
                        IsError = true,
                        ErrorData = await BuildMethodNotFoundErrorAsync(solution, root, methodName, filePathResolved, _workspaceManager.GetSolutionRoot(), cancellationToken)
                    };
                }

                var sourceText = await document.GetTextAsync(cancellationToken);
                var (missMessage, missCandidates) = SyntaxTargetResolver.BuildContainerMissDetail(root, sourceText, methodName, containerName!, filePathResolved,
                    retryCallTemplate: "GetMethodSource(filePath: '{file}', methodName: '{member}', containerName: '{container}')", methodsOnly: true);
                return new SentinelCallToolResult<MethodSourceResult, ResultError>()
                {
                    IsError = true,
                    ErrorData = new ResultError("MethodNotFound", missMessage, null, ToolErrorMapper.ToStructuredDetail(missCandidates))
                };
            }

            if (matches.Count > 1)
            {
                return new SentinelCallToolResult<MethodSourceResult, ResultError>()
                {
                    IsError = true,
                    ErrorData = BuildAmbiguousMethodError(matches, methodName, containerName, filePathResolved)
                };
            }

            var method = matches[0];

            var methodSource = method.ToFullString();
            var methodBytes = System.Text.Encoding.UTF8.GetByteCount(methodSource);
            var attributes = ExtractAttributes(method);
            var signature = BuildSignature(method);
            _logger.LogInformation("GetMethodSource: {SizeBytes} bytes for '{MethodName}'", methodBytes, methodName);
            const int thresholdBytes = LargeResultHelper.OffloadThresholdBytes;
            var solutionRoot = _workspaceManager.GetSolutionRoot();

            var fileText = await document.GetTextAsync(cancellationToken);
            var fileLineCount = fileText.Lines.Count;
            var methodSpan = method.GetLocation().GetLineSpan();
            var envelope = ReadEnvelopeBuilder.BuildForMember(
                fileLineCount,
                memberFromLine: methodSpan.StartLinePosition.Line + 1,
                memberToLine: methodSpan.EndLinePosition.Line + 1,
                memberByteCount: methodBytes);

            if (methodBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var fullResult = new MethodSourceResult { Envelope = envelope, Signature = signature, Source = methodSource, Attributes = attributes };
                var stored = await LargeResultHelper.StoreLargeResultAsync(fullResult, solutionRoot, ResultWrapperType.MethodSource, cancellationToken);
                return new SentinelCallToolResult<MethodSourceResult, ResultError>
                {
                    IsError = false,
                    LargeResult = new LargeResultInfo(resultType: "MethodSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: methodBytes, totalRecords: 1, message: $"Result is {methodBytes} bytes (threshold: {thresholdBytes}). " + $"Use GetLargeResult(resultId: \"{stored.resultId}\") to page through results."),
                    SuccessData = new MethodSourceResult { Envelope = envelope, Signature = signature, Attributes = attributes },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new SentinelCallToolResult<MethodSourceResult, ResultError>()
            {
                IsError = false,
                SuccessData = new MethodSourceResult
                {
                    Envelope = envelope,
                    Signature = signature,
                    Source = methodSource,
                    Attributes = attributes
                },
                WorkspaceVersion = _workspaceManager.WorkspaceVersion,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodSource failed for '{MethodName}' in '{FilePathWrapper}'", methodName, filePathResolved);
            return new SentinelCallToolResult<MethodSourceResult, ResultError>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetMethodSource")
            };
        }
    }

    public async Task<SentinelCallToolResult<FileOutlineResult, ResultError>> GetFileOutline(
        ToolCallReason reason,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var normalizedPath = Path.GetFullPath(filePathResolved);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return new SentinelCallToolResult<FileOutlineResult, ResultError>()
                {
                    IsError = true,
                    ErrorData = BuildFileNotFoundError(solution, normalizedPath)
                };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken: cancellationToken);
            if (root == null)
            {
                return new SentinelCallToolResult<FileOutlineResult, ResultError>()
                {
                    IsError = true,
                    ErrorData = new ResultError("SyntaxRootNotFound", "Syntax root not found.")
                };
            }

            var items = ExtractOutlineItems(root);

            var fileText = await document.GetTextAsync(cancellationToken);
            var fileLineCount = fileText.Lines.Count;
            var fileByteCount = System.Text.Encoding.UTF8.GetByteCount(fileText.ToString());
            var envelope = ReadEnvelopeBuilder.Build(fileLineCount, fileByteCount, returnedFromLine: 1, returnedToLine: fileLineCount);

            return new SentinelCallToolResult<FileOutlineResult, ResultError>()
            {
                IsError = false,
                SuccessData = new FileOutlineResult { Envelope = envelope, Symbols = items },
                WorkspaceVersion = _workspaceManager.WorkspaceVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileOutline failed for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<FileOutlineResult, ResultError>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetFileOutline")
            };
        }
    }

    /// <summary>Walks a document's syntax tree and extracts the same outline entries GetFileOutlineTool returns for one file -> shared with ListAllTool, which runs this across every document in the solution.</summary>
    internal static List<OutlineItem> ExtractOutlineItems(SyntaxNode root)
    {
        var items = new List<OutlineItem>();
        foreach (var node in root.DescendantNodes())
        {
            string? kind = null;
            string? name = null;
            string? container = null;
            switch (node)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    kind = "namespace";
                    name = ns.Name.ToString();
                    break;
                case ClassDeclarationSyntax cls:
                    kind = "class";
                    name = cls.Identifier.Text;
                    container = (cls.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (cls.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case InterfaceDeclarationSyntax iface:
                    kind = "interface";
                    name = iface.Identifier.Text;
                    container = (iface.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (iface.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case MethodDeclarationSyntax method:
                    kind = "method";
                    name = method.Identifier.Text;
                    container = (method.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case PropertyDeclarationSyntax prop:
                    kind = "property";
                    name = prop.Identifier.Text;
                    container = (prop.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                // Struct/record/enum, and enum members, constructors, and fields were never
                // covered here -> a file containing only these (e.g. a pure enum file) produced
                // an outline with nothing but a "namespace" entry, silently implying the file had
                // no commentable/editable members at all. Confirmed live: an agent asked to add
                // summary comments to every member skipped OrderStatus.cs's enum entirely because
                // GetFileOutlineTool gave no indication OrderStatus existed, then SummaryCommentTool also
                // failed once the agent tried it anyway (separate gap, see GetMemberName).
                case StructDeclarationSyntax @struct:
                    kind = "struct";
                    name = @struct.Identifier.Text;
                    container = (@struct.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (@struct.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case RecordDeclarationSyntax record:
                    kind = "record";
                    name = record.Identifier.Text;
                    container = (record.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (record.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case EnumDeclarationSyntax @enum:
                    kind = "enum";
                    name = @enum.Identifier.Text;
                    container = (@enum.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (@enum.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case EnumMemberDeclarationSyntax enumMember:
                    kind = "enum member";
                    name = enumMember.Identifier.Text;
                    container = (enumMember.Parent as EnumDeclarationSyntax)?.Identifier.Text;
                    break;
                case ConstructorDeclarationSyntax ctor:
                    kind = "constructor";
                    name = ctor.Identifier.Text;
                    container = (ctor.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case FieldDeclarationSyntax field:
                    kind = "field";
                    name = field.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                    container = (field.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
            }

            if (kind == null || name == null)
            {
                continue;
            }

            var span = node.GetLocation().GetLineSpan();
            items.Add(new OutlineItem(Kind: kind, Name: name, Container: container, StartLine: span.StartLinePosition.Line + 1, EndLine: span.EndLinePosition.Line + 1));
        }

        return items;
    }

    public async Task<SentinelCallToolResult<object>> ListAll(
        ToolCallReason reason,
        ListAllKind kind = ListAllKind.all,
        string? projectName = null,
        string? nameFilter = null,
        bool exactMatch = true,
        int? maxItems = null,
        bool includeTests = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var projects = solution.Projects.AsEnumerable();
            if (!string.IsNullOrEmpty(projectName))
            {
                projects = projects.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            }
            var hideTestProjects = string.IsNullOrEmpty(projectName) && !includeTests;
            var hiddenByProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var kindFilter = kind switch
            {
                ListAllKind.all => null,
                ListAllKind.enumMember => "enum member",
                _ => kind.ToString()
            };
            var entries = new List<SolutionSymbolEntry>();
            var totalMatches = 0;
            var allMatches = maxItems is null ? null : new List<(string FilePath, string ProjectName)>();
            foreach (var project in projects)
            {
                var isHiddenTestProject = hideTestProjects && TestProjectDetector.IsTestProject(project);
                foreach (var document in project.Documents)
                {
                    if (string.IsNullOrEmpty(document.FilePath))
                    {
                        continue;
                    }

                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null)
                    {
                        continue;
                    }

                    var filePath = new FilePathWrapper(document.FilePath);
                    foreach (var item in ExtractOutlineItems(root))
                    {
                        if (kindFilter != null && item.Kind != kindFilter)
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(nameFilter))
                        {
                            var matches = exactMatch
                                ? string.Equals(item.Name, nameFilter, StringComparison.OrdinalIgnoreCase)
                                : item.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
                            if (!matches)
                            {
                                continue;
                            }
                        }

                        if (isHiddenTestProject)
                        {
                            hiddenByProject.TryGetValue(project.Name, out var hiddenSoFar);
                            if (hiddenSoFar < HiddenTestMatchCapPerProject)
                            {
                                hiddenByProject[project.Name] = hiddenSoFar + 1;
                            }

                            continue;
                        }

                        totalMatches++;
                        allMatches?.Add((document.FilePath, project.Name));
                        if (maxItems is null || entries.Count < maxItems.Value)
                        {
                            entries.Add(new SolutionSymbolEntry(filePath, item.Kind, item.Name, item.Container, item.StartLine, item.EndLine));
                        }
                    }
                }
            }

            var hiddenTestHint = hiddenByProject.Count == 0
                ? null
                : BuildHiddenTestMatchHint(hiddenByProject, hasVisibleHits: totalMatches > 0);

            var result = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                entries,
                solutionRoot,
                typeof(SolutionSymbolEntry).Name,
                ResultWrapperType.SolutionSymbolEntryList,
                totalRecords: entries.Count,
                cancellationToken: cancellationToken);

            if (totalMatches > entries.Count)
            {
                var summary = SummarizeListResult.BuildWithProjects(allMatches!, m => m.FilePath, m => m.ProjectName, maxFilesShown: 10);
                return result with
                {
                    TotalRecords = totalMatches,
                    HasMoreData = true,
                    ListSummary = summary,
                    StatusMessage = hiddenTestHint,
                    WarningDetails = $"Truncated: showing the first {entries.Count} of {totalMatches} matching declarations (cap {maxItems}), spread over {summary.FileCount} files and {summary.ByProject!.Count} projects - listSummary.byProject and listSummary.byFile (top {summary.ByFile.Count} files, {summary.TruncatedFileCount} more files not shown) count ALL matches, use them to pick where to look. Narrow with query (name filter) or projectName, or use mode: symbol. For all callers/implementations of a symbol use Search(mode: references) or QuerySymbolRelationships; for orientation use ListAll or GetFileOutline."
                };
            }

            if (totalMatches == 0 && !string.IsNullOrEmpty(nameFilter))
            {
                return result with
                {
                    TotalRecords = 0,
                    StatusMessage = $"No {(kindFilter ?? "declaration")} named '{nameFilter}' found ({(exactMatch ? "exact" : "substring")} match). Use mode: symbol to search every kind at once" + (exactMatch ? ", or pass exactMatch: false for a substring match." : ".") + (hiddenTestHint is null ? string.Empty : " " + hiddenTestHint)
                };
            }

            return result with { TotalRecords = totalMatches, StatusMessage = hiddenTestHint };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListAll failed (kind={Kind}, projectName={ProjectName})", kind, projectName);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ListAll")
            };
        }
    }

    // A '/'-containing fileGlob whose first path segment matches a test project's name (for example
    // "MyProj.Tests/**/*.cs") is an explicit request for that project, so includeTests is implied
    // (same rule as naming a test project in projectName/filePath). A segment made only of '*'
    // ("**/*.cs", "*/Foo.cs") names nothing and never counts. Malformed globs still raise
    // GlobSyntaxException from GlobToRegex.
    private static bool GlobNamesTestProject(string? fileGlob, IEnumerable<string> testProjectNames)
    {
        if (string.IsNullOrEmpty(fileGlob))
        {
            return false;
        }

        var normalizedGlob = fileGlob.Replace('\\', '/');
        var slashIndex = normalizedGlob.IndexOf('/');
        if (slashIndex <= 0)
        {
            return false;
        }

        var firstSegment = normalizedGlob[..slashIndex];
        if (firstSegment.All(c => c == '*'))
        {
            return false;
        }

        var segmentRegex = "^" + GlobToRegex(firstSegment) + "$";
        return testProjectNames.Any(name => Regex.IsMatch(name, segmentRegex, RegexOptions.IgnoreCase));
    }

    // Cap on hidden (test-project) match lines counted per project when includeTests is false. These
    // counts only feed the status-message hint and never consume maxResults.
    private const int HiddenTestMatchCapPerProject = 1000;

    private static string BuildHiddenTestMatchHint(IReadOnlyDictionary<string, int> hiddenCounts, bool hasVisibleHits)
    {
        var total = hiddenCounts.Values.Sum();
        var anyCapped = hiddenCounts.Values.Any(c => c >= HiddenTestMatchCapPerProject);
        var totalText = anyCapped ? $"{total}+" : total.ToString();
        var perProject = string.Join(", ", hiddenCounts
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} {kv.Value}{(kv.Value >= HiddenTestMatchCapPerProject ? "+" : string.Empty)}"));
        return hasVisibleHits
            ? $"+{totalText} more in test projects ({perProject}), hidden because includeTests is false - rerun with includeTests: true to include them."
            : $"{totalText} match(es) in test projects ({perProject}) are hidden because includeTests is false - rerun with includeTests: true to see them.";
    }

    // Recovery pass (empty visible result only): at most this many relaxed candidates are counted,
    // each capped per project, so the extra scan stays cheap. Relaxed hits are never returned.
    private const int RecoveryCandidateLimit = 6;

    private const int RecoveryCountCapPerProject = 1000;

    // Kind: 0 = same pattern with fileGlob dropped, 1 = dotted-name prefix (Foo.Bar.Baz -> Foo.Bar, Foo),
    // 2 = one alternative of a '|' alternation. Candidates are literal case-insensitive substrings.
    // An empty list means there is nothing to relax (no fileGlob, no dotted name, no '|'), and the
    // recovery pass is skipped entirely.
    private static List<(string Text, bool DropGlob, int Kind)> BuildRelaxationCandidates(string pattern, string? fileGlob, bool globMatchedAnyDocument)
    {
        var candidates = new List<(string Text, bool DropGlob, int Kind)>();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return candidates;
        }

        var hasGlob = !string.IsNullOrEmpty(fileGlob);
        if (hasGlob)
        {
            candidates.Add((pattern, true, 0));
        }

        // Glob-kept candidates cannot hit when the glob matched no document at all.
        var globKeptCanHit = !hasGlob || globMatchedAnyDocument;
        if (globKeptCanHit && Regex.IsMatch(pattern, @"^\w+(\.\w+)+$"))
        {
            var segments = pattern.Split('.');
            for (var length = segments.Length - 1; length >= 1; length--)
            {
                candidates.Add((string.Join(".", segments.Take(length)), false, 1));
            }
        }

        if (globKeptCanHit && pattern.Contains('|'))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pattern };
            foreach (var alternative in pattern.Split('|').Select(a => a.Trim()).Where(a => a.Length > 0))
            {
                if (seen.Add(alternative))
                {
                    candidates.Add((alternative, false, 2));
                }
            }
        }

        return candidates.Take(RecoveryCandidateLimit).ToList();
    }

    private static string FormatRecoveryCount(IReadOnlyDictionary<string, int> perProject)
    {
        var sum = perProject.Values.Sum();
        return perProject.Values.Any(c => c >= RecoveryCountCapPerProject) ? $"{sum}+" : sum.ToString();
    }

    private static List<KeyValuePair<string, int>> TopRecoveryProjects(IReadOnlyDictionary<string, int> perProject)
    => perProject.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(3).ToList();

    // Runs only when a text search has no visible hit: ONE extra flattened parallel pass counts literal
    // case-insensitive substring hits (no regex) for the relaxed candidates, split into visible and
    // hidden test-project hits and per project. The result is advice text only - relaxed matches are
    // never returned as hits. Returns null when there is nothing to relax or no candidate hit anything.
    private static async Task<string?> BuildRecoveryHintAsync(
        Solution solution,
        IReadOnlyList<(Document Document, string ProjectName, bool IsTest)> documents,
        string pattern, string? fileGlob, bool includeTests, bool effectiveIncludeTests,
        int globMatchedDocCount, string? solutionRoot, CancellationToken cancellationToken)
    {
        var candidates = BuildRelaxationCandidates(pattern, fileGlob, globMatchedDocCount > 0);
        if (candidates.Count == 0)
        {
            return null;
        }

        var hasGlob = !string.IsNullOrEmpty(fileGlob);
        var visible = candidates.Select(_ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase)).ToArray();
        var inTests = candidates.Select(_ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase)).ToArray();
        var options = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount, TaskScheduler = TaskScheduler.Default };

        await Parallel.ForEachAsync(documents, options, async (item, ct) =>
        {
            var (document, projectName, isTest) = item;
            var globOk = !hasGlob || GlobMatchesFileName(new FilePathWrapper(document.FilePath ?? "", solutionRoot), fileGlob!);
            var active = new List<int>();
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].DropGlob || globOk)
                {
                    active.Add(i);
                }
            }

            if (active.Count == 0)
            {
                return;
            }

            var text = await document.GetTextAsync(ct);
            var counts = new int[candidates.Count];
            foreach (var line in text.ToString().Split('\n'))
            {
                foreach (var i in active)
                {
                    if (line.IndexOf(candidates[i].Text, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        counts[i]++;
                    }
                }
            }

            foreach (var i in active)
            {
                var added = Math.Min(counts[i], RecoveryCountCapPerProject);
                if (added == 0)
                {
                    continue;
                }

                // Candidates that keep the glob share the main pass's view rule; a glob-dropped candidate
                // no longer has the glob naming a test project, so only the includeTests argument counts.
                var hiddenDoc = isTest && !(candidates[i].DropGlob ? includeTests : effectiveIncludeTests);
                (hiddenDoc ? inTests[i] : visible[i]).AddOrUpdate(projectName, added, (_, existing) => Math.Min(RecoveryCountCapPerProject, existing + added));
            }
        });

        var entries = new List<string>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var visibleCount = visible[i].Values.Sum();
            var testCount = inTests[i].Values.Sum();
            if (visibleCount == 0 && testCount == 0)
            {
                continue;
            }

            var label = candidates[i].DropGlob ? $"the same pattern outside fileGlob '{fileGlob}'" : $"'{candidates[i].Text}'";
            string Top(ConcurrentDictionary<string, int> d) => string.Join(", ", TopRecoveryProjects(d).Select(kv => $"{kv.Key} {kv.Value}{(kv.Value >= RecoveryCountCapPerProject ? "+" : string.Empty)}"));
            if (visibleCount > 0)
            {
                var entry = $"{FormatRecoveryCount(visible[i])} for {label} ({Top(visible[i])})";
                if (testCount > 0)
                {
                    entry += $", plus {FormatRecoveryCount(inTests[i])} in test projects ({Top(inTests[i])}; needs includeTests: true)";
                }
                entries.Add(entry);
            }
            else
            {
                entries.Add($"{FormatRecoveryCount(inTests[i])} in test projects for {label} ({Top(inTests[i])}; rerun with includeTests: true)");
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        // Suggestion: prefer candidates with visible hits, glob-dropped first, then the longest dotted prefix
        // (most specific), then the alternative with the most hits; fall back to test-project-only candidates.
        int? Pick(Func<int, bool> hasHits)
        {
            var ranked = Enumerable.Range(0, candidates.Count)
                .Where(hasHits)
                .OrderBy(i => candidates[i].Kind)
                .ThenBy(i => candidates[i].Kind == 2 ? -(visible[i].Values.Sum() + inTests[i].Values.Sum()) : i)
                .ToList();
            return ranked.Count > 0 ? ranked[0] : null;
        }

        var fromTestsOnly = false;
        var pick = Pick(i => visible[i].Count > 0);
        if (pick == null)
        {
            pick = Pick(i => inTests[i].Count > 0);
            fromTestsOnly = true;
        }

        string suggestion;
        var chosen = candidates[pick!.Value];
        if (chosen.DropGlob)
        {
            var topProject = TopRecoveryProjects(fromTestsOnly ? inTests[pick.Value] : visible[pick.Value])[0].Key;
            var projectFile = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, topProject, StringComparison.OrdinalIgnoreCase))?.FilePath;
            var relativeProjectFile = projectFile == null ? string.Empty : new FilePathWrapper(projectFile, solutionRoot).Relative.Replace('\\', '/');
            var slash = relativeProjectFile.LastIndexOf('/');
            var projectDir = slash > 0 ? relativeProjectFile[..slash] : topProject;
            suggestion = fromTestsOnly
                ? $"Rerun with fileGlob: '{projectDir}/**/*.cs', includeTests: true (or without fileGlob)."
                : $"Rerun without fileGlob or with fileGlob: '{projectDir}/**/*.cs'.";
        }
        else
        {
            suggestion = fromTestsOnly
                ? $"Rerun with query: '{chosen.Text}', includeTests: true."
                : $"Rerun with query: '{chosen.Text}'.";
        }

        return $"0 matches for '{pattern}'; {string.Join("; ", entries)}. {suggestion}";
    }

    public async Task<SentinelCallToolResult<object>> SearchSolutionText(
        ToolCallReason reason,
        string pattern, string? fileGlob = null, int maxResults = 200, bool includeTests = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);

            // Every project (test projects included) is scanned in one flattened pass over documents;
            // includeTests is only a view filter. Test-project matches are counted per project (hidden
            // when includeTests is false) so the status message can say exactly what to rerun with.
            var projectScans = solution.Projects
                .Select(p => (Project: p, IsTest: TestProjectDetector.IsTestProject(p)))
                .ToList();
            var effectiveIncludeTests = includeTests
                || GlobNamesTestProject(fileGlob, projectScans.Where(s => s.IsTest).Select(s => s.Project.Name));
            var documentsToScan = projectScans
                .SelectMany(s => s.Project.Documents.Select(d => (Document: d, ProjectName: s.Project.Name, IsTest: s.IsTest)))
                .ToList();

            var results = new ConcurrentBag<TextSearchMatch>();
            var hiddenCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            // FilePathWrapper.Relative is empty unless a root is supplied; a '/'-containing fileGlob
            // is matched against Relative, so omitting the root made every such glob match 0 files.
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            int resultCount = 0;
            int globMatchedDocCount = 0;
            Regex? regex = null;
            bool regexPatternValid = true;

            try
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeout: TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException)
            {
                regexPatternValid = false;
                warnings.Add($"Pattern '{pattern}' is not a valid regex - only literal substring matches are returned.");
            }

            var scanOptions = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount, TaskScheduler = TaskScheduler.Default };

            await Parallel.ForEachAsync(documentsToScan, scanOptions, async (item, ct) =>
            {
                var (document, projectName, isTest) = item;
                var hidden = isTest && !effectiveIncludeTests;

                if (!hidden && Volatile.Read(ref resultCount) >= maxResults)
                {
                    return;
                }

                var docPath = new FilePathWrapper(document.FilePath ?? "", solutionRoot);
                if (!string.IsNullOrEmpty(fileGlob))
                {
                    if (!GlobMatchesFileName(docPath, fileGlob))
                    {
                        return;
                    }

                    Interlocked.Increment(ref globMatchedDocCount);
                }

                var hiddenAlready = 0;
                if (hidden && hiddenCounts.TryGetValue(projectName, out hiddenAlready) && hiddenAlready >= HiddenTestMatchCapPerProject)
                {
                    return;
                }

                var text = await document.GetTextAsync(ct);
                var sourceText = text.ToString();
                var lines = sourceText.Split('\n');
                // The syntax root is only needed to name the enclosing member of a visible hit, so it
                // is fetched on the first hit in the document rather than for every document.
                SyntaxNode? root = null;
                var rootFetched = false;
                var docHiddenCount = 0;

                string? EnclosingMemberAt(int lineIndex, int col)
                {
                    if (root == null || lineIndex >= text.Lines.Count)
                    {
                        return null;
                    }
                    var lineStart = text.Lines[lineIndex].Start;
                    var lineLength = text.Lines[lineIndex].End - lineStart;
                    var position = lineStart + Math.Clamp(col, 0, Math.Max(0, lineLength));
                    return EnclosingMemberResolver.GetEnclosingMemberName(root, position);
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    if (hidden)
                    {
                        if (hiddenAlready + docHiddenCount >= HiddenTestMatchCapPerProject)
                        {
                            break;
                        }
                    }
                    else if (Volatile.Read(ref resultCount) >= maxResults)
                    {
                        break;
                    }

                    var line = lines[i];
                    var literalCol = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                    Match? regexMatch = null;
                    if (regex != null)
                    {
                        try
                        {
                            var m = regex.Match(line);
                            if (m.Success)
                            {
                                regexMatch = m;
                            }
                        }
                        catch (RegexMatchTimeoutException)
                        {
                        }
                    }

                    if (literalCol < 0 && regexMatch == null)
                    {
                        continue;
                    }

                    if (hidden)
                    {
                        docHiddenCount++;
                        continue;
                    }

                    if (!rootFetched)
                    {
                        root = await document.GetSyntaxRootAsync(ct);
                        rootFetched = true;
                    }

                    var preview = line.Trim();
                    if (preview.Length > 120)
                    {
                        preview = preview[..120] + "…";
                    }

                    if (literalCol >= 0)
                    {
                        results.Add(new TextSearchMatch(docPath.Absolute, i + 1, literalCol + 1, preview, MatchKind.Literal, EnclosingMemberAt(i, literalCol)));
                        Interlocked.Increment(ref resultCount);
                    }

                    if (regexMatch != null)
                    {
                        results.Add(new TextSearchMatch(docPath.Absolute, i + 1, regexMatch.Index + 1, preview, MatchKind.Regex, EnclosingMemberAt(i, regexMatch.Index)));
                        // A regex hit at the same column as the literal hit is deduped out of the
                        // final lists (literalKeys), so it must not consume a resultCount slot.
                        if (!(literalCol >= 0 && regexMatch.Index == literalCol))
                        {
                            Interlocked.Increment(ref resultCount);
                        }
                    }
                }

                if (docHiddenCount > 0)
                {
                    hiddenCounts.AddOrUpdate(projectName, docHiddenCount, (_, existing) => existing + docHiddenCount);
                }
            });

            // ConcurrentBag has no defined enumeration order; sort deterministically before any
            // maxResults truncation so repeated identical searches return identical result sets.
            var orderedResults = results.OrderBy(r => r.filePath.Absolute, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Line).ThenBy(r => r.Column).ToList();
            var literalResults = orderedResults.Where(r => r.MatchedAs == MatchKind.Literal).Take(maxResults).ToList();
            var literalKeys = literalResults.Select(r => (r.filePath, r.Line, r.Column)).ToHashSet();
            var rawRegexResults = orderedResults.Where(r => r.MatchedAs == MatchKind.Regex).ToList();
            var regexResults = rawRegexResults.Where(r => !literalKeys.Contains((r.filePath, r.Line, r.Column))).Take(maxResults).ToList();
            int regexOverlapCount = rawRegexResults.Count - regexResults.Count;
            int totalResultCount = literalResults.Count + regexResults.Count;
            var hiddenHint = hiddenCounts.IsEmpty
                ? string.Empty
                : BuildHiddenTestMatchHint(hiddenCounts, hasVisibleHits: totalResultCount > 0);

            if (literalResults.Count == 0 && regexResults.Count == 0)
            {
                // The recovery pass runs only here (empty visible result), never during the main scan.
                var recoveryHint = await BuildRecoveryHintAsync(solution, documentsToScan, pattern, fileGlob, includeTests, effectiveIncludeTests, globMatchedDocCount, solutionRoot, cancellationToken);
                if (!string.IsNullOrEmpty(fileGlob) && globMatchedDocCount == 0)
                {
                    // A glob that matches no document in any project is an empty answer, not a failure.
                    var matchedAgainstDescription = fileGlob.Replace('\\', '/').Contains('/')
                        ? "the solution-relative path"
                        : "the bare filename";
                    var sampleRelativePaths = solution.Projects
                        .SelectMany(p => p.Documents)
                        .Select(d =>
                        {
                            var wrapper = new FilePathWrapper(d.FilePath ?? "", solutionRoot);
                            // Relative is empty when no solution root is available to resolve against
                            // (e.g. an in-memory solution with no backing .sln/.slnx) - fall back to
                            // Absolute rather than silently dropping a real document from the samples.
                            var path = string.IsNullOrEmpty(wrapper.Relative) ? wrapper.Absolute : wrapper.Relative;
                            return path.Replace('\\', '/');
                        })
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct()
                        .Take(3)
                        .ToList();
                    var sampleText = sampleRelativePaths.Count > 0
                        ? $"Example paths in this solution: {string.Join(", ", sampleRelativePaths)}."
                        : "This solution has no documents to match against.";
                    warnings.Add(
                        $"fileGlob '{fileGlob}' matched 0 documents in any project (matched against {matchedAgainstDescription}), so the search for " +
                        $"'{pattern}' did not run against any document. {sampleText} Widen or fix the fileGlob.");
                }
                else
                {
                    if (hiddenHint.Length > 0 || recoveryHint != null)
                    {
                        warnings.Add($"No matches were found for '{pattern}' (case-insensitive, per line, C# files only).");
                    }
                    else
                    {
                        warnings.Add(
                            $"No matches were found for '{pattern}' as either a literal substring or a regex pattern. Try adjusting the search pattern. Matching is case-insensitive and per line, over C# source files only; a|b alternation is supported. The text may have been reworded, so search for a shorter distinctive fragment, or search each alternative separately. " +
                            "If you were searching for a known symbol by name, use Search with mode: symbol instead (semantic lookup, not text matching). " +
                            "Use ListAll to browse the solution's structure, ProjectDoc to read plan/handoff/documentation files directly, or " +
                            "GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file.");
                    }
                    if (hiddenHint.Length > 0)
                    {
                        warnings.Add(hiddenHint);
                    }
                }
                if (recoveryHint != null)
                {
                    warnings.Add(recoveryHint);
                }
                var justTripped = _workspaceManager.RecordSearchOutcome(0);
                var emptyResult = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                    new TextSearchResult(literalResults, regexResults, regexOverlapCount, regexPatternValid),
                    _workspaceManager.GetSolutionRoot(),
                    typeof(TextSearchMatch).Name,
                    ResultWrapperType.TextSearchMatchList,
                    totalRecords: 0,
                    workspaceVersion: _workspaceManager.WorkspaceVersion,
                    statusMessage: string.Join(" ", warnings),
                    cancellationToken: cancellationToken);
                return emptyResult with { IsError = false, WarningDetails = null, Findings = justTripped ? new[] { new Finding("OrientationBreaker", string.Join(" ", warnings), FindingSeverity.Warning) } : Array.Empty<Finding>() };
            }
            else if (totalResultCount >= maxResults)
            {
                warnings.Add($"{totalResultCount} matches found - returning first ({maxResults}) matches scanned - Narrow fileGlob/pattern or increase maxResults to see more.");
            }

            _workspaceManager.RecordSearchOutcome(literalResults.Count + regexResults.Count);

            string? warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
            var payload = new TextSearchResult(literalResults, regexResults, regexOverlapCount, regexPatternValid);
            var allMatches = literalResults.Concat(regexResults).ToList();
            var matchSummary = SummarizeListResult.Build(allMatches, m => m.filePath.Absolute);
            var searchResult = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                payload,
                _workspaceManager.GetSolutionRoot(),
                typeof(TextSearchMatch).Name,
                ResultWrapperType.TextSearchMatchList,
                totalRecords: literalResults.Count + regexResults.Count,
                workspaceVersion: _workspaceManager.WorkspaceVersion,
                statusMessage: matchSummary.ToSummaryMessage("match") + (hiddenHint.Length > 0 ? " " + hiddenHint : string.Empty),
                listSummary: matchSummary,
                cancellationToken: cancellationToken);
            return searchResult with { WarningDetails = warning };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search (mode: text) failed for '{Pattern}'", pattern);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Search (mode: text)")
            };
        }
    }

    // Globs without a path separator (e.g. "*.cs", "OrderService.cs") are matched against the
    // bare filename so callers can filter by name without knowing the file's directory. Globs
    // with a separator (e.g. "**/OrderService.cs", "ContosoOrders.Core/*.cs") are matched against
    // the path relative to the solution root instead -> matching them against Path.GetFileName()
    // would strip the very directory segment the glob is testing for, so a glob like "**/*.cs"
    // could never match anything.
    private static bool GlobMatchesFileName(FilePathWrapper filePath, string glob)
    {
        var normalizedGlob = glob.Replace('\\', '/');
        var candidate = normalizedGlob.Contains('/') ? filePath.Relative.Replace('\\', '/') : Path.GetFileName(filePath.Absolute);
        var regexPattern = "^" + GlobToRegex(normalizedGlob) + "$";
        return Regex.IsMatch(candidate, regexPattern, RegexOptions.IgnoreCase);
    }

    // Translates glob syntax to a regex fragment: "**/" matches any depth (including none),
    // a lone "**" matches anything, a single "*"/"?" stay within one path segment so they don't
    // accidentally cross a "/" boundary, "{a,b,...}" becomes alternation (each alternative
    // translated recursively, so nested wildcards/classes inside a brace group still work), and
    // "[abc]"/"[!abc]" become a (possibly negated) regex character class. Any other unsupported
    // construct - or a "{"/"[" with no matching close - throws GlobSyntaxException naming the
    // offending character and index, rather than silently Regex.Escape-ing it into a literal that
    // will never match any real file (see docs/current/blockers/
    // blocking_error_search_fileglob_brace_pattern_silently_matches_nothing.md).
    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < glob.Length)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 3;
                }
                else
                {
                    sb.Append(".*");
                    i += 2;
                }
            }
            else if (glob[i] == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (glob[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (glob[i] == '{')
            {
                int depth = 1;
                int j = i + 1;
                while (j < glob.Length && depth > 0)
                {
                    if (glob[j] == '{')
                    {
                        depth++;
                    }
                    else if (glob[j] == '}')
                    {
                        depth--;
                    }
                    j++;
                }

                if (depth != 0)
                {
                    throw new GlobSyntaxException(
                        $"fileGlob '{glob}' has an unterminated '{{' starting at index {i}. " +
                        "Close it with '}', e.g. '{Foo,Bar}'.");
                }

                var inner = glob[(i + 1)..(j - 1)];
                var alternatives = SplitTopLevelCommas(inner);
                if (alternatives.Count < 2)
                {
                    throw new GlobSyntaxException(
                        $"fileGlob '{glob}' has a '{{...}}' group at index {i} with no top-level ',' " +
                        "separator. Brace alternation needs at least two comma-separated options, e.g. '{Foo,Bar}'.");
                }

                sb.Append("(?:").Append(string.Join('|', alternatives.Select(GlobToRegex))).Append(')');
                i = j;
            }
            else if (glob[i] == '}')
            {
                throw new GlobSyntaxException(
                    $"fileGlob '{glob}' has an unmatched '}}' at index {i} with no preceding '{{'.");
            }
            else if (glob[i] == '[')
            {
                int j = glob.IndexOf(']', i + 1);
                if (j < 0)
                {
                    throw new GlobSyntaxException(
                        $"fileGlob '{glob}' has an unterminated '[' starting at index {i}. " +
                        "Close it with ']', e.g. '[abc]' or '[!abc]'.");
                }

                var body = glob[(i + 1)..j];
                var negate = body.StartsWith('!');
                if (negate)
                {
                    body = body[1..];
                }

                if (body.Length == 0)
                {
                    throw new GlobSyntaxException(
                        $"fileGlob '{glob}' has an empty '[...]' character class at index {i}. " +
                        "List at least one character, e.g. '[abc]' or '[!abc]'.");
                }

                var escapedBody = Regex.Escape(body).Replace("\\-", "-");
                sb.Append('[').Append(negate ? "^" : "").Append(escapedBody).Append(']');
                i = j + 1;
            }
            else if (glob[i] == ']')
            {
                throw new GlobSyntaxException(
                    $"fileGlob '{glob}' has an unmatched ']' at index {i} with no preceding '['.");
            }
            else
            {
                sb.Append(Regex.Escape(glob[i].ToString()));
                i++;
            }
        }

        return sb.ToString();
    }

    // Splits a "{...}" group's inner text on top-level commas only - a comma inside a nested
    // "{...}" (e.g. "{a,{b,c}}") stays part of that nested group instead of splitting the outer one.
    private static List<string> SplitTopLevelCommas(string inner)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int k = 0; k < inner.Length; k++)
        {
            if (inner[k] == '{')
            {
                depth++;
            }
            else if (inner[k] == '}')
            {
                depth--;
            }
            else if (inner[k] == ',' && depth == 0)
            {
                parts.Add(inner[start..k]);
                start = k + 1;
            }
        }

        parts.Add(inner[start..]);
        return parts;
    }

    public async Task<SentinelCallToolResult<object>> GetOperationDetail(
        ToolCallReason reason,
        string changeId, string? filter = null, int maxItems = 50, int offset = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var blobPath = OperationBlobWriter.FindBlobPath(changeId, solutionRoot);
            if (blobPath == null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"No operation blob found for changeId '{changeId}'. Verify the changeId, or check that a solution is loaded.")
                };
            }

            var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var allItems = doc.GetProperty("items").EnumerateArray().Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!).ToList();
            IEnumerable<OperationItemRecord> filtered = allItems;
            if (!string.IsNullOrEmpty(filter))
            {
                if (filter.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var pathFilter = filter[5..];
                    filtered = allItems.Where(r => r.FilePath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var outcome = ResolveOutcomeFilter(filter);
                    if (outcome is null)
                    {
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown filter \"{filter}\". Accepted prefixes: fail/err -> failures, warn/skip -> skipped, ok/pass/info/success -> succeeded, roll/revert/undo -> rolledback. Use file:<path> to filter by path, or omit for all items.")
                        };
                    }

                    filtered = allItems.Where(r => r.Outcome == outcome.Value);
                }
            }

            var filteredList = filtered.ToList();
            var safeOffset = Math.Max(0, offset);
            var slice = filteredList.Skip(safeOffset).Take(maxItems).ToList();
            var nextOffset = safeOffset + slice.Count;
            var hasMore = nextOffset < filteredList.Count;
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                HasMoreData = hasMore,
                SuccessData = new OperationDetailResult
                {
                    ChangeId = changeId,
                    BlobName = Path.GetFileName(blobPath),
                    TotalItems = filteredList.Count,
                    ReturnedItems = slice.Count,
                    Offset = safeOffset,
                    NextOffset = hasMore ? nextOffset : null,
                    Filter = filter,
                    Items = slice,
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOperationDetail failed for '{ChangeId}'", changeId);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetOperationDetail")
            };
        }
    }

    // Maps a human-readable filter string to an ItemRecordOutcome via prefix matching.
    // Returns null when the prefix is unrecognised so the caller can return a helpful error.
    private static ItemRecordOutcome? ResolveOutcomeFilter(string filter)
    {
        string f = filter.ToLowerInvariant();
        if (f.StartsWith("fail") || f.StartsWith("err"))
            return ItemRecordOutcome.Failed;
        if (f.StartsWith("skip") || f.StartsWith("warn"))
            return ItemRecordOutcome.Skipped;
        if (f.StartsWith("ok") || f.StartsWith("pass") || f.StartsWith("info") || f.StartsWith("success") || f.StartsWith("succeed"))
            return ItemRecordOutcome.Succeeded;
        if (f.StartsWith("roll") || f.StartsWith("revert") || f.StartsWith("undo"))
            return ItemRecordOutcome.RolledBack;
        if (f.StartsWith("manual") || f.StartsWith("needs_manual"))
            return ItemRecordOutcome.NeedsManualReview;
        return null;
    }

    private static string GetMethodOrCtorName(BaseMethodDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        _ => ""
    };

    private static string BuildSignature(BaseMethodDeclarationSyntax method)
    {
        var modifiers = method.Modifiers.ToString();
        var name = GetMethodOrCtorName(method);
        var parameters = method.ParameterList.ToString();
        if (method is MethodDeclarationSyntax m)
        {
            var returnType = m.ReturnType.ToString();
            var typeParams = m.TypeParameterList?.ToString() ?? "";
            return string.IsNullOrEmpty(modifiers) ? $"{returnType} {name}{typeParams}{parameters}" : $"{modifiers} {returnType} {name}{typeParams}{parameters}";
        }

        return string.IsNullOrEmpty(modifiers) ? $"{name}{parameters}" : $"{modifiers} {name}{parameters}";
    }

    private static List<MethodAttributeInfo> ExtractAttributes(BaseMethodDeclarationSyntax method) => method.AttributeLists.SelectMany(al => al.Attributes).Select(a => new MethodAttributeInfo { Name = a.Name.ToString(), Arguments = a.ArgumentList?.Arguments.ToString() ?? "", }).ToList();
    public async Task<SentinelCallToolResult<object>> GetLargeResult(
        ToolCallReason reason,
        string? resultId = null,
        string? filePath = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default, int? charLimit = null)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        string? resolvedPath = null;

        if (!string.IsNullOrEmpty(resultId) && !string.IsNullOrEmpty(solutionRoot))
        {
            var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "largeresults");
            if (Directory.Exists(dir))
            {
                resolvedPath = Directory
                    .EnumerateFiles(dir, $"largeresult_*_{resultId}.json")
                    .FirstOrDefault();
            }
        }
        else if (!string.IsNullOrEmpty(filePathResolved.Absolute))
        {
            // Validate: path must be inside the largeresults directory and match the largeresult_*.json pattern.
            var fileName = System.IO.Path.GetFileName(filePathResolved.Absolute);
            if (!string.IsNullOrEmpty(solutionRoot))
            {
                var resultsDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "largeresults"));
                var candidate = System.IO.Path.GetFullPath(filePathResolved.Absolute);
                if (candidate.StartsWith(resultsDir, StringComparison.OrdinalIgnoreCase)
                    && fileName.StartsWith("largeresult_", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    resolvedPath = candidate;
                }
            }
        }

        if (resolvedPath == null)
        {
            if (string.IsNullOrEmpty(solutionRoot))
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.SolutionNotLoaded,
                                               SolutionNotLoadedMessage.Build(_workspaceManager.LoadState) + " Offloaded results are stored under the solution root, so they cannot be located until a solution is loaded; the file itself is still on disk under .roslynsentinel/largeresults.")
                };
            }
            
            var message = "Result file not found. Supply a valid resultId or filePath pointing to a largeresult_*.json file in the largeresults directory.";
            if (!string.IsNullOrEmpty(resultId))
            {
                message += $" (resultId looked up: {resultId})";
            }
            
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.NotFound, message)
            };
        }

        ResultWrapper all;
        try
        {
            var json = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
            all = JsonSerializer.Deserialize<ResultWrapper>(
                      json,
                      _jsonOptions)
                  ?? new ResultWrapper();

            if (all.Data == null)
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError("Exception", "Result file has no SuccessData payload - it may be corrupt.")
                };
            }

            // Raw (the generic MCP request-filter offload backstop, see
            // docs/current/proposal_centralized_large_result_filter.md) has no known element shape ->
            // the filter that wrote it only ever saw opaque serialized text, never a typed value -> so
            // the list-shaped Skip(offset).Take(limit) paging every other case below uses does not
            // apply. Instead this pages over the stored raw text itself as a byte/char window, sized
            // and offset by the caller's charLimit/offset (a dedicated character-count parameter,
            // distinct from limit's record-count meaning used by every other case below), capped so
            // a single response can never itself exceed OffloadThresholdBytes. Without this cap,
            // returning the whole stored payload here would let the very filter that offloaded it
            // catch this response on the way back out and re-offload it under a new resultId -> an
            // unbounded fetch/still-too-big/re-offload loop.
            if (all.Type == ResultWrapperType.Raw)
            {
                var rawText = all.Data.ToString();
                var start = Math.Clamp(offset, 0, rawText.Length);

                // The offload filter's threshold check is on the raw response TEXT LENGTH (see
                // AddCallToolFilter's block.Text.Length check in ServiceRegistrationExtensionsBasic.cs),
                // i.e. the length of THIS method's own serialized SentinelCallToolResult, not just the raw slice
                // we embed in it. Two things inflate the final size past the slice length: the
                // envelope's own field names/punctuation (text/offset/nextOffset/totalChars, the
                // outer success/data/totalRecords/hasMorePages wrapper), and JSON string-escaping of
                // the slice content itself -> every '"' or '\' in the slice doubles in size once
                // embedded as a JSON string value, and stored Raw payloads are frequently
                // already-serialized JSON (quote-dense), so escaping overhead cannot be treated as a
                // small fixed constant.
                //
                // Start from a worst-case bound (every slice char could double under escaping) and
                // then verify by actually serializing the candidate response, shrinking if reality
                // still exceeds the threshold. This guarantees the response this method returns can
                // never itself be large enough for the filter to re-offload it, regardless of how
                // quote-dense the stored content is.
                const int envelopeOverheadBytes = 256;
                var worstCaseMaxSlice = Math.Max(1, (LargeResultHelper.OffloadThresholdBytes - envelopeOverheadBytes) / 2);
                var requestedWindow = charLimit is > 0 && charLimit < worstCaseMaxSlice ? charLimit.Value : worstCaseMaxSlice;
                var length = Math.Min(requestedWindow, rawText.Length - start);

                object BuildRawPage(int candidateLength)
                {
                    var candidateSlice = rawText.Substring(start, candidateLength);
                    var candidateNextOffset = start + candidateLength;
                    var candidateHasMore = candidateNextOffset < rawText.Length;
                    return new
                    {
                        text = candidateSlice,
                        offset = start,
                        nextOffset = candidateHasMore ? candidateNextOffset : (int?)null,
                        totalChars = rawText.Length
                    };
                }

                var pageData = BuildRawPage(length);
                while (length > 0 && JsonSerializer.Serialize(pageData, _jsonOptions).Length > LargeResultHelper.OffloadThresholdBytes - envelopeOverheadBytes)
                {
                    length /= 2;
                    pageData = BuildRawPage(length);
                }

                var nextOffset = start + length;
                var hasMoreRaw = nextOffset < rawText.Length;

                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = pageData,
                    TotalRecords = rawText.Length,
                    HasMoreData = hasMoreRaw,
                    NextOffset = hasMoreRaw ? nextOffset : null,
                    WarningDetails = hasMoreRaw
                        ? $"Raw result truncated to a {length}-char window. Call GetLargeResult(resultId, offset: {nextOffset}, charLimit: <N>) to continue reading."
                        : null
                };
            }

            SentinelCallToolResult<object> result;

            // Set by branches whose stored payload is not a single JSON array, so the post-switch totals block
            // cannot derive the total or the returned-row count from the payload's first property.
            int? textSearchTotal = null;
            int? textSearchReturned = null;

            switch (all.Type)
            {
                case ResultWrapperType.MigrationCandidateFindingList:
                    {
                        var findings = JsonSerializer.Deserialize<List<MigrationCandidateFinding>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        // limit/offset were previously accepted but never applied -> the full
                        // on-disk list was returned regardless of the requested page.
                        var requested = Math.Min(limit, Math.Max(0, findings.Count - offset));
                        var page = ShrinkListToFit(findings.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }

                case ResultWrapperType.ApiSurfaceEntryList:
                    {
                        var entries = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        var requested = Math.Min(limit, Math.Max(0, entries.Count - offset));
                        var page = ShrinkListToFit(entries.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.SolutionSymbolEntryList:
                    {
                        var entries = JsonSerializer.Deserialize<List<SolutionSymbolEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        var requested = Math.Min(limit, Math.Max(0, entries.Count - offset));
                        var page = ShrinkListToFit(entries.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.CodeInventoryReport:
                    {
                        var entries = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        var requested = Math.Min(limit, Math.Max(0, entries.Count - offset));
                        var page = ShrinkListToFit(entries.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.MethodSource:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // GetMethodSourceTool returns inline when the result is small enough not to offload.
                        var methodSource = JsonSerializer.Deserialize<MethodSourceResult>(all.Data.ToString(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = methodSource
                        };
                        break;
                    }
                case ResultWrapperType.MigrationScanSummary:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // returned inline when the summary is small enough not to offload.
                        var migrationScanSummary = JsonSerializer.Deserialize<MigrationScanSummary>(all.Data.ToString(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = migrationScanSummary
                        };
                        break;
                    }
                case ResultWrapperType.FileSource:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // ReadFileTool returns inline when the result is small enough not to offload.
                        var fileSource = JsonSerializer.Deserialize<FileSourceResult>(all.Data.ToString(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = fileSource
                        };
                        break;
                    }
                case ResultWrapperType.MemberChangedContent:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // returned inline when the changed content is small enough not to offload.
                        var memberChangedContent = JsonSerializer.Deserialize<MemberChangedContentResult>(all.Data.ToString(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = memberChangedContent
                        };
                        break;
                    }
                case ResultWrapperType.AppliedChangeSummaryResult:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // returned inline when the change summary is small enough not to offload.
                        var appliedChangeSummary = JsonSerializer.Deserialize<AppliedChangeSummary>(all.Data.ToString(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = appliedChangeSummary
                        };
                        break;
                    }
                case ResultWrapperType.BreakingChangeList:
                    {
                        var changes = JsonSerializer.Deserialize<List<BreakingChange>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        var requested = Math.Min(limit, Math.Max(0, changes.Count - offset));
                        var page = ShrinkListToFit(changes.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.TextSearchMatchList:
                    {
                        var searchResult = JsonSerializer.Deserialize<TextSearchResult>(all.Data.ToString(), _jsonOptions);
                        if (searchResult is null)
                        {
                            result = new SentinelCallToolResult<object> { IsError = false, SuccessData = null };
                            break;
                        }

                        // Literal and regex matches are paged as ONE flat sequence (literal first, then regex), so
                        // offset/limit/totalRecords/hasMoreData mean the same thing as for every list-shaped result and
                        // a caller advancing offset by the rows returned sees every row exactly once.
                        var literalAll = searchResult.LiteralResults;
                        var regexAll = searchResult.RegexResults;
                        textSearchTotal = literalAll.Count + regexAll.Count;

                        (List<TextSearchMatch> Literal, List<TextSearchMatch> Regex) BuildTextSearchPage(int rowCount)
                        {
                            var literalPart = literalAll.Skip(offset).Take(rowCount).ToList();
                            var regexPart = regexAll.Skip(Math.Max(0, offset - literalAll.Count)).Take(Math.Max(0, rowCount - literalPart.Count)).ToList();
                            return (literalPart, regexPart);
                        }

                        var requestedRows = Math.Max(0, Math.Min(limit, textSearchTotal.Value - Math.Max(0, offset)));
                        var pageRows = requestedRows;
                        var (literalPage, regexPage) = BuildTextSearchPage(pageRows);
                        const int envelopeOverheadBytes = 256;
                        // Never shrink below one row: a zero-row page would hand the caller an endless no-progress loop.
                        while (pageRows > 1 &&
                               JsonSerializer.Serialize(searchResult with
                               {
                                   LiteralResults = literalPage,
                                   RegexResults = regexPage
                               }, _jsonOptions).Length
                                   > LargeResultHelper.OffloadThresholdBytes - envelopeOverheadBytes)
                        {
                            pageRows = Math.Max(1, pageRows / 2);
                            (literalPage, regexPage) = BuildTextSearchPage(pageRows);
                        }

                        var returnedRows = literalPage.Count + regexPage.Count;
                        textSearchReturned = returnedRows;
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = searchResult with { LiteralResults = literalPage, RegexResults = regexPage },
                            WarningDetails = returnedRows < requestedRows
                                ? $"Page shrunk from {requestedRows} to {returnedRows} record(s) ({literalPage.Count} literal + {regexPage.Count} regex) to stay under the size threshold. Call again with offset: {offset + returnedRows} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.ProjectFileList:
                    {
                        var files = JsonSerializer.Deserialize<List<string>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        var requested = Math.Min(limit, Math.Max(0, files.Count - offset));
                        var page = ShrinkListToFit(files.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.ProjectInfoList:
                    {
                        var projects = JsonSerializer.Deserialize<List<ProjectInfoEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        var requested = Math.Min(limit, Math.Max(0, projects.Count - offset));
                        var page = ShrinkListToFit(projects.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.SolutionItemFileList:
                    {
                        var solutionItems = JsonSerializer.Deserialize<List<SolutionItemFile>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        var requested = Math.Min(limit, Math.Max(0, solutionItems.Count - offset));
                        var page = ShrinkListToFit(solutionItems.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.SolutionItemsAllResult:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // ListSolutionItemsTool(kind: all) returns inline when small enough not to offload.
                        var solutionItemsAll = JsonSerializer.Deserialize<SolutionItemsAllResult>(all.Data.ToString(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = solutionItemsAll
                        };
                        break;
                    }
                case ResultWrapperType.SymbolRelationshipResultList:
                    {
                        // Element shape varies by searchKind (ImplementationInfo, AttributeUsageSite,
                        // ObjectCreationSite, ExtensionMethodInfo, SearchResult) - pass through as raw
                        // JSON nodes instead of a single concrete record type.
                        var items = (all.Data as JsonArray) ?? [];
                        var requested = Math.Min(limit, Math.Max(0, items.Count - offset));
                        var page = ShrinkListToFit(items.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                case ResultWrapperType.BroadenedSymbolRelationshipResults:
                    {
                        // Dictionary<searchKind name, List<object>> - a map, not a flat list, so
                        // limit/offset (list-shaped paging) don't apply; return the whole map.
                        var broadenedMap = JsonSerializer.Deserialize<Dictionary<string, List<JsonNode?>>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = broadenedMap
                        };
                        break;
                    }
                case ResultWrapperType.ErrorStructuredDetailList:
                    {
                        // Offloaded ResultError.StructuredDetail (see ResultError.ForPossiblyLargeDetailAsync).
                        // Element shape varies by which tool/branch produced it (LedgerEntryBase-derived
                        // entries, SkippedCallSite, DiagnosticReport.Diagnostics, ...) - pass through as raw
                        // JSON nodes instead of a single concrete record type, same as
                        // SymbolRelationshipResultList above.
                        var items = (all.Data as JsonArray) ?? [];
                        var requested = Math.Min(limit, Math.Max(0, items.Count - offset));
                        var page = ShrinkListToFit(items.Skip(offset).Take(limit).ToList(), _jsonOptions);
                        result = new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = page,
                            WarningDetails = page.Count < requested
                                ? $"Page shrunk from {requested} to {page.Count} record(s) to stay under the size threshold. Call again with offset: {offset + page.Count} to continue."
                                : null
                        };
                        break;
                    }
                default:
                    {
                        return new SentinelCallToolResult<object>
                        {
                            IsError = true,
                            ErrorData = new ResultError("Exception",
                                          "Unknown scan result type.")
                        };
                    }
            }
            ;

            // MethodSource/FileSource/MigrationScanSummary/MemberChangedContent wrap a single object, not a list - the array-shaped
            // TotalRecords/HasMorePages computation below doesn't apply (and AsArray() on a
            // single-object payload's first property, e.g. a string Signature, throws rather than
            // returning null, since it's the wrong node kind rather than a missing one).
            int totalRecords;
            bool hasMorePages;
            if (all.Type is ResultWrapperType.MethodSource or ResultWrapperType.FileSource or ResultWrapperType.MigrationScanSummary or ResultWrapperType.MemberChangedContent or ResultWrapperType.AppliedChangeSummaryResult or ResultWrapperType.SolutionItemsAllResult)
            {
                totalRecords = 1;
                hasMorePages = false;
            }
            else if (all.Type is ResultWrapperType.BroadenedSymbolRelationshipResults)
            {
                // Map-shaped, not a flat list - report the number of relationship kinds that had
                // results (matching the in-band WarningDetails summary), not a paged item count.
                totalRecords = all.Data?.AsObject().Count ?? 0;
                hasMorePages = false;
            }
            else
            {
                if (textSearchTotal is int searchTotal)
                {
                    totalRecords = searchTotal;
                }
                else
                {
                    var dataArray = all.Data as JsonArray
                        ?? all.Data?.AsObject().FirstOrDefault().Value?.AsArray()
                        ?? [];
                    totalRecords = dataArray.Count;
                }

                // Compare against the rows actually returned, not the requested limit: a page shrunk to fit the
                // size threshold returns fewer rows than limit and the remainder is still unread.
                var returnedCount = textSearchReturned
                    ?? (result.SuccessData as System.Collections.ICollection)?.Count
                    ?? limit;
                hasMorePages = (offset + returnedCount) < totalRecords;
            }

            return new SentinelCallToolResult<object>
            {
                IsError = false,
                // Unwrap: `result` is itself a SentinelCallToolResult<object> built above per ResultWrapperType.
                // Returning it as-is here double-wraps the payload (SuccessData.SuccessData instead of SuccessData),
                // which doesn't match every other tool's flat SentinelCallToolResult<object> shape.
                SuccessData = result.SuccessData,
                TotalRecords = totalRecords,
                HasMoreData = hasMorePages,
                NextOffset = hasMorePages ? offset + (textSearchReturned ?? (result.SuccessData as System.Collections.ICollection)?.Count ?? limit) : null,
                // The branch-built "page shrunk ... call again with offset" hint must reach the caller.
                WarningDetails = result.WarningDetails,
            };
        }
        catch (Exception ex)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError("Exception",
                              "Failed to read scan file.", ex.Message)
            };
        }
    }

    // Mirrors the Raw branch's worst-case-then-shrink loop (see the ResultWrapperType.Raw case in
    // GetLargeResultTool) so every list-shaped switch branch there gets the same guarantee: the page
    // this method hands back can never itself be large enough for the generic offload filter
    // (ServiceRegistrationExtensionsBasic.cs's "Generic large-result offload backstop") to re-catch
    // it and wrap it under a brand-new resultId, which would otherwise hand the caller an
    // unterminating fetch/still-too-big/re-offload loop - see
    // docs/current/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md. Halves the
    // candidate page until the actual serialized size (not a guess) fits under
    // OffloadThresholdBytes minus a fixed envelope overhead, verifying by real serialization the
    // same way the Raw branch does, since element size varies too much across the ~10 record types
    // this feeds to bound analytically.
    private static List<T> ShrinkListToFit<T>(List<T> candidatePage, JsonSerializerOptions jsonOptions)
    {
        const int envelopeOverheadBytes = 256;
        var page = candidatePage;
        while (page.Count > 1 && JsonSerializer.Serialize(page, jsonOptions).Length > LargeResultHelper.OffloadThresholdBytes - envelopeOverheadBytes)
        {
            page = page.Take(Math.Max(1, page.Count / 2)).ToList();
        }

        return page;
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server.Basic;

/// <summary>Return payload for <c>GetMethodSource</c>.</summary>
public record MethodSourceResult
{
    /// <summary>Scope/truncation metadata for the containing file. See <see cref="ReadEnvelope"/>.</summary>
    public ReadEnvelope Envelope { get; init; } = null!;
    /// <summary>Condensed method declaration: modifiers, return type, name, and parameter list — no body.</summary>
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
/// Plain implementation class for the read/navigation slice of workspace tools: GetMethodSource,
/// GetFileOutline, ListAll, SearchSolutionText, GetOperationDetail, GetLargeResult. Method bodies
/// are verbatim moves from SentinelWorkspaceTools — see docs/current/plan_split_workspace_refactoring_tools_for_di.md
/// (Decision 1-Amendment). Not an [McpServerToolType]; the MCP surface lives in WorkspaceReadNavigationTools.
/// </summary>
public class WorkspaceReadNavigationImpl
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger<WorkspaceReadNavigationImpl> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    public WorkspaceReadNavigationImpl(IWorkspaceManager workspaceManager, ILogger<WorkspaceReadNavigationImpl> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // Candidate-suggestion behavior confirmed live: an agent asked to read a file at a slightly
    // wrong path (e.g. missing a subfolder) retried the identical wrong path 2-3 times before
    // giving up, even when ListSolutionItems's own earlier output already showed the real path —
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

    public async Task<ToolResult<object>> GetMethodSource(
        ToolCallReason reason,
        string filepath, string methodName,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var normalizedPath = Path.GetFullPath(filePathResolved);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = BuildFileNotFoundError(solution, normalizedPath)
                };
            }

            var root = await document.GetSyntaxRootAsync();
            if (root == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("SyntaxRootNotFound", "Syntax root not found.")
                };
            }

            // Constructors are ConstructorDeclarationSyntax, not MethodDeclarationSyntax, but callers
            // naturally pass the class name for "give me the source of its constructor" — resolve
            // both node kinds under the shared BaseMethodDeclarationSyntax base.
            var method = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault(m => GetMethodOrCtorName(m).Equals(methodName, StringComparison.Ordinal)) ?? root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault(m => GetMethodOrCtorName(m).Equals(methodName, StringComparison.OrdinalIgnoreCase));
            if (method == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("MethodNotFound", $"Method or constructor '{methodName}' not found in '{filePathResolved}'.")
                };
            }

            var methodSource = method.ToFullString();
            var methodBytes = System.Text.Encoding.UTF8.GetByteCount(methodSource);
            var attributes = ExtractAttributes(method);
            var signature = BuildSignature(method);
            _logger.LogInformation("GetMethodSource: {SizeBytes} bytes for '{MethodName}'", methodBytes, methodName);
            const int thresholdBytes = LargeResultHelper.OffloadThresholdBytes;
            var solutionRoot = _workspaceManager.GetSolutionRoot();

            var fileText = await document.GetTextAsync(cancellationToken);
            var fileLineCount = fileText.Lines.Count;
            var fileByteCount = System.Text.Encoding.UTF8.GetByteCount(fileText.ToString());
            var methodSpan = method.GetLocation().GetLineSpan();
            var envelope = ReadEnvelopeBuilder.Build(
                fileLineCount, fileByteCount,
                returnedFromLine: methodSpan.StartLinePosition.Line + 1,
                returnedToLine: methodSpan.EndLinePosition.Line + 1);

            if (methodBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var fullResult = new MethodSourceResult { Envelope = envelope, Signature = signature, Source = methodSource, Attributes = attributes };
                var stored = await LargeResultHelper.StoreLargeResultAsync(fullResult, solutionRoot, ResultWrapperType.MethodSource, cancellationToken);
                return new ToolResult<object>
                {
                    Success = true,
                    LargeResult = new LargeResultInfo(resultType: "MethodSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: methodBytes, totalRecords: 1, message: $"Result is {methodBytes} bytes (threshold: {thresholdBytes}). " + $"Use get_large_result(resultId: \"{stored.resultId}\") to page through results."),
                    Data = new
                    {
                        envelope,
                        signature,
                        attributes
                    },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new ToolResult<object>()
            {
                Success = true,
                Data = new MethodSourceResult
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
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetMethodSource")
            };
        }
    }

    public async Task<ToolResult<object>> GetFileOutline(
        ToolCallReason reason,
        string filepath,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var normalizedPath = Path.GetFullPath(filePathResolved);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = BuildFileNotFoundError(solution, normalizedPath)
                };
            }

            var root = await document.GetSyntaxRootAsync();
            if (root == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("SyntaxRootNotFound", "Syntax root not found.")
                };
            }

            var items = ExtractOutlineItems(root);

            var fileText = await document.GetTextAsync(cancellationToken);
            var fileLineCount = fileText.Lines.Count;
            var fileByteCount = System.Text.Encoding.UTF8.GetByteCount(fileText.ToString());
            var envelope = ReadEnvelopeBuilder.Build(fileLineCount, fileByteCount, returnedFromLine: 1, returnedToLine: fileLineCount);

            return new ToolResult<object>()
            {
                Success = true,
                Data = new FileOutlineResult { Envelope = envelope, Symbols = items },
                WorkspaceVersion = _workspaceManager.WorkspaceVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileOutline failed for '{FilePathWrapper}'", filePathResolved);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetFileOutline")
            };
        }
    }

    /// <summary>Walks a document's syntax tree and extracts the same outline entries GetFileOutline returns for one file — shared with ListAll, which runs this across every document in the solution.</summary>
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
                // covered here — a file containing only these (e.g. a pure enum file) produced
                // an outline with nothing but a "namespace" entry, silently implying the file had
                // no commentable/editable members at all. Confirmed live: an agent asked to add
                // summary comments to every member skipped OrderStatus.cs's enum entirely because
                // GetFileOutline gave no indication OrderStatus existed, then SummaryComment also
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

    public async Task<ToolResult<object>> ListAll(
        ToolCallReason reason,
        ListAllKind kind = ListAllKind.all,
        string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var projects = solution.Projects.AsEnumerable();
            if (!string.IsNullOrEmpty(projectName))
            {
                projects = projects.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            }

            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var kindFilter = kind switch
            {
                ListAllKind.all => null,
                ListAllKind.enumMember => "enum member",
                _ => kind.ToString()
            };
            var entries = new List<SolutionSymbolEntry>();
            foreach (var project in projects)
            {
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

                    var filePath = new FilePathWrapper(document.FilePath, solutionRoot);
                    foreach (var item in ExtractOutlineItems(root))
                    {
                        if (kindFilter != null && item.Kind != kindFilter)
                        {
                            continue;
                        }

                        entries.Add(new SolutionSymbolEntry(filePath, item.Kind, item.Name, item.Container, item.StartLine, item.EndLine));
                    }
                }
            }

            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                entries,
                solutionRoot,
                typeof(SolutionSymbolEntry).Name,
                ResultWrapperType.SolutionSymbolEntryList,
                totalRecords: entries.Count,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListAll failed (kind={Kind}, projectName={ProjectName})", kind, projectName);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ListAll")
            };
        }
    }

    public async Task<ToolResult<object>> SearchSolutionText(
        ToolCallReason reason,
        string pattern, string? fileGlob = null, int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var results = new List<TextSearchMatch>();
            var warnings = new List<string>();
            Regex? regex = null;
            bool regexPatternValid = true;

            try
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeout: TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException)
            {
                regexPatternValid = false;
                warnings.Add($"Pattern '{pattern}' is not a valid regex — only literal substring matches are returned.");
            }

            var options1 = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };

            await Parallel.ForEachAsync(solution.Projects, options1, async (project, ct1) =>
            {
                var options2 = new ParallelOptions { CancellationToken = ct1, MaxDegreeOfParallelism = Environment.ProcessorCount };

                await Parallel.ForEachAsync(project.Documents, options2, async (document, ct2) =>
                {
                    if (results.Count >= maxResults)
                    {
                        return;
                    }

                    var docPath = new FilePathWrapper(document.FilePath ?? "", _workspaceManager.GetSolutionRoot());
                    if (!string.IsNullOrEmpty(fileGlob) && !GlobMatchesFileName(docPath, fileGlob))
                    {
                        return;
                    }

                    var text = await document.GetTextAsync(ct2);
                    var sourceText = text.ToString();
                    var lines = sourceText.Split('\n');
                    var root = await document.GetSyntaxRootAsync(ct2);
                    for (int i = 0; i < lines.Length && results.Count < maxResults; i++)
                    {
                        var line = lines[i];

                        string BuildPreview()
                        {
                            var preview = line.Trim();
                            return preview.Length > 120 ? preview[..120] + "…" : preview;
                        }

                        string? EnclosingMemberAt(int col)
                        {
                            if (root == null || i >= text.Lines.Count)
                            {
                                return null;
                            }
                            var lineStart = text.Lines[i].Start;
                            var lineLength = text.Lines[i].End - lineStart;
                            var position = lineStart + Math.Clamp(col, 0, Math.Max(0, lineLength));
                            return GetEnclosingMemberName(root, position);
                        }

                        var literalCol = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                        if (literalCol >= 0)
                        {
                            results.Add(new TextSearchMatch(docPath.Absolute, i + 1, literalCol + 1, BuildPreview(), MatchKind.Literal, EnclosingMemberAt(literalCol)));
                        }

                        if (regex != null)
                        {
                            try
                            {
                                var m = regex.Match(line);
                                if (m.Success)
                                {
                                    results.Add(new TextSearchMatch(docPath.Absolute, i + 1, m.Index + 1, BuildPreview(), MatchKind.Regex, EnclosingMemberAt(m.Index)));
                                }
                            }
                            catch (RegexMatchTimeoutException)
                            {
                            }
                        }
                    }
                });
            });

            var literalResults = results.Where(r => r.MatchedAs == MatchKind.Literal).ToList();
            var literalKeys = literalResults.Select(r => (r.filePath, r.Line, r.Column)).ToHashSet();
            var rawRegexResults = results.Where(r => r.MatchedAs == MatchKind.Regex).ToList();
            var regexResults = rawRegexResults.Where(r => !literalKeys.Contains((r.filePath, r.Line, r.Column))).ToList();
            int regexOverlapCount = rawRegexResults.Count - regexResults.Count;

            if (literalResults.Count == 0 && regexResults.Count == 0)
            {
                warnings.Add(
                    $"No matches were found for '{pattern}' as either a literal substring or a regex pattern. Try adjusting the search pattern. " +
                    "If you were searching for a known symbol by name, use LocateSymbol instead (semantic lookup, not text matching). " +
                    "Use ProjectDoc to read plan/handoff/documentation files directly or use GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file.");
                throw new NoSearchMatchesException(string.Join(" ", warnings));
            }
            else if (results.Count >= maxResults)
            {
                warnings.Add($"{results.Count} matches found — returning first ({maxResults}) matches scanned — Narrow fileGlob/pattern or increase maxResults to see more.");
            }

            string? warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
            var payload = new TextSearchResult(literalResults, regexResults, regexOverlapCount, regexPatternValid);
            var searchResult = await ToolResult<object>.ForPossiblyLargeDataAsync(
                payload,
                _workspaceManager.GetSolutionRoot(),
                typeof(TextSearchMatch).Name,
                ResultWrapperType.TextSearchMatchList,
                totalRecords: literalResults.Count + regexResults.Count,
                workspaceVersion: _workspaceManager.WorkspaceVersion,
                cancellationToken: cancellationToken);
            return searchResult with { Warning = warning };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchSolutionText failed for '{Pattern}'", pattern);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SearchSolutionText")
            };
        }
    }

    /// <summary>
    /// Walks up from <paramref name = "position"/> to the nearest named member declaration
    /// (method, property, constructor, field/event, indexer, or operator) and returns its name.
    /// Returns null if the position isn't inside any member — e.g. a using directive, a
    /// namespace-level comment, or a type declaration's own header.
    /// </summary>
    private static string? GetEnclosingMemberName(SyntaxNode root, int position)
    {
        if (position < root.FullSpan.Start || position > root.FullSpan.End)
        {
            return null;
        }

        var token = root.FindToken(position);
        foreach (var node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.Text;
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Identifier.Text;
                case PropertyDeclarationSyntax prop:
                    return prop.Identifier.Text;
                case IndexerDeclarationSyntax:
                    return "this[]";
                case OperatorDeclarationSyntax op:
                    return $"operator {op.OperatorToken.Text}";
                case EventDeclarationSyntax evt:
                    return evt.Identifier.Text;
                case FieldDeclarationSyntax field:
                    return string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
                case EventFieldDeclarationSyntax eventField:
                    return string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.Text));
                case BaseTypeDeclarationSyntax:
                    // Reached a type declaration without finding a member first — e.g. the match
                    // was on the class header itself, not inside any member body.
                    return null;
            }
        }

        return null;
    }

    // Globs without a path separator (e.g. "*.cs", "OrderService.cs") are matched against the
    // bare filename so callers can filter by name without knowing the file's directory. Globs
    // with a separator (e.g. "**/OrderService.cs", "ContosoOrders.Core/*.cs") are matched against
    // the path relative to the solution root instead — matching them against Path.GetFileName()
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
    // a lone "**" matches anything, and a single "*"/"?" stay within one path segment so they
    // don't accidentally cross a "/" boundary.
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
            else
            {
                sb.Append(Regex.Escape(glob[i].ToString()));
                i++;
            }
        }

        return sb.ToString();
    }

    public async Task<ToolResult<object>> GetOperationDetail(
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
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"No operation blob found for changeId '{changeId}'. Verify the changeId, or check that a solution is loaded.")
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
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown filter \"{filter}\". Accepted prefixes: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback. Use file:<path> to filter by path, or omit for all items.")
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
            return new ToolResult<object>()
            {
                Success = true,
                HasMorePages = hasMore,
                Data = new OperationDetailResult
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
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetOperationDetail")
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

    public async Task<ToolResult<object>> GetLargeResult(
        ToolCallReason reason,
        string? resultId = null,
        string? filepath = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
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
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                                           "Result file not found. Supply a valid resultId or filePath pointing to a largeresult_*.json file in the largeresults directory.")
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
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError("Exception", "Result file has no Data payload — it may be corrupt.")
                };
            }

            ToolResult<object> result;

            switch (all.Type)
            {
                case ResultWrapperType.MigrationCandidateFindingList:
                    {
                        var findings = JsonSerializer.Deserialize<List<MigrationCandidateFinding>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            // limit/offset were previously accepted but never applied — the full
                            // on-disk list was returned regardless of the requested page.
                            Data = findings.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }

                case ResultWrapperType.ApiSurfaceEntryList:
                    {
                        var entries = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = entries.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.SolutionSymbolEntryList:
                    {
                        var entries = JsonSerializer.Deserialize<List<SolutionSymbolEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = entries.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.CodeInventoryReport:
                    {
                        var entries = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = entries.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.MethodSource:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // GetMethodSource returns inline when the result is small enough not to offload.
                        var methodSource = JsonSerializer.Deserialize<MethodSourceResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = methodSource
                        };
                        break;
                    }
                case ResultWrapperType.MigrationScanSummary:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // returned inline when the summary is small enough not to offload.
                        var migrationScanSummary = JsonSerializer.Deserialize<MigrationScanSummary>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = migrationScanSummary
                        };
                        break;
                    }
                case ResultWrapperType.FileSource:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // ReadFile returns inline when the result is small enough not to offload.
                        var fileSource = JsonSerializer.Deserialize<FileSourceResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = fileSource
                        };
                        break;
                    }
                case ResultWrapperType.MemberChangedContent:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // returned inline when the changed content is small enough not to offload.
                        var memberChangedContent = JsonSerializer.Deserialize<MemberChangedContentResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = memberChangedContent
                        };
                        break;
                    }
                case ResultWrapperType.BreakingChangeList:
                    {
                        var changes = JsonSerializer.Deserialize<List<BreakingChange>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = changes.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.TextSearchMatchList:
                    {
                        var searchResult = JsonSerializer.Deserialize<TextSearchResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = searchResult is null ? null : searchResult with
                            {
                                LiteralResults = searchResult.LiteralResults.Skip(offset).Take(limit).ToList(),
                                RegexResults = searchResult.RegexResults.Skip(offset).Take(limit).ToList()
                            }
                        };
                        break;
                    }
                case ResultWrapperType.ProjectFileList:
                    {
                        var files = JsonSerializer.Deserialize<List<string>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = files.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.ProjectInfoList:
                    {
                        var projects = JsonSerializer.Deserialize<List<ProjectInfoEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = projects.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.SolutionItemFileList:
                    {
                        var solutionItems = JsonSerializer.Deserialize<List<SolutionItemFile>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = solutionItems.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.SolutionItemsAllResult:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // ListSolutionItems(kind: all) returns inline when small enough not to offload.
                        var solutionItemsAll = JsonSerializer.Deserialize<SolutionItemsAllResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = solutionItemsAll
                        };
                        break;
                    }
                case ResultWrapperType.SymbolRelationshipResultList:
                    {
                        // Element shape varies by searchKind (ImplementationInfo, AttributeUsageSite,
                        // ObjectCreationSite, ExtensionMethodInfo, SearchResult) - pass through as raw
                        // JSON nodes instead of a single concrete record type.
                        var items = (all.Data as JsonArray) ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = items.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.BroadenedSymbolRelationshipResults:
                    {
                        // Dictionary<searchKind name, List<object>> - a map, not a flat list, so
                        // limit/offset (list-shaped paging) don't apply; return the whole map.
                        var broadenedMap = JsonSerializer.Deserialize<Dictionary<string, List<JsonNode?>>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = broadenedMap
                        };
                        break;
                    }
                default:
                    {
                        return new ToolResult<object>
                        {
                            Success = false,
                            Error = new ResultError("Exception",
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
            if (all.Type is ResultWrapperType.MethodSource or ResultWrapperType.FileSource or ResultWrapperType.MigrationScanSummary or ResultWrapperType.MemberChangedContent or ResultWrapperType.SolutionItemsAllResult)
            {
                totalRecords = 1;
                hasMorePages = false;
            }
            else if (all.Type is ResultWrapperType.BroadenedSymbolRelationshipResults)
            {
                // Map-shaped, not a flat list - report the number of relationship kinds that had
                // results (matching the in-band Warning summary), not a paged item count.
                totalRecords = all.Data?.AsObject().Count ?? 0;
                hasMorePages = false;
            }
            else
            {
                var dataArray = all.Data as JsonArray
                    ?? all.Data?.AsObject().FirstOrDefault().Value?.AsArray()
                    ?? [];
                totalRecords = dataArray.Count;
                hasMorePages = (offset + limit) < totalRecords;
            }

            return new ToolResult<object>
            {
                Success = true,
                // Unwrap: `result` is itself a ToolResult<object> built above per ResultWrapperType.
                // Returning it as-is here double-wraps the payload (Data.Data instead of Data),
                // which doesn't match every other tool's flat ToolResult<object> shape.
                Data = result.Data,
                TotalRecords = totalRecords,
                HasMorePages = hasMorePages,
            };
        }
        catch (Exception ex)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                              "Failed to read scan file.", ex.Message)
            };
        }
    }
}

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;
/// <summary>
/// God-class MCP surface for workspace tools. Six of its tool methods -&gt; GetMethodSource,
/// GetFileOutline, ListAll, SearchSolutionText, GetOperationDetail, GetLargeResult -&gt; are thin
/// delegates to <see cref="WorkspaceReadNavigationTools"/> (field <c>_readNav</c>), which itself
/// delegates to <see cref="WorkspaceReadNavigationImpl"/> for the actual logic. That pair is a
/// trial slice of a larger planned split of this class -&gt; see
/// docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md. This class remains the one
/// actually registered/reachable over MCP for those six tool names until that plan's Decision 4/
/// Decision 7 step 4 (fine-grained mode-string wiring) lands.
/// </summary>
[McpServerToolType]
public class SentinelWorkspaceTools
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;    // Added by AddConstructorParameter
    private readonly BuildEngine _buildEngine;
    private readonly TestRunEngine _testRunEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly SolutionManagementEngine _solutionManagementEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<SentinelWorkspaceTools> _logger;
    private readonly WorkspaceReadNavigationImpl _readNav;
    private readonly WriteToolAdviceHelper _writeAdvice;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceProjectManagementTools _projectManagement;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceBuildTestTools _buildTest;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceFileEditTools _fileEdit;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceHealthMiscTools _healthMisc;


    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    public SentinelWorkspaceTools(IWorkspaceManager workspaceManager, ValidationEngine validationEngine, DiffEngine diffEngine, DiagnosticEngine diagnosticEngine, SolutionManagementEngine solutionManagementEngine, StructuralRefinementEngine structuralRefinementEngine, DependencyEngine dependencyEngine, ProjectConsistencyEngine projectConsistencyEngine, SentinelConfiguration config, ILogger<SentinelWorkspaceTools> logger, BuildEngine buildEngine, SymbolNavigationEngine symbolNavigationEngine, TestRunEngine testRunEngine, WorkspaceReadNavigationImpl readNav, WriteToolAdviceHelper writeAdvice)
    {
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _diagnosticEngine = diagnosticEngine;
        _solutionManagementEngine = solutionManagementEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _dependencyEngine = dependencyEngine;
        _projectConsistencyEngine = projectConsistencyEngine;
        _config = config;
        _logger = logger;
        _buildEngine = buildEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _testRunEngine = testRunEngine;
        _readNav = readNav;
        _writeAdvice = writeAdvice;

        // Decision 7 step 2 (plan_split_workspace_refactoring_tools_for_di.md): SentinelWorkspaceTools
        // is now a legacy facade preserving its original constructor/tool signatures, delegating
        // internally to the newly-split *Tools classes.
        _projectManagement = new WorkspaceProjectManagementTools(workspaceManager, solutionManagementEngine, dependencyEngine, projectConsistencyEngine, structuralRefinementEngine, logger);
        _buildTest = new WorkspaceBuildTestTools(workspaceManager, diagnosticEngine, buildEngine, testRunEngine, logger);
        _fileEdit = new WorkspaceFileEditTools(workspaceManager, readNav, logger);
        _healthMisc = new WorkspaceHealthMiscTools(workspaceManager, config, buildEngine, logger);
    }
    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags.")]
    public Task<SentinelCallToolResult<object>> Features(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("list: returns all feature flags. get: returns only the flags named in names. update: batch-updates the flags named in enabled.")]
        FeaturesAction action,
        [Description("Required for action=get - the feature names to look up. Not used for list/update.")]
        List<string>? names = null,
        [Description("Required for action=update - [{Key: featureName, Value: bool}] pairs to apply. Not used for list/get.")]
        List<KeyValuePair<string, bool>>? enabled = null,
        [Description("Test-only: waits this many seconds before acting, to exercise MCP task polling/cancellation.")]
        int delaySeconds = 0,
        CancellationToken cancellationToken = default)
        => _healthMisc.Features(reason, action, names, enabled, delaySeconds, cancellationToken);
    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, dependencies, or solution-folder items in the loaded solution.")]
    public Task<SentinelCallToolResult<object>> ListSolutionItems(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files/dependencies: requires projectName. projects/solutionItems: ignore projectName, list every project or every solution-folder item respectively - solutionItems are files attached via the .sln's Solution Folders (e.g. plan/handoff docs), never part of any project's compiled Documents, so SearchSolutionText and kind=files won't find them; read their content with ProjectDoc. all: ignores projectName and returns everything in one call (every project, every solution-folder item, and every project's files and dependencies) - use this for a complete, guaranteed-non-empty view instead of guessing which project or kind to ask for.")]
        [ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind,
        [Description("Required for kind=files/dependencies. Not used for projects/solutionItems/all.")]
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListSolutionItems(reason, kind, projectName, cancellationToken);

    /// <summary>
    /// Hard cap on how many files ListWorkspaceSolutions will walk before giving up -> protects
    /// against a caller passing an overly broad root (see the drive-root guard below) that isn't
    /// caught by that check but still turns out to contain far more than any real workspace would
    /// (e.g. a node_modules-style tree, or a root one level above the intended one).
    /// </summary>
    private const int ListWorkspaceSolutionsMaxFilesWalked = 200_000;
    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists all *.sln and *.slnx files under a directory. Returns absolute paths for use with LoadSolution.")]
    public SentinelCallToolResult<List<SolutionFileInfo>> ListWorkspaceSolutions(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Your workspace root - a real project/repo directory, not a drive root or '/'.")] string workspacePath,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListWorkspaceSolutions(reason, workspacePath, cancellationToken);
    // current directory, --base-repo-dir (if set), or the server's install directory.
    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution file into memory for persistent analysis. Must be called before any operation that returns ErrorCode=\"SolutionNotLoaded\". Accepts absolute paths. For relative paths, omit baseRepoDir and let the server resolve it against its configured base directory - only pass baseRepoDir if you have independently confirmed that exact directory exists on this host; a fabricated/guessed baseRepoDir is rejected with an error rather than silently ignored. If this exact solution is already loaded, this is a no-op by default (no re-read from disk) - pass forceReload:true to discard in-memory state and re-open it from disk.")]
    public Task<SentinelCallToolResult<object>> LoadSolution(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath,
        [ToolOption(ToolOptionTag.RepoDirectory)][Description("Optional base directory used to resolve a relative solutionPath (e.g. the repo root). Overrides the server's configured base-repo-dir for this call. Must exist on this host - omit this entirely rather than guessing a value.")] string? baseRepoDir = null,
        [Description("If the given solutionPath is already loaded, false (default) returns immediately without touching the workspace. true forces a full reload from disk, discarding any in-memory state (equivalent to today's unconditional LoadSolution behavior). Has no effect when a different or no solution is currently loaded - that always loads normally regardless of this flag.")] bool forceReload = false,
        CancellationToken cancellationToken = default)
        => _projectManagement.LoadSolution(reason, solutionPath, baseRepoDir, forceReload, cancellationToken);

    // ListExternalDiskChanges/AcknowledgeExternalFileChanges moved to SentinelAdminTools.cs,
    // gated behind the "Admin" mode -> see docs/current/ideas/external-drift-hard-blocker.md.
    // Not model-visible by default anymore; reconciliation is an out-of-band operator action now.

    private static string PreviewFileContent(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length <= 20)
        {
            return content;
        }

        var head = lines.Take(10);
        var tail = lines.TakeLast(10);
        return string.Join("\n", head) + "\n// ... (truncated)\n" + string.Join("\n", tail);
    }

    // ApplyUnifiedDiff moved to SentinelWholeFileWriteTools.cs (gated off the default MCP surface,
    // alongside ApplyDiff) -> see docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    // ReplaceSnippet (below, on this default surface) replaces it for small, exact-text edits.

    // Raised from 20 lines / 200 chars (shared across both parameters) after run
    // 20260910-013550-398 livelocked here: the 200-char ceiling was the binding constraint ->
    // a 6-line C# insertion at normal indentation exceeds it long before the 20-line bound -> and
    // the only escape hatch the error named was gated off, so the model burned its last 24 turns
    // reshaping the same edit. Bounds are per-parameter now rather than a shared char cap: unlike
    // the ApplyDiff family, which anchors a small hunk inside a larger context, ReplaceSnippet
    // replaces the entire matched span, so oldContent legitimately grows with the edit.
    // Startup-tunable via ReplaceSnippetOptions (--replace-snippet-max-* args / env vars) so
    // model-eval runs can experiment with looser/tighter limits without a code change.
    private static int MaxOldContentLines => ReplaceSnippetOptions.MaxOldContentLines;
    private static int MaxOldContentChars => ReplaceSnippetOptions.MaxOldContentChars;
    private static int MaxNewContentLines => ReplaceSnippetOptions.MaxNewContentLines;
    private static int MaxNewContentChars => ReplaceSnippetOptions.MaxNewContentChars;
    [McpServerTool(Name = "ReplaceSnippet")]
    [Produces(DataTag.ChangeId)]
    // Deliberately names no whole-file-write tool. Attribute arguments must be compile-time
    // constants, so this text can't be generated per-session from the tool registry the way the
    // over-cap error can (see WriteToolAdviceHelper) -> and a hardcoded name here would be shown to
    // the model on every single call even when that tool is gated off, which is the run-398 failure
    // in its most persistent form. The error path is where the redirect is actually needed.
    [Description("Replaces one exact block of text with another in a single file, for localized edits. For a structural change, prefer the matching Roslyn tool (RenameSymbol, ChangeSignature, ExtractMethodSafe, Member, etc.) instead. For multiple small edits - in the same file or across files - pass 'edits' instead of the singular filepath/oldContent/newContent params. By default this also delta-compiles the edited project(s) plus every project that transitively references them BEFORE writing, and REJECTS the change if it introduces any new compiler error.")]
    public async Task<SentinelCallToolResult<object>> ReplaceSnippet(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
    [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'edits' is omitted -> see the either/or check below.
    [Consumes(DataTag.SourceFilepath, required: false)] FilePathWrapper? filepath = null,
    [ToolOption(ToolOptionTag.OldContent, required: false)][Description(ToolParams.OldContent)] string? oldContent = null,
    [ToolOption(ToolOptionTag.NewContent, required: false)][Description(ToolParams.NewContent)] string? newContent = null,
    [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
    [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
    [Description(ToolParams.SnippetEdits)] List<SnippetEdit>? edits = null,
    [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
    [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
    CancellationToken cancellationToken = default)
    {
        try
        {
            bool hasSingularEdit = filepath.HasValue || !string.IsNullOrEmpty(oldContent) || newContent != null;
            bool hasBatchEdit = edits != null;

            if (hasSingularEdit && hasBatchEdit)
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument,
                        "ReplaceSnippet: supply either filepath/oldContent/newContent or 'edits', not both.")
                };
            }

            if (hasBatchEdit)
            {
                if (edits!.Count == 0)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'edits' was supplied but is empty.")
                    };
                }

                return await ReplaceSnippetBatch(edits, action, validateOnApply, returnDiff, cancellationToken);
            }

            if (!filepath.HasValue)
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument,
                        "ReplaceSnippet: 'filepath' is required (it names the single file oldContent/newContent applies to), unless 'edits' is supplied instead.")
                };
            }

            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath.Value);
            if (!filePathResolved.Validated)
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, "ReplaceSnippet: no solution is loaded, so 'filepath' could not be resolved. Call LoadSolution first, then retry with the same filepath.")
                        : new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'filepath' could not be resolved.")
                };
            }

            if (string.IsNullOrEmpty(oldContent))
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'oldContent' is required.")
                };
            }

            if (newContent == null)
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'newContent' is required (pass an empty string for a pure deletion).")
                };
            }

            // Each bound is reported separately, with the actual value against the limit: run
            // 20260910-013550-398 shows the model repeatedly guessing wrong about which of the four
            // it had hit, because the old message named them all at once.
            var exceeded = DescribeExceededSnippetSizeBounds(oldContent, newContent);
            if (exceeded.Count > 0)
            {
                // Escape-hatch advice comes from WriteToolAdviceHelper, never a hardcoded tool name:
                // the old text here named WriteFile unconditionally, and in run 398 WriteFile was
                // gated off, so the one instruction the model was given was unfollowable.
                var advice = _writeAdvice.AdviseForOversizedEdit("ReplaceSnippet");
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument,
                        $"ReplaceSnippet: {string.Join("; ", exceeded)}. " + advice.Sentence)
                };
            }

            if (action == ProposedChangeAction.apply || action == ProposedChangeAction.validate)
            {
                try
                {
                    var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                    var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute);
                    if (document == null)
                    {
                        return new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                        };
                    }

                    var oldText = await document.GetTextAsync();
                    // FindExactSnippetPosition (not FindSnippetPosition) is required here: it never
                    // uses ContextHelper's whitespace-collapsing fallback, so match.Length is always
                    // the real removable span. Using oldContent.Length instead of match.Length used
                    // to corrupt adjacent lines when a fallback match fired (see
                    // project_replacesnippet_silent_splice_corruption_adjacent_lines memory).
                    var match = ContextHelper.FindExactSnippetPosition(oldText, oldContent, lineBefore, lineAfter);
                    var newFileContent = oldText.ToString().Remove(match.Start, match.Length).Insert(match.Start, newContent);
                    var targetPath = document.FilePath ?? filePathResolved;
                    var snippetChanges = new Dictionary<FilePathWrapper, string>
                    {
                        [targetPath] = newFileContent
                    };

                    if (action == ProposedChangeAction.validate)
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(snippetChanges);
                        return validationResult.Success ? new SentinelCallToolResult<object>()
                        {
                            Success = true,
                            Data = validationResult
                        }
                        : new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"ReplaceSnippet validate failed: {validationResult.Diagnostics.ToInfo()}")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(snippetChanges, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception,
                                "ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n" +
                                "[COMPILER ERROR]\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet", result);
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = SentinelRefactoringTools.BuildDiffFromPreImages(snippetChanges, result.PreImages)
                        }
                        : strippedResult;
                    return new SentinelCallToolResult<object>()
                    {
                        Success = true,
                        Data = responseData
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReplaceSnippet {Action} unexpected exception for '{FilePathWrapper}'", action, filePathResolved);
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceSnippet {action} for '{filePathResolved}'")
                    };
                }
            }

            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unhandled action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceSnippet ({Action}) failed", action);
            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReplaceSnippet")
            };
        }
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private List<string> DescribeExceededSnippetSizeBounds(string oldContent, string newContent)
    {
        var exceeded = new List<string>();
        var oldContentLineCount = oldContent.Split('\n').Length;
        var newContentLineCount = newContent.Split('\n').Length;
        var oldContentCharCount = CountCharsIgnoringLeadingIndentation(oldContent);
        var newContentCharCount = CountCharsIgnoringLeadingIndentation(newContent);
        if (oldContentLineCount > MaxOldContentLines)
        {
            exceeded.Add($"oldContent is {oldContentLineCount} lines (limit {MaxOldContentLines})");
        }
        if (oldContentCharCount > MaxOldContentChars)
        {
            exceeded.Add($"oldContent is {oldContentCharCount} chars excluding leading indentation (limit {MaxOldContentChars})");
        }
        if (newContentLineCount > MaxNewContentLines)
        {
            exceeded.Add($"newContent is {newContentLineCount} lines (limit {MaxNewContentLines})");
        }
        if (newContentCharCount > MaxNewContentChars)
        {
            exceeded.Add($"newContent is {newContentCharCount} chars excluding leading indentation (limit {MaxNewContentChars})");
        }
        return exceeded;
    }

    // Leading indentation is not meaningful "content" - a deeply-nested but small edit
    // should not trip the size guard purely because of indentation depth.
    private static int CountCharsIgnoringLeadingIndentation(string content)
    {
        var total = 0;
        foreach (var line in content.Split('\n'))
        {
            total += line.TrimStart(' ', '\t').Length;
        }
        return total;
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private async Task<SentinelCallToolResult<object>> ReplaceSnippetBatch(
        List<SnippetEdit> edits,
        ProposedChangeAction action,
        bool validateOnApply,
        bool returnDiff,
        CancellationToken cancellationToken)
    {
        if (edits.Count > MaxSnippetEditsPerBatch)
        {
            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ReplaceSnippet: edits has {edits.Count} entries (limit {MaxSnippetEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if (string.IsNullOrEmpty(edit.FilePath))
            {
                perEditErrors.Add($"edits[{i}]: filePath is required.");
                continue;
            }
            if (string.IsNullOrEmpty(edit.OldContent))
            {
                perEditErrors.Add($"edits[{i}] ({edit.FilePath}): oldContent is required.");
                continue;
            }
            if (edit.NewContent == null)
            {
                perEditErrors.Add($"edits[{i}] ({edit.FilePath}): newContent is required (pass an empty string for a pure deletion).");
                continue;
            }
            var exceeded = DescribeExceededSnippetSizeBounds(edit.OldContent, edit.NewContent);
            if (exceeded.Count > 0)
            {
                perEditErrors.Add($"edits[{i}] ({edit.FilePath}): {string.Join("; ", exceeded)}.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet batch rejected before anchoring:\n" + string.Join("\n", perEditErrors))
            };
        }

        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.SetFilePath(pair.edit.FilePath));

        var finalContents = new Dictionary<FilePathWrapper, string>();
        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            if (!filePathResolved.Validated)
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? "no solution is loaded." : "path could not be resolved.")}");
                continue;
            }

            var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute);
            if (document == null)
            {
                perEditErrors.Add($"'{filePathResolved}': file not found.");
                continue;
            }

            var originalText = await document.GetTextAsync(cancellationToken);
            var originalString = originalText.ToString();

            // Resolve every edit's match against the file's ORIGINAL text -> never against a prior
            // edit's output within this same batch. This is what removes the sequencing hazard a
            // series of single ReplaceSnippet calls has: every edit here is anchored against one
            // fixed snapshot, not a chain of intermediate results the model never sees.
            var resolved = new List<(int index, ContextHelper.SnippetMatch match, string newContent)>();
            foreach (var (edit, index) in fileGroup)
            {
                try
                {
                    var match = ContextHelper.FindExactSnippetPosition(originalText, edit.OldContent, edit.LineBefore, edit.LineAfter);
                    resolved.Add((index, match, edit.NewContent));
                }
                catch (ToolException toolEx)
                {
                    perEditErrors.Add($"edits[{index}] ({filePathResolved}): {toolEx.Message}");
                }
            }

            if (perEditErrors.Count > 0)
            {
                continue;
            }

            // Reject overlapping matches before splicing anything -> silent last-writer-wins here is
            // the same corruption class as the closed ReplaceSnippet silent-splice-corruption finding
            // on adjacent lines (wrong match length previously corrupted a neighboring line with no
            // error at all).
            var byStart = resolved.OrderBy(r => r.match.Start).ToList();
            for (int i = 1; i < byStart.Count; i++)
            {
                var prev = byStart[i - 1];
                var curr = byStart[i];
                if (curr.match.Start < prev.match.Start + prev.match.Length)
                {
                    perEditErrors.Add(
                        $"edits[{prev.index}] and edits[{curr.index}] ({filePathResolved}) have overlapping matches " +
                        $"(edits[{prev.index}]: [{prev.match.Start}, {prev.match.Start + prev.match.Length}), " +
                        $"edits[{curr.index}]: [{curr.match.Start}, {curr.match.Start + curr.match.Length})). " +
                        "Split these into separate ReplaceSnippet calls.");
                }
            }

            if (perEditErrors.Count > 0)
            {
                continue;
            }

            // Apply highest-offset-first so an earlier splice's growth/shrink never invalidates a
            // later match's offset -> every position was already computed against originalString above,
            // so no running correction term is needed the way DiffEngine's forward hunk application uses.
            var spliced = originalString;
            foreach (var (_, match, newContent) in byStart.OrderByDescending(r => r.match.Start))
            {
                spliced = spliced.Remove(match.Start, match.Length).Insert(match.Start, newContent);
            }

            finalContents[filePathResolved] = spliced;
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        if (action == ProposedChangeAction.validate)
        {
            var validationResult = await _validationEngine.ValidateChangesAsync(finalContents);
            return validationResult.Success
                ? new SentinelCallToolResult<object>() { Success = true, Data = validationResult }
                : new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"ReplaceSnippet batch validate failed: {validationResult.Diagnostics.ToInfo()}")
                };
        }

        try
        {
            var result = await _workspaceManager.ApplyProposedChangesAsync(finalContents, validateChanges: validateOnApply);
            if (!result.Success && result.ValidationResult != null)
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception,
                        "ReplaceSnippet batch: every edit matched, but the resulting code introduces new compiler errors - no changes were written. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                };
            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet_batch", result);
            var strippedResult = result with { PreImages = null };
            object responseData = returnDiff
                ? new
                {
                    result = strippedResult,
                    diff = SentinelRefactoringTools.BuildDiffFromPreImages(finalContents, result.PreImages)
                }
                : strippedResult;
            return new SentinelCallToolResult<object>()
            {
                Success = true,
                Data = responseData
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceSnippet batch ({Action}) unexpected exception for {Count} file(s)", action, finalContents.Count);
            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceSnippet batch {action} for {finalContents.Count} file(s)")
            };
        }
    }
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: namespaceName/typeKind/typeName are required for a .cs
    // file (to seed a valid compilation unit) and ignored for every other file extension -> a model
    // creating a non-.cs file can omit all three, but a model creating a .cs file must supply all
    // three or the call fails, and nothing besides the description text signals that split.
    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file. Fails if the file already exists - this tool never overwrites or writes free-form whole-file content. Parent directories are created automatically if missing.")]
    public async Task<SentinelCallToolResult<object>> CreateFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Required for .cs files, ignored otherwise. Namespace to seed the file with (e.g. 'RoslynSentinel.Tests.Battery').")] string? namespaceName = null,
        [Description("Required for .cs files, ignored otherwise. Kind of top-level type to seed the file with - this seeds a valid compilation unit plus one empty top-level type declaration (e.g. 'public class Foo\\n{\\n}'), so Member(add) can immediately populate members inside it. Use staticClass for a static utility/helper class (e.g. static test helpers, extension-method containers) - static is only valid on classes, not the other kinds. For a second top-level type in the same file, add it afterward with Member(add, containerName: null, newMemberSource: \"...\").")] NewTypeKind? typeKind = null,
        [Description("Required for .cs files, ignored otherwise. Name of the top-level type to seed the file with (e.g. 'Foo').")] string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
        try
        {
            if (!filePathResolved.Validated)
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, "CreateFile: no solution is loaded, so 'filepath' could not be resolved. Call LoadSolution first, then retry with the same filepath.")
                        : new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'filepath' is required.")
                };
            }

            if (File.Exists(filePathResolved.Absolute))
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"CreateFile: '{filePathResolved}' already exists. CreateFile never overwrites - use Member/ReplaceSnippet to edit an existing file.")
                };
            }

            bool isCSharpFile = filePathResolved.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isCSharpFile && string.IsNullOrWhiteSpace(namespaceName))
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'namespaceName' is required for a .cs file, so the new file starts as a valid compilation unit that Member(add) can populate.")
                };
            }

            if (isCSharpFile && (typeKind == null || string.IsNullOrWhiteSpace(typeName)))
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'typeKind' and 'typeName' are both required for a .cs file, so the new file starts with an empty top-level type that Member(add) can populate members into.")
                };
            }

            string content;
            if (isCSharpFile)
            {
                string keyword = typeKind!.Value == NewTypeKind.staticClass ? "static class" : typeKind.Value.ToString().TrimStart('@');
                content = $"namespace {namespaceName};\n\npublic {keyword} {typeName}\n{{\n}}\n";
            }
            else
            {
                content = "";
            }

            var directory = Path.GetDirectoryName((string)filePathResolved.Absolute);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = content };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: true, cancellationToken: cancellationToken);
            if (!result.Success && result.ValidationResult != null)
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception,
                        "CreateFile: this content would introduce new compiler errors - not written to disk. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                };
            }

            if (!result.Success)
            {
                return new SentinelCallToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"CreateFile failed to write '{filePathResolved}': {result.Summary}")
                };
            }

            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "create_file", result);
            var strippedResult = result with { PreImages = null };
            return new SentinelCallToolResult<object>()
            {
                Success = true,
                Data = strippedResult,
                Findings = _writeAdvice.IsExposed("WriteFile")
                    ? [new Finding("CreateFile",
                        "WriteFile is also available on this server and can create a file with its full body " +
                        "in one call, instead of populating it afterward via Member(add).")]
                    : []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateFile failed for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "CreateFile")
            };
        }
    }

    // The confirmationCode paramater was causing hallucinations and invalid tool calls. Reverted back to the original ApplyDiff tool but keeping this here (block-commented, since it depends
    // on ProposedChangeAction.confirmationCode, which is also commented out in ToolEnums.cs) in case we want to reintroduce ApplyDiff with a confirmationCode in the future.
    /*
    //[McpServerTool(Name = "ApplyDiffWithConfirmationCode")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files -> changes dict filePath->newContent (filepath not used). changesetFormat=diff -> filepath and unifiedDiff are BOTH REQUIRED (filepath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApply/GetOperationDetail) - pass returnDiff=true to get a unified-diff-style preview of what changed instead. IMPORTANT: for changesetFormat=files with action=apply, any file whose content would shrink by more than 50% is rejected with errorCode=ConfirmationRequired - this is a strong signal you submitted only a changed fragment as if it were the whole file, rather than a genuine whole-file rewrite. If the rewrite is really intended, call ApplyDiff again with action=confirmationCode and confirmationCode set to the code from the rejection - do not resend changes/filepath/unifiedDiff on that call, the original changeset is already cached server-side.")]
    public async Task<SentinelCallToolResult<object>> ApplyDiffWithConfirmationCode([ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat, [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action, [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePathWrapper, string>? changes = null, [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null, [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, [ToolOption(ToolOptionTag.ConfirmationCode)][Description("Required when action=confirmationCode. The code returned by a prior apply call that was rejected for exceeding the whole-file-rewrite size threshold. Replays that exact cached changeset - do not also pass changes/filepath/unifiedDiff.")] string? confirmationCode = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            if (action == ProposedChangeAction.confirmationCode)
            {
                if (string.IsNullOrEmpty(confirmationCode))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "confirmationCode is required when action=confirmationCode.")
                    };
                }

                var pending = _workspaceManager.TakePendingChangeset(confirmationCode);
                if (pending == null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, $"confirmationCode '{confirmationCode}' is unrecognized or has expired (codes are single-use and expire after 10 minutes). Resubmit the original ApplyDiff(changesetFormat: files, action: apply, ...) call to get a fresh code.")
                    };
                }

                var confirmedResult = await _workspaceManager.ApplyProposedChangesAsync(pending.Value.Changes, pending.Value.RetryCount, validateChanges: pending.Value.ValidateOnApply);
                if (!confirmedResult.Success && confirmedResult.ValidationResult != null)
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff pre-apply validate failed: {confirmedResult.ValidationResult.Diagnostics.ToJson()}")
                    };
                await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", confirmedResult);
                var strippedConfirmedResult = confirmedResult with { PreImages = null };
                object confirmedResponseData = returnDiff
                    ? new
                    {
                        result = strippedConfirmedResult,
                        diff = SentinelRefactoringTools.BuildDiffFromPreImages(pending.Value.Changes, confirmedResult.PreImages)
                    }
                    : strippedConfirmedResult;
                return new SentinelCallToolResult<object>()
                {
                    Success = true,
                    Data = confirmedResponseData
                };
            }

            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
            if (changesetFormat == ChangesetFormat.files)
            {
                if (changes == null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "changes is required when changesetFormat=files.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    string? oversizedFile = null;
                    double oversizedPercent = 0;
                    foreach (var (changedPath, newContent) in changes)
                    {
                        var oldContent = await FileIoHelper.ReadAllTextIfExistsAsync(changedPath, cancellationToken);
                        var percentRemoved = PercentLinesRemoved(oldContent, newContent);
                        if (percentRemoved > LargeShrinkRejectionThreshold)
                        {
                            oversizedFile = changedPath;
                            oversizedPercent = percentRemoved;
                            break;
                        }
                    }

                    if (oversizedFile != null)
                    {
                        var code = _workspaceManager.CachePendingChangeset(changes, retryCount, validateOnApply);
                        return new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.ConfirmationRequired,
                                $"File '{oversizedFile}' would shrink by {oversizedPercent:P0}, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                "This usually means only a changed fragment was submitted instead of the complete file content - use changesetFormat=diff for a partial edit instead. " +
                                $"If a whole-file rewrite to this size is genuinely intended, call ApplyDiff again with action=confirmationCode and confirmationCode=\"{code}\" to apply the exact changeset just submitted (no need to resend changes). This code expires in 10 minutes.")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception,
                                "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n[COMPILER ERROR]\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetail/
                    // UndoLastApply can retrieve it) and was the single largest contributor to
                    // ApplyDiff responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = SentinelRefactoringTools.BuildDiffFromPreImages(changes, result.PreImages)
                        }
                        : strippedResult;
                    return new SentinelCallToolResult<object>()
                    {
                        Success = true,
                        Data = responseData
                    };
                }

                if (action == ProposedChangeAction.validate)
                {
                    try
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(changes);
                        return validationResult.Success ? new SentinelCallToolResult<object>()
                        {
                            Success = true,
                            Data = validationResult
                        }

                        : new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff validate failed: {validationResult.Diagnostics}")
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff validate unexpected exception");
                        return new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff validate")
                        };
                    }
                }
            }
            else if (changesetFormat == ChangesetFormat.diff)
            {
                if (!filePathResolved.Validated && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filepath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePathResolved.Validated)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'filepath' is required when changesetFormat=diff (it names the single file the unifiedDiff applies to). Only changesetFormat=files takes multiple files via 'changes'.")
                    };
                }

                if (string.IsNullOrEmpty(unifiedDiff))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'unifiedDiff' is required when changesetFormat=diff.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    try
                    {
                        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePathWrapper == filePathResolved.Absolute);
                        if (document == null)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                            };
                        }

                        var oldText = await document.GetTextAsync();
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).Text.ToString();
                        var targetPath = document.FilePathWrapper ?? filePath;
                        var diffChanges = new Dictionary<FilePathWrapper, string>
                        {
                            [targetPath] = newContent
                        };
                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply);
                        if (!result.Success && result.ValidationResult != null)
                            return new SentinelCallToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n[COMPILER ERROR]\n" +
                                    await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                            };
                        await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result);
                        var strippedDiffResult = result with { PreImages = null };
                        object diffResponseData = returnDiff
                            ? new
                            {
                                result = strippedDiffResult,
                                diff = SentinelRefactoringTools.BuildDiffFromPreImages(diffChanges, result.PreImages)
                            }
                            : strippedDiffResult;
                        return new SentinelCallToolResult<object>()
                        {
                            Success = true,
                            Data = diffResponseData
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                        return new SentinelCallToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePathResolved}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePathResolved.Absolute, unifiedDiff);
                    return validationResult.Success ? new SentinelCallToolResult<object>()
                    {
                        Success = true,
                        Data = validationResult
                    }

                    : new SentinelCallToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff diff validate failed: {validationResult.Diagnostics.ToInfo()}")
                    };
                }
            }

            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unhandled changesetFormat '{changesetFormat}' / action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyDiff ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new SentinelCallToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff")
            };
        }
    }
    */

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content - no need to re-send file contents. specificFiles limits to a subset. retryCount defaults to 3.")]
    public Task<SentinelCallToolResult<object>> RetryFailedChanges(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        CancellationToken cancellationToken = default)
        => _fileEdit.RetryFailedChanges(reason, specificFiles, retryCount, cancellationToken);

    // WriteBlobForApplyAsync moved to OperationBlobHelper.WriteBlobForApplyAsync (Decision 7 step 1).
    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics for a file, project, or the whole solution.")]
    public Task<SentinelCallToolResult<object>> GetDiagnostics(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("file/project: also pass scopeName. solution: scopeName is ignored.")]
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution,
        [Description("Required for scope=file (a filePath) or scope=project (a projectName). Ignored for scope=solution.")]
        string? scopeName = null,
        [Description("Groups results by diagnostic ID and returns counts instead of the raw list.")]
        bool summarize = false,
        [Description("Caps the raw diagnostics list. Ignored when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [Description("Caps the number of groups returned. Only used when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20,
        [Description("noBuild (default): diagnostics only. quickBuild/fullBuild: additionally runs a build check (see Build tool) and attaches it as BuildVerification.")]
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _buildTest.GetDiagnostics(reason, scope, scopeName, summarize, maxDetails, topN, verify, cancellationToken);
    [McpServerTool(Name = "Build")]
    [Produces(DataTag.Report)]
    [Description("Compiles the loaded solution and reports errors/warnings. level=quickBuild uses in-memory Roslyn diagnostics (fast, same check GetDiagnostics does). level=fullBuild shells out to `dotnet build` (slower, catches MSBuild-only failures - NuGet restore, resource copy, post-build events - that quickBuild can't see). Returns BuildSucceeded, ExitCode, ErrorCount/WarningCount, capped Errors/Warnings lists, ErrorSummary/WarningSummary (grouped by diagnostic Id, uncapped, for spotting one cause behind many errors), Duration.")]
    public Task<SentinelCallToolResult<object>> Build(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        CancellationToken cancellationToken = default)
        => _buildTest.Build(reason, level, scope, scopeName, maxDetails, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description("Runs `dotnet test` against the loaded solution (or a single project) and reports structured results. Returns TotalCount/PassedCount/FailedCount/SkippedCount, a FailureSummary grouping failures by message signature (e.g. \"45 of 50 failures share one cause\") so an agent doesn't have to paginate to notice a pattern, and a capped Results list (filtered by resultsType, then capped by maxDetails). resultsType defaults to \"failed\" so a clean run stays a short summary with no per-test list; pass \"all\" to see every test's outcome. Set summary=true to omit the Results list entirely (just counts + FailureSummary), regardless of resultsType. filter is passed through to `dotnet test --filter` - an unresolvable filter expression is a distinct error from a filter that resolves but matches zero tests.")]
    public Task<SentinelCallToolResult<object>> RunTest(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        string? filter = null,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("If true, omit the per-test Results list from the response entirely - only counts and FailureSummary are returned, independent of resultsType.")] bool summary = false,
        CancellationToken cancellationToken = default)
        => _buildTest.RunTest(reason, scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, cancellationToken);
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: none of projectName/docCommentId/symbolName/line/column is
    // individually required -> the tool needs exactly one full resolution strategy: (projectName +
    // docCommentId), or symbolName (optionally with contextSnippet/lineBefore/lineAfter), or
    // (line + column). Supplying an incomplete subset of any one strategy (e.g. line without column)
    // is accepted by the schema but rejected at runtime with InvalidArgument.
    [McpServerTool(Name = "SafeDeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages in the entire codebase. Distinction from RemoveMember: this tool refuses if ANY usage is found; RemoveMember checks for callers/implementations but allows skipPrecheck. Returns changeId.")]
    public Task<SentinelCallToolResult<object>> SafeDeleteUnusedSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Preferred resolution path, together with docCommentId - as returned by LocateSymbol/FindReferences. The most reliable and accurate way to identify the target.")] string projectName = "",
        [Description("Preferred resolution path, together with projectName - as returned by LocateSymbol/FindReferences.")] string docCommentId = "",
        [Description("Fallback resolution path if projectName/docCommentId aren't available. Combine with contextSnippet/lineBefore/lineAfter to disambiguate; symbolName alone is enough if there's only one declaration with that name.")]
        [Consumes(DataTag.SymbolName, required: false)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description("Legacy fallback resolution path if neither of the above is available - 1-based line of the declaration site. Both line and column are required together.")]
        [Consumes(DataTag.StartLine, required: false)] int line = 0,
        [Description("Legacy fallback resolution path - 1-based column of the declaration site. Both line and column are required together.")]
        [Consumes(DataTag.Offset, required: false)] int column = 0,
        CancellationToken cancellationToken = default)
        => _projectManagement.SafeDeleteUnusedSymbol(reason, filepath, projectName, docCommentId, symbolName, contextSnippet, lineBefore, lineAfter, line, column, cancellationToken);

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the current solution. projectType defaults to console.")]
    public Task<SentinelCallToolResult<object>> CreateProject(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console",
        CancellationToken cancellationToken = default)
        => _projectManagement.CreateProject(reason, projectName, projectType, cancellationToken);

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public Task<SentinelCallToolResult<object>> SplitProjectByFolder(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName,
        CancellationToken cancellationToken = default)
        => _projectManagement.SplitProjectByFolder(reason, sourceProjectName, folderName, targetProjectName, cancellationToken);

    // ── Phase 1 -> Low-level fallback tools ──────────────────────────────────
    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the full source text of a named method or constructor, plus a structured list of its attributes. For a constructor, pass the containing class's name (e.g. methodName: \"OrderService\" for `public OrderService(...)`). Case-sensitive match with case-insensitive fallback. Returns the first match for overloaded names.")]
    public Task<SentinelCallToolResult<object>> GetMethodSource(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath, [Consumes(DataTag.MethodName, required: true)] string methodName, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetMethodSource(reason, filePathResolved, methodName, cancellationToken);
    }

    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the raw text of a file in the loaded solution, verbatim (no reformatting). Pass startLine/endLine (1-based, inclusive) to read a slice instead of the whole file - useful once GetFileOutline or a search result gives you a line range. Whole-file reads past the size threshold are written to .roslynsentinel/largeresults and returned as a resultId (see GetMethodSource) instead of inline text.")]
    public Task<SentinelCallToolResult<object>> ReadFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("1-based, inclusive. Omit to start from the first line.")] int? startLine = null,
        [Description("1-based, inclusive. Omit to read through the last line.")] int? endLine = null,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReadFile(reason, filepath, startLine, endLine, cancellationToken);

    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file - namespaces, classes, structs, records, interfaces, enums (and their members), methods, properties, constructors, and fields, with 1-based line ranges. Member bodies are not included.")]
    public Task<SentinelCallToolResult<object>> GetFileOutline(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetFileOutline(reason, filePathResolved, cancellationToken);
    }

    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every namespace/class/interface/struct/record/enum/enum member/constructor/field/method/property declared anywhere in the loaded solution, one row per symbol with its file, kind, name, container, and line range - the solution-wide equivalent of GetFileOutline. Call this FIRST when you don't already know the exact name of the type/method/field you need - it is cheaper and more reliable than guessing plausible-sounding names and searching for each one individually with SearchSolutionText. Can return a lot of rows on a large solution; narrow with kind and/or projectName first.")]
    public Task<SentinelCallToolResult<object>> ListAll(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ListAllKindValues)][ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Description("Restricts results to one project. Omit to search the whole solution.")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _readNav.ListAll(reason, kind, projectName, cancellationToken);
    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches all source files in the loaded solution for pattern, evaluated BOTH as a literal substring and (if it compiles) as a regex in a single pass - there is no search-mode to choose. Only searches documents that are part of a loaded project's source code (e.g. .cs files). For a known symbol (class/method/field/etc. by name), use LocateSymbol instead - it's semantic, not text-based, so it won't false-positive on comments/strings or miss partial-line matches. If you don't know the exact name you're looking for, call ListAll first - it's cheaper and more reliable than guessing plausible-sounding names and searching for each one individually here. Use ListSolutionItems(kind: solutionItems) to see files attached via the .sln's Solution Folders and other non-project files, use ProjectDoc to read plan/handoff/documentation files directly, and use GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file. Returns literalResults (always the complete literal-substring match set) and regexResults (regex matches not already in literalResults - empty when pattern has no regex metacharacters, since every regex match is then also a literal match), plus regexOverlapCount (matches found both ways) and regexPatternValid (false if pattern doesn't compile as a regex - literal search is unaffected). Each match has file path, 1-based line and column, a preview, and enclosingMember (the name of the method/property/constructor/field/etc. containing the match, or null if the match isn't inside any member).")]
    public Task<SentinelCallToolResult<object>> SearchSolutionText(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("The text to search for - evaluated both as a literal substring and, if it compiles, as a regex.")]
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern,
        [Description("Restricts to matching file paths (glob syntax). Omit to search every file.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [Description("Caps total matches scanned.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
        => _readNav.SearchSolutionText(reason, pattern, fileGlob, maxResults, cancellationToken);
    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered slice of an operation result blob by changeId. offset skips that many filtered items before taking maxItems; pass NextOffset from the previous response to page through the rest. TotalItems reflects the filtered count; HasMorePages is true when more items remain past this page.")]
    public Task<SentinelCallToolResult<object>> GetOperationDetail(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [Description("Filters items by outcome or path. Accepts prefix synonyms: fail/err -> failures, warn/skip -> skipped, ok/pass/info/success -> succeeded, roll/revert/undo -> rolledback, manual/manual_review/needs_manual_review -> NeedsManualReview (bridge compiler-error skips), file:<path> to filter by path. Omit for all items. An unrecognised prefix returns an error.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null,
        [Description("Caps how many filtered items this page returns.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50,
        [Description("How many filtered items to skip before taking maxItems. Pass NextOffset from the previous response to continue paging.")]
        [ToolOptionAttribute(ToolOptionTag.Offset)] int offset = 0, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
        => _readNav.GetOperationDetail(reason, changeId, filter, maxItems, offset, cancellationToken);
    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: ApplyDiff, refactoring-tool writes, and batch-first tools.")]
    public Task<SentinelCallToolResult<object>> UndoLastApply(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.OperationId, required: true)] string changeId,
        CancellationToken cancellationToken = default)
        => _fileEdit.UndoLastApply(reason, changeId, cancellationToken);

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────
    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("Targeted workspace health check - reads actual workspace/solution state directly rather than environment probes. Returns IsOperational, HasLoadedSolution, LoadedSolutionPath, ProjectCount, DocumentCount, LoadErrors, Summary, StaleDocumentCount, RequiresReload, SampleStaleFiles. IsOperational=true + HasLoadedSolution=false means no solution loaded yet - not an error. RequiresReload=true means files changed on disk since the last LoadSolution call. verify=quickBuild/fullBuild additionally runs a build check and attaches it as BuildVerification.")]
    public Task<SentinelCallToolResult<object>> GetWorkspaceHealth(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _healthMisc.GetWorkspaceHealth(reason, verify, cancellationToken);

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value. No parameters.")]
    public Task<SentinelCallToolResult<object>> ListProjectFrameworkTargets(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListProjectFrameworkTargets(reason, cancellationToken);

    // ── get_large_result ────────────────────────────────────────────────────────

    [McpServerTool(Name = "GetLargeResult")]
    [Produces(DataTag.Report)]
    [Description("Pages through a large result that was written to disk because it exceeded the inline size threshold.")]
    public Task<SentinelCallToolResult<object>> GetLargeResult(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of resultId/filepath must be supplied;
        // neither is individually required but the tool fails if both are omitted.
        [Description("The result's resultId, as returned alongside the original truncated result. Required if filepath is omitted.")]
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Description("Path to a largeresult_*.json file under .roslynsentinel/largeresults. Required if resultId is omitted.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description("Maximum number of records to return.")]
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [Description("Number of records to skip before taking limit.")]
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetLargeResult(reason, resultId, filePathResolved, limit, offset, cancellationToken);
    }


    // Added by AddMember (expected - used for diagnostics)
    private const int MaxSnippetEditsPerBatch = 20;
}

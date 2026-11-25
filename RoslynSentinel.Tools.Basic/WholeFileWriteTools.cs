using System.ComponentModel;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

[McpServerToolType]
public class WholeFileWriteTools
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;    // Added by AddConstructorParameter
    private readonly IWorkspaceManager _workspaceManager;
    private readonly WorkspaceTools _workspaceTools;
    private readonly ValidationEngine _validationEngine;
    private readonly DiffEngine _diffEngine;
    private readonly ILogger<WholeFileWriteTools> _logger;
    // Which tools this server can serve; null (unit tests, hosts without a surface) = treat every tool as callable.
    private readonly ActiveToolSurface? _activeToolSurface;

    public WholeFileWriteTools(IWorkspaceManager workspaceManager, WorkspaceTools workspaceTools, ValidationEngine validationEngine, DiffEngine diffEngine, ILogger<WholeFileWriteTools> logger, SymbolNavigationEngine symbolNavigationEngine, ActiveToolSurface? activeToolSurface = null)
    {
        _activeToolSurface = activeToolSurface;
        _workspaceManager = workspaceManager;
        _workspaceTools = workspaceTools;
        _validationEngine = validationEngine;
        _diffEngine = diffEngine;
        _logger = logger;
        _symbolNavigationEngine = symbolNavigationEngine;
    }
    [McpServerTool(Name = "WriteFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Writes a whole file to disk, creating or overwriting it. For a partial change to an existing file use a targeted edit instead.")]
    public async Task<SentinelCallToolResult<object>> WriteFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("CreateFile: file must not already exist. ReplaceFile: overwrites, creating if missing.")]
        [ExternalInputRequired(DataTag.Action)] WriteFileOperation operation,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Full file content. Parent directories are created automatically.")] string content,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            bool exists = File.Exists(filePathResolved);
            if (operation is WriteFileOperation.CreateFile or WriteFileOperation.Create && exists)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"WriteFile: '{filePathResolved}' already exists. Use operation=ReplaceFile to overwrite an existing file.")
                };
            }

            var directory = Path.GetDirectoryName((string)filePathResolved);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // For ReplaceFile, normalize the new content to the policy EOL (resolver).
            // For CreateFileTool, leave the content as-is (new files use their provided EOL).
            var normalizedContent = content;
            if (exists)
            {
                // TODO reads existing text from disk, not the workspace document
                var existingText = await FileIoHelper.ReadAllTextIfExistsAsync(filePathResolved, cancellationToken);
                if (!string.IsNullOrEmpty(existingText))
                {
                    var targetEol = _workspaceManager.ResolveEol((string)filePathResolved, existingText);
                    normalizedContent = EolUtilities.NormalizeEol(content, targetEol);
                }
            }

            var changes = new DocumentChangeSet { [filePathResolved] = normalizedContent };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: validateOnApply, cancellationToken: cancellationToken, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            if (WriteRefusal.TryMap("WriteFile", result, out var refusal, WriteRefusal.LedgerOf(_workspaceManager)))
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = refusal };

            IReadOnlyList<Finding>? nearMissFindings = null;
            if (result.IsError && result.ValidationResult != null
                && await NearMissAutoCorrector.TryAutoCorrectAsync(_workspaceManager, changes, result.ValidationResult, "WriteFile", StaleReadPolicy.RefuseIfObservedStale, cancellationToken) is { } nearMissFix)
            {
                result = nearMissFix.ApplyResult;
                changes = new DocumentChangeSet(nearMissFix.CorrectedChanges);
                nearMissFindings = nearMissFix.Findings;
            }

            if (result.IsError && result.ValidationResult != null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = await EditImpactGate.RejectionAsync(_workspaceManager, changes, result.ValidationResult, _activeToolSurface,
                        "WriteFile: this content would introduce new compiler errors - not written to disk. Fix the issue(s) below and retry:\n",
                        renamedAway => CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable, renamedAwayNames: renamedAway), cancellationToken)
                };
            }

            if (result.IsError)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"WriteFile failed to write '{filePathResolved}': {result.Summary}")
                };
            }

            var changeId = Guid.NewGuid().ToString("n")[..8];
            var blobWrite = await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, exists ? "replace_file" : "create_file", result, cancellationToken: cancellationToken, blobChangeId: changeId);
            var strippedResult = result with { PreImages = null, ChangeId = blobWrite.Written ? changeId : null };
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = strippedResult,
                // chokepoint findings (EOL attribution) are lifted to the envelope
                Findings = nearMissFindings is null ? (result.Findings ?? []) : (result.Findings ?? []).Concat(nearMissFindings).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WriteFile failed for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"WriteFile for '{filePathResolved}'")
            };
        }
    }

    [McpServerTool(Name = "DeleteFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Deletes a file from disk; fails if it doesn't exist. Undoable via UndoChange.")]
    public async Task<SentinelCallToolResult<object>> DeleteFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            // DeleteFile shares the text edit tools' rule (NonProjectFile.Classify), so an existing file outside the solution root is refused.
            Solution? solution = null;
            try
            {
                solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            }
            catch (SolutionNotLoadedException)
            {
                // No solution: Classify sees no project documents.
            }

            var status = NonProjectFile.Classify(solution, _workspaceManager.GetSolutionRoot(), filePathResolved);
            if (status == NonProjectFileStatus.Missing)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.NotFound, await DeleteFileMissTextAsync(filePathResolved, filePath, cancellationToken))
                };
            }

            if (status == NonProjectFileStatus.OutsideSolutionRoot)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.NotFound, FileNotFoundMessage.Build((string)filePathResolved, Array.Empty<FileCandidate>(), solution?.Projects.Count() ?? 0, _activeToolSurface is null ? null : (Func<string, bool>)_activeToolSurface.IsCallable))
                };
            }

            await RecordDeleteEffectsAsync(filePath, filePathResolved, cancellationToken);
            var result = await _workspaceManager.ApplyProposedChangesAsync(
                changes: [],
                cancellationToken: cancellationToken,
                deletePaths: [filePathResolved],
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            if (WriteRefusal.TryMap("DeleteFile", result, out var refusal, WriteRefusal.LedgerOf(_workspaceManager)))
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = refusal };

            if (result.IsError)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"DeleteFile failed to delete '{filePathResolved}': {result.Summary}")
                };
            }

            var changeId = Guid.NewGuid().ToString("n")[..8];
            var blobWrite = await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "delete_file", result, cancellationToken: cancellationToken, blobChangeId: changeId);
            var strippedResult = result with { PreImages = null, ChangeId = blobWrite.Written ? changeId : null };
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = strippedResult,
                Findings = result.Findings ?? []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteFile failed for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"DeleteFile for '{filePathResolved}'")
            };
        }
    }

    // Edit effect emitter for DeleteFile (see EditEffect). DeleteFile calls the write chokepoint directly (there is no dry run), which commits the
    // pending effects only when the delete stands and drops them otherwise. Records one Removed effect per top-level type the file declares (so a
    // lookup by type name finds the file that held it, with the deleted file as NewFilePath), then FileDeleted: SymbolDisplay is the path as the
    // caller passed it and SimpleName its file name without extension. Fail-open: a failed fact lookup costs the note, never the delete.
    private async Task RecordDeleteEffectsAsync(string filePathAsPassed, FilePathWrapper filePathResolved, CancellationToken cancellationToken)
    {
        if (ToolCallContext.Current is not { } context)
        {
            return;
        }

        try
        {
            // READCHOKEPOINT-CAST: the committed solution still holds the file's declarations (read before the delete).
            var committed = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var types = await DeleteFileEditEffectFacts.DescribeDeclaredTypesAsync(committed, filePathResolved, cancellationToken);
            var path = EditEffectNames.DisplayPath(filePathResolved.Absolute, _workspaceManager.GetSolutionRoot());
            foreach (var (display, docId, simpleName) in types)
            {
                context.RecordEffect(EditEffectKind.Removed, display, docId, simpleName, newName: null, newContainer: null, path);
            }

            context.RecordEffect(EditEffectKind.FileDeleted, filePathAsPassed, symbolDocId: null, Path.GetFileNameWithoutExtension(filePathAsPassed), newName: null, newContainer: null, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DeleteFile could not record its edit effects for '{FilePath}'; the delete proceeds.", filePathAsPassed);
        }
    }

    // Facts and options for a DeleteFile target that is not a file on disk: what was checked, similar files that do exist (loaded
    // solution only, each with a prefilled DeleteFile retry) or the no-similar-name fact, and Search as an option when it is callable.
    // TODO DeleteFile with no solution loaded returns NotFound; the cause is SolutionNotLoaded (ResolveFromWire yields an empty path), so the code is wrong.
    private async Task<string> DeleteFileMissTextAsync(FilePathWrapper filePathResolved, string wirePath, CancellationToken cancellationToken)
    {
        var path = (string)filePathResolved;
        Solution? solution = null;
        try
        {
            solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        }
        catch (SolutionNotLoadedException)
        {
            // No solution: nothing was resolved or checked, which the first sentence says.
        }

        var parts = new List<string>();
        if (solution is null)
        {
            parts.Add($"DeleteFile: no solution is loaded, so '{wirePath}' was not resolved to a path and no file was checked.");
        }
        else
        {
            parts.Add(Directory.Exists(path)
                ? $"DeleteFile: '{path}' is a directory, not a file; this tool deletes single files."
                : $"DeleteFile: '{path}' does not exist on disk.");
            parts.Add($"Searched {solution.Projects.Count()} loaded project(s) by exact path, bare file name and similar file name; path comparison ignores case.");
            var candidates = DocumentLookup.FindFileCandidates(solution, path).Where(c => File.Exists(c.Path)).ToList();
            if (candidates.Count == 0)
            {
                parts.Add("No existing file in the solution has a similar name.");
            }

            foreach (var candidate in candidates)
            {
                parts.Add($"Did you mean '{candidate.Path}'? Retry with {ToolCallHint.Call("DeleteFile", ("filePath", candidate.Path))}.");
            }
        }

        if (_activeToolSurface is null || _activeToolSurface.IsCallable("Search"))
        {
            parts.Add(ToolCallHint.Next("Search", "to find the right path.", ("mode", "text")));
        }

        return string.Join(" ", parts);
    }

    [McpServerTool(Name = "MoveFile")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves or renames text files in one step that is checked against the build (destinations may be in other projects) and undoable via UndoChange. A pair may name a folder (its contents land at the destination path, subfolders kept, a same-name file refuses); a single move is a list of one, up to 200 files, one build check, one undo. Fails if a source is missing or binary, or a destination exists.")]
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> MoveFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // Deliberate deviation from the single-path pattern (plan P9): a list parameter cannot carry the [Consumes]/[ExternalInputRequired] tags (they tag top-level
        // parameters only), so each pair's two paths are validated here and the item schema text rides on the FileMovePair properties ([BatchItemField]).
        [Description(ToolParams.MoveFileMoves)] List<FileMovePair> moves,
        [Description("keep: the text moves unchanged, a .cs namespace stays as written. copyAndObsolete (.cs): copies the file with the folder-derived namespace, marks the old types [Obsolete], migrates callers best effort; refused when the namespace would not change or a type is partial across files. update: not supported yet. Non-.cs files accept only keep.")] MoveFileNamespaceHandling namespaceHandling,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        // Owner exception to the mandatory-parameter rule (plan D3, R3): both default to false so the tool never widens project references or visibility unasked.
        [Description(ToolParams.AddProjectReferences)] bool addProjectReferences = false,
        [Description(ToolParams.AddInternalsVisibleTo)] bool addInternalsVisibleTo = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (moves is null || moves.Count == 0)
            {
                return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: {nameof(moves)} is empty. Options: pass one pair of {MoveFileParameterNames.SourcePath} and {MoveFileParameterNames.DestinationPath} per file or folder to move. Nothing was moved.");
            }

            Solution solution;
            try
            {
                solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            }
            catch (SolutionNotLoadedException)
            {
                return MoveFileError(ToolErrorCode.SolutionNotLoaded, $"MoveFile: no solution is loaded, so the {moves.Count} move pair(s) were not resolved to paths and nothing was moved.");
            }

            var resolved = new List<FileMovePair>(moves.Count);
            for (var i = 0; i < moves.Count; i++)
            {
                var pair = moves[i];
                if (pair is null || string.IsNullOrWhiteSpace(pair.SourcePath) || string.IsNullOrWhiteSpace(pair.DestinationPath))
                {
                    return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: {nameof(moves)}[{i}] needs both {MoveFileParameterNames.SourcePath} and {MoveFileParameterNames.DestinationPath}. Nothing was moved.");
                }

                resolved.Add(new FileMovePair
                {
                    SourcePath = _workspaceManager.ResolveFromWire(pair.SourcePath).Absolute,
                    DestinationPath = _workspaceManager.ResolveFromWire(pair.DestinationPath).Absolute
                });
            }

            if (namespaceHandling != MoveFileNamespaceHandling.keep)
            {
                // A non-.cs file (a folder pair is judged by the files it expands to, below) accepts only keep.
                if (resolved.Select(p => p.SourcePath).FirstOrDefault(p => !Directory.Exists(p) && !IsCSharpPath(p)) is { } nonCs)
                {
                    return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: namespaceHandling '{namespaceHandling}' does not apply to '{nonCs}', which is not a .cs file; non-.cs files accept only 'keep'. Nothing was moved.");
                }

                if (namespaceHandling == MoveFileNamespaceHandling.update)
                {
                    var supported = $"MoveFile: namespaceHandling '{namespaceHandling}' is not supported yet; only 'keep' is supported in this version, apart from 'copyAndObsolete'. Nothing was moved.";
                    var option = _activeToolSurface is null || _activeToolSurface.IsCallable("ChangeNamespace")
                        ? ToolCallHint.Next("ChangeNamespace", "changes the namespace of files, after a move with namespaceHandling keep.")
                        : $"ChangeNamespace (changes the namespace of files, after a move with namespaceHandling keep) is in the moveExtract toolset: {ToolCallHint.Call("McpToolsetControl", ("toolSet", "moveExtract"), ("enabled", "true"))} enables it.";
                    return MoveFileError(ToolErrorCode.NotImplemented, $"{supported} {option}");
                }

                if (dryRun)
                {
                    // The copyAndObsolete engine has no dry-run mode, so a dry run would write.
                    return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: dryRun does not apply to namespaceHandling '{namespaceHandling}', which always writes. Options: dryRun with namespaceHandling keep, or run copyAndObsolete without dryRun. Nothing was moved.");
                }
            }

            var plan = await MoveSetPlanner.ExpandAndCheckAsync(new MoveSetRequest(resolved), cancellationToken);
            if (plan.Moves.Count == 0)
            {
                return MoveFileError(ToolErrorCode.InvalidArgument, "MoveFile: the move set expands to no files; a folder pair moves the files inside the folder, and this folder holds none. Nothing was moved.");
            }

            if (namespaceHandling == MoveFileNamespaceHandling.copyAndObsolete)
            {
                if (plan.Moves.Count != 1)
                {
                    return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: namespaceHandling '{namespaceHandling}' applies to a set that expands to exactly one .cs file; this set expands to {plan.Moves.Count} files. Options: name one .cs file, or use namespaceHandling keep. Nothing was moved.");
                }

                var only = plan.Moves[0];
                if (!IsCSharpPath(only.Source.Absolute))
                {
                    return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: namespaceHandling '{namespaceHandling}' does not apply to '{only.Source.Absolute}', which is not a .cs file; non-.cs files accept only 'keep'. Nothing was moved.");
                }

                // The copyAndObsolete engine keeps its own checks and operation name (copy_and_obsolete_file): the original stays, so the blob must not read as a move.
                return await MoveFileCopyAndObsolete.RunAsync(_workspaceManager, _validationEngine, _symbolNavigationEngine, _activeToolSurface, _logger, solution, only.Source, only.Destination, plan.Changes[only.Destination], cancellationToken);
            }

            plan = await MoveSetPlanner.CheckProjectsAndCollectFactsAsync(plan, solution, cancellationToken);
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            string Display(FilePathWrapper path) => EditEffectNames.DisplayPath(path.Absolute, solutionRoot);
            var movesText = DescribeMoves(plan.Moves, Display);

            // D3: the project edges the set needs. With a toggle off each needed edge is a fact; with a toggle on and an edge found, the edge gate below runs.
            var gateFacts = new List<MoveFact>();
            var edges = await AnalyzeEdgesAsync(plan, solution, addProjectReferences, addInternalsVisibleTo, gateFacts, cancellationToken);
            if (addProjectReferences && gateFacts.Where(f => f.Kind == MoveFactKind.EdgeWouldCloseCycle).Select(f => f.Detail).ToList() is { Count: > 0 } cycles)
            {
                return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: moving {movesText} cannot get the references it needs: {string.Join(" ", cycles)} Nothing was moved. Options: move the files that use each other into one project, or move a different set.");
            }

            var gateEdges = edges.Where(e => e.Kind == ProjectEdgeKind.ProjectReference ? addProjectReferences : addInternalsVisibleTo).ToList();
            if (FindProjectWithoutFile(solution, gateEdges) is { } fileless)
            {
                return MoveFileError(ToolErrorCode.InvalidArgument, $"MoveFile: project '{fileless}' has no project file on disk, so the needed edge cannot be added to it. Nothing was moved. Options: leave {MoveFileParameterNames.AddProjectReferences} and {MoveFileParameterNames.AddInternalsVisibleTo} false and add the edge by hand.");
            }

            var apply = gateEdges.Count > 0
                ? await ApplyWithEdgeGateAsync(plan, gateEdges, addProjectReferences, addInternalsVisibleTo, dryRun, gateFacts, cancellationToken)
                : await ApplyMoveAsync(plan, plan.Changes, dryRun, null, cancellationToken);

            if (apply.Error is { } error)
            {
                // The compile gate's own text says what broke; the projects are added so a cross-project move names both ends.
                var refusal = error.ErrorCode == ToolErrorCode.ValidationFailed
                    ? error with
                    {
                        Message = $"MoveFile: moving {movesText} would break the build; nothing was moved. {error.Message}"
                    }
                    : error;
                if (gateFacts.Where(f => f.Kind == MoveFactKind.WorkspaceChangedDuringGate).ToList() is { Count: > 0 } missed)
                {
                    // The edge gate's second window miss: the refusal above came from a check that ran without the added references, so it says so.
                    refusal = refusal with
                    {
                        Message = $"{refusal.Message} Note: {missed[0].Detail}",
                        Findings = (refusal.Findings ?? []).Concat(LiftMoveFacts(missed)).ToList()
                    };
                }

                return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = refusal };
            }

            if (!apply.DryRun)
            {
                // A later read of an old path is told the new one. Recorded after the write stands (not in recordEffects, which runs before the chokepoint can still refuse).
                foreach (var move in plan.Moves)
                {
                    FileMoveMemory.Shared.Record(move.Source.Absolute, move.Destination.Absolute, "MoveFile");
                }
            }

            var description = $"{(apply.DryRun ? "Would move" : "Moved")} {movesText}; the text moved unchanged (namespaceHandling keep).";
            // The kept-namespace fact is also in the description (callers read it there); every fact is lifted to a finding below.
            foreach (var kept in plan.Facts.Where(f => f.Kind == MoveFactKind.KeptNamespace && f.Detail is not null))
            {
                description += " " + kept.Detail;
            }

            // The edge gate's facts (project file edits, pending reload) are also in the description; every fact is lifted to a finding below.
            foreach (var edgeFact in gateFacts.Where(f => f.Kind is MoveFactKind.CsprojEdgeAdded or MoveFactKind.ReloadPending && f.Detail is not null))
            {
                description += $" Note: {edgeFact.Detail}";
            }

            var affectedFiles = plan.Moves.SelectMany(m => new[] { m.Source, m.Destination }).ToList();
            if (!apply.DryRun && gateFacts.Any(f => f.Kind == MoveFactKind.CsprojEdgeAdded))
            {
                // The gate wrote these project files in the same change as the move.
                foreach (var projectName in gateEdges.Select(e => e.FromProject).Distinct())
                {
                    if (ProjectLookup.FindProject(solution, projectName)?.FilePath is { } csprojPath)
                    {
                        affectedFiles.Add(_workspaceManager.ResolveFromWire(csprojPath));
                    }
                }
            }

            var findings = (apply.Findings ?? []).Concat(LiftMoveFacts(plan.Facts.Concat(gateFacts).ToList())).ToList();
            return new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsError = false,
                SuccessData = AppliedChangeSummary.From(apply, affectedFiles, description),
                Findings = findings
            };
        }
        catch (Exception ex)
        {
            if (ex is MoveSetRefusedException refused)
            {
                // A typed refusal is an expected outcome, not a fault: no error log. A missing source keeps the Search option the legacy text carried.
                var refusal = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MoveFile");
                if (refused.Offenders.Any(o => o.Reason == MoveOffenderReason.Missing) && (_activeToolSurface is null || _activeToolSurface.IsCallable("Search")))
                {
                    refusal = refusal with { Message = refusal.Message + " " + ToolCallHint.Next("Search", "to find the right path.", ("mode", "text")) };
                }

                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = refusal };
            }

            _logger.LogError(ex, "MoveFile failed for {Count} move pair(s)", moves?.Count ?? 0);
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"MoveFile for {moves?.Count ?? 0} move pair(s)")
            };
        }
    }

    private static bool IsCSharpPath(string path) => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    // One move as the description and the compile-gate refusal name it, with both projects; several moves are listed up to a cap.
    private static string DescribeMoves(IReadOnlyList<PlannedFileMove> moves, Func<FilePathWrapper, string> display)
    {
        const int maxListed = 20;
        string One(PlannedFileMove move) =>
            $"'{display(move.Source)}' (project '{move.SourceProjectName ?? "(no loaded project)"}') to '{display(move.Destination)}' (project '{move.DestinationProjectName ?? "(no loaded project)"}')";

        if (moves.Count == 1)
        {
            return One(moves[0]);
        }

        var listed = string.Join(", ", moves.Take(maxListed).Select(One));
        return $"{moves.Count} files: {listed}{(moves.Count > maxListed ? $" and {moves.Count - maxListed} more" : string.Empty)}";
    }

    // The one place a planner fact becomes a wire finding: the fact's own detail when it has one, else its kind and subject.
    private static List<Finding> LiftMoveFacts(IReadOnlyList<MoveFact> facts) =>
        facts.Select(fact => new Finding(
                "MoveFile",
                fact.Detail ?? $"{fact.Kind}: {fact.Subject}",
                fact.Kind is MoveFactKind.MissingProjectReference or MoveFactKind.MissingInternalsVisibleTo or MoveFactKind.MissingPackageReference
                    or MoveFactKind.EdgeWouldCloseCycle or MoveFactKind.WorkspaceChangedDuringGate
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info))
            .ToList();

    private static SentinelCallToolResult<AppliedChangeSummary> MoveFileError(string errorCode, string message) =>
        new()
        {
            IsError = true,
            ErrorData = new ResultError(errorCode, message)
        };

    /// <summary>
    /// Test seam for the MoveFile edge gate (plan Step 26, D13(e)): runs between the gate's compile and its apply on each attempt, so a test can move the workspace
    /// inside that window (for example a foreign write) and exercise the retry. Null in production. Set per tool instance (precedent: PersistentWorkspaceManager.PostWriteFoldOverride).
    /// </summary>
    internal Func<CancellationToken, Task>? BetweenEdgeGateAndApply
    {
        get; set;
    }

    // D3: the project edges the move set needs, found on the committed solution. Facts are added to `facts`: package references and cycles always, and one
    // MissingProjectReference / MissingInternalsVisibleTo per needed edge whose toggle is off. Returns every needed edge (both kinds; the toggles are applied by the caller).
    // TODO the three analyzer calls below each repeat the whole analysis (a FindReferences pass per moved type); expose one call that returns edges, cycles and package facts together
    private async Task<IReadOnlyList<NeededProjectEdge>> AnalyzeEdgesAsync(
        MoveSetPlanResult plan, Solution committed, bool addProjectReferences, bool addInternalsVisibleTo, List<MoveFact> facts, CancellationToken cancellationToken)
    {
        if (!plan.Moves.Any(m => IsCSharpPath(m.Source.Absolute) && m.SourceProjectName is not null && m.DestinationProjectName is not null
                && !string.Equals(m.SourceProjectName, m.DestinationProjectName, StringComparison.Ordinal)))
        {
            return [];
        }

        try
        {
            var edges = await MoveSetEdgeAnalyzer.AnalyzeAsync(plan, committed, cancellationToken);
            foreach (var cyclic in await MoveSetEdgeAnalyzer.FindCycleEdgesAsync(plan, committed, cancellationToken))
            {
                facts.Add(new MoveFact(
                    MoveFactKind.EdgeWouldCloseCycle,
                    $"{cyclic.FromProject} -> {cyclic.ToProject}",
                    $"a ProjectReference from '{cyclic.FromProject}' to '{cyclic.ToProject}' would close a project cycle, because '{cyclic.ToProject}' already depends on '{cyclic.FromProject}': {cyclic.Reason}"));
            }

            facts.AddRange(await MoveSetEdgeAnalyzer.FindMissingPackageFactsAsync(plan, committed, cancellationToken));
            foreach (var edge in edges)
            {
                if (edge.Kind == ProjectEdgeKind.ProjectReference && !addProjectReferences)
                {
                    facts.Add(new MoveFact(
                        MoveFactKind.MissingProjectReference,
                        $"{edge.FromProject} -> {edge.ToProject}",
                        $"project '{edge.FromProject}' needs a ProjectReference to '{edge.ToProject}': {edge.Reason} Option: {MoveFileParameterNames.AddProjectReferences} true adds it to the project file in the same undoable change."));
                }
                else if (edge.Kind == ProjectEdgeKind.InternalsVisibleTo && !addInternalsVisibleTo)
                {
                    facts.Add(new MoveFact(
                        MoveFactKind.MissingInternalsVisibleTo,
                        $"{edge.FromProject} -> {edge.ToProject}",
                        $"project '{edge.FromProject}' needs to grant InternalsVisibleTo '{edge.ToProject}': {edge.Reason} Option: {MoveFileParameterNames.AddInternalsVisibleTo} true adds it to the project file in the same undoable change."));
                }
            }

            return edges;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !addProjectReferences && !addInternalsVisibleTo)
        {
            // With both toggles off the analysis only adds facts; the compile gate still judges the move itself, so a failed analysis must not fail an otherwise valid move.
            _logger.LogWarning(ex, "MoveFile edge analysis failed; the move continues without edge facts");
            return [];
        }
    }

    // The first project (by name) among the edges' projects whose project file cannot be edited: the referencing or granting project, and for a ProjectReference the referenced one (its path becomes the Include).
    private static string? FindProjectWithoutFile(Solution committed, IReadOnlyList<NeededProjectEdge> edges)
    {
        var names = edges.Select(e => e.FromProject)
            .Concat(edges.Where(e => e.Kind == ProjectEdgeKind.ProjectReference).Select(e => e.ToProject))
            .Distinct();
        return names.FirstOrDefault(name => ProjectLookup.FindProject(committed, name)?.FilePath is not { } path || !File.Exists(path));
    }

    // The change set plus one spliced project file per project that gains an edge. The csproj is read as bytes and its text goes in verbatim (the Step 25 contract), keyed by its path, so
    // the chokepoint writes it in the same change as the move (one changeId, one UndoChange) and treats it as a structural write (full reload). A project the committed solution no longer has is skipped,
    // as AugmentBaseline skips it.
    private async Task<DocumentChangeSet> WithCsprojEdgesAsync(DocumentChangeSet changes, Solution committed, IReadOnlyList<NeededProjectEdge> edges, CancellationToken cancellationToken)
    {
        var withEdges = new DocumentChangeSet(changes);
        foreach (var edgesOfProject in edges.GroupBy(e => e.FromProject))
        {
            if (ProjectLookup.FindProject(committed, edgesOfProject.Key)?.FilePath is not { } csprojPath)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(csprojPath)!;
            var bytes = await File.ReadAllBytesAsync(csprojPath, cancellationToken);
            var text = CsprojEdgeWriter.Splice(
                bytes,
                edgesOfProject.ToList(),
                referenced => Path.GetRelativePath(directory, ProjectLookup.FindProject(committed, referenced)!.FilePath!).Replace('/', '\\'));
            if (!string.Equals(text, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal))
            {
                withEdges[_workspaceManager.ResolveFromWire(csprojPath)] = text;
            }
        }

        return withEdges;
    }

    private Task<string> DescribeMoveValidationFailureAsync(DiagnosticReport report, CancellationToken cancellationToken) =>
        CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, cancellationToken, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable);

    // The edit-impact describer plus the grouped-refusal wrapper (Step 15): the same text, plus one structured item that groups the errors by the set file they bind to.
    private Func<DiagnosticReport, CancellationToken, Task<(string? Text, IReadOnlyList<object>? StructuredDetail)>> MoveEditImpactDescriber(
        MoveSetPlanResult plan, IReadOnlyDictionary<FilePathWrapper, string> changes) =>
        async (report, ct) =>
        {
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var inner = EditImpactGate.Describer(_workspaceManager, changes, _activeToolSurface, null, plan.DeletePaths);
            var (impactText, impactDetail) = await inner(report, ct);
            var setGroups = MoveSetErrorGrouper.Group(report, plan.Moves);
            if (setGroups.Count == 0)
            {
                return (impactText, impactDetail);
            }

            // Facts and options only: each group names the file its errors bind to; the outside group is one v1 bucket.
            var builder = new System.Text.StringBuilder(impactText ?? string.Empty);
            foreach (var group in setGroups)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                var scope = group.OutsideSet ? "outside the move set" : "in the move set";
                builder.Append($"Compile errors ({group.Diagnostics.Count}) bind to {EditEffectNames.DisplayPath(group.FilePath, solutionRoot)} ({scope}). Option: include these files in moves.");
            }

            var items = new List<object>(impactDetail ?? Array.Empty<object>()) { setGroups };
            return (builder.ToString(), (IReadOnlyList<object>?)items);
        };

    // One FileMoved effect per pair; recorded by the helper just before the chokepoint, which commits them only if the write stands.
    private Action RecordMoveEffects(MoveSetPlanResult plan) => () =>
    {
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        foreach (var move in plan.Moves)
        {
            ToolCallContext.Current?.RecordEffect(
                EditEffectKind.FileMoved,
                EditEffectNames.DisplayPath(move.Source.Absolute, solutionRoot),
                null,
                Path.GetFileNameWithoutExtension(move.Source.Absolute),
                null,
                null,
                EditEffectNames.DisplayPath(move.Destination.Absolute, solutionRoot));
        }
    };

    // The one apply call every MoveFile path shares. verbatim: the decoded text (BOM kept as a leading U+FEFF) is written back byte-exact; no EOL or BOM normalization, every other chokepoint guard still applies.
    // preValidatedAgainst: the committed solution the edge gate compiled this exact set against (with its added edges); null compiles as usual.
    private Task<ApplyOutcome> ApplyMoveAsync(
        MoveSetPlanResult plan, IReadOnlyDictionary<FilePathWrapper, string> changes, bool dryRun, Solution? preValidatedAgainst, CancellationToken cancellationToken) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, MoveFileOperation.Name,
            dryRun: dryRun,
            cancellationToken: cancellationToken,
            deletePaths: plan.DeletePaths,
            verbatim: true,
            describeValidationFailure: DescribeMoveValidationFailureAsync,
            describeEditImpact: MoveEditImpactDescriber(plan, changes),
            recordEffects: RecordMoveEffects(plan),
            preValidatedAgainst: preValidatedAgainst);

    // True while the committed solution is still the instance the gate worked from.
    private async Task<bool> CommittedIsAsync(Solution committed, CancellationToken cancellationToken) =>
        ReferenceEquals(await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken), committed);

    // D13 (plan Step 26): compile the set against the committed solution plus the needed edges, and on a pass write the csproj edits and the move as ONE change.
    // The lease (a real apply only) is taken here, in this method, because its fallback flow identity is an AsyncLocal that only flows down from the method that set it:
    // the holder's own apply then passes the chokepoint wait. One automatic retry when the workspace moved between compile and apply; a second miss returns the refusal plus a fact.
    private async Task<ApplyOutcome> ApplyWithEdgeGateAsync(
        MoveSetPlanResult plan, IReadOnlyList<NeededProjectEdge> edges, bool addReferences, bool addInternalsVisibleTo, bool dryRun, List<MoveFact> facts, CancellationToken cancellationToken)
    {
        EdgeGateLease.Holder? holder = null;
        try
        {
            if (!dryRun && (_workspaceManager as IEdgeGateLeaseSource)?.Lease is { } lease)
            {
                try
                {
                    holder = await lease.AcquireAsync(cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    return new ApplyOutcome(null, new ResultError(ToolErrorCode.WriteLockBusy, $"MoveFile: {ex.Message} Nothing was moved. Option: retry in a moment."), dryRun);
                }
            }

            var last = default(ApplyOutcome)!;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var committed = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
                var augmented = MoveSetEdgeAnalyzer.AugmentBaseline(committed, edges, addReferences, addInternalsVisibleTo);
                var compiled = await ValidationEngine.ValidateChangesAgainstBaselineAsync(committed, augmented, plan.Changes, plan.DeletePaths, cancellationToken);
                if (compiled.Report.IsError)
                {
                    var rejection = await ValidateAndApplyHelper.BuildValidationRejectionAsync(
                        MoveFileOperation.Name, compiled.Report, plan.Changes, _workspaceManager, DescribeMoveValidationFailureAsync, MoveEditImpactDescriber(plan, plan.Changes), cancellationToken);
                    last = new ApplyOutcome(null, rejection, dryRun, Validation: compiled.Report);
                    if (await CommittedIsAsync(committed, cancellationToken))
                    {
                        return last;    // a genuine error: the workspace did not move during the compile, so a retry would compile the same thing
                    }

                    continue;
                }

                var withEdges = await WithCsprojEdgesAsync(plan.Changes, committed, edges, cancellationToken);
                if (BetweenEdgeGateAndApply is { } seam)
                {
                    await seam(cancellationToken);
                }

                last = await ApplyMoveAsync(plan, withEdges, dryRun, committed, cancellationToken);
                if (last.Error is null)
                {
                    foreach (var edge in edges)
                    {
                        var what = edge.Kind == ProjectEdgeKind.ProjectReference ? $"a ProjectReference to '{edge.ToProject}'" : $"InternalsVisibleTo '{edge.ToProject}'";
                        facts.Add(new MoveFact(MoveFactKind.CsprojEdgeAdded, $"{edge.FromProject} -> {edge.ToProject}", $"{(dryRun ? "would add" : "added")} {what} to the project file of '{edge.FromProject}'."));
                    }

                    if (last.ReloadPending)
                    {
                        facts.Add(new MoveFact(MoveFactKind.ReloadPending, string.Join(", ", edges.Select(e => e.FromProject).Distinct()), "the project file was written; a background reload of the workspace follows, and the next call waits for it."));
                    }

                    return last;
                }

                // A refusal because the workspace moved (the helper recompiled without the edges, or the chokepoint's foreign-write re-validation fired) is a window miss; anything else stands.
                if (last.Error.ErrorCode != ToolErrorCode.ConcurrentEditRefused && await CommittedIsAsync(committed, cancellationToken))
                {
                    return last;
                }
            }

            facts.Add(new MoveFact(MoveFactKind.WorkspaceChangedDuringGate, "workspace", "the workspace changed during the call; the check ran without the added references; a retry recomputes them."));
            return last;
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>
    /// Fraction of <paramref name="oldContent"/>'s line count that <paramref name="newContent"/>
    /// would remove. Only shrinkage counts -> a large *increase* (codegen, genuine expansion) is
    /// not the "submitted a fragment as if it were the whole file" failure mode this guards
    /// against, so it's exempt. Returns 0 for a new file (no oldContent to shrink from) or a
    /// same-size-or-larger replacement.
    /// </summary>
    private static double PercentLinesRemoved(string? oldContent, string newContent)
    {
        if (string.IsNullOrEmpty(oldContent))
        {
            return 0;
        }

        int oldLines = oldContent.Split('\n').Length;
        int newLines = newContent.Split('\n').Length;
        if (newLines >= oldLines || oldLines == 0)
        {
            return 0;
        }

        return (oldLines - newLines) / (double)oldLines;
    }

    /// <summary>
    /// Count of non-blank source lines in <paramref name="content"/> that contain real C# syntax
    /// (tokens), as opposed to lines that are blank or entirely comment trivia. Parsed rather than
    /// regex-matched so that comment markers appearing inside string literals don't skew the count.
    /// Returns 0 (guard exempt -> see <see cref="PercentActiveCodeLinesRemoved"/>) if the content
    /// doesn't parse as C#, since a non-.cs file has no meaningful "active code line" notion here.
    /// </summary>
    private static int CountActiveCodeLines(string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var text = tree.GetText();
        var activeLines = new HashSet<int>();
        foreach (var token in tree.GetRoot().DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.None) || token.IsKind(SyntaxKind.EndOfFileToken))
            {
                continue;
            }

            int startLine = text.Lines.GetLineFromPosition(token.SpanStart).LineNumber;
            int endLine = text.Lines.GetLineFromPosition(token.Span.End).LineNumber;
            for (int line = startLine; line <= endLine; line++)
            {
                activeLines.Add(line);
            }
        }

        return activeLines.Count;
    }

    /// <summary>
    /// Fraction of <paramref name="oldContent"/>'s active (non-comment, non-blank) C# code lines
    /// that <paramref name="newContent"/> would remove. This catches the case the raw
    /// <see cref="PercentLinesRemoved"/> line-count guard misses entirely: an agent that comments
    /// out every existing line one-for-one (e.g. prefixing each with "// ") produces a file with
    /// the SAME line count as before, so the shrink guard sees 0% change, even though the file now
    /// contains no working code. Only files that still look like C# after the edit are checked
    /// (both old and new content must parse to at least one active line) -> a genuine full-file
    /// deletion-to-near-empty is already caught by the line-count guard, and non-.cs content has no
    /// "active code line" concept to compare. Returns 0 (exempt) for a new file or malformed content.
    /// </summary>
    private static double PercentActiveCodeLinesRemoved(string filePath, string? oldContent, string newContent)
    {
        if (string.IsNullOrEmpty(oldContent) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int oldActive = CountActiveCodeLines(oldContent);
        if (oldActive == 0)
        {
            return 0;
        }

        int newActive = CountActiveCodeLines(newContent);
        if (newActive >= oldActive)
        {
            return 0;
        }

        return (oldActive - newActive) / (double)oldActive;
    }

    /// <summary>
    /// Builds the success-response payload for a diff-hunk apply (ApplyDiffTool's changesetFormat=diff
    /// branch, and ApplyUnifiedDiffTool), including <paramref name="diffReport"/>'s findings when it has
    /// any. Previously a hunk whose header line counts didn't match its own body (e.g. declaring 2
    /// removed/10 added lines when the body actually had 2 removed/159 added) was only logged
    /// server-side on success -> the caller had no way to learn its hunk was malformed until the file
    /// came out wrong. Surfacing it here lets the calling model catch its own mistake immediately.
    /// </summary>
    private static object BuildDiffApplyResponseData(
        ApplyChangesResult strippedResult,
        DiffHunkAnalyzer.DiffReport diffReport,
        bool returnDiff,
        Dictionary<FilePathWrapper, string> diffChanges,
        IReadOnlyDictionary<string, string?>? preImages)
    {
        if (!returnDiff && !diffReport.HasFindings)
        {
            return strippedResult;
        }

        return new
        {
            result = strippedResult,
            diff = returnDiff ? ValidateAndApplyHelper.BuildDiffFromPreImages(diffChanges, preImages) : null,
            diffHunkFindings = diffReport.HasFindings ? diffReport.Describe() : null
        };
    }

    /// <summary>
    /// Envelope findings for a diff-hunk apply: the write chokepoint's findings (EOL attribution) plus the
    /// hunk analyzer's warning when it has one. An explicit envelope list replaces the AppliedChangeSummary lift,
    /// and this tool's payload is not an AppliedChangeSummary, so both sources must be merged here.
    /// </summary>
    private static IReadOnlyList<Finding> MergeDiffApplyFindings(IReadOnlyList<Finding>? applyFindings, DiffHunkAnalyzer.DiffReport diffReport)
    {
        var merged = new List<Finding>(applyFindings ?? []);
        if (diffReport.HasFindings)
        {
            merged.Add(new Finding("DiffHunkAnalyzer", diffReport.Describe(), FindingSeverity.Warning));
        }

        return merged.Distinct().ToList();
    }

    /// <summary>
    /// A files-format apply where any file would lose more than this fraction of its line count
    /// (see <see cref="PercentLinesRemoved"/>), or of its active C# code lines (see
    /// <see cref="PercentActiveCodeLinesRemoved"/>), is rejected (see
    /// <see cref="ToolErrorCode.ConfirmationRequired"/>) rather than applied -> the first check
    /// catches a caller submitting only a changed fragment as if it were the entire file; the
    /// second catches the same intent expressed by commenting out the whole file instead of
    /// shortening it, which leaves the raw line count unchanged. A large increase in either
    /// dimension is exempt from both.
    /// </summary>
    private const double LargeShrinkRejectionThreshold = 0.5;
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: changesetFormat=files requires 'changes' (filePath/unifiedDiff
    // unused); changesetFormat=diff requires 'filePath' and 'unifiedDiff' (changes unused). No single
    // param is universally required beyond changesetFormat/action, so a model can supply the wrong
    // subset for its chosen format and only find out at runtime.
    [McpServerTool(Name = "ApplyDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set as either full file contents (changesetFormat=files) or a unified diff (changesetFormat=diff), validating by delta-compile before writing.")]
    public async Task<SentinelCallToolResult<object>> ApplyDiffTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files: use changes (filePath->content map). diff: use filePath+unifiedDiff.")]
        [ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat,
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required when changesetFormat=files, unused otherwise.
        [Description("Required for changesetFormat=files: filePath -> newContent map.")]
        [ExternalInputRequired(DataTag.OperationId)] Dictionary<string, string>? changes = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required when changesetFormat=diff, unused otherwise.
        [Description("Required for changesetFormat=diff.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required when changesetFormat=diff, unused otherwise.
        [Description("Required for changesetFormat=diff.")]
        [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
            if (changesetFormat == ChangesetFormat.files)
            {
                if (changes == null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "changes is required when changesetFormat=files.")
                    };
                }

                // MCP wire type is Dictionary<string,string>: a Dictionary<FilePathWrapper,...> tool
                // parameter makes System.Text.Json.Schema.JsonSchemaExporter fall back to an
                // unrepresentable `true` schema node for the key type, which LM Studio's grammar
                // converter rejects outright ("Unrecognized schema: true"). Resolve keys to
                // FilePathWrapper here instead, after the schema boundary.
                Dictionary<FilePathWrapper, string> resolvedChanges = changes.ToDictionary(
                    kvp => _workspaceManager.ResolveFromWire(kvp.Key),
                    kvp => kvp.Value);

                if (action == ProposedChangeAction.apply)
                {
                    string? oversizedFile = null;
                    double oversizedPercent = 0;
                    bool oversizedIsCommentCollapse = false;
                    foreach (var (changedPath, newContent) in resolvedChanges)
                    {
                        var oldContent = await ExistingTextReader.ReadAsync(_workspaceManager, changedPath, cancellationToken);
                        var percentRemoved = PercentLinesRemoved(oldContent, newContent);
                        var percentActiveRemoved = PercentActiveCodeLinesRemoved(changedPath, oldContent, newContent);
                        if (percentRemoved > LargeShrinkRejectionThreshold || percentActiveRemoved > LargeShrinkRejectionThreshold)
                        {
                            oversizedFile = changedPath;
                            oversizedPercent = Math.Max(percentRemoved, percentActiveRemoved);
                            oversizedIsCommentCollapse = percentActiveRemoved > LargeShrinkRejectionThreshold && percentRemoved <= LargeShrinkRejectionThreshold;
                            break;
                        }
                    }

                    if (oversizedFile != null)
                    {
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.ConfirmationRequired,
                                oversizedIsCommentCollapse
                                    ? $"File '{oversizedFile}' would have {oversizedPercent:P0} of its active code lines turned into comments, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                      "This usually means the intended change (or a partial rewrite) was commented out instead of actually being edited or removed, rather than a genuine intentional deletion. If this is a genuine intentional removal of this code, re-submit ApplyDiff with the complete file content included in 'changes'. For a partial edit, use changesetFormat=diff instead."
                                    : $"File '{oversizedFile}' would shrink by {oversizedPercent:P0}, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                      "This usually means only a changed fragment was submitted instead of the complete file content. If this is a genuine whole-file rewrite, re-submit ApplyDiff with the complete file content included in 'changes'. For a partial edit, use changesetFormat=diff instead.")
                        };
                    }

                    // Normalize each file's content to the policy EOL (resolver).
                    var normalizedChanges = new DocumentChangeSet();
                    foreach (var (changeTargetPath, newContent) in resolvedChanges)
                    {
                        var existingText = await ExistingTextReader.ReadAsync(_workspaceManager, changeTargetPath, cancellationToken);
                        var normalizedContent = newContent;
                        if (!string.IsNullOrEmpty(existingText))
                        {
                            var targetEol = _workspaceManager.ResolveEol((string)changeTargetPath, existingText);
                            normalizedContent = EolUtilities.NormalizeEol(newContent, targetEol);
                        }
                        normalizedChanges[changeTargetPath] = normalizedContent;
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(normalizedChanges, retryCount, validateChanges: validateOnApply, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
                    if (WriteRefusal.TryMap("ApplyDiff", result, out var refusal, WriteRefusal.LedgerOf(_workspaceManager)))
                        return new SentinelCallToolResult<object>() { IsError = true, ErrorData = refusal };
                    IReadOnlyList<Finding>? nearMissFindings = null;
                    if (result.IsError && result.ValidationResult != null
                        && await NearMissAutoCorrector.TryAutoCorrectAsync(_workspaceManager, normalizedChanges, result.ValidationResult, "ApplyDiff", StaleReadPolicy.RefuseIfObservedStale, cancellationToken) is { } nearMissFix)
                    {
                        result = nearMissFix.ApplyResult;
                        normalizedChanges = new DocumentChangeSet(nearMissFix.CorrectedChanges);
                        nearMissFindings = nearMissFix.Findings;
                    }

                    if (result.IsError && result.ValidationResult != null)
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = await EditImpactGate.RejectionAsync(_workspaceManager, normalizedChanges, result.ValidationResult, _activeToolSurface,
                                "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n",
                                renamedAway => CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable, renamedAwayNames: renamedAway), cancellationToken)
                        };
                    if (result.IsError)
                        return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(result.RefusalCode ?? ToolErrorCode.Exception, $"ApplyDiff failed to apply the change set: {result.Summary}") };
                    var changeId = Guid.NewGuid().ToString("n")[..8];
                    var blobWrite = await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result, cancellationToken: cancellationToken, blobChangeId: changeId);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetailTool/
                    // UndoChangeTool can retrieve it) and was the single largest contributor to
                    // ApplyDiffTool responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null, ChangeId = blobWrite.Written ? changeId : null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = ValidateAndApplyHelper.BuildDiffFromPreImages(resolvedChanges, result.PreImages)
                        }
                        : strippedResult;
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = responseData,
                        Findings = nearMissFindings is null ? (result.Findings ?? []) : (result.Findings ?? []).Concat(nearMissFindings).ToList()
                    };
                }

                if (action == ProposedChangeAction.validate)
                {
                    try
                    {
                        // Normalize each file's content to the policy EOL (resolver) before validation.
                        var normalizedValidateChanges = new DocumentChangeSet();
                        foreach (var (valPath, valContent) in resolvedChanges)
                        {
                            var existingText = await ExistingTextReader.ReadAsync(_workspaceManager, valPath, cancellationToken);
                            var normalizedContent = valContent;
                            if (!string.IsNullOrEmpty(existingText))
                            {
                                var targetEol = _workspaceManager.ResolveEol((string)valPath, existingText);
                                normalizedContent = EolUtilities.NormalizeEol(valContent, targetEol);
                            }
                            normalizedValidateChanges[valPath] = normalizedContent;
                        }

                        var validationResult = await _validationEngine.ValidateChangesAsync(normalizedValidateChanges, cancellationToken: cancellationToken);
                        return !validationResult.IsError ? new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = validationResult
                        }

                        : new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.ValidationFailed, $"ApplyDiff validate failed: {validationResult.Diagnostics}")
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff validate unexpected exception");
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff validate")
                        };
                    }
                }
            }
            else if (changesetFormat == ChangesetFormat.diff)
            {
                if (filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.ForFilePath("ApplyDiff", _workspaceManager.LoadState))
                    };
                }

                if (!filePathResolved.Validated && string.IsNullOrWhiteSpace(filePath) && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filePath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePathResolved.Validated)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, string.IsNullOrWhiteSpace(filePath) ? "ApplyDiff: 'filePath' is required when changesetFormat=diff (it names the single file the unifiedDiff applies to). Only changesetFormat=files takes multiple files via 'changes'." : $"ApplyDiff: 'filePath' ('{filePath}') could not be resolved: the loaded solution has no root directory to resolve a relative path against. Pass an absolute path.")
                    };
                }

                if (string.IsNullOrEmpty(unifiedDiff))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'unifiedDiff' is required when changesetFormat=diff.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    try
                    {
                        // One lookup, owned by the read chokepoint: case-insensitive exact path, then a unique
                        // bare name; a miss names the closest real paths (or the ambiguous candidates).
                        var lookup = await _workspaceManager.GetDocumentAsync(filePathResolved, ReadSource.Committed, cancellationToken);
                        if (!lookup.TryGetDocument(out var document))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = lookup.ToResultError()
                            };
                        }

                        var oldText = await document.GetTextAsync(cancellationToken: cancellationToken);
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff, out var diffReport).ToString();
                        var targetPath = document.FilePath ?? filePathResolved;
                        var diffChanges = new DocumentChangeSet
                        {
                            [targetPath] = newContent
                        };
                        // TODO no shrink/no-type-left guard here (ApplyDiff files format has LargeShrinkRejectionThreshold; ReplaceSnippet has the no-type-left guard)
                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
                        // TODO a non-mapped, non-validation refusal (IsError without ValidationResult) falls through to IsError = false below; map it once decided (plan_stale_read_guard_followups open decision 9)
                        if (WriteRefusal.TryMap("ApplyDiff", result, out var refusal, WriteRefusal.LedgerOf(_workspaceManager)))
                            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = refusal };
                        IReadOnlyList<Finding>? nearMissFindings = null;
                        if (result.IsError && result.ValidationResult != null
                            && await NearMissAutoCorrector.TryAutoCorrectAsync(_workspaceManager, diffChanges, result.ValidationResult, "ApplyDiff", StaleReadPolicy.RefuseIfObservedStale, cancellationToken) is { } nearMissFix)
                        {
                            result = nearMissFix.ApplyResult;
                            diffChanges = new DocumentChangeSet(nearMissFix.CorrectedChanges);
                            nearMissFindings = nearMissFix.Findings;
                        }

                        if (result.IsError && result.ValidationResult != null)
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = await EditImpactGate.RejectionAsync(_workspaceManager, diffChanges, result.ValidationResult, _activeToolSurface,
                                    "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n",
                                    renamedAway => CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable, renamedAwayNames: renamedAway), cancellationToken)
                            };
                        var changeId = Guid.NewGuid().ToString("n")[..8];
                        var blobWrite = await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result, cancellationToken: cancellationToken, blobChangeId: changeId);
                        var strippedDiffResult = result with { PreImages = null, ChangeId = blobWrite.Written ? changeId : null };
                        object diffResponseData = BuildDiffApplyResponseData(strippedDiffResult, diffReport, returnDiff, diffChanges, result.PreImages);
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = diffResponseData,
                            Findings = nearMissFindings is null ? MergeDiffApplyFindings(result.Findings, diffReport) : MergeDiffApplyFindings(result.Findings, diffReport).Concat(nearMissFindings).ToList()
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePathResolved}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePathResolved.Absolute, unifiedDiff, cancellationToken: cancellationToken);
                    return !validationResult.IsError ? new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = validationResult
                    }

                    : new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.ValidationFailed, $"ApplyDiff diff validate failed: {validationResult.Diagnostics.ToInfo()}")
                    };
                }
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.Exception, $"Unhandled changesetFormat '{changesetFormat}' / action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyDiff ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff")
            };
        }
    }
    // Simplified, diff-only sibling of ApplyDiffTool -> collapses ApplyDiffTool's two required-param-sets
    // (files: 'changes' dict / diff: 'filePath'+'unifiedDiff', with 'filePath' silently ignored in
    // files mode) into a single always-required (filePath, unifiedDiff) pair, closing the common
    // agent footgun of supplying unifiedDiff without filePath. Whole-file rewrites now go through
    // WriteFileTool(operation=ReplaceFile) instead of a 'files' mode here. ApplyDiffTool itself is kept
    // unchanged (not deleted) so its multi-file 'files' mode can be reactivated later if needed.
    // Moved here (off the default MCP surface, this class carries no [McpServerToolType]) from
    // WorkspaceTools.cs -> ReplaceSnippetTool there now covers small exact-text edits on the
    // default surface without diff-hunk syntax; this tool is kept for reactivation if a genuine
    // need for multi-line diff-hunk edits resurfaces. See
    // docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    [McpServerTool(Name = "ApplyUnifiedDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a unified diff against a single file; mismatched hunk lines are re-anchored nearby.")]
    public async Task<SentinelCallToolResult<object>> ApplyUnifiedDiffTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        [Description("The single file unifiedDiff applies to.")]
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("The unified diff to apply to filePath.")]
        [ToolOption(ToolOptionTag.UnifiedDiff, required: true)] string unifiedDiff,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
            if (!filePathResolved.Validated)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.ForFilePath("ApplyUnifiedDiff", _workspaceManager.LoadState))
                        : new ResultError(ToolErrorCode.InvalidArgument, string.IsNullOrWhiteSpace(filePath) ? "ApplyUnifiedDiff: 'filePath' is required (it names the single file the unifiedDiff applies to)." : $"ApplyUnifiedDiff: 'filePath' ('{filePath}') could not be resolved: the loaded solution has no root directory to resolve a relative path against. Pass an absolute path.")
                };
            }

            if (string.IsNullOrEmpty(unifiedDiff))
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ApplyUnifiedDiff: 'unifiedDiff' is required.")
                };
            }

            if (action == ProposedChangeAction.apply)
            {
                try
                {
                    // One lookup, owned by the read chokepoint: case-insensitive exact path, then a unique
                    // bare name; a miss names the closest real paths (or the ambiguous candidates).
                    var lookup = await _workspaceManager.GetDocumentAsync(filePathResolved, ReadSource.Committed, cancellationToken);
                    if (!lookup.TryGetDocument(out var document))
                    {
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = lookup.ToResultError()
                        };
                    }

                    var oldText = await document.GetTextAsync(cancellationToken: cancellationToken);
                    var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff, out var diffReport).ToString();
                    var targetPath = document.FilePath ?? filePathResolved;
                    var diffChanges = new DocumentChangeSet
                    {
                        [targetPath] = newContent
                    };
                    // TODO no shrink/no-type-left guard here (ApplyDiff files format has LargeShrinkRejectionThreshold; ReplaceSnippet has the no-type-left guard)
                    var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
                    // TODO a non-mapped, non-validation refusal (IsError without ValidationResult) falls through to IsError = false below; map it once decided (plan_stale_read_guard_followups open decision 9)
                    if (WriteRefusal.TryMap("ApplyUnifiedDiff", result, out var refusal, WriteRefusal.LedgerOf(_workspaceManager)))
                        return new SentinelCallToolResult<object>() { IsError = true, ErrorData = refusal };
                    IReadOnlyList<Finding>? nearMissFindings = null;
                    if (result.IsError && result.ValidationResult != null
                        && await NearMissAutoCorrector.TryAutoCorrectAsync(_workspaceManager, diffChanges, result.ValidationResult, "ApplyUnifiedDiff", StaleReadPolicy.RefuseIfObservedStale, cancellationToken) is { } nearMissFix)
                    {
                        result = nearMissFix.ApplyResult;
                        diffChanges = new DocumentChangeSet(nearMissFix.CorrectedChanges);
                        nearMissFindings = nearMissFix.Findings;
                    }

                    if (result.IsError && result.ValidationResult != null)
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = await EditImpactGate.RejectionAsync(_workspaceManager, diffChanges, result.ValidationResult, _activeToolSurface,
                                "ApplyUnifiedDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n",
                                renamedAway => CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable, renamedAwayNames: renamedAway), cancellationToken)
                        };
                    var changeId = Guid.NewGuid().ToString("n")[..8];
                    var blobWrite = await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_unified_diff", result, cancellationToken: cancellationToken, blobChangeId: changeId);
                    var strippedDiffResult = result with { PreImages = null, ChangeId = blobWrite.Written ? changeId : null };
                    object diffResponseData = BuildDiffApplyResponseData(strippedDiffResult, diffReport, returnDiff, diffChanges, result.PreImages);
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = diffResponseData,
                        Findings = nearMissFindings is null ? MergeDiffApplyFindings(result.Findings, diffReport) : MergeDiffApplyFindings(result.Findings, diffReport).Concat(nearMissFindings).ToList(),
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ApplyUnifiedDiff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyUnifiedDiff apply for '{filePathResolved}'")
                    };
                }
            }

            if (action == ProposedChangeAction.validate)
            {
                var validationResult = await _validationEngine.ValidateDiffAsync(filePathResolved.Absolute, unifiedDiff, cancellationToken: cancellationToken);
                return !validationResult.IsError ? new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = validationResult
                }

                : new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.ValidationFailed, $"ApplyUnifiedDiff validate failed.", StructuredDetail: validationResult.Diagnostics)
                };
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.Exception, $"Unhandled action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyUnifiedDiff ({Action}) failed", action);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyUnifiedDiff")
            };
        }
    }
}

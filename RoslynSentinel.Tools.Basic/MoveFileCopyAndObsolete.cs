using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// The copyAndObsolete branch of MoveFile for a .cs source: the checks (<see cref="CopyAndObsoleteChecks"/>), the best-effort caller migration
/// (<see cref="CopyAndObsoleteMigration"/>) and one atomic apply of the result (the copy, the [Obsolete]-marked original and every migrated caller as one
/// compile-gated change set, one operation blob and one changeId). The caller (MoveFileTool) has already run the generic validations (same path, directory,
/// missing source, existing destination, binary file, strict UTF-8 decode); a refusal of the checks or of the migration is returned as the tool error unchanged.
/// </summary>
internal static class MoveFileCopyAndObsolete
{
    /// <summary>
    /// Operation name of the write: it names the operation blob (<c>copy_and_obsolete_file_*.json</c>) and prefixes the compile-gate error text. Deliberately not
    /// <c>move_file</c>: <c>GitStatusRenameHints</c> reads the blobs named <c>move_file_*</c> as "moved, source deleted" pairs, and a copy leaves the source in place.
    /// </summary>
    internal const string OperationName = "copy_and_obsolete_file";

    // Listed callers per list in the description; the total is always stated.
    private const int MaxListed = 20;

    // Observations of the repair loop carried into the description.
    private const int MaxFacts = 5;

    private const char Bom = '﻿';

    /// <summary>
    /// Runs the checks and the migration for <paramref name="source"/> to <paramref name="destination"/> against <paramref name="solution"/> (the committed
    /// solution) and applies the result. <paramref name="sourceText"/> is the source file decoded from its bytes, a leading BOM kept as U+FEFF.
    /// </summary>
    internal static async Task<SentinelCallToolResult<AppliedChangeSummary>> RunAsync(
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        ActiveToolSurface? activeToolSurface,
        ILogger logger,
        Solution solution,
        FilePathWrapper source,
        FilePathWrapper destination,
        string sourceText,
        CancellationToken cancellationToken)
    {
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, source, destination, cancellationToken);
        if (check.Refusal is { } checkRefusal)
        {
            return Failed(checkRefusal);
        }

        var migration = await CopyAndObsoleteMigration.RunAsync(solution, source, destination, sourceText, check, cancellationToken);
        if (migration.Refusal is { } migrationRefusal)
        {
            return Failed(migrationRefusal);
        }

        var changes = await WithDiskBomsAsync(migration, cancellationToken);
        var solutionRoot = workspaceManager.GetSolutionRoot();
        var sourceDisplay = EditEffectNames.DisplayPath(source.Absolute, solutionRoot);
        var destinationDisplay = EditEffectNames.DisplayPath(destination.Absolute, solutionRoot);

        // No deletePaths: the original stays. No effect is recorded: EditEffectKind has no "file copied, types marked obsolete" kind, and FileMoved would
        // tell a later read of the source path that the file moved. FileMoveMemory is not recorded for the same reason (the old path still exists).
        // verbatim: the engine texts carry their BOM and line endings as they must be written (the changed callers' BOM is restored from disk above, because
        // a workspace document's text does not hold it), so the chokepoint must not normalize them.
        // preValidatedAgainst: a migration without a refusal always ends on a clean compile of exactly this set (the loop breaks only on a clean round or a clean
        // final compile; the fall-back set is the base set, which was compiled first), against `solution`. The only difference is a BOM char put back in front of a
        // caller's text above, which the compiler reads as whitespace. The helper skips its duplicate compile only while the committed solution is still that instance;
        // the chokepoint's re-validation after a foreign write, drift and stale-read checks still run.
        var apply = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validationEngine, workspaceManager, logger, changes, OperationName,
            cancellationToken: cancellationToken,
            verbatim: true,
            preValidatedAgainst: solution,
            describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, symbolNavigationEngine, ct, isToolActive: activeToolSurface is null ? null : activeToolSurface.IsCallable),
            describeEditImpact: EditImpactGate.Describer(workspaceManager, changes, activeToolSurface));

        if (apply.Error is { } error)
        {
            var refusal = error.ErrorCode == ToolErrorCode.ValidationFailed
                ? error with
                {
                    Message = $"MoveFile: copying '{sourceDisplay}' to '{destinationDisplay}' (copyAndObsolete) would break the build; nothing was written. {error.Message}"
                }
                : error;
            return Failed(refusal);
        }

        var affected = new List<FilePathWrapper> { destination, source };
        affected.AddRange(migration.MigratedCallers.Select(caller => caller.FilePath));
        var description = Describe(workspaceManager, solution, source, destination, check, migration);

        return new SentinelCallToolResult<AppliedChangeSummary>
        {
            IsError = false,
            SuccessData = AppliedChangeSummary.From(apply, affected, description),
            Findings = apply.Findings ?? []
        };
    }

    private static SentinelCallToolResult<AppliedChangeSummary> Failed(ResultError error) => new() { IsError = true, ErrorData = error };

    // The migration's change set holds the callers' text as the workspace document had it, and a document's text carries no BOM (Roslyn keeps the encoding
    // beside the text). The write is verbatim, so a caller whose file on disk starts with the UTF-8 BOM gets U+FEFF put back in front of its text; the copy
    // and the original already carry theirs (they were built from the decoded bytes). Line endings need no such step: the rewrite splices into the text
    // with the file's own endings. A file that cannot be read keeps the engine's text (the chokepoint's drift check reports an unreadable file).
    private static async Task<DocumentChangeSet> WithDiskBomsAsync(CopyAndObsoleteMigrationResult migration, CancellationToken cancellationToken)
    {
        var changes = new DocumentChangeSet();
        changes.Merge(migration.Changes!);
        foreach (var caller in migration.MigratedCallers)
        {
            if (caller.NewText.Length > 0 && caller.NewText[0] == Bom)
            {
                continue;
            }

            try
            {
                var head = new byte[3];
                await using var stream = new FileStream(caller.FilePath.Absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var read = await stream.ReadAsync(head, cancellationToken);
                if (read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                {
                    changes[caller.FilePath] = Bom + caller.NewText;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left as the engine produced it.
            }
        }

        return changes;
    }

    // Facts only: what was copied and marked, which callers moved (mode and count), which stay with the blocking error, duplicated static state, the fall-back and the rounds.
    private static string Describe(IWorkspaceManager workspaceManager, Solution solution, FilePathWrapper source, FilePathWrapper destination, CopyAndObsoleteCheckResult check, CopyAndObsoleteMigrationResult migration)
    {
        var solutionRoot = workspaceManager.GetSolutionRoot();
        string Display(FilePathWrapper path) => EditEffectNames.DisplayPath(path.Absolute, solutionRoot);
        string ProjectOf(FilePathWrapper path) => SolutionProjectLocator.FindContainingProject(solution, path.Absolute)?.Name ?? "(no loaded project)";
        string Namespace(string? name) => string.IsNullOrEmpty(name) ? "(global namespace)" : $"'{name}'";

        var newNamespace = check.NewNamespace ?? string.Empty;
        var obsoleteTypes = check.Types.Where(type => !migration.AlreadyObsoleteTypeNames.Contains(type.Name)).Select(type => type.Name).ToList();
        var lines = new List<string>
        {
            $"Copied '{Display(source)}' (project '{ProjectOf(source)}') to '{Display(destination)}' (project '{ProjectOf(destination)}') with the folder-derived namespace {Namespace(check.NewNamespace)}; the original declared {Namespace(check.OldNamespace)} and stays at its path."
        };
        if (obsoleteTypes.Count > 0)
        {
            lines.Add($"Marked [System.Obsolete(\"Use {newNamespace}.<Type>\")] in the original: {string.Join(", ", obsoleteTypes)}.");
        }

        if (migration.AlreadyObsoleteTypeNames.Count > 0)
        {
            lines.Add($"Already marked [Obsolete] and left as written in the original: {string.Join(", ", migration.AlreadyObsoleteTypeNames)}.");
        }

        if (migration.AddedUsings.Count > 0)
        {
            lines.Add($"Using directives added in the copy: {string.Join(", ", migration.AddedUsings)}.");
        }

        if (migration.MigratedCallers.Count == 0 && migration.RemainingCallers.Count == 0)
        {
            lines.Add("No other file was changed or left behind on the old types: no caller file referenced them.");
        }

        if (migration.MigratedCallers.Count > 0)
        {
            lines.Add($"Migrated callers ({migration.MigratedCallers.Count}), now on {Namespace(check.NewNamespace)}:");
            lines.AddRange(migration.MigratedCallers.Take(MaxListed).Select(caller => $"  {Display(caller.FilePath)} ({caller.Mode}, {caller.ReferencesRewritten} reference(s))"));
            AddMore(lines, migration.MigratedCallers.Count);
        }

        if (migration.RemainingCallers.Count > 0)
        {
            lines.Add($"Remaining callers on the old obsolete types ({migration.RemainingCallers.Count}): text unchanged, the compiler reports CS0618 warnings there; the error that blocked each file's rewrite:");
            lines.AddRange(migration.RemainingCallers.Take(MaxListed).Select(caller =>
                $"  {Display(caller.FilePath)}:{caller.FirstReferenceLine}  {caller.ErrorCode} {caller.ErrorMessage}"
                + (PathComparison.Comparer.Equals(caller.BlockingFile.Absolute, caller.FilePath.Absolute) ? string.Empty : $" [reported in {Display(caller.BlockingFile)}:{caller.BlockingLine}]")));
            AddMore(lines, migration.RemainingCallers.Count);
        }

        if (migration.StaticFieldTypeNames.Count > 0)
        {
            lines.Add($"Types with static non-const fields now exist twice, each copy with its own static state: {string.Join(", ", migration.StaticFieldTypeNames)}.");
        }

        if (migration.FellBackToBaseSet)
        {
            lines.Add("The caller rewrites did not compile together after the last round, so every caller was left unchanged.");
        }

        lines.AddRange(migration.Facts.Take(MaxFacts));
        if (migration.Facts.Count > MaxFacts)
        {
            lines.Add($"({migration.Facts.Count - MaxFacts} more observation(s) not listed.)");
        }

        lines.Add($"Compile rounds used: {migration.RoundsUsed}.");
        return string.Join("\n", lines);
    }

    private static void AddMore(List<string> lines, int total)
    {
        if (total > MaxListed)
        {
            lines.Add($"  ... and {total - MaxListed} more ({total} in total).");
        }
    }
}

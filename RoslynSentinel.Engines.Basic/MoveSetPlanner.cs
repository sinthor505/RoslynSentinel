using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Plans a multi-file move set in two stages: expansion and eligibility, then project check and facts.</summary>
public static class MoveSetPlanner
{
    /// <summary>The most files one move set call may name.</summary>
    public const int MaxFilesPerCall = 200;

    /// <summary>Stage 1: expands the request to files and checks each move's eligibility.</summary>
    public static async Task<MoveSetPlanResult> ExpandAndCheckAsync(MoveSetRequest request, CancellationToken cancellationToken)
    {
        var expanded = new List<PlannedFileMove>();
        var offenders = new List<MoveOffender>();
        foreach (var pair in request.Pairs)
        {
            ExpandPair(pair, expanded, offenders);
        }

        if (expanded.Count > MaxFilesPerCall)
        {
            var capDetail = $"the move set expands to {expanded.Count} files, above the cap of {MaxFilesPerCall} per call; splitting the move into several calls is the option.";
            throw new MoveSetRefusedException($"MoveFile: {capDetail} Nothing was moved.", new List<MoveOffender> { new MoveOffender(request.Pairs[0].SourcePath, MoveOffenderReason.OverCap, capDetail) });
        }

        var destinationSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<PlannedFileMove>(expanded.Count);
        var changes = new DocumentChangeSet();
        var deletePaths = new List<FilePathWrapper>(expanded.Count);
        foreach (var move in expanded)
        {
            var offender = CheckPaths(move, destinationSources);
            if (offender is null)
            {
                var bytes = await File.ReadAllBytesAsync(move.Source.Absolute, cancellationToken);
                offender = CheckBytes(move.Source.Absolute, bytes, out var text);
                if (offender is null)
                {
                    changes[move.Destination] = text;
                }
            }

            if (offender is not null)
            {
                offenders.Add(offender);
            }

            moves.Add(move);
            deletePaths.Add(move.Source);
        }

        if (offenders.Count > 0)
        {
            var details = string.Join(" ", offenders.Select(o => o.Detail));
            throw new MoveSetRefusedException($"MoveFile: {details} Nothing was moved.", offenders);
        }

        var sourceFolders = request.Pairs.Where(pair => Directory.Exists(pair.SourcePath)).Select(pair => pair.SourcePath).ToList();
        return new MoveSetPlanResult(moves, changes, deletePaths, Array.Empty<MoveFact>(), Array.Empty<NeededProjectEdge>(), sourceFolders);
    }

    /// <summary>Expands one pair: a file pair stays one entry; a folder pair expands to its files with subfolders preserved.</summary>
    private static void ExpandPair(FileMovePair pair, List<PlannedFileMove> expanded, List<MoveOffender> offenders)
    {
        if (!Directory.Exists(pair.SourcePath))
        {
            expanded.Add(new PlannedFileMove(pair.SourcePath, pair.DestinationPath, null, null, false));
            return;
        }

        if (File.Exists(pair.DestinationPath))
        {
            offenders.Add(DestinationExistsOffender(pair.SourcePath, pair.DestinationPath, "file"));
            return;
        }

        foreach (var file in Directory.EnumerateFiles(pair.SourcePath, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(pair.SourcePath, file);
            expanded.Add(new PlannedFileMove(file, Path.Combine(pair.DestinationPath, relative), null, null, true));
        }
    }

    /// <summary>Path-level reasons for one file pair, in legacy order; also records the destination for the duplicate check.</summary>
    private static MoveOffender? CheckPaths(PlannedFileMove move, Dictionary<string, string> destinationSources)
    {
        var source = move.Source.Absolute;
        var destination = move.Destination.Absolute;
        string? earlierSource = destinationSources.TryGetValue(destination, out var firstSource) ? firstSource : null;
        destinationSources.TryAdd(destination, source);

        if (move.Source.Equals(move.Destination))
        {
            return new MoveOffender(source, MoveOffenderReason.SamePath, $"{MoveFileParameterNames.SourcePath} and {MoveFileParameterNames.DestinationPath} resolve to the same path '{source}'.");
        }

        if (!File.Exists(source))
        {
            return new MoveOffender(source, MoveOffenderReason.Missing, $"'{source}' does not exist on disk (path comparison ignores case).");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return DestinationExistsOffender(source, destination, Directory.Exists(destination) ? "directory" : "file");
        }

        if (earlierSource is null)
        {
            return null;
        }

        return new MoveOffender(source, MoveOffenderReason.DuplicateDestination, $"the destination '{destination}' is the destination of both '{earlierSource}' and '{source}' in this set; a move does not overwrite.");
    }

    /// <summary>Content-level reasons (NUL byte in the first 8000 bytes, strict UTF-8) and the decoded text when the file passes.</summary>
    private static MoveOffender? CheckBytes(string source, byte[] bytes, out string text)
    {
        text = string.Empty;
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
        {
            return new MoveOffender(source, MoveOffenderReason.Binary, $"'{source}' contains a NUL byte within its first 8000 bytes, so it is treated as a binary file; this tool moves text files only.");
        }

        try
        {
            text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return null;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return new MoveOffender(source, MoveOffenderReason.NotUtf8, $"'{source}' is not valid UTF-8 text, so it cannot be moved without altering its bytes; this tool moves UTF-8 text files only.");
        }
    }

    private static MoveOffender DestinationExistsOffender(string source, string destination, string kind) =>
        new(source, MoveOffenderReason.DestinationExists, $"the destination '{destination}' already exists as a {kind}; a move does not overwrite. Options: pass a {MoveFileParameterNames.DestinationPath} that does not exist, or remove the existing {kind} first.");

    /// <summary>Stage 2: checks the projects touched by the plan and collects its facts.</summary>
    public static async Task<MoveSetPlanResult> CheckProjectsAndCollectFactsAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        const string options = "Options: LoadSolution with forceReload after adding a project that covers the destination folder, or a destination inside a loaded project.";
        var moves = new List<PlannedFileMove>(plan.Moves.Count);
        var facts = new List<MoveFact>(plan.Facts);
        var offenders = new List<MoveOffender>();
        var keptPairs = new HashSet<(string Declared, string Expected)>();
        foreach (var move in plan.Moves)
        {
            var sourceProject = SolutionProjectLocator.FindContainingProject(committed, move.Source.Absolute);
            var destinationProject = SolutionProjectLocator.FindContainingProject(committed, move.Destination.Absolute);
            moves.Add(move with { SourceProjectName = sourceProject?.Name, DestinationProjectName = destinationProject?.Name });
            if (!IsCSharpFile(move.Source.Absolute))
            {
                continue;
            }

            if (destinationProject is null)
            {
                offenders.Add(new MoveOffender(move.Destination.Absolute, MoveOffenderReason.OutsideLoadedProject, $"the destination '{move.Destination.Absolute}' is inside no loaded project."));
                continue;
            }

            if (KeptNamespace(plan.Changes[move.Destination], destinationProject, move.Destination) is { } kept && keptPairs.Add(kept))
            {
                facts.Add(new MoveFact(MoveFactKind.KeptNamespace, kept.Declared, KeptNamespaceDetail(kept.Declared, kept.Expected)));
            }
        }

        if (offenders.Count > 0)
        {
            var details = string.Join(" ", offenders.Select(o => o.Detail));
            throw new MoveSetRefusedException($"MoveFile: {details} {options} Nothing was moved.", offenders);
        }

        // D8: source project items that name a moved folder are reported, never edited.
        var csprojFacts = new HashSet<(string CsprojPath, string Line)>();
        foreach (var root in plan.SourceFolders ?? Array.Empty<string>())
        {
            var sourceProject = SolutionProjectLocator.FindContainingProject(committed, root);
            if (sourceProject?.FilePath is { } csprojPath
                && File.Exists(csprojPath)
                && Path.GetDirectoryName(csprojPath) is { } projectDirectory)
            {
                var relative = Path.GetRelativePath(projectDirectory, root).Replace('/', '\\');
                if (relative != "." && relative != ".." && !relative.StartsWith("..\\", StringComparison.Ordinal))
                {
                    var csprojText = await File.ReadAllTextAsync(csprojPath, cancellationToken);
                    foreach (var rawLine in csprojText.Split('\n'))
                    {
                        var line = rawLine.Trim();
                        if (NamesFolder(line, relative) && csprojFacts.Add((csprojPath, line)))
                        {
                            facts.Add(new MoveFact(MoveFactKind.SourceCsprojItemNamesFolder, csprojPath, $"not updated: the project file '{csprojPath}' has an item that names the moved folder: {line}."));
                        }
                    }
                }
            }
        }

        foreach (var folder in plan.SourceFolders ?? Array.Empty<string>())
        {
            facts.Add(new MoveFact(MoveFactKind.EmptySourceFolderLeft, folder, $"the source folder '{folder}' is left in place; moving files does not remove folders."));
        }

        return plan with { Moves = moves, Facts = facts };
    }

    private static bool IsCSharpFile(string path) => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    // Moved from the tool's DescribeKeptNamespace (RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs:444-462); the tool's copy stays until Step 13.
    private static (string Declared, string Expected)? KeptNamespace(string text, Project destinationProject, FilePathWrapper destination)
    {
        if (FolderNamespace.Expected(destinationProject, destination) is not { } expected)
        {
            return null;
        }

        var declared = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();
        if (declared is null || string.Equals(declared, expected, StringComparison.Ordinal))
        {
            return null;
        }

        return (declared, expected);
    }

    private static string KeptNamespaceDetail(string declared, string expected) =>
        $"The namespace '{declared}' was kept; the destination folder's convention would give '{expected}'.";

    private static bool NamesFolder(string line, string relativeFolder)
    {
        if (!(line.Contains("<Compile", StringComparison.Ordinal)
            || line.Contains("<None", StringComparison.Ordinal)
            || line.Contains("<Content", StringComparison.Ordinal)
            || line.Contains("<EmbeddedResource", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (Match match in Regex.Matches(line, "(?:Include|Remove|Update)\\s*=\\s*\"([^\"]*)\""))
        {
            var value = match.Groups[1].Value.Replace('/', '\\');
            if (value.Equals(relativeFolder, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(relativeFolder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

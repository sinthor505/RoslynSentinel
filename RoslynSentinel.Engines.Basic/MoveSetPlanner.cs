using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
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

        return new MoveSetPlanResult(moves, changes, deletePaths, Array.Empty<MoveFact>(), Array.Empty<NeededProjectEdge>());
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
    public static Task<MoveSetPlanResult> CheckProjectsAndCollectFactsAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 10)
    }
}

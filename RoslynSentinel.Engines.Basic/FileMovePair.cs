using System.Collections.Generic;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>One source and destination pair for a multi-file MoveFile call; paths are as given on the wire.</summary>
public sealed class FileMovePair
{
    [BatchItemField("Path of the file or folder to move.", Required = true)]
    public string SourcePath { get; set; } = "";

    [BatchItemField("Path the file or folder moves to. For a folder pair, the folder's contents land at this path.", Required = true)]
    public string DestinationPath { get; set; } = "";
}

/// <summary>A set of move pairs to plan, with absolute paths already resolved from the wire.</summary>
public sealed record MoveSetRequest(IReadOnlyList<FileMovePair> Pairs);

/// <summary>Wire names of the MoveFile parameters (camelCase), used in findings that name a parameter.</summary>
public static class MoveFileParameterNames
{
    public const string SourcePath = "sourcePath";
    public const string DestinationPath = "destinationPath";
    public const string AddProjectReferences = "addProjectReferences";
    public const string AddInternalsVisibleTo = "addInternalsVisibleTo";
}

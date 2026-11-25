using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>The planned result of a multi-file move: the moves, the document changes, the paths to delete and the facts found.</summary>
public sealed record MoveSetPlanResult(
    IReadOnlyList<PlannedFileMove> Moves,
    DocumentChangeSet Changes,
    IReadOnlyList<FilePathWrapper> DeletePaths,
    IReadOnlyList<MoveFact> Facts,
    IReadOnlyList<NeededProjectEdge> Edges);

/// <summary>One planned file move, with its source and destination projects when known.</summary>
public sealed record PlannedFileMove(
    FilePathWrapper Source,
    FilePathWrapper Destination,
    string? SourceProjectName,
    string? DestinationProjectName,
    bool FromFolderPair);

/// <summary>Why a move set member was refused.</summary>
public enum MoveOffenderReason
{
    Missing,
    Binary,
    NotUtf8,
    SamePath,
    DestinationExists,
    DuplicateDestination,
    OutsideLoadedProject,
    OverCap,
}

/// <summary>One refused move set member, with its reason and detail.</summary>
public sealed record MoveOffender(string Path, MoveOffenderReason Reason, string Detail)
{
    /// <summary>The tool error code for a refusal reason.</summary>
    public static string ErrorCodeOf(MoveOffenderReason reason)
    {
        switch (reason)
        {
            case MoveOffenderReason.Missing:
                return ToolErrorCode.NotFound;
            case MoveOffenderReason.Binary:
            case MoveOffenderReason.NotUtf8:
                return ToolErrorCode.TargetIneligible;
            case MoveOffenderReason.SamePath:
            case MoveOffenderReason.DestinationExists:
            case MoveOffenderReason.DuplicateDestination:
            case MoveOffenderReason.OutsideLoadedProject:
            case MoveOffenderReason.OverCap:
                return ToolErrorCode.InvalidArgument;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
        }
    }
}

/// <summary>The kinds of facts a move set plan reports.</summary>
public enum MoveFactKind
{
    KeptNamespace,
    SourceCsprojItemNamesFolder,
    MissingProjectReference,
    MissingInternalsVisibleTo,
    MissingPackageReference,
    EdgeWouldCloseCycle,
    CsprojEdgeAdded,
    EmptySourceFolderLeft,
    ReloadPending,
    WorkspaceChangedDuringGate,
}

/// <summary>A typed fact found while planning a move set, lifted to a finding for the caller.</summary>
public sealed record MoveFact(MoveFactKind Kind, string Subject, string? Detail = null);

/// <summary>The kinds of project edge a move set may need.</summary>
public enum ProjectEdgeKind
{
    ProjectReference,
    InternalsVisibleTo,
}

/// <summary>A project edge the move set needs but that is absent, with the reason it is needed.</summary>
public sealed record NeededProjectEdge(ProjectEdgeKind Kind, string FromProject, string ToProject, string Reason);

/// <summary>Diagnostics grouped by the move set file they bind to.</summary>
public sealed record MoveSetErrorGroup(string FilePath, string? SourcePath, bool OutsideSet, IReadOnlyList<string> Diagnostics);

/// <summary>A move set was refused; carries each refused member in <see cref="Offenders"/>.</summary>
public sealed class MoveSetRefusedException : ToolException
{
    public MoveSetRefusedException(string message, IReadOnlyList<MoveOffender> offenders) : base(message)
    {
        Offenders = offenders;
    }

    /// <summary>The refused move set members, in request order.</summary>
    public IReadOnlyList<MoveOffender> Offenders { get; }

    public override string ErrorCode
        => Offenders.Count == 0
            ? ToolErrorCode.InvalidArgument
            : Offenders.Select(o => MoveOffender.ErrorCodeOf(o.Reason)).Distinct().Count() == 1
                ? MoveOffender.ErrorCodeOf(Offenders[0].Reason)
                : ToolErrorCode.TargetIneligible;

    /// <summary>The refused members as objects; null when there are none.</summary>
    public override IReadOnlyList<object>? StructuredDetail => Offenders.Count == 0 ? null : Offenders.Cast<object>().ToList();
}

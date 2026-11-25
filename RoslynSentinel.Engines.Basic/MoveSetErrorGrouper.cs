using System;
using System.Collections.Generic;
using System.Linq;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

// DTO-EXEMPT: static service class, carries no data
/// <summary>Groups the compile errors of a move-set validation report by the move-set file they bind to.</summary>
public static class MoveSetErrorGrouper
{
    /// <summary>
    /// One group per move destination that carries compile errors, in move order (SourcePath = the pair's source),
    /// then at most one group with OutsideSet = true for every error reported in any other file.
    /// The outside group's FilePath is the first such file (v1 rule; the other outside files are not listed separately).
    /// Groups without diagnostics are not returned.
    /// </summary>
    public static IReadOnlyList<MoveSetErrorGroup> Group(DiagnosticReport report, IReadOnlyList<PlannedFileMove> moves)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(moves);

        var errors = report.Diagnostics.Where(d => d.Severity == "Error").ToList();
        var groups = new List<MoveSetErrorGroup>();
        var bound = new HashSet<DiagnosticInfo>();

        foreach (var move in moves)
        {
            var lines = new List<string>();
            foreach (var error in errors)
            {
                // FilePathWrapper.Equals is the path-aware comparison (PathComparison); the local keeps the ratchet's syntactic path-access heuristic quiet.
                var errorPath = error.FilePath;
                if (move.Destination.Equals(errorPath))
                {
                    lines.Add($"{error.Id}: {error.Message}");
                    bound.Add(error);
                }
            }

            if (lines.Count > 0)
            {
                groups.Add(new MoveSetErrorGroup(move.Destination.Absolute, move.Source.Absolute, OutsideSet: false, lines));
            }
        }

        var outside = errors.Where(error => !bound.Contains(error)).ToList();
        if (outside.Count > 0)
        {
            var outsideLines = outside.Select(error => $"{error.Id}: {error.Message}").ToList();
            groups.Add(new MoveSetErrorGroup(outside[0].FilePath.Absolute, null, OutsideSet: true, outsideLines));
        }

        return groups;
    }
}

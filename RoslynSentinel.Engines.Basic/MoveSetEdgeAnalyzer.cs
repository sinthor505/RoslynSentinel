using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Works out which project references and InternalsVisibleTo edges a multi-file move set needs.</summary>
public static class MoveSetEdgeAnalyzer
{
    /// <summary>Analyzes the move set plan against the committed solution and returns the project edges it needs.</summary>
    public static Task<IReadOnlyList<NeededProjectEdge>> AnalyzeAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 24)
    }

    /// <summary>Returns a copy of the committed solution with the needed edges added as references or InternalsVisibleTo.</summary>
    public static Solution AugmentBaseline(Solution committed, IReadOnlyList<NeededProjectEdge> edges, bool addReferences, bool addInternalsVisibleTo)
    {
        throw new NotImplementedException(); // TODO implement (Step 24)
    }
}

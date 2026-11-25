using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Plans a multi-file move set in two stages: expansion and eligibility, then project check and facts.</summary>
public static class MoveSetPlanner
{
    /// <summary>The most files one move set call may name.</summary>
    public const int MaxFilesPerCall = 200;

    /// <summary>Stage 1: expands the request to files and checks each move's eligibility.</summary>
    public static Task<MoveSetPlanResult> ExpandAndCheckAsync(MoveSetRequest request, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 7)
    }

    /// <summary>Stage 2: checks the projects touched by the plan and collects its facts.</summary>
    public static Task<MoveSetPlanResult> CheckProjectsAndCollectFactsAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 10)
    }
}

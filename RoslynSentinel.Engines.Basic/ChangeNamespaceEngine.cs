using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Plans namespace changes across a committed solution.</summary>
public static class ChangeNamespaceEngine
{
    /// <summary>Plans the caller rewrites for a namespace change (not implemented yet).</summary>
    public static Task<CallerRewriteResult> PlanAsync(ChangeNamespaceRequest request, Solution committed, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 30)
    }
}

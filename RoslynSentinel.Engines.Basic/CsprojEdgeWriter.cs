using System;
using System.Collections.Generic;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Splices the needed project edges into a project file's bytes.</summary>
public static class CsprojEdgeWriter
{
    /// <summary>Returns the project file text with the given edges spliced in as ProjectReference or InternalsVisibleTo items.</summary>
    public static string Splice(byte[] csprojBytes, IReadOnlyList<NeededProjectEdge> edgesForThisProject)
    {
        throw new NotImplementedException(); // TODO implement (Step 25)
    }
}

namespace RoslynSentinel.Common;

public record ImpactReport(
    string SymbolName,
    string SymbolKind,
    List<ReferenceInfo> References,
    int TotalCallSites,
    int AffectedProjectsCount,
    string? Error = null
);

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

// No [ResultRecords] on these records, deliberately: SequencePager.ResolveSequence treats a non-empty declared list as the
// only sequence, so declaring Callers would drop Implementations out of paging. The default sequence pages every list here.

/// <summary><c>Search(mode: callers)</c> success data.</summary>
public record CallersSearchResult(List<CallerInfo> Callers, ListSummary Summary);

/// <summary><c>Search(mode: implementations)</c> success data.</summary>
public record ImplementationsSearchResult(List<ImplementationInfo> Implementations, ListSummary Summary);

/// <summary><c>Search(mode: references)</c> success data: callers and implementations together.</summary>
public record ReferencesSearchResult(List<CallerInfo> Callers, List<ImplementationInfo> Implementations, ListSummary Summary);

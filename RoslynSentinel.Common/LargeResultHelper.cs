using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynSentinel.Common;

public static class LargeResultHelper
{
    // Points at the solution-wide shared instance rather than constructing its own, per
    // docs/current/plans/plan_shared_json_serializer_options.md. JsonOptions is internal (not
    // private) - grepped for external references to LargeResultHelper.JsonOptions before this
    // change; none found outside this file.
    internal static readonly JsonSerializerOptions JsonOptions = SharedJsonOptions.Default;
    public const int OffloadThresholdBytes = 15 * 1024; // 15 KB

    /// <summary>
    /// Counts the items in a tool result's <c>successData</c> for the offload envelope's
    /// <c>itemCount</c> hint: a top-level array counts its elements; an object sums every array found
    /// under it, recursing through nested objects (so an array nested inside a sub-object is counted,
    /// not just direct children). Array elements are never descended into, so an array of records
    /// that each carry their own inner list still counts one per record. Returns null when no array
    /// is found at all, so the hint is omitted rather than reported as a misleading 0.
    /// </summary>
    public static int? CountResultItems(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                return array.Count;
            case JsonObject obj:
                var counts = obj.Select(kv => CountResultItems(kv.Value)).OfType<int>().ToList();
                return counts.Count > 0 ? counts.Sum() : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Serializes <paramref name="data"/> and, if it exceeds <see cref="OffloadThresholdBytes"/>, writes it
    /// to <c>.roslynsentinel/largeresults/largeresult_<timestamp>_<resultId>.json</c> wrapped in a
    /// <see cref="ResultWrapper"/> tagged with <paramref name="wrapperType"/> so <c>GetLargeResult</c> can
    /// deserialize it back. Callers that skip this and hand-write their own file (as GetMethodSource/
    /// ReadFile once did) produce a file GetLargeResult cannot read - always go through this method
    /// instead of reimplementing the write.
    /// </summary>
    public static async Task<(bool offloaded, FilePathWrapper filePath, string? resultId, byte[] jsonBytes)> StoreLargeResultAsync<T>(
        T data, string? solutionRoot, ResultWrapperType wrapperType, CancellationToken cancellationToken)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        if (jsonBytes.Length <= OffloadThresholdBytes || string.IsNullOrEmpty(solutionRoot) || data == null)
        {
            return (false, default, null, jsonBytes);
        }

        var wrapper = new ResultWrapper
        {
            Type = wrapperType,
            Data = JsonSerializer.SerializeToNode(data, JsonOptions)
        };

        var resultId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePathString = Path.Combine(dir, $"largeresult_{timestamp}_{resultId}.json");
        await File.WriteAllTextAsync(filePathString, JsonSerializer.Serialize(wrapper, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        return (true, new FilePathWrapper(filePathString, validated: true), resultId, jsonBytes);
    }

    /// <summary>
    /// Writes already-serialized JSON text verbatim to a <see cref="ResultWrapperType.Raw"/> file,
    /// for callers that only have a final serialized response body -> not a typed value -> such as the
    /// generic MCP request-filter backstop. Unlike <see cref="StoreLargeResultAsync{T}"/>, this never
    /// re-serializes: <paramref name="json"/> is parsed once into a <see cref="JsonNode"/> and wrapped
    /// as-is. Mirrors the same <c>.roslynsentinel/largeresults/largeresult_<timestamp>_<resultId>.json</c>
    /// naming convention so <c>GetLargeResult</c>'s existing file-resolution logic finds it unchanged.
    /// Fails closed: if no solution root is available, returns <c>offloaded: false</c> rather than
    /// throwing, so a guardrail can never itself break the call it's guarding.
    /// </summary>
    public static async Task<(bool offloaded, FilePathWrapper filePath, string? resultId)> StoreRawJsonAsync(
        string json, string? solutionRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(solutionRoot) || string.IsNullOrEmpty(json))
        {
            return (false, default, null);
        }

        var wrapper = new ResultWrapper
        {
            Type = ResultWrapperType.Raw,
            Data = JsonNode.Parse(json)
        };

        var resultId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePathString = Path.Combine(dir, $"largeresult_{timestamp}_{resultId}.json");
        await File.WriteAllTextAsync(filePathString, JsonSerializer.Serialize(wrapper, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        return (true, new FilePathWrapper(filePathString, validated: true), resultId);
    }
}

public record ResultWrapper
{
    public ResultWrapperType Type
    {
        get; init;
    }
    public JsonNode? Data
    {
        get; init;
    }
}

public enum ResultWrapperType
{
    MigrationCandidateFindingList,
    ApiSurfaceEntryList,
    CodeInventoryReport,
    MethodSource,
    FileSource,
    MigrationScanSummary,
    MemberChangedContent,
    AppliedChangeSummaryResult,
    BreakingChangeList,
    TextSearchMatchList,
    ProjectFileList,
    ProjectInfoList,
    SolutionItemFileList,
    SolutionItemsAllResult,
    SolutionSymbolEntryList,
    SymbolRelationshipResultList,
    BroadenedSymbolRelationshipResults,
    Raw,
    ErrorStructuredDetailList
}

/// <summary>Offloaded payload shape for a whole-file ReadFile result too large to inline (mirrors the anonymous shape ReadFile returns inline for the non-offloaded case).</summary>
public record FileSourceResult
{
    public string FilePath { get; init; } = "";
    public int StartLine
    {
        get; init;
    }
    public int EndLine
    {
        get; init;
    }
    public int TotalLines
    {
        get; init;
    }
    public string Source { get; init; } = "";
}

/// <summary>Offloaded payload shape for a mutating member/constructor-parameter tool's changed content, too large to inline.</summary>
public record MemberChangedContentResult
{
    public AppliedChangeSummary Summary { get; init; } = null!;
    public string ChangedContent { get; init; } = "";
}

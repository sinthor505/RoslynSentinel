using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

public static class LargeResultHelper
{
    internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };
    public const int OffloadThresholdBytes = 30 * 1024;

    /// <summary>
    /// Serializes <paramref name="data"/> and, if it exceeds <see cref="OffloadThresholdBytes"/>, writes it
    /// to <c>.roslynsentinel/largeresults/largeresult_&lt;timestamp&gt;_&lt;resultId&gt;.json</c> wrapped in a
    /// <see cref="ResultWrapper"/> tagged with <paramref name="wrapperType"/> so <c>GetLargeResult</c> can
    /// deserialize it back. Callers that skip this and hand-write their own file (as GetMethodSource/
    /// ReadFile once did) produce a file GetLargeResult cannot read - always go through this method
    /// instead of reimplementing the write.
    /// </summary>
    public static async Task<(bool offloaded, FilePath filePath, string? resultId, byte[] jsonBytes)> StoreLargeResultAsync<T>(
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
        return (true, new FilePath(filePathString, solutionRoot, validated: true), resultId, jsonBytes);
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
    BreakingChangeList,
    TextSearchMatchList,
    ProjectFileList,
    ProjectInfoList,
    SolutionItemFileList
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

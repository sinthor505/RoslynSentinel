using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Server;

public static class ScanResultHelper
{
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };
    internal const int ThresholdBytes = 30 * 1024;
}

public record ScanWapper
{
    public ScanWrapperType Type
    {
        get; init;
    }
    public JsonNode Data
    {
        get; init;
    }
}

public enum ScanWrapperType
{
    MigrationCandidateFindingList,
    ApiSurfaceEntryList,
    CodeInventoryReport
}

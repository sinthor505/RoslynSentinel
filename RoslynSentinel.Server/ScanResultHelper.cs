using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using ModelContextProtocol.Server;

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

    // ── get_scan_result ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Pages through a large scan result written to disk when output result payload exceeded the inline size threshold. Supply either scanId (resolves to .roslynsentinel/scans/scan_*_{scanId}.json) or filePath (must match the scan_*.json pattern). Returns ToolResult<object> with TotalRecords and HasMore.
        """)]
    public static async Task<ToolResult<object>> GetScanResult(
        string? scanId = null,
        string? solutionRoot = null,
        string? filePath = null,
        int limit = 50,
        int offset = 0)
    {
        string? resolvedPath = null;

        if (!string.IsNullOrEmpty(scanId) && !string.IsNullOrEmpty(solutionRoot))
        {
            var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "scans");
            if (Directory.Exists(dir))
            {
                resolvedPath = Directory
                    .EnumerateFiles(dir, $"scan_*_{scanId}.json")
                    .FirstOrDefault();
            }
        }
        else if (!string.IsNullOrEmpty(filePath))
        {
            // Validate: path must be inside the scans directory and match the scan_*.json pattern.
            var fileName = System.IO.Path.GetFileName(filePath);
            if (!string.IsNullOrEmpty(solutionRoot))
            {
                var scansDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "scans"));
                var candidate = System.IO.Path.GetFullPath(filePath);
                if (candidate.StartsWith(scansDir, StringComparison.OrdinalIgnoreCase)
                    && fileName.StartsWith("scan_", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    resolvedPath = candidate;
                }
            }
        }

        if (resolvedPath == null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                              "Scan file not found. Supply a valid scanId or filePath pointing to a scan_*.json file in the scans directory.")
            };
        }

        ScanWapper all;
        try
        {
            var json = await File.ReadAllTextAsync(resolvedPath);
            all = JsonSerializer.Deserialize<ScanWapper>(
                      json,
                      _jsonOptions)
                  ?? new ScanWapper();

            ToolResult<object> result;

            switch (all.Type)
            {
                case ScanWrapperType.MigrationCandidateFindingList:
                    {
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = JsonSerializer.Deserialize<List<MigrationCandidateFinding>>(all.Data.ToString(), _jsonOptions)
                        };
                        break;
                    }

                case ScanWrapperType.ApiSurfaceEntryList:
                    {
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                        };
                        break;
                    }
                case ScanWrapperType.CodeInventoryReport:
                    {
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                        };
                        break;
                    }
                default:
                    {
                        return new ToolResult<object>
                        {
                            Success = false,
                            Error = new ResultError("Exception",
                                          "Unknown scan result type.")
                        };
                    }
            }
            ;

            if (result.LargeResult?.SizeBytes > ThresholdBytes)
            {

            }
        }
        catch (Exception ex)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                              "Failed to read scan file.", ex.Message)
            };
        }

        var page = all.Data.AsArray().Skip(offset).Take(limit).ToList();
        bool hasMore = (offset + limit) < all.Data.AsArray().Count;

        return new ToolResult<object>
        {
            Success = true,
            Data = page,
            TotalRecords = all.Data.AsArray().Count,
            HasMore = hasMore,
        };
    }

    internal static async Task<(bool offloaded, string filePath, string scanId, byte[] jsonBytes)> StoreScanResultAsync<T>(T data, string? solutionRoot, ScanWrapperType wrapperType)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        if (jsonBytes.Length <= ThresholdBytes || string.IsNullOrEmpty(solutionRoot))
        {
            return (false, string.Empty, string.Empty, jsonBytes);
        }

        var wrapper = new ScanWapper
        {
            Type = wrapperType,
            Data = JsonSerializer.SerializeToNode(data, _jsonOptions)
        };

        var scanId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "scans");
        Directory.CreateDirectory(dir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePath = Path.Combine(dir, $"scan_{timestamp}_{scanId}.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(wrapper, _jsonOptions), new UTF8Encoding(false));
        return (true, filePath, scanId, jsonBytes);
    }
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

using System.Text;
using System.Text.Json;

namespace RoslynSentinel.Server;

internal static class ScanResultOffloadHelper
{
    private static readonly JsonSerializerOptions _jsonOption = new JsonSerializerOptions
    {
        WriteIndented = true
    };
    internal const int ThresholdBytes = 30 * 1024;

    internal static async Task<(bool offloaded, string filePath, string operationId, byte[] jsonBytes)> TryOffloadAsync<T>(T data, string? solutionRoot)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        if (jsonBytes.Length <= ThresholdBytes || string.IsNullOrEmpty(solutionRoot))
        {
            return (false, string.Empty, string.Empty, jsonBytes);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
        Directory.CreateDirectory(dir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePath = Path.Combine(dir, $"scan_{timestamp}_{operationId}.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(data, _jsonOption), new UTF8Encoding(false));
        return (true, filePath, operationId, jsonBytes);
    }
}

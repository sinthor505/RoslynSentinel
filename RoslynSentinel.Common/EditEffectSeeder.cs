using System.Diagnostics;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// Seeds the <see cref="EditEffectIndex"/> from the operation blobs of the last hour at solution load, so a not-found note survives a server restart
/// (the call ids in the agent's transcript are still valid). Reads only this solution's blob folder (<c>.roslynsentinel/operations</c> under the root),
/// takes the newest <see cref="EditEffectIndex.MaxEffects"/> effects, and skips a blob that <see cref="OperationBlobWriter.IsUndone"/> reports undone. Effects of other
/// windows' blobs are seeded too: identity (session id) is filtered at lookup. Reads JSON by property name and tolerates absent or unknown fields, so blobs from
/// before the <c>effects</c> field existed seed nothing. Fail-open: never throws, and stops at a time budget; the caller runs it off the load path.
/// </summary>
public static class EditEffectSeeder
{
    /// <summary>How far back blobs are read; matches the not-found text note's window.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(60);

    /// <summary>Most blobs opened in one seeding pass, newest first.</summary>
    public const int MaxBlobsOpened = 200;

    // A blob holds full before and after sources; one past this size is skipped rather than parsed in the background.
    private const long MaxBlobBytes = 32L * 1024 * 1024;

    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the recent blobs under <paramref name="solutionRoot"/> and seeds <paramref name="index"/> with their effects (newest bounded to
    /// <see cref="EditEffectIndex.MaxEffects"/>); returns how many effects were offered to the index (the index itself skips ones it already holds).
    /// Returns 0 when there is no root or no blob folder. Never throws.
    /// </summary>
    public static int Seed(EditEffectIndex index, string? solutionRoot, DateTime utcNow, TimeSpan? window = null, TimeSpan? budget = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return 0;
            }

            var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            var cutoff = utcNow - (window ?? DefaultWindow);
            var stopwatch = Stopwatch.StartNew();
            var limit = budget ?? DefaultBudget;
            var recent = Directory.EnumerateFiles(dir, "*.json")
                .Select(path => (BlobPath: path, Written: NotFoundEvidence.TimestampOf(path)))
                .Where(blob => blob.Written >= cutoff)
                .OrderByDescending(blob => blob.Written)
                .ThenByDescending(blob => blob.BlobPath, StringComparer.Ordinal)
                .Take(MaxBlobsOpened);

            var collected = new List<EditEffect>();
            foreach (var blob in recent)
            {
                if (stopwatch.Elapsed > limit || collected.Count >= EditEffectIndex.MaxEffects)
                {
                    break;
                }

                if (OperationBlobWriter.IsUndone(blob.BlobPath))
                {
                    continue;
                }

                collected.AddRange(ReadEffects(blob.BlobPath, cutoff));
            }

            if (collected.Count == 0)
            {
                return 0;
            }

            var newest = collected.OrderByDescending(effect => effect.UtcTime).Take(EditEffectIndex.MaxEffects).ToList();
            index.Seed(newest);
            return newest.Count;
        }
        catch (Exception ex)
        {
            // Seeding is a convenience: a failure costs the pre-restart notes, never the load.
            Debug.WriteLine($"Edit effect seeding failed: {ex}");
            return 0;
        }
    }

    // The effects of one blob, stamped with the blob's change id and call id. A blob that is unreadable (mid-write by another window, malformed,
    // too large) or has no effects array yields nothing; a single effect that does not read (unknown kind, wrong shape) is skipped alone.
    private static List<EditEffect> ReadEffects(string blobPath, DateTime cutoff)
    {
        var effects = new List<EditEffect>();
        try
        {
            if (new FileInfo(blobPath).Length > MaxBlobBytes)
            {
                return effects;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(blobPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("effects", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return effects;
            }

            var changeId = StringOf(root, "changeId");
            var toolCallId = StringOf(root, "toolCallId");
            var toolName = StringOf(root, "toolName");
            foreach (var element in array.EnumerateArray())
            {
                var effect = ReadEffect(element);
                if (effect is null || effect.UtcTime < cutoff)
                {
                    continue;
                }

                effects.Add(effect with
                {
                    ChangeId = changeId ?? effect.ChangeId,
                    ToolCallId = effect.ToolCallId ?? toolCallId,
                    ToolName = string.IsNullOrEmpty(effect.ToolName) ? toolName ?? string.Empty : effect.ToolName,
                    ServerInstanceId = effect.ServerInstanceId ?? string.Empty,
                    Reverted = false,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Edit effect seeding skipped {blobPath}: {ex.Message}");
        }

        return effects;
    }

    private static EditEffect? ReadEffect(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var effect = JsonSerializer.Deserialize<EditEffect>(element.GetRawText(), EditEffect.JsonOptions);
            // The deserializer fills absent constructor parameters with defaults, so a record can come back with null names.
            return effect is null || string.IsNullOrEmpty(effect.SimpleName) || string.IsNullOrEmpty(effect.SymbolDisplay) ? null : effect;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

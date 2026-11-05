using System.Text.Json.Nodes;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// RenameSymbol wire projection of the residual-mention scan (owner ruling D2: targeted and small). The full list can
/// be hundreds of KB for a namespace rename, so the tool returns this summary and keeps the full rows behind per-bucket
/// GetLargeResult hints. Names are the JSON contract (camelCase on the wire).
/// </summary>
/// <param name="Total">Number of residual mentions (one per line).</param>
/// <param name="ByBucket">Bucket name (code, comment, string, pathLike, nonSource) to count; non-zero buckets only.</param>
/// <param name="TopFiles">Up to <see cref="ResidualMentionSummarizer.MaxTopFiles"/> files by mention count.</param>
/// <param name="Examples">Bucket name to up to <see cref="ResidualMentionSummarizer.MaxExamplesPerBucket"/> examples, in scan order.</param>
/// <param name="Details">Bucket name to a ready-to-run GetLargeResult call for that bucket's rows; null when the detail store failed.</param>
public sealed record ResidualMentionsView(
    int Total,
    IReadOnlyDictionary<string, int> ByBucket,
    IReadOnlyList<ResidualFileCount> TopFiles,
    IReadOnlyDictionary<string, IReadOnlyList<ResidualExample>> Examples,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>One example mention: repo-relative path, 1-based line and an excerpt of the line centred on the match.</summary>
public sealed record ResidualExample(string Path, int Line, string Excerpt);

/// <summary>A file and how many residual mentions it holds.</summary>
public sealed record ResidualFileCount(string Path, int Count);

/// <summary>
/// Pure builders for the RenameSymbol residual-mention summary and for the stored detail envelope. No IO.
/// </summary>
public static class ResidualMentionSummarizer
{
    public const int MaxTopFiles = 10;
    public const int MaxExamplesPerBucket = 5;
    public const int ExcerptChars = 100;
    public const int DetailTextChars = 300;

    // The detail envelope mirrors the wire envelope shape ({isError, successData}) so GetLargeResult's PageEnvelope
    // lists the bucket arrays as pageable paths (it looks under successData) and a path such as successData.comment
    // selects one bucket array.
    private const string DetailRoot = "successData";

    /// <summary>Wire name of a bucket: lowercase camelCase (code, comment, string, pathLike, nonSource).</summary>
    public static string BucketName(ResidualBucket bucket) => bucket switch
    {
        ResidualBucket.Code => "code",
        ResidualBucket.Comment => "comment",
        ResidualBucket.String => "string",
        ResidualBucket.PathLike => "pathLike",
        ResidualBucket.NonSource => "nonSource",
        _ => bucket.ToString().ToLowerInvariant(),
    };

    /// <summary>GetLargeResult <c>path</c> that selects one bucket's rows in the stored detail envelope, for example successData.comment.</summary>
    public static string DetailPath(ResidualBucket bucket) => DetailRoot + "." + BucketName(bucket);

    /// <summary>
    /// Builds the wire summary. <paramref name="detailResultId"/> is the id of the stored detail envelope
    /// (<see cref="ResidualMentionDetailStore"/>); when null (store failed or no solution root) <c>Details</c> is null.
    /// <paramref name="oldName"/> is used only to locate the match for an excerpt when a mention carries no column.
    /// </summary>
    public static ResidualMentionsView Summarize(IReadOnlyList<ResidualMention> mentions, string oldName, string? detailResultId)
    {
        ArgumentNullException.ThrowIfNull(mentions);

        var byBucket = new Dictionary<string, int>();
        var examples = new Dictionary<string, IReadOnlyList<ResidualExample>>();
        var details = string.IsNullOrEmpty(detailResultId) ? null : new Dictionary<string, string>();
        foreach (var bucket in Enum.GetValues<ResidualBucket>())
        {
            var inBucket = mentions.Where(m => m.Bucket == bucket).ToList();
            if (inBucket.Count == 0)
            {
                continue;
            }

            var name = BucketName(bucket);
            byBucket[name] = inBucket.Count;
            examples[name] = inBucket
                .Take(MaxExamplesPerBucket)
                .Select(m => new ResidualExample(PathOf(m), m.LineNumber, Excerpt(m, oldName)))
                .ToList();
            if (details is not null)
            {
                details[name] = LargeResultHints.FetchCall(detailResultId!, path: DetailPath(bucket));
            }
        }

        var topFiles = mentions
            .GroupBy(PathOf, StringComparer.Ordinal)
            .Select(g => new ResidualFileCount(g.Key, g.Count()))
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(MaxTopFiles)
            .ToList();

        return new ResidualMentionsView(mentions.Count, byBucket, topFiles, examples, details);
    }

    /// <summary>The one-sentence note that accompanies the summary.</summary>
    public static string Note(string oldName, int total, bool detailsStored) =>
        detailsStored
            ? $"'{oldName}' still appears in {total} place(s) Rename couldn't reach; residualMentions summarises them by bucket and the full rows per bucket are behind the residualMentions.details calls."
            : $"'{oldName}' still appears in {total} place(s) Rename couldn't reach; residualMentions summarises them by bucket and the full rows could not be stored.";

    /// <summary>
    /// About <paramref name="maxChars"/> characters of the mention's line centred on the match (ASCII "..." marks a cut
    /// side). A mention without a column (0) whose line does not start with <paramref name="oldName"/> is located by
    /// first occurrence of the name.
    /// </summary>
    public static string Excerpt(ResidualMention mention, string oldName, int maxChars = ExcerptChars)
    {
        var line = mention.LineText ?? string.Empty;
        if (line.Length <= maxChars)
        {
            return line;
        }

        var column = Math.Clamp(mention.MatchColumn, 0, line.Length - 1);
        if (column == 0 && oldName.Length > 0 && !line.StartsWith(oldName, StringComparison.Ordinal))
        {
            var found = line.IndexOf(oldName, StringComparison.Ordinal);
            if (found > 0)
            {
                column = found;
            }
        }

        var start = Math.Clamp(column + (oldName.Length / 2) - (maxChars / 2), 0, line.Length - maxChars);
        var excerpt = line.Substring(start, maxChars);
        return (start > 0 ? "..." : string.Empty) + excerpt + (start + maxChars < line.Length ? "..." : string.Empty);
    }

    /// <summary>
    /// JSON text of the detail envelope: <c>{"isError":false,"successData":{"code":[{path,line,text}],...}}</c> with one
    /// array per non-empty bucket (rows in scan order, text trimmed and capped at <see cref="DetailTextChars"/>).
    /// </summary>
    public static string BuildDetailEnvelope(IReadOnlyList<ResidualMention> mentions)
    {
        ArgumentNullException.ThrowIfNull(mentions);

        var data = new JsonObject();
        foreach (var bucket in Enum.GetValues<ResidualBucket>())
        {
            var rows = new JsonArray();
            foreach (var m in mentions.Where(m => m.Bucket == bucket))
            {
                rows.Add(new JsonObject
                {
                    ["path"] = PathOf(m),
                    ["line"] = m.LineNumber,
                    ["text"] = Cap(m.LineText),
                });
            }

            if (rows.Count > 0)
            {
                data[BucketName(bucket)] = rows;
            }
        }

        return new JsonObject { ["isError"] = false, [DetailRoot] = data }.ToJsonString();
    }

    private static string Cap(string? text)
    {
        var value = text ?? string.Empty;
        return value.Length <= DetailTextChars ? value : value[..(DetailTextChars - 3)] + "...";
    }

    private static string PathOf(ResidualMention mention) =>
        mention.FilePath.Relative.Length > 0 ? mention.FilePath.Relative : mention.FilePath.Absolute;
}

/// <summary>
/// Stores the full residual-mention rows once per RenameSymbol call as an Envelope large result, so GetLargeResult
/// honours <c>path</c> (Raw results ignore it).
/// </summary>
public static class ResidualMentionDetailStore
{
    /// <summary>
    /// Returns the resultId of the stored detail envelope, or null when there is nothing to store, no solution root,
    /// or the store failed. Never throws except on cancellation, and never reports internal paths.
    /// </summary>
    public static async Task<string?> StoreAsync(IReadOnlyList<ResidualMention> mentions, string? solutionRoot, CancellationToken cancellationToken)
    {
        if (mentions.Count == 0 || string.IsNullOrEmpty(solutionRoot))
        {
            return null;
        }

        try
        {
            var (offloaded, _, resultId) = await LargeResultHelper.StoreEnvelopeAsync(
                ResidualMentionSummarizer.BuildDetailEnvelope(mentions), solutionRoot, cancellationToken);
            return offloaded ? resultId : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The summary is still returned; the caller reports that details could not be stored.
            return null;
        }
    }
}

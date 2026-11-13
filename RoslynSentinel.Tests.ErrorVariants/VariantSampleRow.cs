using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// One replayed sample, written as a row of variants.csv beside the run's transcripts.
/// Property order is the column order: <see cref="Header"/> and <see cref="ToCsvLine"/> follow it.
/// </summary>
public sealed record VariantSampleRow
{
    /// <summary>Stamp shared by every sample of one replay run.</summary>
    public required string RunStamp { get; init; }

    /// <summary>Model key the replay ran against.</summary>
    public required string ModelKey { get; init; }

    /// <summary>Invariant numeric text, "default" for the server default, or "unknown".</summary>
    public required string Temperature { get; init; }

    /// <summary>Invariant numeric text, "default" for the server default, or "unknown".</summary>
    public required string TopP { get; init; }

    /// <summary>Max tokens requested for the sample.</summary>
    public required int MaxTokens { get; init; }

    /// <summary>Continuation turns allowed after the cut.</summary>
    public required int ContinueTurns { get; init; }

    /// <summary>Variant identifier (file stem, for example armF).</summary>
    public required string VariantId { get; init; }

    /// <summary>12-hex hash of the variant file text.</summary>
    public required string VariantHash { get; init; }

    /// <summary>Error code the variant replaces (for example FileNotFound).</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Folder of the source run whose transcript was cut.</summary>
    public required string SourceRunFolder { get; init; }

    /// <summary>Source sample name within that run.</summary>
    public required string SourceSample { get; init; }

    /// <summary>Commit hash the source run was recorded at.</summary>
    public required string SourceCommitHash { get; init; }

    /// <summary>Turn number at which the transcript was cut at the first error.</summary>
    public required int CutTurn { get; init; }

    /// <summary>Hash of the recorded message that the cut replaced.</summary>
    public required string RecordedMessageHash { get; init; }

    /// <summary>Index of this sample within the variant's sample set.</summary>
    public required int SampleIndex { get; init; }

    /// <summary>Decision class the replayed sample reached.</summary>
    public required string DecisionClass { get; init; }

    /// <summary>First tool called after the cut; empty when none.</summary>
    public required string FirstTool { get; init; }

    /// <summary>Number of tool calls made by the replayed sample.</summary>
    public required int CallCount { get; init; }

    /// <summary>True when a blocked call occurred anywhere in the sample.</summary>
    public required bool BlockedAnywhere { get; init; }

    /// <summary>Turn at which the sample stopped; empty cell when null.</summary>
    public int? StopTurn { get; init; }

    /// <summary>Stop reason of the replayed sample.</summary>
    public required string StopReason { get; init; }

    /// <summary>Wall-clock latency of the sample in milliseconds.</summary>
    public required long LatencyMs { get; init; }

    /// <summary>Input tokens reported by the server; empty cell when null.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Output tokens reported by the server; empty cell when null.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Harness fault text; empty when none. May span lines in the source, flattened on write.</summary>
    public required string HarnessError { get; init; }

    /// <summary>
    /// How the source transcript's recorded model key, temperature and top-p compare with this run's:
    /// "matched", "unknown" (the header records none: transcripts written before the fields existed) or "mismatch" (the sample was refused).
    /// Last column so earlier cell positions did not move.
    /// </summary>
    public required string SourceSettings { get; init; }

    private static readonly string[] ColumnNames =
    {
        nameof(RunStamp), nameof(ModelKey), nameof(Temperature), nameof(TopP), nameof(MaxTokens), nameof(ContinueTurns),
        nameof(VariantId), nameof(VariantHash), nameof(ErrorCode),
        nameof(SourceRunFolder), nameof(SourceSample), nameof(SourceCommitHash), nameof(CutTurn), nameof(RecordedMessageHash),
        nameof(SampleIndex), nameof(DecisionClass), nameof(FirstTool), nameof(CallCount), nameof(BlockedAnywhere), nameof(StopTurn), nameof(StopReason),
        nameof(LatencyMs), nameof(InputTokens), nameof(OutputTokens), nameof(HarnessError), nameof(SourceSettings),
    };

    private static readonly Regex LineBreaks = new("[\\r\\n]+");

    private static readonly char[] QuoteTriggers = { ',', '"', '\r', '\n' };

    /// <summary>Comma-joined column names in property order.</summary>
    public static readonly string Header = string.Join(",", ColumnNames);

    /// <summary>
    /// Formats the row as one CSV line (no line terminator). Line breaks inside string cells
    /// (HarnessError may carry multi-line exception text) are flattened to a single space first,
    /// because the file is line-oriented. Numbers and bools use the invariant culture.
    /// </summary>
    public string ToCsvLine()
    {
        var cells = new[]
        {
            Flatten(RunStamp), Flatten(ModelKey), Flatten(Temperature), Flatten(TopP),
            MaxTokens.ToString(CultureInfo.InvariantCulture), ContinueTurns.ToString(CultureInfo.InvariantCulture),
            Flatten(VariantId), Flatten(VariantHash), Flatten(ErrorCode),
            Flatten(SourceRunFolder), Flatten(SourceSample), Flatten(SourceCommitHash),
            CutTurn.ToString(CultureInfo.InvariantCulture), Flatten(RecordedMessageHash),
            SampleIndex.ToString(CultureInfo.InvariantCulture), Flatten(DecisionClass), Flatten(FirstTool),
            CallCount.ToString(CultureInfo.InvariantCulture), BlockedAnywhere ? "true" : "false",
            StopTurn?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, Flatten(StopReason),
            LatencyMs.ToString(CultureInfo.InvariantCulture),
            InputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Flatten(HarnessError),
            Flatten(SourceSettings),
        };

        return string.Join(",", cells.Select(Quote));
    }

    /// <summary>
    /// Inverse of <see cref="ToCsvLine"/>. Throws <see cref="FormatException"/> naming the column and the
    /// cell count when the row does not have the expected cell count or a numeric or bool cell does not parse.
    /// </summary>
    public static VariantSampleRow Parse(string line)
    {
        var cells = SplitCells(line);
        if (cells.Count != ColumnNames.Length)
        {
            throw new FormatException($"variants.csv row has {cells.Count} cells; expected {ColumnNames.Length}.");
        }

        return new VariantSampleRow
        {
            RunStamp = cells[0],
            ModelKey = cells[1],
            Temperature = cells[2],
            TopP = cells[3],
            MaxTokens = ReadInt(cells, 4),
            ContinueTurns = ReadInt(cells, 5),
            VariantId = cells[6],
            VariantHash = cells[7],
            ErrorCode = cells[8],
            SourceRunFolder = cells[9],
            SourceSample = cells[10],
            SourceCommitHash = cells[11],
            CutTurn = ReadInt(cells, 12),
            RecordedMessageHash = cells[13],
            SampleIndex = ReadInt(cells, 14),
            DecisionClass = cells[15],
            FirstTool = cells[16],
            CallCount = ReadInt(cells, 17),
            BlockedAnywhere = ReadBool(cells, 18),
            StopTurn = ReadNullableInt(cells, 19),
            StopReason = cells[20],
            LatencyMs = ReadLong(cells, 21),
            InputTokens = ReadNullableLong(cells, 22),
            OutputTokens = ReadNullableLong(cells, 23),
            HarnessError = cells[24],
            SourceSettings = cells[25],
        };
    }

    /// <summary>
    /// Appends the row to the CSV file, creating the directory when missing and writing the
    /// <see cref="Header"/> line first when the file does not exist or is empty. UTF-8 without BOM.
    /// </summary>
    public static void Append(string csvPath, VariantSampleRow row)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(csvPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var needsHeader = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
        var text = (needsHeader ? Header + Environment.NewLine : string.Empty)
            + row.ToCsvLine() + Environment.NewLine;
        File.AppendAllText(csvPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Flatten(string value) => LineBreaks.Replace(value, " ");

    private static string Quote(string cell) =>
        cell.IndexOfAny(QuoteTriggers) < 0 ? cell : "\"" + cell.Replace("\"", "\"\"") + "\"";

    private static List<string> SplitCells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        if (inQuotes)
        {
            throw new FormatException($"variants.csv row ends inside a quoted cell; it has {cells.Count + 1} cells so far.");
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static int ReadInt(IReadOnlyList<string> cells, int index) =>
        int.TryParse(cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw BadCell(cells, index, "an integer");

    private static int? ReadNullableInt(IReadOnlyList<string> cells, int index)
    {
        if (cells[index].Length == 0)
        {
            return null;
        }

        return ReadInt(cells, index);
    }

    private static long ReadLong(IReadOnlyList<string> cells, int index) =>
        long.TryParse(cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw BadCell(cells, index, "an integer");

    private static long? ReadNullableLong(IReadOnlyList<string> cells, int index)
    {
        if (cells[index].Length == 0)
        {
            return null;
        }

        return ReadLong(cells, index);
    }

    private static bool ReadBool(IReadOnlyList<string> cells, int index) =>
        bool.TryParse(cells[index], out var value)
            ? value
            : throw BadCell(cells, index, "a bool");

    private static FormatException BadCell(IReadOnlyList<string> cells, int index, string expected) =>
        new($"variants.csv column '{ColumnNames[index]}' (index {index}) is not {expected}: '{cells[index]}' (row has {cells.Count} cells).");
}

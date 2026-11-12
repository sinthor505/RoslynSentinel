using System.Globalization;
using System.IO;
using System.Text;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// One model-eval sample, written as one results CSV line. Score is null exactly when InconclusiveReason is non-empty.
/// </summary>
/// <param name="ScenarioId">Scenario name from the plan.</param>
/// <param name="ScenarioType">1 cold-start clean, 2 resume clean, 3 cold-start faulted, 4 resume faulted.</param>
/// <param name="FaultKind">Injected fault kind; "" when none.</param>
/// <param name="SampleIndex">Sample number within its scenario.</param>
/// <param name="Commit">Working-tree HEAD stamped on the run; "" when unstamped.</param>
/// <param name="Dirty">Dirty flag stamped on the run; null when unknown.</param>
/// <param name="LiftRenames">State of ROSLYNSENTINEL_LIFT_RENAMES: "on", "off" or "unset".</param>
/// <param name="Score">Scored outcome of the run; null for an Inconclusive sample.</param>
/// <param name="SchemaDrift">True when the tool schema drifted from the baseline.</param>
/// <param name="ResultDrift">Result-drift count for the sample.</param>
/// <param name="InconclusiveReason">Why the sample did not run; "" when it ran.</param>
/// <param name="TranscriptPath">Path of the saved transcript for the sample.</param>
/// <param name="LiveTurnsToTarget">Live turns the model took (see ScenarioDefinition.LiveTurnsFor); null for an inconclusive sample or an old CSV row without the column.</param>
public sealed record ScenarioSample(
    string ScenarioId,
    int ScenarioType,
    string FaultKind,
    int SampleIndex,
    string Commit,
    bool? Dirty,
    string LiftRenames,
    ScoreResult? Score,
    bool SchemaDrift,
    int ResultDrift,
    string InconclusiveReason,
    string TranscriptPath,
    int? LiveTurnsToTarget = null)
{
    /// <summary>True when the sample did not run; see InconclusiveReason.</summary>
    public bool IsInconclusive => InconclusiveReason.Length > 0;
}

/// <summary>CSV reader and writer for <see cref="ScenarioSample"/> rows. One writer per run.</summary>
public static class ScenarioSampleCsv
{
    /// <summary>Column names, in the order ToCsvLine writes them.</summary>
    public const string Header =
        "scenarioId,scenarioType,faultKind,sampleIndex,commit,dirty,liftRenames,firstCallSucceeded,firstErrorCode,firstErrorClass,recovered,callsToRecover,recoveryUsedExpectedTool,markerPresent,turnCount,stopReason,failureClass,passed,finalStateVerdict,turnCapHit,callsAfterFirstError,turnsAfterFirstError,outputCharsAfterFirstError,maxIdenticalCallRepeat,identicalReasoningBlocks,retryPhraseCount,schemaDrift,resultDrift,inconclusiveReason,transcriptPath,liveTurnsToTarget";

    /// <summary>Column count of rows written before liveTurnsToTarget existed; Parse still accepts them.</summary>
    public const int LegacyColumnCount = 30;

    /// <summary>One CSV line (no line terminator). Score columns are all empty when Score is null.</summary>
    public static string ToCsvLine(ScenarioSample sample)
    {
        var s = sample.Score;
        return string.Join(
            ',',
            Csv(sample.ScenarioId),
            Int(sample.ScenarioType),
            Csv(sample.FaultKind),
            Int(sample.SampleIndex),
            Csv(sample.Commit),
            NullableBool(sample.Dirty),
            Csv(sample.LiftRenames),
            ScoreField(s, r => NullableBool(r.FirstCallSucceeded)),
            ScoreField(s, r => Csv(r.FirstErrorCode)),
            ScoreField(s, r => EnumName(r.FirstErrorClass)),
            ScoreField(s, r => Bool(r.Recovered)),
            ScoreField(s, r => NullableInt(r.CallsToRecover)),
            ScoreField(s, r => NullableBool(r.RecoveryUsedExpectedTool)),
            ScoreField(s, r => Bool(r.MarkerPresent)),
            ScoreField(s, r => Int(r.TurnCount)),
            ScoreField(s, r => EnumName(r.StopReason)),
            ScoreField(s, r => r.FailureClass.ToString()),
            ScoreField(s, r => NullableBool(r.Passed)),
            ScoreField(s, r => EnumName(r.Verdict)),
            ScoreField(s, r => Bool(r.TurnCapHit)),
            ScoreField(s, r => Int(r.CallsAfterFirstError)),
            ScoreField(s, r => Int(r.TurnsAfterFirstError)),
            ScoreField(s, r => Int(r.OutputCharsAfterFirstError)),
            ScoreField(s, r => Int(r.MaxIdenticalCallRepeat)),
            ScoreField(s, r => Int(r.IdenticalReasoningBlocks)),
            ScoreField(s, r => Int(r.RetryPhraseCount)),
            Bool(sample.SchemaDrift),
            Int(sample.ResultDrift),
            Csv(sample.InconclusiveReason),
            Csv(sample.TranscriptPath),
            NullableInt(sample.LiveTurnsToTarget));
    }

    /// <summary>
    /// Parses one CSV line written by ToCsvLine. Accepts Header's column count or LegacyColumnCount (rows written before
    /// liveTurnsToTarget existed; LiveTurnsToTarget is null for those). Throws FormatException for any other count.
    /// </summary>
    public static ScenarioSample Parse(string csvLine)
    {
        var f = SplitFields(csvLine);
        var expected = Header.Split(',').Length;
        if (f.Count != expected && f.Count != LegacyColumnCount)
        {
            throw new FormatException($"Expected {LegacyColumnCount} or {expected} CSV columns, found {f.Count}.");
        }

        ScoreResult? score = f[14].Length == 0 ? null : new ScoreResult(
            ParseNullableBool(f[7]),
            f[8].Length == 0 ? null : f[8],
            ParseNullableEnum<ToolErrorClass>(f[9]),
            ParseBool(f[10]),
            ParseNullableInt(f[11]),
            ParseNullableBool(f[12]),
            ParseBool(f[13]),
            ParseInt(f[14]),
            ParseNullableEnum<AgentStopReason>(f[15]),
            ParseEnum<FailureClass>(f[16]),
            ParseNullableBool(f[17]),
            ParseInt(f[20]),
            ParseInt(f[21]),
            ParseInt(f[22]),
            ParseInt(f[23]),
            ParseInt(f[24]),
            ParseInt(f[25]),
            Verdict: ParseNullableEnum<FinalStateVerdict>(f[18]),
            TurnCapHit: ParseBool(f[19]));

        return new ScenarioSample(
            f[0],
            ParseInt(f[1]),
            f[2],
            ParseInt(f[3]),
            f[4],
            ParseNullableBool(f[5]),
            f[6],
            score,
            ParseBool(f[26]),
            ParseInt(f[27]),
            f[28],
            f[29],
            f.Count == expected ? ParseNullableInt(f[30]) : null);
    }

    /// <summary>Appends one row, writing Header first when the file is missing or empty. Not thread-safe: one writer per run.</summary>
    public static void Append(string csvPath, ScenarioSample sample)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(csvPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var needsHeader = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
        using var stream = new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (needsHeader)
        {
            writer.Write(Header + Environment.NewLine);
        }

        writer.Write(ToCsvLine(sample) + Environment.NewLine);
    }

    private static string ScoreField(ScoreResult? score, Func<ScoreResult, string> field) =>
        score is null ? string.Empty : field(score);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string NullableBool(bool? value) => value is bool b ? Bool(b) : string.Empty;

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string NullableInt(int? value) => value is int i ? Int(i) : string.Empty;

    private static string EnumName<T>(T? value) where T : struct, Enum =>
        value.HasValue ? value.Value.ToString() : string.Empty;

    // Same rule as ResumeSampleRow.Csv in TranscriptResume.cs: quote when the value holds a comma, quote, CR or LF.
    private static string Csv(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool ParseBool(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException($"Expected true or false, found \"{value}\"."),
    };

    private static bool? ParseNullableBool(string value) => value.Length == 0 ? null : ParseBool(value);

    private static int ParseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static int? ParseNullableInt(string value) => value.Length == 0 ? null : ParseInt(value);

    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse(value, out T parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FormatException($"Unknown {typeof(T).Name} \"{value}\".");

    private static T? ParseNullableEnum<T>(string value) where T : struct, Enum =>
        value.Length == 0 ? null : ParseEnum<T>(value);

    // Quote-aware split of one line: doubled quotes inside a quoted field become one quote.
    internal static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}

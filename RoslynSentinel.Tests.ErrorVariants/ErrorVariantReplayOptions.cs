using System.Globalization;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// What one error-variant replay run covers: which source transcripts, which variants, how many samples per
/// (source, variant) cell and how many live turns after the cut. Read from VARIANT_* environment variables by
/// <see cref="FromEnvironment"/>; the model host, model key and sampling come from <see cref="RoslynSentinel.Common.LlmOptions"/>.
/// </summary>
public sealed record ErrorVariantReplayOptions
{
    /// <summary>Source run folders (their sample-NN/transcript.json children), sample folders or transcript.json paths. Separator in the variable: ';'.</summary>
    public const string SourcesVariable = "VARIANT_SOURCES";

    /// <summary>Variants as "&lt;errorCode&gt;/&lt;id&gt;", "&lt;errorCode&gt;/*" (every variant of the folder) or a bare id that exists in exactly one errorCode folder. Separators: ',' or ';'.</summary>
    public const string FilesVariable = "VARIANT_FILES";

    /// <summary>Samples per (source, variant) cell; default 3.</summary>
    public const string SamplesVariable = "VARIANT_SAMPLES";

    /// <summary>Live turns each sample runs after the cut; default 1 (one step).</summary>
    public const string ContinueTurnsVariable = "VARIANT_CONTINUE_TURNS";

    /// <summary>Optional cut turn: 1 or more is the 1-based seeded cut; 0 means regenerate (no seed, every turn live); absent means seeded at each transcript's first turn with an erroring call.</summary>
    public const string CutTurnVariable = "VARIANT_CUT_TURN";

    /// <summary>Optional comma list of target names the task says are absent (feeds the StopProse check).</summary>
    public const string AbsentTargetsVariable = "VARIANT_ABSENT_TARGETS";

    /// <summary>The <see cref="CutTurn"/> value that selects regenerate mode.</summary>
    public const int RegenerateCutTurn = 0;

    /// <summary>Source specs as given; see <see cref="SourcesVariable"/>.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Variant specs as given; see <see cref="FilesVariable"/>.</summary>
    public required IReadOnlyList<string> Variants { get; init; }

    /// <summary>Samples per (source, variant) cell; at least 1.</summary>
    public int Samples { get; init; } = 3;

    /// <summary>Live turns after the cut; at least 1.</summary>
    public int ContinueTurns { get; init; } = 1;

    /// <summary>Fixed cut turn for every source; 0 selects regenerate mode (<see cref="RegenerateCutTurn"/>); null picks the first erroring turn of each (seeded).</summary>
    public int? CutTurn { get; init; }

    /// <summary>Target names the task names as absent; empty disables the StopProse check.</summary>
    public IReadOnlyList<string> AbsentTargets { get; init; } = [];

    /// <summary>Folder holding the &lt;errorCode&gt; variant folders; null means the ErrorVariants folder copied beside the test assembly.</summary>
    public string? VariantsRoot { get; init; }

    /// <summary>
    /// Reads the VARIANT_* variables through <paramref name="getVariable"/> (null reads the process environment).
    /// </summary>
    /// <exception cref="InvalidOperationException">A required variable is missing or a number is not a valid integer in range; the message names the variable.</exception>
    public static ErrorVariantReplayOptions FromEnvironment(Func<string, string?>? getVariable = null)
    {
        getVariable ??= Environment.GetEnvironmentVariable;

        var sources = Split(getVariable(SourcesVariable), ';');
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SourcesVariable} is not set: give run folders or transcript.json paths separated by ';'.");
        }

        var variants = Split(getVariable(FilesVariable), ',', ';');
        if (variants.Count == 0)
        {
            throw new InvalidOperationException(
                $"{FilesVariable} is not set: give variant ids such as FileNotFound/armE, or FileNotFound/* for a whole errorCode folder.");
        }

        return new ErrorVariantReplayOptions
        {
            Sources = sources,
            Variants = variants,
            Samples = ReadInt(getVariable, SamplesVariable) ?? 3,
            ContinueTurns = ReadInt(getVariable, ContinueTurnsVariable) ?? 1,
            CutTurn = ReadInt(getVariable, CutTurnVariable, minimum: RegenerateCutTurn),
            AbsentTargets = Split(getVariable(AbsentTargetsVariable), ',', ';'),
        };
    }

    private static List<string> Split(string? raw, params char[] separators) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // Returns null for an unset or blank variable; a set variable must be an integer of at least minimum.
    private static int? ReadInt(Func<string, string?> getVariable, string name, int minimum = 1)
    {
        var raw = getVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum)
        {
            throw new InvalidOperationException($"{name}='{raw}' is not an integer of at least {minimum}.");
        }

        return value;
    }
}

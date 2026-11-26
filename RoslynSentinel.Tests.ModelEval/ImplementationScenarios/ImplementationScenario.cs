using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>Result classes of one implementation-scenario step run; mirrors ShippingKindStepOutcome.</summary>
internal enum ImplementationScenarioOutcome
{
    Pass,
    OutOfScopeEdit,
    CheckFailed,
    BuildBroken,
    Blocked,

    /// <summary>The tree passed the step check but the run stopped on the turn cap or wall-clock cap.</summary>
    PassedAfterCap,
    NotConverged
}

/// <summary>
/// One step of a scenario. <paramref name="GoldenFiles"/> is the overlay this step's golden commit writes on top of the previous
/// step (relative path with '/', file text; write-only, a golden never deletes). <paramref name="AllowedPaths"/> are the relative
/// paths the model may change in this step; an entry ending in '/' allows every path below that folder (deviation from the
/// plan, which only names exact paths: a model-written test file has no fixed name). <paramref name="HiddenTestSource"/> is an
/// xUnit file injected for grading (null = none). <paramref name="ExtraCheck"/> returns a failure message or null.
/// <paramref name="CannedFinalMessage"/> is the final message the golden self-test supplies (read-only steps).
/// <paramref name="ExpectedEnding"/> is how a correct run ends (DONE: or BLOCKED:); <paramref name="ExpectedRedTests"/> are test names
/// a correct BLOCKED end state may leave failing; <paramref name="Variants"/> are the named end states of the executable outcome
/// table; <paramref name="VariantProbe"/> returns extra variant detail or null; <paramref name="DoneWhenTests"/> are the test class
/// names as written in the step's Done-when sentence (read by the catalog; null = exempt from rule 9);
/// <paramref name="PromptVariants"/> are alternative prompts by name, picked for a live run by the prompt-variant setting
/// (<see cref="TestHarnessSettings.ModelEvalPromptVariantSetting"/>); the seed, checks and golden stay those of the step (null = none).
/// </summary>
internal sealed record ImplementationScenarioStep(
    string Prompt,
    IReadOnlyDictionary<string, string> GoldenFiles,
    IReadOnlyList<string> AllowedPaths,
    string? HiddenTestSource,
    Func<ImplementationScenarioCheckContext, Task<string?>>? ExtraCheck,
    string? CannedFinalMessage,
    ExpectedEnding ExpectedEnding = ExpectedEnding.Done,
    IReadOnlyList<string>? ExpectedRedTests = null,
    IReadOnlyList<ScenarioVariant>? Variants = null,
    Func<ImplementationScenarioCheckContext, string?>? VariantProbe = null,
    IReadOnlyList<string>? DoneWhenTests = null,
    IReadOnlyDictionary<string, string>? PromptVariants = null)
{
    /// <summary>
    /// The prompt for a live run: <see cref="Prompt"/> when <paramref name="variantName"/> is null or blank, otherwise the named entry
    /// of <see cref="PromptVariants"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name is not a variant of this step; the message names the setting, the value and the known variants.</exception>
    public string PromptFor(string? variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName))
        {
            return Prompt;
        }

        var name = variantName.Trim();
        if (PromptVariants is not null && PromptVariants.TryGetValue(name, out var text))
        {
            return text;
        }

        var known = PromptVariants is null || PromptVariants.Count == 0 ? "none" : string.Join(", ", PromptVariants.Keys);
        throw new InvalidOperationException(
            $"{TestHarnessSettings.ModelEvalPromptVariantSetting.EnvName} is '{name}', but this step has no prompt variant with that name; known variants: {known}.");
    }
}

/// <summary>
/// A data-driven implementation scenario (see docs/current/plans/closed/plan_modeleval_implementation_scenarios.md).
/// <paramref name="SeedFiles"/> is the step-0 overlay on the ContosoOrders sample. A parked scenario carries
/// <paramref name="ParkedReason"/> and its test cases are reported Ignored.
/// </summary>
/// <param name="OfferFullToolSchema">Mechanism only: true builds the sample's world with the full tool schema (validateOnApply offered); no scenario sets it.</param>
/// <param name="PrimaryAxis">The one declared axis; the NUnit category Axis:&lt;Name&gt; and the catalog section are generated from it. The free-text <paramref name="Axis"/> stays as a description.</param>
internal sealed record ImplementationScenario(
    string Id,
    string Title,
    string Axis,
    string Source,
    IReadOnlyDictionary<string, string> SeedFiles,
    IReadOnlyList<ImplementationScenarioStep> Steps,
    int TurnCap = 25,
    string PreferredHost = "113",
    string? ParkedReason = null,
    bool OfferFullToolSchema = false,
    EvalAxis PrimaryAxis = EvalAxis.Unassigned)
{
    /// <summary>Context budget in characters: seed text (visible acceptance tests excluded) + the longest step prompt (~6k tokens at 24,000).</summary>
    // Visible acceptance tests (seed files under ContosoOrders.Tests/ that no step lets the model edit) are not counted: the model runs them with RunTest instead of reading them. This is on purpose. Watch run transcripts for context-limit failures; if the model starts reading them, count them again.
    public const int ContextBudgetCharacters = 24000;

    public static ImplementationScenario Placeholder(string id, string reason)
    {
        return new ImplementationScenario(
            id,
            id,
            "placeholder",
            "docs/current/plans/closed/plan_modeleval_implementation_scenarios.md",
            new Dictionary<string, string>(),
            Array.Empty<ImplementationScenarioStep>(),
            ParkedReason: reason);
    }

    /// <summary>0 = seed, k = Steps[k-1].GoldenFiles; text normalised to CRLF (decision: done here once instead of in every scenario file).</summary>
    public IReadOnlyDictionary<string, string> FilesForStep(int step)
    {
        if (step < 0 || step > Steps.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Scenario {Id} has steps 0 (seed) to {Steps.Count}.");
        }

        var files = step == 0 ? SeedFiles : Steps[step - 1].GoldenFiles;
        return files.ToDictionary(entry => entry.Key, entry => NormalizeToCrlf(entry.Value));
    }

    /// <summary>
    /// Seed text characters plus the longest step prompt, compared with <see cref="ContextBudgetCharacters"/>. Visible acceptance
    /// tests (seed files under ContosoOrders.Tests/ that no step's AllowedPaths lets the model edit) are not counted: the model runs
    /// them instead of reading them.
    /// </summary>
    public int ContextCharacters()
    {
        var longestPrompt = Steps.Count == 0 ? 0 : Steps.Max(step => step.Prompt.Length);
        return SeedFiles.Where(entry => !IsVisibleAcceptanceTest(entry.Key)).Sum(entry => entry.Value.Length) + longestPrompt;
    }

    // A visible acceptance test: under ContosoOrders.Tests/ and matched by no step's AllowedPaths (an entry equals the key, or ends with '/' and is a prefix of it).
    private bool IsVisibleAcceptanceTest(string seedPath) =>
        seedPath.StartsWith("ContosoOrders.Tests/", StringComparison.Ordinal)
        && !Steps.Any(step => step.AllowedPaths.Any(allowed =>
            allowed == seedPath || (allowed.EndsWith('/') && seedPath.StartsWith(allowed, StringComparison.Ordinal))));

    internal static string NormalizeToCrlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}

/// <summary>What a step's extra check sees: the solution directory after the run plus helpers for common assertions.</summary>
internal sealed record ImplementationScenarioCheckContext(
    string SolutionDirectory,
    string? FinalMessage,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyDictionary<string, string> SeedFiles,
    CancellationToken CancellationToken,
    DotnetTestResult? TestRun = null)
{
    // File.WriteAllText of a string that starts with U+FEFF writes a BOM; this encoding reproduces exactly those bytes.
    private static readonly UTF8Encoding Utf8NoBomEncoding = new(false);

    public string FullPath(string relativePath) =>
        Path.Combine(SolutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>File text; a UTF-8 BOM is consumed (use <see cref="ReadBytes"/> for byte-level checks).</summary>
    public string ReadText(string relativePath) => File.ReadAllText(FullPath(relativePath));

    public byte[] ReadBytes(string relativePath) => File.ReadAllBytes(FullPath(relativePath));

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));

    /// <summary>True when the file exists and its bytes equal the CRLF-normalised seed text.</summary>
    public bool IsUnchanged(string relativePath)
    {
        var key = relativePath.Replace('\\', '/');
        if (!SeedFiles.TryGetValue(key, out var seed))
        {
            throw new ArgumentException($"'{relativePath}' is not a seed file of this scenario.", nameof(relativePath));
        }

        if (!Exists(relativePath))
        {
            return false;
        }

        var expected = Utf8NoBomEncoding.GetBytes(ImplementationScenario.NormalizeToCrlf(seed));
        return ReadBytes(relativePath).AsSpan().SequenceEqual(expected);
    }

    public SyntaxTree Syntax(string relativePath) => CSharpSyntaxTree.ParseText(ReadText(relativePath));

    // The per-folder cache lives in a side table keyed by this instance, not in a record field: a private instance field would join the
    // synthesized Equals/GetHashCode (two equal contexts would compare unequal) and a with-expression would share the cache.
    private static readonly ConditionalWeakTable<ImplementationScenarioCheckContext, Dictionary<string, ScenarioCompilation>> CompilationCaches = new();

    /// <summary>The compilation over the scenario folder (solution-relative, for example ContosoOrders.Core/Evals/X); built once per folder and context.</summary>
    public ScenarioCompilation Compile(string relativeFolder)
    {
        var cache = CompilationCaches.GetValue(this, _ => new Dictionary<string, ScenarioCompilation>(StringComparer.OrdinalIgnoreCase));
        var key = relativeFolder.Replace('\\', '/');
        if (!cache.TryGetValue(key, out var compilation))
        {
            compilation = ScenarioCompilation.Create(SolutionDirectory, key);
            cache[key] = compilation;
        }

        return compilation;
    }

    /// <summary>True when the final message exists and contains every fragment (ordinal, case-insensitive).</summary>
    public bool FinalMessageMentions(params string[] fragments)
    {
        if (string.IsNullOrWhiteSpace(FinalMessage))
        {
            return false;
        }

        return fragments.All(fragment => FinalMessage.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}

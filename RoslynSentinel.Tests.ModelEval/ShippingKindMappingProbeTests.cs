using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// The cheapest look at the "Overnight" misspelling: no tools, no server, no edit. The prompt carries the three files of the
/// ShippingKind step 4 start state (ShippingKinds.cs, ShippingFeeCalculator.cs, ShipmentValidator.cs) and asks for the
/// literal-to-constant mapping as text. Each sample is one model turn, so the test measures only whether the model writes the
/// constant names correctly when it copies them from text it can see: generational roulette, repeated _RESUME_SAMPLES times
/// (-Samples on roslynsentinel-modeleval.ps1). One results.csv row per sample (names found, names that are not members, whether
/// all four constants appear) plus a per-sample transcript.json that scripts/Get-ShippingKindTypoCounts.ps1 -Turn 1 also reads.
/// It asserts nothing about the model. The system prompt is deliberately minimal, so the result is not comparable to a step 4 run's
/// own conversation (use <see cref="ShippingKindResumeAgentTests"/> for that).
/// </summary>
[TestFixture]
public class ShippingKindMappingProbeTests
{
    private const string CsvHeader = "sampleIndex,seconds,allFourConstantsPresent,unknownNames,foundNames,responseChars,stopReason";

    // Same declaration shape as the typo script: const string X = / static T X(
    private static readonly Regex DeclarationRegex = new(
        @"(?:const\s+string\s+|static\s+[\w<>\?\[\]]+\s+)([A-Za-z_]\w*)\s*(?:=|\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ConstantRegex = new(
        @"const\s+string\s+([A-Za-z_]\w*)\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemberUseRegex = new(
        @"ShippingKinds\.([A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] FilePaths =
    [
        "ContosoOrders.Core/Shipping/ShippingKinds.cs",
        "ContosoOrders.Core/Shipping/ShippingFeeCalculator.cs",
        "ContosoOrders.Core/Shipping/ShipmentValidator.cs",
    ];

    [Test]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1 -Test ShippingKindMappingProbe -Samples <n>")]
    public async Task ShippingKindMappingProbe_SamplesTheMapping()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        if (!GitTestRepo.IsGitAvailable())
        {
            Assert.Ignore("git is not available - the probe builds the step 4 start state from the golden commits.");
        }

        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var samples = ResumeSettings.FromEnvironment().Samples;

        // Step 4 starts from the golden commit of step 3. Only the files are needed, so there is no solution load.
        using var fixture = new RoslynSentinel.Tests.TestSolutionFixture();
        var repository = GoldenStepRepository.Create(
            fixture.SolutionDirectory, ShippingKindStepGoldens.LastStep, s => ShippingKindStepGoldens.FilesForStep(s, false));
        repository.CheckoutStep(3, "probe/step-4");
        var files = FilePaths.ToDictionary(
            path => path,
            path => File.ReadAllText(Path.Combine(fixture.SolutionDirectory, path.Replace('/', Path.DirectorySeparatorChar))));

        var constants = ConstantRegex.Matches(files[FilePaths[0]]).Select(m => m.Groups[1].Value).ToList();
        var declared = DeclarationRegex.Matches(files[FilePaths[0]]).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var userPrompt = BuildUserPrompt(files);

        var runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "results.csv");
        File.WriteAllText(csvPath, CsvHeader + "\r\n");

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddLmStudioAgentClient(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(runDirectory, "agent.log")));
        using var host = hostBuilder.Build();
        var agentClient = host.Services.GetRequiredService<LmStudioAgentClient>();

        TestContext.Out.WriteLine($"Probing {samples} sample(s); constants declared: {string.Join(", ", constants)}.");
        var withUnknown = 0;
        try
        {
            for (var sampleIndex = 1; sampleIndex <= samples; sampleIndex++)
            {
                var sampleDirectory = Path.Combine(runDirectory, $"sample-{sampleIndex:D2}");
                var stopwatch = Stopwatch.StartNew();
                var runner = new ModelAgentRunner(
                    agentClient, new NoToolHost(),
                    repeatedFailureLimit: 10,
                    maxTokensPerTurn: 16384,
                    turnCap: 1,
                    wallClockCap: TimeSpan.FromMinutes(10),
                    logger: host.Services.GetRequiredService<ILogger<ModelAgentRunner>>(),
                    loopAssist: LoopAssistOptions.Off);
                var result = await runner.RunAsync(
                    "You are a careful C# assistant.", userPrompt, sampleDirectory, cancellationToken);
                stopwatch.Stop();

                var response = result.Transcript.FinalMessage() ?? string.Empty;
                var found = MemberUseRegex.Matches(response).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
                var unknown = found.Where(n => !declared.Contains(n)).ToList();
                var allFour = constants.All(c => found.Contains(c, StringComparer.Ordinal));
                if (unknown.Count > 0)
                {
                    withUnknown++;
                }

                var line = string.Join(
                    ',',
                    sampleIndex,
                    stopwatch.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                    allFour ? "true" : "false",
                    string.Join(';', unknown),
                    string.Join(';', found),
                    response.Length,
                    result.StopReason);
                File.AppendAllText(csvPath, line + "\r\n");
                TestContext.Out.WriteLine($"Sample {sampleIndex}: {line}");
            }
        }
        finally
        {
            TestContext.Out.WriteLine($"{withUnknown} of {samples} sample(s) named a ShippingKinds member that does not exist.");
            ModelTestingResultsArchiver.ArchiveRunDirectory(runDirectory);
        }
    }

    private static string BuildUserPrompt(IReadOnlyDictionary<string, string> files)
    {
        var builder = new StringBuilder();
        builder.AppendLine("These three C# files are in ContosoOrders.Core/Shipping.");
        foreach (var (path, content) in files)
        {
            builder.AppendLine().AppendLine(path).AppendLine("```csharp").AppendLine(content.TrimEnd()).AppendLine("```");
        }

        builder.AppendLine();
        builder.AppendLine(
            "ShippingFeeCalculator.cs and ShipmentValidator.cs compare shipping kinds against string literals. " +
            "List each distinct string literal that is a shipping kind, one per line, in the form: \"literal\" -> ShippingKinds.<constant name>. " +
            "Use the constant names exactly as ShippingKinds.cs declares them. Reply with only these lines.");
        return builder.ToString();
    }

    /// <summary>A tool host that offers no tools: the probe is a single text turn.</summary>
    private sealed class NoToolHost : IAgentToolHost
    {
        public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentToolDefinition>>([]);

        public Task<CallToolResult> CallToolAsync(
            string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"The mapping probe offers no tools, so '{toolName}' cannot be called.");
    }
}

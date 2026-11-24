using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

/// <summary>
/// Migrate a hand-wired runner onto a shared environment helper. The golden checkout used to run between host start and load;
/// it must move into the beforeLoad callback so the event order stays fixture-create, host-start, checkout, load, client-connect.
/// </summary>
internal static class MigrateOntoEnvironmentHelperScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/MigrateOntoEnvironmentHelper/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/MigrateOntoEnvironmentHelper/";
    private const string EnvironmentPath = CoreFolder + "EvalEnvironment.cs";
    private const string RepoPath = CoreFolder + "GoldenRepo.cs";
    private const string RunnerPath = CoreFolder + "SteppedRunner.cs";
    private const string TestsPath = TestFolder + "SteppedRunnerTests.cs";
    private const string EventLogTestsPath = TestFolder + "EventLogTests.cs";

    private const string RunnerTypeName = "ContosoOrders.Evals.MigrateOntoEnvironmentHelper.SteppedRunner";

    // The seed's comment on the checkout (see SeedRunnerSource); the grader looks for it whitespace-normalised among the runner's comments (R15: no symbol can see a comment).
    private const string CheckoutComment = "The checkout happens after the host started and before the solution is loaded, so the server never sees a harness write.";

    private const string EnvironmentSource = """
namespace ContosoOrders.Evals.MigrateOntoEnvironmentHelper;

/// <summary>Ordered log of what the helpers did; tests read it.</summary>
public static class EventLog
{
    public static List<string> Entries { get; } = new();

    public static void Add(string entry) => Entries.Add(entry);
}

/// <summary>The working copy a run happens in.</summary>
public sealed class EvalFixture
{
    public EvalFixture(string directory)
    {
        Directory = directory;
        EventLog.Add("fixture-create");
    }

    public string Directory { get; }
}

/// <summary>The server host.</summary>
public sealed class EvalHost : IAsyncDisposable
{
    private EvalHost()
    {
    }

    public static Task<EvalHost> StartAsync()
    {
        EventLog.Add("host-start");
        return Task.FromResult(new EvalHost());
    }

    public ValueTask DisposeAsync()
    {
        EventLog.Add("host-dispose");
        return ValueTask.CompletedTask;
    }
}

/// <summary>The client connected to the host.</summary>
public sealed class EvalClient : IAsyncDisposable
{
    private EvalClient()
    {
    }

    public static Task<EvalClient> ConnectAsync()
    {
        EventLog.Add("client-connect");
        return Task.FromResult(new EvalClient());
    }

    public ValueTask DisposeAsync()
    {
        EventLog.Add("client-dispose");
        return ValueTask.CompletedTask;
    }
}

/// <summary>A staging step run on the fixture around the solution load.</summary>
public delegate Task EvalStaging(EvalFixture fixture, CancellationToken cancellationToken);

/// <summary>Builds fixture, host and client in the right order; beforeLoad runs after the host started and before the load.</summary>
public sealed class EvalEnvironment : IAsyncDisposable
{
    private readonly EvalHost _host;
    private readonly EvalClient _client;

    private EvalEnvironment(EvalFixture fixture, EvalHost host, EvalClient client)
    {
        Fixture = fixture;
        _host = host;
        _client = client;
    }

    public EvalFixture Fixture { get; }

    public string SolutionDirectory => Fixture.Directory;

    public static async Task<EvalEnvironment> CreateAsync(
        string runDirectory,
        EvalStaging? beforeLoad,
        EvalStaging? afterLoad,
        CancellationToken cancellationToken)
    {
        var fixture = new EvalFixture(runDirectory);
        var host = await EvalHost.StartAsync();
        if (beforeLoad is not null)
        {
            await beforeLoad(fixture, cancellationToken);
        }

        EventLog.Add("load");
        if (afterLoad is not null)
        {
            await afterLoad(fixture, cancellationToken);
        }

        var client = await EvalClient.ConnectAsync();
        return new EvalEnvironment(fixture, host, client);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }
}
""";

    private const string RepoSource = """
namespace ContosoOrders.Evals.MigrateOntoEnvironmentHelper;

/// <summary>The golden commits of a scenario inside a working copy.</summary>
public sealed class GoldenRepo
{
    private readonly int _lastStep;

    private GoldenRepo(string directory, int lastStep)
    {
        Directory = directory;
        _lastStep = lastStep;
    }

    public string Directory { get; }

    public static GoldenRepo Create(string directory, int lastStep) => new(directory, lastStep);

    public void CheckoutStep(int step, string branch)
    {
        if (step < 0 || step > _lastStep)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"No step {step} for branch {branch}.");
        }

        EventLog.Add("checkout");
    }

    public IReadOnlyList<string> ChangedPathsSinceStep(int step) => Array.Empty<string>();
}
""";

    private const string SeedRunnerSource = """
namespace ContosoOrders.Evals.MigrateOntoEnvironmentHelper;

/// <summary>Runs one scenario step against a host with the golden state checked out.</summary>
public sealed class SteppedRunner
{
    // TODO extract the shared setup (third copy)
    private readonly bool _modelConfigured;
    private EvalHost? _host;
    private EvalClient? _client;
    private EvalFixture? _fixture;
    private string? _runDirectory;

    public SteppedRunner(bool modelConfigured)
    {
        _modelConfigured = modelConfigured;
    }

    public void SetUp(string runDirectory)
    {
        if (!_modelConfigured)
        {
            throw new InvalidOperationException("No model is configured.");
        }

        _runDirectory = runDirectory;
    }

    public async Task<string> RunStepAsync(int step, CancellationToken cancellationToken)
    {
        if (_runDirectory is null)
        {
            throw new InvalidOperationException("SetUp was not called.");
        }

        _fixture = new EvalFixture(_runDirectory);
        _host = await EvalHost.StartAsync();

        // The checkout happens after the host started and before the solution is loaded, so the server never sees a harness write.
        var repository = GoldenRepo.Create(_fixture.Directory, 3);
        repository.CheckoutStep(step - 1, $"run/step-{step}");
        EventLog.Add("load");

        _client = await EvalClient.ConnectAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return $"step {step} ran in {_fixture.Directory} ({repository.ChangedPathsSinceStep(step - 1).Count} changed)";
    }

    public async Task TearDownAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _client = null;
        _host = null;
        _fixture = null;
    }
}
""";

    private const string GoldenRunnerSource = """
namespace ContosoOrders.Evals.MigrateOntoEnvironmentHelper;

/// <summary>Runs one scenario step against a host with the golden state checked out.</summary>
public sealed class SteppedRunner
{
    private readonly bool _modelConfigured;
    private EvalEnvironment? _environment;
    private string? _runDirectory;

    public SteppedRunner(bool modelConfigured)
    {
        _modelConfigured = modelConfigured;
    }

    public void SetUp(string runDirectory)
    {
        if (!_modelConfigured)
        {
            throw new InvalidOperationException("No model is configured.");
        }

        _runDirectory = runDirectory;
    }

    public async Task<string> RunStepAsync(int step, CancellationToken cancellationToken)
    {
        if (_runDirectory is null)
        {
            throw new InvalidOperationException("SetUp was not called.");
        }

        GoldenRepo? repository = null;
        _environment = await EvalEnvironment.CreateAsync(
            _runDirectory,
            beforeLoad: (fixture, ct) =>
            {
                // The checkout happens after the host started and before the solution is loaded, so the server never sees a harness write.
                repository = GoldenRepo.Create(fixture.Directory, 3);
                repository.CheckoutStep(step - 1, $"run/step-{step}");
                return Task.CompletedTask;
            },
            afterLoad: null,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return $"step {step} ran in {_environment.SolutionDirectory} ({repository!.ChangedPathsSinceStep(step - 1).Count} changed)";
    }

    public async Task TearDownAsync()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
            _environment = null;
        }
    }
}
""";

    private const string TestsSource = """
using ContosoOrders.Evals.MigrateOntoEnvironmentHelper;
using Xunit;

namespace ContosoOrders.Tests.Evals.MigrateOntoEnvironmentHelper;

[Collection("MigrateOntoEnvironmentHelperEventLog")]
public class SteppedRunnerTests
{
    [Fact]
    public async Task RunStep_ReturnsTheStepDescription()
    {
        EventLog.Entries.Clear();
        var runner = new SteppedRunner(modelConfigured: true);
        runner.SetUp("run-dir");
        var description = await runner.RunStepAsync(1, CancellationToken.None);
        await runner.TearDownAsync();
        Assert.Equal("step 1 ran in run-dir (0 changed)", description);
    }

    [Fact]
    public void SetUp_WithoutAModel_Throws()
    {
        var runner = new SteppedRunner(modelConfigured: false);
        Assert.Throws<InvalidOperationException>(() => runner.SetUp("run-dir"));
    }
}
""";

    private const string EventLogTestsSource = """
using ContosoOrders.Evals.MigrateOntoEnvironmentHelper;
using Xunit;

namespace ContosoOrders.Tests.Evals.MigrateOntoEnvironmentHelper;

[Collection("MigrateOntoEnvironmentHelperEventLog")]
public class EventLogTests
{
    [Fact]
    public async Task SuccessRunWritesTheEntriesInOrder()
    {
        EventLog.Entries.Clear();
        var runner = new SteppedRunner(modelConfigured: true);
        runner.SetUp("run-dir");

        var description = await runner.RunStepAsync(2, CancellationToken.None);

        Assert.Equal("step 2 ran in run-dir (0 changed)", description);
        Assert.Equal(
            new[] { "fixture-create", "host-start", "checkout", "load", "client-connect" },
            EventLog.Entries.ToArray());

        await runner.TearDownAsync();
        Assert.Equal(
            new[] { "fixture-create", "host-start", "checkout", "load", "client-connect", "client-dispose", "host-dispose" },
            EventLog.Entries.ToArray());
    }

    [Fact]
    public async Task GuardsAreKept()
    {
        Assert.Throws<InvalidOperationException>(() => new SteppedRunner(modelConfigured: false).SetUp("run-dir"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteppedRunner(modelConfigured: true).RunStepAsync(1, CancellationToken.None));
        await new SteppedRunner(modelConfigured: true).TearDownAsync();
    }
}
""";

    private const string CannedReport = "DONE: SteppedRunner now uses EvalEnvironment.CreateAsync with the checkout in the beforeLoad callback; the fields _host, _client and _fixture and the TODO are gone, the checkout comment is kept.";

    // Expression-bodied on purpose: a static readonly field would render before the contract placement is known (initialisation order).
    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/MigrateOntoEnvironmentHelper, SteppedRunner wires its own fixture, host and client. " +
            "Migrate it onto `EvalEnvironment.CreateAsync` (EvalEnvironment.cs) so there is one setup path. " +
            "Delete the hand-wired setup code and the TODO comment about extracting the shared setup; " +
            "replace the fields `_host`, `_client` and `_fixture` by one field `private EvalEnvironment? _environment;`; " +
            "do the golden checkout inside the beforeLoad staging callback (the `GoldenRepo` object is still needed afterwards for the changed-path count; " +
            "keep the comment that explains when the checkout has to happen); `TearDownAsync` disposes the environment. " +
            "Keep the guard checks in SetUp and RunStepAsync and the run-directory assignment.",
        Files: "SteppedRunner.cs in ContosoOrders.Core/Evals/MigrateOntoEnvironmentHelper only. EvalEnvironment.cs and GoldenRepo.cs stay as they are.",
        Conditions: "On a run that succeeds, the entries written to EventLog and their order stay identical (EventLogTests pins them). " +
            "Error paths are out of scope: EvalEnvironment.CreateAsync keeps the host in a local, so a checkout that throws inside the callback " +
            "cannot dispose the host the old code disposed.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including SteppedRunnerTests and EventLogTests; " +
            "no field `_host`, `_client` or `_fixture` remains; the TODO comment is gone and the comment about when the checkout must happen is kept."));

    // The contract placement is read once per process, when this Definition initialises (StepPrompt renders then); one process runs one arm.
    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "MigrateOntoEnvironmentHelper",
        "Migrate a hand-wired runner onto a shared environment helper, moving the checkout into the before-load hook",
        "adherence, ordering",
        "ModelEval-Scenario-Ideas 19, commit 0feac026",
        new Dictionary<string, string>
        {
            [EnvironmentPath] = EnvironmentSource,
            [RepoPath] = RepoSource,
            [RunnerPath] = SeedRunnerSource,
            [TestsPath] = TestsSource,
            [EventLogTestsPath] = EventLogTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string> { [RunnerPath] = GoldenRunnerSource },
                AllowedPaths: new List<string> { RunnerPath },
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckMigrated(ctx)),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "SteppedRunnerTests", "EventLogTests" }),
        },
        PreferredHost: "113",
        PrimaryAxis: EvalAxis.InstructionAdherence);

    /// <summary>
    /// The runner declares none of the fields _host, _client and _fixture and one field _environment of type EvalEnvironment?, never
    /// starts or connects an EvalHost or EvalClient and never creates an EvalFixture (symbol checks); the TODO about extracting the
    /// shared setup is gone and the comment on when the checkout has to happen is still there (comment text: no symbol can see a
    /// comment). Checkout-before-load is decided by the EventLogTests order test and the scope by the shared check, not here.
    /// </summary>
    private static string? CheckMigrated(ImplementationScenarioCheckContext ctx)
    {
        var compilation = ctx.Compile(CoreFolder.TrimEnd('/'));
        var runner = compilation.Type(RunnerTypeName);
        if (runner is null)
        {
            return "the type SteppedRunner is missing from ContosoOrders.Core/Evals/MigrateOntoEnvironmentHelper";
        }

        var fields = runner.GetMembers().OfType<IFieldSymbol>().ToList();
        foreach (var name in new[] { "_host", "_client", "_fixture" })
        {
            if (fields.Any(field => field.Name == name))
            {
                return $"the hand-wired field {name} is still there";
            }
        }

        var environment = fields.FirstOrDefault(field => field.Name == "_environment");
        if (environment is null)
        {
            return "there is no _environment field";
        }

        if (environment.Type is not INamedTypeSymbol { Name: "EvalEnvironment", NullableAnnotation: NullableAnnotation.Annotated })
        {
            return $"the field _environment must be declared as EvalEnvironment? (it is {environment.Type.ToDisplayString()})";
        }

        // Reading fixture.Directory from the beforeLoad callback parameter is not hand-wiring (the golden does it), so EvalFixture
        // is flagged only where the runner creates one; EvalHost and EvalClient are flagged for any member (StartAsync, ConnectAsync,
        // DisposeAsync). Decision on the spec's "a member of EvalHost, EvalClient or EvalFixture", which the golden itself would violate.
        var model = compilation.ModelFor(RunnerPath);
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not (IdentifierNameSyntax or BaseObjectCreationExpressionSyntax))
            {
                continue;
            }

            var wired = HandWiredReference(model.GetSymbolInfo(node).Symbol);
            if (wired is not null)
            {
                return $"the runner still wires its setup by hand ({wired})";
            }
        }

        var comments = Normalize(string.Join(" ", ctx.Syntax(RunnerPath).GetRoot().DescendantTrivia()
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .Select(CommentText)));
        if (comments.Contains("extract the shared setup", StringComparison.OrdinalIgnoreCase))
        {
            return "the TODO comment about extracting the shared setup is still there";
        }

        if (!comments.Contains(Normalize(CheckoutComment), StringComparison.OrdinalIgnoreCase))
        {
            return "the comment that explains when the checkout has to happen is gone";
        }

        return null;
    }

    private static string? HandWiredReference(ISymbol? symbol) => symbol switch
    {
        { ContainingType.Name: "EvalHost" or "EvalClient" } => $"{symbol.ContainingType.Name}.{symbol.Name}",
        IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType.Name: "EvalFixture" } => "new EvalFixture",
        _ => null,
    };

    // The text of one comment without its markers, lines joined by a space.
    private static string CommentText(SyntaxTrivia comment) =>
        string.Join(" ", comment.ToString().Split('\n').Select(line => line.Trim().TrimStart('/', '*').TrimEnd('*', '/').Trim()));

    private static string Normalize(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The executable outcome table (Spec S15): each variant is a one-file overlay of SteppedRunner.cs (the golden plus one edit) on
    /// step 1 (golden) with a DONE: final message. Every non-passing row claims DONE, so its class is SilentIncomplete.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // Edits are written with \n; the template's line endings are kept. Throws when a template no longer contains the text to
        // edit, so a template change cannot silently turn a variant into the golden.
        static string Edited(string text, string oldText, string newText)
        {
            var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!normalized.Contains(oldText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"MigrateOntoEnvironmentHelper variant edit: '{oldText}' is not in the template.");
            }

            return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
        }

        static IReadOnlyDictionary<string, string> Overlay(string runner) =>
            new Dictionary<string, string> { [RunnerPath] = runner };

        const string Done = "DONE: migrated SteppedRunner onto EvalEnvironment.";

        // GoldenRepo.Create runs before CreateAsync; only CheckoutStep stays in the callback, so the event order is unchanged.
        var checkoutBeforeCreate = Edited(
            Edited(
                Edited(
                    GoldenRunnerSource,
                    "        GoldenRepo? repository = null;\n",
                    "        var repository = GoldenRepo.Create(_runDirectory, 3);\n"),
                "                repository = GoldenRepo.Create(fixture.Directory, 3);\n",
                string.Empty),
            "repository!.",
            "repository.");

        // The golden without the comment on when the checkout has to happen.
        var commentDropped = Edited(GoldenRunnerSource, "                // " + CheckoutComment + "\n", string.Empty);

        // The SetUp guard no longer throws (the shared tests pin it).
        var guardRemoved = Edited(
            GoldenRunnerSource,
            "            throw new InvalidOperationException(\"No model is configured.\");\n",
            "            return;\n");

        // The three old fields stay next to _environment (unused, so the tests still pass).
        var oldFieldsKept = Edited(
            GoldenRunnerSource,
            "    private EvalEnvironment? _environment;\n",
            "    private EvalHost? _host;\n    private EvalClient? _client;\n    private EvalFixture? _fixture;\n    private EvalEnvironment? _environment;\n");

        return new[]
        {
            new ScenarioVariant(
                Name: "CheckoutBeforeCreate",
                BaseStep: 1,
                Files: Overlay(checkoutBeforeCreate),
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "CommentDropped",
                BaseStep: 1,
                Files: Overlay(commentDropped),
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "when the checkout has to happen"),
            new ScenarioVariant(
                Name: "GuardRemoved",
                BaseStep: 1,
                Files: Overlay(guardRemoved),
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "check tests failed"),
            new ScenarioVariant(
                Name: "OldFieldsKept",
                BaseStep: 1,
                Files: Overlay(oldFieldsKept),
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "the hand-wired field _host"),
        };
    }
}

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

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

    private const string HiddenTestSource = """
using ContosoOrders.Evals.MigrateOntoEnvironmentHelper;
using Xunit;

namespace ContosoOrders.Tests.Evals.MigrateOntoEnvironmentHelper;

[Collection("MigrateOntoEnvironmentHelperEventLog")]
public class MigrateOntoEnvironmentHelperStep1Tests
{
    [Fact]
    public async Task RunStep_ChecksOutBeforeTheLoadAndKeepsTheEventOrder()
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
    public async Task Guards_AreKept()
    {
        Assert.Throws<InvalidOperationException>(() => new SteppedRunner(modelConfigured: false).SetUp("run-dir"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteppedRunner(modelConfigured: true).RunStepAsync(1, CancellationToken.None));
        await new SteppedRunner(modelConfigured: true).TearDownAsync();
    }
}
""";

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/MigrateOntoEnvironmentHelper, SteppedRunner wires its own fixture, host and client. Migrate it onto
EvalEnvironment.CreateAsync (EvalEnvironment.cs; do not change that file). Keep the guard checks in SetUp and RunStepAsync and
the run-directory assignment. Delete the hand-wired setup code and the TODO comment about extracting the shared setup, replace
the fields _host, _client and _fixture by one field `private EvalEnvironment? _environment;`, and do the golden checkout inside
the beforeLoad staging callback (the GoldenRepo object is still needed afterwards for the changed-path count; keep the comment
that explains when the checkout has to happen). Update TearDownAsync to dispose the environment. Behaviour must stay identical,
including the order of the entries written to EventLog. Edit no other file.
""";

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
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string> { [RunnerPath] = GoldenRunnerSource },
                AllowedPaths: new List<string> { RunnerPath },
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckMigrated(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "113");

    /// <summary>Hand-wired fields and the TODO are gone, one _environment field exists, and the checkout runs inside the beforeLoad lambda of CreateAsync.</summary>
    private static string? CheckMigrated(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in new[] { EnvironmentPath, RepoPath, TestsPath })
        {
            if (!ctx.IsUnchanged(path))
            {
                return $"changed but must stay as it was: {path}";
            }
        }

        if (ctx.ReadText(RunnerPath).Contains("extract the shared setup"))
        {
            return "the TODO about extracting the shared setup is still there";
        }

        var root = ctx.Syntax(RunnerPath).GetRoot();
        var fields = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.ValueText).ToList();
        foreach (var name in new[] { "_host", "_client", "_fixture" })
        {
            if (fields.Contains(name))
            {
                return $"the hand-wired field {name} is still there";
            }
        }

        if (!fields.Contains("_environment"))
        {
            return "there is no _environment field";
        }

        var wired = root.DescendantTokens()
            .FirstOrDefault(token => token.Kind() == SyntaxKind.IdentifierToken && (token.ValueText == "EvalHost" || token.ValueText == "EvalClient"));
        if (wired.ValueText is "EvalHost" or "EvalClient")
        {
            return $"the runner still wires {wired.ValueText} by hand";
        }

        if (root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(create => create.Type.ToString() == "EvalFixture"))
        {
            return "the runner still creates an EvalFixture by hand";
        }

        var checkout = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(call => call.Expression.ToString().EndsWith("CheckoutStep", StringComparison.Ordinal));
        if (checkout is null)
        {
            return "CheckoutStep is no longer called";
        }

        var lambda = checkout.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
        if (lambda is null || !lambda.Ancestors().OfType<InvocationExpressionSyntax>()
                .Any(call => call.Expression.ToString().EndsWith("EvalEnvironment.CreateAsync", StringComparison.Ordinal)))
        {
            return "the checkout is not done inside the beforeLoad callback passed to EvalEnvironment.CreateAsync";
        }

        return null;
    }
}

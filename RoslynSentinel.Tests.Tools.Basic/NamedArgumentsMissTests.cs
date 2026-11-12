using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Lookup misses of NamedArgumentsEngine reached through NamedArgumentsTools (plan_not_found_error_orientation, Step 5e slice 4):
/// scope project (project name), scope file (path) and targetSymbol (docCommentId). A near name gives the ranked candidate with its retry,
/// an unrelated name gives the no-similar-name fact and, where one is callable, a prefilled orientation call. The code stays NotFound and
/// mode: apply writes nothing (the file text is compared before and after).
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("NamedArgumentsTools")] // sentinel:auto-category
public class NamedArgumentsMissTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/NamedArgumentsFixture.cs";

    private const string Source = """
        public class Pricing
        {
            public int ComputeTotal(int rate, int tax) => rate + tax;

            public int Run() => ComputeTotal(1, 2);
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // Runs one refused call: a NotFound error and the file untouched. Returns the error text.
    private static async Task<string> RunAsync(NamedArgumentsScope scope, Func<InMemoryWorkspace, string?> scopeName, string? targetSymbol = null)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, Source));
        var tools = new NamedArgumentsTools(
            new NamedArgumentsEngine(workspace.Manager),
            Battery.Basic.TestEngines.ValidationEngine(workspace.Manager),
            workspace.Manager,
            NullLogger<NamedArgumentsTools>.Instance);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.NamedArgumentsTool(
            new ToolCallReason("testing a lookup miss"),
            scope, scopeName: scopeName(workspace), targetSymbol: targetSymbol, minParameters: null,
            literalArgumentsOnly: false, SemanticReplaceMode.apply);

        Assert.That(result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A refused lookup must not write the file.");
        return result.ErrorData!.Message;
    }

    // ---- scope: project (SelectDocuments) ----

    [Test]
    public async Task ProjectScope_MisspeltProject_OffersTheNearProjectWithItsRetryAsync()
    {
        var message = await RunAsync(NamedArgumentsScope.project, _ => "InMemoryProjct");

        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with scopeName: 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        Assert.That(message, Does.Contain("'InMemoryProjct'"), "The name the caller passed is echoed.");
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task ProjectScope_NoSimilarProject_StatesTheNoCandidateFactAsync()
    {
        var message = await RunAsync(NamedArgumentsScope.project, _ => "Zzz.Missing");

        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- scope: file (SelectDocuments) ----

    [Test]
    public async Task FileScope_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunAsync(NamedArgumentsScope.file, w => w.PathOf("ContosoOrders.Core/NamedArgumentsFixtur.cs"));

        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("NamedArgumentsFixture.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task FileScope_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        var message = await RunAsync(NamedArgumentsScope.file, w => w.PathOf("ContosoOrders.Core/Zzzqqq.cs"));

        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- targetSymbol (ResolveTargetAsync) ----

    [Test]
    public async Task TargetSymbol_NearId_OffersTheCandidateIdAndTheToolsOwnRetryAsync()
    {
        var message = await RunAsync(NamedArgumentsScope.solution, _ => null, "M:Pricing.ComputeTotl(System.Int32,System.Int32)");

        Assert.That(message, Does.Contain("targetSymbol: docCommentId 'M:Pricing.ComputeTotl(System.Int32,System.Int32)' resolves to no symbol"));
        Assert.That(message, Does.Contain("docCommentId 'M:Pricing.ComputeTotal(System.Int32,System.Int32)'"), "the candidate states its exact id");
        // The retry is the tool's own call (preview, so it writes nothing); how many of its parameters render depends on the signature catalog in scope, so only the tool name is pinned.
        Assert.That(message, Does.Contain("NamedArguments"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task TargetSymbol_NoSimilarId_StatesTheFactAndOffersTheOutlineAsync()
    {
        var message = await RunAsync(NamedArgumentsScope.solution, _ => null, "M:Pricing.Qzxvbnm(System.Int32)");

        Assert.That(message, Does.Contain("targetSymbol: docCommentId 'M:Pricing.Qzxvbnm(System.Int32)' resolves to no symbol"));
        Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
        Assert.That(message, Does.Contain("GetFileOutline"), "the container Pricing resolves to one file, so that file's outline is the orientation call");
        AssertFactsAndOptionsOnly(message);
    }
}

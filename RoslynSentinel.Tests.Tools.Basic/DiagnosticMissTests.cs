using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the DiagnosticEngine lookups that moved onto the shared helpers (plan_not_found_error_orientation, Step 5e slice 2).
// Both producers are driven through GetDiagnostics, the tool that hosts them: GetFileDiagnosticsAsync (scope file) and GetProjectDiagnosticsAsync
// (scope project). A near-miss name gets the ranked "Did you mean" candidate with its retry, an unrelated name gets the no-similar-name fact,
// and a refused call writes nothing (GetDiagnostics never writes, so the file text is compared before and after).
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class DiagnosticMissTests
{
    private const string FileName = "Calc.cs";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator
        {
            public int ComputeTotal(int rate, int tax) => rate + tax;
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // Runs one refused call: a NotFound error and the file untouched. Returns the error text.
    private static async Task<string> RunMissAsync(Func<InMemoryWorkspace, Task<(bool IsError, string? Code, string? Message)>> call)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var (isError, code, message) = await call(workspace);
        Assert.That(isError, Is.True, message);
        Assert.That(code, Is.EqualTo(nameof(ToolErrorCode.NotFound)), message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused lookup must not write the file.");
        return message ?? string.Empty;
    }

    // ---- GetDiagnostics(scope: file): GetFileDiagnosticsAsync ----

    [Test]
    [Category("DiagnosticEngine")]
    public async Task GetDiagnosticsFile_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).GetDiagnosticsTool(reason: "test message", ToolScope.file, w.PathOf("Calcs.cs"));
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Calc.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("DiagnosticEngine")]
    public async Task GetDiagnosticsFile_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).GetDiagnosticsTool(reason: "test message", ToolScope.file, w.PathOf("Zzzqqq.cs"));
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- GetDiagnostics(scope: project): GetProjectDiagnosticsAsync ----

    [Test]
    [Category("DiagnosticEngine")]
    public async Task GetDiagnosticsProject_MisspeltProject_OffersTheNearProjectAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).GetDiagnosticsTool(reason: "test message", ToolScope.project, "InMemoryProjct");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with projectName: 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        Assert.That(message, Does.Contain("'InMemoryProjct'"), "The name the caller passed is echoed.");
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("DiagnosticEngine")]
    public async Task GetDiagnosticsProject_NoSimilarProject_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).GetDiagnosticsTool(reason: "test message", ToolScope.project, "Zzz.Missing");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }
}

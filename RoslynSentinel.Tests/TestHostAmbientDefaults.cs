namespace RoslynSentinel.Tests;

/// <summary>
/// The one place for the ambient defaults that every test assembly building an in-process server host sets
/// from its <c>[SetUpFixture]</c>. A fixture only ever calls <see cref="Apply"/> and never sets a default itself.
/// </summary>
public static class TestHostAmbientDefaults
{
    /// <summary>
    /// Applies the ambient defaults for the current test process. Call once from a <c>[OneTimeSetUp]</c>
    /// before any in-process host is built. Switches the call-transcript folder to the test-host folder
    /// (<c>call-transcripts-tests</c>) with its smaller cap, so test hosts never write to the real-session folder.
    /// Also makes the solution restore that LoadSolution and CreateProject run a no-op
    /// (<see cref="RoslynSentinel.Engines.Basic.SolutionRestore.NoOpRestoreForTests"/>): every TestSolutionFixture copy lacks obj/,
    /// so a real restore would spawn dotnet on every load. A test that needs the real restore passes
    /// <c>SolutionRestore.RestoreAsync</c> as the explicit restore seam of the object under test.
    /// </summary>
    public static void Apply()
    {
        RoslynSentinel.Common.ToolCallTranscript.UseTestHostDefaults();
        RoslynSentinel.Engines.Basic.SolutionRestore.AmbientRestore = RoslynSentinel.Engines.Basic.SolutionRestore.NoOpRestoreForTests;
    }
}

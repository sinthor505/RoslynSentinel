namespace RoslynSentinel.Tests;

/// <summary>
/// The one place for the ambient defaults that every test assembly building an in-process server host sets
/// from its <c>[SetUpFixture]</c>. Later steps add more defaults here (for example a no-op solution restore),
/// so a fixture only ever calls <see cref="Apply"/> and never sets a default itself.
/// </summary>
public static class TestHostAmbientDefaults
{
    /// <summary>
    /// Applies the ambient defaults for the current test process. Call once from a <c>[OneTimeSetUp]</c>
    /// before any in-process host is built. Switches the call-transcript folder to the test-host folder
    /// (<c>call-transcripts-tests</c>) with its smaller cap, so test hosts never write to the real-session folder.
    /// </summary>
    public static void Apply()
    {
        RoslynSentinel.Common.ToolCallTranscript.UseTestHostDefaults();
    }
}

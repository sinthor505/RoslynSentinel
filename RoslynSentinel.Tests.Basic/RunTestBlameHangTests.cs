using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// TestRunEngine.BlameHangArguments and ParseHungTestName wire `dotnet test --blame-hang` into RunOneProjectAsync.
// dotnet test prints the hung test's name after the "The test running when the crash occurred:" marker, either on the
// marker line or on the line after it.
[TestFixture]
public class RunTestBlameHangTests
{
    private const string HungStdoutNextLine =
        "The active test run was aborted. Reason: Test host process crashed\n" +
        "...\n" +
        "The test running when the crash occurred: \n" +
        "Spike.HangTests.ThisOneHangsForever\n" +
        "\n" +
        "This test may, or may not be the source of the crash.\n";

    [Test]
    public void BlameHangArguments_600s_TimeoutIsFiveSecondsUnderProjectTimeout()
    {
        var args = TestRunEngine.BlameHangArguments(600);

        Assert.That(ValueAfter(args, "--blame-hang-timeout"), Is.EqualTo("595s"));
    }

    [Test]
    public void BlameHangArguments_DumpTypeIsNone()
    {
        var args = TestRunEngine.BlameHangArguments(600);

        Assert.That(args, Does.Contain("--blame-hang"));
        Assert.That(ValueAfter(args, "--blame-hang-dump-type"), Is.EqualTo("none"));
    }

    [Test]
    public void BlameHangArguments_SmallProjectTimeout_FloorsAt30Seconds()
    {
        var args = TestRunEngine.BlameHangArguments(20);

        Assert.That(ValueAfter(args, "--blame-hang-timeout"), Is.EqualTo("30s"));
    }

    [Test]
    public void ParseHungTestName_NameOnLineAfterMarker_ReturnsName()
    {
        var name = TestRunEngine.ParseHungTestName(HungStdoutNextLine);

        Assert.That(name, Is.EqualTo("Spike.HangTests.ThisOneHangsForever"));
    }

    [Test]
    public void ParseHungTestName_NameOnMarkerLine_ReturnsName()
    {
        var name = TestRunEngine.ParseHungTestName(
            "The test running when the crash occurred: Spike.HangTests.ThisOneHangsForever\nafter\n");

        Assert.That(name, Is.EqualTo("Spike.HangTests.ThisOneHangsForever"));
    }

    [Test]
    public void ParseHungTestName_OrdinaryOutput_ReturnsNull()
    {
        var name = TestRunEngine.ParseHungTestName("Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3\n");

        Assert.That(name, Is.Null);
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

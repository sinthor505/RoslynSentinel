using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public class StopMarkerTests
{
    [TestCase("BLOCKED: x")]
    [TestCase("  BLOCKED: x")]
    [TestCase("# BLOCKED: x")]
    [TestCase("## **BLOCKED**: x")]
    [TestCase("`BLOCKED`: x")]
    [TestCase("_BLOCKED_: x")]
    public void IsLeading_MarkerFirst_ReturnsTrue(string message)
    {
        Assert.That(StopMarker.IsLeading(message), Is.True);
    }

    [TestCase("I am BLOCKED: x")]
    [TestCase("Not blocked")]
    [TestCase("blocked: x")]
    [TestCase("")]
    [TestCase("First line\nBLOCKED: x")]
    [TestCase("BLOCKED")]
    public void IsLeading_MarkerNotFirst_ReturnsFalse(string message)
    {
        Assert.That(StopMarker.IsLeading(message), Is.False);
    }

    [TestCase("First line\nBLOCKED: x")]
    [TestCase("I am BLOCKED: x")]
    [TestCase("BLOCKED: x")]
    public void ContainsAnywhere_MarkerPresent_ReturnsTrue(string message)
    {
        Assert.That(StopMarker.ContainsAnywhere(message), Is.True);
    }

    [TestCase("BLOCKED")]
    [TestCase("blocked: x")]
    [TestCase("")]
    public void ContainsAnywhere_MarkerAbsent_ReturnsFalse(string message)
    {
        Assert.That(StopMarker.ContainsAnywhere(message), Is.False);
    }
}

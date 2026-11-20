namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public class PlaceholderTests
{
    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Assertion", "NUnit2007:The actual value should not be a constant", Justification = "Placeholder Test")]
    public void Placeholder_AssertsTrue()
    {
        Assert.That(true, Is.True);
    }
}

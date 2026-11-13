namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public class PlaceholderTests
{
    [Test]
    public void Placeholder_AssertsTrue()
    {
        Assert.That(true, Is.True);
    }
}

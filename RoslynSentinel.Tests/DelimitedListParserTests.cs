using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("DelimitedListParser")]
public class DelimitedListParserTests
{
    [Test]
    public void CommaList_ReturnsTwoItemsWithoutError()
    {
        var result = DelimitedListParser.ParseStringOrJsonArrayToList("a.cs,b.cs", out var error);
        Assert.That(error, Is.Null);
        Assert.That(result, Is.EqualTo(new[] { "a.cs", "b.cs" }));
    }

    [Test]
    public void WellFormedJsonArray_ReturnsTwoItemsWithoutError()
    {
        var result = DelimitedListParser.ParseStringOrJsonArrayToList("[\"a.cs\",\"b.cs\"]", out var error);
        Assert.That(error, Is.Null);
        Assert.That(result, Is.EqualTo(new[] { "a.cs", "b.cs" }));
    }

    [Test]
    public void UnclosedJsonArray_ReportsParseError()
    {
        var result = DelimitedListParser.ParseStringOrJsonArrayToList("[\"a.cs\"", out var error);
        Assert.That(result, Is.Null);
        Assert.That(error, Does.Contain("looks like a JSON array"));
    }

    [Test]
    public void UnopenedJsonArray_ReportsParseError()
    {
        var result = DelimitedListParser.ParseStringOrJsonArrayToList("\"a.cs\"]", out var error);
        Assert.That(result, Is.Null);
        Assert.That(error, Is.Not.Null);
    }

    [Test]
    public void BracketedPlainName_WithoutQuote_IsCommaSplit()
    {
        var result = DelimitedListParser.ParseStringOrJsonArrayToList("[abc].cs", out var error);
        Assert.That(error, Is.Null);
        Assert.That(result, Is.EqualTo(new[] { "[abc].cs" }));
    }

    [Test]
    public void NullOrWhitespace_ReturnsEmptyWithoutError()
    {
        var fromNull = DelimitedListParser.ParseStringOrJsonArrayToList(null, out var nullError);
        var fromWhitespace = DelimitedListParser.ParseStringOrJsonArrayToList("   ", out var wsError);
        Assert.That(nullError, Is.Null);
        Assert.That(fromNull, Is.Empty);
        Assert.That(wsError, Is.Null);
        Assert.That(fromWhitespace, Is.Empty);
    }
}

using System.Linq;
using NUnit.Framework;
using RoslynSentinel.Tests.Support;

namespace RoslynSentinel.Tests;

[TestFixture]
public class FormatKitTests
{
    [Test]
    public void Src_JoinsLinesWithLf_WhenCrlfIsFalse()
    {
        Assert.That(FormatKit.Src(false, "one", "two"), Is.EqualTo("one\ntwo"));
    }

    [Test]
    public void Src_JoinsLinesWithCrlf_WhenCrlfIsTrue()
    {
        Assert.That(FormatKit.Src(true, "one", "two"), Is.EqualTo("one\r\ntwo"));
    }

    [Test]
    public void WithStrayCrlf_LeavesExactlyOneCarriageReturnOnAnchorLine()
    {
        var mixed = FormatKit.WithStrayCrlf("one\ntwo\nthree\n", "two");

        Assert.That(mixed, Is.EqualTo("one\ntwo\r\nthree\n"));
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1));
    }

    [Test]
    public void WithStrayCrlf_FailsPrecondition_WhenAnchorLineIsMissing()
    {
        Assert.Throws<AssertionException>(() => FormatKit.WithStrayCrlf("one\ntwo\n", "missing"));
    }

    [Test]
    public void AssertByteExact_Passes_WhenTextIsEqual()
    {
        Assert.DoesNotThrow(() => FormatKit.AssertByteExact("a\r\nb", "a\r\nb"));
    }

    [Test]
    public void AssertByteExact_Fails_ForCrlfVersusLf_AndShowsVisibleCrLfMarkers()
    {
        var ex = Assert.Throws<AssertionException>(() => FormatKit.AssertByteExact("a\r\nb", "a\nb"));

        Assert.That(ex.Message, Does.Contain("\\r\\n"));
    }

    [Test]
    public void AssertSameIgnoringWhitespace_Passes_ForDifferentIndentationAndLineBreaks()
    {
        Assert.DoesNotThrow(() => FormatKit.AssertSameIgnoringWhitespace("a  b\n\tc\r\n", "a b c"));
    }

    [Test]
    public void AssertSameIgnoringWhitespace_Fails_ForDifferentToken()
    {
        Assert.Throws<AssertionException>(() => FormatKit.AssertSameIgnoringWhitespace("a b d", "a b c"));
    }
}

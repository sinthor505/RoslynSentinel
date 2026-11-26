// ThinkTagText strips the think tags some models wrap their reasoning in, and reports whether a close tag is present.
// These tests pin the text transformation: tags are removed case-insensitively, inner text is kept, and the result is trimmed.

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ThinkTagTextTests
{
    [Test]
    public void RemoveTags_LoneCloseTag_KeepsProse()
    {
        const string prose = "Looking at the file first.";

        Assert.That(ThinkTagText.RemoveTags(prose + ThinkTagText.CloseTag), Is.EqualTo(prose));
    }

    [Test]
    public void RemoveTags_TagPairAnyCase_KeepsInnerText()
    {
        const string inner = "plan the edit";

        Assert.That(ThinkTagText.RemoveTags("<think>" + inner + "</THINK>"), Is.EqualTo(inner));
    }

    [Test]
    public void HasCloseTag_OnlyWhenCloseTagPresent()
    {
        Assert.That(ThinkTagText.HasCloseTag("x" + ThinkTagText.CloseTag), Is.True);
        Assert.That(ThinkTagText.HasCloseTag(null), Is.False);
        Assert.That(ThinkTagText.HasCloseTag(string.Empty), Is.False);
        Assert.That(ThinkTagText.HasCloseTag("x"), Is.False);
        Assert.That(ThinkTagText.HasCloseTag("<think>x"), Is.False);
    }
}

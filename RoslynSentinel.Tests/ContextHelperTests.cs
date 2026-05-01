using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

public class ContextHelperTests
{
    [Test]
    public void FindSnippetPosition_UniqueSnippet_ReturnsCorrectOffset()
    {
        var source = "namespace Foo;\npublic class Bar { public int X => 42; }";
        var snippet = "public int X";
        var pos = ContextHelper.FindSnippetPosition(source, snippet);
        Assert.That(pos, Is.EqualTo(source.IndexOf(snippet, StringComparison.Ordinal)));
    }

    [Test]
    public void FindSnippetPosition_NotFound_ThrowsHelpfulError()
    {
        var source = "namespace Foo;\npublic class Bar { }";
        var ex = Assert.Throws<InvalidOperationException>(
            () => ContextHelper.FindSnippetPosition(source, "NotPresent"));
        Assert.That(ex!.Message, Does.Contain("not found").IgnoreCase);
    }

    [Test]
    public void FindSnippetPosition_Ambiguous_ThrowsWithCount()
    {
        var source = "int x = 1; int y = 1;";
        var ex = Assert.Throws<InvalidOperationException>(
            () => ContextHelper.FindSnippetPosition(source, "int"));
        Assert.That(ex!.Message, Does.Contain("ambiguous").IgnoreCase);
        Assert.That(ex.Message, Does.Contain("2"));
    }

    [Test]
    public void FindSnippetPosition_EmptySnippet_Throws()
    {
        var source = "namespace Foo;";
        Assert.Throws<InvalidOperationException>(
            () => ContextHelper.FindSnippetPosition(source, "   "));
    }
}

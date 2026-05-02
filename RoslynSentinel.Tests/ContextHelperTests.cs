using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    // ── Bug 7 regression: AdvanceToLastIdentifier ─────────────────────────────

    [Test]
    public void AdvanceToLastIdentifier_WhenSnippetStartsWithKeyword_ReturnsIdentifierPosition()
    {
        // Simulate: snippet "public async Task GetByIdAsync" starting at start of the method decl.
        // FindSnippetPosition would return the offset of "public" (a keyword).
        // AdvanceToLastIdentifier should advance to "GetByIdAsync".
        var source = "public class Foo { public async Task GetByIdAsync(int id) => id; }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var snippetStart = source.IndexOf("public async Task GetByIdAsync", StringComparison.Ordinal);
        var snippet = "public async Task GetByIdAsync";
        var idPos = ContextHelper.AdvanceToLastIdentifier(root, snippetStart, snippet.Length);

        // The identifier at idPos should be "GetByIdAsync"
        var token = root.FindToken(idPos);
        Assert.That(token.Text, Is.EqualTo("GetByIdAsync"),
            "AdvanceToLastIdentifier should land on the declared name, not the modifier keyword");
    }

    [Test]
    public void AdvanceToLastIdentifier_WhenSnippetStartsWithIdentifier_ReturnsOriginalPosition()
    {
        // When snippet already starts with an identifier, position should not change.
        var source = "public class MyService { }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var snippetStart = source.IndexOf("MyService", StringComparison.Ordinal);
        var snippet = "MyService";
        var idPos = ContextHelper.AdvanceToLastIdentifier(root, snippetStart, snippet.Length);

        Assert.That(idPos, Is.EqualTo(snippetStart),
            "If snippet starts on an identifier, position should remain unchanged");
    }

    [Test]
    public void AdvanceToLastIdentifier_GenericReturnType_ReturnsMethodNameNotTypeArg()
    {
        // Snippet: "public Task<string> GetNameAsync" — last identifier is GetNameAsync, not string
        var source = "public class Svc { public Task<string> GetNameAsync() => Task.FromResult(\"\"); }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var snippet = "public Task<string> GetNameAsync";
        var snippetStart = source.IndexOf(snippet, StringComparison.Ordinal);
        var idPos = ContextHelper.AdvanceToLastIdentifier(root, snippetStart, snippet.Length);

        var token = root.FindToken(idPos);
        Assert.That(token.Text, Is.EqualTo("GetNameAsync"));
    }
}

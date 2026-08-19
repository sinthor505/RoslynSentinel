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

    // ── Regression: multi-line contextSnippet with mismatched indentation ────
    // Reproduces a live ContosoOrders agent run (ExtractMethodSafe, Step 6): the agent had
    // just read the exact 8/12-space-indented source via ReadFile, then retyped the snippet
    // from memory with flattened indentation (0 spaces on the outer lines, 4 in the loop body)
    // instead of copying it verbatim. An LLM reliably reproduces tokens but not incidental
    // whitespace, so a multi-line snippet whose indentation doesn't match the source is the
    // common case, not an edge case — this must resolve, not throw "contextSnippet not found".

    private const string OrderProcessorLikeSource =
        "namespace ContosoOrders.Core;\r\n" +
        "\r\n" +
        "public class Order\r\n" +
        "{\r\n" +
        "    public string BuildOrderSummary()\r\n" +
        "    {\r\n" +
        "        var sb = new System.Text.StringBuilder();\r\n" +
        "        sb.AppendLine($\"Order for {_customerId}\");\r\n" +
        "        sb.AppendLine($\"Status: {Status}\");\r\n" +
        "\r\n" +
        "        // --- extract-target block start ---\r\n" +
        "        decimal runningTotal = 0m;\r\n" +
        "        int totalUnits = 0;\r\n" +
        "        foreach (var line in _lines)\r\n" +
        "        {\r\n" +
        "            runningTotal += line.Quantity * line.UnitPrice;\r\n" +
        "            totalUnits += line.Quantity;\r\n" +
        "        }\r\n" +
        "        sb.AppendLine($\"Total units: {totalUnits}\");\r\n" +
        "        sb.AppendLine($\"Running total: {runningTotal:C}\");\r\n" +
        "        // --- extract-target block end ---\r\n" +
        "\r\n" +
        "        return sb.ToString();\r\n" +
        "    }\r\n" +
        "}\r\n";

    [Test]
    public void FindSnippetPosition_MultilineSnippetWithFlattenedIndentation_ResolvesViaWindowFallback()
    {
        // Exact snippet shape from the live agent transcript: \n-joined, 0 spaces on the
        // outer lines, 4 spaces inside the loop body — the file itself uses 8/12 spaces.
        var snippet =
            "decimal runningTotal = 0m;\n" +
            "int totalUnits = 0;\n" +
            "foreach (var line in _lines)\n" +
            "{\n" +
            "    runningTotal += line.Quantity * line.UnitPrice;\n" +
            "    totalUnits += line.Quantity;\n" +
            "}";

        var pos = ContextHelper.FindSnippetPosition(OrderProcessorLikeSource, snippet);

        // The window fallback resolves to the start of the matched source *line* (including its
        // real leading whitespace), not the start of the trimmed statement text within it — same
        // convention as the existing single-line collapsed-whitespace fallback just above it.
        var lineStart = OrderProcessorLikeSource.LastIndexOf(
            '\n', OrderProcessorLikeSource.IndexOf("decimal runningTotal = 0m;", StringComparison.Ordinal)) + 1;
        Assert.That(pos, Is.EqualTo(lineStart),
            "Should resolve to the start of the real (differently-indented) block in source");
    }

    [Test]
    public void FindSnippetPosition_MultilineSnippetReorderedStatements_StillNotFound()
    {
        // The window fallback must stay structure-sensitive: reordering statements is a real
        // content difference, not a formatting difference, and must still fail to match.
        var snippet =
            "int totalUnits = 0;\n" +
            "decimal runningTotal = 0m;\n" +
            "foreach (var line in _lines)\n" +
            "{\n" +
            "    runningTotal += line.Quantity * line.UnitPrice;\n" +
            "    totalUnits += line.Quantity;\n" +
            "}";

        Assert.Throws<InvalidOperationException>(
            () => ContextHelper.FindSnippetPosition(OrderProcessorLikeSource, snippet));
    }
}

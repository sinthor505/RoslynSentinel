using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

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

    [Test]
    [Description("Regression (ContosoOrders live agent run, attempt 7): a caller-supplied "
                 + "contextSnippet ending in a trailing newline (e.g. copying a statement plus its "
                 + "closing brace with a trailing '\\n') produced a phantom empty line via "
                 + "Split('\\n'), inflating the sliding window's size by one and forcing it to "
                 + "compare against one extra, unrelated real source line — so a snippet like "
                 + "'return foo;\\n}\\n' never matched even though 'return foo;\\n}' (no trailing "
                 + "newline) matched fine. Trailing (and leading) blank lines in the snippet must "
                 + "not affect the window size.")]
    public void FindSnippetPosition_MultilineSnippetWithTrailingNewline_StillResolvesViaWindowFallback()
    {
        var snippetNoTrailingNewline =
            "runningTotal += line.Quantity * line.UnitPrice;\n" +
            "totalUnits += line.Quantity;\n" +
            "}";
        var snippetWithTrailingNewline = snippetNoTrailingNewline + "\n";

        var posWithoutTrailingNewline = ContextHelper.FindSnippetPosition(OrderProcessorLikeSource, snippetNoTrailingNewline);
        var posWithTrailingNewline = ContextHelper.FindSnippetPosition(OrderProcessorLikeSource, snippetWithTrailingNewline);

        Assert.That(posWithTrailingNewline, Is.EqualTo(posWithoutTrailingNewline),
            "A trailing newline on an otherwise-identical snippet must resolve to the same position, " +
            "not fail to match at all.");
    }

    // ── Real-world corpus: every contextSnippet a live agent actually sent ─────
    // Pulled verbatim from 5 recorded ContosoOrders agent-run transcripts (attempts 1-4 and 7,
    // covering both the original Qwen3.5-9b run and every subsequent RoslynSentinel-tool-surface
    // revision). Each entry reproduces a REAL wire-level contextSnippet argument an agent sent for
    // a REAL target that exists in the sample source, so these are not synthetic edge cases —
    // they're exactly what tools receive in practice. A tool description that says "distinctive
    // substring" invites free-form fragments; this corpus is what "free-form" looks like from an
    // actual 7B-9B model, and every one of these should resolve, since each targets real, existing
    // code (contrast with FindSnippetPosition_MultilineSnippetReorderedStatements_StillNotFound
    // above, which is a genuine content difference and must keep failing).

    // An earlier revision of the sample's BuildOrderSummary had a blank line between the loop's
    // closing brace and the following sb.AppendLine call (confirmed against the raw transcript's
    // own GetMethodSource tool result, which echoed "}\r\n\r\n        sb.AppendLine..." verbatim) —
    // later attempts' PLAN.md/source revisions removed that blank line, but the agent's snippet
    // was captured against THIS shape and copied it faithfully. Kept as its own constant (rather
    // than editing the shared OrderProcessorLikeSource used by the tests above) since it's a
    // distinct historical source revision, not a formatting variant of the same file.
    private const string OrderProcessorLikeSourceWithBlankLineBeforeFooter =
        "namespace ContosoOrders.Core;\r\n" +
        "\r\n" +
        "public class Order\r\n" +
        "{\r\n" +
        "    public string BuildOrderSummary()\r\n" +
        "    {\r\n" +
        "        var sb = new System.Text.StringBuilder();\r\n" +
        "        sb.AppendLine($\"Order for {_customerId}\");\r\n" +
        "        sb.AppendLine($\"Status: {Status}\");\r\n" +
        "        // --- extract-target block start ---\r\n" +
        "        decimal runningTotal = 0m;\r\n" +
        "        int totalUnits = 0;\r\n" +
        "        foreach (var line in _lines)\r\n" +
        "        {\r\n" +
        "            runningTotal += line.Quantity * line.UnitPrice;\r\n" +
        "            totalUnits += line.Quantity;\r\n" +
        "        }\r\n" +
        "\r\n" +
        "        sb.AppendLine($\"Total units: {totalUnits}\");\r\n" +
        "        sb.AppendLine($\"Running total: {runningTotal:C}\");\r\n" +
        "        // --- extract-target block end ---\r\n" +
        "        return sb.ToString();\r\n" +
        "    }\r\n" +
        "}\r\n";

    private const string ApplyDiscountLikeSource =
        "namespace ContosoOrders.Core;\r\n" +
        "\r\n" +
        "public class Order\r\n" +
        "{\r\n" +
        "    // Unused private method: nothing in the solution calls this. Target for SafeDeleteUnusedSymbol.\r\n" +
        "    private string BuildInternalDebugLabel()\r\n" +
        "    {\r\n" +
        "        return $\"[{_customerId}] {_lines.Count} line(s)\";\r\n" +
        "    }\r\n" +
        "\r\n" +
        "    private decimal ApplyDiscount(decimal percentage)\r\n" +
        "    {\r\n" +
        "        // NOTE: this method uses DiscountCalculator, but the using directive for\r\n" +
        "        // ContosoOrders.Core.Discounts is intentionally missing from this file (fully qualified below\r\n" +
        "        // as a workaround) to create a scenario for AddUsingDirective.\r\n" +
        "        return ContosoOrders.Core.Discounts.DiscountCalculator.ApplyPercentage(CalculateTotal(), percentage);\r\n" +
        "    }\r\n" +
        "}\r\n";

    private const string OrderServiceLikeSource =
        "namespace ContosoOrders.Core;\r\n" +
        "\r\n" +
        "public class OrderService\r\n" +
        "{\r\n" +
        "    private readonly List<Order> _orders = new();\r\n" +
        "\r\n" +
        "    public Order CreateOrder(string customerId, List<OrderLine> lines)\r\n" +
        "    {\r\n" +
        "        var order = new Order(customerId, lines);\r\n" +
        "        _orders.Add(order);\r\n" +
        "        return order;\r\n" +
        "    }\r\n" +
        "}\r\n";

    private static readonly (string Label, string Source, string ContextSnippet)[] RealAgentContextSnippets =
    [
        // ── ExtractMethodSafe on BuildOrderSummary's totals block, every shape actually sent ──
        // The 3 entries below cross the loop's closing brace into the following sb.AppendLine
        // calls, which the transcript's own GetMethodSource result confirms had a blank line
        // between them at the time — hence OrderProcessorLikeSourceWithBlankLineBeforeFooter,
        // not the later (blank-line-removed) OrderProcessorLikeSource.
        ("attempt1_wholeBlockPlusFooter",
            OrderProcessorLikeSourceWithBlankLineBeforeFooter,
            "// --- extract-target block start ---\ndecimal runningTotal = 0m;\nint totalUnits = 0;\nforeach (var line in _lines)\n{\n    runningTotal += line.Quantity * line.UnitPrice;\n    totalUnits += line.Quantity;\n}\n\nsb.AppendLine($\"Total units: {totalUnits}\");\nsb.AppendLine($\"Running total: {runningTotal:C}\");\n// --- extract-target block end ---"),
        ("attempt1_blockPlusOneFooterLine",
            OrderProcessorLikeSourceWithBlankLineBeforeFooter,
            "decimal runningTotal = 0m;\nint totalUnits = 0;\nforeach (var line in _lines)\n{\n    runningTotal += line.Quantity * line.UnitPrice;\n    totalUnits += line.Quantity;\n}\n\nsb.AppendLine($\"Total units: {totalUnits}\");"),
        ("attempt2_wholeBlockNoFooter",
            OrderProcessorLikeSource,
            "// --- extract-target block start ---\ndecimal runningTotal = 0m;\nint totalUnits = 0;\nforeach (var line in _lines)\n{\n    runningTotal += line.Quantity * line.UnitPrice;\n    totalUnits += line.Quantity;\n}"),
        ("attempt2_wholeBlockPlusBothFooterLines",
            OrderProcessorLikeSourceWithBlankLineBeforeFooter,
            "// --- extract-target block start ---\ndecimal runningTotal = 0m;\nint totalUnits = 0;\nforeach (var line in _lines)\n{\n    runningTotal += line.Quantity * line.UnitPrice;\n    totalUnits += line.Quantity;\n}\n\nsb.AppendLine($\"Total units: {totalUnits}\");\nsb.AppendLine($\"Running total: {runningTotal:C}\");\n// --- extract-target block end ---"),

        // ── AddSummaryComment on OrderService.CreateOrder, every shape actually sent ──
        ("attempt2_fullMethodBody",
            OrderServiceLikeSource,
            "public Order CreateOrder(string customerId, List<OrderLine> lines)\n    {\n        var order = new Order(customerId, lines);\n        _orders.Add(order);\n        return order;\n    }"),
        ("attempt3_signatureOnly",
            OrderServiceLikeSource,
            "public Order CreateOrder(string customerId, List<OrderLine> lines)"),

        // ── ChangeAccessibility / ReplaceMember on Order.ApplyDiscount, every shape actually sent ──
        ("attempt2_replaceMember_commentOnly",
            ApplyDiscountLikeSource,
            "// NOTE: this method uses DiscountCalculator, but the using directive for\n// ContosoOrders.Core.Discounts is intentionally missing from this file (fully qualified below\n// as a workaround) to create a scenario for AddUsingDirective."),
        ("attempt3_changeAccessibility_signatureOnly",
            ApplyDiscountLikeSource,
            "private decimal ApplyDiscount(decimal percentage)"),

        // ── SafeDeleteUnusedSymbol on BuildInternalDebugLabel, actually sent (attempt 2) ──
        // Note: the agent's own reconstruction subtly differs from the real source (see
        // FindSnippetPosition_SafeDelete_AgentFabricatedInterpolation_StillFailsToMatch below for
        // the case where that difference is real, not just whitespace) — this entry uses the
        // agent's snippet reproduced verbatim against a source shape it DOES match structurally.
        ("attempt2_safeDelete_commentPlusSignature",
            ApplyDiscountLikeSource,
            "// Unused private method: nothing in the solution calls this. Target for SafeDeleteUnusedSymbol.\nprivate string BuildInternalDebugLabel()\n{\n    return $\"[{_customerId}] {_lines.Count} line(s)\";\n}"),
    ];

    [TestCaseSource(nameof(RealAgentContextSnippets))]
    public void FindSnippetPosition_RealAgentContextSnippetCorpus_Resolves((string Label, string Source, string ContextSnippet) testCase)
    {
        int pos;
        try
        {
            pos = ContextHelper.FindSnippetPosition(testCase.Source, testCase.ContextSnippet);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Fail($"[{testCase.Label}] contextSnippet failed to resolve against real target " +
                $"source, but this snippet targets code that genuinely exists: {ex.Message}");
            return;
        }

        Assert.That(pos, Is.GreaterThanOrEqualTo(0), $"[{testCase.Label}] resolved to a valid position");
    }

    [Test]
    [Description("Regression (ContosoOrders live agent run, attempt 2): the agent's SafeDeleteUnusedSymbol "
                 + "contextSnippet reconstructed the method body from memory instead of copying it "
                 + "verbatim, and introduced a genuine CONTENT difference, not just whitespace: it wrote "
                 + "return $\"{_customerId}\" + \" \" + {_lines.Count} + \" line(s)\"; (string concatenation, "
                 + "and not even valid C# — {_lines.Count} outside an interpolated string) where the real "
                 + "source has return $\"[{_customerId}] {_lines.Count} line(s)\"; (a single interpolated "
                 + "string). This must keep failing to match — it is not a formatting difference the "
                 + "whitespace-tolerant fallback should paper over, and conflating the two would let a "
                 + "tool silently act on the wrong text.")]
    public void FindSnippetPosition_SafeDelete_AgentFabricatedInterpolation_StillFailsToMatch()
    {
        var fabricatedSnippet =
            "// Unused private method: nothing in the solution calls this. Target for SafeDeleteUnusedSymbol.\n" +
            "private string BuildInternalDebugLabel()\n" +
            "{\n" +
            "    return $\"{_customerId}\" + \" \" + {_lines.Count} + \" line(s)\";\n" +
            "}";

        Assert.Throws<InvalidOperationException>(
            () => ContextHelper.FindSnippetPosition(ApplyDiscountLikeSource, fabricatedSnippet));
    }
}

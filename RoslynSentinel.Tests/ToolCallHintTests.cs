using System.Text.Json;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ToolCallHintTests
{
    private const string SearchSchema = """{"type":"object","properties":{"reason":{"type":"string"},"mode":{"type":"string","enum":["symbol","text","references"]},"query":{"type":"string"},"maxResults":{"type":"integer"}},"required":["reason","mode"]}""";
    private const string LoadSolutionSchema = """{"type":"object","properties":{"reason":{"type":"string"},"solutionPath":{"type":"string"}},"required":["reason","solutionPath"]}""";
    private const string ToggleSchema = """{"type":"object","properties":{"reason":{"type":"string"},"verbose":{"type":"boolean"}},"required":["reason"]}""";

    private ToolSignatureCatalog _savedCurrent = ToolSignatureCatalog.Empty;

    [SetUp]
    public void SaveCurrent() => _savedCurrent = ToolCallHint.Current;

    [TearDown]
    public void RestoreCurrent() => ToolCallHint.Current = _savedCurrent;

    [Test]
    public void Call_MissingRequired_UsesPlaceholders()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "Search"), Is.EqualTo("Search(reason: \"<why you call this>\", mode: <symbol|text|references>)"));
    }

    [Test]
    public void Call_SuppliedValues_RenderByType()
    {
        var catalog = BuildCatalog();

        var call = ToolCallHint.Call(catalog, "Search", ("reason", "I need to find the symbol"), ("mode", "text"), ("query", "say \"hi\" \\ there"), ("maxResults", "25"));

        Assert.That(call, Is.EqualTo("Search(reason: \"I need to find the symbol\", mode: text, query: \"say \\\"hi\\\" \\\\ there\", maxResults: 25)"));
    }

    [Test]
    public void Call_OptionalUnsupplied_IsOmitted()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "Search", ("reason", "a reason that is long"), ("mode", "symbol")), Is.EqualTo("Search(reason: \"a reason that is long\", mode: symbol)"));
    }

    [Test]
    public void Call_SchemaOrderKept_RegardlessOfArgumentOrder()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "Search", ("maxResults", "3"), ("mode", "symbol"), ("reason", "a reason that is long")), Is.EqualTo("Search(reason: \"a reason that is long\", mode: symbol, maxResults: 3)"));
    }

    [Test]
    public void Call_BooleanRendersLowercase()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "Toggle", ("reason", "a reason that is long"), ("verbose", "TRUE")), Is.EqualTo("Toggle(reason: \"a reason that is long\", verbose: true)"));
    }

    [Test]
    public void Call_UnknownOrEmptyTool_ReturnsNameOrEmpty()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "Nope", ("reason", "a reason that is long")), Is.EqualTo("Nope"));
        Assert.That(ToolCallHint.Call(catalog, ""), Is.EqualTo(string.Empty));
        Assert.That(ToolCallHint.Call(catalog, null!), Is.EqualTo(string.Empty));
        Assert.That(ToolCallHint.Call(ToolSignatureCatalog.Empty, "Search"), Is.EqualTo("Search"));
    }

    [Test]
    public void Call_UnknownParameter_IsDropped()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "LoadSolution", ("reason", "a reason that is long"), ("bogus", "x"), ("solutionPath", "C:/a.sln")), Is.EqualTo("LoadSolution(reason: \"a reason that is long\", solutionPath: \"C:/a.sln\")"));
    }

    [Test]
    public void Call_InvalidEnum_IsDroppedAndPlaceholderShown()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "Search", ("reason", "a reason that is long"), ("mode", "nope")), Is.EqualTo("Search(reason: \"a reason that is long\", mode: <symbol|text|references>)"));
    }

    [Test]
    public void Call_InvalidNumber_IsDropped()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "Search", ("reason", "a reason that is long"), ("mode", "text"), ("maxResults", "twelve")), Is.EqualTo("Search(reason: \"a reason that is long\", mode: text)"));
    }

    [Test]
    public void Call_SuppliedReason_ReplacesPlaceholderButOtherRequiredKeepsIt()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Call(catalog, "LoadSolution", ("reason", "a reason that is long")), Is.EqualTo("LoadSolution(reason: \"a reason that is long\", solutionPath: \"<solutionPath>\")"));
    }

    [Test]
    public void Next_FormatsCallAndMessage()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Next(catalog, "LoadSolution", "Then retry.", ("reason", "a reason that is long"), ("solutionPath", "x.sln")), Is.EqualTo("Next: LoadSolution(reason: \"a reason that is long\", solutionPath: \"x.sln\") Then retry."));
    }

    [Test]
    public void Next_EmptyMessage_HasNoTrailingSpace()
    {
        var catalog = BuildCatalog();

        var hint = ToolCallHint.Next(catalog, "LoadSolution", string.Empty, ("reason", "a reason that is long"), ("solutionPath", "x.sln"));

        Assert.That(hint, Is.EqualTo("Next: LoadSolution(reason: \"a reason that is long\", solutionPath: \"x.sln\")"));
        Assert.That(hint, Does.Not.EndWith(" "));
    }

    [Test]
    public void Current_NoOverride_UsesFallbackAndNullClearsTestCatalog()
    {
        ToolCallHint.Current = null!;
        Assert.That(ToolCallHint.Current, Is.Not.Null);

        var testCatalog = BuildCatalog();
        ToolCallHint.Current = testCatalog;
        Assert.That(ToolCallHint.Current, Is.SameAs(testCatalog));

        ToolCallHint.Current = null!;
        Assert.That(ToolCallHint.Current, Is.Not.SameAs(testCatalog));
    }

    [Test]
    public void CurrentOverloads_UseCurrentCatalog()
    {
        ToolCallHint.Current = BuildCatalog();

        Assert.That(ToolCallHint.Call("Search", ("mode", "text")), Is.EqualTo("Search(reason: \"<why you call this>\", mode: text)"));
        Assert.That(ToolCallHint.Next("LoadSolution", string.Empty, ("reason", "a reason that is long"), ("solutionPath", "x.sln")), Is.EqualTo("Next: LoadSolution(reason: \"a reason that is long\", solutionPath: \"x.sln\")"));
        Assert.That(ToolCallHint.Validate("Nope"), Is.EqualTo(new[] { "Unknown tool 'Nope'." }));
    }

    [Test]
    public void Validate_ValidCall_IsEmpty()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Validate(catalog, "Search", ("reason", "a reason that is long"), ("mode", "text")), Is.Empty);
    }

    [Test]
    public void Validate_UnknownTool_ReportsIt()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Validate(catalog, "Nope"), Is.EqualTo(new[] { "Unknown tool 'Nope'." }));
    }

    [Test]
    public void Validate_UnknownParameter_ReportsIt()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Validate(catalog, "LoadSolution", ("reason", "a reason that is long"), ("solutionPath", "x"), ("bogus", "y")), Is.EqualTo(new[] { "Unknown parameter 'bogus' for tool 'LoadSolution'." }));
    }

    [Test]
    public void Validate_InvalidEnum_NamesAllowedValues()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Validate(catalog, "Search", ("reason", "a reason that is long"), ("mode", "nope")), Is.EqualTo(new[] { "Invalid value 'nope' for 'mode'; allowed: symbol, text, references." }));
    }

    [Test]
    public void Validate_UnparsableNumberAndBoolean_ReportThem()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Validate(catalog, "Search", ("reason", "a reason that is long"), ("mode", "text"), ("maxResults", "twelve")), Is.EqualTo(new[] { "'maxResults' expects an integer, got 'twelve'." }));
        Assert.That(ToolCallHint.Validate(catalog, "Toggle", ("reason", "a reason that is long"), ("verbose", "maybe")), Is.EqualTo(new[] { "'verbose' expects true or false, got 'maybe'." }));
    }

    [Test]
    public void Validate_DuplicateParameter_ReportsIt()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Validate(catalog, "Search", ("reason", "a reason that is long"), ("mode", "text"), ("mode", "symbol")), Is.EqualTo(new[] { "Duplicate parameter 'mode' for tool 'Search'." }));
    }

    [Test]
    public void Validate_OmittedRequiredParameters_AreValid()
    {
        var catalog = BuildCatalog();

        Assert.That(ToolCallHint.Validate(catalog, "LoadSolution"), Is.Empty);
        Assert.That(ToolCallHint.Validate(catalog, "Search", ("mode", "text")), Is.Empty);
    }

    private static ToolSignatureCatalog BuildCatalog()
    {
        using var search = JsonDocument.Parse(SearchSchema);
        using var loadSolution = JsonDocument.Parse(LoadSolutionSchema);
        using var toggle = JsonDocument.Parse(ToggleSchema);
        return ToolSignatureCatalog.FromSchemas(new[]
        {
            KeyValuePair.Create("Search", search.RootElement.Clone()),
            KeyValuePair.Create("LoadSolution", loadSolution.RootElement.Clone()),
            KeyValuePair.Create("Toggle", toggle.RootElement.Clone()),
        });
    }
}

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Tool-level coverage for the <c>Search</c> tool's modes: the blank-query rejection, the
/// InvalidArgument rejection of parameters that do not apply to a mode, the callers and
/// implementations modes, and the syntax-only attribute mode (including includeTests).
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")]
public class SearchModeTests
{
    private const string ProbeFile = "ContosoOrders.Core/SearchModeProbe.cs";

    private const string ProbeSource =
        "namespace ContosoOrders.Core;" + "\n" +
        "public interface ISearchModeProbe" + "\n" +
        "{" + "\n" +
        "    void Target();" + "\n" +
        "}" + "\n" +
        "public class SearchModeProbeImpl : ISearchModeProbe" + "\n" +
        "{" + "\n" +
        "    public void Target() { }" + "\n" +
        "}" + "\n" +
        "public class SearchModeProbeCaller" + "\n" +
        "{" + "\n" +
        "    public void Invoke(ISearchModeProbe probe) { probe.Target(); }" + "\n" +
        "}" + "\n" +
        "public sealed class ProbeMarkerAttribute : System.Attribute { }" + "\n" +
        "[ProbeMarker]" + "\n" +
        "public class MarkedProbe { }" + "\n" +
        "public sealed class CoFooAttribute : System.Attribute { }" + "\n" +
        "public sealed class CoBarAttribute : System.Attribute { }" + "\n" +
        "[CoFoo]" + "\n" +
        "[CoBar]" + "\n" +
        "public class CoBothMarked { }" + "\n" +
        "[CoFoo]" + "\n" +
        "public class CoFooOnly { }" + "\n";

    // Lives in a test project of the sample solution; must be excluded when includeTests is false.
    private const string TestProjectFile = "ContosoOrders.Tests/SearchModeTestProjectUsage.cs";

    private const string TestProjectSource =
        "namespace ContosoOrders.Tests;" + "\n" +
        "[ProbeMarker]" + "\n" +
        "public class TestMarkedProbe { }" + "\n" +
        "public sealed class TestOnlyMarkerAttribute : System.Attribute { }" + "\n" +
        "[TestOnlyMarker]" + "\n" +
        "public class TestOnlyUsage { }" + "\n";

    private sealed record Harness(TestSolutionFixture Fixture, PersistentWorkspaceManager Workspace, WorkspaceTools Tools) : IDisposable
    {
        public void Dispose()
        {
            Workspace.Dispose();
            Fixture.Dispose();
        }
    }

    /// <summary>Seeds the probe files into a fresh sample solution copy, then loads it.</summary>
    private static async Task<Harness> CreateHarnessAsync()
    {
        var fixture = new TestSolutionFixture();

        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = fixture.SolutionPath })
        {
            await fixture.AddFileToSolution(seedWorkspaceManager, ProbeFile, ProbeSource, reloadSolution: false);
            await fixture.AddFileToSolution(seedWorkspaceManager, TestProjectFile, TestProjectSource, reloadSolution: false);
        }

        var workspace = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspace.LoadSolutionAsync(fixture.SolutionPath);

        var tools = TestTools.WorkspaceTools(workspace);

        return new Harness(fixture, workspace, tools);
    }

    private static string ProbeFilePath(Harness h) => Path.Combine(h.Fixture.SolutionDirectory, ProbeFile);

    private static string Serialize(object? value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Test]
    public async Task Search_CallersMode_WithoutQuery_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search mode query required regression",
            SearchMode.callers);

        Assert.That(result.IsError, Is.True, "A blank query must be rejected for mode callers.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("query is required for mode"));
        Assert.That(result.ErrorData?.Message, Does.Contain("for example"));
        Assert.That(result.ErrorData?.Message, Does.Contain("Search("));
    }

    [Test]
    public async Task Search_TextMode_WithIrrelevantParameter_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search irrelevant parameter rejection regression",
            SearchMode.text,
            query: "Target",
            symbolKind: SymbolKindFilter.method);

        Assert.That(result.IsError, Is.True, "A symbol-only parameter passed to mode text must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("symbolKind"));
    }

    [Test]
    public async Task Search_TextMode_ContextLines2_ReturnsTwoLinesBeforeAndAfterEachHit()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text context lines two-before-and-after regression",
            SearchMode.text,
            query: "public class MarkedProbe",
            contextLines: 2);

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        var hit = ((TextSearchResult)result.SuccessData!).LiteralResults
            .Single(m => m.Preview.Contains("public class MarkedProbe"));
        Assert.That(hit.ContextBefore, Is.EqualTo(new[]
        {
            "public sealed class ProbeMarkerAttribute : System.Attribute { }",
            "[ProbeMarker]",
        }), "contextLines 2 must return the two source lines above the hit.");
        Assert.That(hit.ContextAfter, Is.EqualTo(new[]
        {
            "public sealed class CoFooAttribute : System.Attribute { }",
            "public sealed class CoBarAttribute : System.Attribute { }",
        }), "contextLines 2 must return the two source lines below the hit.");
    }

    [Test]
    public async Task Search_TextMode_ContextLinesOmitted_ReturnsNullContext()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text context lines omitted regression",
            SearchMode.text,
            query: "public class MarkedProbe");

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        var hit = ((TextSearchResult)result.SuccessData!).LiteralResults
            .Single(m => m.Preview.Contains("public class MarkedProbe"));
        Assert.That(hit.ContextBefore, Is.Null, "Without contextLines the match must carry no ContextBefore.");
        Assert.That(hit.ContextAfter, Is.Null, "Without contextLines the match must carry no ContextAfter.");
    }

    [Test]
    public async Task Search_TextMode_ContextLinesAboveMax_IsClampedToTen()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text context lines clamp to ten regression",
            SearchMode.text,
            query: "probe.Target",
            contextLines: 50);

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        var hit = ((TextSearchResult)result.SuccessData!).LiteralResults
            .Single(m => m.Preview.Contains("probe.Target"));
        Assert.That(hit.ContextBefore, Has.Count.EqualTo(10), "contextLines above 10 must be clamped to 10 before the hit.");
        Assert.That(hit.ContextAfter, Has.Count.EqualTo(10), "contextLines above 10 must be clamped to 10 after the hit.");
    }

    [Test]
    public async Task Search_SymbolMode_WithContextLines_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search symbol contextLines rejection regression",
            SearchMode.symbol,
            query: "SearchModeProbeImpl",
            contextLines: 2);

        Assert.That(result.IsError, Is.True, "A text-only parameter passed to mode symbol must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("contextLines"));
    }

    [Test]
    public async Task Search_CallersMode_FindsCallerOfInterfaceMethod()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search callers mode regression",
            SearchMode.callers,
            query: "Target",
            filePath: ProbeFilePath(h),
            contextSnippet: "void Target();");

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        Assert.That(Serialize(result.SuccessData), Does.Contain("Invoke"),
            "Callers of ISearchModeProbe.Target must include SearchModeProbeCaller.Invoke.");
    }

    [Test]
    public async Task Search_ImplementationsMode_FindsImplementerOfInterfaceMethod()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search implementations mode regression",
            SearchMode.implementations,
            query: "Target",
            filePath: ProbeFilePath(h),
            contextSnippet: "void Target();");

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        Assert.That(Serialize(result.SuccessData), Does.Contain("SearchModeProbeImpl"),
            "Implementations of ISearchModeProbe.Target must include SearchModeProbeImpl.Target.");
    }

    [Test]
    public async Task Search_CallersMode_SnippetStartsAtReturnType_ResolvesEnclosingMethod()
    {
        // A snippet beginning at a return-type keyword must resolve the enclosing method, not System.Void.
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search callers snippet return type regression",
            SearchMode.callers,
            query: "Target",
            filePath: ProbeFilePath(h),
            contextSnippet: "void Target();");

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        var json = Serialize(result.SuccessData);
        Assert.That(json, Does.Contain("Invoke"),
            "The snippet 'void Target();' must resolve ISearchModeProbe.Target, so its real caller SearchModeProbeCaller.Invoke is reported.");
        Assert.That(json, Does.Not.Contain("System.Void"),
            "A return-type keyword snippet must not resolve to System.Void.");
    }

    [Test]
    public async Task Search_CallersMode_SnippetNameMismatch_ReturnsNotFoundNotSystemVoid()
    {
        // The snippet resolves to Invoke, which does not match the query Target: must be NotFound, not a wrong symbol.
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search callers snippet name mismatch regression",
            SearchMode.callers,
            query: "Target",
            filePath: ProbeFilePath(h),
            contextSnippet: "public void Invoke(ISearchModeProbe probe) { probe.Target(); }");

        Assert.That(result.IsError, Is.True, "A snippet resolving to a differently named symbol must return an error.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task Search_AttributeMode_FindsDecoratedType()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search attribute mode regression",
            SearchMode.attribute,
            query: "ProbeMarker");

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        Assert.That(Serialize(result.SuccessData), Does.Contain("MarkedProbe"));
    }

    [Test]
    public async Task Search_AttributeMode_IncludeTestsFalse_ExcludesTestProjectOccurrence()
    {
        using var h = await CreateHarnessAsync();

        var withoutTests = await h.Tools.SearchSolutionTool(
            reason: "search attribute excludes test projects regression",
            SearchMode.attribute,
            query: "ProbeMarker",
            includeTests: false);

        Assert.That(withoutTests.IsError, Is.Not.True, withoutTests.ErrorData?.Message);
        var withoutTestsJson = Serialize(withoutTests.SuccessData);
        Assert.That(withoutTestsJson, Does.Contain("MarkedProbe"));
        Assert.That(withoutTestsJson, Does.Not.Contain("TestMarkedProbe"),
            "includeTests=false must skip the occurrence in the test project.");

        var withTests = await h.Tools.SearchSolutionTool(
            reason: "search attribute includes test projects regression",
            SearchMode.attribute,
            query: "ProbeMarker",
            includeTests: true);

        Assert.That(withTests.IsError, Is.Not.True, withTests.ErrorData?.Message);
        Assert.That(Serialize(withTests.SuccessData), Does.Contain("TestMarkedProbe"),
            "includeTests=true must include the occurrence in the test project.");
    }

    [Test]
    public async Task Search_AttributeMode_HasAttribute_KeepsOnlyOwnersCarryingBothAttributes()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search attribute hasAttribute co-attribute filter regression",
            SearchMode.attribute,
            query: "CoFoo",
            hasAttribute: "CoBar");

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        var json = Serialize(result.SuccessData);
        Assert.That(json, Does.Contain("CoBothMarked"), "The class carrying both CoFoo and CoBar must be reported.");
        Assert.That(json, Does.Not.Contain("CoFooOnly"), "A class carrying only CoFoo must be excluded by hasAttribute CoBar.");
    }

    [TestCase("CoBar")]
    [TestCase("CoBarAttribute")]
    [TestCase("ContosoOrders.Core.CoBar")]
    [TestCase("ContosoOrders.Core.CoBarAttribute")]
    public async Task Search_AttributeMode_HasAttribute_MatchesSpellingVariants(string hasAttribute)
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search attribute hasAttribute spelling variants regression",
            SearchMode.attribute,
            query: "CoFoo",
            hasAttribute: hasAttribute);

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        var json = Serialize(result.SuccessData);
        Assert.That(json, Does.Contain("CoBothMarked"), $"hasAttribute '{hasAttribute}' must match CoBar, with or without the Attribute suffix or namespace.");
        Assert.That(json, Does.Not.Contain("CoFooOnly"));
    }

    [Test]
    public async Task Search_AttributeMode_HasAttributeNoMatch_ReturnsEmptySuccessAndSkipsBroadening()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search attribute hasAttribute no match regression",
            SearchMode.attribute,
            query: "CoFoo",
            hasAttribute: "NoSuchCoAttribute");

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
        var json = Serialize(result.SuccessData);
        Assert.That(json, Does.Not.Contain("CoBothMarked"));
        Assert.That(json, Does.Not.Contain("CoFooOnly"));
        Assert.That(result.StatusMessage, Does.Contain("Broaden-on-empty was skipped"),
            "An empty hasAttribute result must say broaden-on-empty was skipped, because it cannot honor the filter.");
    }

    [Test]
    public async Task Search_SymbolMode_WithHasAttribute_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search symbol hasAttribute rejection regression",
            SearchMode.symbol,
            query: "SearchModeProbeImpl",
            hasAttribute: "CoBar");

        Assert.That(result.IsError, Is.True, "An attribute-only parameter passed to mode symbol must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("hasAttribute"));
    }

    [Test]
    public async Task Search_AttributeMode_AttributeOnlyInTestProject_IncludeTestsFalseFindsNothing()
    {
        using var h = await CreateHarnessAsync();

        var withoutTests = await h.Tools.SearchSolutionTool(
            reason: "search attribute test-only attribute excluded regression",
            SearchMode.attribute,
            query: "TestOnlyMarker",
            includeTests: false);

        Assert.That(withoutTests.IsError, Is.Not.True, withoutTests.ErrorData?.Message);
        Assert.That(Serialize(withoutTests.SuccessData), Does.Not.Contain("TestOnlyUsage"),
            "includeTests=false must find nothing for an attribute used only in a test project.");

        var withTests = await h.Tools.SearchSolutionTool(
            reason: "search attribute test-only attribute included regression",
            SearchMode.attribute,
            query: "TestOnlyMarker",
            includeTests: true);

        Assert.That(withTests.IsError, Is.Not.True, withTests.ErrorData?.Message);
        Assert.That(Serialize(withTests.SuccessData), Does.Contain("TestOnlyUsage"),
            "includeTests=true must find the attribute used in the test project.");
    }

    [Test]
    public async Task FindTypesByAttribute_AttributeOnlyInTestProject_IncludeTestsFalseFindsNothing()
    {
        using var h = await CreateHarnessAsync();
        var engine = TestEngines.DiscoveryEngine(h.Workspace);

        var withoutTests = await engine.FindTypesByAttributeAsync("TestOnlyMarker", includeTests: false);
        Assert.That(withoutTests, Is.Empty, "includeTests=false must skip the test project when resolving typesWithAttribute.");

        var withTests = await engine.FindTypesByAttributeAsync("TestOnlyMarker", includeTests: true);
        Assert.That(withTests.Select(r => r.MemberName), Does.Contain("TestOnlyUsage"),
            "includeTests=true must report the type decorated in the test project.");
    }

    [Test]
    public async Task Search_SymbolMode_WithFileGlob_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search symbol fileGlob rejection regression",
            SearchMode.symbol,
            query: "SearchModeProbeImpl",
            fileGlob: "**/*.cs");

        Assert.That(result.IsError, Is.True, "A text-only fileGlob passed to mode symbol must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("fileGlob"));
        Assert.That(result.ErrorData?.Message, Does.Contain("filePath"));
    }

    [Test]
    public async Task Search_ReferencesMode_WithMaxResults_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search references maxResults rejection regression",
            SearchMode.references,
            query: "Target",
            maxResults: 5);

        Assert.That(result.IsError, Is.True, "A text-only maxResults of 5 passed to mode references must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("maxResults"));
    }

    [Test]
    public async Task Search_TextMode_WithContainingType_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text containingType rejection regression",
            SearchMode.text,
            query: "Target",
            containingType: "SearchModeProbeImpl");

        Assert.That(result.IsError, Is.True, "A symbol-only containingType passed to mode text must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("containingType"));
    }

    [Test]
    public async Task Search_SymbolMode_WithContextSnippet_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search symbol contextSnippet rejection regression",
            SearchMode.symbol,
            query: "SearchModeProbeImpl",
            contextSnippet: "public class SearchModeProbeImpl");

        Assert.That(result.IsError, Is.True, "A references-only contextSnippet passed to mode symbol must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("contextSnippet"));
    }

    [Test]
    public async Task Search_TextMode_WithExactMatchFalse_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text exactMatch rejection regression",
            SearchMode.text,
            query: "Target",
            exactMatch: false);

        Assert.That(result.IsError, Is.True, "exactMatch false passed to mode text must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("exactMatch"));
    }

    [Test]
    public async Task Search_TextMode_WithFilePath_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text filePath rejection regression",
            SearchMode.text,
            query: "Target",
            filePath: ProbeFilePath(h));

        Assert.That(result.IsError, Is.True, "A symbol-only filePath passed to mode text must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("filePath"));
        Assert.That(result.ErrorData?.Message, Does.Contain("fileGlob"));
    }

    [Test]
    public async Task Search_ReferencesMode_WithProjectName_IsInvalidArgumentError()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search references projectName rejection regression",
            SearchMode.references,
            query: "Target",
            projectName: "ContosoOrders.Core");

        Assert.That(result.IsError, Is.True, "projectName passed to mode references must be rejected.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("ignores"));
        Assert.That(result.ErrorData?.Message, Does.Contain("projectName"));
    }

    [Test]
    public async Task Search_TextMode_ExplicitDefaults_AreNotRejected()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text explicit default values not rejected regression",
            SearchMode.text,
            query: "Target",
            maxResults: 200,
            contextLines: 0,
            symbolKind: SymbolKindFilter.any,
            exactMatch: true,
            filePath: null,
            fileGlob: "");

        Assert.That(result.IsError, Is.Not.True, "Default or blank values of mode-irrelevant parameters must not be rejected.");
    }

    [Test]
    public async Task Search_SymbolMode_ExplicitDefaults_AreNotRejected()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search symbol explicit default values not rejected regression",
            SearchMode.symbol,
            query: "Target",
            maxResults: 200,
            contextLines: 0);

        Assert.That(result.IsError, Is.Not.True, "Default values of text-only parameters must not be rejected in mode symbol.");
    }

    [Test]
    public async Task Search_TextMode_FileGlobAndContextLines_AreNotRejected()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.SearchSolutionTool(
            reason: "search text fileGlob and contextLines positive control regression",
            SearchMode.text,
            query: "Target",
            fileGlob: "**/*.cs",
            contextLines: 1);

        Assert.That(result.IsError, Is.Not.True, result.ErrorData?.Message);
    }
}

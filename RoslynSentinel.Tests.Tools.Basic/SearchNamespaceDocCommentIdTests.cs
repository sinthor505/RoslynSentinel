using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// docCommentId on Search declaration-kind rows: only Kind "namespace" rows carry "N:Full.Name";
/// every other kind is null and the property is omitted from the wire JSON. Covers the ListAll
/// fill, the NamespaceDocCommentId helper and the large-result round trip.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class SearchNamespaceDocCommentIdTests
{
    private IWorkspaceManager _workspaceManager;
    private WorkspaceTools _workspaceTools;
    private string _tempDir;

    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "SearchNamespaceDocCommentIdTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
        _workspaceTools = TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void SetSources(params (string fileName, string source)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    private async Task<List<SolutionSymbolEntry>> ListKindAsync(SearchMode mode)
    {
        var result = await _workspaceTools.SearchSolutionTool(reason: "test message", mode: mode, query: "");
        Assert.That(result.IsError, Is.False, "Search of a declaration kind with an empty query should succeed.");
        return (List<SolutionSymbolEntry>)result.SuccessData!;
    }

    private static BaseNamespaceDeclarationSyntax FirstNamespace(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First();

    // ══════════════════════════════════════════════════════════════════════════
    // 1 - namespace rows carry N:<full qualified name> (block, file-scoped, nested)
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task Namespace_BlockFileScopedAndNested_DocCommentIdIsFullQualifiedNameAsync()
    {
        SetSources(
            ("AB.cs", "namespace A.B { class C {} }"),
            ("XYZ.cs", "namespace X.Y.Z; public class W { }"),
            ("Outer.cs", "namespace Outer { namespace Inner { } }"));

        var entries = await ListKindAsync(SearchMode.@namespace);

        Assert.That(entries, Has.Count.EqualTo(4));
        var expected = new (string Name, string DocCommentId)[]
        {
            ("A.B", "N:A.B"),
            ("X.Y.Z", "N:X.Y.Z"),
            ("Outer", "N:Outer"),
            ("Inner", "N:Outer.Inner"),
        };
        foreach (var (name, docCommentId) in expected)
        {
            var row = entries.Single(e => e.Name == name);
            Assert.That(row.Kind, Is.EqualTo("namespace"));
            Assert.That(row.DocCommentId, Is.EqualTo(docCommentId), $"namespace row '{name}'");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2 - non-namespace kinds have a null DocCommentId
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task ClassAndMethodRows_DocCommentIdIsNullAsync()
    {
        SetSources(("Order.cs", "namespace TestProj; public class Order { public void Ship() { } }"));

        var classes = await ListKindAsync(SearchMode.@class);
        Assert.That(classes, Has.Count.EqualTo(1));
        Assert.That(classes, Has.All.Property("DocCommentId").Null);

        var methods = await ListKindAsync(SearchMode.method);
        Assert.That(methods, Has.Count.EqualTo(1));
        Assert.That(methods, Has.All.Property("DocCommentId").Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3 - wire JSON: docCommentId present for namespace rows, absent for class rows
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task WireJson_NamespaceRowCarriesDocCommentId_ClassRowOmitsIt()
    {
        SetSources(("AB.cs", "namespace A.B { class C {} }"));

        var namespaces = await ListKindAsync(SearchMode.@namespace);
        var nsRow = namespaces.Single(e => e.Name == "A.B");
        var nsJson = JsonSerializer.Serialize(nsRow, Wire);
        var nsObject = JsonNode.Parse(nsJson)!.AsObject();
        Assert.That(nsObject["docCommentId"]?.GetValue<string>(), Is.EqualTo("N:A.B"), nsJson);

        var classes = await ListKindAsync(SearchMode.@class);
        var classRow = classes.Single(e => e.Name == "C");
        var classJson = JsonSerializer.Serialize(classRow, Wire);
        var classObject = JsonNode.Parse(classJson)!.AsObject();
        Assert.That(classObject.Any(kv => string.Equals(kv.Key, "docCommentId", StringComparison.OrdinalIgnoreCase)),
            Is.False, $"A class row must not carry a docCommentId property: {classJson}");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4 - NamespaceDocCommentId helper (internal; visible to this assembly)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void NamespaceDocCommentId_GlobalAliasPrefix_IsStripped()
    {
        var ns = FirstNamespace("namespace global::A { }");

        Assert.That(WorkspaceReadNavigationImpl.NamespaceDocCommentId(ns), Is.EqualTo("N:A"));
    }

    [Test]
    public void NamespaceDocCommentId_WhitespaceInWrittenName_IsStripped()
    {
        var ns = FirstNamespace("namespace A . B { }");

        Assert.That(WorkspaceReadNavigationImpl.NamespaceDocCommentId(ns), Is.EqualTo("N:A.B"));
    }

    [Test]
    public void NamespaceDocCommentId_NestedNamespace_PrefixesEnclosingNames()
    {
        var inner = CSharpSyntaxTree.ParseText("namespace Outer { namespace Inner { } }").GetRoot()
            .DescendantNodes().OfType<NamespaceDeclarationSyntax>().Single(n => n.Name.ToString() == "Inner");

        Assert.That(WorkspaceReadNavigationImpl.NamespaceDocCommentId(inner), Is.EqualTo("N:Outer.Inner"));
    }

    [Test]
    public void NamespaceDocCommentId_NonNamespaceNode_ReturnsNull()
    {
        var classNode = CSharpSyntaxTree.ParseText("class C { }").GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var compilationUnit = CSharpSyntaxTree.ParseText("namespace A { }").GetRoot();

        Assert.That(WorkspaceReadNavigationImpl.NamespaceDocCommentId(classNode), Is.Null);
        Assert.That(WorkspaceReadNavigationImpl.NamespaceDocCommentId(compilationUnit), Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5 - large-result round trip: written with SharedJsonOptions.Default (the options
    //     GetLargeResult deserializes with), read back through GetLargeResultTool.
    // ══════════════════════════════════════════════════════════════════════════

    private void WriteLargeResultFile<T>(T data, ResultWrapperType type, string resultId)
    {
        var dir = Path.Combine(_tempDir, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePath = Path.Combine(dir, $"largeresult_{ts}_{resultId}.json");
        var wrapper = new ResultWrapper
        {
            Type = type,
            Data = JsonSerializer.SerializeToNode(data, SharedJsonOptions.Default),
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(wrapper, SharedJsonOptions.Default), new UTF8Encoding(false));
    }

    [Test, CancelAfter(10000)]
    public async Task LargeResultRoundTrip_NamespaceDocCommentIdSurvives()
    {
        SetSources();
        var entries = new List<SolutionSymbolEntry>
        {
            new SolutionSymbolEntry(new FilePathWrapper("AB.cs", null), "namespace", "A.B", null, 1, 1, "N:A.B"),
            new SolutionSymbolEntry(new FilePathWrapper("AB.cs", null), "class", "C", "A.B", 1, 1),
        };

        var serialized = JsonSerializer.Serialize(entries, SharedJsonOptions.Default);
        var reparsed = JsonSerializer.Deserialize<List<SolutionSymbolEntry>>(serialized, SharedJsonOptions.Default)!;
        Assert.That(reparsed[0].DocCommentId, Is.EqualTo("N:A.B"), "serializer round trip");
        Assert.That(reparsed[1].DocCommentId, Is.Null, "serializer round trip of a class row");

        var resultId = Guid.NewGuid().ToString("N");
        WriteLargeResultFile(entries, ResultWrapperType.SolutionSymbolEntryList, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 10, offset: 0);

        Assert.That(result.IsError, Is.False);
        var returned = result.SuccessData as List<SolutionSymbolEntry>;
        Assert.That(returned, Is.Not.Null, "Data should be List<SolutionSymbolEntry>.");
        Assert.That(returned!.Single(e => e.Name == "A.B").DocCommentId, Is.EqualTo("N:A.B"));
        Assert.That(returned!.Single(e => e.Name == "C").DocCommentId, Is.Null);
    }
}

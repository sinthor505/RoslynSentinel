// Tool-layer coverage for FindReferences ambiguity: a by-name lookup that matches two same-named
// methods surfaces ErrorCode Ambiguous with one "FindReferences failed:" prefix, and the structured
// candidate list is retry-ready (FilePath + ContextSnippet resolve the lookup).

using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class FindReferencesAmbiguityToolTests
{
    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string FirstSource = "public class First\n{\n    public void Run() { }\n}";
    private const string SecondSource = "public class Second\n{\n    public void Run() { }\n}";
    private const string FirstSnippet = "public void Run() { }";

    private string _dir = string.Empty;
    private string _firstDocPath = string.Empty;
    private string _secondDocPath = string.Empty;
    private IWorkspaceManager _workspaceManager = null!;
    private SymbolRelationshipImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "FindReferencesAmbiguityToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "AmbigProj"));
        var csprojPath = Path.Combine(_dir, "AmbigProj", "AmbigProj.csproj");
        File.WriteAllText(csprojPath, TestSdkCsproj);
        _firstDocPath = Path.Combine(_dir, "AmbigProj", "First.cs");
        _secondDocPath = Path.Combine(_dir, "AmbigProj", "Second.cs");

        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _impl = new SymbolRelationshipImpl(
            TestEngines.DiscoveryEngine(_workspaceManager),
            TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            NullLogger<SymbolRelationshipImpl>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "AmbigProj",
            csprojPath,
            [("First.cs", FirstSource, _firstDocPath)]);
        var projectId = solution.Projects.Single(p => p.Name == "AmbigProj").Id;
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId, "Second.cs"), "Second.cs", SourceText.From(SecondSource), filePath: _secondDocPath);

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public async Task FindReferences_AllKind_AmbiguousName_ReturnsStructuredCandidatesThatRoundTrip()
    {
        var ambiguous = await _impl.FindReferences(
            reason: "ambiguity tool-layer test: by-name lookup of Run",
            symbolName: "Run",
            kind: FindReferencesKind.all);

        Assert.That(ambiguous.IsError, Is.True);
        Assert.That(ambiguous.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        var message = ambiguous.ErrorData.Message;
        Assert.That(System.Text.RegularExpressions.Regex.Matches(message, "FindReferences failed:").Count, Is.EqualTo(1));
        Assert.That(message, Does.Not.Contain("FindCallers:"));
        Assert.That(ambiguous.ErrorData.StructuredDetail, Is.Not.Null);
        Assert.That(ambiguous.ErrorData.StructuredDetail!, Has.Count.EqualTo(2));
        Assert.That(ambiguous.ErrorData.StructuredDetail!.All(d => d is AmbiguousCandidate), Is.True);

        var candidates = ambiguous.ErrorData.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        var pick = candidates[0];
        Assert.That(pick.ContextSnippet, Is.Not.Empty);

        var retry = await _impl.FindReferences(
            reason: "ambiguity tool-layer test: retry with candidate filePath and snippet",
            symbolName: "Run",
            kind: FindReferencesKind.all,
            filePath: pick.FilePath,
            contextSnippet: pick.ContextSnippet);

        Assert.That(retry.IsError, Is.False, retry.ErrorData?.ErrorCode + ": " + retry.ErrorData?.Message);
    }

    // Fix B1: a by-name kind:all lookup of a sealed type (no same-named member) resolves to the type; the
    // implementations half is skipped with a status message instead of throwing ToolTargetIneligibleException.
    [Test]
    public async Task FindReferences_AllMode_SealedTypeByName_ReturnsCallersWithoutThrowing()
    {
        const string sealedSource = "public sealed class Gadget\n{\n    public void Spin() { }\n}\npublic class Consumer\n{\n    public void Use(Gadget g) { g.Spin(); }\n}";
        var gadgetPath = Path.Combine(_dir, "AmbigProj", "Gadget.cs");
        var csprojPath = Path.Combine(_dir, "AmbigProj", "AmbigProj.csproj");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "AmbigProj",
            csprojPath,
            [("Gadget.cs", sealedSource, gadgetPath)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _impl.FindReferences(
            reason: "sealed type by-name kind all regression: implementations must be skipped, not thrown",
            symbolName: "Gadget",
            kind: FindReferencesKind.all);

        Assert.That(result.IsError, Is.False, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.That(json, Does.Contain("Use"), "Callers of Gadget (Consumer.Use) must be returned");
        Assert.That(json, Does.Contain("Implementations skipped"), "The implementations half must be reported as skipped for a sealed type");
    }
}

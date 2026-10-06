using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("PerformanceEngine")] // sentinel:auto-category
public class PerformanceEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private PerformanceEngine _performanceEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _performanceEngine = new PerformanceEngine(_workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    private Solution CreateSolution(string source, string fileName = "Test.cs")
    {
        var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);
        var docId = DocumentId.CreateNewId(projectId);
        return solution.AddDocument(docId, fileName, SourceText.From(source), filePath: fileName);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task FindBoxingAllocations_ShouldIdentifyBoxing(int id)
    {
        SetSource($"public class C{id} {{ void M() {{ object o = {id}; }} }}", $"C{id}.cs");
        var results = await _performanceEngine.FindBoxingAllocationsAsync(filePath: $"C{id}.cs");
        Assert.That(results.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task DetectInefficientStringComparisons_Should_Flag_ToLower_Equals()
    {
        var source = "public class C { bool IsMatch(string s) => s.ToLower() == \"test\"; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _performanceEngine.DetectInefficientStringComparisonsAsync("C.cs");
        Assert.That(issues.Count, Is.GreaterThan(0));
        Assert.That(issues[0].Description, Contains.Substring("Inefficient string comparison"));
    }

    // Bug 2: PerformanceEngine missing += in loop
    [Test]
    public async Task AnalyzePerformance_FindsPlusAssignInLoop()
    {
        const string src = @"public class Builder
{
    public string Build(string[] items)
    {
        string result = """";
        foreach (var item in items)
        {
            result += item;
        }
        return result;
    }
}";
        SetSource(src, "Builder.cs");
        var issues = await _performanceEngine.AnalyzePerformanceAsync("Builder.cs");
        Assert.That(issues, Has.Some.Matches<PerformanceIssueReport>(r => r?.IssueType == "StringConcatenationInLoop"), "Should detect '+=' string concatenation inside loop");
    }

    // Bug 2: PerformanceEngine missing .ToList()/.ToArray() in loop
    [Test]
    public async Task AnalyzePerformance_FindsToListInLoop()
    {
        const string src = @"using System.Linq;
public class Processor
{
    public void Process(int[][] batches)
    {
        foreach (var batch in batches)
        {
            var list = batch.Where(x => x > 0).ToList();
        }
    }
}";
        SetSource(src, "Processor.cs");
        var issues = await _performanceEngine.AnalyzePerformanceAsync("Processor.cs");
        Assert.That(issues, Has.Some.Matches<PerformanceIssueReport>(r => r?.IssueType == "AllocationInLoop"), "Should detect .ToList() allocation inside loop");
    }
}
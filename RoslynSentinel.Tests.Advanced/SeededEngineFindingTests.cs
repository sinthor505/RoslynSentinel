using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Fixture-based replacements for vacuous real-solution integration tests. Each test seeds a
/// small source file that is known to trigger a finding, then asserts exact results.
/// </summary>
[TestFixture]
public class SeededEngineFindingTests
{
    private IWorkspaceManager _workspaceManager;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [Category("SecurityAndSafetyEngine")]
    [Category("SafetyIssue")]
    public async Task SecurityAndSafetyEngine_UnguardedPublicParam_ReportsMissingNullCheckWithAllFields()
    {
        SetSource("""
            public class Svc
            {
                public int Len(string text) { return text.Length; }
            }
            """);
        var engine = new SecurityAndSafetyEngine(_workspaceManager);

        var issues = await engine.DetectMissingNullChecksAsync("Test.cs");

        Assert.That(issues, Has.Count.EqualTo(1), "Exactly one unguarded parameter is seeded.");
        var issue = issues[0];
        Assert.That(issue.filePath.Absolute, Is.Not.Null.And.Not.Empty);
        Assert.That(issue.Type, Is.EqualTo("MissingNullCheck"));
        Assert.That(issue.Description, Does.Contain("'text'").And.Contain("'Len'"));
        Assert.That(issue.Line, Is.EqualTo(3));
    }

    [Test]
    [Category("SyntaxModernizationEngine")]
    public async Task ImmutabilityEngine_ReadonlyOutput_NoFusedTokens()
    {
        SetSource("""
            public class Holder
            {
                private string name;
                private int count;
                private bool flag;
            }
            """);

        var result = await new SyntaxModernizationEngine(_workspaceManager, new SentinelConfiguration())
            .MakeClassImmutableAsync("Test.cs", "Holder");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        var text = result.UpdatedText!;
        Assert.That(text, Does.Contain("readonly string name"));
        Assert.That(text, Does.Contain("readonly int count"));
        Assert.That(text, Does.Contain("readonly bool flag"));
        Assert.That(text, Does.Not.Contain("readonlystring"), "B02 regression: 'readonly' and 'string' must be space-separated.");
        Assert.That(text, Does.Not.Contain("readonlyint"), "B02 regression: 'readonly' and 'int' must be space-separated.");
        Assert.That(text, Does.Not.Contain("readonlybool"), "B02 regression: 'readonly' and 'bool' must be space-separated.");
    }

    [Test]
    [Category("PerformanceEngine")]
    public async Task PerformanceEngine_StringConcatInLoop_ReportsIssueWithAllFields()
    {
        SetSource("""
            class Builder
            {
                public string Build(string[] items)
                {
                    string htmlStr = "";
                    foreach (var item in items)
                        htmlStr += "x";
                    return htmlStr;
                }
            }
            """);
        var engine = new PerformanceEngine(_workspaceManager);

        var issues = await engine.AnalyzePerformanceAsync("Test.cs");

        Assert.That(issues, Is.Not.Empty);
        foreach (var issue in issues)
        {
            Assert.That(issue.FilePath.Absolute, Is.Not.Null.And.Not.Empty);
            Assert.That(issue.IssueType, Is.Not.Null.And.Not.Empty);
            Assert.That(issue.Description, Is.Not.Null.And.Not.Empty);
        }

        var concat = issues.Where(i => i.IssueType == "StringConcatenationInLoop").ToList();
        Assert.That(concat, Has.Count.EqualTo(1));
        Assert.That(concat[0].Line, Is.EqualTo(7));
    }

    [Test]
    [Category("SecurityEngine")]
    public async Task SecurityEngine_HardcodedPassword_ReportsIssueWithTypeAndDescription()
    {
        SetSource("""
            class Config
            {
                private string password = "hunter2secret";
            }
            """);
        var engine = new SecurityEngine(_workspaceManager);

        var issues = await engine.AnalyzeSecurityAsync("Test.cs");

        Assert.That(issues, Has.Count.EqualTo(1), "Exactly one hardcoded secret is seeded.");
        var issue = issues[0];
        Assert.That(issue.filePath.Absolute, Is.Not.Null.And.Not.Empty);
        Assert.That(issue.IssueType, Is.EqualTo("HardcodedSecret"));
        Assert.That(issue.Description, Does.Contain("'password'"));
        Assert.That(issue.Line, Is.EqualTo(3));
    }

    [Test]
    [Category("AntiPatternEngine")]
    public async Task AnalysisEngine_CallTree_ContainsTransitiveCallees()
    {
        SetSource("""
            class Chain
            {
                public void A() { B(); }
                public void B() { C(); }
                public void C() { }
            }
            """);
        var engine = new AntiPatternEngine(_workspaceManager, new SentinelConfiguration());

        var result = await engine.GenerateCallTreeAsync("Test.cs", "Chain.A");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        var lines = result.UpdatedText!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(lines, Is.EqualTo(new[] { "- void Chain.A()", "- void Chain.B()", "- void Chain.C()" }));
        Assert.That(result.UpdatedText, Does.Contain("  - void Chain.B()"), "B must be indented one level under A.");
        Assert.That(result.UpdatedText, Does.Contain("    - void Chain.C()"), "C must be indented two levels under A.");
    }
}

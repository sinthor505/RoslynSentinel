namespace RoslynSentinel.Tests.ModelEval;

// UnrelatedCodeEquivalenceAssert takes the expected member as a bare snippet; it must be found even
// though a bare method parses as a top-level local function.
[TestFixture]
public class UnrelatedCodeEquivalenceAssertTests
{
    private string _path = null!;

    [SetUp]
    public void SetUp() => _path = Path.Combine(Path.GetTempPath(), $"uce_{Guid.NewGuid():N}.cs");

    [TearDown]
    public void TearDown() => File.Delete(_path);

    [Test]
    public void BareExpectedSnippet_MatchesReformattedMember()
    {
        File.WriteAllText(_path, "namespace N;\npublic class C\n{\n    public string M(int x, int y) => (x + y).ToString();\n}\n");

        Assert.DoesNotThrow(() => UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(
            _path, "M", "public string M( int    x , int y )\n{\n        return (x+y).ToString();\n}"));
    }

    [Test]
    public void BareExpectedSnippet_DetectsChangedLogic()
    {
        File.WriteAllText(_path, "namespace N;\npublic class C\n{\n    public string M(int x, int y) => (x - y).ToString();\n}\n");

        var ex = Assert.Throws<InvalidOperationException>(() => UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(
            _path, "M", "public string M( int    x , int y )\n{\n        return (x+y).ToString();\n}"));
        Assert.That(ex!.Message, Does.Contain("changed structurally"));
    }
}

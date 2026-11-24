using System.Text.Json;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// RunTest `suite`: the fast suite excludes [Category("OutOfProcess")] fixtures through the NUnit adapter's TestCategory
// filter property; the full suite passes the caller's filter through unchanged. Pure string logic, no process is started.
// Not parallelizable: the zero-match tests replace the static ToolCallHint.Current catalog and reset it in TearDown.
[TestFixture]
[NonParallelizable]
public class TestSuiteFilterTests
{
    private string _tempDirectory = "";

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "rs_suite_filter_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallHint.Current = null!;
        Directory.Delete(_tempDirectory, recursive: true);
    }

    // Gives ToolCallHint a catalog that knows RunTest's suite parameter, so the hint text does not depend on which assemblies are loaded.
    private static void UseRunTestCatalog()
    {
        using var runTest = JsonDocument.Parse(
            """{"type":"object","properties":{"reason":{"type":"string"},"suite":{"type":"string","enum":["fast","full"]},"filter":{"type":"string"},"scope":{"type":"string","enum":["file","project","solution"]},"scopeName":{"type":"string"}},"required":["reason"]}""");
        using var search = JsonDocument.Parse(
            """{"type":"object","properties":{"reason":{"type":"string"},"mode":{"type":"string","enum":["text","symbol","references","callers","implementations","attribute","all","namespace","class","interface","method","property","struct","record","enum","enum member","constructor","field"]},"query":{"type":"string"}},"required":["reason","mode"]}""");
        ToolCallHint.Current = ToolSignatureCatalog.FromSchemas(
            [new KeyValuePair<string, JsonElement>("RunTest", runTest.RootElement.Clone()),
             new KeyValuePair<string, JsonElement>("Search", search.RootElement.Clone())]);
    }

    [Test]
    public void Compose_FastWithNoFilter_IsTheCategoryExclusionAlone()
    {
        Assert.That(TestSuiteFilter.Compose(null, TestSuite.fast), Is.EqualTo("TestCategory!=OutOfProcess"));
        Assert.That(TestSuiteFilter.Compose("   ", TestSuite.fast), Is.EqualTo("TestCategory!=OutOfProcess"));
    }

    [Test]
    public void Compose_FastWithFilter_WrapsTheFilterInParentheses()
    {
        Assert.That(
            TestSuiteFilter.Compose("FullyQualifiedName~Foo|FullyQualifiedName~Bar", TestSuite.fast),
            Is.EqualTo("(FullyQualifiedName~Foo|FullyQualifiedName~Bar)&TestCategory!=OutOfProcess"));
    }

    [Test]
    public void Compose_Full_PassesTheFilterThroughUnchanged()
    {
        Assert.That(TestSuiteFilter.Compose("FullyQualifiedName~Foo", TestSuite.full), Is.EqualTo("FullyQualifiedName~Foo"));
        Assert.That(TestSuiteFilter.Compose(null, TestSuite.full), Is.Null);
    }

    [Test]
    public void ZeroMatchHint_FastWithFilterAndZeroTests_SaysNothingWasVerifiedAndNamesTheNameCheck()
    {
        UseRunTestCatalog();

        var hint = TestSuiteFilter.ZeroMatchHint(TestSuite.fast, "FullyQualifiedName~GitToolsSmoke", totalCount: 0);

        Assert.That(hint, Is.Not.Null);
        Assert.That(hint, Does.Contain("nothing was verified").And.Contain("Search("));
        Assert.That(hint, Does.Contain("OutOfProcess").And.Contain("suite: full"));
        Assert.That(hint!.IndexOf("nothing was verified", StringComparison.Ordinal),
            Is.LessThan(hint.IndexOf("OutOfProcess", StringComparison.Ordinal)));
    }

    [Test]
    public void ZeroMatchHint_FullSuiteWithFilterAndZeroTests_SaysNothingWasVerifiedWithoutTheOutOfProcessClause()
    {
        UseRunTestCatalog();

        var hint = TestSuiteFilter.ZeroMatchHint(TestSuite.full, "FullyQualifiedName~GitToolsSmoke", totalCount: 0);

        Assert.That(hint, Does.Contain("nothing was verified"));
        Assert.That(hint, Does.Not.Contain("OutOfProcess"));
    }

    [Test]
    public void ZeroMatchHint_NoFilterOrSomeTestsMatched_IsNull()
    {
        UseRunTestCatalog();

        Assert.That(TestSuiteFilter.ZeroMatchHint(TestSuite.fast, null, totalCount: 0), Is.Null, "no filter given");
        Assert.That(TestSuiteFilter.ZeroMatchHint(TestSuite.fast, "FullyQualifiedName~Foo", totalCount: 3), Is.Null, "tests matched");
    }

    [Test]
    public void ZeroMatchHint_FastWithScopedFilterAndZeroTests_FullRunHintCarriesFilterScopeAndScopeName()
    {
        UseRunTestCatalog();

        var hint = TestSuiteFilter.ZeroMatchHint(TestSuite.fast, "FullyQualifiedName~Foo", totalCount: 0, ToolScope.project, "P.Tests");

        Assert.That(hint, Is.Not.Null);
        Assert.That(hint, Does.Contain("suite: full").And.Contain("FullyQualifiedName~Foo").And.Contain("scope: project").And.Contain("scopeName: \"P.Tests\""));
    }

    [Test]
    public void ZeroMatchHint_FastWithFilterAndDefaultScope_FullRunHintCarriesFilterButNoScopeName()
    {
        UseRunTestCatalog();

        var hint = TestSuiteFilter.ZeroMatchHint(TestSuite.fast, "FullyQualifiedName~Foo", totalCount: 0);

        Assert.That(hint, Does.Contain("suite: full").And.Contain("FullyQualifiedName~Foo"));
        Assert.That(hint, Does.Not.Contain("scopeName"));
    }

    [Test]
    public void AllSkippedNote_FastWithFilterAllSkipped_NamesTheFullRunAndTheOutOfProcessTerm()
    {
        UseRunTestCatalog();

        var note = TestSuiteFilter.AllSkippedNote(TestSuite.fast, "FullyQualifiedName~Budget_", passedCount: 0, failedCount: 0, skippedCount: 15);

        Assert.That(note, Is.Not.Null);
        Assert.That(note, Does.Contain("15 skipped").And.Contain("suite: full").And.Contain("FullyQualifiedName~Budget_"));
        Assert.That(note, Does.Contain(TestSuiteFilter.ExcludeOutOfProcess));
    }

    [Test]
    public void AllSkippedNote_FastWithScopedFilterAllSkipped_FullRunNoteCarriesScopeAndScopeName()
    {
        UseRunTestCatalog();

        var note = TestSuiteFilter.AllSkippedNote(TestSuite.fast, "FullyQualifiedName~Budget_", 0, 0, 15, ToolScope.project, "P.Tests");

        Assert.That(note, Is.Not.Null);
        Assert.That(note, Does.Contain("scope: project").And.Contain("scopeName: \"P.Tests\""));
    }

    [Test]
    public void AllSkippedNote_FullSuite_IsNull()
    {
        Assert.That(TestSuiteFilter.AllSkippedNote(TestSuite.full, "FullyQualifiedName~Budget_", 0, 0, 15), Is.Null, "full suite");
    }

    [Test]
    public void AllSkippedNote_NoFilter_IsNull()
    {
        Assert.That(TestSuiteFilter.AllSkippedNote(TestSuite.fast, null, 0, 0, 15), Is.Null, "no filter given");
        Assert.That(TestSuiteFilter.AllSkippedNote(TestSuite.fast, "   ", 0, 0, 15), Is.Null, "blank filter");
    }

    [Test]
    public void AllSkippedNote_SomeTestsPassed_IsNull()
    {
        Assert.That(TestSuiteFilter.AllSkippedNote(TestSuite.fast, "FullyQualifiedName~Budget_", passedCount: 1, failedCount: 0, skippedCount: 14), Is.Null, "a test passed");
    }

    [Test]
    public void AllSkippedNote_SomeTestsFailed_IsNull()
    {
        Assert.That(TestSuiteFilter.AllSkippedNote(TestSuite.fast, "FullyQualifiedName~Budget_", passedCount: 0, failedCount: 1, skippedCount: 14), Is.Null, "a test failed");
    }

    [Test]
    public void AllSkippedNote_NoSkippedTests_IsNull()
    {
        Assert.That(TestSuiteFilter.AllSkippedNote(TestSuite.fast, "FullyQualifiedName~Budget_", passedCount: 0, failedCount: 0, skippedCount: 0), Is.Null, "nothing skipped");
    }

    [Test]
    public void UsesNUnitAdapter_ReadsTheProjectFile()
    {
        var nunit = Path.Combine(_tempDirectory, "NUnitTests.csproj");
        File.WriteAllText(nunit, "<Project><ItemGroup><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /></ItemGroup></Project>");
        var xunit = Path.Combine(_tempDirectory, "XunitTests.csproj");
        File.WriteAllText(xunit, "<Project><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.0\" /></ItemGroup></Project>");

        Assert.That(TestProjectDetector.UsesNUnitAdapter(nunit), Is.True);
        Assert.That(TestProjectDetector.UsesNUnitAdapter(xunit), Is.False);
        Assert.That(TestProjectDetector.UsesNUnitAdapter(Path.Combine(_tempDirectory, "Missing.csproj")), Is.False);
    }
}

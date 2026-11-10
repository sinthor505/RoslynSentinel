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
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"reason":{"type":"string"},"suite":{"type":"string","enum":["fast","full"]}},"required":["reason"]}""");
        ToolCallHint.Current = ToolSignatureCatalog.FromSchemas(
            [new KeyValuePair<string, JsonElement>("RunTest", schema.RootElement.Clone())]);
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
    public void ZeroMatchHint_FastWithFilterAndZeroTests_ExplainsTheExclusionAndNamesTheFullRun()
    {
        UseRunTestCatalog();

        var hint = TestSuiteFilter.ZeroMatchHint(TestSuite.fast, "FullyQualifiedName~GitToolsSmoke", totalCount: 0);

        Assert.That(hint, Is.Not.Null);
        Assert.That(hint, Does.Contain("fast suite excludes").And.Contain("OutOfProcess"));
        Assert.That(hint, Does.Contain("RunTest(").And.Contain("suite: full"));
    }

    [Test]
    public void ZeroMatchHint_WhenNotFastWithFilterAndZeroTests_IsNull()
    {
        UseRunTestCatalog();

        Assert.That(TestSuiteFilter.ZeroMatchHint(TestSuite.fast, null, totalCount: 0), Is.Null, "no filter given");
        Assert.That(TestSuiteFilter.ZeroMatchHint(TestSuite.fast, "FullyQualifiedName~Foo", totalCount: 3), Is.Null, "tests matched");
        Assert.That(TestSuiteFilter.ZeroMatchHint(TestSuite.full, "FullyQualifiedName~Foo", totalCount: 0), Is.Null, "full suite");
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

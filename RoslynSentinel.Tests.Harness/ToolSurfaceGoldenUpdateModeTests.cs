// ToolSurfaceGolden update mode: a regenerating run writes the files and then fails, and prunes catalog entries no case file references.
// The helper takes its golden folder from [CallerFilePath]; these tests pass a file path inside a temp folder, so nothing is written to the repo.
using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
[NonParallelizable] // sets the process-wide ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN variable
public class ToolSurfaceGoldenUpdateModeTests
{
    private string _root = string.Empty;
    private string _callerFile = string.Empty;
    private string? _savedVariable;

    private string GoldenFolder => Path.Combine(_root, ToolSurfaceGolden.GoldenFolderName);

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rs-golden-update-" + Guid.NewGuid().ToString("N"));
        _callerFile = Path.Combine(_root, "Caller.cs");
        Directory.CreateDirectory(_root);
        _savedVariable = Environment.GetEnvironmentVariable(ToolSurfaceGolden.UpdateEnvironmentVariable);
        Environment.SetEnvironmentVariable(ToolSurfaceGolden.UpdateEnvironmentVariable, "1");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(ToolSurfaceGolden.UpdateEnvironmentVariable, _savedVariable);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Tool[] Tools(params string[] names) =>
        names.Select(name => new Tool { Name = name, Description = "description of " + name }).ToArray();

    private void Update(string caseName, params string[] toolNames)
    {
        Assert.That(
            () => ToolSurfaceGolden.AssertMatchesGolden(caseName, Tools(toolNames), _callerFile),
            Throws.InstanceOf<AssertionException>().With.Message.Contains("regenerated"),
            "update mode must fail the test after writing, so a forgotten variable cannot turn a suite green");
    }

    private Dictionary<string, string> ReadCatalog() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(GoldenFolder, "_catalog.json")))!;

    private string HashOf(string caseName, string toolName) =>
        File.ReadAllLines(Path.Combine(GoldenFolder, caseName + ".txt"))
            .Single(line => line.StartsWith(toolName + "\t", StringComparison.Ordinal))[(toolName.Length + 1)..];

    [Test]
    public void UpdateMode_WritesTheGoldenThenFails_AndTheNextPlainRunPasses()
    {
        Update("case-a", "alpha");

        Assert.That(File.Exists(Path.Combine(GoldenFolder, "case-a.txt")), Is.True);

        Environment.SetEnvironmentVariable(ToolSurfaceGolden.UpdateEnvironmentVariable, null);
        Assert.That(() => ToolSurfaceGolden.AssertMatchesGolden("case-a", Tools("alpha"), _callerFile), Throws.Nothing);
    }

    [Test]
    public void UpdateMode_PrunesCatalogEntriesNoCaseFileReferences()
    {
        Update("case-a", "alpha");
        Update("case-b", "beta");
        var oldAlpha = HashOf("case-a", "alpha");
        var beta = HashOf("case-b", "beta");

        Update("case-a", "alpha2");
        var newAlpha = HashOf("case-a", "alpha2");

        var catalog = ReadCatalog();
        Assert.Multiple(() =>
        {
            Assert.That(catalog.Keys, Does.Not.Contain(oldAlpha), "no case file references the replaced hash any more");
            Assert.That(catalog.Keys, Does.Contain(newAlpha));
            Assert.That(catalog.Keys, Does.Contain(beta), "a run of one case must keep the entries other case files reference");
        });
    }

    [Test]
    public void UpdateMode_SharedHash_SurvivesWhileAnyCaseFileStillReferencesIt()
    {
        Update("case-a", "alpha");
        Update("case-b", "alpha");
        var shared = HashOf("case-a", "alpha");

        Update("case-a", "alpha2");

        Assert.That(ReadCatalog().Keys, Does.Contain(shared), "case-b still references the hash");
    }
}

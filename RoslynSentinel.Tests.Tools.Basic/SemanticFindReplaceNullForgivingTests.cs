using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for SemanticFindReplace operation removeNullForgiving: delete the redundant postfix '!' from every use of one
/// non-nullable property, as one atomic multi-file change through the compile gate. Sources are seeded to disk with
/// CRLF (Core project) and LF (Tests project) line endings, which the edit must preserve.
/// </summary>
[TestFixture]
[Category("SemanticFindReplaceTools")] // sentinel:auto-category
public class SemanticFindReplaceNullForgivingTests
{
    private const string TypesPath = "ContosoOrders.Core/Nf/Types.cs";
    private const string UsagePath = "ContosoOrders.Core/Nf/Usage.cs";
    private const string TestUsagePath = "ContosoOrders.Tests/NfTests/TestUsage.cs";
    private const string CoreProject = "ContosoOrders.Core";
    private const string TestsProject = "ContosoOrders.Tests";

    private const string PersonName = "P:Nf.Person.Name";

    // Name is the target; Child is another non-nullable property, Nick is nullable-annotated (refused), Tag has no '!' use at all.
    // 'null!' on Child is a '!' on a literal and is never a use of Child. Len/Self are the bare and 'this.' forms in the declaring class.
    private static readonly string TypesSource = Crlf("""
        namespace Nf;

        public class Person
        {
            public string Name { get; init; } = "";
            public Person Child { get; init; } = null!;
            public string? Nick { get; init; }
            public string Tag { get; init; } = "";
            public int Len() => Name!.Length;
            public string Self() => this.Name!;
        }

        public class Pet
        {
            public string Name { get; init; } = "";
        }
        """);

    // One call-site shape per method; OtherType uses a different type's Name, which must stay untouched.
    private static readonly string UsageSource = Crlf("""
        namespace Nf;

        public class Usage
        {
            public string Simple(Person x) => x.Name!;
            public int Member(Person x) => x.Name!.Length;
            public string Paren(Person x) => (x.Name!);
            public string? Cond(Person? x) => x?.Name!;
            public string Nested(Person x) => x.Child!.Name!;
            public string Twice(Person a) => a.Name!!;
            public string OtherType(Pet p) => p.Name!;
        }
        """);

    // LF endings on purpose: the Core files above are CRLF.
    private static readonly string TestUsageSource = """
        namespace NfTests;

        public class TestUsage
        {
            public string Get(Nf.Person x) => x.Name!;
        }
        """.Replace("\r\n", "\n");

    private static readonly string ExpectedTypes = TypesSource
        .Replace("Name!.Length", "Name.Length")
        .Replace("this.Name!;", "this.Name;");

    private static readonly string ExpectedUsage = UsageSource
        .Replace("=> x.Name!;", "=> x.Name;")
        .Replace("x.Name!.Length", "x.Name.Length")
        .Replace("(x.Name!)", "(x.Name)")
        .Replace("x?.Name!", "x?.Name")
        .Replace("x.Child!.Name!", "x.Child!.Name")
        .Replace("a.Name!!", "a.Name");

    private static readonly string ExpectedTestUsage = TestUsageSource.Replace("x.Name!", "x.Name");

    private static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private IWorkspaceManager _workspaceManager;
    private ValidationEngine _validationEngine;
    private SemanticFindReplaceTools _tool;
    private TestSolutionFixture? _fixture;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _validationEngine = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager);
        _tool = new SemanticFindReplaceTools(
            new SemanticReplaceEngine(_workspaceManager),
            _validationEngine,
            _workspaceManager,
            NullLogger<SemanticFindReplaceTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
        _fixture = null;
    }

    private async Task SeedAsync()
    {
        _fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
        await _fixture.AddFileToSolution(_workspaceManager, TypesPath, TypesSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, UsagePath, UsageSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, TestUsagePath, TestUsageSource, reloadSolution: true);
    }

    private string ReadSeeded(string relativePath) =>
        File.ReadAllText(Path.Combine(_fixture!.SolutionDirectory, relativePath));

    private Task<SentinelCallToolResult<object>> RunAsync(
        SemanticReplaceMode mode,
        string symbol,
        string? projectFilter = null,
        string? fileFilter = null,
        string? newName = null,
        string? factory = null) =>
        _tool.SemanticFindReplaceTool(
            new ToolCallReason("testing removeNullForgiving"),
            SemanticReplaceOperation.removeNullForgiving,
            symbol,
            mode,
            newName: newName,
            factory: factory,
            projectFilter: projectFilter,
            fileFilter: fileFilter);

    private static JsonElement Data(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return JsonSerializer.SerializeToElement(result.SuccessData);
    }

    private static JsonElement FindSite(JsonElement data, string fileName, string before)
    {
        foreach (var site in data.GetProperty("sites").EnumerateArray())
        {
            if (Path.GetFileName(site.GetProperty("FilePath").GetString()) == fileName
                && site.GetProperty("Before").GetString() == before)
            {
                return site;
            }
        }

        Assert.Fail($"No site in {fileName} with before text '{before}'. Sites: {data.GetProperty("sites")}");
        return default;
    }

    private void AssertSeedUnchanged()
    {
        Assert.That(ReadSeeded(TypesPath), Is.EqualTo(TypesSource));
        Assert.That(ReadSeeded(UsagePath), Is.EqualTo(UsageSource));
        Assert.That(ReadSeeded(TestUsagePath), Is.EqualTo(TestUsageSource));
    }

    /// <summary>
    /// Lists the compiler errors of the whole loaded solution as 'id message (file name)' (no line numbers, so an edit that moves
    /// code does not change an entry), to prove an apply introduced none. The sample solution has errors of its own (for example
    /// unrestored test packages), so the check is on the difference, not on zero.
    /// </summary>
    private async Task<HashSet<string>> CompileErrorsAsync()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var errors = new HashSet<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            foreach (var diagnostic in compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                errors.Add($"{diagnostic.Id} {diagnostic.GetMessage()} ({Path.GetFileName(diagnostic.Location.SourceTree?.FilePath)})");
            }
        }

        return errors;
    }

    [Test]
    [Description("Preview lists the sites with before/after text, issues no changeId and writes nothing")]
    public async Task RemoveNullForgiving_Preview_ListsSitesWithoutChangeIdAndWritesNothing()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.preview, PersonName));

        Assert.That(data.GetProperty("mode").GetString(), Is.EqualTo("preview"));
        Assert.That(data.TryGetProperty("changeId", out _), Is.False);
        Assert.That(data.GetProperty("sites").GetArrayLength(), Is.EqualTo(10), "2 in Types.cs + 7 in Usage.cs (Twice has two) + 1 in TestUsage.cs");
        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(3));

        var cond = FindSite(data, "Usage.cs", "x?.Name!");
        Assert.That(cond.GetProperty("After").GetString(), Is.EqualTo("x?.Name"));
        Assert.That(cond.GetProperty("Line").GetInt32(), Is.GreaterThan(0));
        Assert.That(FindSite(data, "Types.cs", "this.Name!").GetProperty("After").GetString(), Is.EqualTo("this.Name"));
        Assert.That(FindSite(data, "TestUsage.cs", "x.Name!").GetProperty("After").GetString(), Is.EqualTo("x.Name"));
        Assert.That(FindSite(data, "Usage.cs", "x.Child!.Name!").GetProperty("After").GetString(), Is.EqualTo("x.Child!.Name"));

        foreach (var site in data.GetProperty("sites").EnumerateArray())
        {
            Assert.That(site.GetProperty("Before").GetString(), Does.Not.StartWith("p."), "Pet.Name is a different symbol");
        }

        AssertSeedUnchanged();
    }

    [Test]
    [Description("Apply across two projects is one atomic change: bangs gone, line endings and everything else untouched, no new compile error")]
    public async Task RemoveNullForgiving_Apply_MultiFile_RemovesBangsKeepsEverythingElseAndStillCompiles()
    {
        await SeedAsync();
        var errorsBefore = await CompileErrorsAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, PersonName));

        Assert.That(data.GetProperty("mode").GetString(), Is.EqualTo("apply"));
        Assert.That(data.GetProperty("changeId").GetString(), Is.Not.Empty);
        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(3));
        Assert.That(data.GetProperty("sites").GetArrayLength(), Is.EqualTo(10));

        Assert.That(ExpectedUsage, Is.Not.EqualTo(UsageSource), "Fixture sanity: the expected text differs from the source");
        Assert.That(ReadSeeded(TypesPath), Is.EqualTo(ExpectedTypes));
        Assert.That(ReadSeeded(UsagePath), Is.EqualTo(ExpectedUsage));
        Assert.That(ReadSeeded(TestUsagePath), Is.EqualTo(ExpectedTestUsage));

        // Line endings survive: the Core files stay pure CRLF and the Tests file stays pure LF.
        Assert.That(Regex.IsMatch(ReadSeeded(TypesPath), "(?<!\r)\n"), Is.False);
        Assert.That(Regex.IsMatch(ReadSeeded(UsagePath), "(?<!\r)\n"), Is.False);
        Assert.That(ReadSeeded(TestUsagePath), Does.Not.Contain("\r"));

        // Pet.Name keeps its '!' and the 'null!' initializer is untouched.
        Assert.That(ReadSeeded(UsagePath), Does.Contain("p.Name!"));
        Assert.That(ReadSeeded(TypesPath), Does.Contain("= null!;"));

        var errorsAfter = await CompileErrorsAsync();
        Assert.That(errorsAfter.Except(errorsBefore), Is.Empty, "The apply must not introduce compiler errors");
    }

    [Test]
    [Description("Apply with no matching expression writes nothing and issues no changeId")]
    public async Task RemoveNullForgiving_ApplyWithZeroSites_ReportsNothingToChangeWithoutChangeId()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, "P:Nf.Person.Tag"));

        Assert.That(data.GetProperty("mode").GetString(), Is.EqualTo("apply"));
        Assert.That(data.TryGetProperty("changeId", out _), Is.False);
        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(0));
        Assert.That(data.GetProperty("sites").GetArrayLength(), Is.EqualTo(0));
        Assert.That(data.GetProperty("message").GetString(), Does.Contain("nothing was written"));
        AssertSeedUnchanged();
    }

    [Test]
    [Description("Preview with no matching expression is a valid empty result")]
    public async Task RemoveNullForgiving_PreviewWithZeroSites_ReturnsEmptySites()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.preview, "P:Nf.Person.Tag"));

        Assert.That(data.GetProperty("sites").GetArrayLength(), Is.EqualTo(0));
        Assert.That(data.TryGetProperty("changeId", out _), Is.False);
        AssertSeedUnchanged();
    }

    [Test]
    [Description("projectFilter limits the apply to the named project")]
    public async Task RemoveNullForgiving_ProjectFilter_OnlyChangesNamedProject()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, PersonName, projectFilter: TestsProject));

        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(1));
        Assert.That(data.GetProperty("changeId").GetString(), Is.Not.Empty);
        Assert.That(ReadSeeded(TestUsagePath), Is.EqualTo(ExpectedTestUsage));
        Assert.That(ReadSeeded(TypesPath), Is.EqualTo(TypesSource), "Sites in other projects are untouched");
        Assert.That(ReadSeeded(UsagePath), Is.EqualTo(UsageSource));
    }

    [TestCase("ContosoOrders.Core/Nf/Usage.cs", TestName = "RemoveNullForgiving_FileFilter_SolutionRelativePath_OnlyChangesThatFile")]
    [TestCase("Usage.cs", TestName = "RemoveNullForgiving_FileFilter_BareFileName_OnlyChangesThatFile")]
    [TestCase("**/Nf/Usage.cs", TestName = "RemoveNullForgiving_FileFilter_DoubleStar_OnlyChangesThatFile")]
    public async Task RemoveNullForgiving_FileFilter_OnlyChangesMatchingFile(string fileFilter)
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, PersonName, fileFilter: fileFilter));

        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(1));
        Assert.That(data.GetProperty("sites").GetArrayLength(), Is.EqualTo(7));
        Assert.That(ReadSeeded(UsagePath), Is.EqualTo(ExpectedUsage));
        Assert.That(ReadSeeded(TypesPath), Is.EqualTo(TypesSource), "Files outside the filter are untouched");
        Assert.That(ReadSeeded(TestUsagePath), Is.EqualTo(TestUsageSource));
    }

    [Test]
    [Description("projectFilter and fileFilter together are intersected; an empty intersection is a valid zero-site apply")]
    public async Task RemoveNullForgiving_ProjectAndFileFilter_AreIntersected()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, PersonName, projectFilter: CoreProject, fileFilter: "TestUsage.cs"));

        Assert.That(data.TryGetProperty("changeId", out _), Is.False);
        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(0));
        AssertSeedUnchanged();
    }

    [Test]
    [Description("A nullable-annotated target is refused, naming the symbol, and nothing is written")]
    public async Task RemoveNullForgiving_NullableAnnotatedTarget_IsRefusedAndWritesNothing()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.apply, "P:Nf.Person.Nick");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData.Message, Does.Contain("Nick"));
        AssertSeedUnchanged();
    }

    [Test]
    [Description("A method symbol is refused as a non-member target")]
    public async Task RemoveNullForgiving_MethodSymbol_IsRefusedAndWritesNothing()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.apply, "M:Nf.Person.Len");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("symbol"));
        AssertSeedUnchanged();
    }

    [Test]
    [Description("An unknown symbol returns NotFound naming the symbol parameter")]
    public async Task RemoveNullForgiving_UnknownSymbol_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.preview, "P:Nf.Person.Nope");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("symbol"));
        AssertSeedUnchanged();
    }

    [Test]
    [Description("An unknown projectFilter is refused naming the parameter and the available projects")]
    public async Task RemoveNullForgiving_UnknownProjectFilter_ReturnsInvalidArgument()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.apply, PersonName, projectFilter: "NoSuchProject");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("projectFilter"));
        Assert.That(result.ErrorData.Message, Does.Contain(CoreProject));
        AssertSeedUnchanged();
    }

    [Test]
    [Description("newName, factory and an empty symbol are rejected for removeNullForgiving, naming the parameter")]
    public async Task RemoveNullForgiving_ParameterValidation_RejectsForeignAndMissingParameters()
    {
        var withNewName = await RunAsync(SemanticReplaceMode.preview, PersonName, newName: "Other");
        Assert.That(withNewName.IsError, Is.True);
        Assert.That(withNewName.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(withNewName.ErrorData.Message, Does.Contain("newName"));

        var withFactory = await RunAsync(SemanticReplaceMode.preview, PersonName, factory: "M:Nf.Factories.Create");
        Assert.That(withFactory.IsError, Is.True);
        Assert.That(withFactory.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(withFactory.ErrorData.Message, Does.Contain("factory"));

        var withoutSymbol = await RunAsync(SemanticReplaceMode.preview, "");
        Assert.That(withoutSymbol.IsError, Is.True);
        Assert.That(withoutSymbol.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(withoutSymbol.ErrorData.Message, Does.Contain("symbol"));
    }
}

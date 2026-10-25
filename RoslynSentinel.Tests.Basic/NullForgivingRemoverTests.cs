using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Engine tests for SemanticFindReplace operation removeNullForgiving (<see cref="NullForgivingRemover"/>, reached through
/// <see cref="SemanticReplaceEngine.RemoveNullForgivingAsync"/>): delete the redundant postfix '!' from uses of one non-nullable member.
/// </summary>
[Category("NullForgivingRemover")] // sentinel:auto-category
public class NullForgivingRemoverTests
{
    private const string SolutionFilePath = @"C:\fixture\Test.sln";
    private const string TypesPath = @"C:\fixture\Lib\Types.cs";
    private const string InsidePath = @"C:\fixture\Lib\Inside.cs";
    private const string UsagePath = @"C:\fixture\Lib\Usage.cs";
    private const string SubUsagePath = @"C:\fixture\Lib\Sub\SubUsage.cs";
    private const string AppPath = @"C:\fixture\App\AppUser.cs";

    private const string PersonName = "P:Fx.Person.Name";

    // Child is a different non-nullable property (target for the nested case), Nick is nullable-annotated (refused target).
    // 'null!' on the Child initializer is a '!' on a literal and must never be matched.
    private const string TypesSource = """
        namespace Fx;

        public partial class Person
        {
            public string Name { get; init; } = "";
            public Person Child { get; init; } = null!;
            public string? Nick { get; init; }
        }

        public class Pet
        {
            public string Name { get; init; } = "";
        }
        """;

    // Bare 'Name!' and 'this.Name!' inside the declaring class, plus '(this.Name)!' (a '!' after a parenthesised operand; with an
    // identifier such as '(x.Name)!' the parser reads a cast, so 'this' is used to keep it an expression).
    private const string InsideSource = """
        namespace Fx;

        public partial class Person
        {
            public int Len() => Name!.Length;
            public string Self() => this.Name!;
            public string Group() => (this.Name)!;
        }
        """;

    // One call-site shape per method; OtherType uses a different type's Name, which must stay untouched.
    private const string UsageSource = """
        namespace Fx;

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
        """;

    private const string SubUsageSource = """
        namespace Fx;

        public class SubUsage
        {
            public string Get(Person x) => x.Name!;
        }
        """;

    private const string AppSource = """
        namespace App;

        public class AppUser
        {
            public string Get(Fx.Person x) => x.Name!;
        }
        """;

    private sealed record ProjectSpec(string Name, string[] References, (string Path, string Source)[] Documents);

    private static SemanticReplaceEngine CreateEngine(Solution solution)
    {
        var fakeManager = FakeWorkspaceManager.ReadOnly();
        fakeManager.SetTestSolution(solution);
        return new SemanticReplaceEngine(fakeManager);
    }

    /// <summary>
    /// Builds an in-memory solution (file path C:\fixture\Test.sln, so solution-relative paths read 'Lib/Usage.cs') with nullable
    /// annotations enabled. Every document needs an absolute file path: the engine skips documents without one.
    /// </summary>
    private static Solution CreateSolution(params ProjectSpec[] projects)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), SolutionFilePath));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var ids = projects.ToDictionary(p => p.Name, _ => ProjectId.CreateNewId());
        foreach (var project in projects)
        {
            var id = ids[project.Name];
            solution = solution
                .AddProject(ProjectInfo.Create(id, VersionStamp.Create(), project.Name, project.Name, LanguageNames.CSharp, compilationOptions: options))
                .AddMetadataReference(id, corlib);
            foreach (var reference in project.References)
            {
                solution = solution.AddProjectReference(id, new ProjectReference(ids[reference]));
            }

            foreach (var (path, source) in project.Documents)
            {
                solution = solution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(path), source, filePath: path);
            }
        }

        return solution;
    }

    /// <summary>One project 'Lib' with the type declarations, the declaring-class body and the usage file.</summary>
    private static Solution CreateLibSolution() => CreateSolution(
        new ProjectSpec("Lib", [], [(TypesPath, TypesSource), (InsidePath, InsideSource), (UsagePath, UsageSource)]));

    /// <summary>Project 'Lib' (types, usages, a file in a sub folder) plus project 'App' that references it and uses Person.Name too.</summary>
    private static Solution CreateLibAndAppSolution() => CreateSolution(
        new ProjectSpec("Lib", [], [(TypesPath, TypesSource), (InsidePath, InsideSource), (UsagePath, UsageSource), (SubUsagePath, SubUsageSource)]),
        new ProjectSpec("App", ["Lib"], [(AppPath, AppSource)]));

    private static Task<SemanticReplaceOutcome> RunAsync(Solution solution, string symbol, string? projectFilter = null, string? fileFilter = null) =>
        CreateEngine(solution).RemoveNullForgivingAsync(symbol, projectFilter, fileFilter, CancellationToken.None);

    private static Dictionary<string, string> ChangesByFileName(SemanticReplaceOutcome outcome) =>
        outcome.Changes.ToDictionary(kv => Path.GetFileName((string)kv.Key), kv => kv.Value);

    private static string[] SiteFileNames(SemanticReplaceOutcome outcome) =>
        outcome.Sites.Select(s => Path.GetFileName(s.FilePath)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static void AssertSite(SemanticReplaceOutcome outcome, string fileName, string before, string after, int count = 1)
    {
        var matches = outcome.Sites.Where(s => Path.GetFileName(s.FilePath) == fileName && s.Before == before).ToList();
        Assert.That(matches, Has.Count.EqualTo(count), $"Expected {count} site(s) in {fileName} with before text '{before}'. Sites: {string.Join(" | ", outcome.Sites.Select(s => $"{Path.GetFileName(s.FilePath)}:{s.Line} {s.Before}"))}");
        foreach (var site in matches)
        {
            Assert.That(site.After, Is.EqualTo(after));
            Assert.That(site.Role, Is.EqualTo(SemanticReplaceRole.NullForgiving));
            Assert.That(site.UnsupportedReason, Is.Null);
            Assert.That(site.Line, Is.GreaterThan(0));
        }
    }

    /// <summary>Returns the trimmed text of the first line that declares the given method, so a test can assert one call-site shape.</summary>
    private static string LineOf(string text, string methodName) =>
        text.Split('\n').Select(l => l.Trim()).First(l => l.Contains($" {methodName}("));

    private static readonly string ExpectedUsage = UsageSource
        .Replace("=> x.Name!;", "=> x.Name;")
        .Replace("x.Name!.Length", "x.Name.Length")
        .Replace("(x.Name!)", "(x.Name)")
        .Replace("x?.Name!", "x?.Name")
        .Replace("x.Child!.Name!", "x.Child!.Name")
        .Replace("a.Name!!", "a.Name");

    private static readonly string ExpectedInside = InsideSource
        .Replace("Name!.Length", "Name.Length")
        .Replace("this.Name!;", "this.Name;")
        .Replace("(this.Name)!", "(this.Name)");

    [Test]
    public async Task Plan_AllShapes_RewritesEveryUseOfTheTargetAndNothingElse()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName);

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(ExpectedUsage, Is.Not.EqualTo(UsageSource), "Fixture sanity: the expected text differs from the source");
        var changes = ChangesByFileName(outcome);
        Assert.That(changes.Keys, Is.EquivalentTo(new[] { "Inside.cs", "Usage.cs" }), "Types.cs has no use of Person.Name with '!' (only 'null!' on Child)");
        Assert.That(changes["Usage.cs"], Is.EqualTo(ExpectedUsage));
        Assert.That(changes["Inside.cs"], Is.EqualTo(ExpectedInside));
        Assert.That(outcome.Sites, Has.Count.EqualTo(10), "3 inside the class + 7 in Usage.cs (Twice has two)");
        Assert.That(outcome.Sites.Select(s => s.Role), Has.All.EqualTo(SemanticReplaceRole.NullForgiving));
        Assert.That(outcome.Sites.Select(s => s.FilePath), Is.Ordered.Using<string>(StringComparer.Ordinal));
    }

    [Test]
    public async Task Plan_MemberAccessBang_RemovesOnlyTheBang()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName);

        AssertSite(outcome, "Usage.cs", "x.Name!", "x.Name", count: 3); // Simple, Member and the inner one of Paren
        var changes = ChangesByFileName(outcome);
        Assert.That(LineOf(changes["Usage.cs"], "Simple"), Is.EqualTo("public string Simple(Person x) => x.Name;"));
        Assert.That(LineOf(changes["Usage.cs"], "Member"), Is.EqualTo("public int Member(Person x) => x.Name.Length;"));
    }

    [Test]
    public async Task Plan_BareAndThisAccessInsideDeclaringClass_AreRewritten()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName);

        AssertSite(outcome, "Inside.cs", "Name!", "Name");
        AssertSite(outcome, "Inside.cs", "this.Name!", "this.Name");
        var changes = ChangesByFileName(outcome);
        Assert.That(changes["Inside.cs"], Is.EqualTo(ExpectedInside));
        Assert.That(LineOf(changes["Inside.cs"], "Len"), Is.EqualTo("public int Len() => Name.Length;"));
        Assert.That(LineOf(changes["Inside.cs"], "Self"), Is.EqualTo("public string Self() => this.Name;"));
    }

    [Test]
    [Description("Pins the open risk in the plan: x?.Name! (conditional access wrapping a member binding) must bind to Name")]
    public async Task Plan_ConditionalAccessBang_IsRewritten()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName);

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        AssertSite(outcome, "Usage.cs", "x?.Name!", "x?.Name");
        Assert.That(LineOf(ChangesByFileName(outcome)["Usage.cs"], "Cond"), Is.EqualTo("public string? Cond(Person? x) => x?.Name;"));
    }

    [Test]
    public async Task Plan_ParenthesisedBang_IsRewrittenInsideAndOutsideTheParentheses()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName);

        AssertSite(outcome, "Usage.cs", "x.Name!", "x.Name", count: 3);
        AssertSite(outcome, "Inside.cs", "(this.Name)!", "(this.Name)");
        var changes = ChangesByFileName(outcome);
        Assert.That(LineOf(changes["Usage.cs"], "Paren"), Is.EqualTo("public string Paren(Person x) => (x.Name);"));
        Assert.That(LineOf(changes["Inside.cs"], "Group"), Is.EqualTo("public string Group() => (this.Name);"));
    }

    [Test]
    public async Task Plan_NestedBang_TargetName_OnlyTheBangAfterNameGoes()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName);

        AssertSite(outcome, "Usage.cs", "x.Child!.Name!", "x.Child!.Name");
        Assert.That(LineOf(ChangesByFileName(outcome)["Usage.cs"], "Nested"), Is.EqualTo("public string Nested(Person x) => x.Child!.Name;"));
    }

    [Test]
    public async Task Plan_NestedBang_TargetChild_OnlyTheFirstBangGoes()
    {
        var outcome = await RunAsync(CreateLibSolution(), "P:Fx.Person.Child");

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(outcome.Sites, Has.Count.EqualTo(1), "The 'null!' initializer in Types.cs is a '!' on a literal, not a use of Child");
        AssertSite(outcome, "Usage.cs", "x.Child!", "x.Child");
        var changes = ChangesByFileName(outcome);
        Assert.That(changes.Keys, Is.EquivalentTo(new[] { "Usage.cs" }));
        Assert.That(LineOf(changes["Usage.cs"], "Nested"), Is.EqualTo("public string Nested(Person x) => x.Child.Name!;"));
        Assert.That(changes["Usage.cs"], Is.EqualTo(UsageSource.Replace("x.Child!.Name!", "x.Child.Name!")));
    }

    [Test]
    public async Task Plan_DoubleBang_RemovesBothBangs()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName);

        AssertSite(outcome, "Usage.cs", "a.Name!!", "a.Name!");
        AssertSite(outcome, "Usage.cs", "a.Name!", "a.Name");
        Assert.That(LineOf(ChangesByFileName(outcome)["Usage.cs"], "Twice"), Is.EqualTo("public string Twice(Person a) => a.Name;"));
    }

    [Test]
    public async Task Plan_SameNameOnDifferentType_IsLeftUntouched()
    {
        var personOutcome = await RunAsync(CreateLibSolution(), PersonName);

        Assert.That(personOutcome.Sites.Where(s => s.Before.StartsWith("p.", StringComparison.Ordinal)), Is.Empty, "Pet.Name is a different symbol");
        Assert.That(LineOf(ChangesByFileName(personOutcome)["Usage.cs"], "OtherType"), Is.EqualTo("public string OtherType(Pet p) => p.Name!;"));

        // And the other way round: targeting Pet.Name changes only the Pet use.
        var petOutcome = await RunAsync(CreateLibSolution(), "P:Fx.Pet.Name");

        Assert.That(petOutcome.Error, Is.Null, petOutcome.Error?.Message);
        Assert.That(petOutcome.Sites, Has.Count.EqualTo(1));
        AssertSite(petOutcome, "Usage.cs", "p.Name!", "p.Name");
        Assert.That(ChangesByFileName(petOutcome)["Usage.cs"], Is.EqualTo(UsageSource.Replace("p.Name!", "p.Name")));
    }

    [TestCase("P:Fx.Person.Nick", ToolErrorCode.TargetIneligible, TestName = "Plan_NullableAnnotatedTarget_IsRefused")]
    [TestCase("M:Fx.Person.Len", ToolErrorCode.InvalidArgument, TestName = "Plan_MethodTarget_IsRefused")]
    [TestCase("T:Fx.Person", ToolErrorCode.InvalidArgument, TestName = "Plan_TypeTarget_IsRefused")]
    [TestCase("P:Fx.Person.NoSuchProperty", ToolErrorCode.NotFound, TestName = "Plan_UnknownDocCommentId_ReturnsNotFound")]
    [TestCase("", ToolErrorCode.InvalidArgument, TestName = "Plan_EmptySymbol_IsRefused")]
    public async Task Plan_IneligibleTarget_ReturnsErrorAndWritesNothing(string symbol, string expectedCode)
    {
        var outcome = await RunAsync(CreateLibSolution(), symbol);

        Assert.That(outcome.Error, Is.Not.Null);
        Assert.That(outcome.Error!.Message, Is.Not.Empty);
        Assert.That(outcome.Error.ErrorCode, Is.EqualTo(expectedCode), outcome.Error.Message);
        Assert.That(outcome.Changes, Is.Empty, "A refused operation changes nothing");
        Assert.That(outcome.Sites, Is.Empty);
    }

    [Test]
    public async Task Plan_NoFilter_CoversEveryProject()
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName);

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(ChangesByFileName(outcome).Keys, Is.EquivalentTo(new[] { "AppUser.cs", "Inside.cs", "SubUsage.cs", "Usage.cs" }));
        Assert.That(outcome.Sites, Has.Count.EqualTo(12), "10 (Inside + Usage) + 1 (SubUsage) + 1 (AppUser)");
    }

    [Test]
    public async Task Plan_ProjectFilter_App_OnlyChangesTheAppProject()
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName, projectFilter: "App");

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        var changes = ChangesByFileName(outcome);
        Assert.That(changes.Keys, Is.EquivalentTo(new[] { "AppUser.cs" }));
        Assert.That(changes["AppUser.cs"], Is.EqualTo(AppSource.Replace("x.Name!", "x.Name")));
        Assert.That(outcome.Sites, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Plan_ProjectFilter_Lib_LeavesTheReferencingProjectUntouched()
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName, projectFilter: "Lib");

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(ChangesByFileName(outcome).Keys, Is.EquivalentTo(new[] { "Inside.cs", "SubUsage.cs", "Usage.cs" }));
        Assert.That(outcome.Sites, Has.Count.EqualTo(11));
    }

    [TestCase("Lib;App")]
    [TestCase("app, lib")]
    public async Task Plan_ProjectFilter_SeveralNamesAnyCase_CoversThoseProjects(string projectFilter)
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName, projectFilter: projectFilter);

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(outcome.Sites, Has.Count.EqualTo(12));
    }

    [Test]
    public async Task Plan_ProjectFilter_UnknownProject_ReturnsInvalidArgumentNamingTheParameter()
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName, projectFilter: "NoSuchProject");

        Assert.That(outcome.Error, Is.Not.Null);
        Assert.That(outcome.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(outcome.Error.Message, Does.Contain("projectFilter"));
        Assert.That(outcome.Changes, Is.Empty);
    }

    [TestCase("Usage.cs", new[] { "Usage.cs" }, TestName = "Plan_FileFilter_BareFileName_MatchesOnlyThatName")]
    [TestCase("Lib/Usage.cs", new[] { "Usage.cs" }, TestName = "Plan_FileFilter_SolutionRelativePath_MatchesOnlyThatFile")]
    [TestCase("Lib/Sub/*.cs", new[] { "SubUsage.cs" }, TestName = "Plan_FileFilter_StarWithinAFolder_MatchesOnlyThatFolder")]
    [TestCase("**/SubUsage.cs", new[] { "SubUsage.cs" }, TestName = "Plan_FileFilter_DoubleStarPrefix_MatchesAcrossFolders")]
    [TestCase("Lib/*.cs", new[] { "Inside.cs", "Usage.cs" }, TestName = "Plan_FileFilter_SingleStar_DoesNotCrossFolders")]
    [TestCase("Lib/**", new[] { "Inside.cs", "SubUsage.cs", "Usage.cs" }, TestName = "Plan_FileFilter_TrailingDoubleStar_MatchesTheWholeProjectFolder")]
    [TestCase("lib\\usage.CS", new[] { "Usage.cs" }, TestName = "Plan_FileFilter_BackslashesAndCase_AreNormalised")]
    [TestCase("*.cs", new[] { "AppUser.cs", "Inside.cs", "SubUsage.cs", "Usage.cs" }, TestName = "Plan_FileFilter_BareStarGlob_MatchesFileNameInAnyFolder")]
    public async Task Plan_FileFilter_RestrictsToMatchingFiles(string fileFilter, string[] expectedFiles)
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName, fileFilter: fileFilter);

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(ChangesByFileName(outcome).Keys, Is.EquivalentTo(expectedFiles));
        Assert.That(SiteFileNames(outcome), Is.EqualTo(expectedFiles));
    }

    [Test]
    public async Task Plan_FileFilter_LeavesOtherFilesUntouched()
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName, fileFilter: "Lib/Usage.cs");

        var changes = ChangesByFileName(outcome);
        Assert.That(changes.Keys, Is.EquivalentTo(new[] { "Usage.cs" }));
        Assert.That(changes["Usage.cs"], Is.EqualTo(ExpectedUsage));
        Assert.That(outcome.Sites, Has.Count.EqualTo(7));
    }

    [Test]
    public async Task Plan_ProjectFilterAndFileFilterTogether_AreIntersected()
    {
        var outcome = await RunAsync(CreateLibAndAppSolution(), PersonName, projectFilter: "App", fileFilter: "Lib/Usage.cs");

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(outcome.Sites, Is.Empty);
        Assert.That(outcome.Changes, Is.Empty);
    }

    [Test]
    public async Task Plan_NoUseWithBang_IsAValidEmptyOutcome()
    {
        var solution = CreateSolution(new ProjectSpec("Lib", [], [(TypesPath, TypesSource)]));

        var outcome = await RunAsync(solution, PersonName);

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(outcome.Sites, Is.Empty);
        Assert.That(outcome.Changes, Is.Empty);
    }

    [Test]
    public async Task Plan_FileFilterMatchingNothing_IsAValidEmptyOutcome()
    {
        var outcome = await RunAsync(CreateLibSolution(), PersonName, fileFilter: "NoSuchFile.cs");

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        Assert.That(outcome.Sites, Is.Empty);
        Assert.That(outcome.Changes, Is.Empty);
    }
}

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for <see cref="CopyAndObsoleteChecks.CheckAsync"/>: the facts an accepted file yields and each refusal (same namespace, partial with parts
/// elsewhere, several namespaces or none, destination in no project, non-.cs path, source not in the solution). In-memory only: the paths sit under
/// the scratch root but nothing is written there.
/// </summary>
[TestFixture]
[Category("CopyAndObsoleteChecks")] // sentinel:auto-category
public class CopyAndObsoleteChecksTests
{
    private static readonly string ProjectDirectory = Path.Combine(ScratchRoot.Resolve(), "CopyAndObsoleteChecksTests", "Demo");

    // A project-relative path written with '/' becomes an absolute path with the platform's separators.
    private static string Abs(string relativePath) => Path.Combine(ProjectDirectory, Path.Combine(relativePath.Split('/')));

    // Builds a one-project solution holding the source and any extra files, then checks source -> destination (both project-relative unless absolute).
    private static Task<CopyAndObsoleteCheckResult> CheckAsync(string sourceRelative, string sourceContent, string destination, params (string Relative, string Content)[] others)
    {
        var documents = new[] { (Relative: sourceRelative, Content: sourceContent) }.Concat(others)
            .Select(file => (name: Path.GetFileName(file.Relative), content: file.Content, filePath: Abs(file.Relative)))
            .ToArray();
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Demo", Path.Combine(ProjectDirectory, "Demo.csproj"), documents);
        var destinationPath = Path.IsPathRooted(destination) ? destination : Abs(destination);
        return CopyAndObsoleteChecks.CheckAsync(solution, Abs(sourceRelative), destinationPath);
    }

    // The refusal text carries facts and options, never policy.
    private static void AssertFactsAndOptions(CopyAndObsoleteCheckResult result)
    {
        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.Refusal!.Message, Does.Contain("Options:"));
        Assert.That(result.Refusal.Message, Does.Not.Contain("must").IgnoreCase.And.Not.Contain("stop").IgnoreCase.And.Not.Contain("do not").IgnoreCase);
    }

    [Test]
    public async Task Accepted_ReturnsNamespacesTypesAndStaticFieldTypes()
    {
        const string source = """
            namespace Demo.Models;

            public class Counter
            {
                public static int Total;
            }

            public enum Kind { A, B }

            public interface IThing { }

            public record Point(int X, int Y);

            public struct Plain { public int Value; }

            public delegate void Handler(int value);
            """;

        var result = await CheckAsync("Models/Counter.cs", source, "Other/Counter.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.OldNamespace, Is.EqualTo("Demo.Models"));
        Assert.That(result.NewNamespace, Is.EqualTo("Demo.Other"));
        Assert.That(result.Types, Is.EqualTo(new[]
        {
            new TopLevelType("class", "Counter", true),
            new TopLevelType("enum", "Kind", false),
            new TopLevelType("interface", "IThing", false),
            new TopLevelType("record", "Point", false),
            new TopLevelType("struct", "Plain", false),
            new TopLevelType("delegate", "Handler", false),
        }));
        Assert.That(result.StaticFieldTypeNames, Is.EqualTo(new[] { "Counter" }));
    }

    [Test]
    public async Task Accepted_DestinationInProjectRoot_GetsTheDefaultNamespace()
    {
        var result = await CheckAsync("Models/Foo.cs", "namespace Demo.Models { public class Foo { } }", "Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.NewNamespace, Is.EqualTo("Demo"));
    }

    [Test]
    public async Task Accepted_StaticFieldRules_ConstIsIgnoredAndNestedTypesCount()
    {
        const string source = """
            namespace Demo.Models
            {
                public class ConstOnly { public const int Limit = 3; public int Instance; }
                public class Holder { public class Inner { private static readonly object Gate = new object(); } }
            }
            """;

        var result = await CheckAsync("Models/Types.cs", source, "Other/Types.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.StaticFieldTypeNames, Is.EqualTo(new[] { "Holder" }));
        Assert.That(result.Types.Select(type => type.Name), Is.EqualTo(new[] { "ConstOnly", "Holder" }), "A nested type is copied with its type, not listed on its own.");
    }

    [Test]
    public async Task Accepted_StaticFieldRules_StaticAutoPropertyAndFieldLikeEventCount()
    {
        const string source = """
            namespace Demo.Models
            {
                public class WithAutoProp { public static int Count { get; set; } }
                public class WithEvent { public static event System.EventHandler Changed; }
                public class WithBodyProp { public static int V => 3; }
                public class Plain { public int X { get; set; } }
            }
            """;

        var result = await CheckAsync("Models/Types.cs", source, "Other/Types.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.StaticFieldTypeNames, Is.EqualTo(new[] { "WithAutoProp", "WithEvent" }));
    }

    [Test]
    public async Task Accepted_NestedNamespaceDeclarations_JoinIntoOneOldNamespace()
    {
        const string source = "namespace Demo { namespace Models { public class Foo { } public class Bar { } } }";

        var result = await CheckAsync("Models/Foo.cs", source, "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.OldNamespace, Is.EqualTo("Demo.Models"));
    }

    [Test]
    public async Task Accepted_GlobalNamespaceTypes_HaveAnEmptyOldNamespace()
    {
        var result = await CheckAsync("Foo.cs", "public class Foo { }", "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.OldNamespace, Is.EqualTo(string.Empty));
        Assert.That(result.NewNamespace, Is.EqualTo("Demo.Other"));
    }

    [Test]
    public async Task Accepted_PartialTypeWithAllPartsInTheFile_IsNotRefused()
    {
        const string source = "namespace Demo.Models { public partial class Foo { } public partial class Foo { } }";

        var result = await CheckAsync("Models/Foo.cs", source, "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
    }

    [Test]
    public async Task Refused_SameFolder_NamesTheNamespaceAndTheDestination()
    {
        var destination = Abs("Models/Foo2.cs");

        var result = await CheckAsync("Models/Foo.cs", "namespace Demo.Models { public class Foo { } }", "Models/Foo2.cs");

        AssertFactsAndOptions(result);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.OldNamespace, Is.EqualTo("Demo.Models"));
        Assert.That(result.NewNamespace, Is.EqualTo("Demo.Models"));
        Assert.That(result.Refusal.Message, Does.Contain("'Demo.Models'").And.Contain(destination).And.Contain("namespaceHandling keep"));
    }

    [Test]
    public async Task Refused_PartialTypeWithPartsInAnotherFile_NamesTheOtherFile()
    {
        var result = await CheckAsync(
            "Models/Foo.cs",
            "namespace Demo.Models { public partial class Foo { public int A; } }",
            "Other/Foo.cs",
            ("Models/Foo.Part2.cs", "namespace Demo.Models { public partial class Foo { public int B; } }"));

        AssertFactsAndOptions(result);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.Refusal.Message, Does.Contain("'Foo'").And.Contain("Foo.Part2.cs").And.Contain("partial"));
    }

    [Test]
    public async Task Refused_TypesInSeveralNamespaces_NamesThem()
    {
        const string source = "namespace Alpha { public class X { } } namespace Beta { public class Y { } }";

        var result = await CheckAsync("Models/Two.cs", source, "Other/Two.cs");

        AssertFactsAndOptions(result);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.Refusal.Message, Does.Contain("2 namespaces").And.Contain("'Alpha'").And.Contain("'Beta'").And.Contain("'Demo.Other'"));
    }

    [Test]
    public async Task Refused_FileWithoutATopLevelType_SaysSo()
    {
        var result = await CheckAsync("Models/Empty.cs", "namespace Demo.Models { }", "Other/Empty.cs");

        AssertFactsAndOptions(result);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.Refusal.Message, Does.Contain("no top-level type"));
    }

    [Test]
    public async Task Refused_DestinationInNoLoadedProject_ListsTheSearchedFolders()
    {
        var elsewhere = Path.Combine(ScratchRoot.Resolve(), "CopyAndObsoleteChecksTests", "Elsewhere", "Foo.cs");

        var result = await CheckAsync("Models/Foo.cs", "namespace Demo.Models { public class Foo { } }", elsewhere);

        AssertFactsAndOptions(result);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.Refusal.Message, Does.Contain("inside no loaded project").And.Contain(elsewhere).And.Contain(ProjectDirectory).And.Contain("1 project folder(s)"));
    }

    [Test]
    public async Task Refused_DestinationIsNotACsFile_NamesIt()
    {
        var result = await CheckAsync("Models/Foo.cs", "namespace Demo.Models { public class Foo { } }", "Other/Foo.txt");

        AssertFactsAndOptions(result);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.Refusal.Message, Does.Contain("Foo.txt").And.Contain(".cs file"));
    }

    [Test]
    public async Task Refused_SourceNotInTheSolution_IsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Demo", Path.Combine(ProjectDirectory, "Demo.csproj"), []);

        var result = await CopyAndObsoleteChecks.CheckAsync(solution, Abs("Models/Missing.cs"), Abs("Other/Missing.cs"));

        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.Refusal.Message, Does.Contain("Missing.cs"));
    }
}

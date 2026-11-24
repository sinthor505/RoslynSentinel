using Microsoft.CodeAnalysis.CSharp;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.AmbiguityResolution;

/// <summary>
/// Two callers derive a namespace from folder segments with the same folder rule but different root rules, and tests pin both.
/// The model shares code only where the pinned behaviour allows and comments the deviation.
/// </summary>
internal static class ShareHelperDifferingRulesScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/ShareHelperDifferingRules/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/ShareHelperDifferingRules/";
    private const string InfoPath = CoreFolder + "ProjectInfo.cs";
    private const string StructurePath = CoreFolder + "ProjectStructure.cs";
    private const string ToolsPath = CoreFolder + "FileTools.cs";
    private const string HelperPath = CoreFolder + "FolderNamespace.cs";
    private const string TestsPath = TestFolder + "NamespaceTests.cs";

    private const string InfoSource = """
namespace ContosoOrders.Evals.ShareHelperDifferingRules;

/// <summary>What the two namespace helpers know about a project.</summary>
public sealed record ProjectInfo(string Name, string? DefaultNamespace, string? CsprojText);
""";

    private const string SeedStructureSource = """
using System.Text.RegularExpressions;

namespace ContosoOrders.Evals.ShareHelperDifferingRules;

public static class ProjectStructure
{
    /// <summary>The namespace a file in a project folder is expected to declare.</summary>
    public static string ExpectedNamespace(ProjectInfo project, string relativeFolder)
    {
        return DeriveExpected(project, relativeFolder);
    }

    private static string DeriveExpected(ProjectInfo project, string relativeFolder)
    {
        var root = RootNamespace(project);
        var segments = relativeFolder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace('-', '_').Replace(' ', '_'));
        return string.Join('.', new[] { root }.Concat(segments));
    }

    private static string RootNamespace(ProjectInfo project)
    {
        var match = project.CsprojText is null
            ? null
            : Regex.Match(project.CsprojText, "<RootNamespace>([^<]+)</RootNamespace>");
        return match is { Success: true } ? match.Groups[1].Value.Trim() : project.Name;
    }
}
""";

    private const string GoldenStructureSource = """
using System.Text.RegularExpressions;

namespace ContosoOrders.Evals.ShareHelperDifferingRules;

public static class ProjectStructure
{
    /// <summary>The namespace a file in a project folder is expected to declare.</summary>
    public static string ExpectedNamespace(ProjectInfo project, string relativeFolder)
    {
        return DeriveExpected(project, relativeFolder);
    }

    private static string DeriveExpected(ProjectInfo project, string relativeFolder)
    {
        // The root differs from FileTools.DescribeKept (the csproj RootNamespace here, DefaultNamespace there); only the folder part is shared.
        return FolderNamespace.Expected(RootNamespace(project), relativeFolder);
    }

    private static string RootNamespace(ProjectInfo project)
    {
        var match = project.CsprojText is null
            ? null
            : Regex.Match(project.CsprojText, "<RootNamespace>([^<]+)</RootNamespace>");
        return match is { Success: true } ? match.Groups[1].Value.Trim() : project.Name;
    }
}
""";

    private const string SeedToolsSource = """
namespace ContosoOrders.Evals.ShareHelperDifferingRules;

public static class FileTools
{
    /// <summary>Describes the namespace a moved file keeps.</summary>
    public static string DescribeKept(ProjectInfo project, string relativeFolder)
    {
        var root = project.DefaultNamespace ?? project.Name;
        var segments = relativeFolder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace('-', '_').Replace(' ', '_'));
        var expected = string.Join('.', new[] { root }.Concat(segments));
        return $"namespace kept as {expected}";
    }
}
""";

    private const string GoldenToolsSource = """
namespace ContosoOrders.Evals.ShareHelperDifferingRules;

public static class FileTools
{
    /// <summary>Describes the namespace a moved file keeps.</summary>
    public static string DescribeKept(ProjectInfo project, string relativeFolder)
    {
        var expected = FolderNamespace.Expected(project.DefaultNamespace ?? project.Name, relativeFolder);
        return $"namespace kept as {expected}";
    }
}
""";

    private const string GoldenHelperSource = """
namespace ContosoOrders.Evals.ShareHelperDifferingRules;

/// <summary>Builds the namespace of a file from a root namespace and the folder the file sits in.</summary>
public static class FolderNamespace
{
    public static string Expected(string rootNamespace, string relativeFolder)
    {
        var segments = relativeFolder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace('-', '_').Replace(' ', '_'));
        return string.Join('.', new[] { rootNamespace }.Concat(segments));
    }
}
""";

    private const string TestsSource = """
using ContosoOrders.Evals.ShareHelperDifferingRules;
using Xunit;

namespace ContosoOrders.Tests.Evals.ShareHelperDifferingRules;

public class NamespaceTests
{
    private const string Csproj = "<Project><PropertyGroup><RootNamespace>Contoso.Orders</RootNamespace></PropertyGroup></Project>";

    private static readonly ProjectInfo WithBoth = new("Contoso.Orders", "Contoso.Orders.Core", Csproj);

    [Fact]
    public void ProjectStructure_UsesTheCsprojRootNamespaceAndSanitisesFolders()
    {
        Assert.Equal("Contoso.Orders.Sub_Folder.Models", ProjectStructure.ExpectedNamespace(WithBoth, "Sub-Folder/Models"));
        Assert.Equal("Contoso.Orders", ProjectStructure.ExpectedNamespace(WithBoth, string.Empty));
    }

    [Fact]
    public void ProjectStructure_FallsBackToTheProjectName()
    {
        var project = new ProjectInfo("Contoso.Orders", "Other.Namespace", "<Project />");
        Assert.Equal("Contoso.Orders.Models", ProjectStructure.ExpectedNamespace(project, "Models"));
    }

    [Fact]
    public void FileTools_UsesTheDefaultNamespaceAndSanitisesFolders()
    {
        Assert.Equal("namespace kept as Contoso.Orders.Core.Sub_Folder.Models", FileTools.DescribeKept(WithBoth, "Sub-Folder/Models"));
        Assert.Equal("namespace kept as Contoso.Orders.Core", FileTools.DescribeKept(WithBoth, string.Empty));
    }

    [Fact]
    public void FileTools_FallsBackToTheProjectName()
    {
        var project = new ProjectInfo("Contoso.Orders", null, Csproj);
        Assert.Equal("namespace kept as Contoso.Orders.Models", FileTools.DescribeKept(project, "Models"));
    }
}
""";

    private const string HiddenTestSource = """
using System.Reflection;
using ContosoOrders.Evals.ShareHelperDifferingRules;
using Xunit;

namespace ContosoOrders.Tests.Evals.ShareHelperDifferingRules;

public class ShareHelperDifferingRulesStep1Tests
{
    [Fact]
    public void FolderNamespaceIsAPublicStaticClassWithAnExpectedMethod()
    {
        var type = typeof(ProjectInfo).Assembly.GetType("ContosoOrders.Evals.ShareHelperDifferingRules.FolderNamespace");
        Assert.NotNull(type);
        Assert.True(type!.IsPublic && type.IsAbstract && type.IsSealed, "FolderNamespace must be a public static class");
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Expected");
        Assert.NotNull(method);
        Assert.Equal(typeof(string), method!.ReturnType);
    }

    [Fact]
    public void BothCallersKeepTheirOwnRootRule()
    {
        var project = new ProjectInfo(
            "Contoso.Orders",
            "Contoso.Orders.Core",
            "<Project><PropertyGroup><RootNamespace>Contoso.Orders</RootNamespace></PropertyGroup></Project>");
        Assert.Equal("Contoso.Orders.Models", ProjectStructure.ExpectedNamespace(project, "Models"));
        Assert.Equal("namespace kept as Contoso.Orders.Core.Models", FileTools.DescribeKept(project, "Models"));
    }
}
""";

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/ShareHelperDifferingRules, ProjectStructure.DeriveExpected and FileTools.DescribeKept both build a
namespace from a root namespace plus the project folder a file sits in. Add a new `public static class FolderNamespace` with a
public static method `Expected(...)` (you choose its parameters) in a new file of the same folder, and make both callers use
it wherever their pinned behaviour allows. The tests in ContosoOrders.Tests/Evals/ShareHelperDifferingRules/NamespaceTests.cs
pin the current output of both callers and must keep passing unchanged. If the two callers do not agree on everything, do not
change the behaviour of either one: share only what they agree on, and leave a comment where they deviate that says so. Edit
no test file.
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "ShareHelperDifferingRules",
        "Share a helper between two callers whose pinned rules differ in one part, and comment the deviation",
        "ambiguity",
        "ModelEval-Scenario-Ideas 16 step 1, commit 3c7ea6a1",
        new Dictionary<string, string>
        {
            [InfoPath] = InfoSource,
            [StructurePath] = SeedStructureSource,
            [ToolsPath] = SeedToolsSource,
            [TestsPath] = TestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [StructurePath] = GoldenStructureSource,
                    [ToolsPath] = GoldenToolsSource,
                    [HelperPath] = GoldenHelperSource,
                },
                AllowedPaths: new List<string> { CoreFolder },
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckSharing(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "112");

    /// <summary>
    /// Keyword check, to be refined after live runs: both callers use FolderNamespace and no longer carry the folder-sanitising code,
    /// the project info and the tests are untouched, and some comment in the folder says the root rules differ.
    /// </summary>
    private static string? CheckSharing(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in new[] { InfoPath, TestsPath })
        {
            if (!ctx.IsUnchanged(path))
            {
                return $"changed but must stay as it was: {path}";
            }
        }

        foreach (var path in new[] { StructurePath, ToolsPath })
        {
            if (!ctx.Syntax(path).GetRoot().DescendantTokens().Any(token => token.ValueText == "FolderNamespace"))
            {
                return $"{path} does not use FolderNamespace";
            }

            if (ctx.ReadText(path).Contains("Replace('-', '_')"))
            {
                return $"{path} still carries its own copy of the folder-segment sanitising code";
            }
        }

        var comments = Directory.GetFiles(ctx.FullPath(CoreFolder), "*.cs")
            .SelectMany(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantTrivia())
            .Where(trivia => trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
            .Select(trivia => trivia.ToString())
            .ToList();
        var mentionsDeviation = comments.Any(comment =>
            (comment.Contains("RootNamespace", StringComparison.Ordinal) && comment.Contains("DefaultNamespace", StringComparison.Ordinal))
            || (comment.Contains("root", StringComparison.OrdinalIgnoreCase)
                && (comment.Contains("differ", StringComparison.OrdinalIgnoreCase)
                    || comment.Contains("deviat", StringComparison.OrdinalIgnoreCase))));
        if (!mentionsDeviation)
        {
            return "no comment in the folder says that the two callers use different root namespace rules";
        }

        return null;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

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
    private const string RootRuleTestsPath = TestFolder + "RootRuleTests.cs";
    private const string FolderNamespaceTestsPath = TestFolder + "FolderNamespaceTests.cs";

    private const string HelperTypeName = "ContosoOrders.Evals.ShareHelperDifferingRules.FolderNamespace";

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

    // Visible. Both root rules are pinned on a padded root: ProjectStructure trims the csproj RootNamespace, FileTools keeps DefaultNamespace as it is. Green on the seed.
    private const string RootRuleTestsSource = """
using ContosoOrders.Evals.ShareHelperDifferingRules;
using Xunit;

namespace ContosoOrders.Tests.Evals.ShareHelperDifferingRules;

public class RootRuleTests
{
    [Fact]
    public void ProjectStructure_TrimsThePaddedCsprojRootNamespace()
    {
        var project = new ProjectInfo(
            "Contoso.Orders",
            "Contoso.Orders.Core",
            "<Project><PropertyGroup><RootNamespace>  Contoso.Orders  </RootNamespace></PropertyGroup></Project>");
        Assert.Equal("Contoso.Orders.Models", ProjectStructure.ExpectedNamespace(project, "Models"));
    }

    [Fact]
    public void FileTools_KeepsThePaddedDefaultNamespaceAsItIs()
    {
        var project = new ProjectInfo("Contoso.Orders", "  Contoso.Orders.Core  ", "<Project />");
        Assert.Equal("namespace kept as   Contoso.Orders.Core  .Models", FileTools.DescribeKept(project, "Models"));
    }
}
""";

    // Visible; compiles on the seed (reflection), so the first test is red until FolderNamespace exists.
    private const string FolderNamespaceTestsSource = """
using System.Reflection;
using ContosoOrders.Evals.ShareHelperDifferingRules;
using Xunit;

namespace ContosoOrders.Tests.Evals.ShareHelperDifferingRules;

public class FolderNamespaceTests
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

    private const string CannedReport = "DONE: FolderNamespace.Expected holds the shared folder-segment code; ProjectStructure.DeriveExpected and FileTools.DescribeKept both call it, and a comment in ProjectStructure says where the root rules differ.";

    // Expression-bodied on purpose: a static readonly field would render before the contract placement is known (initialisation order).
    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/ShareHelperDifferingRules, `ProjectStructure.DeriveExpected` and `FileTools.DescribeKept` " +
            "both build a namespace from a root namespace plus the project folder a file sits in. " +
            "Add a new `public static class FolderNamespace` with a public static method `Expected(...)` (you choose its parameters) " +
            "in a new file of the same folder, and make both callers use it for whatever they compute identically.",
        Files: "A new file in ContosoOrders.Core/Evals/ShareHelperDifferingRules, plus ProjectStructure.cs and FileTools.cs. No test file.",
        Conditions: "NamespaceTests and RootRuleTests pin the current output of both callers and pass unchanged. " +
            "Where the two callers differ, each keeps its own behaviour exactly.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including NamespaceTests, RootRuleTests and FolderNamespaceTests; " +
            "both callers call `FolderNamespace.Expected`; a comment where the callers deviate says so."));

    // The contract placement is read once per process, when this Definition initialises (StepPrompt renders then); one process runs one arm.
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
            [RootRuleTestsPath] = RootRuleTestsSource,
            [FolderNamespaceTestsPath] = FolderNamespaceTestsSource,
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
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckSharing(ctx)),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "NamespaceTests", "RootRuleTests", "FolderNamespaceTests" }),
        },
        PreferredHost: "112",
        PrimaryAxis: EvalAxis.AmbiguityResolution);

    /// <summary>
    /// ProjectInfo.cs is untouched; ProjectStructure.DeriveExpected and FileTools.DescribeKept each contain an invocation whose symbol is
    /// FolderNamespace.Expected (compiled symbols, not text); and a comment exists in some file of the folder that the seed did not have
    /// (R15: the brief asks for "a comment where the callers deviate", but the position is not graded and no symbol can see a comment, so
    /// any new comment text in any file of the folder counts, a doc comment on the new helper included). The tests decide the rest:
    /// NamespaceTests and RootRuleTests pin each caller's own root rule (padded roots), FolderNamespaceTests the helper's shape.
    /// </summary>
    private static string? CheckSharing(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.IsUnchanged(InfoPath))
        {
            return $"changed but must stay as it was: {InfoPath}";
        }

        var compilation = ctx.Compile(CoreFolder.TrimEnd('/'));
        var helper = compilation.Type(HelperTypeName);
        if (helper is null)
        {
            return "the type FolderNamespace is missing from ContosoOrders.Core/Evals/ShareHelperDifferingRules";
        }

        foreach (var (path, typeName, methodName) in new[] { (StructurePath, "ProjectStructure", "DeriveExpected"), (ToolsPath, "FileTools", "DescribeKept") })
        {
            if (!ctx.Exists(path))
            {
                return $"{path} is missing";
            }

            var method = compilation.ModelFor(path).SyntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(declaration => declaration.Identifier.ValueText == methodName);
            if (method is null)
            {
                return $"{typeName}.{methodName} is missing from {path}";
            }

            var callsHelper = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                compilation.SymbolOf(call) is IMethodSymbol { Name: "Expected" } called
                && SymbolEqualityComparer.Default.Equals(called.ContainingType, helper));
            if (!callsHelper)
            {
                return $"{typeName}.{methodName} does not call FolderNamespace.Expected";
            }
        }

        foreach (var file in Directory.EnumerateFiles(ctx.FullPath(CoreFolder), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(ctx.SolutionDirectory, file).Replace('\\', '/');
            var seedComments = ctx.SeedFiles.TryGetValue(relative, out var seedText)
                ? CommentTexts(CSharpSyntaxTree.ParseText(seedText))
                : new HashSet<string>();
            if (CommentTexts(CSharpSyntaxTree.ParseText(File.ReadAllText(file))).Any(text => !seedComments.Contains(text)))
            {
                return null;
            }
        }

        return "no comment in the folder is new: the brief asks for a comment where the callers deviate";
    }

    // The whitespace-normalised text of every comment in a tree (line, block and doc comments); no symbol can see a comment.
    private static HashSet<string> CommentTexts(SyntaxTree tree) =>
        tree.GetRoot().DescendantTrivia()
            .Where(trivia => trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
            .Select(trivia => string.Join(" ", trivia.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The executable outcome table (Spec S13): each variant is an overlay on step 1 (golden) with a DONE: final message. Every
    /// non-passing row claims DONE, so its class is SilentIncomplete.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // Edits are written with \n; the template's line endings are kept. Throws when a template no longer contains the text to
        // edit, so a template change cannot silently turn a variant into the golden.
        static string Edited(string text, string oldText, string newText)
        {
            var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!normalized.Contains(oldText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"ShareHelperDifferingRules variant edit: '{oldText}' is not in the template.");
            }

            return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
        }

        const string Done = "DONE: shared the folder-segment code through FolderNamespace.Expected.";

        // A deviation comment without any of the words the old keyword check looked for (13.2).
        var commentWithoutKeywords = Edited(
            GoldenStructureSource,
            "// The root differs from FileTools.DescribeKept (the csproj RootNamespace here, DefaultNamespace there); only the folder part is shared.",
            "// The two callers choose the namespace base differently; only the folder part is shared.");

        // The helper trims the root for both callers: FileTools now trims a padded DefaultNamespace, which RootRuleTests pins (13.5).
        var helperTrims = Edited(GoldenHelperSource, "new[] { rootNamespace }", "new[] { rootNamespace.Trim() }");

        return new[]
        {
            new ScenarioVariant(
                Name: "CommentWithoutKeywords",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [StructurePath] = commentWithoutKeywords },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "HelperTrimsForBoth",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [HelperPath] = helperTrims },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "check tests failed"),
            new ScenarioVariant(
                Name: "OnlyOneCallerUsesHelper",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ToolsPath] = SeedToolsSource },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "FileTools.DescribeKept does not call FolderNamespace.Expected"),
        };
    }
}

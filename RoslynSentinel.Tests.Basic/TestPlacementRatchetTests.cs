using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for test placement. A test class whose identifier tokens name no type declared in the Advanced subject projects is a
// core-subject class and belongs in Tests.Basic (engine subject) or Tests.Tools.Basic (tool subject). The Advanced test projects
// may keep core-subject classes only when they are listed in docs/known-misplaced-core-tests.txt; that baseline may only shrink.
[TestFixture]
[Category("Ratchet")]
[Category("TestPlacementRatchet")]
public class TestPlacementRatchetTests
{
    private const string BaselineRelativePath = "docs/known-misplaced-core-tests.txt";
    private const string UpdateEnvVar = "ROSLYNSENTINEL_UPDATE_PLACEMENT_BASELINE";
    private const string BasicDestination = "RoslynSentinel.Tests.Basic";
    private const string ToolsDestination = "RoslynSentinel.Tests.Tools.Basic";
    private const string FactoryProject = "RoslynSentinel.Tests.Tools.Advanced";
    private const string FactoryFile = "BatteryToolFactory.cs";

    private static readonly string[] AdvancedSourceProjects =
    {
        "RoslynSentinel.Engines.Advanced",
        "RoslynSentinel.Tools.Advanced",
        "RoslynSentinel.Server.Advanced",
        "RoslynSentinel.Tests.Support.Advanced",
    };

    private static readonly string[] ScannedProjects =
    {
        "RoslynSentinel.Tests.Advanced",
        "RoslynSentinel.Tests.Tools.Advanced",
        "RoslynSentinel.Tests.Battery.Advanced",
        "RoslynSentinel.Tests.Asyncify",
    };

    private static readonly string[] TestMethodAttributes = { "Test", "TestCase", "TestCaseSource", "Theory" };
    private static readonly string[] ToolsNameSuffixes = { "Tools", "Impl" };
    private static readonly string[] ToolsNames = { "BatteryToolFactory", "TestTools" };

    [Test]
    public void AdvancedTestProjects_HoldNoNewCoreSubjectClasses()
    {
        var repo = RepoRootLocator.Find(AppContext.BaseDirectory);
        var advanced = CollectAdvancedSymbols(repo);
        Assert.That(advanced, Is.Not.Empty, "The Advanced symbol set is empty; the Advanced source projects were not scanned.");

        var found = ScanScannedProjects(repo, advanced);
        var baselinePath = Path.Combine(repo, BaselineRelativePath);

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            WriteBaseline(baselinePath, found);
            var perDestination = found.Values.GroupBy(destination => destination, StringComparer.Ordinal)
                .Select(group => $"{group.Key}={group.Count()}");
            TestContext.Out.WriteLine($"baseline lines: {found.Count}; per destination: {string.Join(", ", perDestination)}");
            return;
        }

        var baseline = LoadBaseline(baselinePath);
        var problems = found.Keys
            .Where(key => !baseline.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => $"NEW core-subject test class {key} (suggested destination {found[key]}): place it in Tests.Basic (engine subject) or Tests.Tools.Basic (tool subject).")
            .ToList();
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));

        var stale = baseline.Keys
            .Where(key => !found.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        if (stale.Count > 0)
        {
            Assert.Warn(
                $"lower the baseline: remove line(s) {string.Join("; ", stale)} from {BaselineRelativePath} (the ratchet only shrinks); new line count {found.Count}");
        }
    }

    [Test]
    public void Classify_AdvancedNamedTypeIsNotCoreSubject()
    {
        const string source = """
            using NUnit.Framework;

            [TestFixture]
            public class AdvancedThingTests
            {
                [Test]
                public void Runs()
                {
                    var subject = new AdvancedThing();
                }
            }
            """;

        var result = Classify(source, new HashSet<string> { "AdvancedThing" });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CoreSubject, Is.False);
    }

    [Test]
    public void Classify_EngineBasicOnlyIsCoreSubjectForTestsBasic()
    {
        const string source = """
            using NUnit.Framework;
            using RoslynSentinel.Engines.Basic;
            using RoslynSentinel.Tools.Basic;

            [TestFixture]
            public class BasicEngineTests
            {
                [Test]
                public void Runs()
                {
                    var subject = new BasicEngine();
                }
            }
            """;

        var result = Classify(source, new HashSet<string> { "AdvancedThing" });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].CoreSubject, Is.True);
            Assert.That(result[0].Destination, Is.EqualTo(BasicDestination), "using directives never decide the destination");
        });
    }

    [Test]
    public void Classify_ToolsNamedTypeSelectsToolsDestination()
    {
        const string source = """
            using NUnit.Framework;
            using RoslynSentinel.Tools.Basic;

            [TestFixture]
            public class ToolSubjectTests
            {
                [Test]
                public void Runs()
                {
                    var tools = new CoreRefactoringTools();
                }
            }
            """;

        var result = Classify(source, new HashSet<string> { "AdvancedThing" });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].CoreSubject, Is.True);
            Assert.That(result[0].Destination, Is.EqualTo(ToolsDestination));
        });
    }

    [Test]
    public void Classify_StringAndCommentNamingAdvancedTypeDoNotCount()
    {
        const string source = """
            using NUnit.Framework;

            [TestFixture]
            [Category("AdvancedThing")]
            public class CategoryNamedTests
            {
                // AdvancedThing is mentioned only in this comment.
                [Test]
                public void Runs()
                {
                    var label = "AdvancedThing";
                }
            }
            """;

        var result = Classify(source, new HashSet<string> { "AdvancedThing" });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CoreSubject, Is.True);
    }

    [Test]
    public void Classify_ClassWithoutTestAttributeIsSkipped()
    {
        const string source = """
            public class PlainHelper
            {
                public void Make()
                {
                    var subject = new AdvancedThing();
                }
            }
            """;

        var result = Classify(source, new HashSet<string> { "AdvancedThing" });

        Assert.That(result, Is.Empty);
    }

    internal static IReadOnlyList<(string ClassName, bool CoreSubject, string Destination)> Classify(string sourceText, ISet<string> advancedSymbols)
    {
        var root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();
        var result = new List<(string ClassName, bool CoreSubject, string Destination)>();
        foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!IsTestClass(declaration))
            {
                continue;
            }

            var names = IdentifierNames(declaration);
            var coreSubject = !names.Overlaps(advancedSymbols);
            var toolSubject = names.Any(IsToolsName);
            result.Add((declaration.Identifier.ValueText, coreSubject, toolSubject ? ToolsDestination : BasicDestination));
        }

        return result;
    }

    private static HashSet<string> CollectAdvancedSymbols(string repo)
    {
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in AdvancedSourceProjects)
        {
            foreach (var file in EnumerateCsFiles(repo, Path.Combine(repo, project)))
            {
                foreach (var node in ParseFile(file).DescendantNodes())
                {
                    var name = node switch
                    {
                        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                        DelegateDeclarationSyntax del => del.Identifier.ValueText,
                        _ => null,
                    };
                    if (name is not null)
                    {
                        typeNames.Add(name);
                    }
                }
            }
        }

        var symbols = new HashSet<string>(typeNames, StringComparer.Ordinal);
        foreach (var method in ParseFile(Path.Combine(repo, FactoryProject, FactoryFile)).DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.ValueText;
            if (name.StartsWith("Create", StringComparison.Ordinal) && IdentifierNames(method.ReturnType).Overlaps(typeNames))
            {
                symbols.Add(name);
            }
        }

        return symbols;
    }

    private static SortedDictionary<string, string> ScanScannedProjects(string repo, ISet<string> advanced)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in ScannedProjects)
        {
            var projectDir = Path.Combine(repo, project);
            foreach (var file in EnumerateCsFiles(repo, projectDir))
            {
                var relative = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
                foreach (var (className, coreSubject, destination) in Classify(File.ReadAllText(file), advanced))
                {
                    if (coreSubject)
                    {
                        found[$"{project}/{relative}::{className}"] = destination;
                    }
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> EnumerateCsFiles(string repo, string projectDir)
    {
        if (!Directory.Exists(projectDir))
        {
            throw new DirectoryNotFoundException("Expected project folder is missing: " + projectDir);
        }

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = Path.GetRelativePath(repo, file).Split('\\', '/');
            if (segments.Any(segment => segment is "bin" or "obj" or "Worktree"))
            {
                continue;
            }

            yield return file;
        }
    }

    private static SyntaxNode ParseFile(string path) => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();

    private static bool IsTestClass(ClassDeclarationSyntax declaration)
    {
        if (AttributeNames(declaration.AttributeLists).Contains("TestFixture"))
        {
            return true;
        }

        return declaration.Members.OfType<MethodDeclarationSyntax>()
            .Any(method => AttributeNames(method.AttributeLists).Any(name => TestMethodAttributes.Contains(name)));
    }

    private static IEnumerable<string> AttributeNames(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(list => list.Attributes).Select(attribute => ShortName(attribute.Name.ToString()));

    private static string ShortName(string qualifiedName)
    {
        var dot = qualifiedName.LastIndexOf('.');
        return dot < 0 ? qualifiedName : qualifiedName[(dot + 1)..];
    }

    private static HashSet<string> IdentifierNames(SyntaxNode node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in node.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                names.Add(token.ValueText);
            }
        }

        return names;
    }

    // "Tools" alone is a namespace segment, not a type name, so a suffix match needs a longer identifier.
    private static bool IsToolsName(string identifier) =>
        ToolsNames.Contains(identifier)
        || ToolsNameSuffixes.Any(suffix => identifier.Length > suffix.Length && identifier.EndsWith(suffix, StringComparison.Ordinal));

    private static Dictionary<string, string> LoadBaseline(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var split = line.LastIndexOf(" | ", StringComparison.Ordinal);
            if (split < 0)
            {
                throw new InvalidDataException("Malformed placement baseline line: " + line);
            }

            result[line[..split]] = line[(split + 3)..];
        }

        return result;
    }

    private static void WriteBaseline(string path, IReadOnlyDictionary<string, string> found)
    {
        var lines = new List<string>
        {
            "# Core-subject test classes that still sit in an Advanced test project (placement ratchet baseline).",
            "# Core-subject: no identifier token in the class names a type declared in the Advanced subject projects.",
            "# Move a class to its destination project, then remove its line. New entries are not allowed.",
            "# Refresh after moving classes: set ROSLYNSENTINEL_UPDATE_PLACEMENT_BASELINE=1 and run",
            "# TestPlacementRatchetTests.AdvancedTestProjects_HoldNoNewCoreSubjectClasses.",
            "# Line format: <project>/<relative path>::<ClassName> | <destination project>",
        };
        lines.AddRange(found.Select(pair => $"{pair.Key} | {pair.Value}"));
        File.WriteAllLines(path, lines);
    }
}

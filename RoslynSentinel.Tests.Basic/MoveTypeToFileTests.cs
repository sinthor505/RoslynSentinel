#pragma warning disable CS8618
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
[Category("BasicRefactoringEngine")] // sentinel:auto-category
public class MoveTypeToFileTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _refactoringEngine;

    [SetUp]
    public void Setup()
    {
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(new NullLogger<IWorkspaceManager>());
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, new NullLogger<BasicRefactoringEngine>(), config);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
    }

    private static async Task LogAndThrow(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex.Data.Contains("Logs"))
        {
            var logs = ex.Data["Logs"] as List<string>;
            var logMessage = new StringBuilder();
            logMessage.AppendLine($"Test failed with message: {ex.Message}");
            logMessage.AppendLine("--- Server-Side Logs ---");
            logs?.ForEach(l => logMessage.AppendLine(l));
            logMessage.AppendLine("--------------------------");
            Assert.Fail(logMessage.ToString());
        }
    }

    [Test]
    public async Task MoveTypeToFile_WithRealWorldScenario_ShouldSucceed()
    {
        // Arrange
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("RealWorldTest.cs", @"
namespace ExpressRecipe.Services.Domain.FeatureGates;
public sealed class FeatureGateResult { public bool IsEnabled { get; set; } }
public sealed record FeatureGateErrorResponse { public string ErrorMessage { get; init; } public string FeatureName { get; init; } }
")
        });
        _workspaceManager.SetTestSolution(solution);

        var filePath = "RealWorldTest.cs"; // Relative path within the ad-hoc workspace
        var typeToMove = "FeatureGateErrorResponse";

        // Act & Assert
        await LogAndThrow(async () =>
        {
            var changes = await _refactoringEngine.MoveTypeToFileAsync(filePath, typeToMove);

            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.Count, Is.EqualTo(2));
            var newFilePath = changes.Keys.FirstOrDefault(k => k.Contains("FeatureGateErrorResponse.cs"));
            Assert.That(newFilePath.Absolute, Does.EndWith("FeatureGateErrorResponse.cs"), "the moved type must land in FeatureGateErrorResponse.cs");
            Assert.That(changes[newFilePath], Does.Contain("record FeatureGateErrorResponse"), "new file must contain the moved record");
        });
    }

    [Test]
    public async Task MoveTypeToFile_NewFileContainsMovedType()
    {
        // Arrange -> the new file should contain the moved type declaration
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("MultiType.cs", @"
namespace MyApp.Services;
public class ServiceA { public void DoWork() { } }
public class ServiceB { public string Name { get; set; } }
")
        });
        _workspaceManager.SetTestSolution(solution);

        // Act
        var changes = await _refactoringEngine.MoveTypeToFileAsync("MultiType.cs", "ServiceB");

        // Assert -> new file has the moved type, source file no longer does
        var newKey = changes.Keys.Single(k => k.Contains("ServiceB.cs"));
        var srcKey = changes.Keys.Single(k => !k.Contains("ServiceB.cs"));

        Assert.That(changes[newKey], Does.Contain("class ServiceB"), "New file must contain moved type");
        Assert.That(changes[srcKey], Does.Not.Contain("class ServiceB"), "Source file must not retain moved type");
        Assert.That(changes[srcKey], Does.Contain("class ServiceA"), "Source file must keep primary type");
    }

    [Test]
    public async Task MoveTypeToFile_NewFilePreservesNamespaceAndUsings()
    {
        // Arrange -> verify namespace and usings are copied into the new file
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("OrderTypes.cs", @"
using System.Collections.Generic;
namespace MyApp.Orders;
public class Order { public List<string> Items { get; set; } = new(); }
public class OrderLine { public string Sku { get; set; } public int Qty { get; set; } }
")
        });
        _workspaceManager.SetTestSolution(solution);

        // Act
        var changes = await _refactoringEngine.MoveTypeToFileAsync("OrderTypes.cs", "OrderLine");

        var newKey = changes.Keys.Single(k => k.Contains("OrderLine.cs"));
        Assert.That(changes[newKey], Does.Contain("namespace MyApp.Orders"), "New file must carry namespace");
        Assert.That(changes[newKey], Does.Contain("OrderLine"), "New file must contain the type body");
    }

    [Test]
    public async Task MoveTypeToFile_SourceFileRetainsNonMovedType()
    {
        // Arrange
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Dtos.cs", @"
namespace MyApp.Contracts;
public record CreateRequest(string Name);
public record CreateResponse(string Id);
")
        });
        _workspaceManager.SetTestSolution(solution);

        // Act
        var changes = await _refactoringEngine.MoveTypeToFileAsync("Dtos.cs", "CreateResponse");

        var srcKey = changes.Keys.Single(k => !k.Contains("CreateResponse.cs"));
        Assert.That(changes[srcKey], Does.Contain("CreateRequest"), "Source file must keep the untouched type");
    }

    [Test]
    public async Task MoveAllTypesToFiles_MovesAllSecondaryTypes()
    {
        // Arrange
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Bundle.cs", @"
namespace MyApp;
public class Alpha { }
public class Beta { }
public class Gamma { }
")
        });
        _workspaceManager.SetTestSolution(solution);

        // Act
        var changes = await _refactoringEngine.MoveAllTypesToFilesAsync("Bundle.cs");

        // Expect: original file updated + 2 new files (Beta and Gamma; Alpha is primary because it matches no filename)
        // Actually Alpha is first so it's primary; Beta and Gamma get new files
        Assert.That(changes.Count, Is.EqualTo(3), "Should have source + 2 new files");
        Assert.That(changes.Keys.Any(k => k.Contains("Beta.cs")), "Beta should get its own file");
        Assert.That(changes.Keys.Any(k => k.Contains("Gamma.cs")), "Gamma should get its own file");
    }

    [Test]
    public async Task MoveAllTypesToFilesAsync_MissingFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Bundle.cs", @"
namespace App;
public class Alpha { }
")
        });
        _workspaceManager.SetTestSolution(solution);

        await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _refactoringEngine.MoveAllTypesToFilesAsync("Missing.cs"));
    }

    [Test]
    public async Task MoveTypeToFile_ReturnsNonEmptyContent_ForBothFiles()
    {
        // This test directly verifies the content previews that the MCP tool exposes.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Combined.cs", @"
namespace App;
public class WidgetA { public int Id { get; set; } }
public class WidgetB { public string Label { get; set; } }
")
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveTypeToFileAsync("Combined.cs", "WidgetB");

        Assert.That(changes, Has.Count.EqualTo(2), "source file + new WidgetB.cs");
        var newKey = changes.Keys.Single(k => k.Contains("WidgetB.cs"));
        var srcKey = changes.Keys.Single(k => !k.Contains("WidgetB.cs"));
        Assert.That(changes[newKey], Does.Contain("class WidgetB"), "new file must contain the moved type");
        Assert.That(changes[srcKey], Does.Contain("class WidgetA"), "source file must keep the primary type");

        foreach (var (path, content) in changes)
        {
            Assert.That(content, Is.Not.Null.And.Not.Empty,
                $"File '{path}' content must not be null or empty (this is what MoveTypeToFile preview shows)");
            Assert.That(content.Length, Is.GreaterThan(20),
                $"File '{path}' content seems too short to be valid C#");
        }
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task MoveAllTypesToFiles_EolCreatorUnderCrlf_LfSource_IsUniformCrlfAsync()
    {
        // The split-out files are new documents. Under Crlf they must reach disk as uniform CRLF through the chokepoint.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var projectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var bundle = Path.Combine(projectDir, "Bundle.cs");
        await File.WriteAllTextAsync(bundle, "namespace MyApp;\npublic class Alpha { }\npublic class Beta { }\npublic class Gamma { }\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var engine = Battery.Basic.TestEngines.BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, Battery.Basic.TestEngines.SentinelConfiguration());

        var changes = await engine.MoveAllTypesToFilesAsync(bundle);
        var applied = await workspaceManager.ApplyProposedChangesAsync(changes, retryCount: 0, validateChanges: false);

        Assert.That(applied.IsError, Is.False, applied.Summary);
        foreach (var splitFile in new[] { Path.Combine(projectDir, "Beta.cs"), Path.Combine(projectDir, "Gamma.cs") })
        {
            Assert.That(File.Exists(splitFile), Is.True, $"{splitFile} should be written");
            var bytes = await File.ReadAllBytesAsync(splitFile);
            Assert.That(bytes.Where((b, i) => b == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r')).Count(), Is.Zero, $"{splitFile}: no bare LF byte may survive");
            Assert.That(bytes.Contains((byte)'\r'), Is.True, $"{splitFile}: the file must be CRLF");
        }
    }

    // Compiles the solution's documents with the proposed texts substituted (new files appended) and returns every diagnostic.
    private static List<Diagnostic> CompileProposedTexts(Solution solution, Dictionary<FilePathWrapper, string> changes)
    {
        var project = solution.Projects.Single();
        var changedByName = changes.ToDictionary(kv => Path.GetFileName(kv.Key.Absolute), kv => kv.Value);
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
        var trees = new List<SyntaxTree>();
        foreach (var doc in project.Documents)
        {
            var text = changedByName.TryGetValue(doc.Name, out var changed) ? changed : doc.GetTextAsync().GetAwaiter().GetResult().ToString();
            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: doc.Name));
        }

        foreach (var (name, text) in changedByName.Where(kv => project.Documents.All(d => d.Name != kv.Key)))
        {
            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: name));
        }

        var compilation = CSharpCompilation.Create("ProposedTexts", trees, project.MetadataReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics().ToList();
    }

    [Test]
    public async Task MoveTypeToFile_NestedType_RewritesQualifiedReferencesSolutionWide_NoCs0426()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Outer.cs", """
namespace Lib.Core;
public class Outer
{
    public class Nested
    {
        public int Value;
        public static Nested Create() => new Outer.Nested();
    }

    public Nested Make() => new Nested();
}
"""),
            ("AliasUser.cs", """
using System;
using O = Lib.Core.Outer;

namespace App.Consumers;
public class AliasUser
{
    private O.Nested _a;
    private Lib.Core.Outer.Nested _b;
    public object M() => new O.Nested();

    /// <summary>See <see cref="O.Nested"/>.</summary>
    public void N() { }
}
"""),
            ("SameNs.cs", """
namespace Lib.Core;
public class SameNs
{
    public Outer.Nested F;
    public Outer.Nested Make() => new Outer.Nested();

    /// <summary>See <see cref="Outer.Nested"/>.</summary>
    public void Z() { }
}
"""),
            ("UsingUser.cs", """
using Lib.Core;

namespace App.Other;
public class UsingUser
{
    private Outer.Nested _f = new Outer.Nested();
}
"""),
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveTypeToFileAsync("Outer.cs", "Nested");

        string TextOf(string name) => changes.Single(kv => Path.GetFileName(kv.Key.Absolute) == name).Value;
        Assert.That(changes, Has.Count.EqualTo(5), "Outer.cs + Nested.cs + the three referencing files");
        Assert.That(TextOf("Outer.cs"), Does.Not.Contain("class Nested").And.Contain("class Outer").And.Contain("public Nested Make()"));
        var nestedText = TextOf("Nested.cs");
        Assert.That(nestedText, Does.Contain("namespace Lib.Core;").And.Contain("class Nested").And.Contain("new Nested()").And.Not.Contain("Outer.Nested"));

        var aliasUser = TextOf("AliasUser.cs");
        Assert.That(aliasUser, Does.Not.Contain("O.Nested"));
        Assert.That(aliasUser, Does.Contain("private Nested _a;").And.Contain("private Lib.Core.Nested _b;").And.Contain("new Nested()").And.Contain("cref=\"Nested\""));
        Assert.That(aliasUser, Does.Contain("using Lib.Core;"), "alias-qualified reference loses its path, so the namespace must be imported");
        Assert.That(aliasUser.IndexOf("using Lib.Core;"), Is.GreaterThan(aliasUser.IndexOf("using System;")), "System usings stay first");

        var sameNs = TextOf("SameNs.cs");
        Assert.That(sameNs, Does.Not.Contain("Outer.Nested").And.Not.Contain("using Lib.Core;"));
        Assert.That(sameNs, Does.Contain("public Nested F;").And.Contain("new Nested()").And.Contain("cref=\"Nested\""));

        var usingUser = TextOf("UsingUser.cs");
        Assert.That(usingUser, Does.Not.Contain("Outer.Nested"));
        Assert.That(Regex.Matches(usingUser, "using Lib\\.Core;").Count, Is.EqualTo(1), "existing using must not be duplicated");

        var diagnostics = CompileProposedTexts(solution, changes);
        Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        Assert.That(diagnostics.Select(d => d.Id), Does.Not.Contain("CS0426").And.Not.Contain("CS1574"), "no unresolved nested type or cref");
    }

    [Test]
    public async Task MoveTypeToFile_NestedTypeWithoutExternalReferences_ReturnsOnlySourceAndNewFile()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Holder.cs", """
namespace App;
public class Holder
{
    public class Item { public int Id; }
    public Item Current;
}
"""),
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveTypeToFileAsync("Holder.cs", "Item");

        Assert.That(changes, Has.Count.EqualTo(2));
        var diagnostics = CompileProposedTexts(solution, changes);
        Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
    }

    [Test]
    public async Task MoveTypeToFile_SingleTypeFile_NoTargetFolder_ReturnsEmpty()
    {
        // Arrange
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Solo.cs", @"
namespace MyApp;
public class Solo { }
")
        });
        _workspaceManager.SetTestSolution(solution);

        // Act
        var changes = await _refactoringEngine.MoveTypeToFileAsync("Solo.cs", "Solo");

        // Assert
        Assert.That(changes, Is.Empty);
    }

    [Test]
    public async Task MoveTypeToFile_SingleTypeFile_DifferingTargetFolder_RefusesNamingTargetFolder()
    {
        // Arrange
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[] {
            ("Solo.cs", @"
namespace MyApp;
public class Solo { }
")
        });
        _workspaceManager.SetTestSolution(solution);

        // Act
        var ex = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => _refactoringEngine.MoveTypeToFileAsync("Solo.cs", "Solo", @"C:\proj\OtherFolder"));

        // Assert
        Assert.That(ex.Message, Does.Contain("targetFolder"));
        Assert.That(ex.Message, Does.Not.Contain("MoveFile"));
    }
}

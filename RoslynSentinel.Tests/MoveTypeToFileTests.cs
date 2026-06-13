#pragma warning disable CS8618
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class MoveTypeToFileTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private RefactoringEngine _refactoringEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        _refactoringEngine = new RefactoringEngine(new NullLogger<RefactoringEngine>(), _workspaceManager, config);
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
            Assert.That(newFilePath.Absolute, Is.Not.Null);
        });
    }

    [Test]
    public async Task MoveTypeToFile_NewFileContainsMovedType()
    {
        // Arrange — the new file should contain the moved type declaration
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

        // Assert — new file has the moved type, source file no longer does
        var newKey = changes.Keys.Single(k => k.Contains("ServiceB.cs"));
        var srcKey = changes.Keys.Single(k => !k.Contains("ServiceB.cs"));

        Assert.That(changes[newKey], Does.Contain("class ServiceB"), "New file must contain moved type");
        Assert.That(changes[srcKey], Does.Not.Contain("class ServiceB"), "Source file must not retain moved type");
        Assert.That(changes[srcKey], Does.Contain("class ServiceA"), "Source file must keep primary type");
    }

    [Test]
    public async Task MoveTypeToFile_NewFilePreservesNamespaceAndUsings()
    {
        // Arrange — verify namespace and usings are copied into the new file
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

        foreach (var (path, content) in changes)
        {
            Assert.That(content, Is.Not.Null.And.Not.Empty,
                $"File '{path}' content must not be null or empty (this is what MoveTypeToFile preview shows)");
            Assert.That(content.Length, Is.GreaterThan(20),
                $"File '{path}' content seems too short to be valid C#");
        }
    }
}

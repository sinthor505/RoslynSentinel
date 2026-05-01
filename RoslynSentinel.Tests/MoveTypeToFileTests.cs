#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;
using System.Text;

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
            var logs = (List<string>)ex.Data["Logs"];
            var logMessage = new StringBuilder();
            logMessage.AppendLine($"Test failed with message: {ex.Message}");
            logMessage.AppendLine("--- Server-Side Logs ---");
            logs.ForEach(l => logMessage.AppendLine(l));
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
            Assert.That(newFilePath, Is.Not.Null);
        });
    }
}

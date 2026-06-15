using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests;

[TestFixture]
public class WorkspaceRefreshTests
{
    private PersistentWorkspaceManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _manager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _manager.Dispose();
    }

    [Test]
    public async Task Refresh_UpdatesExistingCsDocument_ReflectsInCurrentSolution()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"WsRefresh_Update_{Guid.NewGuid()}.cs");
        try
        {
            const string initialContent = "public class Foo { }";
            await File.WriteAllTextAsync(tempFile, initialContent);

            var projectCsproj = Path.Combine(Path.GetTempPath(), "TestProj", "TestProj.csproj");
            var solution = TestSolutionBuilder.CreateSolutionWithProject(
                "TestProj",
                projectCsproj,
                [("Foo.cs", initialContent, tempFile)]);
            _manager.SetTestSolution(solution);

            const string updatedContent = "public class Foo { public int X { get; set; } }";
            await File.WriteAllTextAsync(tempFile, updatedContent);

            var result = await _manager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { [tempFile] = updatedContent });

            Assert.That(result.WorkspaceInSync, Is.True);

            var doc = _manager.CurrentSolution!.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, tempFile, StringComparison.OrdinalIgnoreCase));

            Assert.That(doc, Is.Not.Null, "Document should exist in CurrentSolution");
            var text = await doc!.GetTextAsync();
            Assert.That(text.ToString(), Is.EqualTo(updatedContent));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Refresh_AddsNewCsDocumentToContainingProject_ReflectsInCurrentSolution()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"WsRefresh_{Guid.NewGuid()}");
        Directory.CreateDirectory(projectDir);
        var csprojPath = Path.Combine(projectDir, "TestProj.csproj");
        var newFilePath = Path.Combine(projectDir, "NewClass.cs");
        try
        {
            const string content = "public class NewClass { }";
            await File.WriteAllTextAsync(newFilePath, content);

            var solution = TestSolutionBuilder.CreateSolutionWithProject(
                "TestProj",
                csprojPath,
                []);
            _manager.SetTestSolution(solution);

            var result = await _manager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { [newFilePath] = content });

            Assert.That(result.WorkspaceInSync, Is.True);

            var doc = _manager.CurrentSolution!.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, newFilePath, StringComparison.OrdinalIgnoreCase));

            Assert.That(doc, Is.Not.Null, "New document should have been added to the containing project");
            var text = await doc!.GetTextAsync();
            Assert.That(text.ToString(), Is.EqualTo(content));
        }
        finally
        {
            if (File.Exists(newFilePath)) File.Delete(newFilePath);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
        }
    }

    [Test]
    public async Task Refresh_LockIsReleasedBeforeMsBuildReload_GetBranchedSolutionAsyncDoesNotBlock()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"WsRefresh_Lock_{Guid.NewGuid()}");
        Directory.CreateDirectory(projectDir);
        var csprojFile = Path.Combine(projectDir, "TestProj.csproj");
        try
        {
            const string originalCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
            await File.WriteAllTextAsync(csprojFile, originalCsproj);

            var solution = TestSolutionBuilder.CreateSolutionWithProject(
                "TestProj",
                csprojFile,
                []);
            _manager.SetTestSolution(solution);
            // SolutionPath is null — no background MSBuild reload will be fired.

            const string updatedCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

            var result = await _manager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { [csprojFile] = updatedCsproj });

            // Structural change: fast path sets needsFullReload; WorkspaceInSync must be false.
            Assert.That(result.WorkspaceInSync, Is.False, "Structural change should set WorkspaceInSync=false");

            // After ApplyProposedChangesAsync returns the lock must be free.
            // GetBranchedSolutionAsync should not block.
            var sw = Stopwatch.StartNew();
            var branchedTask = _manager.GetBranchedSolutionAsync();
            var completed = await Task.WhenAny(branchedTask, Task.Delay(TimeSpan.FromSeconds(2)));
            sw.Stop();

            Assert.That(completed, Is.SameAs(branchedTask),
                "GetBranchedSolutionAsync should complete promptly — lock must not be held after ApplyProposedChangesAsync returns");
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000));
        }
        finally
        {
            if (File.Exists(csprojFile)) File.Delete(csprojFile);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
        }
    }
}

// ReadFile -> WorkspaceTools. Zero coverage before this file (GetTestCoverageMap flagged
// branches: document == null, startLine/endLine slicing, out-of-range slice, offload threshold).
// Data is returned as an anonymous object (not a named record) for the non-offload paths. Anonymous
// type properties are internal to the declaring assembly, so `dynamic` binding fails cross-assembly
// here -> use reflection (GetProperty) instead.

using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReadFileTests
{
    private FakeWorkspaceManager _workspaceManager;
    private WorkspaceTools _tools;
    private string _documentPath;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _documentPath = Path.Combine(Path.GetTempPath(), "ReadFileTests_" + Guid.NewGuid().ToString("N"), "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "line1\nline2\nline3\nline4\nline5\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(_workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, config);
        var dependencyEngine = new DependencyEngine(_workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(_workspaceManager);
        _tools = new WorkspaceTools(
            _workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(_workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(_workspaceManager),
            new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static object? GetProp(object data, string name) =>
        data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);

    [Test]
    public async Task ReadFile_WholeFile_ReturnsFullSourceAsync()
    {
        var result = await _tools.ReadFile(reason: "test message", _documentPath);

        Assert.That(!result.IsError, Is.True);
        var data = result.SuccessData!;
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line1"));
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line5"));
        Assert.That((int)GetProp(data, "totalLines")!, Is.EqualTo(6));
    }

    [Test]
    public async Task ReadFile_FileNotInSolution_ReturnsFileNotFoundAsync()
    {
        var missingPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "DoesNotExist.cs");

        var result = await _tools.ReadFile(reason: "test message", missingPath);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("FileNotFound"));
    }

    [Test]
    public async Task ReadFile_FileOnDiskButNotTrackedAsDocument_FallsBackToDiskReadAsync()
    {
        // Mirrors CreateFile writing a file that PersistentWorkspaceManager's in-memory sync
        // never turns into a Roslyn Document (any non-.cs file, or a .cs file outside every
        // project's globs) -> ReadFile must still be able to see it, matching what CreateFile wrote.
        var onDiskOnlyPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "OnDiskOnly.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(onDiskOnlyPath)!);
        var content = "not part of the solution, but present on disk";
        await File.WriteAllTextAsync(onDiskOnlyPath, content);

        try
        {
            var result = await _tools.ReadFile(reason: "test message", onDiskOnlyPath);

            Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
            Assert.That((string)GetProp(result.SuccessData!, "source")!, Is.EqualTo(content));
        }
        finally
        {
            File.Delete(onDiskOnlyPath);
        }
    }

    [Test]
    public async Task ReadFile_WithLineRange_ReturnsRequestedSliceAsync()
    {
        var result = await _tools.ReadFile(reason: "test message", _documentPath, startLine: 2, endLine: 3);

        Assert.That(!result.IsError, Is.True);
        var data = result.SuccessData!;
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line2"));
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line3"));
        Assert.That((string)GetProp(data, "source")!, Does.Not.Contain("line4"));
        Assert.That((int)GetProp(data, "startLine")!, Is.EqualTo(2));
        Assert.That((int)GetProp(data, "endLine")!, Is.EqualTo(3));
    }

    [Test]
    public async Task ReadFile_StartLineBeyondEndOfFile_ReturnsInvalidArgumentAsync()
    {
        var result = await _tools.ReadFile(reason: "test message", _documentPath, startLine: 100, endLine: 200);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ReadFile_LargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync()
    {
        var bigDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Big.cs");
        var bigSource = string.Concat(Enumerable.Range(0, 2000).Select(i => $"var line{i} = {i};\n"));
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("Big.cs", bigSource, bigDocPath) });
        _workspaceManager.SetTestSolution(solution);
        // ReadFile only offloads when GetSolutionRoot() is non-empty; the fake derives that from
        // SolutionPath since the AdhocWorkspace solution here has no FilePathWrapper of its own.
        _workspaceManager.SolutionPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Test.sln");

        var result = await _tools.ReadFile(reason: "test message", bigDocPath);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.LargeResult, Is.Not.Null);
        Assert.That(result.LargeResult!.ResultType, Is.EqualTo("FileSource"));
    }

    [Test]
    public async Task ReadFile_RangeLargerThanInlineBudget_IsShortenedNotOffloadedAsync()
    {
        // Create a large document with ~800 lines of code-like content (~60 chars per line).
        // TrimRangeToInlineBudget should shorten the requested range to stay under the inline budget.
        var bigDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "BigRanged.cs");
        var lines = new List<string>();
        for (int i = 0; i < 820; i++)
        {
            // Lines with code-like content, ~60 chars each, including special chars.
            lines.Add($"var x{i:000} = GetValue<int>(\"{i} value\") + someFunc(x < 100 && y > 50);\n");
        }
        var bigSource = string.Concat(lines);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("BigRanged.cs", bigSource, bigDocPath) });
        _workspaceManager.SetTestSolution(solution);
        _workspaceManager.SolutionPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Test.sln");

        var result = await _tools.ReadFile(reason: "test message", bigDocPath, startLine: 1, endLine: 800);

        Assert.That(!result.IsError, Is.True);
        var data = result.SuccessData!;
        var startLine = (int)GetProp(data, "startLine")!;
        var endLine = (int)GetProp(data, "endLine")!;
        var source = (string)GetProp(data, "source")!;
        var statusMessage = (string?)GetProp(result, "StatusMessage");
        var hasMoreData = (bool?)GetProp(result, "HasMoreData") ?? false;

        // Should be shortened, not offloaded.
        Assert.That(result.LargeResult, Is.Null);
        Assert.That(endLine, Is.LessThan(800));
        Assert.That(hasMoreData, Is.True);
        Assert.That(statusMessage, Does.Contain("startLine:"));

        // Verify the result fits under the inline budget when serialized.
        var jsonSerialized = System.Text.Json.JsonSerializer.Serialize(result.SuccessData, RoslynSentinel.Common.SharedJsonOptions.Default);
        Assert.That(jsonSerialized.Length, Is.LessThan(RoslynSentinel.Common.LargeResultHelper.OffloadThresholdBytes));
    }

    [Test]
    public async Task ReadFile_RangeUnderBudget_IsReturnedUnchangedAsync()
    {
        // Create a document with ~820 lines, request a small range 1-20 that easily fits.
        var bigDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "BigSmallRange.cs");
        var lines = new List<string>();
        for (int i = 0; i < 820; i++)
        {
            lines.Add($"var x{i:000} = GetValue<int>(\"{i} value\") + someFunc(x < 100 && y > 50);\n");
        }
        var bigSource = string.Concat(lines);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("BigSmallRange.cs", bigSource, bigDocPath) });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.ReadFile(reason: "test message", bigDocPath, startLine: 1, endLine: 20);

        Assert.That(!result.IsError, Is.True);
        var data = result.SuccessData!;
        var endLine = (int)GetProp(data, "endLine")!;
        var statusMessage = result.SuccessData?.GetType().GetProperty("StatusMessage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(result.SuccessData);

        // Should not be shortened (range fits easily).
        Assert.That(endLine, Is.EqualTo(20));
        Assert.That(statusMessage, Is.Null);
    }

    [Test]
    public async Task ReadFile_ShortenedRange_ContinuationCallReturnsTheNextLinesAsync()
    {
        // Request lines 1-800, which will be shortened. Then call again with startLine = previousEndLine + 1
        // and verify the first line of the second call is the next sequential line after the first call's last line.
        var bigDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "BigContinuation.cs");
        var lines = new List<string>();
        for (int i = 0; i < 820; i++)
        {
            lines.Add($"// Line {i:000}: x < y && z > 0 \"quoted\" test\n");
        }
        var bigSource = string.Concat(lines);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("BigContinuation.cs", bigSource, bigDocPath) });
        _workspaceManager.SetTestSolution(solution);
        _workspaceManager.SolutionPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Test.sln");

        // First call: request 1-800, should be shortened.
        var result1 = await _tools.ReadFile(reason: "test message", bigDocPath, startLine: 1, endLine: 800);
        Assert.That(!result1.IsError, Is.True);
        var data1 = result1.SuccessData!;
        var firstEndLine = (int)GetProp(data1, "endLine")!;
        var source1 = (string)GetProp(data1, "source")!;

        // Extract line numbers from first result's last content line (e.g., "// Line 295: ...")
        var lines1 = source1.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lastLineContent = lines1.Last();
        var lastLineMatch = System.Text.RegularExpressions.Regex.Match(lastLineContent, @"Line (\d+):");
        Assert.That(lastLineMatch.Success, "Should extract line number from last line of first result");
        int lastLineNumber = int.Parse(lastLineMatch.Groups[1].Value);

        // Second call: request from firstEndLine + 1 onwards.
        var result2 = await _tools.ReadFile(reason: "test message", bigDocPath, startLine: firstEndLine + 1, endLine: 820);
        Assert.That(!result2.IsError, Is.True);
        var data2 = result2.SuccessData!;
        var source2 = (string)GetProp(data2, "source")!;

        // Extract the first line's line number from second result.
        var lines2 = source2.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var firstLineContent = lines2.First();
        var firstLineMatch = System.Text.RegularExpressions.Regex.Match(firstLineContent, @"Line (\d+):");
        Assert.That(firstLineMatch.Success, "Should extract line number from first line of second result");
        int firstLineNumber = int.Parse(firstLineMatch.Groups[1].Value);

        // The line numbers should be consecutive (one after the other).
        Assert.That(firstLineNumber, Is.EqualTo(lastLineNumber + 1), "Continuation should provide the next sequential line");
    }

    private WorkspaceTools BuildToolsWithoutSolution()
    {
        // Create tools backed by a FakeWorkspaceManager that has NOT been assigned a solution,
        // so CurrentSolution is null and ResolveFromWire returns NoSolutionLoaded.
        var workspaceManager = new FakeWorkspaceManager();
        // Deliberately do NOT call SetTestSolution, leaving CurrentSolution null.

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(workspaceManager);
        return new WorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager),
            new WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
    }

    [Test]
    public async Task ReadFile_NoSolutionLoadedAndRootedPath_ReadsFromDiskWithExplanationAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ReadFileNoSolution_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "TestFile.cs");
        var content = "using System;\npublic class Foo { }\n";

        try
        {
            await File.WriteAllTextAsync(filePath, content);

            var toolsWithoutSolution = BuildToolsWithoutSolution();
            var result = await toolsWithoutSolution.ReadFile(reason: "test message", filePath);

            Assert.That(!result.IsError, Is.True);
            var data = result.SuccessData!;
            var source = (string)GetProp(data, "source")!;
            var statusMessage = result.StatusMessage;

            Assert.That(source, Is.EqualTo(content));
            Assert.That(statusMessage, Does.Contain("No solution is loaded"));
        }
        finally
        {
            try
            {
                File.Delete(filePath);
                Directory.Delete(tempDir);
            }
            catch { }
        }
    }

    [Test]
    public async Task ReadFile_NoSolutionLoadedAndRelativePath_StillReportsSolutionNotLoadedAsync()
    {
        var toolsWithoutSolution = BuildToolsWithoutSolution();
        var result = await toolsWithoutSolution.ReadFile(reason: "test message", "Foo.cs");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.SolutionNotLoaded));
    }
}

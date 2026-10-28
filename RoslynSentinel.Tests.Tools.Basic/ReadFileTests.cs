// ReadFileTool -> WorkspaceTools. Zero coverage before this file (GetTestCoverageMap flagged
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
        _workspaceManager = FakeWorkspaceManager.ReadOnly();
        _documentPath = Path.Combine(Path.GetTempPath(), "ReadFileTests_" + Guid.NewGuid().ToString("N"), "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "line1\nline2\nline3\nline4\nline5\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        _tools = TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static object? GetProp(object data, string name) =>
        data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);

    [Test]
    public async Task ReadFile_WholeFile_ReturnsFullSourceAsync()
    {
        var result = await _tools.ReadFileTool(reason: "test message", _documentPath);

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

        var result = await _tools.ReadFileTool(reason: "test message", missingPath);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("FileNotFound"));
    }

    [Test]
    public async Task ReadFile_FileOnDiskButNotTrackedAsDocument_FallsBackToDiskReadAsync()
    {
        // Mirrors CreateFileTool writing a file that PersistentWorkspaceManager's in-memory sync
        // never turns into a Roslyn Document (any non-.cs file, or a .cs file outside every
        // project's globs) -> ReadFileTool must still be able to see it, matching what CreateFileTool wrote.
        var onDiskOnlyPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "OnDiskOnly.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(onDiskOnlyPath)!);
        var content = "not part of the solution, but present on disk";
        await File.WriteAllTextAsync(onDiskOnlyPath, content);

        try
        {
            var result = await _tools.ReadFileTool(reason: "test message", onDiskOnlyPath);

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
        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, startLine: 2, endLine: 3);

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
        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, startLine: 100, endLine: 200);

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
        // ReadFileTool only offloads when GetSolutionRoot() is non-empty; the fake derives that from
        // SolutionPath since the AdhocWorkspace solution here has no FilePathWrapper of its own.
        _workspaceManager.SolutionPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Test.sln");

        var result = await _tools.ReadFileTool(reason: "test message", bigDocPath);

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

        var result = await _tools.ReadFileTool(reason: "test message", bigDocPath, startLine: 1, endLine: 800);

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

        var result = await _tools.ReadFileTool(reason: "test message", bigDocPath, startLine: 1, endLine: 20);

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
        var result1 = await _tools.ReadFileTool(reason: "test message", bigDocPath, startLine: 1, endLine: 800);
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
        var result2 = await _tools.ReadFileTool(reason: "test message", bigDocPath, startLine: firstEndLine + 1, endLine: 820);
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

    // Deliberately no SetTestSolution: CurrentSolution stays null, so ResolveFromWire returns NoSolutionLoaded.
    private WorkspaceTools BuildToolsWithoutSolution() =>
        TestTools.WorkspaceTools(FakeWorkspaceManager.ReadOnly());

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
            var result = await toolsWithoutSolution.ReadFileTool(reason: "test message", filePath);

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
        var result = await toolsWithoutSolution.ReadFileTool(reason: "test message", "Foo.cs");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.SolutionNotLoaded));
    }

    private void UseDocumentSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", source, _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ReadFile_MemberNameOnly_ReturnsOnlyThatMemberSourceAsync()
    {
        UseDocumentSource("public class Alpha\n{\n    public int Keep() => 1;\n    public int Other() => 2;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Keep");

        Assert.That(result.IsError, Is.False);
        var view = (MemberSourceViewResult)result.SuccessData!;
        Assert.That(view.Member.Name, Is.EqualTo("Keep"));
        Assert.That(view.Member.Source, Does.Contain("public int Keep()"));
        Assert.That(view.Member.Source, Does.Not.Contain("Other"));
    }

    [Test]
    public async Task ReadFile_MemberNameWithContainerName_ReturnsMethodFromNamedContainerAsync()
    {
        UseDocumentSource(
            "public class First\n{\n    public int Value() => 1;\n}\n\n" +
            "public class Second\n{\n    public int Value() => 2;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Second");

        Assert.That(result.IsError, Is.False);
        var view = (MemberSourceViewResult)result.SuccessData!;
        Assert.That(view.Member.Source, Does.Contain("=> 2"));
        Assert.That(view.Member.Source, Does.Not.Contain("=> 1"));
    }

    [Test]
    public async Task ReadFile_MemberNameCombinedWithStartLine_ReturnsInvalidArgumentAsync()
    {
        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, startLine: 1, memberName: "Keep");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ReadFile_UnknownMemberName_ReturnsErrorWithoutRawExceptionAsync()
    {
        UseDocumentSource("public class Alpha\n{\n    public int Keep() => 1;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "DoesNotExist");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Is.Not.Null.And.Not.Empty);
        Assert.That(message, Does.Not.Contain("Exception"));
        Assert.That(message, Does.Not.Contain("   at "));
    }

    [Test]
    public async Task ReadFile_ContainerHoldsNoSuchMember_SingleCandidateElsewhere_RefusesWithTheTypeThatDeclaresItAsync()
    {
        UseDocumentSource("public class First\n{\n    public int Value() => 1;\n}\n\npublic class Second\n{\n    public int Other() => 2;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Second");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Second"));
        Assert.That(message, Does.Contain("declares no member named 'Value'"));
        Assert.That(message, Does.Contain("First."));
        Assert.That(message, Does.Contain("lines"));
        Assert.That(message, Does.Contain("ReadFile(filePath:"));
        Assert.That(message, Does.Contain("containerName: 'First'"));
        Assert.That(result.ErrorData!.StructuredDetail, Has.Count.EqualTo(1));
        var candidate = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().Single();
        Assert.That(candidate.ContainingType, Is.EqualTo("First"));
        Assert.That(candidate.Line, Is.EqualTo(3));
    }

    [Test]
    public async Task ReadFile_RefusalRetryHints_BothWorkAsync()
    {
        UseDocumentSource("public class First\n{\n    public int Value() => 1;\n}\n\npublic class Second\n{\n    public int Other() => 2;\n}\n");

        var refusal = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Second");
        Assert.That(refusal.IsError, Is.True);
        var match = System.Text.RegularExpressions.Regex.Match(refusal.ErrorData!.Message, @"lines (\d+)(?:-(\d+))?");
        Assert.That(match.Success, Is.True, refusal.ErrorData!.Message);
        var start = int.Parse(match.Groups[1].Value);
        var end = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : start;

        var memberRetry = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "First");
        Assert.That(memberRetry.IsError, Is.False);
        var view = (MemberSourceViewResult)memberRetry.SuccessData!;
        Assert.That(view.Member.Source, Does.Contain("=> 1"));

        var rangeRetry = await _tools.ReadFileTool(reason: "test message", _documentPath, startLine: start, endLine: end);
        Assert.That(rangeRetry.IsError, Is.False);
        Assert.That(rangeRetry.SuccessData!.ToString(), Does.Contain("Value()"));
    }

    [Test]
    public async Task ReadFile_MemberInTwoOtherTypes_RefusalListsBothWithARetryEachAsync()
    {
        UseDocumentSource(
            "public class First\n{\n    public int Value() => 1;\n}\n\n" +
            "public class Second\n{\n    public int Other() => 2;\n}\n\n" +
            "public class Third\n{\n    public int Value() => 3;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Second");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("First"));
        Assert.That(message, Does.Contain("Third"));
        Assert.That(message, Does.Contain("containerName: 'First'"));
        Assert.That(message, Does.Contain("containerName: 'Third'"));
        Assert.That(result.ErrorData!.StructuredDetail, Has.Count.EqualTo(2));
        var candidates = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(candidates.Select(c => c.ContainingType), Is.EquivalentTo(new[] { "First", "Third" }));
        Assert.That(candidates.Select(c => c.Line), Is.EquivalentTo(new[] { 3, 13 }));
    }

    [Test]
    public async Task ReadFile_ContainerNotInFile_RefusesAndListsDeclaredTypesAsync()
    {
        UseDocumentSource("public class First\n{\n    public int Value() => 1;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Nope");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("No type named 'Nope'"));
        Assert.That(message, Does.Contain("Types declared here"));
    }

    [Test]
    public async Task ReadFile_MemberDeclaredNowhere_WithContainer_PointsAtGetFileOutlineAsync()
    {
        UseDocumentSource("public class First\n{\n    public int Value() => 1;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Missing", containerName: "First");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("No type in this file declares 'Missing'"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        Assert.That(message, Does.Not.Contain("Exception"));
        Assert.That(message, Does.Not.Contain("   at "));
    }

    [Test]
    public async Task ReadFile_GenericContainerSpellings_BothResolveAsync()
    {
        UseDocumentSource("public class Box<T>\n{\n    public T Get() => default!;\n}\n\npublic class Other\n{\n    public int Get() => 1;\n}\n");

        var plain = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Get", containerName: "Box");
        Assert.That(plain.IsError, Is.False);
        var plainView = (MemberSourceViewResult)plain.SuccessData!;
        Assert.That(plainView.Member.Source, Does.Contain("default!"));

        var spelled = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Get", containerName: "Box<T>");
        Assert.That(spelled.IsError, Is.False);
        var spelledView = (MemberSourceViewResult)spelled.SuccessData!;
        Assert.That(spelledView.Member.Source, Does.Contain("default!"));
        Assert.That(spelledView.Member.Source, Does.Not.Contain("=> 1"));
    }

    [Test]
    public async Task ReadFile_ContainerNameCaseDiffers_ResolvesAsync()
    {
        UseDocumentSource(
            "public class First\n{\n    public int Value() => 1;\n}\n\n" +
            "public class Second\n{\n    public int Value() => 2;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "second");

        Assert.That(result.IsError, Is.False);
        var view = (MemberSourceViewResult)result.SuccessData!;
        Assert.That(view.Member.Source, Does.Contain("=> 2"));
        Assert.That(view.Member.Source, Does.Not.Contain("=> 1"));
    }

    [Test]
    public async Task ReadFile_OverloadsInsideTheNamedContainer_StillAmbiguousAsync()
    {
        UseDocumentSource("public class A\n{\n    public void Run(int x) { }\n    public void Run(string s) { }\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Run", containerName: "A");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
    }

    [Test]
    public async Task ReadFile_DottedContainer_ResolvesByLastSegmentAsync()
    {
        UseDocumentSource(
            "public class Outer\n{\n    public class Inner\n    {\n        public int Value() => 1;\n    }\n}\n\n" +
            "public class Other\n{\n    public int Value() => 2;\n}\n");

        var dotted = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Outer.Inner");
        Assert.That(dotted.IsError, Is.False);
        Assert.That(((MemberSourceViewResult)dotted.SuccessData!).Member.Source, Does.Contain("=> 1"));

        var plain = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Inner");
        Assert.That(plain.IsError, Is.False);
        Assert.That(((MemberSourceViewResult)plain.SuccessData!).Member.Source, Does.Contain("=> 1"));
    }

    [Test]
    public async Task ReadFile_WrongDottedContainer_RefusesAsync()
    {
        UseDocumentSource(
            "public class Outer\n{\n    public class Inner\n    {\n        public int Value() => 1;\n    }\n}\n\n" +
            "public class Other\n{\n    public int Value() => 2;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value", containerName: "Outer.Missing");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("Types declared here"));
        var candidates = result.ErrorData!.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        Assert.That(candidates, Has.Count.EqualTo(2));
        Assert.That(candidates.Select(c => c.Line), Is.EquivalentTo(new[] { 5, 11 }));
    }

    [Test]
    public async Task ReadFile_ContainerNotInFile_StillWorksWithoutContainerAsync()
    {
        UseDocumentSource(
            "public class Outer\n{\n    public class Inner\n    {\n        public int Value() => 1;\n    }\n}\n\n" +
            "public class Other\n{\n    public int Value() => 2;\n}\n");

        var result = await _tools.ReadFileTool(reason: "test message", _documentPath, memberName: "Value");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
    }

    private static string WriteOnDiskTextFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "ReadFileEol_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task ReadFile_MixedLineEndingsOnDisk_ReturnsOneReadFileFindingAsync()
    {
        // Lines: CRLF, LF, CRLF, CRLF -> CRLF dominant, line 2 is the only mismatch.
        var path = WriteOnDiskTextFile("a\r\nb\nc\r\nd\r\n");
        try
        {
            var result = await _tools.ReadFileTool(reason: "test message", path);

            Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
            Assert.That(result.Findings?.Count ?? 0, Is.EqualTo(1));
            Assert.That(result.Findings![0].Source, Is.EqualTo("ReadFile"));
            Assert.That(result.Findings![0].Message, Does.Contain("(CRLF): 2"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadFile_UniformLineEndingsOnDisk_ReturnsNoFindingsAsync()
    {
        var path = WriteOnDiskTextFile("a\r\nb\r\nc\r\n");
        try
        {
            var result = await _tools.ReadFileTool(reason: "test message", path);

            Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
            Assert.That(result.Findings?.Count ?? 0, Is.Zero);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadFile_MixedLineEndingsOnDiskWithLineRange_StillReturnsReadFileFindingAsync()
    {
        var path = WriteOnDiskTextFile("a\r\nb\nc\r\nd\r\n");
        try
        {
            var result = await _tools.ReadFileTool(reason: "test message", path, startLine: 1, endLine: 2);

            Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
            Assert.That(result.Findings?.Count ?? 0, Is.EqualTo(1));
            Assert.That(result.Findings![0].Source, Is.EqualTo("ReadFile"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

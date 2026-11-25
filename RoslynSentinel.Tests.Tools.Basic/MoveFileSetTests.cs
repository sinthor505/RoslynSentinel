using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Tool-level tests for the multi-file MoveFile set path (plan Step 16, rows T16.1-T16.11). Same fixture style as MoveFileTests: a real
// PersistentWorkspaceManager over a TestSolutionFixture copy of ContosoOrders (two projects: ContosoOrders.Core and ContosoOrders.Tests).
// Refusals are structured (ErrorCode, StructuredDetail) and leave every file as it was.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WholeFileWriteTools")]
public sealed class MoveFileSetTests
{
    private const string CoreFolder = "ContosoOrders.Core";
    private const string TestsFolder = "ContosoOrders.Tests";

    private static async Task<PersistentWorkspaceManager> LoadAsync(TestSolutionFixture fixture)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        return manager;
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager, WorkspaceTools workspaceTools)
    {
        var diffEngine = Battery.Basic.TestEngines.DiffEngine();
        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return Battery.Basic.TestTools.WholeFileWriteTools(manager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Battery.Basic.TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager) => BuildTools(manager, Battery.Basic.TestTools.WorkspaceTools(manager));

    // Calls MoveFile with one list of pairs (a set of one is a single move).
    private static Task<SentinelCallToolResult<AppliedChangeSummary>> MoveSetAsync(WholeFileWriteTools tools, IEnumerable<(string Source, string Destination)> pairs, MoveFileNamespaceHandling handling, bool dryRun = false)
    {
        var moves = pairs.Select(pair => new FileMovePair { SourcePath = pair.Source, DestinationPath = pair.Destination }).ToList();
        return tools.MoveFileTool(reason: "test message", moves, handling, dryRun);
    }

    private static string PathIn(TestSolutionFixture fixture, params string[] segments) =>
        Path.Combine(new[] { fixture.SolutionDirectory }.Concat(segments).ToArray());

    private static void AssertRefused(SentinelCallToolResult<AppliedChangeSummary> result, string expectedCode, params string[] expectedFragments)
    {
        Assert.That(result.IsError, Is.True, "The move should have been refused.");
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(expectedCode));
        foreach (var fragment in expectedFragments)
        {
            Assert.That(result.ErrorData.Message, Does.Contain(fragment));
        }

        // Facts and options only: no policy words (CLAUDE.md, tool error text).
        Assert.That(result.ErrorData.Message, Does.Not.Contain("MUST"));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("stop").IgnoreCase);
        Assert.That(result.ErrorData.Message, Does.Not.Contain("do not").IgnoreCase);
    }

    // Sum of error-severity compiler diagnostics over every project of the committed solution.
    private static async Task<int> CountErrorsAsync(IWorkspaceManager manager)
    {
        var solution = await manager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var count = 0;
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is not null)
            {
                count += compilation.GetDiagnostics().Count(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            }
        }

        return count;
    }

    // The effects are recorded through ToolCallContext.Current, so a call context must be set before the MoveFile call.
    private static void StartMoveCall()
    {
        var context = new ToolCallContext("agent-1", "coder", "MoveFile", "move the set", 0) { SessionId = "session-1" };
        context.AssignToolCallId("b2d4f6a8");
        ToolCallContext.Set(context);
    }

    // Three files that form one cluster: two classes that reference each other, and one partial class split across two of the files.
    // Written into the Tests project before the solution loads, so the files are part of the workspace from the start.
    private static List<(string Source, string Destination)> WriteClusterFiles(TestSolutionFixture fixture)
    {
        var testsFolder = PathIn(fixture, TestsFolder);
        File.WriteAllText(Path.Combine(testsFolder, "ClusterLeft.cs"), "namespace ContosoOrders.Tests;\r\n\r\npublic class ClusterLeft\r\n{\r\n    public ClusterRight? Peer { get; set; }\r\n}\r\n");
        File.WriteAllText(Path.Combine(testsFolder, "ClusterRight.cs"), "namespace ContosoOrders.Tests;\r\n\r\npublic class ClusterRight\r\n{\r\n    public ClusterLeft? Peer { get; set; }\r\n}\r\n\r\npublic partial class ClusterPart\r\n{\r\n    public ClusterLeft? Left { get; set; }\r\n}\r\n");
        File.WriteAllText(Path.Combine(testsFolder, "ClusterPart.cs"), "namespace ContosoOrders.Tests;\r\n\r\npublic partial class ClusterPart\r\n{\r\n    public int Count { get; set; }\r\n}\r\n");
        return
        [
            (Path.Combine(testsFolder, "ClusterLeft.cs"), PathIn(fixture, CoreFolder, "Cluster", "ClusterLeft.cs")),
            (Path.Combine(testsFolder, "ClusterRight.cs"), PathIn(fixture, CoreFolder, "Cluster", "ClusterRight.cs")),
            (Path.Combine(testsFolder, "ClusterPart.cs"), PathIn(fixture, CoreFolder, "Cluster", "ClusterPart.cs")),
        ];
    }

    private static string CamelCaseOf(string propertyName) => char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    // Heavy fixture: the compile gate over a cross-project move with one ChangeId for three files is chokepoint behaviour, so a real manager is needed.
    [Test]
    public async Task MoveCluster_MutuallyReferencingTypesAndPartialClass_CrossProject_OneCall_SucceedsAsync()
    {
        using var fixture = new TestSolutionFixture();
        var pairs = WriteClusterFiles(fixture);
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        var errorsBefore = await CountErrorsAsync(manager);

        var result = await MoveSetAsync(tools, pairs, MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.ChangeId, Is.Not.Null.And.Not.Empty, "One change covers the whole set.");
        foreach (var (source, destination) in pairs)
        {
            Assert.That(File.Exists(source), Is.False, $"The source is gone: {source}");
            Assert.That(File.Exists(destination), Is.True, $"The destination exists: {destination}");
        }

        Assert.That(await CountErrorsAsync(manager), Is.LessThanOrEqualTo(errorsBefore), "The move adds no compiler error.");
    }

    [Test]
    public async Task MoveSameClusterFileByFile_StillRefusedAsync()
    {
        // Regression pin: moving one member of the cluster on its own still breaks the build, so the compile gate refuses it.
        using var fixture = new TestSolutionFixture();
        var pairs = WriteClusterFiles(fixture);
        using var manager = await LoadAsync(fixture);
        var (source, destination) = pairs[0];

        var result = await MoveSetAsync(BuildTools(manager), [(source, destination)], MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.ValidationFailed);
        Assert.That(File.Exists(source), Is.True, "The file is still in Tests.");
        Assert.That(File.Exists(destination), Is.False, "Nothing reached Core.");
    }

    [Test]
    public async Task MoveSet_EligibilityFailure_LeavesEverythingUntouchedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine("docs", "Good.txt"), "good\r\n");
        var missing = PathIn(fixture, "docs", "Missing.txt");
        var binary = PathIn(fixture, "docs", "Binary.txt");
        var nonUtf8 = PathIn(fixture, "docs", "NotUtf8.txt");
        var good = PathIn(fixture, "docs", "Good.txt");
        File.WriteAllBytes(binary, [0x50, 0x4B, 0x00, 0x00, 0x01, 0x02]);
        File.WriteAllBytes(nonUtf8, [0x80, 0x81, 0xFE, 0xFF]);
        var goodBytes = File.ReadAllBytes(good);
        var binaryBytes = File.ReadAllBytes(binary);
        var nonUtf8Bytes = File.ReadAllBytes(nonUtf8);
        var pairs = new[]
        {
            (missing, PathIn(fixture, "docs", "archive", "Missing.txt")),
            (binary, PathIn(fixture, "docs", "archive", "Binary.txt")),
            (nonUtf8, PathIn(fixture, "docs", "archive", "NotUtf8.txt")),
            (good, PathIn(fixture, "docs", "archive", "Good.txt")),
        };

        var result = await MoveSetAsync(BuildTools(manager), pairs, MoveFileNamespaceHandling.keep);

        // Two distinct reasons among the offenders (not found, not text), so the set reports the shared ineligible code.
        AssertRefused(result, ToolErrorCode.TargetIneligible);
        var offenders = result.ErrorData!.StructuredDetail!.OfType<MoveOffender>().ToList();
        Assert.That(offenders, Has.Count.EqualTo(3), "Every offender is listed, and the good file is not one of them.");
        Assert.That(offenders.Select(offender => Path.GetFileName(offender.Path)), Is.EquivalentTo(new[] { "Missing.txt", "Binary.txt", "NotUtf8.txt" }));
        Assert.That(File.ReadAllBytes(good), Is.EqualTo(goodBytes), "The good file is unchanged.");
        Assert.That(File.ReadAllBytes(binary), Is.EqualTo(binaryBytes), "The binary file is unchanged.");
        Assert.That(File.ReadAllBytes(nonUtf8), Is.EqualTo(nonUtf8Bytes), "The non-UTF-8 file is unchanged.");
        Assert.That(File.Exists(PathIn(fixture, "docs", "archive", "Good.txt")), Is.False, "Nothing reached the destination.");
    }

    [Test]
    public async Task MoveSet_DestinationInNoLoadedProject_RefusesAsync()
    {
        // The planner's text is a plain string (CheckProjectsAndCollectFactsAsync), so the stable fragment is the tool name it names.
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Loose.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Loose\r\n{\r\n}\r\n");
        var source = PathIn(fixture, CoreFolder, "Loose.cs");
        var destination = PathIn(fixture, "loose", "Lost.cs");

        var result = await MoveSetAsync(BuildTools(manager), [(source, destination)], MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.InvalidArgument, "LoadSolution");
        Assert.That(File.Exists(source), Is.True);
        Assert.That(File.Exists(destination), Is.False);
    }

    // Heavy fixture: undoing one change that restored N files is chokepoint behaviour, so the real manager and workspace tools are needed.
    [Test]
    public async Task UndoChange_RestoresWholeSetAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var workspaceTools = Battery.Basic.TestTools.WorkspaceTools(manager);
        var tools = BuildTools(manager, workspaceTools);
        var pairs = new List<(string Source, string Destination)>();
        var originals = new Dictionary<string, byte[]>();
        for (var i = 0; i < 5; i++)
        {
            await fixture.AddFileToSolution(manager, Path.Combine("docs", "UndoSet", $"File{i}.txt"), $"file {i}\r\n");
            var source = PathIn(fixture, "docs", "UndoSet", $"File{i}.txt");
            originals[source] = File.ReadAllBytes(source);
            pairs.Add((source, PathIn(fixture, "docs", "UndoSetMoved", $"File{i}.txt")));
        }

        var moved = await MoveSetAsync(tools, pairs, MoveFileNamespaceHandling.keep);
        Assert.That(moved.IsError, Is.False, moved.ErrorData?.Message);

        var undone = await workspaceTools.UndoChangeTool(reason: "test message", moved.SuccessData!.ChangeId!);

        Assert.That(undone.IsError, Is.False, undone.ErrorData?.Message);
        foreach (var (source, destination) in pairs)
        {
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(originals[source]), $"The original bytes are back: {source}");
            Assert.That(File.Exists(destination), Is.False, $"The destination is gone: {destination}");
        }
    }

    // Heavy fixture: the verbatim bytes are only observable through the real write chokepoint (same reason as MoveFileTests byte tests).
    [Test]
    public async Task MoveSet_CrlfLfBom_ByteIdenticalAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        var files = new (string Name, string Text)[]
        {
            ("crlf.txt", "first\r\nsecond\r\n"),
            ("lf.txt", "first\nsecond\n"),
            ("bom.txt", "﻿first\r\nsecond\n"),
        };
        var pairs = new List<(string Source, string Destination)>();
        var originals = new Dictionary<string, byte[]>();
        foreach (var (name, text) in files)
        {
            await fixture.AddFileToSolution(manager, Path.Combine("docs", "EolSet", name), text);
            var source = PathIn(fixture, "docs", "EolSet", name);
            originals[source] = File.ReadAllBytes(source);
            pairs.Add((source, PathIn(fixture, "docs", "EolSetMoved", name)));
        }

        var result = await MoveSetAsync(tools, pairs, MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        foreach (var (source, destination) in pairs)
        {
            Assert.That(File.Exists(source), Is.False, $"The source is gone: {source}");
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(originals[source]), $"The destination holds the original bytes: {destination}");
        }
    }

    // Heavy fixture: FileMoveMemory and the committed FileMoved effects are written by the chokepoint after the real apply, so a real manager is needed.
    [Test]
    public async Task MoveSet_RecordsFileMoveMemoryAndEffectPerPairAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        var stems = new[] { "Alpha", "Beta", "Gamma" };
        var pairs = new List<(string Source, string Destination)>();
        foreach (var stem in stems)
        {
            await fixture.AddFileToSolution(manager, Path.Combine("docs", "Memory", $"{stem}.txt"), $"{stem}\r\n");
            pairs.Add((PathIn(fixture, "docs", "Memory", $"{stem}.txt"), PathIn(fixture, "docs", "MemoryMoved", $"{stem}.txt")));
        }

        StartMoveCall();
        var result = await MoveSetAsync(tools, pairs, MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        foreach (var (source, destination) in pairs)
        {
            Assert.That(FileMoveMemory.Shared.TryGetCurrentPath(source, out var currentPath, out var toolName), Is.True, $"The move of {source} is remembered.");
            Assert.That(currentPath, Is.EqualTo(Path.GetFullPath(destination)));
            Assert.That(toolName, Is.EqualTo("MoveFile"));
        }

        var fileMoved = stems.Sum(stem => manager.EditEffects.FindFileMovesByNewFileStem(stem).Count);
        Assert.That(fileMoved, Is.EqualTo(3), "One FileMoved effect per pair.");
    }

    [Test]
    public async Task DryRun_ReturnsExpandedPairs_WritesNothingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine("docs", "DryFolder", "One.md"), "# One\r\n");
        await fixture.AddFileToSolution(manager, Path.Combine("docs", "DryFolder", "Nested", "Two.md"), "# Two\r\n");
        var source = PathIn(fixture, "docs", "DryFolder");
        var destination = PathIn(fixture, "docs", "DryDest");

        var result = await MoveSetAsync(BuildTools(manager), [(source, destination)], MoveFileNamespaceHandling.keep, dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.DryRun, Is.True);
        var affected = result.SuccessData.AffectedFiles.Select(file => file.Absolute).ToList();
        Assert.That(affected, Has.Count.EqualTo(4), "Both paths of each of the two expanded files are listed.");
        Assert.That(affected.Count(path => path.EndsWith("One.md", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(affected.Count(path => path.EndsWith("Two.md", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(File.Exists(Path.Combine(source, "One.md")), Is.True, "The source is untouched.");
        Assert.That(File.Exists(Path.Combine(source, "Nested", "Two.md")), Is.True, "The nested source is untouched.");
        Assert.That(Directory.Exists(destination), Is.False, "The destination was not created.");
    }

    [Test]
    public async Task NamespaceHandling_CopyAndObsoleteWithTwoFiles_IsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "TwoA.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class TwoA\r\n{\r\n}\r\n");
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "TwoB.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class TwoB\r\n{\r\n}\r\n");
        var first = PathIn(fixture, CoreFolder, "TwoA.cs");
        var second = PathIn(fixture, CoreFolder, "TwoB.cs");

        var result = await MoveSetAsync(
            BuildTools(manager),
            [(first, PathIn(fixture, CoreFolder, "Moved", "TwoA.cs")), (second, PathIn(fixture, CoreFolder, "Moved", "TwoB.cs"))],
            MoveFileNamespaceHandling.copyAndObsolete);

        AssertRefused(result, ToolErrorCode.InvalidArgument);
        Assert.That(File.Exists(first), Is.True);
        Assert.That(File.Exists(second), Is.True);
    }

    [Test]
    public async Task NamespaceHandling_Update_IsNotImplemented_AndHintNamesChangeNamespaceAsync()
    {
        // The tool is built with no active tool surface, so the hint is the ToolCallHint.Next branch.
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "UpdateMe.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class UpdateMe\r\n{\r\n}\r\n");
        var source = PathIn(fixture, CoreFolder, "UpdateMe.cs");
        var hint = ToolCallHint.Next("ChangeNamespace", "changes the namespace of files, after a move with namespaceHandling keep.");

        var result = await MoveSetAsync(BuildTools(manager), [(source, PathIn(fixture, CoreFolder, "UpdateMoved.cs"))], MoveFileNamespaceHandling.update);

        AssertRefused(result, ToolErrorCode.NotImplemented, "not supported yet", hint);
        Assert.That(File.Exists(source), Is.True);
    }

    // Reflection pin: the constants that name the parameters in findings must match the tool's real parameter and the wire (camelCase) names.
    [Test]
    public void MoveFileParameterNames_MatchTheToolsActualParameterNames()
    {
        var method = typeof(WholeFileWriteTools).GetMethod(nameof(WholeFileWriteTools.MoveFileTool));
        Assert.That(method, Is.Not.Null, "MoveFileTool exists.");
        var movesParameter = method!.GetParameters().SingleOrDefault(parameter => parameter.Name == "moves");
        Assert.That(movesParameter, Is.Not.Null, "The tool takes a parameter named moves.");
        Assert.That(movesParameter!.ParameterType, Is.EqualTo(typeof(List<FileMovePair>)));

        var sourceProperty = typeof(FileMovePair).GetProperty(nameof(FileMovePair.SourcePath));
        var destinationProperty = typeof(FileMovePair).GetProperty(nameof(FileMovePair.DestinationPath));
        Assert.That(sourceProperty, Is.Not.Null);
        Assert.That(destinationProperty, Is.Not.Null);
        Assert.That(MoveFileParameterNames.SourcePath, Is.EqualTo(CamelCaseOf(sourceProperty!.Name)));
        Assert.That(MoveFileParameterNames.DestinationPath, Is.EqualTo(CamelCaseOf(destinationProperty!.Name)));
    }
}

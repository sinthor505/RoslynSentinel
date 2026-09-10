// The blob-integrity invariant: an apply that writes files and returns a changeId must have a
// blob that UndoLastApply can resolve.
//
// Run 20260910-013550-398 (A3) violated it across five tools at once — WrapRange, ExtractMembers,
// SyncInterface, Inline and MoveType all passed slashed operation names ("WrapRange/region"),
// Path.Combine resolved the blob into a non-existent subdirectory, the write threw, and
// ValidateAndApplyHelper discarded the result. Each tool reported status:"applied" with a
// confident undo instruction and no blob on disk.
//
// Driven across all five tools rather than the one the original report named, because this is the
// third recorded UndoLastApply gap (cf. project_synctypeandfilename_undolastapply_blocker) and the
// class of bug is what needs covering. Tools that decline a change (target not found, etc.) return
// no changeId and are simply not exercised by that case — the assertion is conditional on a
// changeId being issued, which is exactly the invariant's own precondition.

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class BlobIntegrityInvariantTests
{
    private TestSolutionFixture _fixture;
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelAdvancedRefactoringTools _tools;
    private string _targetFile;

    [SetUp]
    public async Task SetUpAsync()
    {
        // A real on-disk solution, not TestSolutionBuilder: blobs are written relative to
        // GetSolutionRoot(), so an in-memory solution would make every blob write a no-op and the
        // invariant vacuously true.
        _fixture = new TestSolutionFixture();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);

        var config = new SentinelConfiguration();
        _tools = new SentinelAdvancedRefactoringTools(
            new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, config),
            new StandardRefactoringEngine(_workspaceManager),
            new AdvancedStructuralEngine(_workspaceManager),
            new MappingEngine(_workspaceManager),
            new SemanticRefactoringLibrary(_workspaceManager),
            new GranularRefactoringEngine(_workspaceManager),
            new AdvancedLogicEngine(_workspaceManager),
            new RefinementEngine(_workspaceManager),
            new AdvancedTypeEngine(_workspaceManager),
            new CodeStyleEngine(_workspaceManager, config),
            new CodeFlowEngine(_workspaceManager),
            new AdvancedRefactoringEngine(_workspaceManager),
            new LogicOptimizationEngine(_workspaceManager),
            new ModernizationEngine(_workspaceManager, config),
            new OutParamRefactoringEngine(_workspaceManager),
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine()),
            config,
            NullLogger<SentinelAdvancedRefactoringTools>.Instance);

        _targetFile = Directory
            .EnumerateFiles(_fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories)
            .First(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase));
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
    }

    /// <summary>
    /// The invariant itself. Asserts nothing about whether the operation succeeded — only that if
    /// it issued a changeId, that changeId is redeemable. A tool that declines the change passes
    /// trivially, which is correct: the invariant is about issued handles.
    /// </summary>
    private void AssertChangeIdIsRedeemable(ToolResult<object> result, string toolName)
    {
        var changeId = (result.Data as AppliedChangeSummary)?.ChangeId;
        if (string.IsNullOrEmpty(changeId))
        {
            return;
        }

        Assert.That(
            OperationBlobWriter.FindBlobPath(changeId, _workspaceManager.GetSolutionRoot()),
            Is.Not.Null,
            $"{toolName} returned changeId '{changeId}' but UndoLastApply cannot resolve a blob for it. " +
            "Either the blob write failed silently, or the operation name contains a character that " +
            "can't appear in a filename (the A3 defect — see OperationBlobWriterTests).");
    }

    [Test]
    public async Task WrapRange_IssuedChangeIdResolvesToABlobAsync()
    {
        var source = await File.ReadAllTextAsync(_targetFile);
        var lines = source.Split('\n');
        var bodyLine = Array.FindIndex(lines, l => l.TrimStart().StartsWith("public ")) + 2;

        var result = await _tools.WrapRange(
            reason: "test", _targetFile, startLine: bodyLine, endLine: bodyLine,
            wrapper: "region", name: "TestRegion", dryRun: false);

        AssertChangeIdIsRedeemable(result, "WrapRange");
    }

    [Test]
    public async Task MoveType_IssuedChangeIdResolvesToABlobAsync()
    {
        var typeName = await FindATypeNameAsync();

        var result = await _tools.MoveType(
            reason: "test", _targetFile, typeName, destination: "ownFile", dryRun: false);

        AssertChangeIdIsRedeemable(result, "MoveType");
    }

    [Test]
    public async Task ExtractMembers_IssuedChangeIdResolvesToABlobAsync()
    {
        var typeName = await FindATypeNameAsync();

        var result = await _tools.ExtractMembers(
            reason: "test", _targetFile, typeName, ExtractAsType.@interface,
            newTypeName: "IExtractedForTest", dryRun: false);

        AssertChangeIdIsRedeemable(result, "ExtractMembers");
    }

    [Test]
    public async Task SyncInterface_IssuedChangeIdResolvesToABlobAsync()
    {
        var typeName = await FindATypeNameAsync();

        var result = await _tools.SyncInterface(
            reason: "test", _targetFile, interfaceName: "IDisposable", action: "sync",
            className: typeName, dryRun: false);

        AssertChangeIdIsRedeemable(result, "SyncInterface");
    }

    [Test]
    public async Task Inline_IssuedChangeIdResolvesToABlobAsync()
    {
        var result = await _tools.Inline(
            reason: "test", _targetFile, targetName: "value", kind: "variable", dryRun: false);

        AssertChangeIdIsRedeemable(result, "Inline");
    }

    [Test]
    public async Task EverySlashedOperationNameHasBeenRenamed()
    {
        // The rename half of the A3 fix (sanitization at the sink is the other half, covered by
        // OperationBlobWriterTests). Pinned as a test because the literals are easy to copy from a
        // neighbouring call site, which is how there came to be seventeen of them.
        var toolsSource = Path.Combine(
            FindRepoRoot(), "RoslynSentinel.Server.Advanced", "SentinelAdvancedRefactoringTools.cs");
        Assert.That(File.Exists(toolsSource), Is.True, $"expected source at {toolsSource}");

        var offenders = File.ReadAllLines(toolsSource)
            .Select((line, index) => (line, number: index + 1))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, "\"[A-Za-z]+/[a-zA-Z]+\""))
            .Select(x => $"line {x.number}: {x.line.Trim()}")
            .ToList();

        Assert.That(offenders, Is.Empty,
            "operation names must not contain '/': it becomes a directory separator in the blob " +
            "filename. Use '_' instead.\n" + string.Join("\n", offenders));
    }

    private async Task<string> FindATypeNameAsync()
    {
        var source = await File.ReadAllTextAsync(_targetFile);
        var match = System.Text.RegularExpressions.Regex.Match(source, @"\b(?:class|record)\s+(\w+)");
        Assert.That(match.Success, Is.True, $"no class/record found in {_targetFile}");
        return match.Groups[1].Value;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.That(dir, Is.Not.Null, "could not locate the repo root (no RoslynSentinel.slnx found)");
        return dir!.FullName;
    }
}

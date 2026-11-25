using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// MoveFile with namespaceHandling copyAndObsolete on a .cs file (slice 14-5, MoveFileCopyAndObsolete): the checks and the best-effort caller migration of
// slices 14-1..14-4 run, then the copy, the [Obsolete]-marked original and the migrated callers are applied as one compile-gated change set (one operation blob,
// one changeId). Runs on a real PersistentWorkspaceManager over a TestSolutionFixture copy of ContosoOrders (the tests were not run when the slice was written,
// so the real-disk and compile-state assertions are the likeliest to need a first-run adjustment). Layout used: types in ContosoOrders.Core/Legacy (namespace
// ContosoOrders.Core.Legacy) are copied to ContosoOrders.Core/Modern (ContosoOrders.Core.Modern); callers live in ContosoOrders.Core/Consumers. A generated
// Gen/Api.g.cs file is never rewritten, so a caller that passes the new type to its Take(Legacy.Widget) cannot move and stays on the old type.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WholeFileWriteTools")]
public sealed class MoveFileCopyAndObsoleteTests
{
    private const string CoreFolder = "ContosoOrders.Core";
    private const string OldNamespace = "ContosoOrders.Core.Legacy";
    private const string NewNamespace = "ContosoOrders.Core.Modern";

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

    private static string PathIn(TestSolutionFixture fixture, params string[] segments) =>
        Path.Combine(new[] { fixture.SolutionDirectory }.Concat(segments).ToArray());

    // Lines joined with the given line ending plus a final one, so the seeded text does not depend on this file's own line endings.
    private static string Join(string eol, params string[] lines) => string.Join(eol, lines) + eol;

    private static string WidgetText(string eol = "\r\n") => Join(eol, $"namespace {OldNamespace};", "", "public class Widget", "{", "    public int Size;", "}");

    private static string CallerText(string name, string eol = "\r\n") =>
        Join(eol, $"using {OldNamespace};", "", "namespace ContosoOrders.Core.Consumers;", "", $"public class {name}", "{", "    public Widget Item = new Widget();", "}");

    private static string StuckText() =>
        Join("\r\n", $"using {OldNamespace};", "", "namespace ContosoOrders.Core.Consumers;", "", "public class Stuck", "{", "    public void Run()", "    {", "        ContosoOrders.Core.Gen.Api.Take(new Widget());", "    }", "}");

    private static string GeneratedApiText() =>
        Join("\r\n", "namespace ContosoOrders.Core.Gen;", "", "public static class Api", "{", $"    public static void Take({OldNamespace}.Widget widget)", "    {", "    }", "}");

    // Writes the files to disk (relative to the solution directory), then reloads the solution once and acknowledges the external changes.
    private static async Task SeedAsync(TestSolutionFixture fixture, IWorkspaceManager manager, params (string Relative, string Content)[] files)
    {
        foreach (var (relative, content) in files)
        {
            await fixture.AddFileToSolution(manager, relative, content, reloadSolution: false);
        }

        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        manager.ClearExternalFileChanges();
    }

    private static async Task<IReadOnlyList<Diagnostic>> CompileCoreAsync(IWorkspaceManager manager)
    {
        var solution = await manager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var project = solution.Projects.Single(candidate => candidate.Name == CoreFolder);
        var compilation = await project.GetCompilationAsync();
        return compilation!.GetDiagnostics();
    }

    private static List<Diagnostic> ErrorsOf(IEnumerable<Diagnostic> diagnostics) => diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();

    private static string FileOf(Diagnostic diagnostic) => Path.GetFileName(diagnostic.Location.SourceTree?.FilePath ?? string.Empty);

    // True when the three bytes at the offset are the UTF-8 BOM (EF BB BF).
    private static bool HasBomAt(byte[] bytes, int offset) =>
        bytes.Length >= offset + 3 && bytes[offset] == 0xEF && bytes[offset + 1] == 0xBB && bytes[offset + 2] == 0xBF;

    private static void AssertRefused(SentinelCallToolResult<AppliedChangeSummary> result, string expectedCode, params string[] expectedFragments)
    {
        Assert.That(result.IsError, Is.True, "The call should have been refused.");
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

    private static string OperationsFolder(TestSolutionFixture fixture) => Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations");

    private static int OperationBlobCount(TestSolutionFixture fixture) =>
        Directory.Exists(OperationsFolder(fixture)) ? Directory.GetFiles(OperationsFolder(fixture), "*.json").Length : 0;

    private static Task<SentinelCallToolResult<AppliedChangeSummary>> CopyAsync(WholeFileWriteTools tools, string source, string destination) =>
        tools.MoveFileTool(reason: "test message", [new FileMovePair { SourcePath = source, DestinationPath = destination }], MoveFileNamespaceHandling.copyAndObsolete);

    [Test]
    public async Task CopyAndObsolete_TwoCallers_MigratesOneAndListsTheOtherWithItsErrorAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await SeedAsync(fixture, manager,
            (Path.Combine(CoreFolder, "Legacy", "Widget.cs"), WidgetText()),
            (Path.Combine(CoreFolder, "Gen", "Api.g.cs"), GeneratedApiText()),
            (Path.Combine(CoreFolder, "Consumers", "Good.cs"), CallerText("Good")),
            (Path.Combine(CoreFolder, "Consumers", "Stuck.cs"), StuckText()));
        var errorsBefore = ErrorsOf(await CompileCoreAsync(manager)).Count;
        var source = PathIn(fixture, CoreFolder, "Legacy", "Widget.cs");
        var destination = PathIn(fixture, CoreFolder, "Modern", "Widget.cs");

        var result = await CopyAsync(tools, source, destination);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.That(summary.Status, Is.EqualTo("applied"));
        Assert.That(summary.ChangeId, Is.Not.Null.And.Not.Empty);
        Assert.That(summary.AffectedFiles.Select(file => Path.GetFileName(file.Absolute)), Is.EquivalentTo(new[] { "Widget.cs", "Widget.cs", "Good.cs" }), "The copy, the original and the one migrated caller.");
        Assert.That(summary.Description, Does.Contain("Good.cs (Using"), "The migrated caller is listed with its mode.");
        Assert.That(summary.Description, Does.Contain("Stuck.cs:").And.Contain("CS1503"), "The remaining caller is listed with its first reference line and the blocking error id.");

        Assert.That(File.ReadAllText(PathIn(fixture, CoreFolder, "Consumers", "Good.cs")), Does.Contain($"using {NewNamespace};").And.Not.Contain("Legacy"));
        Assert.That(File.ReadAllText(PathIn(fixture, CoreFolder, "Consumers", "Stuck.cs")), Is.EqualTo(StuckText()), "The remaining caller is unchanged.");

        // The compile state of the applied solution: no new error, and the remaining caller only warns about the [Obsolete] type.
        var diagnostics = await CompileCoreAsync(manager);
        Assert.That(ErrorsOf(diagnostics).Count, Is.EqualTo(errorsBefore), string.Join(" | ", ErrorsOf(diagnostics).Select(diagnostic => $"{diagnostic.Id} {FileOf(diagnostic)} {diagnostic.GetMessage()}")));
        var stuckWarnings = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning && FileOf(diagnostic) == "Stuck.cs").Select(diagnostic => diagnostic.Id).Distinct().ToList();
        Assert.That(stuckWarnings, Is.EqualTo(new[] { "CS0618" }));
        Assert.That(diagnostics.Where(diagnostic => FileOf(diagnostic) == "Good.cs" && diagnostic.Id == "CS0618"), Is.Empty, "The migrated caller no longer uses the obsolete type.");
    }

    [Test]
    public async Task CopyAndObsolete_DestinationInTheSameNamespace_IsRefusedAndWritesNothingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await SeedAsync(fixture, manager, (Path.Combine(CoreFolder, "Legacy", "Same.cs"), Join("\r\n", $"namespace {OldNamespace};", "", "public class Same", "{", "}")));
        var source = PathIn(fixture, CoreFolder, "Legacy", "Same.cs");
        var destination = PathIn(fixture, CoreFolder, "Legacy", "SameMoved.cs");
        var before = File.ReadAllText(source);

        var result = await CopyAsync(tools, source, destination);

        AssertRefused(result, ToolErrorCode.InvalidArgument, OldNamespace, "namespaceHandling keep");
        Assert.That(File.ReadAllText(source), Is.EqualTo(before));
        Assert.That(File.Exists(destination), Is.False);
        Assert.That(OperationBlobCount(fixture), Is.Zero);
    }

    [Test]
    public async Task CopyAndObsolete_TypePartialAcrossFiles_IsRefusedAndWritesNothingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        var partOne = Join("\r\n", $"namespace {OldNamespace};", "", "public partial class Split", "{", "    public int One;", "}");
        var partTwo = Join("\r\n", $"namespace {OldNamespace};", "", "public partial class Split", "{", "    public int Two;", "}");
        await SeedAsync(fixture, manager,
            (Path.Combine(CoreFolder, "Legacy", "Split.cs"), partOne),
            (Path.Combine(CoreFolder, "Legacy", "Split.Other.cs"), partTwo));
        var source = PathIn(fixture, CoreFolder, "Legacy", "Split.cs");
        var destination = PathIn(fixture, CoreFolder, "Modern", "Split.cs");

        var result = await CopyAsync(tools, source, destination);

        AssertRefused(result, ToolErrorCode.TargetIneligible, "partial", "Split.Other.cs");
        Assert.That(File.ReadAllText(source), Is.EqualTo(partOne));
        Assert.That(File.ReadAllText(PathIn(fixture, CoreFolder, "Legacy", "Split.Other.cs")), Is.EqualTo(partTwo));
        Assert.That(File.Exists(destination), Is.False);
        Assert.That(OperationBlobCount(fixture), Is.Zero);
    }

    [Test]
    public async Task CopyAndObsolete_DestinationExists_IsRefusedByTheGenericValidationAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await SeedAsync(fixture, manager,
            (Path.Combine(CoreFolder, "Legacy", "Taken.cs"), Join("\r\n", $"namespace {OldNamespace};", "", "public class Taken", "{", "}")),
            (Path.Combine(CoreFolder, "Modern", "Taken.cs"), Join("\r\n", $"namespace {NewNamespace};", "", "public class Elsewhere", "{", "}")));
        var source = PathIn(fixture, CoreFolder, "Legacy", "Taken.cs");
        var destination = PathIn(fixture, CoreFolder, "Modern", "Taken.cs");
        var destinationBefore = File.ReadAllText(destination);

        var result = await CopyAsync(tools, source, destination);

        AssertRefused(result, ToolErrorCode.InvalidArgument, "already exists");
        Assert.That(File.ReadAllText(destination), Is.EqualTo(destinationBefore));
        Assert.That(OperationBlobCount(fixture), Is.Zero);
    }

    [Test]
    public async Task CopyAndObsolete_NoCallers_CopiesTheTextMarksTheOriginalAndWritesOneOperationBlobAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        var text = Join("\r\n", $"namespace {OldNamespace};", "", "public class Solo", "{", "    public int Value => 1;", "}");
        await SeedAsync(fixture, manager, (Path.Combine(CoreFolder, "Legacy", "Solo.cs"), text));
        var source = PathIn(fixture, CoreFolder, "Legacy", "Solo.cs");
        var destination = PathIn(fixture, CoreFolder, "Modern", "Solo.cs");

        var result = await CopyAsync(tools, source, destination);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.That(summary.ChangeId, Is.Not.Null.And.Not.Empty);
        Assert.That(summary.Description, Does.Contain("Copied").And.Contain(NewNamespace).And.Contain("no caller file referenced them"));
        var copy = File.ReadAllText(destination);
        Assert.That(copy, Does.Contain($"namespace {NewNamespace};").And.Contain("public class Solo").And.Not.Contain("Legacy").And.Not.Contain("Obsolete"));
        var original = File.ReadAllText(source);
        Assert.That(original, Does.Contain($"[System.Obsolete(\"Use {NewNamespace}.Solo\")]").And.Contain($"namespace {OldNamespace};"));

        Assert.That(OperationBlobCount(fixture), Is.EqualTo(1), "One operation blob for the whole change.");
        var blobPath = OperationBlobWriter.FindBlobPath(summary.ChangeId!, fixture.SolutionDirectory);
        Assert.That(blobPath, Is.Not.Null);
        Assert.That(Path.GetFileName(blobPath!), Does.StartWith("copy_and_obsolete_file_"));
        Assert.That(File.ReadAllText(blobPath!), Does.Contain("Solo.cs"));

        // A copy leaves the source in place, so Git(status) must not offer the "moved by MoveFile" hint that reads the move_file blobs.
        Assert.That(GitStatusRenameHints.ReadMovePairs(fixture.SolutionDirectory, fixture.SolutionDirectory), Is.Empty);
    }

    [Test]
    public async Task CopyAndObsolete_ThenUndoChange_RestoresTheSourceAndCallersAndRemovesTheCopyAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var workspaceTools = Battery.Basic.TestTools.WorkspaceTools(manager);
        var tools = BuildTools(manager, workspaceTools);
        await SeedAsync(fixture, manager,
            (Path.Combine(CoreFolder, "Legacy", "Widget.cs"), WidgetText()),
            (Path.Combine(CoreFolder, "Consumers", "Good.cs"), CallerText("Good")));
        var source = PathIn(fixture, CoreFolder, "Legacy", "Widget.cs");
        var destination = PathIn(fixture, CoreFolder, "Modern", "Widget.cs");
        var caller = PathIn(fixture, CoreFolder, "Consumers", "Good.cs");
        var applied = await CopyAsync(tools, source, destination);
        Assert.That(applied.IsError, Is.False, applied.ErrorData?.Message);
        Assert.That(File.ReadAllText(caller), Does.Contain(NewNamespace), "The caller was migrated before the undo.");

        var undone = await workspaceTools.UndoChangeTool(reason: "test message", applied.SuccessData!.ChangeId!);

        Assert.That(undone.IsError, Is.False, undone.ErrorData?.Message);
        Assert.That(File.ReadAllText(source), Is.EqualTo(WidgetText()), "The original is back exactly.");
        Assert.That(File.ReadAllText(caller), Is.EqualTo(CallerText("Good")), "The migrated caller is back exactly.");
        Assert.That(File.Exists(destination), Is.False, "The copy is removed.");
    }

    [Test]
    public async Task CopyAndObsolete_CallersWithBomAndWithLf_KeepTheirBomAndLineEndingsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await SeedAsync(fixture, manager,
            (Path.Combine(CoreFolder, "Legacy", "Widget.cs"), WidgetText()),
            (Path.Combine(CoreFolder, "Consumers", "BomCaller.cs"), "﻿" + CallerText("BomCaller")),
            (Path.Combine(CoreFolder, "Consumers", "LfCaller.cs"), CallerText("LfCaller", "\n")));
        var bomPath = PathIn(fixture, CoreFolder, "Consumers", "BomCaller.cs");
        var lfPath = PathIn(fixture, CoreFolder, "Consumers", "LfCaller.cs");

        var result = await CopyAsync(tools, PathIn(fixture, CoreFolder, "Legacy", "Widget.cs"), PathIn(fixture, CoreFolder, "Modern", "Widget.cs"));

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.Description, Does.Contain("BomCaller.cs (").And.Contain("LfCaller.cs ("), "Both callers migrated.");
        var bomBytes = File.ReadAllBytes(bomPath);
        Assert.That(HasBomAt(bomBytes, 0), Is.True, "The BOM is kept.");
        Assert.That(HasBomAt(bomBytes, 3), Is.False, "The BOM is written once, not twice.");
        Assert.That(File.ReadAllText(bomPath), Does.Contain($"using {NewNamespace};").And.Contain("\r\n"), "The CRLF caller migrated and keeps CRLF.");
        var lfBytes = File.ReadAllBytes(lfPath);
        Assert.That(Array.IndexOf(lfBytes, (byte)'\r'), Is.EqualTo(-1), "The LF caller gains no CR.");
        Assert.That(HasBomAt(lfBytes, 0), Is.False, "No BOM appears on the LF caller.");
        Assert.That(File.ReadAllText(lfPath), Does.Contain($"using {NewNamespace};"));
    }

    [Test]
    public async Task CopyAndObsolete_SourceWithBom_KeepsTheBomOnceInTheCopyAndTheOriginalAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await SeedAsync(fixture, manager, (Path.Combine(CoreFolder, "Legacy", "BomWidget.cs"), "﻿" + Join("\r\n", $"namespace {OldNamespace};", "", "public class BomWidget", "{", "}")));
        var source = PathIn(fixture, CoreFolder, "Legacy", "BomWidget.cs");
        var destination = PathIn(fixture, CoreFolder, "Modern", "BomWidget.cs");

        var result = await CopyAsync(tools, source, destination);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        foreach (var path in new[] { source, destination })
        {
            var bytes = File.ReadAllBytes(path);
            Assert.That(HasBomAt(bytes, 0), Is.True, $"{Path.GetFileName(path)} keeps the BOM.");
            Assert.That(HasBomAt(bytes, 3), Is.False, $"{Path.GetFileName(path)} has the BOM once.");
        }
    }

    [Test]
    public async Task CopyAndObsolete_TypeWithStaticFields_IsListedAsHavingStateTwiceAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await SeedAsync(fixture, manager, (Path.Combine(CoreFolder, "Legacy", "Tally.cs"), Join("\r\n", $"namespace {OldNamespace};", "", "public class Tally", "{", "    public static int Count;", "}")));

        var result = await CopyAsync(tools, PathIn(fixture, CoreFolder, "Legacy", "Tally.cs"), PathIn(fixture, CoreFolder, "Modern", "Tally.cs"));

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.Description, Does.Contain("own static state: Tally"));
    }

    // Counts the instance-form compiles (the chokepoint wrapper's compile gate goes through ValidateChangesWithCandidateAsync). The migration compiles through the
    // static ValidationEngine entry point, which this spy does not see: a count of 0 here means the wrapper added no compile on top of the migration's own.
    private sealed class CountingValidationEngine : ValidationEngine
    {
        private int _compiles;

        public CountingValidationEngine(ISolutionProvider workspaceManager)
            : base(workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance)
        {
        }

        public int Compiles => Volatile.Read(ref _compiles);

        public override Task<ValidationCandidateResult> ValidateChangesWithCandidateAsync(IReadOnlyDictionary<FilePathWrapper, string> fileChanges, IReadOnlyCollection<FilePathWrapper>? removePaths = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _compiles);
            return base.ValidateChangesWithCandidateAsync(fileChanges, removePaths, cancellationToken);
        }
    }

    private static WholeFileWriteTools BuildToolsWith(IWorkspaceManager manager, ValidationEngine validationEngine) =>
        Battery.Basic.TestTools.WholeFileWriteTools(manager, Battery.Basic.TestTools.WorkspaceTools(manager), validationEngine, Battery.Basic.TestEngines.DiffEngine(), NullLogger<WholeFileWriteTools>.Instance, Battery.Basic.TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance));

    [Test]
    public async Task CopyAndObsolete_Success_AddsNoSecondCompileOnTopOfTheMigrationsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var spy = new CountingValidationEngine(manager);
        var tools = BuildToolsWith(manager, spy);
        await SeedAsync(fixture, manager,
            (Path.Combine(CoreFolder, "Legacy", "Widget.cs"), WidgetText()),
            (Path.Combine(CoreFolder, "Consumers", "Good.cs"), CallerText("Good")));

        var result = await CopyAsync(tools, PathIn(fixture, CoreFolder, "Legacy", "Widget.cs"), PathIn(fixture, CoreFolder, "Modern", "Widget.cs"));

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(spy.Compiles, Is.EqualTo(0), "The migration already compiled this exact set; the chokepoint wrapper must not compile it again.");
        Assert.That(File.ReadAllText(PathIn(fixture, CoreFolder, "Consumers", "Good.cs")), Does.Contain($"using {NewNamespace};"));
        Assert.That(File.Exists(PathIn(fixture, CoreFolder, "Modern", "Widget.cs")), Is.True);
    }

    [Test]
    public async Task ValidateAndApply_PreValidatedAgainstTheCommittedSolution_SkipsTheCompileAndAnotherSolutionStillCompilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var spy = new CountingValidationEngine(manager);
        var committed = await manager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var broken = new DocumentChangeSet { [PathIn(fixture, CoreFolder, "Modern", "Broken.cs")] = Join("\r\n", $"namespace {NewNamespace};", "", "public class Broken", "{", "    public int Value = ;", "}") };

        var skipped = await ValidateAndApplyHelper.ValidateAndApplyAsync(spy, manager, NullLogger.Instance, broken, "spy_test", dryRun: true, preValidatedAgainst: committed);

        Assert.That(skipped.Error, Is.Null, "The caller vouched for the text, so the wrapper trusts it while the committed solution is unchanged.");
        Assert.That(spy.Compiles, Is.EqualTo(0));

        // The workspace moved on (a different Solution instance): the vouching is void and the wrapper compiles and refuses as before.
        var moved = committed.Projects.First().AddDocument("Spacer.cs", "class Spacer { }").Project.Solution;
        var refused = await ValidateAndApplyHelper.ValidateAndApplyAsync(spy, manager, NullLogger.Instance, broken, "spy_test", dryRun: true, preValidatedAgainst: moved);

        Assert.That(refused.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(spy.Compiles, Is.EqualTo(1));

        var unvouched = await ValidateAndApplyHelper.ValidateAndApplyAsync(spy, manager, NullLogger.Instance, broken, "spy_test", dryRun: true);

        Assert.That(unvouched.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed), "Without the parameter every caller compiles as before.");
        Assert.That(spy.Compiles, Is.EqualTo(2));
    }

    [Test]
    public async Task CopyAndObsolete_CopyThatBreaksTheBuild_IsRefusedAsValidationFailedAndWritesNothingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var spy = new CountingValidationEngine(manager);
        var tools = BuildToolsWith(manager, spy);
        // Modern already declares Widget in another file, so the copy duplicates it (CS0101) in the base set.
        await SeedAsync(fixture, manager,
            (Path.Combine(CoreFolder, "Legacy", "Widget.cs"), WidgetText()),
            (Path.Combine(CoreFolder, "Modern", "Existing.cs"), Join("\r\n", $"namespace {NewNamespace};", "", "public class Widget", "{", "}")));
        var source = PathIn(fixture, CoreFolder, "Legacy", "Widget.cs");
        var destination = PathIn(fixture, CoreFolder, "Modern", "Widget.cs");
        var before = File.ReadAllText(source);

        var result = await CopyAsync(tools, source, destination);

        AssertRefused(result, ToolErrorCode.ValidationFailed);
        Assert.That(File.ReadAllText(source), Is.EqualTo(before));
        Assert.That(File.Exists(destination), Is.False);
    }
}

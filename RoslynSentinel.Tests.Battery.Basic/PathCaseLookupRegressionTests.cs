// Regression coverage for blocker blocking_error_path_lookup_case_sensitive_drive_letter_replacesnippet_file_not_found.
//
// The solution is loaded through a LOWERCASED path (Windows: the lowercased spelling is the same
// file, so MSBuild opens it; the workspace then holds lowercased document paths), and each tool is
// then called with a differently-cased spelling of the same file. Before the fix every tool below
// answered "File not found" / errorCode "Exception", because the lookup compared paths with a
// case-sensitive `==` on the string produced by FilePathWrapper's implicit conversion.
//
// Two spellings are exercised: a mixed-case directory (any OS) and an uppercased drive letter
// (Windows only - skipped elsewhere, where drive letters do not exist).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
public class PathCaseLookupRegressionTests
{
    public enum PathSpelling
    {
        /// <summary>Directory part case-flipped, drive letter left matching the loaded solution.</summary>
        MixedCaseDirectory,

        /// <summary>Everything matches the loaded (lowercased) solution except an UPPERCASED drive letter.</summary>
        UpperDriveLetter,
    }

    private sealed record Harness(
        TestSolutionFixture Fixture,
        PersistentWorkspaceManager Workspace,
        WorkspaceTools Tools,
        WholeFileWriteTools WriteTools) : IDisposable
    {
        public void Dispose()
        {
            Workspace.Dispose();
            Fixture.Dispose();
        }
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var fixture = new TestSolutionFixture();
        var workspace = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        // Windows paths are case-insensitive, so loading through the lowercased spelling is the
        // blocker's exact repro (workspace document paths are lowercased). Elsewhere a lowercased
        // path would not exist, so load the real one and rely on the mixed-case spelling alone.
        var loadPath = OperatingSystem.IsWindows() ? fixture.SolutionPath.ToLowerInvariant() : fixture.SolutionPath;
        await workspace.LoadSolutionAsync(loadPath);

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var diagnosticEngine = new DiagnosticEngine(workspace);
        var validationEngine = new ValidationEngine(workspace, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNavigationEngine = new SymbolNavigationEngine(workspace, NullLogger<SymbolNavigationEngine>.Instance);
        var tools = new WorkspaceTools(
            workspace,
            validationEngine,
            diffEngine, diagnosticEngine,
            new SolutionManagementEngine(workspace),
            new StructuralRefinementEngine(workspace, config),
            new DependencyEngine(workspace),
            new ProjectConsistencyEngine(workspace),
            config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(workspace, diagnosticEngine),
            symbolNavigationEngine,
            new TestRunEngine(workspace),
            new WorkspaceReadNavigationImpl(workspace, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var writeTools = new WholeFileWriteTools(workspace, tools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, symbolNavigationEngine);
        return new Harness(fixture, workspace, tools, writeTools);
    }

    private static string RealPath(Harness h, params string[] relative)
        => Path.Combine([h.Fixture.SolutionDirectory, .. relative]);

    /// <summary>Spells <paramref name="realPath"/> the way <paramref name="spelling"/> says, or ignores the test.</summary>
    private static string Spell(PathSpelling spelling, string realPath)
    {
        switch (spelling)
        {
            case PathSpelling.MixedCaseDirectory:
            {
                var directory = Path.GetDirectoryName(realPath)!;
                var flipped = directory.Length > 1
                    ? char.ToLowerInvariant(directory[0]) + directory[1..].ToUpperInvariant()
                    : directory;
                return Path.Combine(flipped, Path.GetFileName(realPath));
            }

            case PathSpelling.UpperDriveLetter:
            {
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Ignore("Drive letters exist only on Windows.");
                }

                // Everything lowercased (== the loaded solution's spelling) except the drive letter.
                var lowered = realPath.ToLowerInvariant();
                return char.ToUpperInvariant(lowered[0]) + lowered[1..];
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(spelling));
        }
    }

    private static async Task<string> FirstLineAsync(string path)
        => (await File.ReadAllTextAsync(path)).Split('\n')[0].TrimEnd('\r');

    [TestCase(PathSpelling.MixedCaseDirectory)]
    [TestCase(PathSpelling.UpperDriveLetter)]
    public async Task ReplaceSnippet_Single_MisCasedPath_FindsDocumentAndWritesAsync(PathSpelling spelling)
    {
        using var h = await CreateHarnessAsync();
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");
        var anchor = await FirstLineAsync(real);

        var result = await h.Tools.ReplaceSnippet(
            reason: "path case regression single",
            ProposedChangeAction.apply,
            filePath: Spell(spelling, real),
            oldContent: anchor,
            newContent: anchor + " // single-touched");

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(real), Does.Contain("// single-touched"));
    }

    [TestCase(PathSpelling.MixedCaseDirectory)]
    [TestCase(PathSpelling.UpperDriveLetter)]
    public async Task ReplaceSnippet_Batch_MisCasedPaths_FindsDocumentsAndWritesAsync(PathSpelling spelling)
    {
        using var h = await CreateHarnessAsync();
        var realA = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");
        var realB = RealPath(h, "ContosoOrders.Core", "OrderLine.cs");
        var anchorA = await FirstLineAsync(realA);
        var anchorB = await FirstLineAsync(realB);

        var result = await h.Tools.ReplaceSnippet(
            reason: "path case regression batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = Spell(spelling, realA), OldContent = anchorA, NewContent = anchorA + " // batch-a" },
                new SnippetEdit { FilePath = Spell(spelling, realB), OldContent = anchorB, NewContent = anchorB + " // batch-b" },
            ]);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
        Assert.Multiple(async () =>
        {
            Assert.That(await File.ReadAllTextAsync(realA), Does.Contain("// batch-a"));
            Assert.That(await File.ReadAllTextAsync(realB), Does.Contain("// batch-b"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_Batch_SameFileUnderTwoSpellings_IsRejectedNotDoubleAppliedAsync()
    {
        using var h = await CreateHarnessAsync();
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");
        var original = await File.ReadAllTextAsync(real);
        var anchor = await FirstLineAsync(real);

        var result = await h.Tools.ReplaceSnippet(
            reason: "path case regression same file two spellings",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = real, OldContent = anchor, NewContent = anchor + " // one" },
                new SnippetEdit { FilePath = Spell(PathSpelling.MixedCaseDirectory, real), OldContent = "Pending = 0", NewContent = "Pending = 0 // two" },
            ]);

        Assert.That(result.IsSuccess, Is.False, "two spellings of one file must not silently produce two competing rewrites");
        Assert.That(await File.ReadAllTextAsync(real), Is.EqualTo(original), "a rejected batch must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_BareNameMatchingTwoDocuments_IsAmbiguousNotFirstWinsAsync()
    {
        using var h = await CreateHarnessAsync();
        await h.Fixture.AddFileToSolution(h.Workspace, Path.Combine("ContosoOrders.Tests", "OrderStatus.cs"), "class DuplicateNameHolder { }");
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");
        var original = await File.ReadAllTextAsync(real);

        var result = await h.Tools.ReplaceSnippet(
            reason: "path case regression bare duplicate name",
            ProposedChangeAction.apply,
            filePath: "OrderStatus.cs",
            oldContent: "Pending = 0",
            newContent: "Pending = 0 // must-not-land");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("ContosoOrders.Tests").And.Contain("ContosoOrders.Core"),
            "the error must name the candidate files so the caller can retry with a full path");
        Assert.That(await File.ReadAllTextAsync(real), Is.EqualTo(original));
    }

    [TestCase(PathSpelling.MixedCaseDirectory)]
    [TestCase(PathSpelling.UpperDriveLetter)]
    public async Task ApplyDiff_DiffFormat_MisCasedPath_FindsDocumentAndWritesAsync(PathSpelling spelling)
    {
        using var h = await CreateHarnessAsync();
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");

        var result = await h.WriteTools.ApplyDiff(
            reason: "path case regression ApplyDiff",
            ChangesetFormat.diff,
            ProposedChangeAction.apply,
            filePath: Spell(spelling, real),
            unifiedDiff: ThreeLineDiff("diff-touched"));

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(real), Does.Contain("// diff-touched"));
    }

    [TestCase(PathSpelling.MixedCaseDirectory)]
    [TestCase(PathSpelling.UpperDriveLetter)]
    public async Task ApplyUnifiedDiff_MisCasedPath_FindsDocumentAndWritesAsync(PathSpelling spelling)
    {
        using var h = await CreateHarnessAsync();
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");

        var result = await h.WriteTools.ApplyUnifiedDiff(
            reason: "path case regression ApplyUnifiedDiff",
            ProposedChangeAction.apply,
            filepath: Spell(spelling, real),
            unifiedDiff: ThreeLineDiff("unified-touched"));

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(real), Does.Contain("// unified-touched"));
    }

    // OrderStatus.cs starts: "namespace ContosoOrders.Core;", "", "public enum OrderStatus".
    private static string ThreeLineDiff(string marker) =>
        "--- a/OrderStatus.cs\n+++ b/OrderStatus.cs\n@@ -1,3 +1,3 @@\n namespace ContosoOrders.Core;\n \n-public enum OrderStatus\n+public enum OrderStatus // " + marker + "\n";

    [TestCase(PathSpelling.MixedCaseDirectory)]
    [TestCase(PathSpelling.UpperDriveLetter)]
    public async Task Search_SymbolMode_MisCasedFilePath_ResolvesAsync(PathSpelling spelling)
    {
        using var h = await CreateHarnessAsync();
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");

        var result = await h.Tools.SearchSolution(
            reason: "path case regression Search symbol",
            SearchMode.symbol,
            query: "OrderStatus",
            filePath: Spell(spelling, real));

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
    }

    [TestCase(PathSpelling.MixedCaseDirectory)]
    [TestCase(PathSpelling.UpperDriveLetter)]
    public async Task Search_ReferencesMode_MisCasedFilePath_ResolvesAsync(PathSpelling spelling)
    {
        using var h = await CreateHarnessAsync();
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");

        var result = await h.Tools.SearchSolution(
            reason: "path case regression Search references",
            SearchMode.references,
            query: "OrderStatus",
            referencesKind: FindReferencesKind.callers,
            filePath: Spell(spelling, real),
            contextSnippet: "public enum OrderStatus");

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
    }

    [TestCase(PathSpelling.MixedCaseDirectory)]
    [TestCase(PathSpelling.UpperDriveLetter)]
    public async Task FindReferences_MisCasedFilePath_ResolvesAsync(PathSpelling spelling)
    {
        using var h = await CreateHarnessAsync();
        var real = RealPath(h, "ContosoOrders.Core", "OrderStatus.cs");
        var impl = new SymbolRelationshipImpl(
            new DiscoveryEngine(h.Workspace), new SymbolNavigationEngine(h.Workspace, NullLogger<SymbolNavigationEngine>.Instance),
            h.Workspace, NullLogger<SymbolRelationshipImpl>.Instance);

        var result = await impl.FindReferences(
            reason: "path case regression FindReferences",
            symbolName: "OrderStatus",
            kind: FindReferencesKind.callers,
            filepath: Spell(spelling, real),
            contextSnippet: "public enum OrderStatus");

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.ErrorCode + ": " + result.ErrorData?.Message);
    }

    [Test]
    public async Task FindReferences_FileNotInSolution_ReportsNotFoundNotExceptionAsync()
    {
        using var h = await CreateHarnessAsync();
        var missing = RealPath(h, "ContosoOrders.Core", "DoesNotExist.cs");
        var impl = new SymbolRelationshipImpl(
            new DiscoveryEngine(h.Workspace), new SymbolNavigationEngine(h.Workspace, NullLogger<SymbolNavigationEngine>.Instance),
            h.Workspace, NullLogger<SymbolRelationshipImpl>.Instance);

        var result = await impl.FindReferences(
            reason: "path case regression missing file",
            symbolName: "OrderStatus",
            kind: FindReferencesKind.callers,
            filepath: missing,
            contextSnippet: "public enum OrderStatus");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound),
            "a file missing from the solution is a NotFound, not a raw 'Exception'");
    }

    // Evidence for the GetDiagnostics(scope: file) + relative path question: DiagnosticEngine feeds the
    // implicit string -> FilePathWrapper conversion straight into GetDocumentIdsWithFilePath.
    [Test]
    public async Task GetFileDiagnostics_RelativePath_ResolvesOnlyWithinSolutionRootScopeAsync()
    {
        using var h = await CreateHarnessAsync();
        var engine = new DiagnosticEngine(h.Workspace);
        var relative = Path.Combine("ContosoOrders.Core", "OrderStatus.cs");

        Assert.ThrowsAsync<FileNotFoundException>(async () => await engine.GetFileDiagnosticsAsync(relative),
            "without a solution-root scope a relative string stays unrooted and can never match a document path");

        using (FilePathWrapper.UseSolutionRoot(h.Workspace.GetSolutionRoot()))
        {
            var scoped = await engine.GetFileDiagnosticsAsync(relative);
            Assert.That(scoped.Outcome, Is.EqualTo(EngineOutcome.Success));
        }
    }
}

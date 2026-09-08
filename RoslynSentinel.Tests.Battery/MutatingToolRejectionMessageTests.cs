// Regression coverage for the write-path chokepoint (ApplyProposedChangesAsync, shared by
// WriteFile/ApplyDiff/ApplyUnifiedDiff — see docs/current/project_write_path_chokepoint_unified.md):
// when a pre-apply compile check rejects a change, the returned error message must go through
// CompilerErrorLookupHelper, not leak the raw ValidationResult.Diagnostics.ToJson() blob. WriteFile
// shipped with the raw-JSON message for a while (fixed 2026-09-05, commit 3a4c521) because nothing
// asserted on rejection-message *content* — CompilerErrorLookupHelperTests.cs unit-tests the helper
// itself but never calls a tool, and CreateFileDeleteFileTests.cs calls WriteFile but every
// validation-adjacent case there uses validateOnApply:false or only asserts on ErrorCode. This file
// closes that gap for all three mutating tools sharing the chokepoint.

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class MutatingToolRejectionMessageTests
{
    private const string HelperFileContent = """
        namespace TestProj;

        public static class Helper
        {
            public static string Format(string value) => value;
        }
        """;

    private const string CallerFileContent = """
        namespace TestProj;

        public class Caller
        {
            public string Describe(string value) => Helper.Format(value);
        }
        """;

    // Renames Helper.Format -> Helper.FormatValue without updating Caller's call site, the exact
    // cross-file rename-desync shape from the sequential-edit-habit theory (CS1061).
    private const string HelperFileContentRenamed = """
        namespace TestProj;

        public static class Helper
        {
            public static string FormatValue(string value) => value;
        }
        """;

    private static SentinelWorkspaceTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(workspaceManager);
        return new SentinelWorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager),
            new WorkspaceReadNavigationTools(new WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance)));
    }

    private static void AssertRoutedThroughLookupHelper(ToolResult<object> result)
    {
        Assert.That(result.Success, Is.False, "the rename-desync edit should be rejected by pre-apply validation");
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Does.Not.Contain("\"Id\":"),
            "must not leak the raw ValidationResult.Diagnostics.ToJson() blob to the model");
        Assert.That(result.Error!.Message, Does.Not.Contain("\"Severity\":"),
            "must not leak the raw ValidationResult.Diagnostics.ToJson() blob to the model");
        Assert.That(result.Error!.Message, Does.Contain("does not contain a definition"),
            "should surface CompilerErrorLookupHelper's human-readable CS1061 guidance");
    }

    [Test]
    public async Task WriteFile_IntroducesCompileError_RejectsWithLookupHelperGuidanceNotRawJson()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, "ContosoOrders.Core/Helper.cs", HelperFileContent);
        await fixture.AddFileToSolution(workspaceManager, "ContosoOrders.Core/Caller.cs", CallerFileContent);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var helperPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Helper.cs");
        var result = await wholeFileWriteTools.WriteFile(reason: "test", WriteFileOperation.ReplaceFile, helperPath, HelperFileContentRenamed);

        AssertRoutedThroughLookupHelper(result);
    }

    [Test]
    public async Task ApplyDiff_FilesFormat_IntroducesCompileError_RejectsWithLookupHelperGuidanceNotRawJson()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, "ContosoOrders.Core/Helper.cs", HelperFileContent);
        await fixture.AddFileToSolution(workspaceManager, "ContosoOrders.Core/Caller.cs", CallerFileContent);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var helperPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Helper.cs");
        var changes = new Dictionary<string, string>
        {
            [helperPath] = HelperFileContentRenamed
        };
        var result = await wholeFileWriteTools.ApplyDiff(reason: "test", ChangesetFormat.files, ProposedChangeAction.apply, changes: changes);

        AssertRoutedThroughLookupHelper(result);
    }

    [Test]
    public async Task ApplyUnifiedDiff_IntroducesCompileError_RejectsWithLookupHelperGuidanceNotRawJson()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, "ContosoOrders.Core/Helper.cs", HelperFileContent);
        await fixture.AddFileToSolution(workspaceManager, "ContosoOrders.Core/Caller.cs", CallerFileContent);
        var workspaceTools = BuildTools(workspaceManager);

        var helperPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Helper.cs");
        var unifiedDiff =
            "--- Helper.cs\n+++ Helper.cs\n" +
            "@@ -1,5 +1,5 @@\n" +
            " namespace TestProj;\n" +
            " \n" +
            " public static class Helper\n" +
            "-{\n" +
            "-    public static string Format(string value) => value;\n" +
            "+{\n" +
            "+    public static string FormatValue(string value) => value;\n" +
            " }\n";

        var result = await workspaceTools.ApplyUnifiedDiff(reason: "test", ProposedChangeAction.apply, filepath: helperPath, unifiedDiff: unifiedDiff);

        AssertRoutedThroughLookupHelper(result);
    }
}

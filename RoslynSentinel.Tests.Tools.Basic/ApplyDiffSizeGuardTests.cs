// ApplyDiffTool whole-file-rewrite size guard (changesetFormat=files, action=apply): a files-format
// apply that would shrink a file by more than 50% -> either by raw line count, or by active
// (non-comment) C# code lines -> is rejected with errorCode=ConfirmationRequired instead of being
// applied. The raw line-count check guards against an agent submitting only a changed fragment as
// if it were the entire file (see
// docs/current/blockers/blocking_error_searchmode_literal_override_and_iserror_flag.md, "reported
// symptom" section, for the real incident this replays). The active-code-line check guards against
// the same intent expressed differently: commenting out every line of the file one-for-one, which
// leaves the raw line count unchanged (so the first check alone misses it) but leaves the file with
// no working code (see ModelTestingResults/113/Model_FixesWholeFileRewriteBug_PlanImplementVerify/
// 20260902-062730-159, where ApplyDiffTool replaced BlockConverter.cs with every line prefixed "//" and
// reported success because the line count and compile both looked fine). ApplyDiffTool itself no longer offers a
// confirmationCode replay path -> that mechanism reliably caused model hallucination (agents would
// fabricate a confirmationCode and call action=confirmationCode even when the true problem was
// something else entirely; see docs/current/overnight-run-2026-08-30.md section 5b for the traced
// root cause) and was never used correctly in practice. The rejected caller is expected to just
// re-submit the complete file content. The old replay mechanism is preserved, unregistered, on
// ApplyDiffWithConfirmationCode in case it's wanted again -> the confirmationCode-specific tests
// below target that method directly rather than ApplyDiffTool.
//
// The percentage check and the EOL normalization read the "old content" through
// ExistingTextReader.ReadAsync: the committed workspace document's text when the file is a document
// of the loaded solution, else the file on disk. So these tests run on InMemoryWorkspace (a rejected
// or applied change is read back with ReadText); only the disk-fallback tests at the bottom use a
// real temp file, because they are about a file that is NOT in the solution.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("SymbolNavigationEngine")] // sentinel:auto-category
[Category("WholeFileWriteTools")] // sentinel:auto-category
public class ApplyDiffSizeGuardTests
{
    private const string TargetRelativePath = "ContosoOrders.Core/Order.cs";

    // CRLF on purpose: the EOL-normalization test needs a dominant EOL that differs from the LF the caller submits.
    // Built with explicit escapes (not a raw string) so the content does not depend on this source file's line endings.
    private static readonly string TargetContent = string.Join("\r\n",
        "namespace ContosoOrders.Core;",
        "",
        "public class Order",
        "{",
        "    public int Id { get; set; }",
        "    public string Name { get; set; } = \"\";",
        "    public decimal Total { get; set; }",
        "    public int Quantity { get; set; }",
        "}",
        "");

    private static WholeFileWriteTools BuildWholeFileWriteTools(IWorkspaceManager workspaceManager)
    {
        var diffEngine = TestEngines.DiffEngine();
        var validationEngine = TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return TestTools.WholeFileWriteTools(workspaceManager, BuildTools(workspaceManager), validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        TestTools.WorkspaceTools(workspaceManager);

    [Test]
    public async Task ApplyDiff_FilesFormatShrinksOver50Percent_RejectsWithConfirmationRequiredAsync()
    {
        using var workspace = InMemoryWorkspace.Create((TargetRelativePath, TargetContent));
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);
        Assert.That(TargetContent.Split('\n').Length, Is.GreaterThan(4), "fixture file must have enough lines for a >50% shrink to be meaningful");

        var fragment = "using System;\n";
        var result = await wholeFileWriteTools.ApplyDiffTool(
            reason: "test message", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<string, string> { [targetFile] = fragment });

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("ConfirmationRequired"));
        Assert.That(result.ErrorData!.Message, Does.Contain("re-submit"));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(TargetContent), "rejected apply must not touch the workspace");
    }

    [Test]
    public async Task ApplyDiff_FilesFormatCommentsOutWholeFile_RejectsWithConfirmationRequiredAsync()
    {
        using var workspace = InMemoryWorkspace.Create((TargetRelativePath, TargetContent));
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);

        // Same line count as the original (each line prefixed "// " rather than removed), so the
        // raw line-count guard sees 0% shrink -> only the active-code-line guard should catch this.
        var commentedOut = string.Join('\n', TargetContent.Split('\n').Select(line => "// " + line));

        var result = await wholeFileWriteTools.ApplyDiffTool(
            reason: "test message", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<string, string> { [targetFile] = commentedOut });

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("ConfirmationRequired"));
        Assert.That(result.ErrorData!.Message, Does.Contain("active code lines"));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(TargetContent), "rejected apply must not touch the workspace");
    }

    // The following confirmationCode-replay tests targeted ApplyDiffWithConfirmationCode directly ->
    // that mechanism was removed from the registered ApplyDiffTool tool (see comment at top of file).
    // ApplyDiffWithConfirmationCode and ProposedChangeAction.confirmationCode are now both
    // block-commented out (WorkspaceTools.cs / ToolEnums.cs) rather than deleted, so these
    // tests are commented out alongside them -> un-comment all three together if the mechanism is
    // ever reintroduced.
    /*
    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeFromRejectedApply_AppliesOriginalChangesetAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var fragment = "using System;\n";

        // validateOnApply:false -> this test is about the confirmation-code replay mechanism, not
        // ValidateChangesAsync; the arbitrary "first .cs file" the fixture picks may be a type
        // other files in the solution depend on, which would otherwise fail pre-apply validation
        // for unrelated reasons (a real compile break, correctly caught -> see
        // project_searchmode_literal_override_bug.md's validation-scope fix).
        var rejected = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePathWrapper, string> { [targetFile] = fragment },
            validateOnApply: false);
        Assert.That(rejected.IsSuccess, Is.False);

        var code = ExtractConfirmationCode(rejected.ErrorData!.Message);

        var confirmed = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.confirmationCode,
            confirmationCode: code);

        Assert.That(confirmed.IsSuccess, Is.True, confirmed.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(fragment));
    }

    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeUnrecognized_ReturnsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.confirmationCode,
            confirmationCode: "not-a-real-code");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeMissing_ReturnsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await tools.ApplyDiffWithConfirmationCode(ChangesetFormat.files, ProposedChangeAction.confirmationCode);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeIsSingleUse_SecondReplayFailsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var rejected = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePathWrapper, string> { [targetFile] = "using System;\n" },
            validateOnApply: false);
        var code = ExtractConfirmationCode(rejected.ErrorData!.Message);

        var firstReplay = await tools.ApplyDiffWithConfirmationCode(ChangesetFormat.files, ProposedChangeAction.confirmationCode, confirmationCode: code);
        Assert.That(firstReplay.IsSuccess, Is.True, firstReplay.ErrorData?.Message);

        var secondReplay = await tools.ApplyDiffWithConfirmationCode(ChangesetFormat.files, ProposedChangeAction.confirmationCode, confirmationCode: code);
        Assert.That(secondReplay.IsSuccess, Is.False);
        Assert.That(secondReplay.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }
    */

    [Test]
    public async Task ApplyDiff_FilesFormatSmallEdit_AppliesWithoutConfirmationAsync()
    {
        using var workspace = InMemoryWorkspace.Create((TargetRelativePath, TargetContent));
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);
        // Use LF content - it will be normalized to CRLF (the dominant EOL of the existing workspace document)
        var lfContent = TargetContent.Replace("\r\n", "\n") + "\n// small trailing comment\n";

        var result = await wholeFileWriteTools.ApplyDiffTool(
            reason: "test message", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<string, string> { [targetFile] = lfContent });

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        // After normalization, LF content should be normalized to CRLF (the existing document's dominant EOL)
        var expectedNormalized = TargetContent + "\r\n// small trailing comment\r\n";
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(expectedNormalized));
    }

    [Test]
    public async Task ApplyDiff_FilesFormatNewFile_ExemptFromSizeGuardAsync()
    {
        using var workspace = InMemoryWorkspace.Create((TargetRelativePath, TargetContent));
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspace.Manager);

        // Neither a document of the workspace nor a file on disk: no old text, so nothing to shrink from.
        const string newRelativePath = "ContosoOrders.Core/BrandNewFile.cs";
        var content = "namespace ContosoOrders.Core;\npublic class BrandNewFile { }\n";

        var result = await wholeFileWriteTools.ApplyDiffTool(
            reason: "test message", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<string, string> { [workspace.PathOf(newRelativePath)] = content });

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(newRelativePath), Is.EqualTo(content));
    }

    // Stays on disk on purpose: the file is NOT a document of the loaded (in-memory) solution, so ExistingTextReader
    // must fall back to the file on disk -> the only way to prove that fallback is a real temp file.
    [Test]
    public async Task ApplyDiff_FilesFormatFileOutsideSolutionShrinksOver50Percent_RejectedViaDiskFallbackAsync()
    {
        using var workspace = InMemoryWorkspace.Create((TargetRelativePath, TargetContent));
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspace.Manager);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "RoslynSentinelSizeGuard", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var outsideFile = Path.Combine(tempDirectory, "OutsideSolution.cs");
            await File.WriteAllTextAsync(outsideFile, TargetContent);

            var result = await wholeFileWriteTools.ApplyDiffTool(
                reason: "test message", ChangesetFormat.files, ProposedChangeAction.apply,
                changes: new Dictionary<string, string> { [outsideFile] = "using System;\n" });

            Assert.That(!result.IsError, Is.False);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("ConfirmationRequired"));
            Assert.That(await File.ReadAllTextAsync(outsideFile), Is.EqualTo(TargetContent), "rejected apply must not touch disk");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    // Stays on disk on purpose, same reason: with no solution loaded at all the helper must read the disk without throwing.
    [Test]
    public async Task ExistingTextReader_NoSolutionLoaded_ReadsDiskAndMissingFileIsNullAsync()
    {
        using var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadWrite);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "RoslynSentinelSizeGuard", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var onDiskFile = Path.Combine(tempDirectory, "NoSolution.cs");
            await File.WriteAllTextAsync(onDiskFile, TargetContent);

            Assert.That(await ExistingTextReader.ReadAsync(workspaceManager, onDiskFile, CancellationToken.None), Is.EqualTo(TargetContent));
            Assert.That(await ExistingTextReader.ReadAsync(workspaceManager, Path.Combine(tempDirectory, "Missing.cs"), CancellationToken.None), Is.Null);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string ExtractConfirmationCode(string message)
    {
        var marker = "confirmationCode=\"";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.IndexOf('"', start);
        return message[start..end];
    }
}
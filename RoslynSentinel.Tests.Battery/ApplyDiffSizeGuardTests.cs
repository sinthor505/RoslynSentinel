// ApplyDiff whole-file-rewrite size guard (changesetFormat=files, action=apply): a files-format
// apply that would shrink a file by more than 50% — either by raw line count, or by active
// (non-comment) C# code lines — is rejected with errorCode=ConfirmationRequired instead of being
// applied. The raw line-count check guards against an agent submitting only a changed fragment as
// if it were the entire file (see
// docs/current/blockers/blocking_error_searchmode_literal_override_and_iserror_flag.md, "reported
// symptom" section, for the real incident this replays). The active-code-line check guards against
// the same intent expressed differently: commenting out every line of the file one-for-one, which
// leaves the raw line count unchanged (so the first check alone misses it) but leaves the file with
// no working code (see ModelTestingResults/113/Model_FixesWholeFileRewriteBug_PlanImplementVerify/
// 20260902-062730-159, where ApplyDiff replaced BlockConverter.cs with every line prefixed "//" and
// reported success because the line count and compile both looked fine). ApplyDiff itself no longer offers a
// confirmationCode replay path — that mechanism reliably caused model hallucination (agents would
// fabricate a confirmationCode and call action=confirmationCode even when the true problem was
// something else entirely; see docs/current/overnight-run-2026-08-30.md section 5b for the traced
// root cause) and was never used correctly in practice. The rejected caller is expected to just
// re-submit the complete file content. The old replay mechanism is preserved, unregistered, on
// ApplyDiffWithConfirmationCode in case it's wanted again — the confirmationCode-specific tests
// below target that method directly rather than ApplyDiff.
//
// The percentage check reads "old content" from disk via FileIoHelper.ReadAllTextIfExistsAsync
// (same helper ApplyProposedChangesAsync uses for pre-image capture), so these tests need a real
// on-disk file — TestSolutionFixture + PersistentWorkspaceManager, not the in-memory
// TestSolutionBuilder path (see UndoLastApplyTests.cs for the same real-revert-path rationale).

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class ApplyDiffSizeGuardTests
{
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
            new TestRunEngine(workspaceManager));
    }

    [Test]
    public async Task ApplyDiff_FilesFormatShrinksOver50Percent_RejectsWithConfirmationRequiredAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);
        Assert.That(originalContent.Split('\n').Length, Is.GreaterThan(4), "fixture file must have enough lines for a >50% shrink to be meaningful");

        var fragment = "using System;\n";
        var result = await tools.ApplyDiff(
            reason: "test", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePath, string> { [targetFile] = fragment });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("ConfirmationRequired"));
        Assert.That(result.Error!.Message, Does.Contain("re-submit"));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(originalContent), "rejected apply must not touch disk");
    }

    [Test]
    public async Task ApplyDiff_FilesFormatCommentsOutWholeFile_RejectsWithConfirmationRequiredAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);

        // Same line count as the original (each line prefixed "// " rather than removed), so the
        // raw line-count guard sees 0% shrink — only the active-code-line guard should catch this.
        var commentedOut = string.Join('\n', originalContent.Split('\n').Select(line => "// " + line));

        var result = await tools.ApplyDiff(
            reason: "test", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePath, string> { [targetFile] = commentedOut });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("ConfirmationRequired"));
        Assert.That(result.Error!.Message, Does.Contain("active code lines"));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(originalContent), "rejected apply must not touch disk");
    }

    // The following confirmationCode-replay tests targeted ApplyDiffWithConfirmationCode directly —
    // that mechanism was removed from the registered ApplyDiff tool (see comment at top of file).
    // ApplyDiffWithConfirmationCode and ProposedChangeAction.confirmationCode are now both
    // block-commented out (SentinelWorkspaceTools.cs / ToolEnums.cs) rather than deleted, so these
    // tests are commented out alongside them — un-comment all three together if the mechanism is
    // ever reintroduced.
    /*
    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeFromRejectedApply_AppliesOriginalChangesetAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var fragment = "using System;\n";

        // validateOnApply:false — this test is about the confirmation-code replay mechanism, not
        // ValidateChangesAsync; the arbitrary "first .cs file" the fixture picks may be a type
        // other files in the solution depend on, which would otherwise fail pre-apply validation
        // for unrelated reasons (a real compile break, correctly caught — see
        // project_searchmode_literal_override_bug.md's validation-scope fix).
        var rejected = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePath, string> { [targetFile] = fragment },
            validateOnApply: false);
        Assert.That(rejected.Success, Is.False);

        var code = ExtractConfirmationCode(rejected.Error!.Message);

        var confirmed = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.confirmationCode,
            confirmationCode: code);

        Assert.That(confirmed.Success, Is.True, confirmed.Error?.Message);
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(fragment));
    }

    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeUnrecognized_ReturnsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.confirmationCode,
            confirmationCode: "not-a-real-code");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeMissing_ReturnsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ApplyDiffWithConfirmationCode(ChangesetFormat.files, ProposedChangeAction.confirmationCode);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ApplyDiffWithConfirmationCode_ConfirmationCodeIsSingleUse_SecondReplayFailsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var rejected = await tools.ApplyDiffWithConfirmationCode(
            ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePath, string> { [targetFile] = "using System;\n" },
            validateOnApply: false);
        var code = ExtractConfirmationCode(rejected.Error!.Message);

        var firstReplay = await tools.ApplyDiffWithConfirmationCode(ChangesetFormat.files, ProposedChangeAction.confirmationCode, confirmationCode: code);
        Assert.That(firstReplay.Success, Is.True, firstReplay.Error?.Message);

        var secondReplay = await tools.ApplyDiffWithConfirmationCode(ChangesetFormat.files, ProposedChangeAction.confirmationCode, confirmationCode: code);
        Assert.That(secondReplay.Success, Is.False);
        Assert.That(secondReplay.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }
    */

    [Test]
    public async Task ApplyDiff_FilesFormatSmallEdit_AppliesWithoutConfirmationAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var lightlyModified = originalContent + "\n// small trailing comment\n";

        var result = await tools.ApplyDiff(
            reason: "test", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePath, string> { [targetFile] = lightlyModified });

        Assert.That(result.Success, Is.True);
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(lightlyModified));
    }

    [Test]
    public async Task ApplyDiff_FilesFormatNewFile_ExemptFromSizeGuardAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var newFilePath = Path.Combine(fixture.SolutionDirectory, Path.GetDirectoryName(
            Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First())!, "BrandNewFile.cs");
        var content = "namespace ContosoOrders;\npublic class BrandNewFile { }\n";

        var result = await tools.ApplyDiff(
            reason: "test", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<FilePath, string> { [newFilePath] = content });

        Assert.That(result.Success, Is.True);
        Assert.That(await File.ReadAllTextAsync(newFilePath), Is.EqualTo(content));
    }

    private static string ExtractConfirmationCode(string message)
    {
        var marker = "confirmationCode=\"";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.IndexOf('"', start);
        return message[start..end];
    }
}

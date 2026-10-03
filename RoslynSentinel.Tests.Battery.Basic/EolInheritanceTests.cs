// Tests that EOL normalization preserves the file's dominant line ending.
// DISK tier - asserts on real file bytes, not text content.
// Tests WriteFile and ApplyDiff which normalize LF content to match the file's existing CRLF EOL.

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;
using System.IO;
using System.Text;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("Disk")]
public class EolInheritanceTests
{
    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(workspaceManager);
        var workspaceTools = new WorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager),
            new WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        return workspaceTools;
    }

    private (int crlf, int cr, int lf) CountLineEndings(byte[] bytes)
    {
        int crlfCount = 0;
        int crCount = 0;
        int lfCount = 0;

        for (int i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0x0D && bytes[i + 1] == 0x0A)
            {
                crlfCount++;
                i++;
            }
            else if (bytes[i] == 0x0D)
            {
                crCount++;
            }
            else if (bytes[i] == 0x0A)
            {
                lfCount++;
            }
        }

        return (crlfCount, crCount, lfCount);
    }

    [Test]
    [Description("WriteFile ReplaceFile: CRLF file with LF content normalizes to CRLF")]
    public async Task WriteFile_ReplaceFile_CrlfFile_NormalizesEol()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFiles = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).ToList();
        var targetFile = targetFiles.First();
        
        // Ensure CRLF line endings
        var currentContent = File.ReadAllText(targetFile);
        var crlfContent = currentContent.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(targetFile, crlfContent, Encoding.UTF8);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var workspaceTools = BuildTools(workspaceManager);
        var wholeFileTools = new WholeFileWriteTools(
            workspaceManager,
            workspaceTools,
            new ValidationEngine(workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            new DiffEngine(),
            NullLogger<WholeFileWriteTools>.Instance,
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        // Replace with LF content
        var lfContent = "// Replaced with LF line endings\nclass Test\n{\n}\n";
        var result = await wholeFileTools.WriteFile(
            reason: "test EOL preservation",
            operation: WriteFileOperation.ReplaceFile,
            filePath: targetFile,
            content: lfContent,
            validateOnApply: false);

        Assert.That(result.IsSuccess, Is.True, $"WriteFile should succeed: {result.ErrorData?.Message}");

        var bytes = File.ReadAllBytes(targetFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.GreaterThan(0), "File should inherit original CRLF");
        Assert.That(lf, Is.EqualTo(0), "File should have no bare LF");
    }

    [Test]
    [Description("ApplyDiff files-format: CRLF file with LF content normalizes to CRLF")]
    public async Task ApplyDiff_FilesFormat_CrlfFile_NormalizesEol()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFiles = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).ToList();
        var targetFile = targetFiles.First();
        
        // Ensure CRLF
        var currentContent = File.ReadAllText(targetFile);
        var crlfContent = currentContent.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(targetFile, crlfContent, Encoding.UTF8);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var workspaceTools = BuildTools(workspaceManager);
        var wholeFileTools = new WholeFileWriteTools(
            workspaceManager,
            workspaceTools,
            new ValidationEngine(workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            new DiffEngine(),
            NullLogger<WholeFileWriteTools>.Instance,
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        // Apply with LF content via files-format
        var lfContent = "// ApplyDiff replacement with LF\nclass Replaced\n{\n}\n";
        var result = await wholeFileTools.ApplyDiff(
            reason: "test EOL preservation",
            changesetFormat: ChangesetFormat.files,
            action: ProposedChangeAction.apply,
            changes: new Dictionary<string, string> { [targetFile] = lfContent },
            validateOnApply: false);

        Assert.That(result.IsSuccess, Is.True, $"ApplyDiff should succeed: {result.ErrorData?.Message}");

        var bytes = File.ReadAllBytes(targetFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.GreaterThan(0), "File should inherit original CRLF");
        Assert.That(lf, Is.EqualTo(0), "File should have no bare LF");
    }
}

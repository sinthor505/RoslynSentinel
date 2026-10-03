// Tests that EOL normalization preserves the file's dominant line ending.
// DISK tier - asserts on real file bytes, not text content.
// Comprehensive coverage: ReplaceSnippet (single and batch), WriteFile, ApplyDiff,
// LF preservation, and edge cases (single-line files).

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

    private static (int crlf, int cr, int lf) CountLineEndings(byte[] bytes)
    {
        int crlfCount = 0;
        int crCount = 0;
        int lfCount = 0;

        // Walk every byte, including the last: a bare LF as the final byte must be counted.
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
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
    [Description("ReplaceSnippet: CRLF file with multi-line LF newContent stays CRLF")]
    public async Task ReplaceSnippet_CrlfFile_MultilineLf_PreservesEol()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        // Known CRLF seed with a unique anchor; 5 line breaks.
        File.WriteAllText(targetFile, "namespace EolSeed;\r\n\r\npublic class EolMarker\r\n{\r\n}\r\n", new UTF8Encoding(false));
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.ReplaceSnippet(
            reason: "test",
            action: ProposedChangeAction.apply,
            filePath: targetFile,
            oldContent: "public class EolMarker",
            newContent: "// first\n// second\npublic class EolMarker",
            validateOnApply: false);

        Assert.That(result.IsSuccess, Is.True, $"ReplaceSnippet should succeed: {result.ErrorData?.Message}");

        var (crlf, cr, lf) = CountLineEndings(File.ReadAllBytes(targetFile));
        Assert.That(lf, Is.EqualTo(0), "File should have no bare LF");
        Assert.That(cr, Is.EqualTo(0), "File should have no lone CR");
        Assert.That(crlf, Is.EqualTo(7), "5 original line breaks + 2 added, all CRLF");
    }

    [Test]
    [Description("ReplaceSnippet batchEdits: CRLF file with multi-line LF newContent stays CRLF")]
    public async Task ReplaceSnippetBatch_CrlfFile_MultilineLf_PreservesEol()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        File.WriteAllText(targetFile, "namespace EolSeed;\r\n\r\npublic class EolMarker\r\n{\r\n}\r\n", new UTF8Encoding(false));
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.ReplaceSnippet(
            reason: "test batch",
            action: ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit
                {
                    FilePath = targetFile,
                    OldContent = "public class EolMarker",
                    NewContent = "// first\n// second\npublic class EolMarker"
                }
            ],
            validateOnApply: false);

        Assert.That(result.IsSuccess, Is.True, $"ReplaceSnippet batch should succeed: {result.ErrorData?.Message}");

        var (crlf, cr, lf) = CountLineEndings(File.ReadAllBytes(targetFile));
        Assert.That(lf, Is.EqualTo(0), "File should have no bare LF");
        Assert.That(cr, Is.EqualTo(0), "File should have no lone CR");
        Assert.That(crlf, Is.EqualTo(7), "5 original line breaks + 2 added, all CRLF");
    }

    [Test]
    [Description("WriteFile: LF file stays LF (no CR added)")]
    public async Task WriteFile_LfFile_StaysLf()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        
        // Force LF only
        var currentContent = File.ReadAllText(targetFile);
        var lfOnlyContent = currentContent.Replace("\r\n", "\n");
        File.WriteAllText(targetFile, lfOnlyContent, Encoding.UTF8);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var workspaceTools = BuildTools(workspaceManager);
        var wholeFileTools = new WholeFileWriteTools(
            workspaceManager,
            workspaceTools,
            new ValidationEngine(workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            new DiffEngine(),
            NullLogger<WholeFileWriteTools>.Instance,
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        // Replace with mixed content
        var mixedContent = "// Mixed\nclass Test\r\n{\n}\n";
        var result = await wholeFileTools.WriteFile(
            reason: "test LF preservation",
            operation: WriteFileOperation.ReplaceFile,
            filePath: targetFile,
            content: mixedContent,
            validateOnApply: false);

        Assert.That(result.IsSuccess, Is.True);

        var bytes = File.ReadAllBytes(targetFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.EqualTo(0), "LF file should have no CRLF");
        Assert.That(lf, Is.GreaterThan(0), "LF file should stay LF-only");
    }

    [Test]
    [Description("ReplaceSnippet: Single-line file with no newline gains no line endings")]
    public async Task ReplaceSnippet_SingleLineNoNewline_Untouched()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        File.WriteAllText(targetFile, "class SingleLine { }", new UTF8Encoding(false));
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.ReplaceSnippet(
            reason: "test single line",
            action: ProposedChangeAction.apply,
            filePath: targetFile,
            oldContent: "SingleLine",
            newContent: "ModifiedLine",
            validateOnApply: false);

        Assert.That(result.IsSuccess, Is.True, $"ReplaceSnippet should succeed: {result.ErrorData?.Message}");
        Assert.That(File.ReadAllText(targetFile), Is.EqualTo("class ModifiedLine { }"));

        var (crlf, cr, lf) = CountLineEndings(File.ReadAllBytes(targetFile));
        Assert.That(crlf + cr + lf, Is.EqualTo(0), "Single-line file should have no line endings");
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

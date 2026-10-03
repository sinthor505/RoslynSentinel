// Tests that EOL normalization preserves the file's dominant line ending.
// DISK tier - asserts on real file bytes, not text content.
// Directly test the WorkspaceManager.ApplyProposedChangesAsync path that all mutating tools use.

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Tests;
using System.IO;
using System.Text;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("Disk")]
public class EolInheritanceTests
{
    private (int crlf, int cr, int lf) CountLineEndings(byte[] bytes)
    {
        int crlfCount = 0;
        int crCount = 0;
        int lfCount = 0;

        for (int i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0x0D && bytes[i + 1] == 0x0A) // \r\n
            {
                crlfCount++;
                i++; // Skip the LF in CRLF pair
            }
            else if (bytes[i] == 0x0D) // bare \r
            {
                crCount++;
            }
            else if (bytes[i] == 0x0A) // bare \n
            {
                lfCount++;
            }
        }

        return (crlfCount, crCount, lfCount);
    }

    [Test]
    [Description("ApplyProposedChangesAsync: CRLF file with multi-line LF newContent should stay all-CRLF")]
    public async Task ApplyProposedChangesAsync_CrlfFileLfContent_StaysAllCrlf()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        // Create a CRLF test file
        var testFile = Path.Combine(fixture.SolutionDirectory, "TestCrlf.cs");
        var originalCrlf = "class Test\r\n{\r\n    void Foo() { }\r\n}\r\n";
        File.WriteAllText(testFile, originalCrlf, Encoding.UTF8);

        // Apply a multi-line edit with LF content - should be normalized to CRLF
        var lfContent = "public void Bar()\n    {\n        return;\n    }";
        var newFile = originalCrlf.Replace("void Foo() { }", lfContent);

        var filePathWrapper = workspaceManager.ResolveFromWire("TestCrlf.cs");
        await workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string> { [filePathWrapper] = newFile },
            validateChanges: false);

        var bytes = File.ReadAllBytes(testFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.GreaterThan(0), "Should have CRLF line endings");
        Assert.That(lf, Is.EqualTo(0), "Should have no bare LF line endings");
    }

    [Test]
    [Description("ApplyProposedChangesAsync: LF file with multi-line newContent should stay all-LF")]
    public async Task ApplyProposedChangesAsync_LfFileContent_StaysAllLf()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        // Create an LF test file
        var testFile = Path.Combine(fixture.SolutionDirectory, "TestLf.cs");
        var originalLf = "class Test\n{\n    void Foo() { }\n}\n";
        File.WriteAllBytes(testFile, Encoding.UTF8.GetBytes(originalLf));

        // Apply a multi-line edit
        var newContent = "public void Bar()\n    {\n        return;\n    }";
        var newFile = originalLf.Replace("void Foo() { }", newContent);

        var filePathWrapper = workspaceManager.ResolveFromWire("TestLf.cs");
        await workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string> { [filePathWrapper] = newFile },
            validateChanges: false);

        var bytes = File.ReadAllBytes(testFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.EqualTo(0), "Should have no CRLF line endings");
        Assert.That(lf, Is.GreaterThan(0), "Should have LF line endings");
    }

    [Test]
    [Description("ApplyProposedChangesAsync: CRLF file with LF newContent should stay all-CRLF")]
    public async Task ApplyProposedChangesAsync_ReplaceFile_CrlfFilePreservesCrlf()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        // Create a CRLF test file
        var testFile = Path.Combine(fixture.SolutionDirectory, "TestReplace.cs");
        var originalCrlf = "class Old\r\n{\r\n}\r\n";
        File.WriteAllText(testFile, originalCrlf, Encoding.UTF8);

        // Replace with LF content - should be normalized to CRLF
        var lfContent = "class New\n{\n}\n";

        var filePathWrapper = workspaceManager.ResolveFromWire("TestReplace.cs");
        await workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string> { [filePathWrapper] = lfContent },
            validateChanges: false);

        var bytes = File.ReadAllBytes(testFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.GreaterThan(0), "Should inherit file's original CRLF");
        Assert.That(lf, Is.EqualTo(0), "Should have no bare LF");
    }

    [Test]
    [Description("ApplyProposedChangesAsync: LF file content should stay all-LF")]
    public async Task ApplyProposedChangesAsync_ReplaceFile_LfFileStaysLf()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        // Create an LF test file
        var testFile = Path.Combine(fixture.SolutionDirectory, "TestReplaceLf.cs");
        var originalLf = "class Old\n{\n}\n";
        File.WriteAllBytes(testFile, Encoding.UTF8.GetBytes(originalLf));

        var lfContent = "class New\n{\n}\n";

        var filePathWrapper = workspaceManager.ResolveFromWire("TestReplaceLf.cs");
        await workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string> { [filePathWrapper] = lfContent },
            validateChanges: false);

        var bytes = File.ReadAllBytes(testFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.EqualTo(0), "Should have no CRLF");
        Assert.That(lf, Is.GreaterThan(0), "Should stay all-LF");
    }

    [Test]
    [Description("Single-line file without trailing newline should remain unchanged")]
    public async Task ApplyProposedChangesAsync_SingleLineFile_NoTrailingNewline_IsNotModified()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        // Create a single-line file
        var testFile = Path.Combine(fixture.SolutionDirectory, "SingleLine.cs");
        var singleLine = "class Test { }";
        File.WriteAllText(testFile, singleLine, Encoding.UTF8);

        var newContent = "class Test { }";

        var filePathWrapper = workspaceManager.ResolveFromWire("SingleLine.cs");
        await workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string> { [filePathWrapper] = newContent },
            validateChanges: false);

        var bytes = File.ReadAllBytes(testFile);
        var (crlf, cr, lf) = CountLineEndings(bytes);

        Assert.That(crlf, Is.EqualTo(0), "No line breaks in single-line file");
        Assert.That(lf, Is.EqualTo(0), "No line breaks in single-line file");
    }
}

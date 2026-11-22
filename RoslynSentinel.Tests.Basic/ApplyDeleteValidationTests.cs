using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Delete targets at the ApplyProposedChangesAsync compile gate (UndoChange slice 12-3): with validateChanges true a file being
// deleted is dropped from the candidate solution first, so a delete that breaks other code is refused before any write.
[TestFixture]
public class ApplyDeleteValidationTests
{
    private const string ASource = "class A { }\r\n";
    private const string BUsesA = "class B { A Field = new A(); }\r\n";
    private const string BUnrelated = "class B { }\r\n";

    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void DeleteTempDirs()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        _tempDirs.Clear();
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ApplyDeleteValidation", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Real documents whose FilePath is the temp file, so the gate compiles the same text that is on disk.
    private static PersistentWorkspaceManager NewManager(params (string File, string Text)[] documents)
    {
        var project = new AdhocWorkspace().AddProject("ApplyDeleteValidation", LanguageNames.CSharp);
        foreach (var (file, text) in documents)
        {
            project = project.AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project;
        }

        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(project.Solution);
        return manager;
    }

    [Test]
    public async Task DeleteOfFileWhoseTypeIsStillUsed_WithValidateChanges_IsRefusedAndNothingDeleted()
    {
        var dir = NewTempDir();
        var a = Path.Combine(dir, "A.cs");
        var b = Path.Combine(dir, "B.cs");
        File.WriteAllText(a, ASource);
        File.WriteAllText(b, BUsesA);
        using var manager = NewManager((a, ASource), (b, BUsesA));

        var result = await manager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string>(), retryCount: 0, validateChanges: true,
            deletePaths: new List<FilePathWrapper> { a });

        Assert.That(result.IsError, Is.True, result.Summary);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(File.Exists(a), Is.True);
        Assert.That(File.ReadAllText(a), Is.EqualTo(ASource));
    }

    [Test]
    public async Task DeleteOfUnreferencedFile_WithValidateChanges_Succeeds()
    {
        var dir = NewTempDir();
        var a = Path.Combine(dir, "A.cs");
        var b = Path.Combine(dir, "B.cs");
        File.WriteAllText(a, ASource);
        File.WriteAllText(b, BUnrelated);
        using var manager = NewManager((a, ASource), (b, BUnrelated));

        var result = await manager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string>(), retryCount: 0, validateChanges: true,
            deletePaths: new List<FilePathWrapper> { a });

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(File.Exists(a), Is.False);
    }
}

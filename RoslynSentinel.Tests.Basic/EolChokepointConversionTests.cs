using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Write-chokepoint EOL conversion with the resolver pre-pass ON. Assertions read raw bytes on disk and count
// CR and bare-LF bytes; they never compare decoded text, because decoding hides line endings.
[TestFixture]
public class EolChokepointConversionTests
{
    private const string LfSource = "namespace T;\npublic class A\n{\n    public int X() => 1;\n}\n";
    private const string CrlfSource = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSource = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSourceWithAddedLine = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n    public int Y() => 2;\r\n}\r\n";

    // Explicit EOL mode for this manager.
    // Every test goes through this helper so the EOL mode is set in one place.
    private static async Task<ApplyChangesResult> ApplyAsync(
        string file, string proposed, EolMode mode, bool exactRestore = false, string? operationLabel = null)
    {
        using var manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = mode;
        manager.SetTestSolution(new AdhocWorkspace().CurrentSolution);
        return await manager.ApplyProposedChangesAsync(
            new() { [file] = proposed }, retryCount: 0, validateChanges: false,
            exactRestore: exactRestore, operationLabel: operationLabel);
    }

    // root = true stops a parent .editorconfig above the temp directory from leaking into the resolver.
    private static string CreateTree(string editorConfigBody)
    {
        var dir = Path.Combine(Path.GetTempPath(), "EolChokepointConversion", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, ".editorconfig"), Encoding.UTF8.GetBytes("root = true\n" + editorConfigBody));
        return dir;
    }

    private static string WriteExistingFile(string dir, string text)
    {
        var file = Path.Combine(dir, "A.cs");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(text));
        return file;
    }

    private static int CrCount(byte[] bytes) => bytes.Count(b => b == (byte)'\r');

    private static int BareLfCount(byte[] bytes)
    {
        var count = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r'))
            {
                count++;
            }
        }
        return count;
    }

    private static void AssertUniformCrlf(byte[] bytes)
    {
        Assert.That(BareLfCount(bytes), Is.Zero, "no bare LF byte may survive");
        Assert.That(CrCount(bytes), Is.GreaterThan(0), "the file must be CRLF");
    }

    private static void AssertUniformLf(byte[] bytes) =>
        Assert.That(CrCount(bytes), Is.Zero, "no CR byte may survive");

    [Test]
    public async Task NewLfFile_UnderCrlf_IsWrittenAsUniformCrlfAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = Path.Combine(dir, "New.cs");
            var result = await ApplyAsync(file, LfSource, EolMode.Crlf);

            Assert.That(result.IsError, Is.False, result.Summary);
            AssertUniformCrlf(await File.ReadAllBytesAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NewLfFile_UnderLf_IsWrittenWithoutCrAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = Path.Combine(dir, "New.cs");
            var result = await ApplyAsync(file, LfSource, EolMode.Lf);

            Assert.That(result.IsError, Is.False, result.Summary);
            AssertUniformLf(await File.ReadAllBytesAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task MixedPreImage_UnderCrlf_ConvergesToUniformCrlfAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            var result = await ApplyAsync(file, MixedSourceWithAddedLine, EolMode.Crlf);

            Assert.That(result.IsError, Is.False, result.Summary);
            AssertUniformCrlf(await File.ReadAllBytesAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task MixedPreImage_UnderAutoWithoutEditorConfigEol_UsesPlatformNewLineAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            var result = await ApplyAsync(file, MixedSourceWithAddedLine, EolMode.Auto);

            Assert.That(result.IsError, Is.False, result.Summary);
            var bytes = await File.ReadAllBytesAsync(file);
            if (Environment.NewLine == "\r\n")
            {
                AssertUniformCrlf(bytes);
            }
            else
            {
                AssertUniformLf(bytes);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task MixedPreImage_UnderAutoWithEditorConfigLf_ConvergesToUniformLfAsync()
    {
        var dir = CreateTree("\n[*.cs]\nend_of_line = lf\n");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            var result = await ApplyAsync(file, MixedSourceWithAddedLine, EolMode.Auto);

            Assert.That(result.IsError, Is.False, result.Summary);
            AssertUniformLf(await File.ReadAllBytesAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task EolOnlyChange_SurvivesNoOpSkipAndIsWrittenAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            // Proposed text equals the pre-image text, so only the EOL conversion changes bytes.
            var result = await ApplyAsync(file, MixedSource, EolMode.Crlf);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.SucceededFiles, Does.Contain(file));
            Assert.That(result.NoOpFiles?.Contains(file) ?? false, Is.False, "an EOL-only change must not be skipped as a no-op");
            AssertUniformCrlf(await File.ReadAllBytesAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task MixedPreImage_WithOperationLabel_RecordsOneAttributionFindingAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            var result = await ApplyAsync(file, MixedSourceWithAddedLine, EolMode.Crlf, operationLabel: "ReplaceSnippet");

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.Findings?.Count ?? 0, Is.EqualTo(1));
            var finding = result.Findings![0];
            Assert.That(finding.Source, Is.EqualTo("WriteChokepoint"));
            Assert.That(finding.Message, Does.Contain(Path.GetFileName(file)).And.Contain("ReplaceSnippet"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task MixedPreImage_WithoutOperationLabel_SaysUnknownCallerAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            var result = await ApplyAsync(file, MixedSourceWithAddedLine, EolMode.Crlf);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.Findings?.Count ?? 0, Is.EqualTo(1));
            Assert.That(result.Findings![0].Message, Does.Contain("unknown caller"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task CrlfPreImage_UnderCrlf_RecordsNoFindingAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, CrlfSource);
            var result = await ApplyAsync(file, CrlfSource.Replace("=> 1", "=> 2"), EolMode.Crlf, operationLabel: "ReplaceSnippet");

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.Findings?.Count ?? 0, Is.Zero);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ExactRestore_MixedPreImage_RecordsNoFindingAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            var result = await ApplyAsync(file, MixedSourceWithAddedLine, EolMode.Crlf, exactRestore: true, operationLabel: "UndoLastApply");

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.Findings?.Count ?? 0, Is.Zero);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LfPreImage_UnderCrlf_RecordsAttributionFindingAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, LfSource);
            var result = await ApplyAsync(file, LfSource.Replace("=> 1", "=> 2"), EolMode.Crlf, operationLabel: "ReplaceSnippet");

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.Findings?.Count ?? 0, Is.EqualTo(1));
            Assert.That(result.Findings![0].Message, Does.Contain("ReplaceSnippet"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ExactRestore_MixedText_WritesProposedBytesUnchangedAsync()
    {
        var dir = CreateTree("");
        try
        {
            var file = WriteExistingFile(dir, MixedSource);
            var result = await ApplyAsync(file, MixedSourceWithAddedLine, EolMode.Crlf, exactRestore: true);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(Encoding.UTF8.GetBytes(MixedSourceWithAddedLine)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

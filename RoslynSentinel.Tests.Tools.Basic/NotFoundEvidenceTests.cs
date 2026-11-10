using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using NUnit.Framework;

using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class NotFoundEvidenceTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "RoslynSentinelNotFound_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        ToolCallContext.Set(null);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        DeleteTree(_root);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Git objects are read-only on Windows, so clear the attribute before deleting.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private async Task WriteBlobAsync(string? agentId, string? sessionId, string? before, string reason = "rename for test")
    {
        ToolCallContext.Set(new ToolCallContext(agentId, "implementer", "RenameSymbol", reason, 0) { SessionId = sessionId });
        try
        {
            // Going through the real writer proves the JSON property names match what the search reads.
            await OperationBlobWriter.WriteAsync(
                "RenameSymbol",
                Guid.NewGuid().ToString("N")[..8],
                [new OperationItemRecord { FilePath = Path.Combine(_root, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = before }],
                _root);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    private async Task<string> WriteStampedBlobAsync(string toolName, string agentId, DateTime stamp, string before)
    {
        var context = new ToolCallContext(agentId, "implementer", "RenameSymbol", "stamped write", 0) { SessionId = "s1" };
        context.MarkWritten(stamp);
        ToolCallContext.Set(context);
        try
        {
            var blob = await OperationBlobWriter.WriteAsync(
                toolName,
                Guid.NewGuid().ToString("N")[..8],
                [new OperationItemRecord { FilePath = Path.Combine(_root, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = before }],
                _root);
            return Path.Combine(_root, ".roslynsentinel", "operations", blob.FileName!);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    private async Task WriteBlobForFileAsync(string fileName, string agentId, string sessionId, DateTime stamp, string before)
    {
        var context = new ToolCallContext(agentId, "implementer", "RenameSymbol", "stamped write", 0) { SessionId = sessionId };
        context.MarkWritten(stamp);
        ToolCallContext.Set(context);
        try
        {
            await OperationBlobWriter.WriteAsync(
                "RenameSymbol",
                Guid.NewGuid().ToString("N")[..8],
                [new OperationItemRecord { FilePath = Path.Combine(_root, fileName), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = before }],
                _root);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    private static DateTime StartOfSecondOneMinuteAgo()
    {
        var value = DateTime.UtcNow.AddMinutes(-1);
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
    }

    private IReadOnlyList<Finding> Find(string term, string agentId = "main", string? sessionId = "s1", DateTime? now = null) =>
        NotFoundEvidence.Find(_root, "Foo.cs", term, agentId, sessionId, now ?? DateTime.UtcNow);

    private void InitRepoWithHead(string headContent)
    {
        RunGit(_root, "init");
        File.WriteAllText(Path.Combine(_root, "Foo.cs"), headContent);
        RunGit(_root, "add", "Foo.cs");
        RunGit(_root, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "initial");
    }

    [Test]
    public void NoBlobAndNoRepo_NoNote()
    {
        Assert.That(Find("Bar"), Is.Empty);
    }

    [Test]
    public async Task ForeignAgentSameSession_NamesWriter()
    {
        await WriteBlobAsync("b7", "s1", "class Foo { void Bar() { } }", "drop Bar");

        var findings = Find("Bar");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Source, Is.EqualTo("EditAttribution"));
        Assert.That(findings[0].Message, Does.Contain("`Bar`"));
        Assert.That(findings[0].Message, Does.Contain("`Foo.cs`"));
        Assert.That(findings[0].Message, Does.Contain("`b7`"));
        Assert.That(findings[0].Message, Does.Contain("RenameSymbol"));
        Assert.That(findings[0].Message, Does.Contain("drop Bar"));
        Assert.That(findings[0].Message, Does.Not.Contain("another session"));
    }

    [Test]
    public async Task SameAgentNameOtherSession_SaysAnotherSession()
    {
        await WriteBlobAsync("main", "s2", "class Foo { void Bar() { } }");

        var findings = Find("Bar", agentId: "main", sessionId: "s1");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`main`"));
        Assert.That(findings[0].Message, Does.Contain("in another session"));
    }

    [Test]
    public async Task OwnWrite_NoNote_AndNoGitNote()
    {
        InitRepoWithHead("class Foo { void Bar() { } }");
        File.WriteAllText(Path.Combine(_root, "Foo.cs"), "class Foo { }");
        await WriteBlobAsync("main", "s1", "class Foo { void Bar() { } }");

        Assert.That(Find("Bar", agentId: "main", sessionId: "s1"), Is.Empty);
    }

    [Test]
    public async Task ItemWithoutSession_MainWriter_IsNotForeign()
    {
        await WriteBlobAsync("main", null, "class Foo { void Bar() { } }");

        Assert.That(Find("Bar", agentId: "a1"), Is.Empty);
    }

    [Test]
    public async Task ItemWithoutSession_OtherAgent_IsForeign()
    {
        await WriteBlobAsync("b7", null, "class Foo { void Bar() { } }");

        var findings = Find("Bar");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`b7`"));
    }

    [Test]
    public async Task CallerWithoutSession_OtherAgent_IsForeign()
    {
        await WriteBlobAsync("b7", "s2", "class Foo { void Bar() { } }");

        var findings = Find("Bar", sessionId: null);

        Assert.That(findings, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ForeignWriteWithoutTerm_NoNote()
    {
        await WriteBlobAsync("b7", "s1", "class Foo { }");

        Assert.That(Find("Bar"), Is.Empty);
    }

    [Test]
    public async Task BlobOutsideWindow_NoNote()
    {
        await WriteBlobAsync("b7", "s1", "class Foo { void Bar() { } }");

        Assert.That(Find("Bar", now: DateTime.UtcNow.AddHours(2)), Is.Empty);
    }

    [Test]
    public async Task LaterWriteWithoutTerm_StillNamesTheRemover()
    {
        await WriteBlobAsync("b7", "s1", "class Foo { void Bar() { } }");
        await WriteBlobAsync("main", "s1", "class Foo { }");

        var findings = Find("Bar");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`b7`"));
    }

    [Test]
    public async Task SameSecond_NewerFractionIsTheRemover()
    {
        var second = StartOfSecondOneMinuteAgo();
        await WriteStampedBlobAsync("z_tool", "olderAgent", second.AddTicks(1), "class Foo { void Bar() { } }");
        await WriteStampedBlobAsync("a_tool", "newerAgent", second.AddTicks(2), "class Foo { void Bar() { } }");

        var findings = Find("Bar");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`newerAgent`"));
        Assert.That(findings[0].Message, Does.Not.Contain("olderAgent"));
    }

    [Test]
    public async Task NameWithoutFraction_IsStillFound_AndCountsAsStartOfSecond()
    {
        var second = StartOfSecondOneMinuteAgo();
        var path = await WriteStampedBlobAsync("z_tool", "olderAgent", second, "class Foo { void Bar() { } }");
        File.Move(path, path.Replace("_0000000_", "_"));
        await WriteStampedBlobAsync("a_tool", "newerAgent", second.AddTicks(5), "class Foo { void Bar() { } }");

        var findings = Find("Bar");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`newerAgent`"));
    }

    [Test]
    public async Task NameWithoutFraction_AloneIsStillFound()
    {
        var path = await WriteStampedBlobAsync("z_tool", "olderAgent", StartOfSecondOneMinuteAgo(), "class Foo { void Bar() { } }");
        File.Move(path, path.Replace("_0000000_", "_"));

        var findings = Find("Bar");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`olderAgent`"));
    }

    [Test]
    public async Task NameMatchUsesWordBoundaries()
    {
        await WriteBlobAsync("b7", "s1", "class Foo { void BarBaz() { } }");

        Assert.That(Find("Bar"), Is.Empty);
    }

    [Test]
    public async Task SnippetMatchIgnoresLineEndingStyle()
    {
        await WriteBlobAsync("b7", "s1", "int a;\r\nint b;\r\n");

        var findings = Find("int a;\nint b;");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`int a;`"));
    }

    [Test]
    public async Task ShortName_NoNote()
    {
        await WriteBlobAsync("b7", "s1", "int x;");

        Assert.That(Find("x"), Is.Empty);
    }

    [Test]
    public void RemovedOutsideServer_GitNote()
    {
        InitRepoWithHead("class Foo { void Bar() { } }");
        File.WriteAllText(Path.Combine(_root, "Foo.cs"), "class Foo { }");

        var findings = Find("Bar");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("at HEAD but not on disk"));
        Assert.That(findings[0].Message, Does.Contain("writer unknown"));
    }

    [Test]
    public void RemovedOutsideServer_GitNote_IsServedByLibgit2NotTheCliFallback()
    {
        InitRepoWithHead("class Foo { void Bar() { } }");
        File.WriteAllText(Path.Combine(_root, "Foo.cs"), "class Foo { }");
        var libgit2Before = RoslynSentinel.Common.GitReaders.Default.Libgit2Count;
        var fallbacksBefore = RoslynSentinel.Common.GitReaders.Default.CliFallbackCount;

        var findings = Find("Bar");

        // Counters are process-wide and other tests may add to them in parallel, so assert on direction, not exact deltas;
        // a silent fallback leaves FirstFallbackReason set and raises CliFallbackCount.
        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(RoslynSentinel.Common.GitReaders.Default.FirstFallbackReason, Is.Null, "libgit2 fell back to the CLI: native library missing or broken?");
        Assert.That(RoslynSentinel.Common.GitReaders.Default.CliFallbackCount, Is.EqualTo(fallbacksBefore));
        Assert.That(RoslynSentinel.Common.GitReaders.Default.Libgit2Count, Is.GreaterThan(libgit2Before));
    }

    [Test]
    public void TypoInRepo_NoGitNote()
    {
        InitRepoWithHead("class Foo { }");

        Assert.That(Find("Bar"), Is.Empty);
    }

    [Test]
    public void StillOnDisk_NoGitNote()
    {
        InitRepoWithHead("class Foo { void Bar() { } }");

        Assert.That(Find("Bar"), Is.Empty);
    }

    [Test]
    public async Task ForCurrentCall_NotFoundCode_ReturnsNote()
    {
        await WriteBlobAsync("b7", "s1", "class Foo { void Bar() { } }");
        ToolCallContext.Set(new ToolCallContext("main", null, "GetMethodSource", null, 0)
        {
            SessionId = "s1",
            TargetFilePath = "Foo.cs",
            MissingTerm = "Bar",
            SolutionRoot = _root,
        });

        var findings = NotFoundEvidence.ForCurrentCall("MethodNotFound");

        Assert.That(findings, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ForCurrentCall_OtherCode_NoAgent_NoContext_AreEmpty()
    {
        await WriteBlobAsync("b7", "s1", "class Foo { void Bar() { } }");
        var context = new ToolCallContext("main", null, "GetMethodSource", null, 0)
        {
            SessionId = "s1",
            TargetFilePath = "Foo.cs",
            MissingTerm = "Bar",
            SolutionRoot = _root,
        };

        ToolCallContext.Set(context);
        Assert.That(NotFoundEvidence.ForCurrentCall("InvalidArgument"), Is.Empty);

        ToolCallContext.Set(new ToolCallContext(null, null, "GetMethodSource", null, 0)
        {
            SessionId = "s1",
            TargetFilePath = "Foo.cs",
            MissingTerm = "Bar",
            SolutionRoot = _root,
        });
        Assert.That(NotFoundEvidence.ForCurrentCall("NotFound"), Is.Empty);

        ToolCallContext.Set(null);
        Assert.That(NotFoundEvidence.ForCurrentCall("NotFound"), Is.Empty);
    }

    [Test]
    public async Task AcrossFiles_ForeignRemoval_InsideWindow_RemovedNote()
    {
        await WriteBlobForFileAsync("Bar.cs", "b7", "s1", DateTime.UtcNow.AddMinutes(-5), "class Bar { void Widget() { } }");

        var findings = NotFoundEvidence.FindAcrossFiles(_root, "Widget", false, "main", "s1", DateTime.UtcNow);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("Widget"));
        Assert.That(findings[0].Message, Does.Contain("Bar.cs"));
        Assert.That(findings[0].Message, Does.Contain("b7"));
        Assert.That(findings[0].Message, Does.Not.Contain("not loaded yet"));
    }

    [Test]
    public async Task AcrossFiles_OwnWrite_NoNote()
    {
        await WriteBlobForFileAsync("Bar.cs", "main", "s1", DateTime.UtcNow.AddMinutes(-5), "class Bar { void Widget() { } }");

        Assert.That(NotFoundEvidence.FindAcrossFiles(_root, "Widget", false, "main", "s1", DateTime.UtcNow), Is.Empty);
    }

    [Test]
    public async Task AcrossFiles_ForeignAdd_Under15s_ReadsDiskNote()
    {
        await WriteBlobForFileAsync("Baz.cs", "b7", "s1", DateTime.UtcNow.AddSeconds(-3), "class Baz { }");
        File.WriteAllText(Path.Combine(_root, "Baz.cs"), "class Baz { void Widget() { } }");

        var findings = NotFoundEvidence.FindAcrossFiles(_root, "Widget", false, "main", "s1", DateTime.UtcNow);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("has not loaded it yet"));
        Assert.That(findings[0].Message, Does.Contain("Baz.cs"));
    }

    [Test]
    public async Task AcrossFiles_ForeignAdd_Older15s_NoAddedNote()
    {
        await WriteBlobForFileAsync("Baz.cs", "b7", "s1", DateTime.UtcNow.AddSeconds(-40), "class Baz { }");
        File.WriteAllText(Path.Combine(_root, "Baz.cs"), "class Baz { void Widget() { } }");

        Assert.That(NotFoundEvidence.FindAcrossFiles(_root, "Widget", false, "main", "s1", DateTime.UtcNow), Is.Empty);
    }

    [Test]
    public async Task AcrossFiles_RegexTerm_Matches()
    {
        await WriteBlobForFileAsync("Bar.cs", "b7", "s1", DateTime.UtcNow.AddMinutes(-5), "class Bar { void Widget() { } }");

        Assert.That(NotFoundEvidence.FindAcrossFiles(_root, "Wid.et", true, "main", "s1", DateTime.UtcNow), Has.Count.EqualTo(1));
        Assert.That(NotFoundEvidence.FindAcrossFiles(_root, "(", true, "main", "s1", DateTime.UtcNow), Is.Empty);
        Assert.That(NotFoundEvidence.FindAcrossFiles(_root, "[Wid", true, "main", "s1", DateTime.UtcNow), Is.Empty);
    }

    [Test]
    public async Task AcrossFiles_ShortTerm_AndCorruptBlob_Ignored()
    {
        Assert.That(NotFoundEvidence.FindAcrossFiles(_root, "ab", false, "main", "s1", DateTime.UtcNow), Is.Empty);

        // Named inside the window so the search really opens it; the JSON parse failure must be skipped, not thrown.
        var operations = Path.Combine(_root, ".roslynsentinel", "operations");
        Directory.CreateDirectory(operations);
        File.WriteAllText(Path.Combine(operations, $"RenameSymbol_{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}_0000000_x.json"), "{{{");
        await WriteBlobForFileAsync("Bar.cs", "b7", "s1", DateTime.UtcNow.AddMinutes(-5), "class Bar { void Widget() { } }");

        var findings = NotFoundEvidence.FindAcrossFiles(_root, "Widget", false, "main", "s1", DateTime.UtcNow);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("Bar.cs"));
    }
}

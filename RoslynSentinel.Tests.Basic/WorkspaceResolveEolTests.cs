using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class WorkspaceResolveEolTests
{
    // persistent = true builds the real PersistentWorkspaceManager (disposed here); false builds the fake.
    // Both get the same explicit override so the static EolPolicyOptions.Mode is never touched.
    private static string ResolveWith(bool persistent, EolMode mode, string filePath, string? preImageText)
    {
        if (persistent)
        {
            using var manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            manager.EolModeOverride = mode;
            return manager.ResolveEol(filePath, preImageText);
        }

        using var fake = new FakeWorkspaceManager(WorkspaceAccess.ReadWrite);
        fake.EolModeOverride = mode;
        return fake.ResolveEol(filePath, preImageText);
    }

    // root = true stops a parent .editorconfig above the temp directory from leaking into the resolver.
    // Callers delete the returned directory in a finally block.
    private static string CreateTempTree(string? editorConfigBody)
    {
        var dir = Path.Combine(Path.GetTempPath(), "WorkspaceResolveEol", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        if (editorConfigBody is not null)
        {
            File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n" + editorConfigBody);
        }

        return dir;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExplicitCrlfOverride_ReturnsCrlfForAnyPreImage(bool persistent)
    {
        var dir = CreateTempTree(null);
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Assert.That(ResolveWith(persistent, EolMode.Crlf, file, "a\nb\n"), Is.EqualTo("\r\n"));
            Assert.That(ResolveWith(persistent, EolMode.Crlf, file, "a\r\nb\r\n"), Is.EqualTo("\r\n"));
            Assert.That(ResolveWith(persistent, EolMode.Crlf, file, "a\r\nb\nc\r\n"), Is.EqualTo("\r\n"));
            Assert.That(ResolveWith(persistent, EolMode.Crlf, file, null), Is.EqualTo("\r\n"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExplicitLfOverride_ReturnsLfForAnyPreImage(bool persistent)
    {
        var dir = CreateTempTree(null);
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Assert.That(ResolveWith(persistent, EolMode.Lf, file, "a\nb\n"), Is.EqualTo("\n"));
            Assert.That(ResolveWith(persistent, EolMode.Lf, file, "a\r\nb\r\n"), Is.EqualTo("\n"));
            Assert.That(ResolveWith(persistent, EolMode.Lf, file, "a\r\nb\nc\r\n"), Is.EqualTo("\n"));
            Assert.That(ResolveWith(persistent, EolMode.Lf, file, null), Is.EqualTo("\n"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AutoMode_SingleStylePreImage_FollowsPreImage(bool persistent)
    {
        var dir = CreateTempTree(null);
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Assert.That(ResolveWith(persistent, EolMode.Auto, file, "a\r\nb\r\n"), Is.EqualTo("\r\n"));
            Assert.That(ResolveWith(persistent, EolMode.Auto, file, "a\nb\n"), Is.EqualTo("\n"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestCase(true, "lf", "\n")]
    [TestCase(false, "lf", "\n")]
    [TestCase(true, "crlf", "\r\n")]
    [TestCase(false, "crlf", "\r\n")]
    public void AutoMode_NullPreImage_UsesEditorConfigEndOfLine(bool persistent, string endOfLine, string expected)
    {
        var dir = CreateTempTree("[*.cs]\nend_of_line = " + endOfLine + "\n");
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Assert.That(ResolveWith(persistent, EolMode.Auto, file, null), Is.EqualTo(expected));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AutoMode_MixedPreImage_WithEditorConfigLf_ReturnsLf(bool persistent)
    {
        var dir = CreateTempTree("[*.cs]\nend_of_line = lf\n");
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Assert.That(ResolveWith(persistent, EolMode.Auto, file, "a\r\nb\nc\r\n"), Is.EqualTo("\n"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

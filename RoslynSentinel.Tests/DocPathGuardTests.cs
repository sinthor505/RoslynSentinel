using System.IO;

using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class DocPathGuardTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TempDocsRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DocPathGuardTests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Test 1: bare valid filename resolves inside docs subdir ───────────────

    [Test]
    public void ValidBareName_ResolvesInsideRoot()
    {
        var root = TempDocsRoot();
        var (ok, fullPath, error) = DocPathGuard.ResolveSafe(root, "notes.md");

        Assert.That(ok, Is.True, error);
        Assert.That(fullPath, Does.StartWith(root));
        Assert.That(Path.GetFileName(fullPath), Is.EqualTo("notes.md"));
    }

    // ── Test 2: relative traversal rejected ──────────────────────────────────

    [Test]
    public void RelativeTraversal_Rejected()
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, @"../../Main/Avaal.Forms/SomeForm.cs");

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("path components"));
    }

    // ── Test 3: absolute path rejected ───────────────────────────────────────

    [Test]
    public void AbsolutePath_Rejected()
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, @"C:\Windows\System32\drivers\etc\hosts");

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("path components").Or.Contain("Invalid character"));
    }

    // ── Test 4: source extension rejected ────────────────────────────────────

    [Test]
    public void SourceExtension_Rejected()
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, "SomeForm.cs");

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("not permitted"));
        // Confirm the rejection reason is the extension, not a substring match
        Assert.That(error, Does.Contain(".cs"));
    }

    // ── Test 5: project extension rejected ───────────────────────────────────

    [Test]
    public void ProjectExtension_Rejected()
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, "Avaal.csproj");

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("not permitted"));
    }

    // ── Test 6: .csv rejected for the right reason (not because it contains ".cs") ──

    [Test]
    public void CsvExtension_RejectedAsNotPermitted_NotDueToSubstringMatch()
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, "release.csv");

        Assert.That(ok, Is.False);
        // Must be rejected because the extension is not in the allowlist,
        // NOT because the name contains ".cs".
        Assert.That(error, Does.Contain("not permitted"));
        Assert.That(error, Does.Contain(".csv"));
    }

    // ── Test 7: all allowed extensions are accepted ───────────────────────────

    [TestCase("plan.md")]
    [TestCase("state.yaml")]
    [TestCase("state.yml")]
    [TestCase("config.json")]
    [TestCase("notes.txt")]
    public void AllowedExtensions_Accepted(string filename)
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, filename);

        Assert.That(ok, Is.True, $"Expected '{filename}' to be accepted but got: {error}");
    }

    // ── Test 8: alternate data stream rejected ────────────────────────────────

    [Test]
    public void AlternateDataStream_Rejected()
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, "notes.md:hidden.cs");

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain(":"));
    }

    // ── Test 9: reserved device names rejected ────────────────────────────────

    [TestCase("CON.md")]
    [TestCase("NUL.txt")]
    [TestCase("COM1.yaml")]
    [TestCase("LPT9.json")]
    public void ReservedDeviceName_Rejected(string filename)
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, filename);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("reserved"));
    }

    // ── Test 10: empty / whitespace filename rejected ─────────────────────────

    [TestCase("")]
    [TestCase("   ")]
    public void EmptyOrWhitespaceFilename_Rejected(string filename)
    {
        var root = TempDocsRoot();
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, filename);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("Empty").Or.Contain("path components"));
    }

    // ── Test 11: containment backstop — forward-slash traversal ──────────────

    [Test]
    public void ForwardSlashTraversal_Rejected()
    {
        var root = TempDocsRoot();
        // A crafted path using forward slashes that Path.GetFileName should catch
        var (ok, _, error) = DocPathGuard.ResolveSafe(root, "subdir/notes.md");

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("path components"));
    }

    // ── Test 12: case-insensitive extension matching ──────────────────────────

    [TestCase("SomeForm.CS")]
    [TestCase("plan.MD")]
    [TestCase("state.YAML")]
    public void UppercaseExtension_HandledCorrectly(string filename)
    {
        var root = TempDocsRoot();
        var (ok, _, _) = DocPathGuard.ResolveSafe(root, filename);

        // .CS should be rejected; .MD and .YAML should be accepted (case-insensitive)
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        bool expectOk = DocPathGuard.AllowedDocExtensions.Contains(ext);
        Assert.That(ok, Is.EqualTo(expectOk),
            $"Extension '{ext}' — expected ok={expectOk} but got ok={ok}");
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    [TearDown]
    public void Cleanup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DocPathGuardTests");
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}

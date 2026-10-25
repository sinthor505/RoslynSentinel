using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class ServerBinaryStalenessTests
{
    private const string AssemblyName = "RoslynSentinel.Fake";
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rs-staleness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    // Deterministic emit so identical source yields an identical MVID, as in the real build.
    private string Emit(string relativeDir, string source, DateTime writeTimeUtc)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, AssemblyName + ".dll");
        var result = CSharpCompilation.Create(
                AssemblyName,
                [CSharpSyntaxTree.ParseText(source)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true))
            .Emit(path);
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics));
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
        return path;
    }

    private static LoadedBinary Loaded(string path, string? configuration)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return new LoadedBinary(AssemblyName, path, reader.GetGuid(reader.GetModuleDefinition().Mvid), configuration);
    }

    private static readonly DateTime Earlier = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Earlier.AddHours(1);

    [Test]
    public void NewerBuildWithDifferentContent_IsStale()
    {
        var loaded = Emit("loaded", "public class A { }", Earlier);
        var newer = Emit("repo/bin/Debug/net10.0", "public class A { public int X; }", Later);

        var report = ServerBinaryStaleness.Evaluate([Loaded(loaded, "Debug")], [newer], _root);

        Assert.That(report.IsStale, Is.True);
        Assert.That(report.StaleAssemblies.Single().NewerPath, Is.EqualTo(newer));
    }

    [Test]
    public void NewerBuildWithIdenticalContent_IsNotStale()
    {
        var loaded = Emit("loaded", "public class A { }", Earlier);
        var rebuilt = Emit("repo/bin/Debug/net10.0", "public class A { }", Later);

        var report = ServerBinaryStaleness.Evaluate([Loaded(loaded, "Debug")], [rebuilt], _root);

        Assert.That(report.IsStale, Is.False, "A rebuild with unchanged content has the same MVID and must not flag.");
    }

    [Test]
    public void OlderBuildWithDifferentContent_IsNotStale()
    {
        var loaded = Emit("loaded", "public class A { }", Later);
        var older = Emit("repo/bin/Debug/net10.0", "public class A { public int X; }", Earlier);

        Assert.That(ServerBinaryStaleness.Evaluate([Loaded(loaded, "Debug")], [older], _root).IsStale, Is.False);
    }

    [Test]
    public void NewerBuildInADifferentConfiguration_IsNotStale()
    {
        var loaded = Emit("loaded", "public class A { }", Earlier);
        var release = Emit("repo/bin/Release/net10.0", "public class A { public int X; }", Later);

        Assert.That(ServerBinaryStaleness.Evaluate([Loaded(loaded, "Debug")], [release], _root).IsStale, Is.False,
            "A Release build must not flag a Debug-running server.");
    }

    private const string Sha40 = "0123456789abcdef0123456789abcdef01234567";

    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase("1.0.0", null)]
    [TestCase("1.0.0+abc1234", "abc1234")]
    [TestCase("1.0.0+" + Sha40, Sha40)]
    [TestCase("1.0.0+" + Sha40 + ".dirty", Sha40)]
    [TestCase("1.0.0+abc12", null)]
    [TestCase("1.0.0+" + Sha40 + "a", null)]
    [TestCase("1.0.0+build.5", null)]
    public void ParseBuildSha_ReadsTheLeadingHexRunAfterPlus(string? informationalVersion, string? expected)
    {
        Assert.That(ServerBuildInfo.ParseBuildSha(informationalVersion), Is.EqualTo(expected));
    }

    private static string[] Hashes(string log) =>
        [.. ServerBinaryStaleness.ParseGitLog(log, ServerBinaryStaleness.SourcePathPrefixes).Select(c => c.Hash)];

    [Test]
    public void ParseGitLog_DocsOnlyCommit_IsExcluded()
    {
        Assert.That(Hashes("abc1234\tDocs only\ndocs/a.md\nREADME.md\n\n"), Is.Empty);
    }

    [Test]
    public void ParseGitLog_CommitTouchingCommon_IsIncludedWithHashAndSubject()
    {
        var commits = ServerBinaryStaleness.ParseGitLog(
            "def5678\tFix common\nRoslynSentinel.Common/x.cs\n\n", ServerBinaryStaleness.SourcePathPrefixes);

        Assert.That(commits, Is.EqualTo(new[] { new StaleCommit("def5678", "Fix common") }));
    }

    [Test]
    public void ParseGitLog_MultiFileCommits_MatchOnAnyPathAndKeepLogOrder()
    {
        const string log =
            "aaa1111\tMulti\ndocs/a.md\nRoslynSentinel.Tools.Basic/Y.cs\nRoslynSentinel.Engines.Basic/Z.cs\n\n"
            + "bbb2222\tOther\ndocs/b.md\n\n"
            + "ccc3333\tThird\nRoslynSentinel.Server.Basic/Q.cs"; // no trailing newline

        Assert.That(Hashes(log), Is.EqualTo(new[] { "aaa1111", "ccc3333" }));
    }

    [Test]
    public void ParseGitLog_TestAndScriptPaths_AreNotServerSource()
    {
        Assert.That(Hashes("abc1234\tTests\nRoslynSentinel.Tests.Tools.Basic/T.cs\nscripts/x.ps1\n\n"), Is.Empty);
    }

    [Test]
    public void ParseGitLog_SubjectContainingATab_KeepsTheWholeSubject()
    {
        var commits = ServerBinaryStaleness.ParseGitLog(
            "abc1234\tFix\tthing\nRoslynSentinel.Common/x.cs\n", ServerBinaryStaleness.SourcePathPrefixes);

        Assert.That(commits.Single().Subject, Is.EqualTo("Fix\tthing"));
    }

    [Test]
    public void ParseGitLog_BlankLinesCrlfAndPathlessCommits_AreTolerated()
    {
        const string log = "\r\nddd4444\tMerge with no files\r\n\r\n\r\neee5555\tReal\r\n\r\nRoslynSentinel.Common/a.cs\r\n\r\n";

        Assert.That(Hashes(log), Is.EqualTo(new[] { "eee5555" }));
        Assert.That(Hashes(string.Empty), Is.Empty);
    }

    [Test]
    public void CheckNow_WithoutBuildSha_UsesTheLegacyBinScanNotGit()
    {
        var report = ServerBinaryStaleness.CheckNow(null, _root);

        Assert.That(report.StaleCommits, Is.Null, "Only the git path fills StaleCommits; the legacy scan leaves it null.");
    }

    [Test]
    public void EnumerateRepoBinaries_SkipsBinVscodeRefAndWorktreeFolders()
    {
        var kept = Emit("ProjA/bin/Debug/net10.0", "public class A { }", Earlier);
        Emit("bin-vscode/abc/Advanced", "public class A { }", Earlier);
        Emit("ProjA/bin/Debug/net10.0/ref", "public class A { }", Earlier);
        Emit("Worktree/bin/Debug/net10.0", "public class A { }", Earlier);

        Assert.That(ServerBinaryStaleness.EnumerateRepoBinaries(_root), Is.EquivalentTo([Path.GetFullPath(kept)]));
    }

    [Test]
    public void ParseUncommittedStatus_ServerSourceEntries_CountsThemAndNamesTheFirst()
    {
        const string porcelain = " M RoslynSentinel.Common/A.cs\n?? RoslynSentinel.Tools.Basic/B.cs\n M docs/x.md\n";

        var message = ServerBinaryStaleness.ParseUncommittedStatus(porcelain, ServerBinaryStaleness.SourcePathPrefixes);

        Assert.That(message, Is.Not.Null);
        Assert.That(message, Does.Contain("2 uncommitted").And.Contain("RoslynSentinel.Common/A.cs"));
    }

    [Test]
    public void ParseUncommittedStatus_NoServerSourceEntriesOrEmptyOutput_ReturnsNull()
    {
        Assert.That(ServerBinaryStaleness.ParseUncommittedStatus(" M docs/x.md\n?? scripts/y.ps1\n", ServerBinaryStaleness.SourcePathPrefixes), Is.Null);
        Assert.That(ServerBinaryStaleness.ParseUncommittedStatus(string.Empty, ServerBinaryStaleness.SourcePathPrefixes), Is.Null);
    }

    [Test]
    public void ParseUncommittedStatus_RenameIntoServerSource_DescribesTheNewPath()
    {
        const string porcelain = "R  RoslynSentinel.Engines.Basic/Old.cs -> RoslynSentinel.Engines.Basic/New.cs\n";

        var message = ServerBinaryStaleness.ParseUncommittedStatus(porcelain, ServerBinaryStaleness.SourcePathPrefixes);

        Assert.That(message, Is.Not.Null);
        Assert.That(message, Does.Contain("New.cs"));
    }
}

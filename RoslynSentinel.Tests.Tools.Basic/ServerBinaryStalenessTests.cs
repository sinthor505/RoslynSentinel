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

    [Test]
    public void EnumerateRepoBinaries_SkipsBinVscodeRefAndWorktreeFolders()
    {
        var kept = Emit("ProjA/bin/Debug/net10.0", "public class A { }", Earlier);
        Emit("bin-vscode/abc/Advanced", "public class A { }", Earlier);
        Emit("ProjA/bin/Debug/net10.0/ref", "public class A { }", Earlier);
        Emit("Worktree/bin/Debug/net10.0", "public class A { }", Earlier);

        Assert.That(ServerBinaryStaleness.EnumerateRepoBinaries(_root), Is.EquivalentTo([Path.GetFullPath(kept)]));
    }
}

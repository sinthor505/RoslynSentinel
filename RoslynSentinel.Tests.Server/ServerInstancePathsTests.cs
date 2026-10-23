// Coverage for plan_build_isolation_prod_instances_and_test_servers.md Step 3.1: --working-dir resolution.
// Uses the pure ServerInstancePaths.Resolve only; Current is process-static and would race under parallel tests.
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("ServerInstancePaths")]
public class ServerInstancePathsTests
{
    private static string NewTempRoot() => Path.Combine(Path.GetTempPath(), "rs-sip-" + Guid.NewGuid().ToString("N"));

    [Test]
    public void Resolve_NoArg_UsesBinaryDirectoryAndLegacyMarkerInParent()
    {
        var root = NewTempRoot();
        var binDir = Path.Combine(root, "Advanced");

        // Trailing separator mirrors AppContext.BaseDirectory.
        var paths = ServerInstancePaths.Resolve(["--mode=all"], binDir + Path.DirectorySeparatorChar);

        Assert.That(paths.IsExplicit, Is.False);
        Assert.That(paths.WorkingDir, Is.EqualTo(binDir));
        Assert.That(paths.LogsDir, Is.EqualTo(Path.Combine(binDir, "logs")));
        Assert.That(paths.CrashLogPath, Is.EqualTo(Path.Combine(binDir, "logs", "crash.log")));
        Assert.That(paths.StopMarkerPath, Is.EqualTo(Path.Combine(root, "stopped-by-script.marker")));
    }

    [Test]
    public void Resolve_NoArg_NeverUsesProcessCwd()
    {
        var binDir = Path.Combine(NewTempRoot(), "bin");

        var paths = ServerInstancePaths.Resolve([], binDir);

        Assert.That(paths.WorkingDir, Is.Not.EqualTo(Path.GetFullPath(Directory.GetCurrentDirectory())));
        Assert.That(paths.WorkingDir, Is.EqualTo(binDir));
    }

    [Test]
    public void Resolve_ExplicitAbsoluteInlineForm_UsesItAndPutsMarkerInside()
    {
        var work = Path.Combine(NewTempRoot(), "work");
        var binDir = Path.Combine(NewTempRoot(), "bin");

        var paths = ServerInstancePaths.Resolve(["--mode=all", $"--working-dir={work}"], binDir);

        Assert.That(paths.IsExplicit, Is.True);
        Assert.That(paths.WorkingDir, Is.EqualTo(work));
        Assert.That(paths.LogsDir, Is.EqualTo(Path.Combine(work, "logs")));
        Assert.That(paths.CrashLogPath, Is.EqualTo(Path.Combine(work, "logs", "crash.log")));
        Assert.That(paths.StopMarkerPath, Is.EqualTo(Path.Combine(work, "stopped-by-script.marker")));
    }

    [Test]
    public void Resolve_ExplicitTwoTokenForm_UsesIt()
    {
        var work = Path.Combine(NewTempRoot(), "work");

        var paths = ServerInstancePaths.Resolve(["--working-dir", work], Path.Combine(NewTempRoot(), "bin"));

        Assert.That(paths.IsExplicit, Is.True);
        Assert.That(paths.WorkingDir, Is.EqualTo(work));
    }

    [Test]
    public void Resolve_ExplicitRelative_IsResolvedAgainstCwd()
    {
        var relative = Path.Combine("some", "relative", "dir");

        var paths = ServerInstancePaths.Resolve([$"--working-dir={relative}"], Path.Combine(NewTempRoot(), "bin"));

        Assert.That(paths.IsExplicit, Is.True);
        Assert.That(Path.IsPathRooted(paths.WorkingDir), Is.True);
        Assert.That(paths.WorkingDir, Is.EqualTo(Path.GetFullPath(relative)));
        Assert.That(paths.StopMarkerPath, Is.EqualTo(Path.Combine(Path.GetFullPath(relative), "stopped-by-script.marker")));
    }

    [TestCase("--working-dir=")]
    [TestCase("--working-dir=   ")]
    public void Resolve_EmptyValue_FallsBackToDefault(string arg)
    {
        var binDir = Path.Combine(NewTempRoot(), "bin");

        var paths = ServerInstancePaths.Resolve([arg], binDir);

        Assert.That(paths.IsExplicit, Is.False);
        Assert.That(paths.WorkingDir, Is.EqualTo(binDir));
    }

    [Test]
    public void Resolve_IsPure_DoesNotCreateAnyDirectory()
    {
        var work = Path.Combine(NewTempRoot(), "work");
        var binDir = Path.Combine(NewTempRoot(), "bin");

        var explicitPaths = ServerInstancePaths.Resolve([$"--working-dir={work}"], binDir);
        var defaultPaths = ServerInstancePaths.Resolve([], binDir);

        // Reading every derived path must not create anything either.
        _ = (explicitPaths.LogsDir, explicitPaths.CrashLogPath, explicitPaths.StopMarkerPath);
        _ = (defaultPaths.LogsDir, defaultPaths.CrashLogPath, defaultPaths.StopMarkerPath);

        Assert.That(Directory.Exists(work), Is.False);
        Assert.That(Directory.Exists(explicitPaths.LogsDir), Is.False);
        Assert.That(Directory.Exists(binDir), Is.False);
        Assert.That(Directory.Exists(defaultPaths.LogsDir), Is.False);
    }
}

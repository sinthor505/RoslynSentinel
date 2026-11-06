using System;
using System.Collections.Concurrent;
using System.IO;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class WorkspaceUnrestoredProjectHintTests
{
    [Test]
    public void FindUnrestoredProjects_NoAssetsFile_FlagsProjectAndHintNamesBuildAndLoadSolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-unrestored-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var csproj = Path.Combine(root, "Sample.csproj");
            File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

            var flagged = WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj });
            Assert.That(flagged, Is.EqualTo(new[] { "Sample" }));

            var hint = WorkspaceProjectManagementImpl.BuildUnrestoredHint(flagged);
            Assert.That(hint, Does.Contain("Sample"));
            Assert.That(hint, Does.Contain("Build"));
            Assert.That(hint, Does.Contain("LoadSolution"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void FindUnrestoredProjects_AssetsNewerThanCsproj_FlagsNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-restored-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var csproj = Path.Combine(root, "Sample.csproj");
            File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.SetLastWriteTimeUtc(csproj, DateTime.UtcNow.AddMinutes(-10));

            var objDir = Path.Combine(root, "obj");
            Directory.CreateDirectory(objDir);
            var assets = Path.Combine(objDir, "project.assets.json");
            File.WriteAllText(assets, "{}");
            File.SetLastWriteTimeUtc(assets, DateTime.UtcNow);

            var flagged = WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj });
            Assert.That(flagged, Is.Empty);
            Assert.That(WorkspaceProjectManagementImpl.BuildUnrestoredHint(flagged), Is.EqualTo(""));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // --- Semantic restore-input comparison (assets older than the csproj, as after a checkout or a non-package edit) ---

    private const string SampleAssets =
        "{\"version\":3,\"projectFileDependencyGroups\":{\"net8.0\":[\"Newtonsoft.Json >= 13.0.3\",\"Other >= 1.0.0\"]},"
        + "\"project\":{\"frameworks\":{\"net8.0\":{\"targetAlias\":\"net8.0\"}},"
        + "\"restore\":{\"originalTargetFrameworks\":[\"net8.0\"],\"frameworks\":{\"net8.0\":{\"projectReferences\":"
        + "{\"C:\\\\repo\\\\Other\\\\Other.csproj\":{\"projectPath\":\"C:\\\\repo\\\\Other\\\\Other.csproj\"}}}}}}}";

    private static string ProjectXml(string items, string targetFramework = "net8.0")
        => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{targetFramework}</TargetFramework></PropertyGroup>{items}</Project>";

    private const string SampleItems =
        "<ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"
        + "<ProjectReference Include=\"..\\Other\\Other.csproj\" /></ItemGroup>";

    // Writes Sample.csproj (5 minutes old) and obj/project.assets.json (assetsJson, null = none; 1 hour old, so older than the csproj).
    private static string WriteStaleProject(string root, string csprojXml, string? assetsJson)
    {
        Directory.CreateDirectory(root);
        var csproj = Path.Combine(root, "Sample.csproj");
        File.WriteAllText(csproj, csprojXml);
        File.SetLastWriteTimeUtc(csproj, DateTime.UtcNow.AddMinutes(-5));
        if (assetsJson is not null)
        {
            var objDir = Path.Combine(root, "obj");
            Directory.CreateDirectory(objDir);
            var assets = Path.Combine(objDir, "project.assets.json");
            File.WriteAllText(assets, assetsJson);
            File.SetLastWriteTimeUtc(assets, DateTime.UtcNow.AddHours(-1));
        }

        return csproj;
    }

    private static void WithTempRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-semantic-" + Guid.NewGuid().ToString("N"));
        try
        {
            body(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderThanCsprojButSameRestoreInputs_FlagsNothing()
    {
        // Regression: a no-op restore never rewrites project.assets.json, so an mtime comparison could never be satisfied.
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, ProjectXml(SampleItems), SampleAssets);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.Empty);
        });
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderAndCsprojGainedPackageReference_Flags()
    {
        WithTempRoot(root =>
        {
            var items = SampleItems.Replace("</ItemGroup>", "<PackageReference Include=\"Serilog\" Version=\"3.1.0\" /></ItemGroup>");
            var csproj = WriteStaleProject(root, ProjectXml(items), SampleAssets);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.EqualTo(new[] { "Sample" }));
        });
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderAndPackageVersionChanged_Flags()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, ProjectXml(SampleItems.Replace("13.0.3", "13.0.4")), SampleAssets);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.EqualTo(new[] { "Sample" }));
        });
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderAndProjectReferenceAdded_Flags()
    {
        WithTempRoot(root =>
        {
            var items = SampleItems.Replace("</ItemGroup>", "<ProjectReference Include=\"..\\Third\\Third.csproj\" /></ItemGroup>");
            var csproj = WriteStaleProject(root, ProjectXml(items), SampleAssets);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.EqualTo(new[] { "Sample" }));
        });
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderAndTargetFrameworkChanged_Flags()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, ProjectXml(SampleItems, targetFramework: "net9.0"), SampleAssets);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.EqualTo(new[] { "Sample" }));
        });
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderAndOnlyConditionalPackageAdded_FlagsNothing()
    {
        // A Condition may exclude the item from this restore, so its absence from the assets file is not evidence.
        WithTempRoot(root =>
        {
            var items = SampleItems.Replace("</ItemGroup>", "</ItemGroup><ItemGroup Condition=\"'$(Configuration)' == 'Debug'\"><PackageReference Include=\"Serilog\" Version=\"3.1.0\" /></ItemGroup>");
            var csproj = WriteStaleProject(root, ProjectXml(items), SampleAssets);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.Empty);
        });
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderAndUnreadable_Flags()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, ProjectXml(SampleItems), "{ not json");

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.EqualTo(new[] { "Sample" }));
        });
    }

    [Test]
    public void FindUnrestoredProjects_AssetsOlderAndCsprojUnparseable_IsSkipped()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, "<Project><not closed", SampleAssets);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.Empty);
        });
    }

    [Test]
    public void FindUnrestoredProjects_CentralVersionUnchangedButPropsNewer_FlagsNothing_ChangedFlags()
    {
        WithTempRoot(root =>
        {
            var items = "<ItemGroup><PackageReference Include=\"Newtonsoft.Json\" /><ProjectReference Include=\"..\\Other\\Other.csproj\" /></ItemGroup>";
            var csproj = WriteStaleProject(root, ProjectXml(items), SampleAssets);
            var props = Path.Combine(root, "Directory.Packages.props");
            File.WriteAllText(props, "<Project><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>");
            File.SetLastWriteTimeUtc(props, DateTime.UtcNow);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.Empty, "props touched, same central version");

            File.WriteAllText(props, "<Project><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.4\" /></ItemGroup></Project>");
            File.SetLastWriteTimeUtc(props, DateTime.UtcNow);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.EqualTo(new[] { "Sample" }), "central version bumped");
        });
    }

    [Test]
    public void FindUnrestoredProjects_NoAssetsWithPackageReferences_Flags()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, ProjectXml(SampleItems), assetsJson: null);

            Assert.That(WorkspaceProjectManagementImpl.FindUnrestoredProjects(new[] { csproj }), Is.EqualTo(new[] { "Sample" }));
        });
    }

    // --- Not repeating advice that cannot change the outcome ---

    [Test]
    public void FindUnadvisedUnrestoredProjects_SameStateTwice_AdvisesOnlyOnce()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, ProjectXml(SampleItems.Replace("13.0.3", "13.0.4")), SampleAssets);
            var advised = new ConcurrentDictionary<string, string>();

            Assert.That(WorkspaceProjectManagementImpl.FindUnadvisedUnrestoredProjects(new[] { csproj }, advised), Is.EqualTo(new[] { "Sample" }));
            Assert.That(WorkspaceProjectManagementImpl.FindUnadvisedUnrestoredProjects(new[] { csproj }, advised), Is.Empty);
            Assert.That(WorkspaceProjectManagementImpl.BuildUnrestoredHint(WorkspaceProjectManagementImpl.FindUnadvisedUnrestoredProjects(new[] { csproj }, advised)), Is.EqualTo(""));
        });
    }

    [Test]
    public void FindUnadvisedUnrestoredProjects_StateChangedAdvisesAgain_ThenFixedIsForgotten()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteStaleProject(root, ProjectXml(SampleItems.Replace("13.0.3", "13.0.4")), SampleAssets);
            var advised = new ConcurrentDictionary<string, string>();
            Assert.That(WorkspaceProjectManagementImpl.FindUnadvisedUnrestoredProjects(new[] { csproj }, advised), Is.EqualTo(new[] { "Sample" }));

            // Edited again (new mtime, new reason): a different state, so advised again.
            File.WriteAllText(csproj, ProjectXml(SampleItems.Replace("13.0.3", "13.0.5")));
            File.SetLastWriteTimeUtc(csproj, DateTime.UtcNow.AddMinutes(-1));
            Assert.That(WorkspaceProjectManagementImpl.FindUnadvisedUnrestoredProjects(new[] { csproj }, advised), Is.EqualTo(new[] { "Sample" }));

            // Inputs back in line with the assets file: no longer flagged, and the memory entry is forgotten.
            File.WriteAllText(csproj, ProjectXml(SampleItems));
            Assert.That(WorkspaceProjectManagementImpl.FindUnadvisedUnrestoredProjects(new[] { csproj }, advised), Is.Empty);
            Assert.That(advised, Is.Empty);
        });
    }
}

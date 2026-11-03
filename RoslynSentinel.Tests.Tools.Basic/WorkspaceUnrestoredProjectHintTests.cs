using System;
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
}

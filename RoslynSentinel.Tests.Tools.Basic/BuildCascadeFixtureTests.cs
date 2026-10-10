using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("BuildEngine")]
public class BuildCascadeFixtureTests
{
    // Characterises MSBuild's behaviour: a project whose ProjectReference failed to compile is not
    // compiled itself, so a full build reports only the root layer (here Base). Mid and Top are valid
    // code that would compile if Base succeeded, so any error from them would mean MSBuild cascaded.
    [Test]
    [NonParallelizable]
    public async Task FullBuild_UpstreamProjectFails_DownstreamProjectsReportNoErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelB1_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            WriteFixtureSolution(root);
            using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(Path.Combine(root, "CascadeFixture.slnx"));
            var buildEngine = new BuildEngine(workspaceManager, new DiagnosticEngine(workspaceManager));

            var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50, useScratchDir: true);

            Assert.That(result.Data, Is.Not.Null, result.Error?.Message);
            Assert.That(result.Data!.Outcome, Is.EqualTo(BuildOutcome.Failed), result.Data.StdoutTail);
            Assert.That(result.Data.ErrorCount, Is.GreaterThan(0), result.Data.StdoutTail);

            var outside = result.Data.Errors
                .Where(e => !e.FilePath.ToString().Replace('/', '\\').Contains("Base\\", StringComparison.OrdinalIgnoreCase))
                .Select(e => $"{e.Id} {e.FilePath} :: {e.Message}")
                .ToList();
            Assert.That(outside, Is.Empty, "MSBuild reported errors outside Base: " + string.Join("; ", outside));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a build worker node may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static void WriteFixtureSolution(string root)
    {
        File.WriteAllText(Path.Combine(root, "CascadeFixture.slnx"), """
            <Solution>
              <Project Path="Base/Base.csproj" />
              <Project Path="Mid/Mid.csproj" />
              <Project Path="Top/Top.csproj" />
            </Solution>
            """);

        WriteProject(root, "Base", string.Empty, """
            namespace Cascade.Base;

            public static class BaseValue
            {
                public static string Name => "base";

                // Deliberate compile error inside a method body; the public surface stays valid.
                public static int Broken()
                {
                    int x = "text";
                    return x;
                }
            }
            """);

        WriteProject(root, "Mid", @"<ProjectReference Include=""..\Base\Base.csproj"" />", """
            using Cascade.Base;

            namespace Cascade.Mid;

            public static class MidValue
            {
                public static string Describe() => "mid:" + BaseValue.Name;
            }
            """);

        WriteProject(root, "Top", @"<ProjectReference Include=""..\Mid\Mid.csproj"" />", """
            using Cascade.Mid;

            namespace Cascade.Top;

            public static class TopValue
            {
                public static string Describe() => "top:" + MidValue.Describe();
            }
            """);
    }

    private static void WriteProject(string root, string name, string projectReference, string source)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>{projectReference}</ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, name + "Value.cs"), source);
    }
}

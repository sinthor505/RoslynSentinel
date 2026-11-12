using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// WorkspaceBuildTestImpl.WithUnrestoredProjectFindings: a failed build's CS0246/CS0234 errors in a project with no
/// current restore assets get one RestoreState finding naming the cause; every other build failure is unchanged.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")]
public class BuildUnrestoredProjectFindingTests
{
    private const string PackageProjectXml =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>"
        + "<ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.0\" /></ItemGroup></Project>";

    private static DiagnosticInfo Error(string id, string project = "Sample")
        => new(id, "Error", "The type or namespace name 'Xunit' could not be found", "C:\\x\\Sample\\A.cs", 1, 1, 1, 5, Project: project);

    private static void WithTempRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-buildfinding-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
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

    private static string WriteProject(string root, string? assetsJson, DateTime? assetsTimeUtc = null)
    {
        var csproj = Path.Combine(root, "Sample.csproj");
        File.WriteAllText(csproj, PackageProjectXml);
        File.SetLastWriteTimeUtc(csproj, DateTime.UtcNow.AddMinutes(-5));
        if (assetsJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            var assets = Path.Combine(root, "obj", "project.assets.json");
            File.WriteAllText(assets, assetsJson);
            File.SetLastWriteTimeUtc(assets, assetsTimeUtc ?? DateTime.UtcNow);
        }

        return csproj;
    }

    private static IReadOnlyList<Finding> Run(IEnumerable<DiagnosticInfo> errors, string csproj, IReadOnlyList<Finding>? existing = null)
        => WorkspaceBuildTestImpl.WithUnrestoredProjectFindings(existing ?? [], errors, e => e.Project == "Sample" ? csproj : null);

    [Test]
    public void Cs0246InProjectWithNoAssets_AddsOneFindingNamingProjectAndRestoreOptions()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteProject(root, assetsJson: null);

            // Two errors in the same project still produce one finding.
            var findings = Run([Error("CS0246"), Error("CS0234")], csproj);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].Source, Is.EqualTo("RestoreState"));
            Assert.That(findings[0].Severity, Is.EqualTo(FindingSeverity.Info));
            Assert.That(findings[0].Message, Does.StartWith("Sample:"));
            Assert.That(findings[0].Message, Does.Contain("is missing"));
            Assert.That(findings[0].Message, Does.Contain("Build"));
            Assert.That(findings[0].Message, Does.Contain("fullBuild"));
            Assert.That(findings[0].Message, Does.Contain("LoadSolution"));
        });
    }

    [Test]
    public void Cs0246InProjectWithStaleAssets_FindingSaysOlderNotMissing()
    {
        WithTempRoot(root =>
        {
            // Assets older than the csproj and recording a different package version than the csproj now asks for.
            const string staleAssets =
                "{\"version\":3,\"projectFileDependencyGroups\":{\"net8.0\":[\"xunit >= 1.0.0\"]},"
                + "\"project\":{\"frameworks\":{\"net8.0\":{\"targetAlias\":\"net8.0\"}},\"restore\":{\"originalTargetFrameworks\":[\"net8.0\"]}}}";
            var csproj = WriteProject(root, staleAssets, DateTime.UtcNow.AddHours(-1));

            var findings = Run([Error("CS0246")], csproj);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].Message, Does.Contain("older than"));
            Assert.That(findings[0].Message, Does.Not.Contain("is missing"));
        });
    }

    [Test]
    public void Cs0246InProjectWithCurrentAssets_AddsNothingAndKeepsSameList()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteProject(root, "{}", DateTime.UtcNow);
            IReadOnlyList<Finding> existing = [new Finding("EditAttribution", "x")];

            var findings = Run([Error("CS0246")], csproj, existing);

            Assert.That(findings, Is.SameAs(existing));
        });
    }

    [Test]
    public void NonPackageNamespaceError_AddsNothing()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteProject(root, assetsJson: null);
            IReadOnlyList<Finding> existing = [];

            var findings = Run([Error("CS1002"), Error("CS0103")], csproj, existing);

            Assert.That(findings, Is.SameAs(existing));
        });
    }

    [Test]
    public void UnresolvableProject_IsSkippedAndExistingFindingsKept()
    {
        IReadOnlyList<Finding> existing = [new Finding("EditAttribution", "x")];

        var findings = WorkspaceBuildTestImpl.WithUnrestoredProjectFindings(existing, [Error("CS0246")], _ => null);

        Assert.That(findings, Is.SameAs(existing));
    }

    [Test]
    public void ResolverThrows_ReturnsExistingWithoutThrowing()
    {
        IReadOnlyList<Finding> existing = [];

        var findings = WorkspaceBuildTestImpl.WithUnrestoredProjectFindings(
            existing, [Error("CS0246")], _ => throw new InvalidOperationException("boom"));

        Assert.That(findings, Is.SameAs(existing));
    }

    [Test]
    public void ExistingFindingsPreservedAheadOfRestoreFinding()
    {
        WithTempRoot(root =>
        {
            var csproj = WriteProject(root, assetsJson: null);
            IReadOnlyList<Finding> existing = [new Finding("EditAttribution", "x")];

            var findings = Run([Error("CS0246")], csproj, existing);

            Assert.That(findings.Select(f => f.Source), Is.EqualTo(new[] { "EditAttribution", "RestoreState" }));
        });
    }
}

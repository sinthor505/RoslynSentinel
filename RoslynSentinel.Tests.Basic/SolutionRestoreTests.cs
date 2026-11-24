using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>Runs the real `dotnet restore` against a scratch project with no package references.</summary>
[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class SolutionRestoreTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(ScratchRoot.Resolve(), "SolutionRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A lingering MSBuild node can hold a handle; a leftover scratch folder is not a test failure.
        }
    }

    [Test]
    public async Task RestoreAsync_ProjectWithoutPackages_SucceedsAndWritesAssetsFile()
    {
        var csproj = Path.Combine(_root, "Demo.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var result = await SolutionRestore.RestoreAsync(csproj, TimeSpan.FromMinutes(3), CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.OutputTail);
        Assert.That(result.Started, Is.True);
        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(File.Exists(Path.Combine(_root, "obj", "project.assets.json")), Is.True);
    }

    [Test]
    public async Task RestoreAsync_MissingSolutionFile_ReportsNonZeroExitWithOutputTailAndDoesNotThrow()
    {
        var result = await SolutionRestore.RestoreAsync(Path.Combine(_root, "Missing.sln"), TimeSpan.FromMinutes(3), CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Started, Is.True);
        Assert.That(result.ExitCode, Is.Not.Null.And.Not.Zero);
        Assert.That(result.OutputTail, Is.Not.Empty);
    }

    [Test]
    public async Task RestoreAsync_TimeoutShorterThanDotnetStartup_ReportsTimedOut()
    {
        var csproj = Path.Combine(_root, "Demo.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var result = await SolutionRestore.RestoreAsync(csproj, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task RestoreAsync_CancelledToken_Throws()
    {
        var csproj = Path.Combine(_root, "Demo.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(async () => await SolutionRestore.RestoreAsync(csproj, TimeSpan.FromMinutes(3), cts.Token));
    }
}

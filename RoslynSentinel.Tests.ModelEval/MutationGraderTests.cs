using System.Security.Cryptography;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free checks of <see cref="MutationGrader"/> (plan_modeleval_first_slice_scenario_rework Step 6). One tiny two-project solution
/// (a library and an NUnit test project) is written under the scratch root and its baseline run once per fixture; each test then
/// overlays one mutant through <see cref="MutationGrader.RunAsync"/>. Package versions are those of this repo's test projects, so
/// the restore of every run directory is served from the local NuGet cache (no network needed once the repo has restored).
/// Each mutant run copies the directory and does a restore, build and test, so the class is explicit and must not run in parallel.
/// Filter: <c>MutationGraderTests</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
[Explicit("Builds and tests a tiny two-project solution with dotnet (restore from the local NuGet cache). Run: MutationGraderTests")]
public class MutationGraderTests
{
    private const string LibraryPath = "TinyLib/Calculator.cs";
    private const string TestProjectPath = "TinyLib.Tests/TinyLib.Tests.csproj";
    private const string TestFilter = "FullyQualifiedName~CalculatorTests";

    // Stops MSBuild from inheriting a Directory.Build.props/.targets from a folder above the scratch root.
    private const string EmptyPropsSource = """
<Project />
""";

    private const string LibraryProjectSource = """
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
""";

    private const string TestProjectSource = """
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.12.0" />
    <PackageReference Include="NUnit" Version="5.0.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\TinyLib\TinyLib.csproj" />
  </ItemGroup>

</Project>
""";

    private const string CalculatorSource = """
namespace TinyLib;

public static class Calculator
{
    public static int Add(int left, int right) => left + right;

    public static int Double(int value) => value * 2;
}
""";

    // Two tests, one per method, so a mutant of Add leaves the Double test passing.
    private const string CalculatorTestsSource = """
using NUnit.Framework;

using TinyLib;

namespace TinyLib.Tests;

[TestFixture]
public class CalculatorTests
{
    [Test]
    public void Add_AddsTwoNumbers() => Assert.That(Calculator.Add(2, 3), Is.EqualTo(5));

    [Test]
    public void Double_DoublesTheValue() => Assert.That(Calculator.Double(4), Is.EqualTo(8));
}
""";

    // Add subtracts: Add_AddsTwoNumbers fails, Double_DoublesTheValue still passes.
    private const string BreakingMutantSource = """
namespace TinyLib;

public static class Calculator
{
    public static int Add(int left, int right) => left - right;

    public static int Double(int value) => value * 2;
}
""";

    // Operands swapped and the product written as a sum: behaviour is identical, so every test passes and the mutant survives.
    private const string EquivalentMutantSource = """
namespace TinyLib;

public static class Calculator
{
    public static int Add(int left, int right) => right + left;

    public static int Double(int value) => value + value;
}
""";

    // Double is gone: the test project no longer compiles (CS0117 in CalculatorTests), so no test runs at all.
    private const string NonCompilingMutantSource = """
namespace TinyLib;

public static class Calculator
{
    public static int Add(int left, int right) => left + right;
}
""";

    private string _solutionDirectory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _solutionDirectory = Path.Combine(ScratchRoot.Resolve(), "MutationGraderTests", Guid.NewGuid().ToString("N"));
        WriteFile("Directory.Build.props", EmptyPropsSource);
        WriteFile("Directory.Build.targets", EmptyPropsSource);
        WriteFile("TinyLib/TinyLib.csproj", LibraryProjectSource);
        WriteFile(LibraryPath, CalculatorSource);
        WriteFile(TestProjectPath, TestProjectSource);
        WriteFile("TinyLib.Tests/CalculatorTests.cs", CalculatorTestsSource);

        // The baseline: the unmutated solution builds and both tests pass, so a survivor below means "tests pass", not "nothing ran".
        var baseline = await DotnetTestRunner.RunAsync(
            Path.Combine(_solutionDirectory, TestProjectPath.Replace('/', Path.DirectorySeparatorChar)),
            TestContext.CurrentContext.CancellationToken,
            TestFilter);
        Assert.That(baseline.Total, Is.EqualTo(2), "baseline run of the tiny solution: " + baseline.RawOutput);
        Assert.That(baseline.Failed, Is.Zero, "baseline run of the tiny solution: " + baseline.RawOutput);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            if (_solutionDirectory is not null && Directory.Exists(_solutionDirectory))
            {
                Directory.Delete(_solutionDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file (a lingering test host) leaves a scratch folder behind; that is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: scratch leftover only.
        }
    }

    [Test]
    public async Task Mutant_BreakingATest_IsKilled()
    {
        var result = await RunOneAsync("add-subtracts", BreakingMutantSource);

        Assert.Multiple(() =>
        {
            Assert.That(result.Name, Is.EqualTo("add-subtracts"));
            Assert.That(result.Killed, Is.True);
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(result.Passed, Is.EqualTo(1));
            Assert.That(result.Note, Is.Empty);
        });
    }

    [Test]
    public async Task Mutant_ThatPassesAllTests_Survives()
    {
        var result = await RunOneAsync("equivalent", EquivalentMutantSource);

        Assert.Multiple(() =>
        {
            Assert.That(result.Killed, Is.False);
            Assert.That(result.Failed, Is.Zero);
            Assert.That(result.Passed, Is.EqualTo(2));
            Assert.That(result.Note, Is.Empty);
        });
    }

    [Test]
    public async Task Mutant_ThatDoesNotCompile_IsKilledWithNote()
    {
        var result = await RunOneAsync("double-removed", NonCompilingMutantSource);

        Assert.Multiple(() =>
        {
            Assert.That(result.Killed, Is.True);
            Assert.That(result.Passed, Is.Zero);
            Assert.That(result.Failed, Is.Zero);
            Assert.That(result.Note, Does.Contain("did not compile"));
        });
    }

    [Test]
    public async Task Run_DoesNotModifyTheOriginalDirectory()
    {
        var before = Snapshot(_solutionDirectory);

        var result = await RunOneAsync("add-subtracts-again", BreakingMutantSource);

        Assert.That(result.Killed, Is.True, "the mutant must have run, otherwise the snapshot comparison proves nothing");
        Assert.That(Snapshot(_solutionDirectory), Is.EqualTo(before), "MutationGrader changed a file of the directory it was given");
        Assert.That(File.ReadAllText(Path.Combine(_solutionDirectory, LibraryPath.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(CalculatorSource));
    }

    private async Task<MutantResult> RunOneAsync(string name, string librarySource)
    {
        var mutant = new ScenarioMutant(name, new Dictionary<string, string> { [LibraryPath] = librarySource });

        var results = await MutationGrader.RunAsync(
            _solutionDirectory, TestProjectPath, TestFilter, new[] { mutant }, TestContext.CurrentContext.CancellationToken);

        Assert.That(results, Has.Count.EqualTo(1));
        return results[0];
    }

    private void WriteFile(string relativePath, string text)
    {
        var path = Path.Combine(_solutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // Relative path to content hash for every file, bin and obj included.
    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(directory, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
            StringComparer.OrdinalIgnoreCase);
}

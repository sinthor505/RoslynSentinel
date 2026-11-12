namespace RoslynSentinel.Tests;

/// <summary>
/// Covers the opt-in <c>restorePackages</c> switch on <see cref="TestSolutionFixture"/>. The default
/// fixture must leave no restore assets in its copy; the opt-in fixture must produce them for
/// ContosoOrders.Tests. The second test runs <c>dotnet restore</c>, so it is categorised as out of process.
/// </summary>
[TestFixture]
[Category("OutOfProcess")]
public class TestSolutionFixtureRestoreTests
{
    [Test]
    public void DefaultFixture_HasNoRestoreAssets()
    {
        using var fixture = new TestSolutionFixture();

        var assets = Directory.GetFiles(fixture.SolutionDirectory, "project.assets.json", SearchOption.AllDirectories);
        Assert.That(assets, Is.Empty, "The default fixture must not run restore, but restore assets were found in its copy.");
    }

    [Test]
    public void RestoreFixture_ProducesAssetsForTheTestProject()
    {
        using var fixture = new TestSolutionFixture(restorePackages: true);

        var assetsPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Tests", "obj", "project.assets.json");
        Assert.That(File.Exists(assetsPath), Is.True, $"Expected restore assets at '{assetsPath}' after restorePackages: true.");
    }
}

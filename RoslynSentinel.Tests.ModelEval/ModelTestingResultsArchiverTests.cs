namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ModelTestingResultsArchiver.DeriveHostSuffix"/>: only the exact alias base URLs map to the short alias folder;
/// any other URL gets the sanitised-URL suffix that roslynsentinel-modeleval.ps1 computes. No live model.
/// </summary>
[TestFixture]
public class ModelTestingResultsArchiverTests
{
    [TestCase("http://192.168.1.112:1234/v1", "112")]
    [TestCase("http://192.168.1.113:1234/v1", "113")]
    [TestCase("http://192.168.10.85:1234/v1", "85")]
    [TestCase("http://192.168.1.112:1234/v1/", "112")]
    [TestCase("HTTP://192.168.1.113:1234/V1", "113")]
    public void DeriveHostSuffix_ExactAliasUrl_MapsToTheAlias(string baseUrl, string expected)
    {
        Assert.That(ModelTestingResultsArchiver.DeriveHostSuffix(baseUrl), Is.EqualTo(expected));
    }

    [TestCase("http://192.168.1.112:8888/v1", "http___192_168_1_112_8888_v1")]
    [TestCase("http://192.168.1.113:8888/v1", "http___192_168_1_113_8888_v1")]
    [TestCase("http://localhost:1234/v1", "http___localhost_1234_v1")]
    public void DeriveHostSuffix_AnyOtherUrl_GetsTheSanitisedUrl(string baseUrl, string expected)
    {
        Assert.That(ModelTestingResultsArchiver.DeriveHostSuffix(baseUrl), Is.EqualTo(expected));
    }
}

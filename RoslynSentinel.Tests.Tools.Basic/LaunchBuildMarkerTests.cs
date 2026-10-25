namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class LaunchBuildMarkerTests
{
    [SetUp]
    public void SetUp() => LaunchBuildMarker.ResetEnvelopeWarningForTests();

    [TearDown]
    public void TearDown() => LaunchBuildMarker.ResetEnvelopeWarningForTests();

    [Test]
    public void Parse_FailedBuild_ReadsShasFirstErrorsAndHint()
    {
        const string json = "{\"utc\":\"2026-10-24T20:39:51Z\",\"attemptedSha\":\"36e51f0c\",\"servedSha\":\"2c93d97b\",\"buildFailed\":true,\"firstErrors\":[\"a.cs(1,1): error CS0103: x\"]}";

        var result = LaunchBuildMarker.Parse(json);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BuildFailed, Is.True);
        Assert.That(result.AttemptedSha, Is.EqualTo("36e51f0c"));
        Assert.That(result.ServedSha, Is.EqualTo("2c93d97b"));
        Assert.That(result.FirstErrors, Has.Count.EqualTo(1));
        Assert.That(result.Hint, Does.Contain("36e51f0c").And.Contain("2c93d97b"));
    }

    [Test]
    public void Parse_SuccessfulBuild_HasNoHintAndNoErrors()
    {
        var result = LaunchBuildMarker.Parse("{\"utc\":\"2026-10-24T20:39:51Z\",\"attemptedSha\":\"36e51f0c\",\"servedSha\":\"36e51f0c\",\"buildFailed\":false,\"firstErrors\":[]}");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BuildFailed, Is.False);
        Assert.That(result.Hint, Is.Null);
        Assert.That(result.FirstErrors, Is.Empty);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("[]")]
    public void Parse_EmptyMalformedOrNonObject_ReturnsNull(string json)
    {
        Assert.That(LaunchBuildMarker.Parse(json), Is.Null);
    }

    [Test]
    public void Read_MarkerPresentOrMissing_ParsesOrReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.That(LaunchBuildMarker.Read(dir), Is.Null, "A missing marker file reads as no outcome.");

            File.WriteAllText(Path.Combine(dir, LaunchBuildMarker.FileName),
                "{\"utc\":\"2026-10-24T20:39:51Z\",\"attemptedSha\":\"36e51f0c\",\"servedSha\":\"2c93d97b\",\"buildFailed\":true,\"firstErrors\":[]}");

            var result = LaunchBuildMarker.Read(dir);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.BuildFailed, Is.True);
            Assert.That(result.ServedSha, Is.EqualTo("2c93d97b"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The once-only envelope flag is process-global (static), so these tests must not overlap with each other.
    [Test]
    [NonParallelizable]
    public void TakeEnvelopeWarning_FailedBuild_ReturnsHintOnceUntilReset()
    {
        var outcome = LaunchBuildMarker.Parse("{\"utc\":\"2026-10-24T20:39:51Z\",\"attemptedSha\":\"36e51f0c\",\"servedSha\":\"2c93d97b\",\"buildFailed\":true,\"firstErrors\":[]}");

        Assert.That(LaunchBuildMarker.TakeEnvelopeWarning(outcome), Is.EqualTo(outcome!.Hint));
        Assert.That(LaunchBuildMarker.TakeEnvelopeWarning(outcome), Is.Null);

        LaunchBuildMarker.ResetEnvelopeWarningForTests();

        Assert.That(LaunchBuildMarker.TakeEnvelopeWarning(outcome), Is.EqualTo(outcome.Hint));
    }

    // Same process-global flag as above, hence [NonParallelizable].
    [Test]
    [NonParallelizable]
    public void TakeEnvelopeWarning_SuccessOrNullOutcome_ReturnsNullAndDoesNotConsumeFlag()
    {
        var success = LaunchBuildMarker.Parse("{\"utc\":\"2026-10-24T20:39:51Z\",\"attemptedSha\":\"36e51f0c\",\"servedSha\":\"36e51f0c\",\"buildFailed\":false,\"firstErrors\":[]}");
        var failed = LaunchBuildMarker.Parse("{\"utc\":\"2026-10-24T20:39:51Z\",\"attemptedSha\":\"36e51f0c\",\"servedSha\":\"2c93d97b\",\"buildFailed\":true,\"firstErrors\":[]}");

        Assert.That(LaunchBuildMarker.TakeEnvelopeWarning(success), Is.Null);
        Assert.That(LaunchBuildMarker.TakeEnvelopeWarning(null), Is.Null);
        Assert.That(LaunchBuildMarker.TakeEnvelopeWarning(failed), Is.EqualTo(failed!.Hint));
    }
}

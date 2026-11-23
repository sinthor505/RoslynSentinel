using System.Globalization;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
[NonParallelizable]
public class LlmOptionsHostSamplerTests
{
    [SetUp]
    public void SetUp() => LlmOptions.Configure(Array.Empty<string>());

    [TearDown]
    public void TearDown() => LlmOptions.Configure(Array.Empty<string>());

    [Test]
    public void ParseHostSampler_ParsesAllFourCanonicalPairs()
    {
        var result = LlmOptions.ParseHostSampler("topK=20;minP=0.1;repeatPenalty=1.0;presencePenalty=0");

        Assert.That(result, Has.Count.EqualTo(4));
        Assert.That(result["topK"], Is.EqualTo(20.0));
        Assert.That(result["minP"], Is.EqualTo(0.1));
        Assert.That(result["repeatPenalty"], Is.EqualTo(1.0));
        Assert.That(result["presencePenalty"], Is.EqualTo(0.0));
    }

    [Test]
    public void ParseHostSampler_KeysAreCaseInsensitiveAndCanonicalized()
    {
        var result = LlmOptions.ParseHostSampler("TOPK=5;frequencypenalty=0.5");

        Assert.That(result.Keys, Is.EquivalentTo(new[] { "topK", "frequencyPenalty" }));
        Assert.That(result["topK"], Is.EqualTo(5.0));
        Assert.That(result["frequencyPenalty"], Is.EqualTo(0.5));
    }

    [Test]
    public void ParseHostSampler_SkipsUnknownKeysNoEqualsUnparsableValuesAndEmptySegments()
    {
        var result = LlmOptions.ParseHostSampler("bogus=1;topK;minP=abc;;topK=7;repeatPenalty=1.1");

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result["topK"], Is.EqualTo(7.0));
        Assert.That(result["repeatPenalty"], Is.EqualTo(1.1));
        Assert.That(result.ContainsKey("minP"), Is.False);
    }

    [Test]
    public void ParseHostSampler_TrimsSpacesAroundKeysAndValues()
    {
        var result = LlmOptions.ParseHostSampler(" topK = 12 ; minP= 0.25 ");

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result["topK"], Is.EqualTo(12.0));
        Assert.That(result["minP"], Is.EqualTo(0.25));
    }

    [Test]
    public void ParseHostSampler_NullOrEmptyGivesEmptyDictionary()
    {
        Assert.That(LlmOptions.ParseHostSampler(null), Is.Empty);
        Assert.That(LlmOptions.ParseHostSampler(""), Is.Empty);
    }

    [Test]
    public void ParseHostSampler_RepeatedKeyKeepsLastValue()
    {
        var result = LlmOptions.ParseHostSampler("topK=1;topK=2;TOPK=3");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result["topK"], Is.EqualTo(3.0));
    }

    [Test]
    public void ParseHostSampler_DecimalPointIsInvariantUnderGermanCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var result = LlmOptions.ParseHostSampler("minP=0.05");

            Assert.That(result["minP"], Is.EqualTo(0.05));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void Configure_ReadsHostSamplerFromArgument()
    {
        LlmOptions.Configure(new[] { "--llm-host-sampler=topK=40;minP=0.2" });

        Assert.That(LlmOptions.HostSampler, Has.Count.EqualTo(2));
        Assert.That(LlmOptions.HostSampler["topK"], Is.EqualTo(40.0));
        Assert.That(LlmOptions.HostSampler["minP"], Is.EqualTo(0.2));
    }

    [Test]
    public void AcceptUnverifiedSampler_DefaultsToFalse()
    {
        LlmOptions.Configure(Array.Empty<string>());

        Assert.That(LlmOptions.AcceptUnverifiedSampler, Is.False);
    }

    [Test]
    public void Configure_ReadsAcceptUnverifiedSamplerFromArgument()
    {
        LlmOptions.Configure(new[] { "--llm-accept-unverified-sampler", "true" });
        Assert.That(LlmOptions.AcceptUnverifiedSampler, Is.True);

        LlmOptions.Configure(new[] { "--llm-accept-unverified-sampler=1" });
        Assert.That(LlmOptions.AcceptUnverifiedSampler, Is.True);
    }

    [Test]
    public void Configure_TreatsFalseAcceptUnverifiedSamplerValueAsFalse()
    {
        LlmOptions.Configure(new[] { "--llm-accept-unverified-sampler", "false" });

        Assert.That(LlmOptions.AcceptUnverifiedSampler, Is.False);
    }

    [Test]
    public void Configure_FoldsLegacySamplerOptionsIntoHostSamplerAndHostSamplerWins()
    {
        LlmOptions.Configure(new[] { "--llm-host-sampler=topK=40", "--llm-top-k=10", "--llm-min-p=0.1", "--llm-repeat-penalty=1.0" });

        Assert.That(LlmOptions.HostSampler, Has.Count.EqualTo(3));
        Assert.That(LlmOptions.HostSampler["topK"], Is.EqualTo(40.0));
        Assert.That(LlmOptions.HostSampler["minP"], Is.EqualTo(0.1));
        Assert.That(LlmOptions.HostSampler["repeatPenalty"], Is.EqualTo(1.0));
    }
}

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="StreamPhraseLoopDetector"/> in isolation (internal; visible through the AgentLoop InternalsVisibleTo): sentence cutting
/// across deltas, the length floor, the code-fence exclusion, the sliding window and a long normal output.
/// </summary>
[TestFixture]
public class StreamPhraseLoopDetectorTests
{
    private const string LoopSentence = "The tests will fail. ";

    private static string? FeedAll(StreamPhraseLoopDetector detector, IEnumerable<string> deltas)
    {
        foreach (var delta in deltas)
        {
            if (detector.Append(delta) is { } looped)
            {
                return looped;
            }
        }

        return null;
    }

    [Test]
    public void SameSentenceTwentyTimes_TripsOnTheTwentiethRepeat_NormalizedSentence()
    {
        var detector = new StreamPhraseLoopDetector();

        for (var i = 1; i < StreamPhraseLoopDetector.RepeatLimit; i++)
        {
            Assert.That(detector.Append(LoopSentence), Is.Null, $"repeat {i}");
        }

        Assert.That(detector.Append(LoopSentence), Is.EqualTo("the tests will fail"));
    }

    [Test]
    public void SentenceSplitAcrossDeltas_IsCountedOnceItsBoundaryArrives()
    {
        var detector = new StreamPhraseLoopDetector();
        // "The tests will fail. " arrives as "The te" / "sts will fail" / "." / " " pieces; the boundary space comes last.
        var pieces = new[] { "The te", "sts will fail", ".", " " };

        var looped = FeedAll(detector, Enumerable.Repeat(pieces, StreamPhraseLoopDetector.RepeatLimit).SelectMany(p => p));

        Assert.That(looped, Is.EqualTo("the tests will fail"));
    }

    [Test]
    public void DifferentCaseAndWhitespaceAndPunctuation_NormalizeToTheSameSentence()
    {
        var detector = new StreamPhraseLoopDetector();
        var variants = new[] { "The tests  will fail. ", "the TESTS will fail!\n", "The tests will\tfail? " };

        var looped = FeedAll(detector, Enumerable.Range(0, StreamPhraseLoopDetector.RepeatLimit).Select(i => variants[i % variants.Length]));

        Assert.That(looped, Is.EqualTo("the tests will fail"));
    }

    [Test]
    public void ShortSentences_AreIgnored()
    {
        var detector = new StreamPhraseLoopDetector();

        var looped = FeedAll(detector, Enumerable.Repeat("Ok. \n}\n", 200));

        Assert.That(looped, Is.Null);
    }

    [Test]
    public void RepeatedLinesInsideACodeFence_AreIgnored()
    {
        var detector = new StreamPhraseLoopDetector();
        var deltas = new List<string> { "Here is the code:\n```csharp\n" };
        deltas.AddRange(Enumerable.Repeat("        this_is_a_repeated_long_code_line();\n", 60));
        deltas.Add("```\n");

        Assert.That(FeedAll(detector, deltas), Is.Null);
    }

    [Test]
    public void TextAfterAClosedCodeFence_IsCountedAgain()
    {
        var detector = new StreamPhraseLoopDetector();
        var deltas = new List<string> { "```\ncode line one that is long enough\n```\n" };
        deltas.AddRange(Enumerable.Repeat(LoopSentence, StreamPhraseLoopDetector.RepeatLimit));

        Assert.That(FeedAll(detector, deltas), Is.EqualTo("the tests will fail"));
    }

    [Test]
    public void Repeats_SpreadWiderThanTheWindow_DoNotTrip()
    {
        var detector = new StreamPhraseLoopDetector();
        var deltas = new List<string>();

        // One repeat every 100 distinct sentences: at most about two repeats are inside the 200-sentence window.
        for (var round = 0; round < 40; round++)
        {
            deltas.Add(LoopSentence);
            deltas.AddRange(Enumerable.Range(0, 100).Select(n => $"Filler sentence {round}-{n} is unique text. "));
        }

        Assert.That(FeedAll(detector, deltas), Is.Null);
    }

    [Test]
    public void LongNormalOutput_WithDistinctSentencesAndARepeatedBraceCodeBlock_DoesNotTrip()
    {
        var detector = new StreamPhraseLoopDetector();
        var deltas = new List<string>();
        for (var n = 0; n < 500; n++)
        {
            deltas.Add($"Step {n} changes a different member of the type. ");
        }

        deltas.Add("\n```csharp\npublic class A\n{\n");
        for (var n = 0; n < 50; n++)
        {
            deltas.Add("    if (x)\n    {\n    }\n}\n}\n}\n");
        }

        deltas.Add("```\nDone.\n");

        Assert.That(FeedAll(detector, deltas), Is.Null);
    }

    [Test]
    public void InstancesKeepSeparateState_PerChannel()
    {
        var content = new StreamPhraseLoopDetector();
        var reasoning = new StreamPhraseLoopDetector();

        // Ten repeats on each channel: neither reaches 20 although the combined total would.
        for (var i = 0; i < 10; i++)
        {
            Assert.That(content.Append(LoopSentence), Is.Null);
            Assert.That(reasoning.Append(LoopSentence), Is.Null);
        }
    }
}

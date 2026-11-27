using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Pins the shipped scripts/eval-review/vocabulary.json (copied to the test output dir) stopReasons array to the AgentStopReason enum order. No live model.
/// </summary>
[TestFixture]
public class EvalReviewVocabularyTests
{
    [Test]
    public void StopReasonNames_MatchAgentStopReasonOrder()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "eval-review-vocabulary.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var stopReasons = document.RootElement.GetProperty("stopReasons")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        var enumNames = Enum.GetNames<AgentStopReason>();
        Assert.That(stopReasons, Is.EqualTo(enumNames));

        for (var i = 0; i < stopReasons.Count; i++)
        {
            Assert.That((int)Enum.Parse<AgentStopReason>(stopReasons[i]!), Is.EqualTo(i), $"index {i} name {stopReasons[i]}");
        }
    }
}

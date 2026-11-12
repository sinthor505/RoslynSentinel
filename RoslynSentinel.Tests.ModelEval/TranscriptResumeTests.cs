using System.Text.Json;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// The pure parts of the resume-from-transcript mode (<see cref="TranscriptResume"/>): settings, keeping turns 1..K,
/// pointing the fixture paths at the new fixture, replaying the kept calls in order, and the results.csv row. The
/// seeding of the runner itself is covered in <see cref="LoopAssistRunnerTests"/>. No live model.
/// </summary>
[TestFixture]
public class TranscriptResumeTests
{
    private const string OldGuidFolder = "RoslynSentinelTests_f3967029-469f-46a2-b5dc-2f6c28bb26e4";
    private const string NewDirectory = @"C:\work\tmp\RoslynSentinelTests_11111111-2222-3333-4444-555555555555";
    private const string Rejection =
        """{"isError":true,"errorData":{"message":"ReplaceSnippet: rejected. Next step: use RenameSymbol(...) instead."}}""";

    // ---- settings ----

    [Test]
    public void Settings_Unset_UseTheDocumentedDefaults()
    {
        var settings = ResumeSettings.FromEnvironment(_ => null);

        Assert.That(settings, Is.EqualTo(new ResumeSettings(null, 3, 10, 3)));
    }

    [Test]
    public void Settings_ReadAllFourVariables()
    {
        var values = new Dictionary<string, string>
        {
            [ResumeSettings.TranscriptVariable] = "  \"C:\\runs\\x\"  ",
            [ResumeSettings.KeepTurnsVariable] = "5",
            [ResumeSettings.SamplesVariable] = "20",
            [ResumeSettings.ContinueTurnsVariable] = "4",
        };

        var settings = ResumeSettings.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.That(settings, Is.EqualTo(new ResumeSettings(@"C:\runs\x", 5, 20, 4)));
    }

    [TestCase("0")]
    [TestCase("-2")]
    [TestCase("three")]
    public void Settings_ANonPositiveOrNonNumericValue_NamesTheVariable(string bad)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ResumeSettings.FromEnvironment(name => name == ResumeSettings.SamplesVariable ? bad : null));

        Assert.That(ex!.Message, Does.Contain(ResumeSettings.SamplesVariable));
    }

    // ---- keeping turns ----

    [Test]
    public void KeepTurns_TakesTurnsOneToK_InOrder()
    {
        var source = new ResumeSource { Turns = [Turn(1, "A"), Turn(2, "B"), Turn(3, "C"), Turn(4, "D")] };

        var kept = TranscriptResume.KeepTurns(source, 3);

        Assert.That(kept.Select(t => t.TurnNumber), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [TestCase(0)]
    [TestCase(5)]
    public void KeepTurns_OutsideTheTranscript_Throws(int keep)
    {
        var source = new ResumeSource { Turns = [Turn(1, "A"), Turn(2, "B")] };

        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptResume.KeepTurns(source, keep));
    }

    [Test]
    public void Load_ReadsPromptsAndTurnsFromAWrittenTranscript_ByFileOrFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rs-resume-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var written = new AgentTranscript { RunId = "r", SystemPrompt = "sys", UserPrompt = "usr" };
            written.Turns.Add(Turn(1, "ReadFile"));
            written.Turns.Add(Turn(2, "ReplaceSnippet"));
            File.WriteAllText(Path.Combine(folder, "transcript.json"), JsonSerializer.Serialize(written));

            var viaFolder = TranscriptResume.Load(TranscriptResume.ResolveTranscriptPath(folder));
            var viaFile = TranscriptResume.Load(TranscriptResume.ResolveTranscriptPath(Path.Combine(folder, "transcript.json")));

            Assert.Multiple(() =>
            {
                Assert.That(viaFolder.SystemPrompt, Is.EqualTo("sys"));
                Assert.That(viaFolder.UserPrompt, Is.EqualTo("usr"));
                Assert.That(viaFolder.Turns.Select(t => t.ToolCalls.Single().ToolName), Is.EqualTo(new[] { "ReadFile", "ReplaceSnippet" }));
                Assert.That(viaFolder.Turns[1].ModelMessage.ToolCalls.Single().Id, Is.EqualTo("call_2"));
                Assert.That(viaFile.Turns, Has.Count.EqualTo(2));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void ResolveTranscriptPath_Missing_ThrowsWithTheResolvedPath()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => TranscriptResume.ResolveTranscriptPath(@"C:\no\such\folder"));

        Assert.That(ex!.Message, Does.Contain("C:\\no\\such\\folder"));
    }

    // ---- fixture directory rewrite ----

    [Test]
    public void RewriteFixtureDirectory_KeepsEachOccurrencesPathStyle()
    {
        var oldBack = @"C:\Users\Administrator\AppData\Local\Temp\2\" + OldGuidFolder;
        var oldForward = "C:/Users/Administrator/AppData/Local/Temp/2/" + OldGuidFolder;
        var oldEscaped = oldBack.Replace("\\", "\\\\");
        var text = $"prompt `{oldBack}\\ContosoOrders.Core/a.cs` args \"{oldForward}/ContosoOrders.Core/b.cs\" result \"{oldEscaped}\\\\x\"";

        var rewritten = TranscriptResume.RewriteFixtureDirectory(text, NewDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Does.Not.Contain(OldGuidFolder));
            Assert.That(rewritten, Does.Contain(@"`C:\work\tmp\RoslynSentinelTests_11111111-2222-3333-4444-555555555555\ContosoOrders.Core/a.cs`"));
            Assert.That(rewritten, Does.Contain("\"C:/work/tmp/RoslynSentinelTests_11111111-2222-3333-4444-555555555555/ContosoOrders.Core/b.cs\""));
            Assert.That(rewritten, Does.Contain("\"C:\\\\work\\\\tmp\\\\RoslynSentinelTests_11111111-2222-3333-4444-555555555555\\\\x\""));
        });
    }

    [Test]
    public void RewriteFixtureDirectory_TextWithoutAFixturePath_IsUnchanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TranscriptResume.RewriteFixtureDirectory("no path here: C:\\other\\dir", NewDirectory), Is.EqualTo("no path here: C:\\other\\dir"));
            Assert.That(TranscriptResume.RewriteFixtureDirectory(null, NewDirectory), Is.Null);
        });
    }

    [Test]
    public void RewriteTurns_PointsArgumentsAndResultsAtTheNewFixture_WithoutTouchingTheOriginals()
    {
        var oldPath = "C:/Temp/" + OldGuidFolder + "/ContosoOrders.Core/a.cs";
        var original = Turn(1, "ReadFile", argumentsJson: $$"""{"filePath":"{{oldPath}}"}""", resultJson: $$"""{"solutionRoot":"{{oldPath}}"}""");

        var rewritten = TranscriptResume.RewriteTurns([original], NewDirectory).Single();

        Assert.Multiple(() =>
        {
            Assert.That(rewritten.ToolCalls[0].ArgumentsJson, Does.Contain("C:/work/tmp/RoslynSentinelTests_11111111"));
            Assert.That(rewritten.ToolCalls[0].ResultJson, Does.Contain("C:/work/tmp/RoslynSentinelTests_11111111"));
            Assert.That(rewritten.ModelMessage.ToolCalls[0].ArgumentsJson, Is.EqualTo(rewritten.ToolCalls[0].ArgumentsJson));
            Assert.That(rewritten.ModelMessage.ToolCalls[0].Id, Is.EqualTo(original.ModelMessage.ToolCalls[0].Id));
            Assert.That(rewritten.ToolCalls[0].ToolCallId, Is.EqualTo(original.ToolCalls[0].ToolCallId));
            Assert.That(original.ToolCalls[0].ArgumentsJson, Does.Contain(OldGuidFolder), "the source turn is not modified");
        });
    }

    // ---- replay of the kept calls ----

    [Test]
    public async Task ReplayKeptCallsAsync_CallsEveryRecordedCallInRecordedOrder_AndCountsFlagMismatches()
    {
        var turns = new List<AgentTranscriptTurn>
        {
            Turn(1, "ReadFile", isError: false),
            Turn(2, "ReplaceSnippet", isError: true),
            Turn(3, "ReplaceSnippet", isError: true),
        };
        var calls = new List<string>();
        var log = new List<string>();

        var mismatches = await TranscriptReplayCountingAsync(turns, calls, log, replayedIsError: _ => false);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(new[] { "ReadFile:args_1", "ReplaceSnippet:args_2", "ReplaceSnippet:args_3" }));
            Assert.That(mismatches, Is.EqualTo(2), "both recorded rejections replayed as successes");
            Assert.That(log.Count(l => l.StartsWith("*** MISMATCH")), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ReplayKeptCallsAsync_NothingDiverges_ReportsZero()
    {
        var turns = new List<AgentTranscriptTurn> { Turn(1, "ReadFile", isError: false), Turn(2, "ReplaceSnippet", isError: true) };

        var mismatches = await TranscriptReplayCountingAsync(turns, [], [], replayedIsError: tool => tool == "ReplaceSnippet");

        Assert.That(mismatches, Is.Zero);
    }

    [Test]
    public async Task ReplayKeptCallsDetailed_ReturnsOneEntryPerCallWithReplayedText()
    {
        var turns = new List<AgentTranscriptTurn>
        {
            Turn(1, "ReadFile", isError: false, resultJson: "{\"ok\":1}"),
            Turn(2, "ReplaceSnippet", isError: true, resultJson: "{\"rejected\":2}"),
        };

        var replayed = await TranscriptResume.ReplayKeptCallsDetailedAsync(
            turns,
            (tool, _, _) => Task.FromResult(($"r:{tool}", tool == "ReplaceSnippet")),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(replayed, Has.Count.EqualTo(2));
            Assert.That(replayed[0].RecordedResultText, Is.EqualTo("{\"ok\":1}"));
            Assert.That(replayed[0].ReplayedResultText, Is.EqualTo("r:ReadFile"));
            Assert.That(replayed[1].RecordedResultText, Is.EqualTo("{\"rejected\":2}"));
            Assert.That(replayed[1].ReplayedResultText, Is.EqualTo("r:ReplaceSnippet"));
            Assert.That(replayed.Any(call => call.Mismatch), Is.False);
        });
    }

    [Test]
    public async Task ReplayKeptCallsDetailed_FlagsDoctoredTranscript()
    {
        // Recorded as a success, but the replayed call is rejected: the entry must be flagged and logged.
        var turns = new List<AgentTranscriptTurn> { Turn(1, "ReplaceSnippet", isError: false) };
        var log = new List<string>();

        var replayed = await TranscriptResume.ReplayKeptCallsDetailedAsync(
            turns,
            (_, _, _) => Task.FromResult((Rejection, true)),
            CancellationToken.None,
            log.Add);

        Assert.Multiple(() =>
        {
            Assert.That(replayed, Has.Count.EqualTo(1));
            Assert.That(replayed[0].Mismatch, Is.True);
            Assert.That(log.Any(line => line.Contains("MISMATCH")), Is.True);
        });
    }

    [Test]
    public void ReplayMismatchReason_IsNullWithoutMismatches()
    {
        var replayed = new List<ReplayedCall>
        {
            new ReplayedCall(1, "ReadFile", false, false, "{}", "r:ReadFile"),
        };

        Assert.That(TranscriptResume.ReplayMismatchReason(replayed), Is.Null);
    }

    [Test]
    public void ReplayMismatchReason_NamesFirstMismatch()
    {
        var replayed = new List<ReplayedCall>
        {
            new ReplayedCall(1, "ReadFile", false, false, "{}", "r:ReadFile"),
            new ReplayedCall(2, "ReplaceSnippet", true, false, Rejection, "r:ReplaceSnippet"),
            new ReplayedCall(3, "ReplaceSnippet", false, true, "{}", Rejection),
        };

        Assert.That(
            TranscriptResume.ReplayMismatchReason(replayed),
            Is.EqualTo("Replay mismatch: 2 of 3 replayed calls differ from the recording; first at turn 2 ReplaceSnippet (recorded IsError=True, replayed IsError=False)."));
    }

    [Test]
    public async Task ReplayKeptCallsAsync_CountEqualsDetailedMismatches_OnDoctoredInput()
    {
        // Recorded: success, rejection, success. The fake always replays success, so only turn 2 differs.
        var turns = new List<AgentTranscriptTurn>
        {
            Turn(1, "ReadFile", isError: false),
            Turn(2, "ReplaceSnippet", isError: true),
            Turn(3, "ReplaceSnippet", isError: false),
        };

        var detailed = await TranscriptResume.ReplayKeptCallsDetailedAsync(
            turns,
            (_, _, _) => Task.FromResult(("r", false)),
            CancellationToken.None);
        var counted = await TranscriptResume.ReplayKeptCallsAsync(
            turns,
            (_, _, _) => Task.FromResult(("r", false)),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(detailed.Count(call => call.Mismatch), Is.EqualTo(1));
            Assert.That(counted, Is.EqualTo(detailed.Count(call => call.Mismatch)));
        });
    }

    // ---- results.csv row ----

    [Test]
    public void BuildRow_R9Shape_FirstCallRepeatsTheRejectedOne_NoRenameSymbol()
    {
        var args = """{"filePath":"a.cs","oldContent":"x"}""";
        var turns = new List<AgentTranscriptTurn>
        {
            Turn(1, "ReadFile", isError: false),
            Turn(2, "ReplaceSnippet", isError: true, argumentsJson: args, resultJson: Rejection),
            Turn(3, "ReplaceSnippet", isError: true, argumentsJson: args, resultJson: Rejection),
            // Same arguments, different key order and spacing: still the identical call.
            Turn(4, "ReplaceSnippet", isError: true, argumentsJson: """{ "oldContent": "x", "filePath": "a.cs" }""", resultJson: Rejection),
            Turn(5, "ReadFile", isError: false),
            Turn(6, "ReplaceSnippet", isError: false),
            Turn(7, "RenameSymbol", isError: false),
        };

        var row = TranscriptResume.BuildRow(
            1, new LoopAssistOptions(true, "assistant"), keepTurns: 3, continueTurns: 3, turns, assistCount: 2, "TurnCapExceeded");

        Assert.Multiple(() =>
        {
            Assert.That(row.FirstNewToolName, Is.EqualTo("ReplaceSnippet"));
            Assert.That(row.FirstCallIdenticalToRejected, Is.True);
            Assert.That(row.ReachedRenameSymbolWithinTurns, Is.False, "RenameSymbol came at turn 7, beyond the 3 continue turns");
            Assert.That(row.NewCalls, Is.EqualTo("ReplaceSnippet(rejected);ReadFile(ok);ReplaceSnippet(ok)"));
            Assert.That(row.ToCsvLine(), Is.EqualTo(
                "1,on,assistant,ReplaceSnippet,true,false,2,TurnCapExceeded,ReplaceSnippet(rejected);ReadFile(ok);ReplaceSnippet(ok),"));
        });
    }

    [Test]
    public void BuildRow_FirstNewCallIsRenameSymbol_LoopAssistOff()
    {
        var turns = new List<AgentTranscriptTurn>
        {
            Turn(1, "ReadFile", isError: false),
            Turn(2, "ReplaceSnippet", isError: true, argumentsJson: """{"a":1}""", resultJson: Rejection),
            Turn(3, "RenameSymbol", isError: false),
        };

        var row = TranscriptResume.BuildRow(
            4, LoopAssistOptions.Off, keepTurns: 2, continueTurns: 3, turns, assistCount: 0, "ModelFinished");

        Assert.Multiple(() =>
        {
            Assert.That(row.FirstNewToolName, Is.EqualTo("RenameSymbol"));
            Assert.That(row.FirstCallIdenticalToRejected, Is.False);
            Assert.That(row.ReachedRenameSymbolWithinTurns, Is.True);
            Assert.That(row.Role, Is.EqualTo("n/a"));
            Assert.That(row.ToCsvLine(), Is.EqualTo("4,off,n/a,RenameSymbol,false,true,0,ModelFinished,RenameSymbol(ok),"));
        });
    }

    [Test]
    public void BuildRow_NoNewToolCall_HasAnEmptyFirstTool()
    {
        var turns = new List<AgentTranscriptTurn> { Turn(1, "ReadFile", isError: false) };

        var row = TranscriptResume.BuildRow(2, LoopAssistOptions.Off, 1, 3, turns, 0, "ModelFinished");

        Assert.Multiple(() =>
        {
            Assert.That(row.FirstNewToolName, Is.Empty);
            Assert.That(row.FirstCallIdenticalToRejected, Is.False);
            Assert.That(row.NewCalls, Is.Empty);
        });
    }

    [Test]
    public void ToCsvLine_QuotesFieldsWithCommasQuotesOrLineBreaks()
    {
        var row = TranscriptResume.BuildHarnessErrorRow(3, new LoopAssistOptions(true, "user"), "boom, \"quoted\"\r\nsecond line");

        Assert.Multiple(() =>
        {
            Assert.That(row.IsHarnessError, Is.True);
            Assert.That(row.StopReason, Is.EqualTo("HarnessError"));
            Assert.That(row.ToCsvLine(), Is.EqualTo("3,on,user,,false,false,0,HarnessError,,\"boom, \"\"quoted\"\" second line\""));
        });
    }

    [Test]
    public void AppendRow_WritesTheHeaderOnce_ThenOneLinePerSample()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rs-resume-" + Guid.NewGuid().ToString("N")[..8]);
        var csv = Path.Combine(folder, "nested", "results.csv");
        try
        {
            TranscriptResume.AppendRow(csv, TranscriptResume.BuildHarnessErrorRow(1, LoopAssistOptions.Off, "e1"));
            TranscriptResume.AppendRow(csv, TranscriptResume.BuildHarnessErrorRow(2, LoopAssistOptions.Off, "e2"));

            var lines = File.ReadAllLines(csv);
            Assert.Multiple(() =>
            {
                Assert.That(lines, Has.Length.EqualTo(3));
                Assert.That(lines[0], Is.EqualTo(ResumeSampleRow.CsvHeader));
                Assert.That(lines[0], Does.StartWith("sampleIndex,loopAssist,role,firstNewToolName,firstCallIdenticalToRejected,reachedRenameSymbolWithinTurns,assistCount"));
                Assert.That(lines[1], Does.StartWith("1,off,n/a,"));
                Assert.That(lines[2], Does.StartWith("2,off,n/a,"));
            });
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // ---- helpers ----

    private static Task<int> TranscriptReplayCountingAsync(
        IReadOnlyList<AgentTranscriptTurn> turns, List<string> calls, List<string> log, Func<string, bool> replayedIsError) =>
        TranscriptResume.ReplayKeptCallsAsync(
            turns,
            (tool, arguments, _) =>
            {
                calls.Add($"{tool}:{arguments}");
                return Task.FromResult(("{}", replayedIsError(tool)));
            },
            CancellationToken.None,
            log.Add);

    /// <summary>A turn with one call; call id "call_N", arguments "args_N" unless given.</summary>
    private static AgentTranscriptTurn Turn(
        int turnNumber, string tool, bool isError = false, string? argumentsJson = null, string resultJson = "{}")
    {
        var callId = $"call_{turnNumber}";
        var arguments = argumentsJson ?? $"args_{turnNumber}";
        return new AgentTranscriptTurn
        {
            TurnNumber = turnNumber,
            ModelLatency = TimeSpan.FromSeconds(1),
            StartedAt = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now,
            ModelMessage = new AgentChatMessage
            {
                Role = "assistant",
                Content = $"turn {turnNumber}",
                ToolCalls = [new AgentToolCall { Id = callId, Name = tool, ArgumentsJson = arguments }],
            },
            ToolCalls =
            [
                new AgentToolCallRecord
                {
                    ToolName = tool,
                    ArgumentsJson = arguments,
                    ResultJson = resultJson,
                    IsError = isError,
                    Latency = TimeSpan.FromMilliseconds(5),
                    StartedAt = DateTimeOffset.Now,
                    CompletedAt = DateTimeOffset.Now,
                    ToolCallId = callId,
                },
            ],
        };
    }
}

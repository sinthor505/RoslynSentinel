using System.Text.Json;
using System.Text.Json.Nodes;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Unit coverage for the RenameSymbol residualMentions summary: ResidualMentionSummarizer (counts, caps, excerpts,
/// detail hints, wire shape, size cap) and ResidualMentionDetailStore (store under a temp solution root). Pure
/// builders, no workspace. ResidualMentionDetailPagingTests proves the stored envelope pages; this fixture proves the
/// summary built from it.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ResidualMentionSummarizerTests
{
    private const string OldName = "OldName";
    private const string FakeResultId = "0123456789abcdef0123456789abcdef";

    private static readonly ResidualBucket[] AllBuckets =
    {
        ResidualBucket.Code,
        ResidualBucket.Comment,
        ResidualBucket.String,
        ResidualBucket.PathLike,
        ResidualBucket.NonSource,
    };

    // The marshaller's wire options (ToolResultMarshaller serializes with the SDK options; the tests use the same instance).
    private static readonly JsonSerializerOptions Wire = ModelContextProtocol.McpJsonUtilities.DefaultOptions;

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ResidualSummary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ResidualMention Mention(string relative, int line, string text, ResidualBucket bucket = ResidualBucket.Code, int column = 0) =>
        new(new FilePathWrapper(Path.Combine(_root, relative), _root), line, text, bucket, column);

    private static string Key(ResidualBucket bucket) => ResidualMentionSummarizer.BucketName(bucket);

    /// <summary>
    /// 1200 mentions over 30 files in scan order, buckets interleaved (index % 5). f00-f02 hold 100 each, f03-f11 hold
    /// 34 each, f12-f29 hold 33 each (300 + 306 + 594 = 1200). Top files therefore tie at 34 and the ordinal tie-break
    /// decides which seven of f03-f11 make the cut.
    /// </summary>
    private List<ResidualMention> VolumeMentions()
    {
        var mentions = new List<ResidualMention>();
        int bumped = 9;
        int index = 0;
        for (int f = 0; f < 30; f++)
        {
            int count = f < 3 ? 100 : (bumped-- > 0 ? 34 : 33);
            string relative = Path.Combine("src", $"f{f:D2}.cs");
            for (int k = 0; k < count; k++, index++)
            {
                string text = $"var value{index} = OldName.Call(argument);";
                mentions.Add(Mention(relative, index + 1, text, AllBuckets[index % AllBuckets.Length], text.IndexOf(OldName, StringComparison.Ordinal)));
            }
        }

        return mentions;
    }

    private static string LongLine(int matchColumn, int length)
    {
        string before = new string('a', matchColumn);
        string after = new string('b', length - matchColumn - OldName.Length);
        return before + OldName + after;
    }

    [Test]
    public void Summarize_VolumeCase_CountsCapsSortsAndExamplesAsSpecified()
    {
        var mentions = VolumeMentions();
        var view = ResidualMentionSummarizer.Summarize(mentions, OldName, FakeResultId);

        Assert.That(mentions, Has.Count.EqualTo(1200));
        Assert.That(view.Total, Is.EqualTo(1200));

        // ByBucket: every bucket present here, counts match an independent count of the input.
        Assert.That(view.ByBucket.Keys, Is.EquivalentTo(new[] { "code", "comment", "string", "pathLike", "nonSource" }));
        foreach (var bucket in AllBuckets)
        {
            Assert.That(view.ByBucket[Key(bucket)], Is.EqualTo(mentions.Count(m => m.Bucket == bucket)), Key(bucket));
        }

        // TopFiles: capped at 10, count desc, ties by ordinal path (f00-f02 at 100, then f03-f09 at 34).
        Assert.That(view.TopFiles, Has.Count.EqualTo(ResidualMentionSummarizer.MaxTopFiles));
        Assert.That(view.TopFiles.Select(f => f.Path).ToList(), Is.EqualTo(new[]
        {
            Path.Combine("src", "f00.cs"), Path.Combine("src", "f01.cs"), Path.Combine("src", "f02.cs"),
            Path.Combine("src", "f03.cs"), Path.Combine("src", "f04.cs"), Path.Combine("src", "f05.cs"),
            Path.Combine("src", "f06.cs"), Path.Combine("src", "f07.cs"), Path.Combine("src", "f08.cs"),
            Path.Combine("src", "f09.cs"),
        }));
        Assert.That(view.TopFiles.Select(f => f.Count).ToList(), Is.EqualTo(new[] { 100, 100, 100, 34, 34, 34, 34, 34, 34, 34 }));

        // Examples: at most MaxExamplesPerBucket per bucket, in scan order (first N of the bucket in list order).
        Assert.That(view.Examples.Keys, Is.EquivalentTo(new[] { "code", "comment", "string", "pathLike", "nonSource" }));
        foreach (var bucket in AllBuckets)
        {
            var expected = mentions.Where(m => m.Bucket == bucket)
                .Take(ResidualMentionSummarizer.MaxExamplesPerBucket)
                .Select(m => (m.FilePath.Relative, m.LineNumber))
                .ToList();
            var actual = view.Examples[Key(bucket)].Select(e => (e.Path, e.Line)).ToList();
            Assert.That(actual, Is.EqualTo(expected), Key(bucket));
        }

        // Details: one entry per non-empty bucket when a detail id is given.
        Assert.That(view.Details, Is.Not.Null);
        Assert.That(view.Details!.Keys, Is.EquivalentTo(new[] { "code", "comment", "string", "pathLike", "nonSource" }));

        var noDetails = ResidualMentionSummarizer.Summarize(mentions, OldName, null);
        Assert.That(noDetails.Details, Is.Null);
    }

    [Test]
    public void Summarize_OnlyCodeAndComment_OmitsEmptyBucketsEverywhere()
    {
        var mentions = new List<ResidualMention>
        {
            Mention("a.cs", 1, "var x = OldName;", ResidualBucket.Code, 8),
            Mention("a.cs", 2, "// OldName", ResidualBucket.Comment, 3),
            Mention("a.cs", 3, "var y = OldName;", ResidualBucket.Code, 8),
        };

        var view = ResidualMentionSummarizer.Summarize(mentions, OldName, FakeResultId);

        Assert.That(view.ByBucket.Keys, Is.EquivalentTo(new[] { "code", "comment" }));
        Assert.That(view.ByBucket["code"], Is.EqualTo(2));
        Assert.That(view.ByBucket["comment"], Is.EqualTo(1));
        Assert.That(view.Examples.Keys, Is.EquivalentTo(new[] { "code", "comment" }));
        Assert.That(view.Details!.Keys, Is.EquivalentTo(new[] { "code", "comment" }));
    }

    [Test]
    public void Summarize_DetailHints_AreExactlyFetchCallForEachBucketPath()
    {
        var view = ResidualMentionSummarizer.Summarize(VolumeMentions(), OldName, FakeResultId);

        Assert.That(view.Details, Is.Not.Null);
        foreach (var bucket in AllBuckets)
        {
            string hint = view.Details![Key(bucket)];
            Assert.That(hint, Is.EqualTo(LargeResultHints.FetchCall(FakeResultId, path: ResidualMentionSummarizer.DetailPath(bucket))), Key(bucket));
            Assert.That(hint, Does.Contain(FakeResultId));
        }
    }

    [Test]
    public void Summarize_PathsAreRepoRelative_AndNeverCarryTheRootPrefix()
    {
        var mentions = new List<ResidualMention>
        {
            Mention(Path.Combine("src", "Engine", "Renamer.cs"), 10, "OldName();", ResidualBucket.Code, 0),
            Mention(Path.Combine("src", "Engine", "Renamer.cs"), 12, "// OldName", ResidualBucket.Comment, 3),
            Mention(Path.Combine("docs", "readme.md"), 3, "OldName docs", ResidualBucket.NonSource, 0),
        };

        var view = ResidualMentionSummarizer.Summarize(mentions, OldName, null);

        var renamerRelative = new FilePathWrapper(Path.Combine(_root, "src", "Engine", "Renamer.cs"), _root).Relative;
        var readmeRelative = new FilePathWrapper(Path.Combine(_root, "docs", "readme.md"), _root).Relative;
        Assert.That(renamerRelative, Is.EqualTo(Path.Combine("src", "Engine", "Renamer.cs")));

        var paths = view.TopFiles.Select(f => f.Path)
            .Concat(view.Examples.Values.SelectMany(rows => rows.Select(r => r.Path)))
            .ToList();
        Assert.That(paths, Is.Not.Empty);
        foreach (var path in paths)
        {
            Assert.That(path, Does.Not.StartWith(_root));
            Assert.That(Path.IsPathRooted(path), Is.False, path);
        }

        Assert.That(view.TopFiles.Select(f => f.Path), Does.Contain(renamerRelative));
        Assert.That(view.TopFiles.Select(f => f.Path), Does.Contain(readmeRelative));
        Assert.That(view.Examples[Key(ResidualBucket.Code)].Single().Path, Is.EqualTo(renamerRelative));
    }

    [Test]
    public void Excerpt_LineOfExactlyMaxChars_ReturnedWhole()
    {
        string line = LongLine(10, ResidualMentionSummarizer.ExcerptChars);
        var mention = Mention("a.cs", 1, line, ResidualBucket.Code, 10);

        Assert.That(ResidualMentionSummarizer.Excerpt(mention, OldName), Is.EqualTo(line));
    }

    [Test]
    public void Excerpt_ShortLine_ReturnedWhole()
    {
        var mention = Mention("a.cs", 1, "var x = OldName;", ResidualBucket.Code, 8);

        Assert.That(ResidualMentionSummarizer.Excerpt(mention, OldName), Is.EqualTo("var x = OldName;"));
    }

    [Test]
    public void Excerpt_LongLine_MatchInMiddle_IsCentredBoundedAndAsciiEllipsed()
    {
        string line = LongLine(200, 400);
        var mention = Mention("a.cs", 1, line, ResidualBucket.Code, 200);

        string excerpt = ResidualMentionSummarizer.Excerpt(mention, OldName);

        Assert.That(excerpt.Length, Is.LessThanOrEqualTo(110));
        Assert.That(excerpt, Does.Contain(OldName));
        Assert.That(excerpt, Does.StartWith("...").And.EndWith("..."));
        Assert.That(excerpt.All(c => c < 128), Is.True, "excerpt must be ASCII only");

        string inner = excerpt[3..^3];
        Assert.That(inner.Length, Is.EqualTo(ResidualMentionSummarizer.ExcerptChars));
        // Centred: the match begins near the middle of the 100-char window, not at either edge.
        Assert.That(inner.IndexOf(OldName, StringComparison.Ordinal), Is.InRange(30, 70));
    }

    [Test]
    public void Excerpt_MatchAtColumnZero_CutsOnlyTheTrailingSide()
    {
        string line = LongLine(0, 400);
        var mention = Mention("a.cs", 1, line, ResidualBucket.Code, 0);

        string excerpt = ResidualMentionSummarizer.Excerpt(mention, OldName);

        Assert.That(excerpt, Does.StartWith(OldName));
        Assert.That(excerpt, Does.EndWith("..."));
        Assert.That(excerpt.Length, Is.EqualTo(ResidualMentionSummarizer.ExcerptChars + 3));
    }

    [Test]
    public void Excerpt_UnknownColumn_LocatesFirstOccurrenceOfName()
    {
        string line = LongLine(250, 400);
        var mention = Mention("a.cs", 1, line, ResidualBucket.Code, 0);

        string excerpt = ResidualMentionSummarizer.Excerpt(mention, OldName);

        Assert.That(excerpt, Does.Contain(OldName));
        Assert.That(excerpt, Does.StartWith("...").And.EndWith("..."));
        string inner = excerpt[3..^3];
        Assert.That(inner.IndexOf(OldName, StringComparison.Ordinal), Is.InRange(30, 70));
    }

    [Test]
    public void Excerpt_MatchNearEnd_CutsOnlyTheLeadingSide()
    {
        string line = LongLine(390, 400);
        var mention = Mention("a.cs", 1, line, ResidualBucket.Code, 390);

        string excerpt = ResidualMentionSummarizer.Excerpt(mention, OldName);

        Assert.That(excerpt, Does.StartWith("..."));
        Assert.That(excerpt, Does.Not.EndWith("..."));
        Assert.That(excerpt, Does.Contain(OldName));
        Assert.That(excerpt.Length, Is.EqualTo(ResidualMentionSummarizer.ExcerptChars + 3));
    }

    [Test]
    public void Summarize_ZeroMentions_ReturnsTotalZeroAndEmptyCollections()
    {
        // Observed behaviour: no special case for zero. Total 0, empty maps and lists, Details null without an id.
        var view = ResidualMentionSummarizer.Summarize(new List<ResidualMention>(), OldName, null);

        Assert.That(view.Total, Is.EqualTo(0));
        Assert.That(view.ByBucket, Is.Empty);
        Assert.That(view.TopFiles, Is.Empty);
        Assert.That(view.Examples, Is.Empty);
        Assert.That(view.Details, Is.Null);
    }

    [Test]
    public void Summarize_ZeroMentionsWithDetailId_ReturnsEmptyDetails()
    {
        var view = ResidualMentionSummarizer.Summarize(new List<ResidualMention>(), OldName, FakeResultId);

        Assert.That(view.Total, Is.EqualTo(0));
        Assert.That(view.Details, Is.Not.Null);
        Assert.That(view.Details!, Is.Empty);
    }

    [Test]
    public void Note_WordingDistinguishesDetailsStoredTrueFromFalse()
    {
        string stored = ResidualMentionSummarizer.Note(OldName, 1200, detailsStored: true);
        string notStored = ResidualMentionSummarizer.Note(OldName, 1200, detailsStored: false);

        Assert.That(stored, Does.Contain("1200"));
        Assert.That(stored, Does.Contain("residualMentions.details"));
        Assert.That(stored, Does.Not.Contain("could not be stored"));

        Assert.That(notStored, Does.Contain("1200"));
        Assert.That(notStored, Does.Contain("could not be stored"));
        Assert.That(notStored, Does.Not.Contain("residualMentions.details"));
    }

    [Test]
    public void Serialize_WireOptions_UseCamelCaseKeysAndNullDetails()
    {
        var mentions = new List<ResidualMention>
        {
            Mention("a.cs", 3, "var x = OldName;", ResidualBucket.Code, 8),
            Mention("c.md", 1, "OldName docs", ResidualBucket.NonSource, 0),
        };

        var json = JsonSerializer.Serialize(ResidualMentionSummarizer.Summarize(mentions, OldName, null), Wire);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.That(root["total"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(root.ContainsKey("Total"), Is.False, "PascalCase key leaked onto the wire");
        Assert.That(root["byBucket"]!.AsObject().Select(p => p.Key), Is.EquivalentTo(new[] { "code", "nonSource" }));
        Assert.That(root["topFiles"]!.AsArray(), Has.Count.EqualTo(2));
        Assert.That(root["topFiles"]![0]!["path"], Is.Not.Null);
        Assert.That(root["topFiles"]![0]!["count"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(root["examples"]!.AsObject().Select(p => p.Key), Is.EquivalentTo(new[] { "code", "nonSource" }));
        Assert.That(root["examples"]!["code"]![0]!["line"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(root["examples"]!["code"]![0]!["excerpt"], Is.Not.Null);

        // details null: the property is either omitted or written as null; both read as null here.
        Assert.That(root["details"], Is.Null);
    }

    [Test]
    public void Serialize_WithDetailId_WritesCamelCaseBucketKeysUnderDetails()
    {
        var mentions = new List<ResidualMention>
        {
            Mention("a.cs", 3, "var x = OldName;", ResidualBucket.Code, 8),
            Mention("c.md", 1, "OldName docs", ResidualBucket.NonSource, 0),
        };

        var json = JsonSerializer.Serialize(ResidualMentionSummarizer.Summarize(mentions, OldName, FakeResultId), Wire);
        var root = JsonNode.Parse(json)!.AsObject();

        var details = root["details"]!.AsObject();
        Assert.That(details.Select(p => p.Key), Is.EquivalentTo(new[] { "code", "nonSource" }));
        Assert.That(details["nonSource"]!.GetValue<string>(), Does.Contain(FakeResultId));
    }

    [Test]
    public void Serialize_VolumeCase_StaysUnderEightThousandChars()
    {
        var json = JsonSerializer.Serialize(ResidualMentionSummarizer.Summarize(VolumeMentions(), OldName, FakeResultId), Wire);

        TestContext.Out.WriteLine($"residualMentions wire length for 1200 mentions: {json.Length}");
        Assert.That(json.Length, Is.LessThan(8000));
    }

    [Test]
    public async Task StoreAsync_NullOrEmptySolutionRoot_ReturnsNullWithoutThrowing()
    {
        var mentions = new List<ResidualMention> { Mention("a.cs", 1, "OldName", ResidualBucket.Code, 0) };

        Assert.That(await ResidualMentionDetailStore.StoreAsync(mentions, null, CancellationToken.None), Is.Null);
        Assert.That(await ResidualMentionDetailStore.StoreAsync(mentions, string.Empty, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task StoreAsync_TempSolutionRoot_WritesLargeResultFileAndReturnsId()
    {
        var mentions = new List<ResidualMention>
        {
            Mention("a.cs", 3, "var x = OldName;", ResidualBucket.Code, 8),
            Mention("c.md", 1, "OldName docs", ResidualBucket.NonSource, 0),
        };

        string? resultId = await ResidualMentionDetailStore.StoreAsync(mentions, _root, CancellationToken.None);

        Assert.That(resultId, Is.Not.Null.And.Not.Empty);
        string dir = Path.Combine(_root, ".roslynsentinel", "largeresults");
        var files = Directory.GetFiles(dir, $"largeresult_*_{resultId}.json");
        Assert.That(files, Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(files[0]), Does.Contain("successData"));
    }
}

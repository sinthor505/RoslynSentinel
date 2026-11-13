using System.Globalization;
using NUnit.Framework;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class VariantSampleRowTests
{
    private static readonly string[] ExpectedColumns =
    {
        "RunStamp", "ModelKey", "Temperature", "TopP", "MaxTokens", "ContinueTurns",
        "VariantId", "VariantHash", "ErrorCode",
        "SourceRunFolder", "SourceSample", "SourceCommitHash", "CutTurn", "RecordedMessageHash",
        "SampleIndex", "DecisionClass", "FirstTool", "CallCount", "BlockedAnywhere", "StopTurn", "StopReason",
        "LatencyMs", "InputTokens", "OutputTokens", "HarnessError", "SourceSettings",
    };

    [Test]
    public void Header_ListsAll26ColumnsInOrder()
    {
        Assert.That(VariantSampleRow.Header.Split(','), Is.EqualTo(ExpectedColumns));
    }

    [Test]
    public void ToCsvLine_ThenParse_RoundTripsAFullRow()
    {
        var row = BuildFullRow();

        var parsed = VariantSampleRow.Parse(row.ToCsvLine());

        Assert.That(parsed, Is.EqualTo(row));
    }

    [Test]
    public void ToCsvLine_ThenParse_RoundTripsNullsAndEmptyStringsAsEmptyCells()
    {
        var row = BuildFullRow() with
        {
            StopTurn = null,
            InputTokens = null,
            OutputTokens = null,
            FirstTool = string.Empty,
            HarnessError = string.Empty,
        };
        var line = row.ToCsvLine();

        // HarnessError is empty and SourceSettings (the last column) is "matched", so the empty cell sits just before it.
        Assert.That(line, Does.EndWith(",,matched"));
        var parsed = VariantSampleRow.Parse(line);
        Assert.That(parsed.StopTurn, Is.Null);
        Assert.That(parsed.InputTokens, Is.Null);
        Assert.That(parsed.OutputTokens, Is.Null);
        Assert.That(parsed, Is.EqualTo(row));
    }

    [Test]
    public void ToCsvLine_ThenParse_QuotesCommasAndDoublesEmbeddedQuotes()
    {
        var row = BuildFullRow() with
        {
            StopReason = "budget, reached",
            RecordedMessageHash = "say \"hi\"",
        };
        var line = row.ToCsvLine();

        Assert.That(line, Does.Contain("\"budget, reached\""));
        Assert.That(line, Does.Contain("\"say \"\"hi\"\"\""));
        Assert.That(VariantSampleRow.Parse(line), Is.EqualTo(row));
    }

    [Test]
    public void ToCsvLine_FlattensLineBreaksInHarnessErrorToOneSpace()
    {
        var row = BuildFullRow() with { HarnessError = "first line\r\nsecond line" };

        var line = row.ToCsvLine();

        Assert.That(line.IndexOfAny(new[] { '\r', '\n' }), Is.EqualTo(-1));
        var parsed = VariantSampleRow.Parse(line);
        Assert.That(parsed.HarnessError, Is.EqualTo("first line second line"));
        Assert.That(parsed, Is.EqualTo(row with { HarnessError = "first line second line" }));
    }

    [Test]
    public void Parse_RejectsWrongCellCount()
    {
        Assert.That(
            () => VariantSampleRow.Parse("a,b,c"),
            Throws.TypeOf<FormatException>().With.Message.Contains("3 cells"));
    }

    [Test]
    public void Parse_RejectsBadNumber()
    {
        var line = (BuildFullRow() with { MaxTokens = 7777 }).ToCsvLine();
        var corrupted = line.Replace(",7777,", ",many,");
        Assert.That(corrupted, Is.Not.EqualTo(line));

        Assert.That(
            () => VariantSampleRow.Parse(corrupted),
            Throws.TypeOf<FormatException>().With.Message.Contains("MaxTokens"));
    }

    [Test]
    public void Append_WritesHeaderOnceThenRows()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "variants.csv");
        try
        {
            var first = BuildFullRow();
            var second = BuildFullRow() with { SampleIndex = 2, FirstTool = "Build" };

            VariantSampleRow.Append(path, first);
            VariantSampleRow.Append(path, second);

            var lines = File.ReadAllLines(path);
            Assert.That(lines, Has.Length.EqualTo(3));
            Assert.That(lines[0], Is.EqualTo(VariantSampleRow.Header));
            Assert.That(VariantSampleRow.Parse(lines[1]), Is.EqualTo(first));
            Assert.That(VariantSampleRow.Parse(lines[2]), Is.EqualTo(second));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Test]
    public void ToCsvLine_UsesInvariantCulture_WhateverTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var row = BuildFullRow() with { Temperature = "0.7", LatencyMs = 1234567 };

            var line = row.ToCsvLine();

            Assert.That(line, Does.Contain(",0.7,"));
            Assert.That(line, Does.Contain(",1234567,"));
            Assert.That(VariantSampleRow.Parse(line).Temperature, Is.EqualTo("0.7"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static VariantSampleRow BuildFullRow() => new()
    {
        RunStamp = "2026-11-13_0900",
        ModelKey = "model-a",
        Temperature = "0.2",
        TopP = "default",
        MaxTokens = 4096,
        ContinueTurns = 3,
        VariantId = "armF",
        VariantHash = "0123456789ab",
        ErrorCode = "FileNotFound",
        SourceRunFolder = @"runs\2026-11-02_1200",
        SourceSample = "sample-01",
        SourceCommitHash = "abc1234",
        CutTurn = 2,
        RecordedMessageHash = "fedcba987654",
        SampleIndex = 1,
        DecisionClass = "Stopped",
        FirstTool = "ReadFile",
        CallCount = 4,
        BlockedAnywhere = false,
        StopTurn = 5,
        StopReason = "TargetReached",
        LatencyMs = 1234,
        InputTokens = 5000,
        OutputTokens = 300,
        HarnessError = string.Empty,
        SourceSettings = "matched",
    };
}

using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the near-miss record channel (Step 11 of plan_server_slip_recovery): <see cref="NearMissRecorder"/> records a
/// <see cref="NearMissSubstitution"/> during a tool call and <see cref="NearMissRecordFilter"/> writes the records into
/// <c>result._meta.nearMissSubstitutions</c>, for a success and an error result alike, and only when an evaluation option is on.
/// </summary>
[TestFixture]
public class NearMissRecordTests
{
    private static NearMissSubstitution Substitution(bool applied = true) => new(
        Tool: "ReplaceSnippet",
        FilePath: "ContosoOrders.Core/OrderCheckout.cs",
        Line: 5,
        From: "Oovernight",
        To: "ShippingKinds.Overnight",
        DiagnosticId: "CS0117",
        CandidateCount: 1,
        DisplayMode: "hide",
        Applied: applied);

    private static CallToolResult TextResult(bool isError = false) => new()
    {
        Content = [new TextContentBlock { Text = "{\"isError\":" + (isError ? "true" : "false") + "}" }],
        IsError = isError,
    };

    private static ValueTask<CallToolResult> RecordingCall(CallToolResult result)
    {
        NearMissRecorder.Record(Substitution());
        return ValueTask.FromResult(result);
    }

    [Test]
    public async Task Enabled_SuccessResult_CarriesTheRecordInMetaAndNotInTheContentAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);
        var result = TextResult();

        var returned = await NearMissRecordFilter.InvokeAsync(null, () => RecordingCall(result));

        Assert.That(returned, Is.SameAs(result), "the filter must annotate the result in place");
        var records = returned.Meta?[NearMissRecorder.MetaKey] as JsonArray;
        Assert.That(records, Is.Not.Null, "the record must be in _meta.nearMissSubstitutions");
        Assert.That(records!, Has.Count.EqualTo(1));
        var record = records![0]!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That((string?)record["tool"], Is.EqualTo("ReplaceSnippet"));
            Assert.That((string?)record["filePath"], Is.EqualTo("ContosoOrders.Core/OrderCheckout.cs"));
            Assert.That((int?)record["line"], Is.EqualTo(5));
            Assert.That((string?)record["from"], Is.EqualTo("Oovernight"));
            Assert.That((string?)record["to"], Is.EqualTo("ShippingKinds.Overnight"));
            Assert.That((string?)record["diagnosticId"], Is.EqualTo("CS0117"));
            Assert.That((int?)record["candidateCount"], Is.EqualTo(1));
            Assert.That((string?)record["displayMode"], Is.EqualTo("hide"));
            Assert.That((bool?)record["applied"], Is.True);
            Assert.That(((TextContentBlock)returned.Content.Single()).Text, Does.Not.Contain("Oovernight"), "the wrong name must never be in the model-facing content");
        });
    }

    [Test]
    public async Task Enabled_ErrorResult_CarriesTheRecordAndKeepsOtherMetaAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Hide);
        var result = TextResult(isError: true);
        result.Meta = new JsonObject { ["other"] = "kept" };

        var returned = await NearMissRecordFilter.InvokeAsync(null, () => RecordingCall(result));

        Assert.Multiple(() =>
        {
            Assert.That(returned.IsError, Is.True);
            Assert.That((returned.Meta?[NearMissRecorder.MetaKey] as JsonArray)?.Count, Is.EqualTo(1), "an error result carries the record too");
            Assert.That((string?)returned.Meta?["other"], Is.EqualTo("kept"), "other _meta entries must survive");
        });
    }

    [Test]
    public async Task Enabled_TwoRecordsInOneCall_AreWrittenInOrderAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);

        var returned = await NearMissRecordFilter.InvokeAsync(null, () =>
        {
            NearMissRecorder.Record(Substitution(applied: true));
            NearMissRecorder.Record(Substitution(applied: false));
            return ValueTask.FromResult(TextResult());
        });

        var records = (JsonArray)returned.Meta![NearMissRecorder.MetaKey]!;
        Assert.That(records.Select(r => (bool?)r!["applied"]).ToList(), Is.EqualTo(new List<bool?> { true, false }));
    }

    [Test]
    public async Task Enabled_CallWithoutARecord_HasNoMetaAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);

        var returned = await NearMissRecordFilter.InvokeAsync(null, () => ValueTask.FromResult(TextResult()));

        Assert.That(returned.Meta, Is.Null);
    }

    [Test]
    public async Task DefaultOptions_NothingIsRecordedOrWrittenAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Show);
        Assert.That(NearMissRecorder.IsActive, Is.False);

        var returned = await NearMissRecordFilter.InvokeAsync(null, () => RecordingCall(TextResult()));

        Assert.That(returned.Meta, Is.Null, "with both options at their defaults the result must be untouched");
    }

    [Test]
    public void Record_OutsideAnyScope_DoesNotThrow()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);

        Assert.DoesNotThrow(() => NearMissRecorder.Record(Substitution()));
    }

    [Test]
    public void FilterRegistration_IsAbsentByDefault_AndSitsDirectlyInsideTelemetryWhenAnOptionIsOn()
    {
        using (NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Show))
        {
            Assert.That(RoslynSentinelServiceExtensionsBasic.CallToolFilterNames, Does.Not.Contain("NearMissRecord"));
        }

        using (NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show))
        {
            var names = RoslynSentinelServiceExtensionsBasic.CallToolFilterNames.ToList();
            Assert.That(names.IndexOf("NearMissRecord"), Is.EqualTo(names.IndexOf("ToolTelemetry") + 1));
        }
    }
}

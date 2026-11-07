// Pins the wire contract of the named Search relationship records against the anonymous objects they replaced,
// and that both lists of ReferencesSearchResult stay in the paged sequence.

using System.Text.Json;

using ModelContextProtocol;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class SearchRelationshipResultsWireTests
{
    private static readonly List<CallerInfo> Callers = [new("Run", "Svc", "src/Svc.cs", 12, "DoWork();")];

    // The second entry has null FilePath and Line: nulls must serialize the same way too.
    private static readonly List<ImplementationInfo> Implementations =
    [
        new("Impl", "src/Impl.cs", 4, "class"),
        new("Generated", null, null, "class"),
    ];

    private static readonly ListSummary Summary = SummarizeListResult.Build(new[] { "src/Svc.cs" }, p => p);

    private static string Wire(object value) => JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions);

    [Test]
    public void CallersSearchResult_SerializesLikeTheAnonymousShape()
    {
        object anonymous = new { callers = Callers, summary = Summary };
        Assert.That(Wire(new CallersSearchResult(Callers, Summary)), Is.EqualTo(Wire(anonymous)));
    }

    [Test]
    public void ImplementationsSearchResult_SerializesLikeTheAnonymousShape()
    {
        object anonymous = new { implementations = Implementations, summary = Summary };
        Assert.That(Wire(new ImplementationsSearchResult(Implementations, Summary)), Is.EqualTo(Wire(anonymous)));
    }

    [Test]
    public void ReferencesSearchResult_SerializesLikeTheAnonymousShape()
    {
        object anonymous = new { callers = Callers, implementations = Implementations, summary = Summary };
        Assert.That(Wire(new ReferencesSearchResult(Callers, Implementations, Summary)), Is.EqualTo(Wire(anonymous)));
    }

    [Test]
    public void ReferencesSearchResult_PagesBothLists()
    {
        object envelope = new { successData = new ReferencesSearchResult(Callers, Implementations, Summary) };
        var node = JsonSerializer.SerializeToNode(envelope, McpJsonUtilities.DefaultOptions)!.AsObject();

        SequenceDescriptor sequence = SequencePager.ResolveSequence(node, hints: null, typeof(ReferencesSearchResult), McpJsonUtilities.DefaultOptions);

        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.callers", "successData.implementations" }));
    }
}

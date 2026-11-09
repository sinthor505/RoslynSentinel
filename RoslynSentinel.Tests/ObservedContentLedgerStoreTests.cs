// Observed-content ledger store (read-ledger plan step 1): ContentFingerprint hashing and the
// per-agent PersistentWorkspaceManager store behind RecordObserved / TryGetObservedHash. The
// stale check that consumes the store is not covered here because it does not exist yet.

#pragma warning disable CS8618

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ObservedContentLedgerStoreTests
{
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private PersistentWorkspaceManager _manager;
    private FilePathWrapper _file;

    [SetUp]
    public void Setup()
    {
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _file = new FilePathWrapper(Path.Combine(Path.GetTempPath(), "observed-ledger-sample.cs"));
        ToolCallContext.Set(null);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _manager?.Dispose();
    }

    private static ToolCallContext AgentContext(string agentId) =>
        new(agentId, "test-agent", "TestTool", "observed-ledger test", 0);

    [Test]
    public void Compute_AbcVector_ReturnsKnownSha256()
    {
        Assert.That(ContentFingerprint.Compute("abc"), Is.EqualTo(AbcHash));
    }

    [Test]
    public void Compute_EmptyString_ReturnsKnownSha256OfEmptyInput()
    {
        Assert.That(ContentFingerprint.Compute(string.Empty), Is.EqualTo(EmptyHash));
    }

    [Test]
    public void Compute_NonAsciiText_MatchesSha256OfUtf8BytesAndIsLowercaseHex()
    {
        const string text = "héllo 世界";

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var actual = ContentFingerprint.Compute(text);

        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual, Has.Length.EqualTo(64));
        Assert.That(actual, Does.Match("^[0-9a-f]{64}$"));
    }

    [Test]
    public void RecordObserved_ThenTryGetObservedHash_ReturnsFingerprintOfRecordedText()
    {
        const string text = "public class Sample { }";

        _manager.RecordObserved(_file, text);

        Assert.That(_manager.TryGetObservedHash(_file, out var hash), Is.True);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute(text)));
    }

    [Test]
    public void RecordObserved_SecondCallSamePath_LastWriterWins()
    {
        _manager.RecordObserved(_file, "first version");
        _manager.RecordObserved(_file, "second version");

        Assert.That(_manager.TryGetObservedHash(_file, out var hash), Is.True);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute("second version")));
    }

    [Test]
    public void TryGetObservedHash_UnrecordedPath_ReturnsFalse()
    {
        Assert.That(_manager.TryGetObservedHash(_file, out _), Is.False);
    }

    [Test]
    public void TryGetObservedHash_AgentKeyIsolation_EachAgentAndNoContextHasOwnEntry()
    {
        const string text1 = "agent A content";
        const string text2 = "agent B content";
        const string text3 = "no-context content";

        // No context yet: nothing is recorded under the null key.
        Assert.That(_manager.TryGetObservedHash(_file, out _), Is.False);

        ToolCallContext.Set(AgentContext("A"));
        _manager.RecordObserved(_file, text1);

        ToolCallContext.Set(AgentContext("B"));
        Assert.That(_manager.TryGetObservedHash(_file, out _), Is.False,
            "Agent B must not see a hash recorded by agent A.");
        _manager.RecordObserved(_file, text2);

        ToolCallContext.Set(AgentContext("A"));
        Assert.That(_manager.TryGetObservedHash(_file, out var hashA), Is.True);
        Assert.That(hashA, Is.EqualTo(ContentFingerprint.Compute(text1)),
            "Agent A's entry must survive agent B's write.");

        ToolCallContext.Set(null);
        Assert.That(_manager.TryGetObservedHash(_file, out _), Is.False,
            "The null (no-context) key is separate from every agent key.");
        _manager.RecordObserved(_file, text3);
        Assert.That(_manager.TryGetObservedHash(_file, out var hashNull), Is.True);
        Assert.That(hashNull, Is.EqualTo(ContentFingerprint.Compute(text3)));
    }

    [Test]
    public void RecordObserved_ConcurrentWriters_NoThrowAndFinalHashIsOneOfInputs()
    {
        // Parallel.For workers run with no ambient context, so every write uses the null key.
        ToolCallContext.Set(null);
        string[] candidates = { "candidate one", "candidate two", "candidate three", "candidate four" };

        Assert.That(
            () => Parallel.For(0, 200, i => _manager.RecordObserved(_file, candidates[i % candidates.Length])),
            Throws.Nothing);

        Assert.That(_manager.TryGetObservedHash(_file, out var finalHash), Is.True);
        Assert.That(candidates.Select(ContentFingerprint.Compute), Does.Contain(finalHash));
    }
}

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Pre-image maps are keyed by path with an OrdinalIgnoreCase comparer, so a path that differs only in case
/// is the same file: one entry is read, and a lookup with the other casing hits.
/// </summary>
[TestFixture]
public class PreImageComparerTests
{
    [Test]
    public async Task ReadBeforeTextsAsync_PathsDifferingOnlyInCase_ReturnsOneEntryAndCaseInsensitiveLookupHits()
    {
        using var workspace = InMemoryWorkspace.Create(("Other.cs", "public class Other { }"));
        var paths = new[] { @"C:\tmp\Pre\A.cs", @"C:\tmp\pre\a.cs" };

        var texts = await ValidateAndApplyHelper.ReadBeforeTextsAsync(workspace.Manager, paths, CancellationToken.None);

        Assert.That(texts, Has.Count.EqualTo(1));
        Assert.That(texts.TryGetValue(@"C:\TMP\PRE\A.CS", out _), Is.True);
    }
}

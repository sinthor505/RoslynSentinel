using System.Collections.Generic;
using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class DocumentChangeSetTests
{
    private static readonly FilePathWrapper PathA = new FilePathWrapper(@"C:\tmp\a.cs");
    private static readonly FilePathWrapper PathB = new FilePathWrapper(@"C:\tmp\b.cs");

    [Test]
    public void Paths_ReturnsTheKeys()
    {
        var set = new DocumentChangeSet { [PathA] = "a", [PathB] = "b" };

        Assert.That(set.Paths, Is.EquivalentTo(new[] { PathA, PathB }));
    }

    [Test]
    public void IsEmpty_TrueWhenNew_FalseAfterAdd()
    {
        var set = new DocumentChangeSet();
        Assert.That(set.IsEmpty, Is.True);

        set[PathA] = "a";

        Assert.That(set.IsEmpty, Is.False);
    }

    [Test]
    public void Merge_AddsNewKeys_AndLaterWinsOnOverlap()
    {
        var set = new DocumentChangeSet { [PathA] = "old-a" };
        var other = new Dictionary<FilePathWrapper, string> { [PathA] = "new-a", [PathB] = "b" };

        set.Merge(other);

        Assert.That(set.Count, Is.EqualTo(2));
        Assert.That(set[PathA], Is.EqualTo("new-a"));
        Assert.That(set[PathB], Is.EqualTo("b"));
    }

    [Test]
    public void KeysDifferingOnlyInCase_AreOneKey()
    {
        var set = new DocumentChangeSet
        {
            [new FilePathWrapper(@"C:\tmp\A.cs")] = "upper",
            [new FilePathWrapper(@"C:\tmp\a.cs")] = "lower",
        };

        Assert.That(set.Count, Is.EqualTo(1));
        Assert.That(set[PathA], Is.EqualTo("lower"));
    }

    [Test]
    public void IDictionaryConstructor_CopiesEntries()
    {
        IDictionary<FilePathWrapper, string> source = new Dictionary<FilePathWrapper, string> { [PathA] = "a", [PathB] = "b" };

        var set = new DocumentChangeSet(source);

        Assert.That(set.Count, Is.EqualTo(2));
        Assert.That(set[PathA], Is.EqualTo("a"));
        Assert.That(set[PathB], Is.EqualTo("b"));
    }

    [Test]
    public void IEnumerableConstructor_CopiesEntries()
    {
        IEnumerable<KeyValuePair<FilePathWrapper, string>> source = new[]
        {
            new KeyValuePair<FilePathWrapper, string>(PathA, "a"),
            new KeyValuePair<FilePathWrapper, string>(PathB, "b"),
        };

        var set = new DocumentChangeSet(source);

        Assert.That(set.Count, Is.EqualTo(2));
        Assert.That(set[PathA], Is.EqualTo("a"));
        Assert.That(set[PathB], Is.EqualTo("b"));
    }

    [Test]
    public void Serialization_MatchesPlainDictionary()
    {
        var plain = new Dictionary<FilePathWrapper, string> { [PathA] = "a", [PathB] = "b" };
        var set = new DocumentChangeSet(plain);

        var expected = JsonSerializer.Serialize(plain, SharedJsonOptions.Compact);
        var actual = JsonSerializer.Serialize(set, SharedJsonOptions.Compact);

        Assert.That(actual, Is.EqualTo(expected));
    }
}

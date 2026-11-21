using System.Collections.Generic;

namespace RoslynSentinel.Common;

/// <summary>
/// A document path -> new full text map. Data, not an outcome: it carries no result or status.
/// Never use it as an [McpServerTool] parameter; wire types stay Dictionary&lt;string, string&gt;.
/// </summary>
public sealed class DocumentChangeSet : Dictionary<FilePathWrapper, string>
{
    public DocumentChangeSet()
    {
    }

    public DocumentChangeSet(IDictionary<FilePathWrapper, string> source)
        : base(source)
    {
    }

    public DocumentChangeSet(IEnumerable<KeyValuePair<FilePathWrapper, string>> source)
        : base(source)
    {
    }

    /// <summary>
    /// The document paths in this set (the keys).
    /// </summary>
    public IReadOnlyCollection<FilePathWrapper> Paths => Keys;

    /// <summary>
    /// True when the set holds no documents.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Adds every entry of <paramref name="other"/>; for a path already present, the value from
    /// <paramref name="other"/> wins.
    /// </summary>
    public void Merge(IReadOnlyDictionary<FilePathWrapper, string> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var pair in other)
        {
            this[pair.Key] = pair.Value;
        }
    }
}

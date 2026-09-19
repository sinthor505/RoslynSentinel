namespace RoslynSentinel.Common;

/// <summary>Return payload for <c>GetFileOutline</c>.</summary>
public record FileOutlineResult
{
    /// <summary>Scope/truncation metadata for the file. See <see cref="ReadEnvelope"/>.</summary>
    public ReadEnvelope Envelope { get; init; } = null!;
    /// <summary>The parsed structural outline.</summary>
    public List<OutlineItem> Symbols { get; init; } = new();
}

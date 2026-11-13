namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// One harvested error-message variant. Trailing is null when the ## trailing section is absent
/// (keep the recorded block), empty when the section is present but empty (remove the block),
/// otherwise the replacement text.
/// </summary>
public sealed record ErrorVariant(string Id, string ErrorCode, string Message, string? Trailing, string Hash);

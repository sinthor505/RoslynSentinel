namespace RoslynSentinel.Common;

/// <summary>
/// The location and real length of a snippet match in source text. Callers that
/// remove or replace a span must use <see cref="Length"/>, not contextSnippet.Length.
/// </summary>
public readonly record struct SnippetSpan(int Start, int Length);

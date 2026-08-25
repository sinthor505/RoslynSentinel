namespace RoslynSentinel.Common;

/// <summary>Server-side single-turn LLM completion, for tools that generate prose from code (e.g. BulkComment).</summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default);
}

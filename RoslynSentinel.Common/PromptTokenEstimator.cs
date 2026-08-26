namespace RoslynSentinel.Common;

/// <summary>
/// Estimates prompt token counts ahead of an LLM call so oversized requests can be rejected
/// before making a network round-trip. No real tokenizer is wired up per-model yet; every model
/// name falls back to a chars/4 heuristic, which is deliberately conservative (English BPE
/// tokenizers average ~4 chars/token, so this slightly over-counts token-dense text like code).
/// </summary>
public static class PromptTokenEstimator
{
    /// <summary>Estimates the token count for <paramref name="text"/> under <paramref name="modelName"/>'s tokenizer.</summary>
    public static int EstimateTokens(string modelName, string text)
    {
        return EstimateByCharCount(text);
    }

    private static int EstimateByCharCount(string text)
    {
        return (text.Length + 3) / 4;
    }
}

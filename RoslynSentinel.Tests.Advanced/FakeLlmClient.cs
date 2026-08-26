namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Test double for <see cref="ILlmClient"/> that returns a fixed summary without calling any real
/// LLM endpoint. Optional <see cref="Delay"/> gives cancellation tests a controllable window during
/// which <c>BulkComment</c>'s work phase is guaranteed to still be in flight.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public int CallCount { get; private set; }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return "Fake summary generated for testing.";
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Shared DI registration for the ModelEval fixtures' <see cref="LmStudioAgentClient"/>.
/// </summary>
public static class ModelEvalServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LmStudioAgentClient"/> as a typed <see cref="HttpClient"/> client whose
    /// <c>model</c> constructor argument is <see cref="LlmOptions.Model"/>, read when the client is
    /// resolved. A plain <c>AddHttpClient&lt;LmStudioAgentClient&gt;</c> cannot satisfy that bare
    /// <see cref="string"/> parameter, so every fixture registers the client through this method.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="configureClient">Configures the <see cref="HttpClient"/> (BaseAddress, Timeout).</param>
    public static IHttpClientBuilder AddLmStudioAgentClient(
        this IServiceCollection services,
        Action<HttpClient> configureClient)
    {
        return services
            .AddHttpClient<LmStudioAgentClient>(configureClient)
            .AddTypedClient((http, sp) => new LmStudioAgentClient(
                http,
                LlmOptions.Model!,
                sp.GetRequiredService<ILogger<LmStudioAgentClient>>()));
    }
}

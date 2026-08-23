// Program.cs v1
namespace RoslynSentinel.Server.Advanced;

public class Program
{
    public static async Task Main(string[] args)
    {
        var transport = ServerStartupHelpers.ParseTransport(args);

        switch (transport.ToLowerInvariant())
        {
            case "http":
                await ServerHttp.Startup(args).ConfigureAwait(false);
                break;
            case "stdio":
                await ServerStdio.Startup(args).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Unknown --transport value '{transport}'. Expected 'stdio' or 'http'.");
        }
    }
}

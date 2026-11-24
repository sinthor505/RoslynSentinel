// Golden-file helper for the tool-surface parity tests.
// ListToolsAsync serves a registration through an in-process MCP host and returns the protocol tools exactly as a client
// sees them; AssertMatchesGolden pins that list against files that sit beside the calling test's source file.
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;

// Same namespace as ModelEvalToolSurfaces (also in this project), so the ModelEval project needs no extra using.
namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Builds the tool surface of a registration in-process and compares it with committed golden files.
/// Golden layout, per calling test folder: <c>ToolSurfaceGolden/&lt;case&gt;.txt</c> (one line per tool,
/// <c>name TAB sha256hex(canonical JSON)</c>, served order, LF line ends, final newline) and one
/// <c>ToolSurfaceGolden/_catalog.json</c> mapping each hash to its canonical tool JSON. Goldens are never copied to the
/// output directory: they are read from, and in update mode written to, the source tree that
/// <see cref="CallerFilePathAttribute"/> names. The catalog holds only hashes some case file in its folder references:
/// each update-mode write adds the case's tools and prunes every entry no case file references any more.
/// </summary>
public static class ToolSurfaceGolden
{
    /// <summary>
    /// Environment variable; when it equals "1", <see cref="AssertMatchesGolden"/> writes the golden files and then fails the test,
    /// so a run with the variable left set can never come out green. A fixture that clears <c>ROSLYNSENTINEL_*</c> variables in its
    /// SetUp must not clear this one, or update mode could not be switched on.
    /// </summary>
    public const string UpdateEnvironmentVariable = "ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN";

    /// <summary>Folder name, beside the calling test's source file, that holds the golden files.</summary>
    public const string GoldenFolderName = "ToolSurfaceGolden";

    private const string CatalogFileName = "_catalog.json";
    private const int ClipLength = 600;

    private static readonly JsonSerializerOptions CatalogOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.Default, // escapes non-ASCII, so the catalog stays ASCII
    };

    /// <summary>Stand-in for the LM Studio client: CommentingEngine needs an ILlmClient and the real registration lives in the Server host.</summary>
    private sealed class StubLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default) =>
            Task.FromResult("stub");
    }

    /// <summary>
    /// Starts an in-process MCP host (engines Advanced, stream transport over two pipes, ValidateOnBuild like the stdio and HTTP
    /// servers, a stub <see cref="ILlmClient"/> registered first), lets <paramref name="register"/> add the tool registration,
    /// lists every page of tools through an <see cref="McpClient"/>, disposes everything and returns the protocol tools in the
    /// order the server served them.
    /// </summary>
    /// <param name="register">Adds the tools, e.g. <c>(b, s) =&gt; b.AddRoslynSentinelToolsAdvanced(s, modes)</c>.</param>
    /// <param name="configureServices">Runs on the service collection after <paramref name="register"/> and before the host is built.</param>
    /// <param name="cancellationToken">Defaults to the current NUnit test's token.</param>
    public static async Task<IReadOnlyList<Tool>> ListToolsAsync(
        Action<IMcpServerBuilder, IServiceCollection> register,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(register);
        if (cancellationToken == default)
        {
            cancellationToken = TestContext.CurrentContext.CancellationToken;
        }

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddSingleton<ILlmClient, StubLlmClient>();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        register(mcpBuilder, services);
        configureServices?.Invoke(services);

        var hostBuilder = Host.CreateApplicationBuilder();
        ServerStartupHelpers.EnableValidateOnBuild(hostBuilder);
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        await host.StartAsync(cancellationToken);
        try
        {
            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

            // ListToolsAsync follows nextCursor until the last page.
            var clientTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return clientTools.Select(t => t.ProtocolTool).ToList();
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The canonical form of a tool: its serialization with the MCP SDK's default options.</summary>
    public static string Canonical(Tool tool) => JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions);

    /// <summary>
    /// Compares <paramref name="tools"/> (in served order) with the golden of <paramref name="caseName"/>, or writes the golden
    /// and fails with a "regenerated" message when <see cref="UpdateEnvironmentVariable"/> is "1". A missing golden or any difference fails the test with the first
    /// differing line and, for a changed tool, the golden and current JSON (each clipped). A case name must be unique per golden
    /// folder: two cases whose sanitized names are equal overwrite one file (this is not detected). Characters other than letters,
    /// digits, '.', '_', '+' and '-' in the name become '_' in the file name.
    /// </summary>
    /// <param name="caseName">E.g. <c>mode-all</c>; the golden file is <c>&lt;sanitized name&gt;.txt</c>.</param>
    /// <param name="tools">The served tools, e.g. from <see cref="ListToolsAsync"/>.</param>
    /// <param name="callerFile">Filled by the compiler; the golden folder is beside this file. Do not pass it.</param>
    public static void AssertMatchesGolden(string caseName, IReadOnlyList<Tool> tools, [CallerFilePath] string callerFile = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseName);
        ArgumentNullException.ThrowIfNull(tools);
        if (string.IsNullOrEmpty(callerFile))
        {
            throw new ArgumentException("callerFile is empty; AssertMatchesGolden must be called with [CallerFilePath] intact.", nameof(callerFile));
        }

        var folder = Path.Combine(Path.GetDirectoryName(callerFile)!, GoldenFolderName);
        var caseFile = Path.Combine(folder, SanitizeFileName(caseName) + ".txt");
        var catalogFile = Path.Combine(folder, CatalogFileName);

        var rows = tools.Select(t =>
        {
            var json = Canonical(t);
            return (t.Name, Hash: Sha256Hex(json), Json: json);
        }).ToList();
        var currentText = string.Concat(rows.Select(r => r.Name + "\t" + r.Hash + "\n"));

        if (Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) == "1")
        {
            Directory.CreateDirectory(folder);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(caseFile, currentText, utf8);

            // Prune against the case files as they are now (this one already rewritten), so a run of one case is safe: the other
            // cases' files keep their own hashes alive. A case file whose case no longer exists keeps its entries until it is deleted.
            var catalog = new SortedDictionary<string, string>(ReadCatalog(catalogFile), StringComparer.Ordinal);
            foreach (var row in rows)
            {
                catalog[row.Hash] = row.Json;
            }

            var referenced = ReferencedHashes(folder);
            foreach (var unreferenced in catalog.Keys.Where(hash => !referenced.Contains(hash)).ToList())
            {
                catalog.Remove(unreferenced);
            }

            File.WriteAllText(catalogFile, JsonSerializer.Serialize(catalog, CatalogOptions) + "\n", utf8);
            Assert.Fail($"Golden for case '{caseName}' regenerated at {caseFile} because {UpdateEnvironmentVariable}=1. This run verified nothing: unset the variable and rerun.");
        }

        if (!File.Exists(caseFile))
        {
            Assert.Fail($"No golden for case '{caseName}' at {caseFile}. Set {UpdateEnvironmentVariable}=1 and run the test once to create it.");
        }

        var goldenText = File.ReadAllText(caseFile).Replace("\r\n", "\n");
        if (goldenText == currentText)
        {
            return;
        }

        var golden = SplitLines(goldenText);
        var current = SplitLines(currentText);
        var firstDiff = 0;
        while (firstDiff < golden.Count && firstDiff < current.Count && golden[firstDiff] == current[firstDiff])
        {
            firstDiff++;
        }

        var message = new StringBuilder();
        message.Append($"Tool surface of case '{caseName}' differs from {caseFile} ({golden.Count} golden tools, {current.Count} served).");
        message.Append($" First differing line: {firstDiff + 1}.");
        message.Append($"{Environment.NewLine}  golden : {(firstDiff < golden.Count ? golden[firstDiff] : "<no line>")}");
        message.Append($"{Environment.NewLine}  current: {(firstDiff < current.Count ? current[firstDiff] : "<no line>")}");

        var goldenNames = golden.Select(NameOf).ToList();
        var currentNames = current.Select(NameOf).ToList();
        var missing = goldenNames.Except(currentNames, StringComparer.Ordinal).ToList();
        var added = currentNames.Except(goldenNames, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            message.Append($"{Environment.NewLine}  no longer served: {Clip(string.Join(", ", missing))}");
        }

        if (added.Count > 0)
        {
            message.Append($"{Environment.NewLine}  newly served: {Clip(string.Join(", ", added))}");
        }

        if (missing.Count == 0 && added.Count == 0 && !goldenNames.SequenceEqual(currentNames, StringComparer.Ordinal))
        {
            message.Append($"{Environment.NewLine}  same tool names, different order.");
        }

        if (firstDiff < golden.Count && firstDiff < current.Count && NameOf(golden[firstDiff]) == NameOf(current[firstDiff]))
        {
            var goldenHash = golden[firstDiff][(golden[firstDiff].IndexOf('\t') + 1)..];
            var currentRow = rows[firstDiff];
            var catalog = ReadCatalog(catalogFile);
            var goldenJson = catalog.TryGetValue(goldenHash, out var found) ? found : "<not in " + CatalogFileName + ">";
            message.Append($"{Environment.NewLine}  golden JSON : {Clip(goldenJson)}");
            message.Append($"{Environment.NewLine}  current JSON: {Clip(currentRow.Json)}");
        }

        Assert.Fail(message.ToString());
    }

    private static string NameOf(string line)
    {
        var tab = line.IndexOf('\t');
        return tab < 0 ? line : line[..tab];
    }

    private static List<string> SplitLines(string text) =>
        text.Split('\n').Where(l => l.Length > 0).ToList();

    /// <summary>The hashes (second tab-separated column) of every case file in <paramref name="folder"/>.</summary>
    private static HashSet<string> ReferencedHashes(string folder)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
        {
            foreach (var line in SplitLines(File.ReadAllText(file).Replace("\r\n", "\n")))
            {
                var tab = line.IndexOf('\t');
                if (tab >= 0)
                {
                    hashes.Add(line[(tab + 1)..]);
                }
            }
        }

        return hashes;
    }

    private static string Clip(string text) =>
        text.Length <= ClipLength ? text : text[..ClipLength] + "...(" + (text.Length - ClipLength) + " more chars)";

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Letters, digits, '.', '_', '+' and '-' stay; every other character (including ',', '=', ' ' and path separators) becomes '_'.</summary>
    private static string SanitizeFileName(string caseName)
    {
        var sb = new StringBuilder(caseName.Length);
        foreach (var c in caseName)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' ? c : '_');
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ReadCatalog(string catalogFile) =>
        File.Exists(catalogFile)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(catalogFile)) ?? []
            : [];
}

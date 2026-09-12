// SentinelConsoleMode.cs v2
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;
using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Handles the --list-tools and --interactive console modes.
/// </summary>
public static partial class SentinelConsoleMode
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    // ─── --list-tools ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the real MCP tool manifest for a given registration by constructing a throwaway
    /// <see cref="ServiceCollection"/>, invoking <paramref name="registerTools"/> against it exactly
    /// as a live server entry point would, then extracting the resulting <see cref="McpServerTool"/>
    /// instances. This is the same mechanism <see cref="WriteStartupDump"/> uses against a real
    /// host's <see cref="IServiceProvider"/>, so <c>--list-tools</c> can never drift from what a
    /// running server actually serves — replacing a previous implementation
    /// (<c>DiscoverTools</c>) that re-derived the tool surface via a separate, hardcoded
    /// single-assembly reflection pass and reported snake_case names the server never serves. See
    /// docs/current/blockers/blocking_error_list_tools_misreports_tool_surface.md.
    /// </summary>
    internal static List<ModelContextProtocol.Protocol.Tool> BuildToolManifestFor(
        Func<IMcpServerBuilder, IServiceCollection, IMcpServerBuilder> registerTools)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var mcpBuilder = services.AddMcpServer();
        registerTools(mcpBuilder, services);

        using var provider = services.BuildServiceProvider();
        return ExtractToolManifest(provider);
    }

    /// <summary>
    /// Reads <see cref="McpServerTool.ProtocolTool"/> from every registered DI instance. Falls back
    /// to constructing tools directly via <see cref="McpServerTool.Create"/> over all loaded
    /// RoslynSentinel.Server.* assemblies when DI registration returns zero (e.g. singleton factory
    /// delay or scope mismatch at startup time) — mirrors what <c>WithToolsFixed&lt;T&gt;()</c> does.
    /// Shared by <see cref="BuildToolManifestFor"/> and <see cref="WriteStartupDump"/> so the two
    /// can never independently drift.
    /// </summary>
    private static List<ModelContextProtocol.Protocol.Tool> ExtractToolManifest(IServiceProvider services)
    {
        var tools = services.GetServices<McpServerTool>()
            .Select(t => t.ProtocolTool)
            .OrderBy(t => t.Name)
            .ToList();

        if (tools.Count == 0)
        {
            tools = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel.Server", StringComparison.Ordinal) == true)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .SelectMany(type =>
                {
                    var instance = services.GetService(type);
                    var opts = new McpServerToolCreateOptions { Services = services, SchemaCreateOptions = McpToolSchemaFix.SchemaCreateOptions };
                    return type
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null
                                 && (m.IsStatic || instance != null))
                        .SelectMany(m =>
                        {
                            try
                            {
                                return (IEnumerable<ModelContextProtocol.Protocol.Tool>)
                                    [McpServerTool.Create(m, m.IsStatic ? null : instance, opts).ProtocolTool];
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine(
                                    $"[ListTools] Skipping {type.Name}.{m.Name}: {ex.Message}");
                                return [];
                            }
                        });
                })
                .OrderBy(t => t.Name)
                .ToList();
        }

        return tools;
    }

    /// <summary>
    /// Prints the tool surface <paramref name="registerTools"/> would expose. Defaults to a
    /// names-only listing; pass <paramref name="full"/> (--full) for the same envelope
    /// <c>tool_list_&lt;modes&gt;.json</c> carries (name/description/inputSchema).
    /// </summary>
    public static void ListTools(
        Func<IMcpServerBuilder, IServiceCollection, IMcpServerBuilder> registerTools,
        string? outputPath,
        bool full = false)
    {
        var tools = BuildToolManifestFor(registerTools);

        object data = full
            ? new
            {
                _metadata = new { toolCount = tools.Count, generatedUtc = DateTime.UtcNow.ToString("O") },
                tools = tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }),
            }
            : new
            {
                _metadata = new { toolCount = tools.Count, generatedUtc = DateTime.UtcNow.ToString("O") },
                tools = tools.Select(t => t.Name).ToList(),
            };

        var json = JsonSerializer.Serialize(data, PrettyJson);

        if (outputPath is not null)
        {
            File.WriteAllText(outputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"Written {tools.Count} tools to: {outputPath}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    // ─── --interactive REPL ────────────────────────────────────────────────

    private static int _msgId;

    private static string NewRequest(string method, object? @params)
    {
        var id = System.Threading.Interlocked.Increment(ref _msgId);
        var msg = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params != null)
        {
            msg["params"] = @params;
        }

        return JsonSerializer.Serialize(msg, CompactJson) + "\n";
    }

    private static string NewNotification(string method, object? @params = null)
    {
        var msg = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params != null)
        {
            msg["params"] = @params;
        }

        return JsonSerializer.Serialize(msg, CompactJson) + "\n";
    }

    /// <summary>
    /// Reads JSON-RPC messages from <paramref name="reader"/> until one matching
    /// <paramref name="expectedId"/> is found.  Notifications (no id) are skipped.
    /// </summary>
    private static async Task<JsonNode?> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;          // stream closed
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(line);
                if (node?["id"] is null)
                {
                    continue;  // notification — ignore
                }

                if (node["id"]!.GetValue<int>() == expectedId)
                {
                    return node;
                }
            }
            catch (JsonException) { /* malformed line — skip */ }
        }

        return null;
    }

    /// <summary>
    /// Runs an interactive MCP REPL.  The REPL communicates with the MCP server
    /// via <paramref name="clientWriteStream"/> / <paramref name="clientReadStream"/>
    /// (piped to <see cref="Microsoft.Extensions.DependencyInjection.McpServerBuilderExtensions.WithStreamServerTransport"/>).
    /// When the user types <c>exit</c> (or presses Ctrl+C) the
    /// <paramref name="lifetimeCts"/> is cancelled so the host shuts down cleanly.
    /// </summary>
    public static async Task RunReplAsync(
        Stream clientWriteStream,
        Stream clientReadStream,
        CancellationTokenSource lifetimeCts)
    {
        using var writer = new StreamWriter(clientWriteStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(clientReadStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // ── MCP handshake ──────────────────────────────────────────────────
        Console.Error.WriteLine("[interactive] Performing MCP handshake…");
        var initId = System.Threading.Interlocked.Increment(ref _msgId);
        try
        {
            await writer.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = initId,
                ["method"] = "initialize",
                ["params"] = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new
                    {
                    },
                    clientInfo = new
                    {
                        name = "sentinel-repl",
                        version = "1.0"
                    },
                },
            }, CompactJson) + "\n").ConfigureAwait(false);

            using var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedInit = CancellationTokenSource.CreateLinkedTokenSource(initCts.Token, cts.Token);

            var initResp = await ReadResponseAsync(reader, initId, linkedInit.Token).ConfigureAwait(false);
            if (initResp?["result"] is null)
            {
                Console.Error.WriteLine("[interactive] Handshake failed — no result from server.");
                return;
            }

            // Send the required initialized notification
            await writer.WriteAsync(NewNotification("notifications/initialized")).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[interactive] Handshake timed out.");
            return;
        }

        Console.Error.WriteLine("[interactive] Ready.  Commands:");
        Console.Error.WriteLine("  <tool_name> [{json_args}]   — call a tool");
        Console.Error.WriteLine("  ? [filter]                  — list tools (optional name filter)");
        Console.Error.WriteLine("  describe <tool_name>        — show parameters");
        Console.Error.WriteLine("  exit                        — quit");

        // ── REPL loop ──────────────────────────────────────────────────────
        while (!cts.IsCancellationRequested)
        {
            Console.Write("\nsentinel> ");

            string? input;
            try
            {
                input = Console.ReadLine();
            }
            catch (Exception)
            {
                break;
            }

            if (input is null)
            {
                break;
            }

            input = input.Trim();
            if (string.IsNullOrEmpty(input))
            {
                continue;
            }

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input == "?" ||
                input.StartsWith("? ", StringComparison.Ordinal) ||
                input.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                var filter = input.Contains(' ') ? input[(input.IndexOf(' ') + 1)..].Trim() : null;
                await ListToolsViaReplAsync(writer, reader, filter, cts.Token).ConfigureAwait(false);
                continue;
            }

            if (input.StartsWith("describe ", StringComparison.OrdinalIgnoreCase))
            {
                await DescribeToolViaReplAsync(writer, reader, input[9..].Trim(), cts.Token).ConfigureAwait(false);
                continue;
            }

            // Parse: toolName [{json}]
            var braceIdx = input.IndexOf('{');
            string toolName;
            string argsJson;
            if (braceIdx > 0)
            {
                toolName = input[..braceIdx].Trim();
                argsJson = input[braceIdx..].Trim();
            }
            else
            {
                toolName = input;
                argsJson = "{}";
            }

            JsonNode? argsNode;
            try
            {
                argsNode = JsonNode.Parse(argsJson);
            }
            catch (JsonException)
            {
                Console.WriteLine("[error] Invalid JSON arguments — expected an object like {\"param\": \"value\"}");
                continue;
            }

            await CallToolAsync(writer, reader, toolName, argsNode, cts.Token).ConfigureAwait(false);
        }

        Console.Error.WriteLine("\n[interactive] Session ended.");
        lifetimeCts.Cancel();
    }

    private static async Task ListToolsViaReplAsync(
        StreamWriter writer, StreamReader reader,
        string? filter,
        CancellationToken cancellationToken)
    {
        var id = System.Threading.Interlocked.Increment(ref _msgId);
        await writer.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/list",
            ["params"] = new { },
        }, CompactJson) + "\n").ConfigureAwait(false);

        try
        {
            using var tcs = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(tcs.Token, cancellationToken);
            var resp = await ReadResponseAsync(reader, id, linked.Token).ConfigureAwait(false);

            if (resp?["result"]?["tools"] is not JsonArray toolsArr)
            {
                Console.WriteLine("[error] Unexpected response from tools/list.");
                return;
            }

            var tools = toolsArr
                .Select(t => (name: t?["name"]?.GetValue<string>() ?? "", desc: t?["description"]?.GetValue<string>() ?? ""))
                .Where(t => filter is null || t.name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.name)
                .ToList();

            Console.WriteLine($"\n  {tools.Count} tool{(tools.Count == 1 ? "" : "s")}{(filter is not null ? $" matching '{filter}'" : "")}:\n");
            foreach (var (name, desc) in tools)
            {
                var shortDesc = desc.Length > 78 ? desc[..75] + "…" : desc;
                Console.WriteLine($"  {name,-52} {shortDesc}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[error] Timed out waiting for tools/list.");
        }
    }

    private static async Task CallToolAsync(
        StreamWriter writer, StreamReader reader,
        string toolName, JsonNode? args,
        CancellationToken cancellationToken)
    {
        var id = System.Threading.Interlocked.Increment(ref _msgId);
        await writer.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new { name = toolName, arguments = args },
        }, CompactJson) + "\n").ConfigureAwait(false);

        Console.WriteLine($"  [calling {toolName}…]");

        try
        {
            using var tcs = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(tcs.Token, cancellationToken);
            var resp = await ReadResponseAsync(reader, id, linked.Token).ConfigureAwait(false);

            if (resp is null)
            {
                Console.WriteLine("  [error] Connection closed.");
                return;
            }

            if (resp["error"] is JsonNode error)
            {
                Console.WriteLine($"  [error] {error["message"]?.GetValue<string>() ?? error.ToJsonString()}");
                return;
            }

            if (resp["result"] is JsonNode result)
            {
                if (result["isError"]?.GetValue<bool>() == true)
                {
                    Console.WriteLine("  [isError=true]");
                }

                if (result["content"] is JsonArray content)
                {
                    foreach (var item in content)
                    {
                        var text = item?["text"]?.GetValue<string>();
                        if (text is null)
                        {
                            continue;
                        }

                        // Pretty-print if the text itself is JSON
                        try
                        {
                            var parsed = JsonNode.Parse(text);
                            Console.WriteLine(parsed!.ToJsonString(PrettyJson));
                        }
                        catch (JsonException)
                        {
                            Console.WriteLine(text);
                        }
                    }
                }
                else
                {
                    Console.WriteLine(result.ToJsonString(PrettyJson));
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"  [error] Timed out waiting for response to {toolName}.");
        }
    }

    /// <summary>
    /// Describes one tool by querying the live server's own <c>tools/list</c> over the REPL's
    /// JSON-RPC connection, rather than a locally-cached reflection pass — so the description can
    /// never disagree with what <c>tools/call</c> on the same connection would actually accept.
    /// </summary>
    private static async Task DescribeToolViaReplAsync(
        StreamWriter writer, StreamReader reader,
        string toolName,
        CancellationToken cancellationToken)
    {
        var id = System.Threading.Interlocked.Increment(ref _msgId);
        await writer.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/list",
            ["params"] = new { },
        }, CompactJson) + "\n").ConfigureAwait(false);

        try
        {
            using var tcs = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(tcs.Token, cancellationToken);
            var resp = await ReadResponseAsync(reader, id, linked.Token).ConfigureAwait(false);

            if (resp?["result"]?["tools"] is not JsonArray toolsArr)
            {
                Console.WriteLine("[error] Unexpected response from tools/list.");
                return;
            }

            var tool = toolsArr.FirstOrDefault(t =>
                string.Equals(t?["name"]?.GetValue<string>(), toolName, StringComparison.OrdinalIgnoreCase));

            if (tool is null)
            {
                Console.WriteLine($"  Unknown tool '{toolName}'.  Use '?' to list available tools.");
                return;
            }

            Console.WriteLine($"\n  Tool  : {tool["name"]?.GetValue<string>()}");
            Console.WriteLine($"  Desc  : {tool["description"]?.GetValue<string>()}");

            if (tool["inputSchema"]?["properties"] is JsonObject props)
            {
                var required = (tool["inputSchema"]?["required"] as JsonArray)?
                    .Select(r => r?.GetValue<string>())
                    .Where(r => r is not null)
                    .Select(r => r!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Console.WriteLine("  Params:");
                foreach (var (paramName, schema) in props)
                {
                    var req = required.Contains(paramName) ? "required" : "optional";
                    var typeName = schema?["type"]?.GetValue<string>() ?? "any";
                    var pdesc = schema?["description"]?.GetValue<string>() ?? "";
                    Console.WriteLine($"    {paramName,-30} {typeName,-10} [{req}]  {pdesc}");
                }
            }
            else
            {
                Console.WriteLine("  Params: (none)");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[error] Timed out waiting for tools/list.");
        }
    }

    // ─── Startup dump ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes tool_list.json (full MCP payload) and tool_list_simple.json (names only)
    /// to <paramref name="outputDir"/> on every server startup. Delegates extraction to
    /// <see cref="ExtractToolManifest"/> — the same routine <see cref="ListTools"/> uses — so the
    /// two can never independently drift.
    /// NOT an [McpServerTool] — internal diagnostic output only.
    /// </summary>
    public static void WriteStartupDump(IServiceProvider services, string outputDir, string modeArg)
    {
        try
        {
            var modeSuffix = "_" + modeArg.ToLowerInvariant()
                                         .Replace(", ", "_")
                                         .Replace(",", "_")
                                         .Replace(" ", "_");

            var tools = ExtractToolManifest(services);

            string generatedUtc = DateTime.UtcNow.ToString("O");
            int totalChars = tools.Sum(t => t.Name.Length + (t.Description?.Length ?? 0));

            // ── tool_list.json — full payload: name + description + inputSchema ──
            var fullPayload = new
            {
                _metadata = new
                {
                    toolCount = tools.Count,
                    generatedUtc = generatedUtc,
                    totalPayloadChars = totalChars,
                },
                tools = tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = t.InputSchema,
                }),
            };

            File.WriteAllText(
                Path.Combine(outputDir, $"tool_list{modeSuffix}.json"),
                JsonSerializer.Serialize(fullPayload, PrettyJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // ── tool_list_simple.json — names only for human readability ────────
            var simplePayload = new
            {
                _metadata = new
                {
                    toolCount = tools.Count,
                    generatedUtc = generatedUtc,
                },
                tools = tools.Select(t => t.Name).ToList(),
            };

            File.WriteAllText(
                Path.Combine(outputDir, $"tool_list_simple{modeSuffix}.json"),
                JsonSerializer.Serialize(simplePayload, PrettyJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StartupDump] Failed to write tool list: {ex.Message}");
        }
    }

    // ─── Method inventory dump ─────────────────────────────────────────────

    /// <summary>
    /// Writes <c>all_methods.csv</c> and <c>engine_methods.json</c> to the solution root
    /// on every server startup, replacing any hand-maintained copies.
    /// Reflects over every concrete class in the assembly to produce an up-to-date
    /// inventory of public instance methods — no manual editing required.
    /// NOT an [McpServerTool] — internal diagnostic output only.
    /// </summary>
    public static void WriteMethodInventory(string outputDir, string modeArg)
    {
        try
        {
            var modeSuffix = "_" + modeArg.ToLowerInvariant()
                                         .Replace(", ", "_")
                                         .Replace(",", "_")
                                         .Replace(" ", "_");

            var solutionRoot = FindSolutionRoot(outputDir) ?? outputDir;
            var inventoryDir = solutionRoot;

            // Scan all loaded RoslynSentinel.Server.* assemblies so both Basic and Advanced
            // types are captured regardless of which server entry point is running.
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel.Server", StringComparison.Ordinal) == true)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                .Where(t => t.IsClass
                         && !t.IsAbstract
                         && !t.IsGenericTypeDefinition
                         && t.Namespace?.StartsWith("RoslynSentinel.Server.", StringComparison.Ordinal) == true
                         && !t.Name.Contains('<'))
                .OrderBy(t => t.Name)
                .ToList();

            var csvLines = new List<string> { "\"Engine\",\"Line\",\"Method\"" };
            var jsonEntries = new Dictionary<string, List<object>>();

            foreach (var type in types)
            {
                var methods = type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName)
                    .OrderBy(m => m.MetadataToken)
                    .ToList();

                if (methods.Count == 0)
                {
                    continue;
                }

                var jsonMethods = new List<object>();
                foreach (var method in methods)
                {
                    var sig = BuildMethodSignature(method);
                    var returnType = ExtractInnerReturnType(method.ReturnType);
                    var sigTrunc = sig.Length > 120 ? sig[..120] : sig;

                    csvLines.Add($"\"{type.Name}\",\"{EscapeCsvField(sigTrunc)}\",\"{method.Name}\"");
                    jsonMethods.Add(new { ReturnType = returnType, Signature = sig, Name = method.Name });
                }

                jsonEntries[type.Name] = jsonMethods;
            }

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            File.WriteAllText(
                Path.Combine(inventoryDir, $"all_methods{modeSuffix}.csv"),
                string.Join(Environment.NewLine, csvLines) + Environment.NewLine,
                encoding);

            File.WriteAllText(
                Path.Combine(inventoryDir, $"engine_methods{modeSuffix}.json"),
                JsonSerializer.Serialize(jsonEntries, PrettyJson),
                encoding);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MethodInventory] Failed to write method inventory: {ex.Message}");
        }
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (dir.GetFiles("Directory.Build.props").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }
        return null;
    }

    private static string BuildMethodSignature(MethodInfo method)
    {
        var isAsync = method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null;
        var asyncMod = isAsync ? "async " : "";
        var returnType = FriendlyType(method.ReturnType);
        var @params = string.Join(", ", method.GetParameters().Select(BuildParamString));
        return $"public {asyncMod}{returnType} {method.Name}({@params})";
    }

    private static string BuildParamString(ParameterInfo p)
    {
        var typeName = FriendlyType(p.ParameterType);
        var name = p.Name ?? "_";
        if (!p.HasDefaultValue)
        {
            return $"{typeName} {name}";
        }

        var defStr = p.DefaultValue switch
        {
            null => "null",
            string s => $"\\{s}\\",
            bool b => b ? "true" : "false",
            var other => other?.ToString() ?? "null",
        };
        return $"{typeName} {name} = {defStr}";
    }

    private static string ExtractInnerReturnType(Type returnType)
    {
        if (returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return FriendlyType(returnType.GetGenericArguments()[0]);
        }

        if (returnType == typeof(Task) || returnType == typeof(void))
        {
            return "void";
        }

        return FriendlyType(returnType);
    }

    private static string FriendlyType(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var outer = type.Name[..type.Name.IndexOf('`')];
        var args = string.Join(", ", type.GetGenericArguments().Select(FriendlyType));
        return $"{outer}<{args}>";
    }

    private static string EscapeCsvField(string s) => s.Replace("\"", "\"\"");
}

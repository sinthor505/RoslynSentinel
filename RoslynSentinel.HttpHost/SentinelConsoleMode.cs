using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using ModelContextProtocol.Server;

using RoslynSentinel.Server;

namespace RoslynSentinel.HttpHost;

/// <summary>
/// Handles the --list-tools and --interactive console modes.
/// </summary>
public static partial class SentinelConsoleMode
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private static readonly Dictionary<string, string> ToolTypeToMode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SentinelWorkspaceTools"] = "Workspace",
        ["SentinelIntelligenceTools"] = "Intelligence",
        ["SentinelRefactoringTools"] = "Refactor",
        ["SentinelAugmentTools"] = "Refactor",
        ["SentinelModernizationTools"] = "Modernize",
        ["SentinelQualityTools"] = "Quality",
        ["SentinelGenerationTools"] = "Generation",
    };

    // ─── Snake-case helpers ──────────────────────────────────────────────────

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex SnakeRegex1();

    [GeneratedRegex(@"([A-Z]+)([A-Z][a-z])")]
    private static partial Regex SnakeRegex2();

    internal static string ToSnakeCase(string name)
    {
        var s = SnakeRegex1().Replace(name, "$1_$2");
        s = SnakeRegex2().Replace(s, "$1_$2");
        return s.ToLowerInvariant();
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

    // ─── Tool discovery via reflection ──────────────────────────────────────

    private sealed record ToolEntry(string Name, string? Description, string Module, MethodInfo Method, Type ToolType);

    private static IReadOnlyList<ToolEntry> DiscoverTools(HashSet<string> activeModes)
    {
        return typeof(SentinelWorkspaceTools).Assembly
            .GetTypes()
            .Where(t =>
            {
                if (t.GetCustomAttribute<McpServerToolTypeAttribute>() == null)
                {
                    return false;
                }

                return !ToolTypeToMode.TryGetValue(t.Name, out var mode) || activeModes.Contains(mode);
            })
            .OrderBy(t => t.Name)
            .SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                    .Select(m => new ToolEntry(
                        Name: ToSnakeCase(m.Name),
                        Description: m.GetCustomAttribute<DescriptionAttribute>()?.Description,
                        Module: type.Name.Replace("Sentinel", "", StringComparison.Ordinal)
                                        .Replace("Tools", "", StringComparison.Ordinal),
                        Method: m,
                        ToolType: type)))
            .OrderBy(t => t.Name)
            .ToList();
    }

    // ─── --list-tools ────────────────────────────────────────────────────────

    public static void ListTools(HashSet<string> activeModes, string? outputPath)
    {
        var tools = DiscoverTools(activeModes);

        var data = tools.Select(t => (object)new
        {
            name = t.Name,
            module = t.Module,
            description = t.Description,
            parameters = t.Method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => new
                {
                    name = p.Name,
                    type = FriendlyType(p.ParameterType),
                    required = !p.HasDefaultValue,
                    description = p.GetCustomAttribute<DescriptionAttribute>()?.Description,
                })
                .ToArray(),
        }).ToList();

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

    // ─── --interactive REPL ──────────────────────────────────────────────────

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
    private static async Task<JsonNode?> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
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
        HashSet<string> activeModes,
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

        // ── MCP handshake ────────────────────────────────────────────────────
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

        // Pre-build local tool dictionary (for describe / validation)
        var localTools = DiscoverTools(activeModes).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // ── REPL loop ────────────────────────────────────────────────────────
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
                DescribeTool(localTools, input[9..].Trim());
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
        CancellationToken ct)
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(tcs.Token, ct);
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
        CancellationToken ct)
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(tcs.Token, ct);
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

    private static void DescribeTool(Dictionary<string, ToolEntry> localTools, string toolName)
    {
        if (!localTools.TryGetValue(toolName, out var tool))
        {
            Console.WriteLine($"  Unknown tool '{toolName}'.  Use '?' to list available tools.");
            return;
        }

        Console.WriteLine($"\n  Tool  : {tool.Name}");
        Console.WriteLine($"  Module: {tool.Module}");
        Console.WriteLine($"  Desc  : {tool.Description}");

        var parameters = tool.Method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .ToList();

        if (parameters.Count == 0)
        {
            Console.WriteLine("  Params: (none)");
        }
        else
        {
            Console.WriteLine("  Params:");
            foreach (var p in parameters)
            {
                var req = p.HasDefaultValue ? "optional" : "required";
                var pdesc = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                Console.WriteLine($"    {p.Name,-30} {FriendlyType(p.ParameterType),-22} [{req}]  {pdesc}");
            }
        }
    }

    // ─── Startup dump ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes tool_list.json (full MCP payload) and tool_list_simple.json (names only)
    /// to <paramref name="outputDir"/> on every server startup.
    /// Reads <see cref="McpServerTool.ProtocolTool"/> from registered DI instances —
    /// the exact schema the MCP layer emits, not a hand-rolled reflection summary.
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

            var tools = services.GetServices<McpServerTool>()
                .Select(t => t.ProtocolTool)
                .OrderBy(t => t.Name)
                .ToList();

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

            // ── tool_list_simple.json — names only for human readability ─────────
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

    // ─── Method inventory dump ───────────────────────────────────────────────

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
            var serverSrcDir = Path.Combine(solutionRoot, "RoslynSentinel.Server");
            var inventoryDir = Directory.Exists(serverSrcDir) ? serverSrcDir : solutionRoot;

            var types = typeof(SentinelConsoleMode).Assembly
                .GetTypes()
                .Where(t => t.IsClass
                         && !t.IsAbstract
                         && !t.IsGenericTypeDefinition
                         && t.Namespace == "RoslynSentinel.Server"
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

    private static string EscapeCsvField(string s) => s.Replace("\"", "\"\"");
}

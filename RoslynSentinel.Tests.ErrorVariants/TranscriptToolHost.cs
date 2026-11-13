using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// A fake <see cref="IAgentToolHost"/> that answers a replayed run from a saved transcript (plan Step 6). The tool list comes
/// from the transcript header; a call is answered only from the source transcript's turns AFTER the cut, and only for read
/// tools (<see cref="ToolEffectList"/>) whose name and canonical arguments equal a recorded call. Any other call gets
/// <see cref="PlaceholderText"/> and marks the host <see cref="OffScript"/>. A fake service (DTO-EXEMPT: fake service, no data type).
/// </summary>
/// <remarks>
/// Matching policy for one (tool, canonical arguments) pair recorded several times after the cut: the first not-yet-served
/// recorded call is served, and once every recorded call of the pair has been served the LAST one is served again (a re-read
/// of an unchanged result).
///
/// Arguments are canonicalized before comparing: object keys sorted ordinally at every depth, numbers compared by value
/// (1 equals 1.0), strings exact, and the free-text "reason" argument ignored at the top level and inside batchEdits items
/// (the same fields LoopAssist ignores when it compares calls: a model never writes the same justification twice).
///
/// Sticky override: when a variant is given, a served recorded result that carries the same errorCode AND the same decoded
/// message as the overridden call of the source's cut turn gets the variant text too. Otherwise the source arm's own text
/// would leak back into the replay the first time the model repeats the failing call. A result whose code or message
/// differs is returned as recorded; with no variant everything is returned as recorded.
/// </remarks>
public sealed class TranscriptToolHost : IAgentToolHost
{
    /// <summary>
    /// What every off-script call returns: a neutral, successful, JSON envelope that names nothing from the transcript, so the
    /// model sees no hint either way. The run is expected to stop right after (targetReached reads <see cref="OffScript"/>).
    /// </summary>
    public const string PlaceholderText = "{\"isError\":false,\"statusMessage\":\"transcript replay: this call is outside the recorded script\"}";

    private readonly AgentTranscript _source;
    private readonly ErrorVariant? _variant;
    private readonly string? _solutionDirectory;
    private readonly string? _overriddenMessage;
    private readonly Dictionary<(string Tool, string Arguments), List<AgentToolCallRecord>> _scripted = [];
    private readonly Dictionary<(string Tool, string Arguments), int> _served = [];

    /// <param name="source">The saved transcript to replay from.</param>
    /// <param name="cutTurn">Number of source turns kept as the seed (as in ResultOverride.Apply); only turns after it are served from.</param>
    /// <param name="variant">The error variant applied to the overridden call's recorded result wherever it is served again; null serves everything as recorded.</param>
    /// <param name="solutionDirectory">Replaces the variant's {SOLUTION} token; null leaves the token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cutTurn"/> is below 1 or above the number of source turns.</exception>
    /// <exception cref="InvalidOperationException">
    /// A variant was given and the source's cut turn has no single erroring call, or its errorCode differs from the variant's.
    /// </exception>
    public TranscriptToolHost(AgentTranscript source, int cutTurn, ErrorVariant? variant = null, string? solutionDirectory = null)
    {
        _source = source;
        _variant = variant;
        _solutionDirectory = solutionDirectory;
        if (cutTurn < 1 || cutTurn > source.Turns.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cutTurn), cutTurn, $"The transcript has {source.Turns.Count} turn(s); cut between 1 and {source.Turns.Count}.");
        }

        if (variant is not null)
        {
            var (callIndex, call, code, message) = ResultOverride.FindErrorCall(source.Turns[cutTurn - 1], cutTurn);
            if (!string.Equals(code, variant.ErrorCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Turn {cutTurn}, call {callIndex + 1} ({call.ToolName}): the recorded errorData.errorCode is '{code}' but variant '{variant.Id}' targets '{variant.ErrorCode}'.");
            }

            _overriddenMessage = message;
        }

        // Turn numbers are 1-based, so the 0-based indexes cutTurn.. are the turns after the cut.
        for (var i = cutTurn; i < source.Turns.Count; i++)
        {
            foreach (var call in source.Turns[i].ToolCalls)
            {
                var key = (call.ToolName, CanonicalArguments(call.ArgumentsJson));
                if (!_scripted.TryGetValue(key, out var list))
                {
                    list = [];
                    _scripted[key] = list;
                }

                list.Add(call);
            }
        }
    }

    /// <summary>True once a call was answered with <see cref="PlaceholderText"/>; stays true.</summary>
    public bool OffScript
    {
        get; private set;
    }

    /// <summary>The tool of the FIRST off-script call; null while on script. Later off-script calls do not overwrite it.</summary>
    public string? OffScriptTool
    {
        get; private set;
    }

    /// <summary>
    /// Zero-based position of the FIRST off-script call among all calls the host received, which is also the number of calls it had
    /// answered from the script by then; null while on script.
    /// </summary>
    public int? OffScriptCallIndex
    {
        get; private set;
    }

    /// <summary>How many calls the host has received so far, scripted or not.</summary>
    public int CallCount
    {
        get; private set;
    }

    /// <summary>
    /// The header's tools in <see cref="AgentTranscript.Tools"/> order.
    /// </summary>
    /// <exception cref="InvalidOperationException">ToolDescriptions is null or empty, or a listed tool has no description or no schema.</exception>
    public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(BuildTools());
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromException<IReadOnlyList<AgentToolDefinition>>(ex);
        }
    }

    /// <inheritdoc/>
    public Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var callIndex = CallCount++;

        if (ToolEffectList.IsReadCall(toolName, arguments))
        {
            var key = (toolName, CanonicalArguments(arguments));
            if (_scripted.TryGetValue(key, out var recorded))
            {
                _served.TryGetValue(key, out var servedSoFar);
                var chosen = servedSoFar < recorded.Count ? recorded[servedSoFar] : recorded[^1];
                _served[key] = servedSoFar + 1;
                return Task.FromResult(ToResult(chosen));
            }
        }

        if (!OffScript)
        {
            OffScript = true;
            OffScriptTool = toolName;
            OffScriptCallIndex = callIndex;
        }

        return Task.FromResult(new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = PlaceholderText }],
        });
    }

    private IReadOnlyList<AgentToolDefinition> BuildTools()
    {
        var descriptions = _source.ToolDescriptions;
        if (descriptions is null || descriptions.Count == 0)
        {
            throw new InvalidOperationException(
                "The source transcript has no ToolDescriptions in its header (it predates their recording), so the tool list cannot be rebuilt.");
        }

        var schemas = _source.ToolSchemas;
        var tools = new List<AgentToolDefinition>(_source.Tools.Count);
        foreach (var name in _source.Tools)
        {
            if (!descriptions.TryGetValue(name, out var description))
            {
                throw new InvalidOperationException($"The source transcript header lists tool '{name}' but has no ToolDescriptions entry for it.");
            }

            if (schemas is null || !schemas.TryGetValue(name, out var schema))
            {
                throw new InvalidOperationException($"The source transcript header lists tool '{name}' but has no ToolSchemas entry for it.");
            }

            tools.Add(new AgentToolDefinition { Name = name, Description = description, ParametersSchema = schema });
        }

        return tools;
    }

    // The runner rebuilds ResultJson from the first text block and TrailingText from the later blocks joined by a blank
    // line, so one trailing block round-trips the recorded TrailingText byte for byte.
    private CallToolResult ToResult(AgentToolCallRecord recorded)
    {
        var resultJson = recorded.ResultJson;
        var trailing = recorded.TrailingText;

        if (_variant is not null
            && ResultOverride.TryReadError(recorded.ResultJson, out var code, out var message)
            && string.Equals(code, _variant.ErrorCode, StringComparison.Ordinal)
            && string.Equals(message, _overriddenMessage, StringComparison.Ordinal))
        {
            (resultJson, trailing) = ResultOverride.ApplyToCall(recorded, _variant, _solutionDirectory);
        }

        var content = new List<ContentBlock> { new TextContentBlock { Text = resultJson } };
        if (!string.IsNullOrEmpty(trailing))
        {
            content.Add(new TextContentBlock { Text = trailing });
        }

        return new CallToolResult { IsError = recorded.IsError, Content = content };
    }

    // ---- canonical arguments ----

    private static string CanonicalArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var builder = new StringBuilder();
            WriteElement(builder, doc.RootElement, topLevel: true, inBatchEditItem: false);
            return builder.ToString();
        }
        catch (JsonException)
        {
            // Not JSON: compare the text as is. It can only match another call whose live arguments serialize the same way, i.e. never.
            return "!raw:" + argumentsJson.Trim();
        }
    }

    private static string CanonicalArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        // The runner passes Dictionary<string, object?> from JsonSerializer (values are JsonElement or null); tests may pass
        // primitives and nested dictionaries. Serializing to a JsonElement gives every shape the same canonical path.
        var builder = new StringBuilder();
        WriteElement(builder, JsonSerializer.SerializeToElement(arguments), topLevel: true, inBatchEditItem: false);
        return builder.ToString();
    }

    private static void WriteElement(StringBuilder builder, JsonElement element, bool topLevel, bool inBatchEditItem)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // "reason" is dropped at the top level and inside batchEdits items, as LoopAssist.CanonicalArguments does.
                var dropReason = topLevel || inBatchEditItem;
                var members = element.EnumerateObject()
                    .Where(property => !(dropReason && property.Name == "reason"))
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToList();
                builder.Append('{');
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteString(builder, members[i].Name);
                    builder.Append(':');
                    var isBatchEdits = topLevel && members[i].Name == "batchEdits";
                    WriteArrayOrValue(builder, members[i].Value, isBatchEdits);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                WriteArrayOrValue(builder, element, itemsAreBatchEdits: false);
                break;
            case JsonValueKind.String:
                WriteString(builder, element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var number))
                {
                    WriteDecimal(builder, number);
                }
                else
                {
                    WriteDouble(builder, element.GetDouble());
                }

                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    // An array writes its items in order (they are batchEdits items when the flag is set); anything else is written as a plain value.
    private static void WriteArrayOrValue(StringBuilder builder, JsonElement value, bool itemsAreBatchEdits)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            WriteElement(builder, value, topLevel: false, inBatchEditItem: false);
            return;
        }

        builder.Append('[');
        var first = true;
        foreach (var item in value.EnumerateArray())
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteElement(builder, item, topLevel: false, inBatchEditItem: itemsAreBatchEdits);
        }

        builder.Append(']');
    }

    private static void WriteString(StringBuilder builder, string text) =>
        builder.Append(JsonSerializer.Serialize(text));

    // Dividing by 1.000...m strips trailing zeros, so 1, 1.0 and 1.00 print the same.
    private static void WriteDecimal(StringBuilder builder, decimal value) =>
        builder.Append((value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));

    private static void WriteDouble(StringBuilder builder, double value)
    {
        // A finite double inside decimal's range compares as a decimal so 1.0 (double) equals 1 (int) and the JSON text 1.0.
        if (double.IsFinite(value) && Math.Abs(value) < 7.9e27)
        {
            WriteDecimal(builder, (decimal)value);
        }
        else
        {
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}

using System.Diagnostics;
using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The logic of the agent identity call-tool filter (<c>AddAgentIdentityFilter</c> in
/// <see cref="RoslynSentinelServiceExtensionsBasic"/>), kept out of the filter lambda so tests can drive it without
/// building an McpRequestContext. Public rather than internal because Server.Advanced has no InternalsVisibleTo for its tests.
/// </summary>
public static class AgentIdentityCapture
{
    /// <summary>Argument key holding the caller's agent id.</summary>
    public const string AgentIdKey = "_agentId";

    /// <summary>Argument key holding the caller's agent type.</summary>
    public const string AgentTypeKey = "_agentType";

    /// <summary>Argument key holding the host session id.</summary>
    public const string SessionIdKey = "_sessionId";

    // Tools whose not-found error can carry an evidence note (NotFoundEvidence): the argument naming what the call looks for in filePath. An unlisted tool gets no note.
    private static readonly Dictionary<string, string> MissingTermArgument = new(StringComparer.Ordinal)
    {
        ["GetMethodSource"] = "methodName",
        ["ReadFile"] = "memberName",
        ["Member"] = "memberName",
        ["MethodSignature"] = "methodName",
        ["ReplaceSnippet"] = "oldContent",
        ["ChangeAccessibility"] = "targetName",
        ["ModifyAttribute"] = "targetName",
        ["ModifyModifier"] = "targetName",
        ["ModifyEnum"] = "enumName",
    };

    // Symbol tools with no file to search: the argument naming the symbol, read only for the edit effect note (EditEffectEvidence). They
    // set MissingTerm but not TargetFilePath, so NotFoundEvidence (which needs a file) behaves as before for them.
    // TODO InspectSymbol has no name argument (filePath + contextSnippet only), so it gets no effect note until a name can be taken from the snippet
    // TODO MoveMember's memberNames is an array and is not read; only className (a renamed class) can match
    // TODO Search symbol mode returns zero rows as success, not an error, so its effect note never reaches the not-found path
    private static readonly Dictionary<string, string> SymbolTermArgument = new(StringComparer.Ordinal)
    {
        ["LocateSymbol"] = "symbolName",
        ["RenameSymbol"] = "docCommentId",
        ["MoveMember"] = "className",
        ["Search"] = "query",
    };

    /// <summary>
    /// Removes the identity keys from <paramref name="arguments"/>, latches the operating mode and sets
    /// <see cref="ToolCallContext.Current"/> for this call. Never throws: this runs outside the echo stamp and the
    /// telemetry count, so a failure here would surface unstamped and uncounted. On failure the call runs with a null context.
    /// </summary>
    public static void Enter(
        string? toolName,
        IDictionary<string, JsonElement>? arguments,
        Func<long> currentSeq,
        Func<string?>? solutionRoot = null,
        Func<EditEffectIndex?>? editEffects = null,
        Func<Microsoft.CodeAnalysis.Solution?>? currentSolution = null)
    {
        try
        {
            // Keys first, so a later failure here still leaves arguments the validation filter accepts.
            var agentId = TakeString(arguments, AgentIdKey);
            var agentType = TakeString(arguments, AgentTypeKey);
            var sessionId = TakeString(arguments, SessionIdKey);
            AgentOperatingModeOptions.ResolveOnce(agentId is not null);
            var fileTerm = toolName is not null && MissingTermArgument.TryGetValue(toolName, out var termArgument) ? ReadString(arguments, termArgument) : null;
            // Search names a symbol only in symbol mode; its text and declaration-listing modes put a pattern or filter in query.
            var symbolTerm = fileTerm is null && toolName is not null && SymbolTermArgument.TryGetValue(toolName, out var symbolArgument)
                && (toolName != "Search" || string.Equals(ReadString(arguments, "mode"), "symbol", StringComparison.OrdinalIgnoreCase))
                ? ReadString(arguments, symbolArgument)
                : null;
            var term = fileTerm ?? symbolTerm;
            ToolCallContext.Set(new ToolCallContext(agentId, agentType, toolName ?? string.Empty, ReadString(arguments, "reason"), currentSeq())
            {
                SessionId = sessionId,
                TargetFilePath = fileTerm is null ? null : ReadString(arguments, "filePath"),
                MissingTerm = term,
                SolutionRoot = term is null ? null : TryGetSolutionRoot(solutionRoot),
                EditEffects = term is null ? null : TryGet(editEffects),
                CurrentSolution = term is null ? null : currentSolution,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Agent identity capture failed: {ex}");
            ToolCallContext.Set(null);
        }
    }

    // Guarded on its own: a failed lookup must cost only the not-found note, not the caller's identity.
    private static string? TryGetSolutionRoot(Func<string?>? solutionRoot)
    {
        try
        {
            return solutionRoot?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Solution root lookup failed: {ex}");
            return null;
        }
    }

    // Guarded on its own, like the solution root: a failed service lookup must cost only the effect note.
    private static T? TryGet<T>(Func<T?>? get) where T : class
    {
        try
        {
            return get?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Service lookup for the call context failed: {ex}");
            return null;
        }
    }

    private static string? TakeString(IDictionary<string, JsonElement>? arguments, string key)
    {
        var value = ReadString(arguments, key);
        arguments?.Remove(key);
        return value;
    }

    private static string? ReadString(IDictionary<string, JsonElement>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString() is { Length: > 0 } text ? text : null;
    }
}

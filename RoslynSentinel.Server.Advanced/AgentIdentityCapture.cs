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
    // TODO a file miss with no mapped term (GetFileOutline, ReadFile without memberName) gets no TargetFilePath/EditEffects, so the FileDeleted/FileMoved fact (EditEffectEvidence.FileFactForCurrentCall) never reaches it; capture TargetFilePath and the index for file tools whenever filePath is present
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
    // MoveMember is not in this map: its term is chosen from className and memberNames by MoveMemberTerm.
    // TODO Search symbol mode returns zero rows as success, not an error, so its effect note never reaches the not-found path
    private static readonly Dictionary<string, string> SymbolTermArgument = new(StringComparer.Ordinal)
    {
        ["LocateSymbol"] = "symbolName",
        ["RenameSymbol"] = "docCommentId",
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
            var symbolTerm = fileTerm is null && toolName == "MoveMember"
                ? MoveMemberTerm(arguments, TryGet(editEffects), sessionId)
                : fileTerm is null && toolName is not null && SymbolTermArgument.TryGetValue(toolName, out var symbolArgument)
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

    // MoveMember names several members and the class they leave, but the context carries one term. Candidates, most specific first: each
    // member qualified by className (a recorded effect's display is container-qualified, so className acts as the container qualifier of
    // EditEffectEvidence's matching), then className alone (a renamed class). The first candidate with a recorded effect for this caller's
    // session or server instance wins; with none (or no index) the first candidate is used, so the term is stable.
    private static string? MoveMemberTerm(IDictionary<string, JsonElement>? arguments, EditEffectIndex? index, string? sessionId)
    {
        var className = ReadString(arguments, "className");
        var candidates = ReadStringArray(arguments, "memberNames").Select(m => className is null ? m : $"{className}.{m}").ToList();
        if (className is not null)
        {
            candidates.Add(className);
        }

        if (index is not null)
        {
            foreach (var candidate in candidates)
            {
                if (EditEffectEvidence.HasEffectFor(index, candidate, sessionId, ServerInstanceIdentity.Id))
                {
                    return candidate;
                }
            }
        }

        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static List<string> ReadStringArray(IDictionary<string, JsonElement>? arguments, string key)
    {
        var names = new List<string>();
        if (arguments is not null && arguments.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
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

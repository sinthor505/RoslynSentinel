using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// Routes a <see cref="FailureReason"/> to a pre-filled <see cref="ToolHint"/> via <see cref="ToolGraph"/> (spec §4).
/// A null return from <see cref="Route"/> is a loud diagnostic signal — it means the reason has no
/// registered producer, manifesting the <c>[RequiresExternalInput]</c> debt rather than swallowing the gap.
/// </summary>
public sealed class FailureRouter
{
    private readonly ToolGraph _graph;

    public FailureRouter(ToolGraph graph)
    {
        _graph = graph;
    }

    /// <summary>
    /// Returns a pre-filled <see cref="ToolHint"/> for the given failure reason, or null when:
    /// <list type="bullet">
    ///   <item>the reason maps to no <see cref="DataTag"/>, or</item>
    ///   <item>no tool in the graph produces that tag.</item>
    /// </list>
    /// Null is intentionally loud — callers must surface it in <see cref="ItemFailure.SuggestedTool"/>.
    /// </summary>
    public ToolHint? Route(FailureReason reason, ItemContext ctx)
    {
        DataTag? needed = MapToUnsatisfiedTag(reason);
        if (needed is null)
        {
            return null;
        }

        IReadOnlyList<ToolDescriptor> producers = _graph.ProducersOf(needed.Value);
        if (producers.Count == 0)
        {
            return null;
        }

        ToolDescriptor target = SelectPreferred(producers);
        Dictionary<string, string> prefilled = BuildPrefilled(target, ctx);
        List<string> requires = ResidualParams(target, prefilled);

        return new ToolHint
        {
            ToolName = target.Name,
            PrefilledArgs = prefilled,
            RequiresFromModel = requires,
            Rationale = DescribeReason(reason),
        };
    }

    // ── Tag mapping ────────────────────────────────────────────────────────────

    private static DataTag? MapToUnsatisfiedTag(FailureReason reason)
    {
        switch (reason)
        {
            case FailureReason.OverloadAlreadyExists:
            {
                return DataTag.CancellationTokenSlot;
            }
            case FailureReason.SymbolNotResolved:
            {
                return DataTag.SymbolHandle;
            }
            case FailureReason.NoAsyncEquivalent:
            {
                return null;
            }
            case FailureReason.CompilerErrorAfterTransform:
            {
                return null;
            }
            case FailureReason.PreconditionMissing:
            {
                return null;
            }
            default:
            {
                return null;
            }
        }
    }

    // ── Producer selection — deterministic across restarts (spec §4.1) ─────────

    private static ToolDescriptor SelectPreferred(IReadOnlyList<ToolDescriptor> producers)
    {
        ToolDescriptor best = producers[0];
        for (int i = 1; i < producers.Count; i++)
        {
            ToolDescriptor candidate = producers[i];
            if (candidate.PreferenceWeight > best.PreferenceWeight)
            {
                best = candidate;
            }
            else if (candidate.PreferenceWeight == best.PreferenceWeight &&
                     string.Compare(candidate.Name, best.Name, StringComparison.Ordinal) < 0)
            {
                best = candidate;
            }
        }
        return best;
    }

    // ── Argument pre-fill — spec §4.2 ─────────────────────────────────────────

    private static Dictionary<string, string> BuildPrefilled(ToolDescriptor target, ItemContext ctx)
    {
        Dictionary<string, string> result = new Dictionary<string, string>();

        foreach (string paramName in target.AllParameterNames)
        {
            switch (paramName)
            {
                case "filePath":
                {
                    if (!string.IsNullOrEmpty(ctx.FilePath))
                    {
                        result["filePath"] = ctx.FilePath;
                    }
                    break;
                }
                case "methodName":
                {
                    if (!string.IsNullOrEmpty(ctx.MethodName))
                    {
                        result["methodName"] = ctx.MethodName;
                    }
                    break;
                }
                case "projectName":
                {
                    if (!string.IsNullOrEmpty(ctx.ProjectName))
                    {
                        result["projectName"] = ctx.ProjectName;
                    }
                    break;
                }
                case "changeId":
                {
                    if (!string.IsNullOrEmpty(ctx.ChangeId))
                    {
                        result["changeId"] = ctx.ChangeId;
                    }
                    break;
                }
                case "targets":
                {
                    if (!string.IsNullOrEmpty(ctx.FilePath))
                    {
                        string json = string.IsNullOrEmpty(ctx.MethodName)
                            ? $"[{{\"FilePath\":{JsonSerializer.Serialize(ctx.FilePath)}}}]"
                            : $"[{{\"FilePath\":{JsonSerializer.Serialize(ctx.FilePath)},\"MethodNames\":[{JsonSerializer.Serialize(ctx.MethodName)}]}}]";
                        result["targets"] = json;
                    }
                    break;
                }
            }
        }

        return result;
    }

    private static List<string> ResidualParams(ToolDescriptor target, Dictionary<string, string> prefilled)
    {
        List<string> residual = new List<string>();
        foreach (string paramName in target.RequiredParameterNames)
        {
            if (!prefilled.ContainsKey(paramName))
            {
                residual.Add(paramName);
            }
        }
        return residual;
    }

    // ── Human-readable rationale ───────────────────────────────────────────────

    private static string DescribeReason(FailureReason reason)
    {
        switch (reason)
        {
            case FailureReason.OverloadAlreadyExists:
            {
                return "An async overload already exists but lacks a CancellationToken parameter; add CT to make it compatible with the uplift.";
            }
            case FailureReason.SymbolNotResolved:
            {
                return "The symbol could not be resolved; locate it via a symbol lookup tool.";
            }
            case FailureReason.NoAsyncEquivalent:
            {
                return "No async equivalent exists for the synchronous call; manual intervention required.";
            }
            case FailureReason.CompilerErrorAfterTransform:
            {
                return "The transform produced compiler errors; manual review and correction required.";
            }
            case FailureReason.PreconditionMissing:
            {
                return "A required precondition was not met; resolve it before retrying.";
            }
            default:
            {
                return "An unknown failure occurred.";
            }
        }
    }
}

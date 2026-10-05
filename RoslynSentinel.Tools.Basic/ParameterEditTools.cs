using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// <c>ParameterEdit</c>: one tool for adding, removing or viewing parameters, merging MethodSignature and
/// ConstructorParameter behind a closed <see cref="ParameterEditOperation"/> enum.
/// Opt-in and additive: exposed only in claude-lean, through the <c>declarations</c> toolset
/// (see ToolsetCatalog and ServiceRegistrationExtensionsBasic); the two original tools are untouched and stay
/// available in the other modes. Each operation delegates to the same Impl method its original tool calls, so
/// behavior is identical. proposal_reduce_tool_schema_token_cost.md, Step 4a.
/// </summary>
[McpServerToolType]
public class ParameterEditTools
{
    private readonly RefactoringSignatureImpl _signature;

    public ParameterEditTools(RefactoringSignatureImpl signature)
    {
        _signature = signature;
    }

    // CONDITIONAL-PARAM-REVIEW-REQUIRED: the required-param set depends on 'operation' and 'action' (documented once on them);
    // the checks below turn a wrong subset into an InvalidArgument naming the missing/unexpected params.
    [McpServerTool(Name = "ParameterEdit")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view parameters of a method (operation method, any method) or of a class's DI constructor (operation constructor, with a backing field). " +
        "For overloaded methods or same-named classes in one file, combine the name with contextSnippet/lineBefore/lineAfter. Returns changeId.")]
    public Task<SentinelCallToolResult<object>> ParameterEdit(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Required params per operation. " +
            "method: filePath+methodName+action; remove removes only the LAST parameter, so paramName must match it; positional call sites are updated, but named-argument call sites make it refuse. " +
            "constructor: filePath+className+action; add creates a private readonly field, parameter and body assignment (and the constructor if none exists); " +
            "remove deletes the parameter and its assignment, and the backing field only if nothing else in the class uses it; fieldName and callSiteFixups are constructor-only.")]
        [Consumes(DataTag.Action, required: true)] ParameterEditOperation operation,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("add needs paramName+paramType and appends the parameter; remove needs paramName; view lists the parameters (method: name, type, default; constructor: parameters and backing fields).")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction action,
        [Description("method: the method to change.")]
        [Consumes(DataTag.MethodName, required: false)] string? methodName = null,
        [Description("constructor: the class whose constructor to change.")]
        [Consumes(DataTag.ClassName, required: false)] string? className = null,
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
        [Consumes(DataTag.DataType, required: false)] string? paramType = null,
        [Description("constructor, add only. Overrides the derived field name (_camelCase); paramName and its underscore-prefixed form both resolve to '_paramName'.")]
        [Consumes(DataTag.SymbolName, required: false)] string? fieldName = null,
        [Description("add only. Default value for the new parameter (e.g. \"3\"); omit for a required parameter. To default to null use nullDefault:true, not the string \"null\". Mutually exclusive with nullDefault.")]
        [ExternalInputRequired(DataTag.Initializer, required: false)] string? defaultValue = null,
        [Description("add only. Sets the new parameter's default to the null literal. Mutually exclusive with defaultValue.")] bool nullDefault = false,
        [Description("constructor, add only. Argument expression per existing constructor call site (new, target-typed new, `: this(...)`, `: base(...)`), so the parameter can be required instead of defaulted. " +
            "Key: \"FilePath:Line\" (full path, 1-based line), \"FilePath:*\" (every call site in that file) or \"*\" (every call site); the most specific key wins. " +
            "A site with no matching key falls back to defaultValue/nullDefault, else the call is rejected listing each unresolved site's key.")] Dictionary<string, string>? callSiteFixups = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        switch (operation)
        {
            case ParameterEditOperation.method:
            {
                var rejected = RejectForeignParams("method", ("className", className is not null), ("fieldName", fieldName is not null), ("callSiteFixups", callSiteFixups is not null));
                if (rejected is not null)
                {
                    return rejected;
                }

                if (string.IsNullOrEmpty(methodName))
                {
                    return InvalidArgument("operation 'method' requires 'methodName'.");
                }

                var missing = MissingForAction(action, paramName, paramType);
                if (missing is not null)
                {
                    return InvalidArgument($"operation 'method', action '{action}' requires {missing}.");
                }

                return _signature.MethodSignature(reason, filePath, action, methodName, paramName, paramType, defaultValue, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken, nullDefault);
            }

            case ParameterEditOperation.constructor:
            {
                var rejected = RejectForeignParams("constructor", ("methodName", methodName is not null));
                if (rejected is not null)
                {
                    return rejected;
                }

                if (string.IsNullOrEmpty(className))
                {
                    return InvalidArgument("operation 'constructor' requires 'className'.");
                }

                var missing = MissingForAction(action, paramName, paramType);
                if (missing is not null)
                {
                    return InvalidArgument($"operation 'constructor', action '{action}' requires {missing}.");
                }

                return _signature.ConstructorParameter(reason, filePath, action, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken, defaultValue, nullDefault, callSiteFixups);
            }

            default:
                return InvalidArgument($"Unhandled operation '{operation}'. Valid values: {string.Join(", ", Enum.GetNames<ParameterEditOperation>())}.");
        }
    }

    // add needs paramName and paramType, remove needs paramName, view needs neither; null when nothing is missing.
    private static string? MissingForAction(AddRemoveViewAction action, string? paramName, string? paramType)
    {
        bool needsName = action != AddRemoveViewAction.view && string.IsNullOrEmpty(paramName);
        bool needsType = action == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType);
        return (needsName, needsType) switch
        {
            (true, true) => "'paramName' and 'paramType'",
            (true, false) => "'paramName'",
            (false, true) => "'paramType'",
            _ => null,
        };
    }

    // Names every supplied param that belongs to the other operation, so a wrong subset fails before delegation.
    private static Task<SentinelCallToolResult<object>>? RejectForeignParams(string operationName, params (string Name, bool Present)[] foreign)
    {
        var present = foreign.Where(p => p.Present).Select(p => $"'{p.Name}'").ToList();
        return present.Count == 0
            ? null
            : InvalidArgument($"operation '{operationName}' does not take {string.Join(", ", present)}; see the 'operation' description for the params each operation takes.");
    }

    private static Task<SentinelCallToolResult<object>> InvalidArgument(string message) =>
        Task.FromResult(new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ParameterEdit: {message}"),
        });
}

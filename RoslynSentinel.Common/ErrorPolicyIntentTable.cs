using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>Whether a tool call (or one operation of a dispatch tool) can change state or only reads it.</summary>
public enum ErrorPolicyIntent
{
    /// <summary>Reads, searches, inspects, previews, validates, builds or runs tests; mutates no workspace file.</summary>
    Lookup,

    /// <summary>Can mutate a file (C# or other), git state, or server/workspace state (unload, toggles).</summary>
    Edit,
}

/// <summary>
/// Static tool-name to <see cref="ErrorPolicyIntent"/> table behind <see cref="ErrorPolicyClassifier.Classify"/>.
/// Plain tools map to one intent. A dispatch tool maps one selector parameter (operation, action or mode) to a
/// per-value intent, with a fallback used when the argument is missing, null, not a string or not a listed value.
/// The table is hand-written and alphabetical on purpose; a drift test (ErrorPolicyIntentTableTests) fails when a
/// tool or a selector value is added without an entry here. Classification rule: Edit when the tool or operation
/// can mutate files, git state or server/workspace state; Lookup otherwise.
/// </summary>
public static class ErrorPolicyIntentTable
{
    // Selector is null for a plain tool (Intent is its fixed intent); for a dispatch tool Intent is the
    // fallback and Values maps selector value -> intent. Ignored lists further enum selectors of the same tool
    // that do not change the intent (the drift test requires every enum selector to be primary or listed here).
    private const ErrorPolicyIntent L = ErrorPolicyIntent.Lookup;
    private const ErrorPolicyIntent E = ErrorPolicyIntent.Edit;

    private static readonly Dictionary<string, (string? Selector, ErrorPolicyIntent Intent, IReadOnlyDictionary<string, ErrorPolicyIntent>? Values, string[] Ignored)> Table = new(StringComparer.Ordinal)
    {
        ["AddCancellationToken"] = Fixed(E),
        ["AnalyzeForeachForLinqConversion"] = Fixed(L),
        ["AnalyzeMethod"] = Fixed(L),
        ["AnalyzeSwitchForPatternConversion"] = Fixed(L),
        ["ApplyClassCodeTransform"] = Fixed(E),
        ["ApplyDiff"] = Dispatch("action", E, lookup: "validate", edit: "apply"),
        ["ApplyFileCodeTransform"] = Fixed(E),
        ["ApplyMethodCodeTransform"] = Fixed(E),
        ["ApplyUnifiedDiff"] = Dispatch("action", E, lookup: "validate", edit: "apply"),
        ["Asyncify"] = Fixed(E),
        ["AsyncifyLoop"] = Fixed(E),
        ["BridgeAsyncMethods"] = Fixed(E),
        ["Build"] = Fixed(L),
        ["BulkComment"] = Fixed(E),
        ["ChangeAccessibility"] = Fixed(E),
        ["ChangeSignature"] = Fixed(E),
        ["ClearAsyncMigrationCandidateFlags"] = Fixed(E),
        ["ConstructorParameter"] = Dispatch("operation", E, lookup: "view", edit: "add,remove"),
        ["ConvertAnonymousToNamed"] = Fixed(E),
        ["CreateFile"] = Fixed(E),
        ["CreateProject"] = Fixed(E),
        ["Declaration"] = Dispatch("operation", E, lookup: "", edit: "modifier,accessibility,attribute,baseType", ignored: "action"),
        ["DeleteFile"] = Fixed(E),
        ["DeleteUnusedSymbol"] = Fixed(E),
        ["DescribeAdvancedToolOptions"] = Fixed(L),
        ["DescribeScanDetectors"] = Fixed(L),
        ["EditSnippets"] = Dispatch("action", E, lookup: "validate", edit: "apply"),
        ["EventHandlersToAsync"] = Fixed(E),
        ["ExternalFileDrift"] = Dispatch("operation", E, lookup: "Status,List,LedgerList", edit: "Acknowledge,LedgerFix,LedgerRelease"),
        ["ExtractEventHandlers"] = Fixed(E),
        ["ExtractLocalVariable"] = Fixed(E),
        ["ExtractMembers"] = Fixed(E),
        ["ExtractMethod"] = Fixed(E),
        ["Features"] = Dispatch("action", E, lookup: "list,get", edit: "update"),
        ["FindReferences"] = Fixed(L),
        ["Generate"] = Fixed(E),
        ["GenerateClassesFromJson"] = Fixed(L),
        ["GenerateDefaultConfigJson"] = Fixed(L),
        ["GenerateHttpClient"] = Fixed(L),
        ["GetAsyncMigrationProgress"] = Fixed(L),
        ["GetAsyncMigrationState"] = Fixed(L),
        ["GetBestInsertionPoint"] = Fixed(L),
        ["GetCallGraph"] = Fixed(L),
        ["GetCodeInventory"] = Fixed(L),
        ["GetComprehensiveHealthReport"] = Fixed(L),
        ["GetDiagnostics"] = Fixed(L),
        ["GetDiRegistrations"] = Fixed(L),
        ["GetFileOutline"] = Fixed(L),
        ["GetLargeResult"] = Fixed(L),
        ["GetMethodComplexity"] = Fixed(L),
        ["GetMethodSource"] = Fixed(L),
        ["GetMutationBreakerStatus"] = Fixed(L),
        ["GetOperationDetail"] = Fixed(L),
        ["GetPublicApiSurface"] = Fixed(L),
        ["GetSolutionMetrics"] = Fixed(L),
        ["GetTestCoverageMap"] = Fixed(L),
        ["GetTypeInfo"] = Fixed(L),
        ["GetWorkspaceHealth"] = Fixed(L),
        ["Git"] = Dispatch("operation", E, lookup: "status,log,diff,show,hunks", edit: "stage,add,unstage,commit,revert,reset,abort", ignored: "mode"),
        ["GitRemote"] = Dispatch("operation", E, lookup: "", edit: "branch,checkout,push,fetch,pull,tag,stash,worktree,config", ignored: "action"),
        ["Inline"] = Fixed(E),
        ["InlineClass"] = Fixed(E),
        ["InspectSymbol"] = Fixed(L),
        ["InterpolateString"] = Fixed(E),
        ["Introduce"] = Fixed(E),
        ["IntroduceParameterObject"] = Fixed(E),
        ["InvertAssignments"] = Fixed(E),
        ["InvertBooleanLogic"] = Fixed(E),
        ["ListAll"] = Fixed(L),
        ["ListProjectFrameworkTargets"] = Fixed(L),
        ["ListSolutionItems"] = Fixed(L),
        ["ListWorkspaceSolutions"] = Fixed(L),
        ["LoadSolution"] = Fixed(E),
        ["LocateSymbol"] = Fixed(L),
        ["McpServerControl"] = Dispatch("operation", E, lookup: "GetServerStatus,ListTestInstances", edit: "StopServer,LaunchTestInstance,StopTestInstance"),
        ["McpServerStatus"] = Fixed(L),
        ["McpToolsetControl"] = Fixed(E),
        ["Member"] = Dispatch("operation", E, lookup: "view", edit: "addMember,addTopLevelType,remove,replace"),
        ["MethodSignature"] = Dispatch("operation", E, lookup: "view", edit: "add,remove"),
        ["ModifyAttribute"] = Dispatch("action", E, lookup: "", edit: "add,replace,remove"),
        ["ModifyBaseType"] = Dispatch("action", E, lookup: "", edit: "add,remove"),
        ["ModifyEnum"] = Fixed(E),
        ["ModifyModifier"] = Dispatch("action", E, lookup: "", edit: "add,remove"),
        ["MoveAllTypesToFiles"] = Fixed(E),
        ["MoveMember"] = Fixed(E),
        ["MoveType"] = Fixed(E),
        ["NamedArguments"] = Dispatch("mode", E, lookup: "preview", edit: "apply"),
        ["ParameterEdit"] = Dispatch("action", E, lookup: "view", edit: "add,remove", ignored: "operation"),
        ["PreviewMoveFileToNamespaceFolder"] = Fixed(L),
        ["PreviewRenameImpact"] = Fixed(L),
        ["ProjectDoc"] = Dispatch("action", E, lookup: "read,list,trailers,validate", edit: "write,append,close"),
        ["PropagateCancellationToken"] = Fixed(E),
        ["QualifyCrefs"] = Fixed(E),
        ["QuerySymbolRelationships"] = Fixed(L),
        ["ReadFile"] = Fixed(L),
        ["RenameSymbol"] = Fixed(E),
        ["ReplaceSnippet"] = Dispatch("action", E, lookup: "validate", edit: "apply"),
        ["ResetAsyncMigrationState"] = Fixed(E),
        ["ResetMutationBreaker"] = Fixed(E),
        ["RetryFailedChanges"] = Fixed(E),
        ["RunScanDetector"] = Fixed(L),
        ["RunTest"] = Fixed(L),
        ["ScanAsyncMigrationCandidates"] = Fixed(E),
        ["ScanBreakingChanges"] = Fixed(L),
        ["ScanDuplicateBlocksInClass"] = Fixed(L),
        ["Search"] = Dispatch("mode", L, lookup: "text,symbol,references,callers,implementations,attribute,all,namespace,class,interface,method,property,struct,record,enum,enumMember,constructor,field", edit: ""),
        ["SemanticFindReplace"] = Dispatch("mode", E, lookup: "preview", edit: "apply", ignored: "operation"),
        ["SplitProjectByFolder"] = Fixed(E),
        ["SubAgent"] = Fixed(E),
        ["SubAgentEval"] = Fixed(E),
        ["SummaryComment"] = Dispatch("operation", E, lookup: "view", edit: "add,remove"),
        ["SyncInterface"] = Dispatch("action", E, lookup: "verify", edit: "implement,sync"),
        ["SyncTypeAndFilename"] = Fixed(E),
        ["TagTestCategories"] = Fixed(E),
        ["TraceVariableLifetime"] = Fixed(L),
        ["TypedMember"] = Fixed(E),
        ["UndoLastApply"] = Fixed(E),
        ["UpliftCallers"] = Fixed(E),
        ["UsingDirective"] = Dispatch("operation", E, lookup: "view", edit: "add,remove"),
        ["WrapRange"] = Fixed(E),
        ["WriteFile"] = Dispatch("operation", E, lookup: "", edit: "CreateFile,ReplaceFile,Create,Replace"),
    };

    /// <summary>Every tool name the table classifies, in table order.</summary>
    public static IReadOnlyCollection<string> ToolNames => Table.Keys;

    /// <summary>
    /// Describes a dispatch tool: the selector parameter name, the selector value to intent map (case-insensitive)
    /// and the other enum selectors that are deliberately not used. Returns false for a plain or unknown tool.
    /// </summary>
    public static bool TryGetDispatch(
        string toolName,
        out string selector,
        out IReadOnlyDictionary<string, ErrorPolicyIntent> values,
        out IReadOnlyCollection<string> ignoredSelectors)
    {
        if (Table.TryGetValue(toolName, out var row) && row.Selector is not null && row.Values is not null)
        {
            selector = row.Selector;
            values = row.Values;
            ignoredSelectors = row.Ignored;
            return true;
        }

        selector = string.Empty;
        values = new Dictionary<string, ErrorPolicyIntent>();
        ignoredSelectors = Array.Empty<string>();
        return false;
    }

    /// <summary>
    /// Returns the intent of a tool call, or null for a tool the table does not know. A dispatch tool reads its
    /// selector argument (name matched case-insensitively, value matched case-insensitively as a string) and falls
    /// back to the tool's default when the argument is missing, null or not a listed value.
    /// </summary>
    public static ErrorPolicyIntent? Resolve(string toolName, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (!Table.TryGetValue(toolName, out var row))
        {
            return null;
        }

        if (row.Selector is null || row.Values is null || arguments is null)
        {
            return row.Intent;
        }

        foreach (var argument in arguments)
        {
            if (!string.Equals(argument.Key, row.Selector, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // TODO error-policy: a numeric selector (enum ordinal) falls back to the tool default; only string values are matched
            if (argument.Value.ValueKind == JsonValueKind.String
                && argument.Value.GetString() is { } text
                && row.Values.TryGetValue(text.Trim(), out var intent))
            {
                return intent;
            }

            break;
        }

        return row.Intent;
    }

    private static (string? Selector, ErrorPolicyIntent Intent, IReadOnlyDictionary<string, ErrorPolicyIntent>? Values, string[] Ignored) Fixed(ErrorPolicyIntent intent) =>
        (null, intent, null, Array.Empty<string>());

    private static (string? Selector, ErrorPolicyIntent Intent, IReadOnlyDictionary<string, ErrorPolicyIntent>? Values, string[] Ignored) Dispatch(
        string selector,
        ErrorPolicyIntent fallback,
        string lookup,
        string edit,
        string ignored = "")
    {
        var values = new Dictionary<string, ErrorPolicyIntent>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in Split(lookup))
        {
            values[value] = ErrorPolicyIntent.Lookup;
        }

        foreach (var value in Split(edit))
        {
            values[value] = ErrorPolicyIntent.Edit;
        }

        return (selector, fallback, values, Split(ignored).ToArray());
    }

    private static IEnumerable<string> Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

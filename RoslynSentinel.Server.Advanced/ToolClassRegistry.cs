using System.Reflection;
using ModelContextProtocol.Server;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Canonical mode-name -> tool-class-name mapping, one entry per <c>[McpServerToolType]</c>
/// class. This is the single source of truth for which classes a given <c>--mode</c> value
/// activates; <c>--include-tools</c>/<c>--exclude-tools</c> then adjust the resulting class set
/// by name (see <see cref="ServerStartupHelpers.ResolveActiveToolClasses"/>).
/// </summary>
public static class ToolClassRegistry
{
    /// <summary>
    /// Name of the opt-in lean Claude toolset (proposal_reduce_tool_schema_token_cost.md, Step 4b,
    /// "Core"). Unlike every other mode it also applies a per-tool allow-list
    /// (<see cref="ClaudeLeanToolNames"/>), because the classes holding the Core tools also hold
    /// tools outside it. It is an exclusive mode: "all" never expands to it (see
    /// <see cref="ExclusiveModes"/>), and combining it with another mode narrows that mode's
    /// classes to the same allow-list.
    /// </summary>
    public const string ClaudeLeanMode = "claude-lean";

    /// <summary>
    /// Name of the opt-in mode that hosts <c>CodeTransformTools</c>: heuristic, single-file code transforms
    /// that have not earned a place in the default surface (proposal_expose_dormant_codetransform_engines.md).
    /// Advanced only. Opt-in: "all" never expands to it, so it costs no prompt tokens unless named.
    /// </summary>
    public const string ExperimentalMode = "Experimental";

    /// <summary>
    /// Modes that "--mode=all" must not expand to: claude-lean restricts rather than adds tools, and
    /// Experimental is opt-in only.
    /// </summary>
    public static readonly IReadOnlySet<string> ExclusiveModes =
        new HashSet<string>([ClaudeLeanMode, ExperimentalMode], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Modes that apply only when named explicitly (for example --mode=EditSnippetsExperiment); "all" skips them.
    /// They are not part of the default surface, so their tools cost no prompt tokens unless asked for.
    /// </summary>
    public static readonly IReadOnlySet<string> OptInModes =
        new HashSet<string>(["EditSnippetsExperiment"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The MCP tool names the <see cref="ClaudeLeanMode"/> mode exposes: the proposal's Core set minus
    /// FindReferencesTool and LocateSymbolTool (reachable via Search mode: references and mode: symbol, which saves
    /// their schema tokens) plus McpToolsetControlTool, which switches the on-demand toolsets (<see cref="ToolsetCatalog"/>) at runtime. SemanticFindReplace, InspectSymbol, MoveMember, UsingDirective and GetDiagnostics are core tools too.
    /// McpServerStatusTool is declared by ServerStatusTools, which is registered outside the class
    /// registry, and passes through the same allow-list.
    /// </summary>
    public static readonly IReadOnlySet<string> ClaudeLeanToolNames = new HashSet<string>(
        [
            "LoadSolution", "ReadFile", "GetFileOutline", "GetMethodSource", "GetLargeResult",
            "Search",
            "Build", "RunTest", "Git", "ReplaceSnippet", "ReplaceRange", "Member",
            "RenameSymbol", "ChangeSignature", "CreateFile", "DeleteFile", "MoveFile", "WriteFile",
            "UndoChange", "McpServerControl", "McpServerStatus", "ExternalFileDrift", "SemanticFindReplace", "InspectSymbol", "MoveMember", "UsingDirective", "GetDiagnostics",
            ToolsetCatalog.ControlToolName,
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Tool classes that declare at least one <see cref="ClaudeLeanToolNames"/> tool, including SubAgentTools (its SubAgent tool is on demand via the modelEval toolset),
    /// DeclarationTools, ParameterEditTools and SemanticFindReplaceTools, whose DeclarationTool and ParameterEditTool are on-demand only (the declarations toolset adds them) while SemanticFindReplaceTool is a core tool: the allow-list keeps
    /// them out of the startup surface and the <c>declarations</c> toolset adds them. Listed here so the class exists in claude-lean and McpServerStatusTool can name its mode.
    /// CoreRefactoringTools contributes ChangeSignature and MoveMember to the core set (MoveAllTypesToFiles stays on demand); AdvancedRefactoringTools' tools all stay on demand (the allow-list filters them out).
    /// </summary>
    private static readonly string[] ClaudeLeanToolClasses =
    [
        "WorkspaceTools", "WorkspaceReadNavigationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools",
        "RefactoringExtractionDocsTools", "RefactoringStructuralTools", "RefactoringSignatureTools",
        "CoreRefactoringTools", "AdvancedRefactoringTools", "AdminTools", "WholeFileWriteTools", "ToolsetControlTools", "DeclarationTools", "ParameterEditTools",
        "SemanticFindReplaceTools", "NamedArgumentsTools", "TestCategoryTaggingTools", "QualifyCrefsTools",
        "SubAgentTools",
    ];

    /// <summary>
    /// Tool classes whose dependencies claude-lean registers WITHOUT registering their tools (the per-tool
    /// allow-list filters every non-Core tool out at startup), so <c>McpToolsetControlTool</c> can build any on-demand
    /// tool later and have its class constructed. These are the tool classes of the "Claude" mode. Only claude-lean uses this.
    /// DeclarationTools and ParameterEditTools (the merged DeclarationTool and ParameterEditTool tools) are the entries that are not part
    /// of the "Claude" mode: they exist only in claude-lean, so their Impl dependencies are covered by the Structural/Signature entries above.
    /// </summary>
    public static readonly IReadOnlySet<string> ClaudeLeanOnDemandToolClasses = new HashSet<string>(
        [
            "WorkspaceTools", "WorkspaceReadNavigationTools", "DocumentationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools", "GitRemoteTools", "TypedMemberTools",
            "RefactoringExtractionDocsTools", "RefactoringStructuralTools", "RefactoringSignatureTools",
            "CoreRefactoringTools", "AdvancedRefactoringTools", "AdminTools", "WholeFileWriteTools", "DeclarationTools", "ParameterEditTools",
            "SemanticFindReplaceTools", "NamedArgumentsTools", "TestCategoryTaggingTools", "QualifyCrefsTools", "SubAgentTools",
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Tool classes registered by the Basic registration, in tools/list order. A new Tools.Basic tool class needs a row here
    /// (and a mode entry in <see cref="ModeToToolClasses"/>).
    /// </summary>
    public static readonly IReadOnlyList<Type> BasicToolClassTypes = [typeof(ServerStatusTools), typeof(ToolsetControlTools), typeof(DeclarationTools), typeof(ParameterEditTools), typeof(SemanticFindReplaceTools), typeof(NamedArgumentsTools), typeof(TestCategoryTaggingTools), typeof(QualifyCrefsTools), typeof(WorkspaceTools), typeof(EditSnippetsTools), typeof(WorkspaceReadNavigationTools), typeof(DocumentationTools), typeof(SymbolNavigationTools), typeof(SymbolRelationshipTools), typeof(GitTools), typeof(GitRemoteTools), typeof(AdminTools), typeof(WholeFileWriteTools), typeof(RefactoringSignatureTools), typeof(RefactoringStructuralTools), typeof(TypedMemberTools), typeof(RefactoringExtractionDocsTools), typeof(CoreRefactoringTools)];

    /// <summary>
    /// Tool classes registered by the Advanced registration, in tools/list order (after the Basic ones). A new Tools.Advanced or
    /// Tools.Experimental tool class needs a row here (and a mode entry in <see cref="ModeToToolClasses"/>).
    /// </summary>
    public static readonly IReadOnlyList<Type> AdvancedToolClassTypes =
    [
        typeof(IntelligenceTools), typeof(ScanTools), typeof(AdvancedRefactoringTools), typeof(ModernizationTools), typeof(QualityTools), typeof(GenerationTools), typeof(CommentingTools), typeof(CodeTransformTools), typeof(AsyncifyTools), typeof(SubAgentEvalTools), typeof(SubAgentTools),
    ];

    /// <summary>
    /// Tool classes selected only while claude-lean is active: outside it they are dropped from the startup surface even when
    /// named by --include-tools (see <see cref="SelectStartupToolTypes"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> ClaudeLeanOnlyToolClasses = new HashSet<string>(["ToolsetControlTools", "DeclarationTools", "ParameterEditTools"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tool classes selected in every launch; not registry classes, so never counted or excludable.
    /// </summary>
    public static readonly IReadOnlySet<string> AlwaysOnToolClasses = new HashSet<string>(["ServerStatusTools"], StringComparer.Ordinal);

    /// <summary>
    /// Returns the types of <paramref name="table"/>, in table order, whose name is in <see cref="AlwaysOnToolClasses"/>, or is in
    /// <paramref name="activeToolClasses"/> and is either not in <see cref="ClaudeLeanOnlyToolClasses"/> or <paramref name="claudeLean"/> is true.
    /// </summary>
    public static IReadOnlyList<Type> SelectStartupToolTypes(IReadOnlyList<Type> table, IReadOnlySet<string> activeToolClasses, bool claudeLean)
    {
        var selected = new List<Type>();
        foreach (var type in table)
        {
            if (AlwaysOnToolClasses.Contains(type.Name)
                || (activeToolClasses.Contains(type.Name) && (!ClaudeLeanOnlyToolClasses.Contains(type.Name) || claudeLean)))
            {
                selected.Add(type);
            }
        }

        return selected;
    }

    /// <summary>
    /// Reports problems in the mode table and the tool-class type tables, one line per problem, each naming the
    /// class and the fix. Empty when the tables agree. It does not detect a class in the wrong table (an Advanced
    /// class in BasicToolClassTypes would register in Basic hosts); the parity goldens catch that for the covered cases.
    /// </summary>
    public static IReadOnlyList<string> DescribeToolClassTableProblems(IReadOnlyDictionary<string, string[]> modeToToolClasses, IReadOnlyList<Type> basicTypes, IReadOnlyList<Type> advancedTypes)
    {
        var problems = new List<string>();

        var registryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var classes in modeToToolClasses.Values)
        {
            foreach (var className in classes)
            {
                registryNames.Add(className);
            }
        }

        var allTypes = new List<Type>(basicTypes);
        allTypes.AddRange(advancedTypes);

        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in allTypes)
        {
            tableNames.Add(type.Name);
        }

        var registryNameList = new List<string>(registryNames);
        registryNameList.Sort(StringComparer.Ordinal);
        foreach (var className in registryNameList)
        {
            if (!tableNames.Contains(className))
            {
                problems.Add($"Tool class '{className}' is in ModeToToolClasses but has no row: add typeof({className}) to BasicToolClassTypes or AdvancedToolClassTypes in ToolClassRegistry.cs.");
            }
        }

        var rowCounts = new Dictionary<Type, int>();
        foreach (var type in allTypes)
        {
            rowCounts[type] = rowCounts.TryGetValue(type, out var count) ? count + 1 : 1;
        }

        var duplicateTypes = new List<Type>();
        foreach (var pair in rowCounts)
        {
            if (pair.Value > 1)
            {
                duplicateTypes.Add(pair.Key);
            }
        }

        duplicateTypes.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        foreach (var type in duplicateTypes)
        {
            problems.Add($"Tool class '{type.Name}' has {rowCounts[type]} rows across BasicToolClassTypes and AdvancedToolClassTypes: keep exactly one.");
        }

        var distinctTypes = new List<Type>();
        var seenTypes = new HashSet<Type>();
        foreach (var type in allTypes)
        {
            if (seenTypes.Add(type))
            {
                distinctTypes.Add(type);
            }
        }

        foreach (var type in distinctTypes)
        {
            if (!registryNames.Contains(type.Name) && !AlwaysOnToolClasses.Contains(type.Name))
            {
                problems.Add($"Table type '{type.Name}' is not a ModeToToolClasses class and not always-on: add it to a mode in ModeToToolClasses or remove its row.");
            }
        }

        foreach (var type in distinctTypes)
        {
            var hasToolMethod = false;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                {
                    hasToolMethod = true;
                    break;
                }
            }

            if (!hasToolMethod)
            {
                problems.Add($"Table type '{type.Name}' declares no [McpServerTool] method: remove its row or add the tool method.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The one mode-name -> tool-class-name map, registered by <c>AddRoslynSentinelToolsAdvanced</c>
    /// (which delegates the Basic tool classes to <c>AddRoslynSentinelToolsBasic</c>). It is the union of
    /// the former separate Basic and Advanced maps: the Basic-only sub-modes (WorkspaceReadNav, ...)
    /// are kept, "Workspace" includes SymbolRelationshipTools, and "Refactor"
    /// includes the Advanced-only AdvancedRefactoringTools. A class name whose tool class a given
    /// registration method does not register is inert there because the registration method selects only the types of its own
    /// table (BasicToolClassTypes / AdvancedToolClassTypes) that are in the active set.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ModeToToolClasses =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Workspace"] = ["WorkspaceTools", "WorkspaceReadNavigationTools", "DocumentationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools", "GitRemoteTools"],
            ["Admin"] = ["AdminTools"],
            ["WholeFileWrite"] = ["WholeFileWriteTools"],
            ["Refactor"] = ["RefactoringExtractionDocsTools", "RefactoringStructuralTools", "TypedMemberTools", "RefactoringSignatureTools", "CoreRefactoringTools", "AdvancedRefactoringTools"],
            ["Intelligence"] = ["IntelligenceTools", "ScanTools"],
            ["Modernize"] = ["ModernizationTools"],
            ["Quality"] = ["QualityTools"],
            ["Generation"] = ["GenerationTools", "CommentingTools"],
            ["Asyncify"] = ["AsyncifyTools"],
            // Dedicated mode: the SubAgent tools spawn a model-driven child server and belong with none of
            // the concerns above. Activate via --mode=SubAgentEval (or --include-tools=SubAgentEvalTools).
            // Deliberately absent from "Claude": a child server must never expose them (see
            // SubAgentChildServerLauncher.ExcludedToolClasses).
            ["SubAgentEval"] = ["SubAgentEvalTools"],
            ["SubAgent"] = ["SubAgentTools"],
            ["NamedArguments"] = ["NamedArgumentsTools"],
            ["TestCategories"] = ["TestCategoryTaggingTools"],
            ["QualifyCrefs"] = ["QualifyCrefsTools"],
            ["Claude"] = ["WorkspaceTools", "WorkspaceReadNavigationTools", "DocumentationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools", "GitRemoteTools", "RefactoringExtractionDocsTools", "RefactoringStructuralTools", "TypedMemberTools", "RefactoringSignatureTools", "CoreRefactoringTools", "AdvancedRefactoringTools", "AdminTools", "WholeFileWriteTools"],
            ["SemanticFindReplace"] = ["SemanticFindReplaceTools"],
            [ClaudeLeanMode] = ClaudeLeanToolClasses,
            [ExperimentalMode] = ["CodeTransformTools"],
            // experiment-only (proposal_edit_snippets_batch_only_ab_test); opt-in, never part of all/Claude
            ["EditSnippetsExperiment"] = ["EditSnippetsTools"],

            // Fine-grained sub-modes (docs/current/plans/closed/plan_split_workspace_refactoring_tools_for_di.md):
            // independently opt-in-able, none require "Workspace"/"Refactor".
            ["WorkspaceReadNav"] = ["WorkspaceReadNavigationTools"],
            ["RefactorSignature"] = ["RefactoringSignatureTools"],
            ["RefactoringStructural"] = ["RefactoringStructuralTools", "TypedMemberTools"],
            ["RefactorExtractionDocs"] = ["RefactoringExtractionDocsTools"],
            ["SymbolNavigation"] = ["SymbolNavigationTools"],
            ["SymbolRelationship"] = ["SymbolRelationshipTools"],
        };

    /// <summary>
    /// Inverts the mode map into tool class name -> the mode names that enable it (no duplicates).
    /// Feeds McpServerStatusTool's per-tool
    /// <c>enabledBy</c> hint; "Claude"/"all"-style umbrella modes are listed like any other.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildClassToModes()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (mode, classes) in ModeToToolClasses)
        {
            foreach (var className in classes)
            {
                if (!result.TryGetValue(className, out var modes))
                {
                    modes = [];
                    result[className] = modes;
                }

                if (!modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
                {
                    modes.Add(mode);
                }
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }
}

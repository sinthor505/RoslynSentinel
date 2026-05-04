using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace RoslynSentinel.Server;

public class SentinelConfiguration
{
    private readonly ConcurrentDictionary<string, bool> _features = new(StringComparer.OrdinalIgnoreCase);

    public SentinelConfiguration()
    {
        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        // --- REFACTORING SUITE ---
        _features["ChangeSignature"] = true;
        _features["ConvertAbstractClassToInterface"] = true;
        _features["ConvertAnonymousToNamedType"] = true;
        _features["ConvertExtensionMethodToPlainStatic"] = true;
        _features["ConvertIndexerToMethod"] = true;
        _features["ConvertInterfaceToAbstractClass"] = true;
        _features["ConvertMethodToIndexer"] = true;
        _features["ConvertMethodToProperty"] = true;
        _features["ConvertPropertyToAutoProperty"] = true;
        _features["ConvertPropertyToMethod"] = true;
        _features["ConvertStaticToExtensionMethod"] = true;
        _features["CopyToGlobalUsing"] = true;
        _features["CopyType"] = true;
        _features["EncapsulateField"] = true;
        _features["ExtractClass"] = true;
        _features["ExtractInterface"] = true;
        _features["ExtractMethod"] = true;
        _features["ExtractSuperclass"] = true;
        _features["InlineClass"] = true;
        _features["InlineField"] = true;
        _features["InlineMethod"] = true;
        _features["InlineParameter"] = true;
        _features["InlineVariable"] = true;
        _features["IntroduceField"] = true;
        _features["IntroduceParameter"] = true;
        _features["IntroduceVariable"] = true;
        _features["InvertBoolean"] = true;
        _features["MakeMethodNonStatic"] = true;
        _features["MakeMethodStatic"] = true;
        _features["ExtractMembersToPartial"] = true;
        _features["MoveInstanceMethod"] = true;
        _features["MoveTypeToFile"] = true;
        _features["MoveTypeToNamespace"] = true;
        _features["MoveTypeToOuterScope"] = true;
        _features["PullMembersUp"] = true;
        _features["PushMembersDown"] = true;
        _features["Rename"] = true;
        _features["SafeDelete"] = true;
        _features["AddRemoveParams"] = true;
        _features["TransformParameters"] = true;
        _features["UseBaseTypeWherePossible"] = true;
        _features["ConvertExpressionBody"] = true;
        _features["ExtractConstant"] = true;
        _features["ExtractLocalVariable"] = true;
        _features["AnalyzeControlFlow"] = true;
        _features["AnalyzeDataFlow"] = true;

        // --- MODERNIZATION (.NET 8/9/10) ---
        _features["TimeProviderInjection"] = true;
        _features["ModernGuardClauses"] = true;
        _features["PrimaryConstructors"] = true;
        _features["CollectionExpressions"] = true;
        _features["LockModernization"] = true;
        _features["FieldBackedProperties"] = true;
        _features["ImplicitSpanCleanup"] = true;
        _features["UnboundNameof"] = true;
        _features["NullConditionalAssignment"] = true;
        _features["PatternMatching"] = true;
        _features["ThrowExpressions"] = true;
        _features["ClassToRecord"] = true;
        _features["RecordToClass"] = true;
        _features["IfToSwitch"] = true;
        _features["SimplifyVerbosity"] = true;
        _features["LengthMinusOneToIndex"] = true;

        // --- QUALITY & ANALYSIS (IDE/EPC RULES) ---
        _features["BoxingAllocation"] = true;
        _features["ReflectionUsage"] = true;
        _features["InefficientStringComparison"] = true;
        _features["AsyncVoidUsage"] = true;
        _features["EmptyCatchBlocks"] = true;
        _features["Deadlocks"] = true;
        _features["DuplicateMethods"] = true;
        _features["LargeTypes"] = true;
        _features["LargeMethods"] = true;
        _features["LargeSwitchStatements"] = true;
        _features["MemoryLeaks"] = true;
        _features["ResourceDisposal"] = true;
        _features["UnusedReferences"] = true;
        _features["InconsistentSql"] = true;
        _features["MultiTypeFile"] = true;
        _features["NameMismatch"] = true;
        _features["ThreadSafety"] = true;
        _features["TimeAbstraction"] = true;
        _features["RedundantTypeSpecification"] = true;
        _features["LongParameterLists"] = true;
        _features["RedundantCasts"] = true;
        _features["MismatchedAwait"] = true;
        _features["SemaphoreLeaks"] = true;
        _features["UninstantiatedTypes"] = true;
        _features["UnusedInterfaces"] = true;

        // --- IDE RULES ---
        _features["IDE0001"] = true; // Simplify name
        _features["IDE0005"] = true; // Remove unnecessary import
        _features["IDE0011"] = true; // Add braces
        _features["IDE0016"] = true; // Throw expression
        _features["IDE0028"] = true; // Collection initializers
        _features["IDE0032"] = true; // Auto property
        _features["IDE0035"] = true; // Remove unreachable code
        _features["IDE0044"] = true; // Add readonly
        _features["IDE0051"] = true; // Remove unused private member

        // --- EPC/ASYNC RULES ---
        _features["EPC14"] = true; // ConfigureAwait redundant
        _features["EPC15"] = true; // ConfigureAwait must be used
        _features["EPC27"] = true; // Avoid async void methods
        _features["EPC33"] = true; // Thread.Sleep in async
        _features["EPC35"] = true; // Block unnecessarily in async
    }

    public bool IsFeatureEnabled(string featureName)
    {
        return _features.TryGetValue(featureName, out var enabled) && enabled;
    }

    public void SetFeatureStatus(string featureName, bool enabled)
    {
        _features[featureName] = enabled;
    }

    public void BatchUpdateFeatureStatus(List<KeyValuePair<string, bool>> updates)
    {
        foreach (var update in updates)
        {
            _features[update.Key] = update.Value;
        }
    }

    public List<KeyValuePair<string, bool>> GetFeatureStatuses(List<string>? filter = null)
    {
        var source = filter == null || !filter.Any() 
            ? _features 
            : _features.Where(f => filter.Contains(f.Key, StringComparer.OrdinalIgnoreCase));

        return source.OrderBy(f => f.Key).ToList();
    }
}

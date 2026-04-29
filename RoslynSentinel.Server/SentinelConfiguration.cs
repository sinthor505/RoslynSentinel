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
        // --- Structural (ProjectStructureEngine) ---
        _features["MultiTypeFile"] = true;
        _features["NameMismatch"] = true;
        _features["NamespaceMismatch"] = true;
        
        // --- Modernization (ModernizationEngine, SyntaxUpgradeEngine, CodeStyleEngine) ---
        _features["TimeProviderInjection"] = true;
        _features["ModernGuardClauses"] = true;
        _features["ClassToRecord"] = true;
        _features["RecordToClass"] = true;
        _features["PrimaryConstructors"] = true;
        _features["CollectionExpressions"] = true;
        _features["LockModernization"] = true;
        _features["FieldBackedProperties"] = true;
        _features["ImplicitSpanCleanup"] = true;
        _features["UnboundNameof"] = true;
        _features["NullConditionalAssignment"] = true;
        _features["SimplifyVerbosity"] = true;
        _features["BooleanInversion"] = true;
        _features["IfToSwitch"] = true;
        _features["LoopConversion"] = true;
        _features["PatternMatching"] = true;
        _features["ThrowExpressions"] = true;

        // --- Quality & Performance (AnalysisEngine, PerformanceEngine) ---
        _features["BoxingAllocation"] = true;
        _features["ReflectionUsage"] = true;
        _features["InefficientStringComparison"] = true;
        _features["UnusedPrivateMembers"] = true;
        _features["UnusedLocalVariables"] = true;
        _features["UninstantiatedTypes"] = true;
        _features["UnusedInterfaces"] = true;
        _features["DuplicateMethods"] = true;
        _features["LongParameterLists"] = true;
        _features["LargeTypes"] = true;
        _features["LargeMethods"] = true;
        _features["LargeSwitchStatements"] = true;
        _features["RedundantCasts"] = true;
        _features["ResourceDisposal"] = true;
        _features["MemoryLeaks"] = true;
        
        // --- Safety (AsyncSafetyEngine) ---
        _features["AsyncVoidUsage"] = true;
        _features["MismatchedAwait"] = true;
        _features["EmptyCatchBlocks"] = true;
        _features["Deadlocks"] = true;
        _features["SemaphoreLeaks"] = true;
        _features["ThreadSafety"] = true; // Added
        _features["TimeAbstraction"] = true; // Added

        // --- Intelligence & Reports ---
        _features["SolutionHealthReport"] = true;
        _features["BlastRadiusAnalysis"] = true;
        _features["ProjectMetrics"] = true;
        _features["DependencyInconsistency"] = true;
        _features["UnusedReferences"] = true;
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

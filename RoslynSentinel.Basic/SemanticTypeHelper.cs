using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Basic;

/// <summary>
/// Shared helpers for type-identity checks using Roslyn semantic model symbols.
/// All methods accept null and return false, so callers can use them without null-guards.
/// </summary>
public static class SemanticTypeHelper
{
    /// <summary>
    /// Returns true if <paramref name="type"/> is Task, Task&lt;T&gt;, ValueTask, or ValueTask&lt;T&gt;.
    /// Uses OriginalDefinition so constructed generic types (Task&lt;int&gt;) match correctly.
    /// </summary>
    public static bool IsTaskOrValueTask(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        var def = (type as INamedTypeSymbol)?.OriginalDefinition ?? type;
        return def.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
            && def.Name is "Task" or "ValueTask";
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is System.Net.Http.HttpClient.
    /// </summary>
    public static bool IsHttpClient(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        return type.Name == "HttpClient"
            && type.ContainingNamespace?.ToDisplayString() == "System.Net.Http";
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is System.Threading.SemaphoreSlim.
    /// </summary>
    public static bool IsSemaphoreSlim(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        return type.Name == "SemaphoreSlim"
            && type.ContainingNamespace?.ToDisplayString() == "System.Threading";
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is a lazy, non-materialized sequence
    /// (IEnumerable&lt;T&gt;, IQueryable&lt;T&gt;, or a LINQ result type that implements IEnumerable).
    /// Materialized types (List, Array, HashSet, Dictionary, etc.) return false.
    /// </summary>
    public static bool IsNonMaterializedSequence(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        var def = (type as INamedTypeSymbol)?.OriginalDefinition ?? type;

        // Exclude materialized collection types
        if (def.Name is "List" or "Array" or "HashSet" or "SortedSet" or "Dictionary" or
                        "IList" or "ICollection" or "IReadOnlyList" or "IReadOnlyCollection" or
                        "ObservableCollection" or "ConcurrentBag" or "Queue" or "Stack" or
                        "ConcurrentQueue" or "ConcurrentStack" or "SortedList" or "SortedDictionary" or
                        "LinkedList" or "ImmutableArray" or "ImmutableList" or "FrozenSet")
        {
            return false;
        }

        // IEnumerable<T> or IQueryable<T> directly
        var ns = def.ContainingNamespace?.ToDisplayString();
        if (ns == "System.Collections.Generic" && def.Name == "IEnumerable")
        {
            return true;
        }

        if (ns == "System.Linq" && def.Name == "IQueryable")
        {
            return true;
        }

        // LINQ operator result types (e.g. WhereSelectArrayIterator, OrderedEnumerable) —
        // they have internal names but implement IEnumerable<T>
        if (type is INamedTypeSymbol named)
        {
            return named.AllInterfaces.Any(i =>
                i.OriginalDefinition.Name == "IEnumerable" &&
                i.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic");
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is System.Text.RegularExpressions.Regex.
    /// </summary>
    public static bool IsRegex(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        return type.Name == "Regex"
            && type.ContainingNamespace?.ToDisplayString() == "System.Text.RegularExpressions";
    }
}

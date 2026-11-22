using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>One top-level type (class, struct, interface, enum, record, record struct or delegate) declared in a file.</summary>
/// <param name="Kind">The declaration keyword: class, struct, interface, enum, record, record struct or delegate.</param>
/// <param name="Name">The type's identifier, without type parameters.</param>
/// <param name="HasStaticFields">True when the type, or a type nested in it, declares a static non-const field, so a copy would hold separate static state.</param>
public sealed record TopLevelType(string Kind, string Name, bool HasStaticFields);

/// <summary>
/// Outcome of <see cref="CopyAndObsoleteChecks.CheckAsync"/>: either the facts a copy-and-obsolete move builds on, or a refusal.
/// <see cref="Refusal"/> is null for an accepted file. <see cref="OldNamespace"/> and <see cref="NewNamespace"/> are null when the check
/// stopped before it could read them; the empty string is the global namespace.
/// </summary>
/// <param name="OldNamespace">The namespace the file's top-level types declare now.</param>
/// <param name="NewNamespace">The folder-derived namespace of the destination.</param>
/// <param name="Types">The file's top-level types in source order; empty on a refusal that stopped before reading them.</param>
/// <param name="Refusal">The refusal, with its error code and a facts-and-options message; null when accepted.</param>
public sealed record CopyAndObsoleteCheckResult(string? OldNamespace, string? NewNamespace, IReadOnlyList<TopLevelType> Types, ResultError? Refusal)
{
    /// <summary>True when the check refused the move.</summary>
    public bool IsRefusal => Refusal is not null;

    /// <summary>Names of the types with <see cref="TopLevelType.HasStaticFields"/> set, in source order; empty when none.</summary>
    public IReadOnlyList<string> StaticFieldTypeNames => Types.Where(type => type.HasStaticFields).Select(type => type.Name).ToList();

    /// <summary>A refusal that stopped before the namespaces and types were read.</summary>
    public static CopyAndObsoleteCheckResult Refused(string errorCode, string message) => new(null, null, [], new ResultError(errorCode, message));

    /// <summary>A refusal after the namespaces and types were read, so the facts stay available.</summary>
    public static CopyAndObsoleteCheckResult Refused(string? oldNamespace, string? newNamespace, IReadOnlyList<TopLevelType> types, string errorCode, string message) => new(oldNamespace, newNamespace, types, new ResultError(errorCode, message));
}

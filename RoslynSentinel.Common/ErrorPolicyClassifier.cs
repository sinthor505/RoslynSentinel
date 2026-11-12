using System;
using System.Collections.Generic;

namespace RoslynSentinel.Common;

/// <summary>The kind of target a missing-target error refers to, used to pick an error-policy key.</summary>
public enum ErrorPolicyTargetKind
{
    File,
    Symbol,
}

/// <summary>
/// Maps a tool error to the target kind it refers to. NotFound alone cannot classify: the same code is also
/// returned for non-code targets (statements, projects), project misses and unknown tools, so the kind passed
/// with the miss decides. Anything not listed here returns null (no policy key).
/// </summary>
public static class ErrorPolicyClassifier
{
    private static readonly HashSet<string> FileKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "document",
        "file",
    };

    // TODO error-policy: kind values passed to ToolNotFoundException as variables were not enumerated by the Step 1 inventory; unknown kinds map to null (no policy key)
    private static readonly HashSet<string> SymbolKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "class",
        "interface",
        "type",
        "record",
        "enum",
        "enum member",
        "method",
        "member",
        "declaration",
        "property",
        "field",
        "constructor",
        "parameter",
        "attribute",
        "namespace",
        "symbol",
        "container",
    };

    /// <summary>
    /// Returns the target kind for a tool error, or null when the error does not map to a policy key.
    /// The comparison is case-insensitive and the kind is trimmed before matching.
    /// </summary>
    public static ErrorPolicyTargetKind? MapKind(string? errorCode, string? missingTargetKind)
    {
        if (string.Equals(errorCode, "FileNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorPolicyTargetKind.File;
        }

        if (string.Equals(errorCode, "MethodNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorPolicyTargetKind.Symbol;
        }

        if (!string.Equals(errorCode, ToolErrorCode.NotFound, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = missingTargetKind?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            return null;
        }

        if (FileKinds.Contains(kind))
        {
            return ErrorPolicyTargetKind.File;
        }

        if (SymbolKinds.Contains(kind))
        {
            return ErrorPolicyTargetKind.Symbol;
        }

        return null;
    }
}

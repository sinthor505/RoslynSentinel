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

        // Kind values passed as literals to ToolNotFoundException and MemberRefactoringNotFound are guarded by ErrorPolicyKindRatchetTests.
        // Unknown kinds map to null (no policy key).
        // TODO error-policy: extend ErrorPolicyKindRatchetTests to the Engines.Advanced throw sites (not yet covered by the ratchet).
        var kind = missingTargetKind?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            return null;
        }

        if (MissingTargetKinds.FileKinds.Contains(kind))
        {
            return ErrorPolicyTargetKind.File;
        }

        if (MissingTargetKinds.SymbolKinds.Contains(kind))
        {
            return ErrorPolicyTargetKind.Symbol;
        }

        return null;
    }

    /// <summary>
    /// Returns the policy key for a tool error: target kind from <see cref="MapKind"/> crossed with the call's
    /// intent from <see cref="ErrorPolicyIntentTable.Resolve"/>. Null when either is unknown (unknown tool,
    /// non-not-found error, unrecognised kind), meaning no policy applies.
    /// </summary>
    public static ErrorPolicyKey? Classify(
        string toolName,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments,
        string? errorCode,
        string? missingTargetKind)
    {
        var kind = MapKind(errorCode, missingTargetKind);
        var intent = ErrorPolicyIntentTable.Resolve(toolName, arguments);
        if (kind is null || intent is null)
        {
            return null;
        }

        return (kind.Value, intent.Value) switch
        {
            (ErrorPolicyTargetKind.File, ErrorPolicyIntent.Lookup) => ErrorPolicyKey.FileNotFoundLookup,
            (ErrorPolicyTargetKind.File, ErrorPolicyIntent.Edit) => ErrorPolicyKey.FileNotFoundEdit,
            (ErrorPolicyTargetKind.Symbol, ErrorPolicyIntent.Lookup) => ErrorPolicyKey.SymbolNotFoundLookup,
            _ => ErrorPolicyKey.SymbolNotFoundEdit,
        };
    }
}

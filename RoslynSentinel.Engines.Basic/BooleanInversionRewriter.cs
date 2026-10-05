namespace RoslynSentinel.Engines.Basic;

/// <summary>Rewrites text spans based on semantic role during boolean inversion.</summary>
/// <remarks>Text spans are what Step 7 will later replace in the document.</remarks>
public static class BooleanInversionRewriter
{
    /// <summary>Rewrites originalText based on semantic role and target name.</summary>
    public static string Rewrite(SemanticReplaceRole role, string originalText, string newName, bool parenthesize = false)
    {
        // Trim input
        var trimmed = originalText.Trim();

        return role switch
        {
            SemanticReplaceRole.WriteLiteral => RewriteWriteLiteral(trimmed),
            SemanticReplaceRole.WriteExpression => RewriteWriteExpression(trimmed),
            SemanticReplaceRole.Read => RewriteRead(trimmed, newName, parenthesize),
            SemanticReplaceRole.NegatedRead => RewriteNegatedRead(trimmed, newName),
            SemanticReplaceRole.NameOf => newName,
            SemanticReplaceRole.DeclarationInitializer => RewriteWriteLiteral(trimmed),
            SemanticReplaceRole.Unsupported => throw new ArgumentException($"Role '{role}' is not supported", nameof(role)),
            _ => throw new ArgumentException($"Unknown role '{role}'", nameof(role))
        };
    }

    private static string RewriteWriteLiteral(string trimmed)
    {
        if (trimmed == "true")
            return "false";
        if (trimmed == "false")
            return "true";
        
        throw new ArgumentException($"WriteLiteral text must be 'true' or 'false', got '{trimmed}'", nameof(trimmed));
    }

    private static string RewriteWriteExpression(string expr)
    {
        // If already starts with '!' and is a single negated operand, remove the '!'
        if (expr.StartsWith("!"))
        {
            var afterNegation = expr[1..].Trim();
            if (IsSimpleOperand(afterNegation))
            {
                return afterNegation;
            }
        }

        // If it's a simple operand (name, member access, or invocation), don't add parens
        if (IsSimpleOperand(expr))
        {
            return $"!{expr}";
        }

        // Otherwise, add parens for complex expressions
        return $"!({expr})";
    }

    private static string RewriteRead(string reference, string newName, bool parenthesize)
    {
        // Replace only the last identifier segment with newName
        var lastDotIndex = reference.LastIndexOf('.');
        var rewritten = lastDotIndex >= 0
            ? reference[..lastDotIndex] + "." + newName
            : newName;

        var result = "!" + rewritten;
        
        if (parenthesize)
        {
            result = $"({result})";
        }

        return result;
    }

    private static string RewriteNegatedRead(string reference, string newName)
    {
        // Input should start with '!', remove it
        var withoutNegation = reference.Trim();
        if (withoutNegation.StartsWith("!"))
        {
            withoutNegation = withoutNegation[1..].Trim();
        }

        // Replace only the last identifier segment with newName
        var lastDotIndex = withoutNegation.LastIndexOf('.');
        return lastDotIndex >= 0
            ? withoutNegation[..lastDotIndex] + "." + newName
            : newName;
    }

    private static bool IsSimpleOperand(string expr)
    {
        // Simple operand: letters/digits/underscore/dots, optional trailing () or (...)
        // E.g.: "ok", "x.IsSuccess", "Foo()", "Foo(x, y)"
        
        // Check if it matches: identifier (optionally with dots for member access)
        // followed by optional (...)
        var withoutCall = expr;
        
        // Check for trailing call (invocation)
        if (expr.EndsWith(")"))
        {
            var parenIndex = expr.LastIndexOf('(');
            if (parenIndex > 0)
            {
                withoutCall = expr[..parenIndex];
            }
        }

        // Now check if the remaining part is valid identifier chain (name or member access)
        return IsValidIdentifierChain(withoutCall);
    }

    private static bool IsValidIdentifierChain(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Split by dots and check each part is a valid identifier
        var parts = text.Split('.');
        foreach (var part in parts)
        {
            var trimmedPart = part.Trim();
            if (!IsValidIdentifier(trimmedPart))
                return false;
        }

        return true;
    }

    private static bool IsValidIdentifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // First char must be letter or underscore
        if (!char.IsLetter(text[0]) && text[0] != '_')
            return false;

        // Rest must be letters, digits, or underscore
        foreach (var c in text[1..])
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }
}

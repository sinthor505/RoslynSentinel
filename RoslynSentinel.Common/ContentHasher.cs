using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>Computes the truncated content hash stored in <c>[ContentHash(purpose, hash)]</c> attributes.</summary>
public static class ContentHasher
{
    /// <summary>Truncated (8 hex char) SHA-256 of <paramref name="member"/>'s normalized-whitespace text.</summary>
    public static string ComputeHash(SyntaxNode member)
    {
        var normalized = member.NormalizeWhitespace().ToFullString();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}

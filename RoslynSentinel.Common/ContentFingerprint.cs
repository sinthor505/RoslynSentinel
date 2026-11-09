using System.Security.Cryptography;
using System.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// SHA-256 content fingerprint, hex-encoded lowercase. Not a security boundary; used only to
/// compare content for equality.
/// </summary>
public static class ContentFingerprint
{
    /// <summary>
    /// Returns the lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="content"/>.
    /// </summary>
    public static string Compute(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
